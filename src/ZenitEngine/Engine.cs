using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

// Zenit Engine's privileged half. ZenitEngine --serve runs elevated at sign-in (a scheduled task, so no UAC prompt)
// and answers over a local named pipe; only hardware control lives here, everything else stays unprivileged.
// Protocol: one text line per connection, one JSON line back: "status", "set <profile>", "install-drivers", "game"
// (the game the watcher sees running), "lights" (saved color) or "lights <RRGGBB> <0-100>", "battery" (charge
// settings) or "battery <adaptive|limit %>".
// It also runs the game watcher and the Alienware function keys, and restores the lighting at sign-in.
static class Engine
{
    const string PipeName = "ZenitEngine";

    public static void Serve()
    {
        // Only this user may connect: the pipe changes hardware state.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        // More instances of the pipe (one per concurrent request) only from an elevated token: a non-elevated
        // process could otherwise add its own instance and pose as the Engine.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        GameWatcher.Start();
        Hotkeys.Start();
        // The lighting controller forgets its colors at power-off; put the user's back at sign-in.
        try { Lighting.Restore(); } catch (Exception e) when (e is IOException or PlatformNotSupportedException or ArgumentException) { }
        while (true)
        {
            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
            pipe.WaitForConnection();
            // Each request on its own thread: a driver install takes minutes and status keeps answering meanwhile.
            Task.Run(() => Handle(pipe));
        }
    }

    static void Handle(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(Answer(reader.ReadLine()));
            }
            catch (IOException) { } // client went away mid-request
        }
    }

    static readonly object InstallLock = new();

    static string Answer(string? request)
    {
        try
        {
            switch (request?.Split(' ', 2))
            {
                case ["status"]: return JsonSerializer.Serialize(Thermal.Read());
                case ["set", var profile]: Thermal.Set(profile); return """{"ok":true}""";
                case ["notice"]: return JsonSerializer.Serialize(Hotkeys.Last);
                case ["game"]: return JsonSerializer.Serialize(new { Game = GameWatcher.Current?.Title });
                case ["lights"]: return JsonSerializer.Serialize(Lighting.Saved());
                case ["lights", var light] when light.Split(' ') is [var color, var level] && int.TryParse(level, out var brightness):
                    Lighting.Set(color, brightness); return """{"ok":true}""";
                case ["battery"]: return JsonSerializer.Serialize(Battery.Read());
                case ["battery", "adaptive"]: Battery.Set(null); return """{"ok":true}""";
                case ["battery", var limit] when int.TryParse(limit, out var percent): Battery.Set(percent); return """{"ok":true}""";
                case ["install-drivers"]:
                    if (!Monitor.TryEnter(InstallLock)) return """{"error":"an install is already running"}""";
                    try { return JsonSerializer.Serialize(Drivers.Install()); }
                    finally { Monitor.Exit(InstallLock); }
                default: return """{"error":"unknown request"}""";
            }
        }
        catch (Exception e) { return JsonSerializer.Serialize(new { error = e.Message }); }
    }

    // Client side, for unprivileged callers. Null when the Engine isn't running.
    public static string? Ask(string request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(300);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(request);
            return reader.ReadLine();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return null; }
    }
}
