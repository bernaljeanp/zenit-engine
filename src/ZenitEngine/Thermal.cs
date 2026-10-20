using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;

// Alienware thermal control through Dell's own ACPI-WMI interface (AWCC): no third-party kernel driver,
// so no Defender/anti-cheat driver risk. Needs admin; in the product this runs in the privileged service.
// Op codes: docs.kernel.org/wmi/devices/alienware-wmi.html. Verified on Alienware 16X Aurora (USTT profiles).
static class Thermal
{
    // A machine exposes either the Legacy or the USTT table, never both, so names don't collide in practice.
    static readonly Dictionary<byte, string> Names = new()
    {
        [0x96] = "quiet", [0x97] = "balanced", [0x98] = "balanced-performance", [0x99] = "performance",
        [0xA0] = "balanced", [0xA1] = "balanced-performance", [0xA2] = "cool", [0xA3] = "quiet",
        [0xA4] = "performance", [0xA5] = "low-power", [0xAB] = "gmode",
    };
    static readonly Dictionary<byte, string> Sensors = new() { [0x01] = "cpu", [0x03] = "front", [0x06] = "gpu" };

    const byte OpDescribe = 0x02, OpResourceId = 0x03, OpTemp = 0x04, OpFanRpm = 0x05, OpFanMax = 0x09, OpCurrentProfile = 0x0B;
    const byte OpActivateProfile = 0x01;

    public record Fan(byte Id, uint Rpm, uint MaxRpm);
    public record Status(string Profile, string[] Profiles, Fan[] Fans, Dictionary<string, uint> TempsC);

    // System description: byte0 fans, byte1 temp sensors, byte2 unknown resources, byte3 profiles.
    // Resource ids are listed in that same order by OpResourceId.
    public static (int Fans, int Temps, int Unknown, int Profiles) Describe(uint d) =>
        ((int)(d & 0xFF), (int)(d >> 8 & 0xFF), (int)(d >> 16 & 0xFF), (int)(d >> 24 & 0xFF));

    public static string Name(byte id) => Names.GetValueOrDefault(id) ?? $"0x{id:X2}";
    public static bool Known(string name) => Names.ContainsValue(name);

    // Current/Set without admin go through the elevated Zenit Engine; without AWCC (not an Alienware) or without
    // the Engine running they fall back to the Windows power mode, so game sessions get a boost on any PC.
    static bool NoAwcc(Exception e) => e is PlatformNotSupportedException or UnauthorizedAccessException;

    public static string Current()
    {
        try
        {
            using var o = Wmi();
            return Name((byte)Info(o, OpCurrentProfile));
        }
        catch (UnauthorizedAccessException) when (Engine.Ask("status") is { } json && ProfileOf(json) is { } profile) { return profile; }
        catch (Exception e) when (NoAwcc(e)) { return PowerMode.Current(); }
    }

    static string? ProfileOf(string statusJson)
    {
        using var doc = JsonDocument.Parse(statusJson);
        return doc.RootElement.TryGetProperty("Profile", out var p) ? p.GetString() : null;
    }

    public static Status Read()
    {
        using var o = Wmi();
        var (fans, temps, unknown, profiles) = Describe(Info(o, OpDescribe));
        byte Id(int i) => (byte)Info(o, OpResourceId, (byte)i);

        var fanList = Enumerable.Range(0, fans).Select(Id)
            .Select(id => new Fan(id, Info(o, OpFanRpm, id), Info(o, OpFanMax, id))).ToArray();
        var tempMap = Enumerable.Range(fans, temps).Select(Id)
            .ToDictionary(id => Sensors.GetValueOrDefault(id) ?? $"0x{id:X2}", id => Info(o, OpTemp, id));
        var profileList = Enumerable.Range(fans + temps + unknown, profiles).Select(i => Name(Id(i))).ToArray();
        return new(Name((byte)Info(o, OpCurrentProfile)), profileList, fanList, tempMap);
    }

    public static void Set(string name)
    {
        ManagementObject wmi;
        try { wmi = Wmi(); }
        catch (UnauthorizedAccessException) when (Engine.Ask($"set {name}") is { } answer && answer.Contains("\"ok\"")) { return; }
        catch (Exception e) when (NoAwcc(e)) { PowerMode.Set(name); return; }
        using var o = wmi;
        var (fans, temps, unknown, profiles) = Describe(Info(o, OpDescribe));
        var supported = Enumerable.Range(fans + temps + unknown, profiles).Select(i => (byte)Info(o, OpResourceId, (byte)i));
        var id = supported.Cast<byte?>().FirstOrDefault(p => Name(p!.Value) == name)
            ?? throw new ArgumentException($"profile '{name}' not supported here");
        var r = Call(o, "Thermal_Control", OpActivateProfile, id);
        if (r != 0) throw new InvalidOperationException($"BIOS rejected profile {name} (argr={r})");
    }

    static ManagementObject Wmi()
    {
        try
        {
            return new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM AWCCWmiMethodFunction")
                .Get().Cast<ManagementObject>().FirstOrDefault()
                ?? throw new PlatformNotSupportedException("no AWCC WMI interface (not an Alienware?)");
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new UnauthorizedAccessException("AWCC WMI needs administrator rights", e);
        }
        catch (ManagementException e) when (e.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.NotFound)
        {
            throw new PlatformNotSupportedException("no AWCC WMI interface (not an Alienware?)", e);
        }
    }

    static uint Info(ManagementObject o, byte op, byte arg = 0) => Call(o, "Thermal_Information", op, arg);

    static uint Call(ManagementObject o, string method, byte op, byte arg)
    {
        using var p = o.GetMethodParameters(method);
        p["arg2"] = (uint)(op | arg << 8);
        using var r = o.InvokeMethod(method, p, null);
        return (uint)r["argr"];
    }
}

// Which thermal profile a game session gets: the profile the user picked for that game, otherwise
// performance on AC and balanced-performance on battery (performance drains it fast).
// Set per game with --game-profile.
static class ThermalPolicy
{
    static readonly string StorePath = Path.Combine(Catalog.DataDir, "game-profiles.json");

    public static string For(string gameId, bool onBattery, IReadOnlyDictionary<string, string> chosen) =>
        chosen.GetValueOrDefault(gameId) ?? (onBattery ? "balanced-performance" : "performance");

    public static Dictionary<string, string> Load() =>
        File.Exists(StorePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StorePath)) ?? [] : [];

    public static void Remember(string gameId, string profile)
    {
        if (!Thermal.Known(profile)) throw new ArgumentException($"unknown profile '{profile}'");
        var chosen = Load(); // a corrupt file throws here instead of being silently overwritten
        chosen[gameId] = profile;
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(chosen));
    }

    // The everyday profile by power source: cooler and quicker on the charger, longer battery without it.
    public static string Everyday(bool onBattery) => onBattery ? "balanced" : "balanced-performance";

    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}

// Universal fallback: the Windows power mode (Settings > System > Power > Power mode). Any PC, no admin.
// Windows only applies it while the Balanced power plan is active; custom plans such as High performance ignore it.
static class PowerMode
{
    public static readonly Guid Best = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    // Thermal profile names map onto the three power modes; Guid.Empty is Balanced.
    public static Guid For(string profile) => profile switch
    {
        "performance" or "balanced-performance" or "gmode" => Best,
        "cool" or "quiet" or "low-power" => Efficiency,
        _ => Guid.Empty,
    };

    public static string Current()
    {
        PowerGetEffectiveOverlayScheme(out var g);
        return g == Best ? "performance" : g == Efficiency ? "quiet" : "balanced";
    }

    public static void Set(string profile)
    {
        var rc = PowerSetActiveOverlayScheme(For(profile));
        if (rc != 0) throw new InvalidOperationException($"power mode change failed (error {rc})");
    }

    [DllImport("powrprof")] static extern uint PowerSetActiveOverlayScheme(Guid scheme);
    [DllImport("powrprof")] static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);
}
