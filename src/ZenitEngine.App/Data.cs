using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

record Fan(int Id, double Rpm, double MaxRpm);
record Status(string Profile, string[] Profiles, Fan[] Fans, Dictionary<string, double> TempsC);
record Device(string Name, string Class, string Status, int ProblemCode, string? DriverVersion, string? Provider);
record DriverUpdate(string Title, string? Class, string? Provider);
record DriverReport(Device[] Problems, Device[] Outdated, DriverUpdate[] Available, string? UpdateError);
record PowerPlan(string Name, int MinProcessorAc);
enum Severity { Good, Info, Warning, Critical }
record Finding(Severity Level, string Title, string Detail);

// Everything the app reads. Nothing here needs admin: hardware goes through the elevated Engine's pipe,
// the rest is plain WMI, powercfg and the engine CLI.
static class Machine
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly string EngineExe = Path.Combine(AppContext.BaseDirectory, "ZenitEngine.exe");

    public static Status? EngineStatus() =>
        Ask("status") is { } json && !json.Contains("\"error\"") ? JsonSerializer.Deserialize<Status>(json, Json) : null;

    public static bool SetProfile(string profile) => Ask($"set {profile}")?.Contains("\"ok\"") == true;

    // Same one-line protocol as ZenitEngine's Engine.Ask.
    static string? Ask(string request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "ZenitEngine", PipeDirection.InOut);
            pipe.Connect(300);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);
            writer.WriteLine(request);
            return reader.ReadLine();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return null; }
    }

    // Formatted counters through WMI: the classic performance counter names are localized ("% de tiempo de procesador").
    public static double CpuLoad()
    {
        using var searcher = new ManagementObjectSearcher("SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name='_Total'");
        foreach (var o in searcher.Get()) return Convert.ToDouble(o["PercentProcessorTime"]);
        return 0;
    }

    public static PowerPlan? ActivePowerPlan()
    {
        var name = Regex.Match(Run("powercfg", "/getactivescheme"), @"\((.+)\)").Groups[1].Value;
        // The query lists minimum, maximum and increment, then the AC and DC values: AC is the second to last.
        var hex = Regex.Matches(Run("powercfg", "/query SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN"), "0x[0-9a-fA-F]{8}");
        return hex.Count >= 2 ? new PowerPlan(name, Convert.ToInt32(hex[^2].Value, 16)) : null;
    }

    public static DriverReport? Drivers(bool online)
    {
        if (!File.Exists(EngineExe)) return null;
        var json = Run(EngineExe, online ? "--drivers" : "--drivers --offline");
        return json.TrimStart().StartsWith('{') ? JsonSerializer.Deserialize<DriverReport>(json, Json) : null;
    }

    static string Run(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, CreateNoWindow = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}

// The plain-language diagnosis: what is wrong, why it matters and what to do, most severe first.
static class Diagnosis
{
    public static List<Finding> Evaluate(Status? status, double load, PowerPlan? plan, DriverReport? drivers)
    {
        var f = new List<Finding>();
        if (status is null)
            f.Add(new(Severity.Critical, "Zenit Engine isn't running",
                "Temperatures and thermal profiles come from the Engine, which starts when you sign in. Run install.ps1 again, or sign out and back in."));
        else if (status.TempsC.TryGetValue("cpu", out var cpu))
        {
            if (cpu >= 95 && load < 15)
                f.Add(new(Severity.Critical, $"CPU at {cpu:0} °C with almost nothing running",
                    $"That is close to its 105 °C limit while the CPU is only {load:0} % busy. Usual causes: a power plan that keeps the CPU at full speed, something stuck after days without a restart, or dust and dried thermal paste."));
            else if (cpu >= 90)
                f.Add(new(Severity.Info, $"CPU at {cpu:0} °C under load",
                    "Normal while gaming for a laptop CPU of this class. The Performance profile spins the fans up sooner."));
        }

        if (plan is { MinProcessorAc: >= 100 })
            f.Add(new(Severity.Warning, $"The \"{plan.Name}\" power plan never lets the CPU slow down",
                "Its minimum processor state is 100 % on the charger, so the CPU stays at full clock even when idle: heat and fan noise for nothing. Press Win+R, type powercfg.cpl and choose Balanced. Games still get full speed."));

        if (drivers is not null)
        {
            // Devices with the same problem become one line (three Intel platform providers, one fix).
            foreach (var g in drivers.Problems.GroupBy(Explain))
                f.Add(g.Count() == 1
                    ? new(Severity.Warning, g.First().Name, g.Key)
                    : new(Severity.Warning, $"{g.Count()} drivers report the same problem", string.Join(", ", g.Select(d => d.Name)) + ". " + g.Key));
            if (drivers.Outdated.Length > 0)
                f.Add(new(Severity.Info, $"{drivers.Outdated.Length} device drivers are over three years old",
                    string.Join(", ", drivers.Outdated.Take(3).Select(d => d.Name)) + ". Look for newer versions on the laptop maker's support page."));
            if (drivers.Available.Length > 0)
                f.Add(new(Severity.Info, $"{drivers.Available.Length} driver updates on Windows Update",
                    "Install them from Settings, Windows Update, Advanced options, Optional updates."));
        }

        if (f.Count == 0)
            f.Add(new(Severity.Good, "Everything looks healthy", "Temperatures, power settings and drivers are all within normal ranges."));
        return f.OrderByDescending(x => x.Level).ToList();
    }

    // The headline: a critical finding speaks for itself; several issues get a count; otherwise the top finding.
    public static Finding Summarize(List<Finding> list)
    {
        if (list[0].Level == Severity.Critical) return list[0];
        var attention = list.Count(f => f.Level >= Severity.Warning);
        return attention > 1
            ? new(Severity.Warning, $"{attention} things need your attention", "Most urgent first. Each one says what it means and what to do.")
            : list[0];
    }

    static string Explain(Device d) => d.Status.Equals("Degraded", StringComparison.OrdinalIgnoreCase)
        ? "The driver is loaded but part of it isn't working. This usually clears after updating the chipset and platform drivers from the laptop maker."
        : d.ProblemCode switch
        {
            28 => "No driver is installed for this device.",
            10 => "The device couldn't start.",
            22 => "This device is disabled in Device Manager.",
            43 => "Windows stopped the device after it reported a problem.",
            _ => $"Windows reports status \"{d.Status}\" (code {d.ProblemCode}).",
        };
}