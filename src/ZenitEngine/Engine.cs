using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

// Zenit Engine's privileged half. ZenitEngine --serve runs elevated at sign-in (a scheduled task, so no UAC prompt)
// and answers over a local named pipe; only hardware control lives here, everything else stays unprivileged.
// Protocol: one text line per connection ("status", "set <profile>" or "install-drivers"), one JSON line back.
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
