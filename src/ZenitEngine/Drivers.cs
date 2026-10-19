using System.Management;

// Driver check, the job Driver Booster does but only from official sources and never installing on its own:
// devices Windows reports as failing, vendor drivers that are years old, and updates the Windows Update driver
// catalog offers for this machine's exact hardware IDs.
static class Drivers
{
    public record Device(string Name, string Class, string Status, int ProblemCode, string? DriverVersion,
        DateTime? DriverDate, string? Provider, string? HardwareId);
    public record Update(string Title, string? Class, string? Model, DateTime? Date, string? Provider, string? HardwareId);
    public record Report(Device[] Problems, Device[] Outdated, Update[] Available, string? UpdateError);

    // Classes worth keeping current on a gaming laptop: graphics, network, audio, Bluetooth, chipset and
    // platform components (thermal frameworks like Intel IPF/DTT live under SoftwareComponent and System).
    static readonly HashSet<string> Watched = new(StringComparer.OrdinalIgnoreCase)
        { "Display", "Net", "Media", "Bluetooth", "System", "SoftwareComponent", "HIDClass", "Camera" };

    public static Report Scan(bool online = true)
    {
        var drivers = Query("SELECT DeviceID, DeviceName, DeviceClass, DriverVersion, DriverDate, DriverProviderName, HardWareID FROM Win32_PnPSignedDriver")
            .Where(d => d["DeviceID"] is string)
            .GroupBy(d => (string)d["DeviceID"])
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var devices = Query("SELECT PNPDeviceID, Name, PNPClass, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE Present = TRUE")
            .Where(e => e["PNPDeviceID"] is string)
            .Select(e =>
            {
                drivers.TryGetValue((string)e["PNPDeviceID"], out var d);
                return new Device(
                    e["Name"] as string ?? d?["DeviceName"] as string ?? "(sin nombre)",
                    e["PNPClass"] as string ?? d?["DeviceClass"] as string ?? "",
                    e["Status"] as string ?? "",
                    Convert.ToInt32(e["ConfigManagerErrorCode"] ?? 0),
                    d?["DriverVersion"] as string,
                    CimDate(d?["DriverDate"] as string),
                    d?["DriverProviderName"] as string,
                    d?["HardWareID"] as string);
            })
            .ToList();

        var problems = devices.Where(IsProblem).ToArray();
        var outdated = devices.Where(d => IsOutdated(d, DateTime.Now)).OrderBy(d => d.DriverDate).ToArray();

        Update[] available = [];
        string? error = null;
        if (online)
        {
            try { available = WindowsUpdateDrivers(); }
            catch (Exception e) { error = e.Message; }
        }
        return new(problems, outdated, available, error);
    }

    // Anything Windows doesn't call OK: an error code (missing driver, failed to start, disabled...)
    // or a non-OK status such as "Degraded", which drivers report when part of their function isn't working.
    public static bool IsProblem(Device d) => d.ProblemCode != 0 || !d.Status.Equals("OK", StringComparison.OrdinalIgnoreCase);

    // ponytail: age heuristic. Inbox Microsoft drivers carry a 2006 date by design, so only vendor drivers count;
    // a real version comparison needs each vendor's catalog (Dell, NVIDIA, Intel), the next step.
    // Intel chipset INFs carry a deliberate 1968 placeholder date so they never outrank a real driver.
    // Only physical hardware counts: VPN adapters (Wintun, tap0901) and root-enumerated services are software.
    public static bool IsOutdated(Device d, DateTime now) =>
        Watched.Contains(d.Class)
        && d.DriverDate is { Year: >= 1990 } date && date < now.AddYears(-3)
        && d.Provider is { } p && !p.Equals("Microsoft", StringComparison.OrdinalIgnoreCase)
        && d.HardwareId is { } id && PhysicalBuses.Any(bus => id.StartsWith(bus, StringComparison.OrdinalIgnoreCase));

    static readonly string[] PhysicalBuses = [@"PCI\", @"USB\", @"HDAUDIO\", @"INTELAUDIO\", @"ACPI\", @"HID\", "BTH", @"SWC\", @"DISPLAY\"];

    // The Windows Update driver catalog through the Windows Update Agent COM API: same source and same
    // hardware-ID matching as Settings > Windows Update > Optional updates. Search only, nothing is installed.
    static Update[] WindowsUpdateDrivers()
    {
        var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new PlatformNotSupportedException("Windows Update Agent not available");
        dynamic session = Activator.CreateInstance(sessionType)!;
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and Type='Driver' and IsHidden=0");
        var list = new List<Update>();
        foreach (dynamic u in result.Updates)
        {
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch { } // some catalog entries omit it
            list.Add(new Update((string)u.Title, (string?)u.DriverClass, (string?)u.DriverModel, date,
                (string?)u.DriverProvider, (string?)u.DriverHardwareID));
        }
        return list.ToArray();
    }

    static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (var o in searcher.Get()) yield return o;
    }

    // Driver dates are calendar dates ("20261203000000.******+***"): read yyyyMMdd as is. Converting the full CIM
    // timestamp to local time shifts it by the UTC offset and can move it to the previous day.
    public static DateTime? CimDate(string? cim) =>
        cim is { Length: >= 8 } && DateTime.TryParseExact(cim[..8], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;
}
