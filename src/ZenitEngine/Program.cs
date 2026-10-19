using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Windows.Management.Deployment;

// Usage: ZenitEngine            -> installed games as JSON
//        ZenitEngine --launch ID -> launch a game by its Id
//        ZenitEngine --selftest  -> parser checks
//        ZenitEngine --thermal [profile] -> Alienware thermal status as JSON, or switch profile (admin)
//        ZenitEngine --game-profile ID PROFILE -> remember the thermal profile a game gets on launch
//        ZenitEngine --serve     -> Zenit Engine pipe server (elevated, started at sign-in by install.ps1)
//        ZenitEngine --drivers [--offline] -> driver check as JSON (problem devices, old vendor drivers, official updates)
Console.OutputEncoding = System.Text.Encoding.UTF8; // titles like "The Sims™ 4" must survive being piped to other tools
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

if (args is ["--selftest"]) { Catalog.SelfTest(); Console.WriteLine("selftest ok"); return; }
if (args is ["--serve"]) { Engine.Serve(); return; } // elevated hardware service for unelevated callers
if (args is ["--drivers", ..]) { Console.WriteLine(JsonSerializer.Serialize(Drivers.Scan(online: !args.Contains("--offline")), json)); return; }
if (args is ["--thermal", ..] && args.Length <= 2)
{
    try
    {
        if (args is [_, var profile]) Thermal.Set(profile);
        Console.WriteLine(JsonSerializer.Serialize(Thermal.Read(), json));
    }
    catch (UnauthorizedAccessException) when (Engine.Ask("status") is { } status) { Console.WriteLine(status); } // via the elevated Engine
    catch (Exception e) when (e is UnauthorizedAccessException or PlatformNotSupportedException or ArgumentException)
    {
        Console.Error.WriteLine(e is UnauthorizedAccessException ? $"{e.Message}: run it from an administrator terminal" : e.Message);
        Environment.ExitCode = 1;
    }
    return;
}
if (args is ["--game-profile", var gameId, var gameProfile])
{
    try { ThermalPolicy.Remember(gameId, gameProfile); }
    catch (ArgumentException e) { Console.Error.WriteLine(e.Message); Environment.ExitCode = 1; }
    return;
}

var games = Catalog.Scan();

if (args is ["--launch", var id])
{
    Session.Run(games.FirstOrDefault(g => g.Id == id) ?? throw new ArgumentException($"unknown id {id}"));
    return;
}

Console.WriteLine(JsonSerializer.Serialize(games, json));

// InstallDir is what Session tracks; null means untracked (e.g. a cloud URL). Args go to the launch command.
record Game(string Store, string Id, string Title, string Launch, string? InstallDir, string? Args = null);

static class Catalog
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zenit");

    public static List<Game> Scan()
    {
        var all = new List<Game>();
        foreach (var (store, scan) in new (string, Func<IEnumerable<Game>>)[]
                 { ("steam", Steam), ("epic", Epic), ("gog", Gog), ("xbox", Xbox), ("ubisoft", Ubisoft), ("riot", Riot), ("apps", Apps), ("custom", Custom) })
        {
            // One broken store must not hide the others.
            try { all.AddRange(scan()); }
            catch (Exception e) { Console.Error.WriteLine($"[{store}] {e.Message}"); }
        }
        return all.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- Steam: libraryfolders.vdf -> every library -> appmanifest_*.acf
    static IEnumerable<Game> Steam()
    {
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string root) yield break;
        var libs = Vdf(File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf")))
            .Where(kv => kv.Key == "path").Select(kv => kv.Value);
        foreach (var lib in libs)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue; // unplugged external drive
            foreach (var f in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
                if (SteamManifest(File.ReadAllText(f), lib) is { } g) yield return g;
        }
    }

    const string SteamRedist = "228980";
    const int FullyInstalled = 4;

    static Game? SteamManifest(string acf, string lib)
    {
        var kv = Vdf(acf).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
        if (!kv.TryGetValue("appid", out var id) || id == SteamRedist) return null;
        if (!int.TryParse(kv.GetValueOrDefault("StateFlags"), out var flags) || (flags & FullyInstalled) == 0) return null;
        return new("steam", $"steam:{id}", kv.GetValueOrDefault("name") ?? id, $"steam://rungameid/{id}",
            Path.Combine(lib, "steamapps", "common", kv.GetValueOrDefault("installdir") ?? ""));
    }

    // ponytail: flat key/value regex, ignores nesting. Enough for the top-level keys we read; swap for a real VDF parser if we need nested sections.
    static readonly Regex KeyValue = new(@"""([^""]+)""\s+""((?:[^""\\]|\\.)*)""");

    static IEnumerable<(string Key, string Value)> Vdf(string text) =>
        KeyValue.Matches(text).Select(m => (m.Groups[1].Value, m.Groups[2].Value.Replace(@"\\", @"\")));

    // ---- Epic: %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item
    static IEnumerable<Game> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.item").Select(f => EpicManifest(File.ReadAllText(f))).OfType<Game>();
    }

    static Game? EpicManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string? S(string name) => r.TryGetProperty(name, out var v) ? v.GetString() : null;
        var isGame = r.TryGetProperty("AppCategories", out var cats) && cats.EnumerateArray().Any(c => c.GetString() == "games");
        if (!isGame || S("AppName") is not { } app) return null; // skips Unreal Engine, plugins, DLC
        return new("epic", $"epic:{app}", S("DisplayName") ?? app,
            $"com.epicgames.launcher://apps/{S("CatalogNamespace")}%3A{S("CatalogItemId")}%3A{app}?action=launch&silent=true",
            S("InstallLocation"));
    }

    // ---- GOG: registry, covers games installed with or without Galaxy
    static IEnumerable<Game> Gog()
    {
        using var games = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (games is null) yield break;
        foreach (var name in games.GetSubKeyNames())
        {
            using var k = games.OpenSubKey(name);
            if (k?.GetValue("exe") is not string exe) continue;
            yield return new("gog", $"gog:{name}", k.GetValue("gameName") as string ?? name, exe, k.GetValue("path") as string);
        }
    }

    // ---- Xbox / Game Pass: packages that ship a MicrosoftGame.config are GDK games, everything else is an app.
    // ponytail: misses legacy UWP-only games without MicrosoftGame.config; add a title allowlist if one shows up.
    static IEnumerable<Game> Xbox()
    {
        foreach (var p in new PackageManager().FindPackagesForUser(""))
        {
            string dir;
            try { dir = p.InstalledPath; } catch { continue; } // staged/broken packages throw
            if (p.IsFramework || !File.Exists(Path.Combine(dir, "MicrosoftGame.config"))) continue;
            if (p.GetAppListEntries().FirstOrDefault() is not { } entry) continue;
            yield return new("xbox", $"xbox:{p.Id.FamilyName}", p.DisplayName, $@"shell:AppsFolder\{entry.AppUserModelId}", dir);
        }
    }

    // ---- Ubisoft Connect: registry install list, launched through its protocol
    static IEnumerable<Game> Ubisoft()
    {
        using var installs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
        if (installs is null) yield break;
        foreach (var id in installs.GetSubKeyNames())
        {
            using var k = installs.OpenSubKey(id);
            if (k?.GetValue("InstallDir") is not string dir) continue;
            dir = dir.Replace('/', '\\').TrimEnd('\\');
            yield return new("ubisoft", $"ubisoft:{id}", Path.GetFileName(dir), $"uplay://launch/{id}/0", dir);
        }
    }

    // ---- Riot (VALORANT, League of Legends...): one Metadata\<product>.<patchline> folder per installed game,
    // launched through the Riot Client so Vanguard and updates run as Riot intends.
    static IEnumerable<Game> Riot()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games");
        var installs = Path.Combine(root, "RiotClientInstalls.json");
        if (!File.Exists(installs)) yield break;
        using var doc = JsonDocument.Parse(File.ReadAllText(installs));
        if (!doc.RootElement.TryGetProperty("rc_default", out var rc) || rc.GetString() is not { } client) yield break;
        foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "Metadata")))
        {
            var folder = Path.GetFileName(dir);
            var yaml = Path.Combine(dir, $"{folder}.product_settings.yaml");
            if (File.Exists(yaml) && RiotProduct(folder, File.ReadAllText(yaml), client.Replace('/', '\\')) is { } g) yield return g;
        }
    }

    static Game? RiotProduct(string folder, string yaml, string client)
    {
        if (folder.Split('.') is not [var product, var patchline] || product == "riot_client") return null;
        var kv = Regex.Matches(yaml, @"^(\w+):\s*""?([^""\r\n]*)""?", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        if (kv.GetValueOrDefault("product_install_full_path") is not { Length: > 0 } dir) return null;
        var title = kv.GetValueOrDefault("shortcut_name") is { Length: > 0 } s ? Path.GetFileNameWithoutExtension(s) : product;
        return new("riot", $"riot:{folder}", title, client, dir.Replace('/', '\\'),
            $"--launch-product={product} --launch-patchline={patchline}");
    }

    // ---- Streaming apps at their default install paths: the game runs on your PS5 or in the cloud,
    // so the app itself is the catalog entry. Anything else goes in custom-games.json.
    static IEnumerable<Game> Apps()
    {
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        (string Id, string Title, string Exe)[] known =
        [
            ("ps-remote-play", "PS Remote Play", Path.Combine(x86, @"Sony\PS Remote Play\RemotePlay.exe")),
            ("geforce-now", "GeForce NOW", Path.Combine(local, @"NVIDIA Corporation\GeForceNOW\CEF\GeForceNOW.exe")),
        ];
        return known.Where(a => File.Exists(a.Exe))
            .Select(a => new Game("app", $"app:{a.Id}", a.Title, a.Exe, Path.GetDirectoryName(a.Exe)));
    }

    // ---- User entries for everything without a scanner: emulators + a ROM, other launchers, cloud URLs.
    // %LOCALAPPDATA%\Zenit\custom-games.json:
    // [{"Title":"Gran Turismo 4","Launch":"D:\\Emu\\pcsx2-qt.exe","Args":"-batch \"D:\\ROMs\\gt4.iso\""},
    //  {"Title":"Xbox Cloud Gaming","Launch":"https://www.xbox.com/play"}]
    static IEnumerable<Game> Custom()
    {
        var path = Path.Combine(DataDir, "custom-games.json");
        return File.Exists(path) ? CustomEntries(File.ReadAllText(path)) : [];
    }

    record CustomEntry(string Title, string Launch, string? Args, string? InstallDir);

    static IEnumerable<Game> CustomEntries(string json) =>
        (JsonSerializer.Deserialize<CustomEntry[]>(json) ?? []).Select(e => new Game("custom", $"custom:{e.Title}", e.Title, e.Launch,
            // A local exe is tracked by its folder (the emulator process); a URL can't be tracked.
            e.InstallDir ?? (Uri.TryCreate(e.Launch, UriKind.Absolute, out var u) && !u.IsFile ? null : Path.GetDirectoryName(e.Launch)),
            e.Args));

    public static void SelfTest()
    {
        static void Check(bool ok, string what) { if (!ok) throw new Exception("selftest failed: " + what); }

        var libs = Vdf("\"0\"\n{\n\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\"label\"\t\t\"\"\n}").ToList();
        Check(libs.Contains(("path", @"D:\SteamLibrary")), "vdf unescapes paths");
        Check(libs.Contains(("label", "")), "vdf empty value");

        var acf = "\"AppState\"\n{\n\"appid\"\t\"730\"\n\"name\"\t\"Counter-Strike 2\"\n\"StateFlags\"\t\"4\"\n\"installdir\"\t\"Counter-Strike Global Offensive\"\n}";
        Check(SteamManifest(acf, @"C:\S") is { Id: "steam:730", Title: "Counter-Strike 2", Launch: "steam://rungameid/730" }, "steam installed");
        Check(SteamManifest(acf.Replace("\"4\"", "\"1026\""), @"C:\S") is null, "steam updating/not installed skipped");
        Check(SteamManifest(acf.Replace("730", SteamRedist), @"C:\S") is null, "steam redist skipped");

        var item = """{"DisplayName":"Fortnite","AppName":"Fortnite","CatalogNamespace":"fn","CatalogItemId":"abc","InstallLocation":"C:\\FN","AppCategories":["public","games"]}""";
        Check(EpicManifest(item) is { Id: "epic:Fortnite", Launch: "com.epicgames.launcher://apps/fn%3Aabc%3AFortnite?action=launch&silent=true" }, "epic game");
        Check(EpicManifest(item.Replace("\"games\"", "\"engines\"")) is null, "epic non-game skipped");

        Check(Session.IsUnder(@"C:\Games\CS2\bin\cs2.exe", @"C:\Games\CS2\"), "process under install dir");
        Check(!Session.IsUnder(@"C:\Games\CS2-Tools\x.exe", @"C:\Games\CS2"), "sibling dir with same prefix");

        Check(Thermal.Describe(0x05000202) == (2, 2, 0, 5), "awcc description as read on the 16X Aurora");
        Check(Thermal.Name(0xA4) == "performance" && Thermal.Name(0xEE) == "0xEE", "awcc profile names");

        var chosen = new Dictionary<string, string> { ["steam:730"] = "quiet" };
        Check(ThermalPolicy.For("steam:730", false, chosen) == "quiet", "per-game choice wins");
        Check(ThermalPolicy.For("xbox:x", false, chosen) == "performance", "AC default");
        Check(ThermalPolicy.For("xbox:x", true, chosen) == "balanced-performance", "battery default");

        var riotYaml = "product_install_full_path: \"C:/Riot Games/VALORANT/live\"\nproduct_install_root: \"C:/Riot Games\"\nshortcut_name: VALORANT.lnk\n";
        Check(RiotProduct("valorant.live", riotYaml, @"C:\RC\RiotClientServices.exe") is
            { Id: "riot:valorant.live", Title: "VALORANT", InstallDir: @"C:\Riot Games\VALORANT\live", Args: "--launch-product=valorant --launch-patchline=live" },
            "riot product");
        Check(RiotProduct("riot_client.live", riotYaml, "x") is null, "riot client itself skipped");

        var custom = CustomEntries("""[{"Title":"GT4","Launch":"D:\\Emu\\pcsx2.exe","Args":"gt4.iso"},{"Title":"Cloud","Launch":"https://www.xbox.com/play"}]""").ToList();
        Check(custom[0] is { Id: "custom:GT4", InstallDir: @"D:\Emu", Args: "gt4.iso" }, "custom emulator tracked by its folder");
        Check(custom[1].InstallDir is null, "custom URL is untracked");

        var now = new DateTime(2026, 10, 19);
        Drivers.Device Dev(string cls, int year, string provider, string status = "OK", int code = 0, string hw = @"PCI\VEN_14C3&DEV_7925") =>
            new("x", cls, status, code, "1.0", new DateTime(year, 1, 1), provider, hw);
        Check(Drivers.IsOutdated(Dev("Net", 2021, "MediaTek"), now), "old vendor network driver is outdated");
        Check(!Drivers.IsOutdated(Dev("Net", 2006, "Microsoft"), now), "inbox Microsoft driver is not flagged");
        Check(!Drivers.IsOutdated(Dev("Net", 2025, "MediaTek"), now), "recent vendor driver is fine");
        Check(!Drivers.IsOutdated(Dev("System", 1968, "INTEL"), now), "Intel 1968 placeholder date is not flagged");
        Check(!Drivers.IsOutdated(Dev("Net", 2021, "WireGuard LLC", hw: "Wintun"), now), "virtual VPN adapter is not hardware");
        Check(Drivers.IsProblem(Dev("SoftwareComponent", 2025, "Intel", "Degraded")), "degraded status is a problem");
        Check(Drivers.IsProblem(Dev("Net", 2025, "Intel", "Error", 28)), "missing driver (code 28) is a problem");
        Check(Drivers.CimDate("20260916000000.******+***") == new DateTime(2026, 9, 16), "CIM driver date");

        Check(PowerMode.For("performance") == PowerMode.Best && PowerMode.For("quiet") == PowerMode.Efficiency
              && PowerMode.For("balanced") == Guid.Empty, "power mode mapping");
    }
}
