using System.Diagnostics;
using System.IO.Pipes;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

record Fan(int Id, double Rpm, double MaxRpm);
record Status(string Profile, string[] Profiles, Fan[] Fans, Dictionary<string, double> TempsC);
record Device(string Name, string Class, string Status, int ProblemCode, string? DriverVersion, string? Provider, bool LegacyDriver = false);
record DriverUpdate(string Title, string? Class, string? Provider);
record MakerPackage(string Name, string Category, string Version, string Installed, string Device, long Size);
record DriverReport(Device[] Problems, Device[] Outdated, DriverUpdate[] Available, string? UpdateError, MakerPackage[]? Maker, string? MakerError);
record InstallResult(string[] Installed, string[] Failed, bool RebootRequired, bool RestorePoint);
record LightState(string Color, int Brightness);
record Notice(int Seq, string Title, string Detail);
record PowerPlan(string Name, int MinProcessorAc);
enum Severity { Good, Info, Warning, Critical }
record Hog(string Name, int Pid, double Cores);
// End: a process the user can stop from the diagnosis.
record Finding(Severity Level, string Title, string Detail, Hog? End = null);

// Everything the app reads. Nothing here needs admin: hardware goes through the elevated Engine's pipe,
// the rest is plain WMI, powercfg and the engine CLI.
static class Machine
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly string EngineExe = Path.Combine(AppContext.BaseDirectory, "ZenitEngine.exe");

    public static Status? EngineStatus() =>
        Ask("status") is { } json && !json.Contains("\"error\"") ? JsonSerializer.Deserialize<Status>(json, Json) : null;

    public static bool SetProfile(string profile) => Ask($"set {profile}")?.Contains("\"ok\"") == true;

    // What the last Alienware function key did, for the on-screen notice.
    public static Notice? LastNotice() => Ask("notice") is { } json && !json.Contains("\"error\"") ? JsonSerializer.Deserialize<Notice>(json, Json) : null;

    // The game the Engine's watcher sees running, if any.
    public static string? RunningGame() =>
        Ask("game") is { } json && JsonDocument.Parse(json).RootElement.TryGetProperty("Game", out var g) ? g.GetString() : null;

    public static LightState? Lights() =>
        Ask("lights") is { } json && json != "null" && !json.Contains("\"error\"") ? JsonSerializer.Deserialize<LightState>(json, Json) : null;

    // Null when applied, otherwise what went wrong.
    public static string? SetLights(string color, int brightness) =>
        Ask($"lights {color} {brightness}") is { } json && json.Contains("\"ok\"") ? null
            : Lang.T("The lighting controller didn't answer. It needs the Engine running on an Alienware laptop with AlienFX lighting.", "El controlador de luces no respondió. Necesita el Engine en marcha en un Alienware con luces AlienFX.");

    // Installing needs admin, so the Engine does it. Throws with the Engine's message on failure.
    public static InstallResult InstallDrivers()
    {
        var json = Ask("install-drivers") ?? throw new InvalidOperationException(Lang.T("The Engine isn't running. Reinstall with install.ps1.", "El Engine no está en marcha. Reinstala con install.ps1."));
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        return doc.RootElement.Deserialize<InstallResult>(Json)!;
    }

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

    // CPU load from GetSystemTimes deltas between calls: no WMI, so polling it every second costs nothing.
    static (ulong Idle, ulong Total)? lastTimes;
    public static double CpuLoad()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var now = (Idle: idle, Total: kernel + user); // kernel time includes idle time
        var prev = lastTimes;
        lastTimes = now;
        if (prev is not { } p || now.Total <= p.Total) return 0;
        return Math.Clamp(100.0 * (1 - (double)(now.Idle - p.Idle) / (now.Total - p.Total)), 0, 100);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

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

    // The process using the most CPU right now, from Windows' own performance counters (covers system processes
    // too, which Process.TotalProcessorTime can't read unelevated). Only sampled while the CPU runs hot doing little.
    public static Hog? TopProcess()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Name, IDProcess, PercentProcessorTime FROM Win32_PerfFormattedData_PerfProc_Process");
        Hog? top = null;
        foreach (var o in searcher.Get())
        {
            var name = o["Name"] as string ?? "";
            if (name is "_Total" or "Idle" || name.StartsWith("ZenitEngine", StringComparison.OrdinalIgnoreCase)) continue;
            var cores = Convert.ToDouble(o["PercentProcessorTime"]) / 100;
            if (cores > (top?.Cores ?? 0)) top = new(name.Split('#')[0], Convert.ToInt32(o["IDProcess"]), cores);
        }
        return top;
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
    public static List<Finding> Evaluate(Status? status, double load, PowerPlan? plan, DriverReport? drivers, Hog? hog = null)
    {
        var f = new List<Finding>();
        if (status is null)
            f.Add(new(Severity.Critical, Lang.T("Zenit Engine isn't running", "Zenit Engine no está en marcha"),
                Lang.T("Temperatures and thermal profiles come from the Engine, which starts when you sign in. Run install.ps1 again, or sign out and back in.",
                       "Las temperaturas y los perfiles térmicos vienen del Engine, que arranca al iniciar sesión. Ejecuta install.ps1 de nuevo, o cierra sesión y vuelve a entrar.")));
        else if (status.TempsC.TryGetValue("cpu", out var cpu))
        {
            if (IsIdleHeat(cpu, load) && hog is { Cores: >= 0.3 } h)
                f.Add(Culprit(cpu, h));
            else if (cpu >= 95 && load < 15)
                f.Add(new(Severity.Critical, Lang.T($"CPU at {cpu:0} °C with almost nothing running", $"CPU a {cpu:0} °C casi sin uso"),
                    Lang.T($"That is close to its 105 °C limit while the CPU is only {load:0} % busy. Usual causes: a power plan that keeps the CPU at full speed, something stuck after days without a restart, or dust and dried thermal paste.",
                           $"Está cerca de su límite de 105 °C con la CPU ocupada solo al {load:0} %. Causas habituales: un plan de energía que la mantiene a fondo, algo trabado tras días sin reiniciar, o polvo y pasta térmica seca.")));
            else if (cpu >= 90)
                f.Add(new(Severity.Info, Lang.T($"CPU at {cpu:0} °C under load", $"CPU a {cpu:0} °C con carga"),
                    Lang.T("Normal while gaming for a laptop CPU of this class. The Performance profile spins the fans up sooner.",
                           "Normal al jugar para una CPU de portátil de esta clase. El perfil Rendimiento acelera antes los ventiladores.")));
        }

        if (plan is { MinProcessorAc: >= 100 })
            f.Add(new(Severity.Warning, Lang.T($"The \"{plan.Name}\" power plan never lets the CPU slow down", $"El plan de energía \"{plan.Name}\" nunca deja que la CPU baje"),
                Lang.T("Its minimum processor state is 100 % on the charger, so the CPU stays at full clock even when idle: heat and fan noise for nothing. Press Win+R, type powercfg.cpl and choose Balanced. Games still get full speed.",
                       "Su estado mínimo del procesador es 100 % con el cargador, así que la CPU queda a fondo incluso en reposo: calor y ruido para nada. Pulsa Win+R, escribe powercfg.cpl y elige Equilibrado. Los juegos siguen a toda velocidad.")));

        if (drivers is not null)
        {
            // Devices with the same problem become one line (three Intel platform providers, one fix).
            // A legacy-style driver that runs fine is information, not a fault.
            foreach (var g in drivers.Problems.GroupBy(Explain))
                f.Add(g.Count() == 1
                    ? new(g.All(d => d.LegacyDriver) ? Severity.Info : Severity.Warning, g.First().Name, g.Key)
                    : new(g.All(d => d.LegacyDriver) ? Severity.Info : Severity.Warning, Lang.T($"{g.Count()} drivers report the same problem", $"{g.Count()} controladores informan el mismo problema"), string.Join(", ", g.Select(d => d.Name)) + ". " + g.Key));
            if (drivers.Outdated.Length > 0)
                f.Add(new(Severity.Info, Lang.T($"{drivers.Outdated.Length} device drivers are over three years old", $"{drivers.Outdated.Length} controladores tienen más de tres años"),
                    string.Join(", ", drivers.Outdated.Take(3).Select(d => d.Name)) + Lang.T(". Look for newer versions on the laptop maker's support page.", ". Busca versiones nuevas en la página de soporte del fabricante.")));
            if (drivers.Maker is { Length: > 0 } maker)
                f.Add(new(Severity.Info, Lang.T($"{maker.Length} driver updates from the laptop maker", $"{maker.Length} actualizaciones del fabricante"),
                    string.Join(", ", maker.Take(3).Select(p => p.Name)) + Lang.T(". Official packages, verified before they install. Press Update drivers.", ". Paquetes oficiales, verificados antes de instalarse. Pulsa Actualizar controladores.")));
            if (drivers.Available.Length > 0)
                f.Add(new(Severity.Info, Lang.T($"{drivers.Available.Length} driver updates on Windows Update", $"{drivers.Available.Length} actualizaciones en Windows Update"),
                    Lang.T("Official drivers from the hardware makers. Press Update drivers to install them.", "Controladores oficiales de los fabricantes. Pulsa Actualizar controladores para instalarlos.")));
        }

        if (f.Count == 0)
            f.Add(new(Severity.Good, Lang.T("Everything looks healthy", "Todo se ve sano"),
                Lang.T("Temperatures, power settings and drivers are all within normal ranges.", "Temperaturas, energía y controladores dentro de lo normal.")));
        return f.OrderByDescending(x => x.Level).ToList();
    }

    static readonly HashSet<string> WindowsCore = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "Interrupts", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "dwm", "MsMpEng", "SearchIndexer", "TiWorker", "TrustedInstaller", "audiodg",
    };

    // Hot while doing little: the case where one background process is usually to blame.
    public static bool IsIdleHeat(double cpu, double load) => cpu >= 80 && load < 25;

    static Finding Culprit(double cpu, Hog h)
    {
        var level = cpu >= 95 ? Severity.Critical : Severity.Warning;
        var title = Lang.T($"{h.Name} is heating the CPU to {cpu:0} °C", $"{h.Name} está calentando la CPU a {cpu:0} °C");
        // Windows' own processes are never offered for stopping: the kernel and services can't be, or shouldn't be.
        if (WindowsCore.Contains(h.Name))
            return new(level, title, Lang.T($"Windows itself is using {h.Cores:0.0} cores with the laptop idle. This is usually a driver busy in the background (graphics, Wi-Fi, storage) or a Windows task such as indexing or a Defender scan. If it lasts more than a few minutes, restart, and check the drivers below.",
                                             $"El propio Windows usa {h.Cores:0.0} núcleos con el equipo en reposo. Suele ser un controlador ocupado en segundo plano (gráficos, Wi-Fi, almacenamiento) o una tarea de Windows como la indexación o un análisis de Defender. Si dura más de unos minutos, reinicia y revisa los controladores de abajo."));
        // WmiPrvSE serves other programs' WMI queries; stopping it does nothing, the program behind it starts it again.
        return h.Name.Equals("WmiPrvSE", StringComparison.OrdinalIgnoreCase)
            ? new(level, title, Lang.T($"Windows' WMI service is using {h.Cores:0.0} cores with nothing else running. A background utility is flooding it with queries; on Alienware laptops Alienware Command Center's process monitor does exactly this. Close or uninstall vendor utilities one by one until it drops.",
                                       $"El servicio WMI de Windows usa {h.Cores:0.0} núcleos sin nada más en marcha. Una utilidad en segundo plano lo inunda de consultas; en los Alienware lo hace el monitor de procesos de Alienware Command Center. Cierra o desinstala utilidades del fabricante una por una hasta que baje."))
            : new(level, title, Lang.T($"It is using {h.Cores:0.0} cores while the rest of the laptop is idle. If you don't need it right now, stop it.",
                                       $"Usa {h.Cores:0.0} núcleos mientras el resto del equipo está en reposo. Si no lo necesitas ahora, detenlo."), h);
    }

    // The headline: a critical finding speaks for itself; several issues get a count; otherwise the top finding.
    public static Finding Summarize(List<Finding> list)
    {
        if (list[0].Level == Severity.Critical) return list[0];
        var attention = list.Count(f => f.Level >= Severity.Warning);
        return attention > 1
            ? new(Severity.Warning, Lang.T($"{attention} things need your attention", $"{attention} cosas necesitan tu atención"),
                Lang.T("Most urgent first. Each one says what it means and what to do.", "Lo más urgente primero. Cada una explica qué significa y qué hacer."))
            : list[0];
    }

    static string Explain(Device d) => d.LegacyDriver
        ? Lang.T("Nothing to fix: Windows labels these \"Degraded\" only because the maker built them with a legacy-style kernel driver. The driver is running normally. Only the maker can change how it is built.",
                 "No hay nada que reparar: Windows los marca como \"Degraded\" solo porque el fabricante los hizo con un controlador de kernel de estilo antiguo. El controlador funciona con normalidad. Solo el fabricante puede cambiar cómo está hecho.")
        : d.Status.Equals("Degraded", StringComparison.OrdinalIgnoreCase)
        ? Lang.T("The driver is loaded but part of it isn't working. This usually clears after updating the chipset and platform drivers from the laptop maker.",
                 "El controlador está cargado pero una parte no funciona. Suele resolverse al actualizar los controladores de chipset y plataforma del fabricante.")
        : d.ProblemCode switch
        {
            28 => Lang.T("No driver is installed for this device.", "Este dispositivo no tiene controlador instalado."),
            10 => Lang.T("The device couldn't start.", "El dispositivo no pudo iniciarse."),
            22 => Lang.T("This device is disabled in Device Manager.", "Este dispositivo está deshabilitado en el Administrador de dispositivos."),
            43 => Lang.T("Windows stopped the device after it reported a problem.", "Windows detuvo el dispositivo porque informó un problema."),
            _ => Lang.T($"Windows reports status \"{d.Status}\" (code {d.ProblemCode}).", $"Windows informa el estado \"{d.Status}\" (código {d.ProblemCode})."),
        };
}
