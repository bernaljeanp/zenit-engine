using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Xml;

// Dell's own driver catalog, the one Dell Command Update and Alienware Update read: an index on downloads.dell.com
// points to one catalog per model (by the BIOS system ID, the SKU), and each package in it lists the hardware IDs
// it supports and the driver version it carries. Covers Alienware, Dell G, XPS, Inspiron, Latitude, Precision...
// Trust chain: the index is Authenticode-signed by Dell, the model catalog matches the SHA-256 in the index, and
// each package matches the SHA-256 in the model catalog and is signed by Dell. Anything else is refused.
static class Dell
{
    public record Package(string Name, string Category, string Version, string Installed, string Device, long Size,
        string Url, string Sha256);

    const string Base = "https://downloads.dell.com/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    // Driver packages only: BIOS, firmware and applications change more than a driver and stay with Dell's tools.
    public static Package[] Available()
    {
        var system = Query("SELECT Manufacturer, SystemSKUNumber FROM Win32_ComputerSystem").FirstOrDefault();
        var maker = system?["Manufacturer"] as string ?? "";
        if (!maker.Contains("Dell", StringComparison.OrdinalIgnoreCase) && !maker.Contains("Alienware", StringComparison.OrdinalIgnoreCase)) return [];
        if (system?["SystemSKUNumber"] is not string { Length: > 0 } sku) return [];

        var dir = WorkDir();
        var index = Fetch("catalog/CatalogIndexPC.cab", dir, sha256: null);
        var model = ModelCatalog(index, sku) ?? throw new InvalidOperationException($"Dell has no driver catalog for system {sku}");
        var catalog = Fetch(model.Path, dir, model.Sha256);

        // Present devices: every hardware ID each one answers to, and the driver version it runs now.
        var versions = Query("SELECT DeviceID, DriverVersion FROM Win32_PnPSignedDriver")
            .Where(d => d["DeviceID"] is string && d["DriverVersion"] is string)
            .GroupBy(d => (string)d["DeviceID"], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (string)g.First()["DriverVersion"], StringComparer.OrdinalIgnoreCase);
        var devices = Query("SELECT PNPDeviceID, Name, HardwareID FROM Win32_PnPEntity WHERE Present = TRUE")
            .Where(e => e["PNPDeviceID"] is string && e["HardwareID"] is string[])
            .Select(e => new HardwareDevice(e["Name"] as string ?? "", (string[])e["HardwareID"], versions.GetValueOrDefault((string)e["PNPDeviceID"])))
            .ToList();
        return Applicable(catalog, devices);
    }

    public record HardwareDevice(string Name, string[] HardwareIds, string? DriverVersion);

    // Newest package per name whose driver is newer than what a present device runs.
    public static Package[] Applicable(XmlDocument catalog, List<HardwareDevice> devices)
    {
        var found = new List<Package>();
        foreach (XmlElement c in catalog.GetElementsByTagName("SoftwareComponent"))
        {
            if (Child(c, "ComponentType")?.GetAttribute("value") != "DRVR") continue;
            var name = Display(Child(c, "Name"));
            foreach (XmlElement d in c.GetElementsByTagName("Device"))
            {
                if (!Version.TryParse(d.GetAttribute("version"), out var offered)) continue;
                var ids = Ids(d);
                var match = devices.FirstOrDefault(h => h.DriverVersion is { } v && Version.TryParse(v, out var current) && offered > current
                    && h.HardwareIds.Any(id => ids.Contains(id)));
                if (match is null) continue;
                found.Add(new(name, Display(Child(c, "Category")), c.GetAttribute("vendorVersion"), match.DriverVersion!, match.Name,
                    long.TryParse(c.GetAttribute("size"), out var size) ? size : 0, Base + c.GetAttribute("path"), Hash(c)));
                break;
            }
        }
        return found.GroupBy(p => p.Name)
            .Select(g => g.OrderByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version()).First())
            .ToArray();
    }

    // Catalog IDs in Windows' hardware ID spelling: PCI\VEN_10DE&DEV_2D58&SUBSYS_0D341028, or as given (ACPI\..., SWC\...).
    public static HashSet<string> Ids(XmlElement device)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlElement pci in device.GetElementsByTagName("PCIInfo"))
        {
            var id = $@"PCI\VEN_{pci.GetAttribute("vendorID")}&DEV_{pci.GetAttribute("deviceID")}";
            ids.Add(pci.GetAttribute("subDeviceID") is { Length: > 0 } sub ? $"{id}&SUBSYS_{sub}{pci.GetAttribute("subVendorID")}" : id);
        }
        foreach (XmlElement g in device.GetElementsByTagName("Generic")) ids.Add(g.InnerText.Trim());
        return ids;
    }

    public record InstallResult(string[] Installed, string[] Failed, bool RebootRequired);

    // Dell Update Packages install silently with /s; exit 0 is done, 2 is done after a restart, 3 means not needed.
    public static InstallResult Install(Package[] packages)
    {
        var dir = WorkDir();
        List<string> installed = [], failed = [];
        var reboot = false;
        foreach (var p in packages)
        {
            try
            {
                var exe = Path.Combine(dir, Path.GetFileName(new Uri(p.Url).LocalPath));
                // Held open with no write or delete sharing from the hash check to the end of the install,
                // so nothing can swap the file in between.
                using (Download(p.Url, exe, p.Sha256))
                {
                    var log = Path.ChangeExtension(exe, ".log");
                    using var dup = Process.Start(new ProcessStartInfo(exe, $"/s /l=\"{log}\"") { UseShellExecute = false, CreateNoWindow = true })!;
                    if (!dup.WaitForExit(TimeSpan.FromMinutes(30))) { dup.Kill(true); throw new TimeoutException("took over 30 minutes"); }
                    switch (dup.ExitCode)
                    {
                        case 0: installed.Add(p.Name); break;
                        case 2: installed.Add(p.Name); reboot = true; break;
                        case 3: break; // Dell's own applicability check says it isn't needed
                        default: failed.Add($"{p.Name} (Dell installer exit code {dup.ExitCode})"); break;
                    }
                }
                File.Delete(exe);
            }
            catch (Exception e) { failed.Add($"{p.Name} ({e.Message})"); }
        }
        return new(installed.ToArray(), failed.ToArray(), reboot);
    }

    record Manifest(string Path, string Sha256);

    static Manifest? ModelCatalog(XmlDocument index, string sku)
    {
        foreach (XmlElement group in index.GetElementsByTagName("GroupManifest"))
        {
            var models = group.GetElementsByTagName("Model").Cast<XmlElement>();
            if (!models.Any(m => m.GetAttribute("systemID").Equals(sku, StringComparison.OrdinalIgnoreCase))) continue;
            if (Child(group, "ManifestInformation") is { } info) return new(info.GetAttribute("path"), Hash(info));
        }
        return null;
    }

    // Downloads a catalog cab, checks it (Dell's signature, plus the hash when the index gave one) and expands its XML.
    static XmlDocument Fetch(string path, string dir, string? sha256)
    {
        var cab = Path.Combine(dir, Path.GetFileName(path));
        using (Download(Base + path, cab, sha256)) { }
        var xml = Path.ChangeExtension(cab, ".xml");
        File.Delete(xml);
        using (var expand = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "expand.exe"), $"\"{cab}\" \"{xml}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            expand.StandardOutput.ReadToEnd();
            expand.WaitForExit();
        }
        var doc = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        doc.Load(reader);
        return doc;
    }

    // Returns the downloaded file open for reading only, after its hash (when known) and Dell's signature check out.
    static FileStream Download(string url, string path, string? sha256)
    {
        if (!url.StartsWith(Base, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("not a Dell download");
        File.Delete(path);
        using (var body = Http.GetStreamAsync(url).GetAwaiter().GetResult())
        using (var output = File.Create(path))
            body.CopyTo(output);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (sha256 is not null && !Convert.ToHexString(SHA256.HashData(file)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("download doesn't match Dell's catalog hash");
            if (!SignedByDell(path)) throw new CryptographicException("not signed by Dell");
            return file;
        }
        catch { file.Dispose(); File.Delete(path); throw; }
    }

    static bool SignedByDell(string path)
    {
        if (!Authenticode.Verify(path)) return false;
#pragma warning disable SYSLIB0057 // only reads the signer of a file whose signature WinVerifyTrust just checked
        var subject = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).GetNameInfo(X509NameType.SimpleName, false);
#pragma warning restore SYSLIB0057
        return subject.StartsWith("Dell Inc", StringComparison.Ordinal) || subject.StartsWith("Dell Technologies Inc", StringComparison.Ordinal);
    }

    // The Engine runs installers from here, so only administrators may write to it: a non-elevated process
    // must not be able to plant or swap a file the Engine will run. Non-elevated scans use the user's temp.
    static string WorkDir()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            return Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zenit-dell")).FullName;
        var dir = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Zenit\Updates"));
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (dir.Exists) dir.SetAccessControl(acl); else dir.Create(acl);
        return dir.FullName;
    }

    static XmlElement? Child(XmlElement e, string name) => e.ChildNodes.OfType<XmlElement>().FirstOrDefault(c => c.LocalName == name);
    static string Display(XmlElement? e) => e?.ChildNodes.OfType<XmlElement>().FirstOrDefault(d => d.GetAttribute("lang") == "en")?.InnerText.Trim() ?? "";
    static string Hash(XmlElement e) =>
        e.GetElementsByTagName("Hash").Cast<XmlElement>().FirstOrDefault(h => h.GetAttribute("algorithm") == "SHA256")?.InnerText.Trim()
        ?? throw new InvalidDataException("catalog entry without a SHA-256");

    static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (var o in searcher.Get()) yield return o;
    }
}

// WinVerifyTrust: the same check as Get-AuthenticodeSignature's "Valid" (signature intact, chain trusted, not revoked).
static class Authenticode
{
    public static bool Verify(string path)
    {
        var file = new FileInfo { cbStruct = Marshal.SizeOf<FileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new TrustData
            {
                cbStruct = Marshal.SizeOf<TrustData>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 1 /* WTD_REVOKE_WHOLECHAIN */,
                dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = filePtr, dwProvFlags = 0x80, /* WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT */
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<FileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FileInfo { public int cbStruct; public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public int cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData; public int dwUIChoice;
        public int fdwRevocationChecks; public int dwUnionChoice; public IntPtr pFile; public int dwStateAction;
        public IntPtr hWVTStateData; public IntPtr pwszURLReference; public int dwProvFlags; public int dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);
}
