using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

// Zenit Engine's privileged half. ZenitEngine --serve runs elevated at sign-in (a scheduled task, so no UAC prompt)
// and answers over a local named pipe; only hardware control lives here, everything else stays unprivileged.
// Protocol: one text line per connection ("status" or "set <profile>"), one JSON line back.
static class Engine
{
    const string PipeName = "ZenitEngine";

    public static void Serve()
    {
        // Only this user may connect: the pipe changes hardware state.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (true)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
            pipe.WaitForConnection();
            try
            {
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(Answer(reader.ReadLine()));
            }
            catch (IOException) { } // client went away mid-request
        }
    }

    static string Answer(string? request)
    {
        try
        {
            switch (request?.Split(' ', 2))
            {
                case ["status"]: return JsonSerializer.Serialize(Thermal.Read());
                case ["set", var profile]: Thermal.Set(profile); return """{"ok":true}""";
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
