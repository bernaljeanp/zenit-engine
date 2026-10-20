using System.Management;

// Driver check and update, the job Driver Booster does but only from official sources and only when asked:
// devices Windows reports as failing, vendor drivers that are years old, and updates the Windows Update driver
// catalog offers for this machine's exact hardware IDs, which Install() applies.
static class Drivers
{
    public record Device(string Name, string Class, string Status, int ProblemCode, string? DriverVersion,
        DateTime? DriverDate, string? Provider, string? HardwareId);
    public record Update(string Title, string? Class, string? Model, DateTime? Date, string? Provider, string? HardwareId);
    public record Report(Device[] Problems, Device[] Outdated, Update[] Available, string? UpdateError, Dell.Package[] Maker, string? MakerError);

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
        Dell.Package[] maker = [];
        string? error = null, makerError = null;
        if (online)
        {
            try { available = WindowsUpdateDrivers(); }
            catch (Exception e) { error = e.Message; }
            try { maker = Dell.Available(); }
            catch (Exception e) { makerError = e.Message; }
        }
        return new(problems, outdated, available, error, maker, makerError);
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
        var installed = InstalledDrivers();
        foreach (dynamic u in result.Updates)
        {
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch { } // some catalog entries omit it
            if (!IsNewer((string)u.Title, date, (string?)u.DriverHardwareID, installed)) continue;
            list.Add(new Update((string)u.Title, (string?)u.DriverClass, (string?)u.DriverModel, date,
                (string?)u.DriverProvider, (string?)u.DriverHardwareID));
        }
        return list.ToArray();
    }

    public record InstalledDriver(string[] Ids, string? Version, DateTime? Date);

    // Windows Update can offer a driver older than the one a device runs (seen on this machine: Intel graphics
    // 32.0.101.8860 offered over 8991), so an offer only counts when it is newer than what every device it targets
    // has now. The catalog gives no version field; driver titles carry it, "... (32.0.101.8860)". Without one, the date decides.
    public static bool IsNewer(string title, DateTime? date, string? hardwareId, IEnumerable<InstalledDriver> installed)
    {
        if (hardwareId is null) return true;
        var targets = installed.Where(d => d.Ids.Contains(hardwareId, StringComparer.OrdinalIgnoreCase)).ToList();
        var offered = System.Text.RegularExpressions.Regex.Match(title, @"\((\d+(?:\.\d+){1,3})\)\s*$") is { Success: true } m
            && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
        return targets.All(d => offered is not null && Version.TryParse(d.Version, out var current) ? offered > current
            : date is null || d.Date is null || date > d.Date);
    }

    static List<InstalledDriver> InstalledDrivers()
    {
        var drivers = Query("SELECT DeviceID, DriverVersion, DriverDate FROM Win32_PnPSignedDriver")
            .Where(d => d["DeviceID"] is string)
            .GroupBy(d => (string)d["DeviceID"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return Query("SELECT PNPDeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity WHERE Present = TRUE")
            .Where(e => e["PNPDeviceID"] is string && drivers.ContainsKey((string)e["PNPDeviceID"]))
            .Select(e =>
            {
                var d = drivers[(string)e["PNPDeviceID"]];
                return new InstalledDriver([.. e["HardwareID"] as string[] ?? [], .. e["CompatibleID"] as string[] ?? []],
                    d["DriverVersion"] as string, CimDate(d["DriverDate"] as string));
            })
            .ToList();
    }

    public record InstallResult(string[] Installed, string[] Failed, bool RebootRequired, bool RestorePoint);

    // Installs every official driver update: first what the Windows Update catalog offers for this machine (the same
    // packages Settings > Optional updates would install, published by the hardware makers for their exact hardware IDs
    // and signed by Microsoft), then the newer ones in the laptop maker's own catalog. Needs admin, so only the Engine
    // calls it. A restore point comes first.
    public static InstallResult Install()
    {
        var maker = Dell.Available();
        var windows = InstallWindowsUpdate(() => CreateRestorePoint("Zenit Engine: driver updates"));
        if (maker.Length == 0) return windows;
        var restorePoint = windows.RestorePoint || CreateRestorePoint("Zenit Engine: driver updates");
        var dell = Dell.Install(maker);
        return new([.. windows.Installed, .. dell.Installed], [.. windows.Failed, .. dell.Failed],
            windows.RebootRequired || dell.RebootRequired, restorePoint);
    }

    static InstallResult InstallWindowsUpdate(Func<bool> restorePointFirst)
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")
            ?? throw new PlatformNotSupportedException("Windows Update Agent not available"))!;
        session.ClientApplicationID = "Zenit Engine";
        dynamic found = session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0");

        dynamic batch = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        var failed = new List<string>();
        var current = InstalledDrivers();
        foreach (dynamic u in found.Updates)
        {
            DateTime? date = null;
            try { date = (DateTime)u.DriverVerDate; } catch { }
            if (!IsNewer((string)u.Title, date, (string?)u.DriverHardwareID, current)) continue;
            // A license is the user's to accept, in Settings; Zenit never accepts one for them.
            if ((bool)u.EulaAccepted) batch.Add(u);
            else failed.Add($"{u.Title} (needs its license accepted in Windows Update)");
        }
        if (batch.Count == 0) return new([], failed.ToArray(), false, false);

        var restorePoint = restorePointFirst();
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = batch;
        downloader.Download();
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = batch;
        dynamic result = installer.Install();

        var installed = new List<string>();
        for (var i = 0; i < batch.Count; i++)
        {
            // OperationResultCode: 2 succeeded, 3 succeeded with errors.
            var code = (int)result.GetUpdateResult(i).ResultCode;
            (code is 2 or 3 ? installed : failed).Add((string)batch.Item(i).Title);
        }
        return new(installed.ToArray(), failed.ToArray(), (bool)result.RebootRequired, restorePoint);
    }

    // Best effort: System Restore may be off, and Windows allows one restore point per 24 hours by default.
    static bool CreateRestorePoint(string description)
    {
        try
        {
            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var args = restore.GetMethodParameters("CreateRestorePoint");
            args["Description"] = description;
            args["RestorePointType"] = 10;  // DEVICE_DRIVER_INSTALL
            args["EventType"] = 100;        // BEGIN_SYSTEM_CHANGE
            using var result = restore.InvokeMethod("CreateRestorePoint", args, null);
            return Convert.ToUInt32(result["ReturnValue"]) == 0;
        }
        catch (ManagementException) { return false; }
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
