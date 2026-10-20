using System.Diagnostics;

// Switches the thermal profile when a game starts, however it was launched, and puts the previous one back when it
// ends. Runs inside the Engine. Cheap by design, unlike Alienware Command Center's process monitor (one WMI query per
// process start and stop, which kept WmiPrvSE at ~180% CPU on the test laptop): a plain process list every few
// seconds, and each new process ID is classified once, by its image path against the installed games' folders.
static class GameWatcher
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(3);          // ponytail: polling; ETW process events if start latency matters
    static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);     // launcher.exe -> game.exe handoffs leave a gap
    static readonly TimeSpan CatalogRefresh = TimeSpan.FromMinutes(10);

    static readonly object Lock = new();
    static Game? current;
    public static Game? Current { get { lock (Lock) return current; } }

    public static void Start() => new Thread(Run) { IsBackground = true, Name = "GameWatcher" }.Start();

    static void Run()
    {
        var known = new Dictionary<int, Game?>(); // pid -> game it belongs to, null for everything else
        List<Game> games = [];
        var scanned = DateTime.MinValue;
        string? restore = null;
        DateTime lastSeen = default;
        while (true)
        {
            try
            {
                if (DateTime.UtcNow - scanned > CatalogRefresh)
                {
                    games = Catalog.Scan().Where(g => g.InstallDir is not null).ToList();
                    scanned = DateTime.UtcNow;
                    known.Clear();
                }
                var alive = Process.GetProcesses().Select(p => { using (p) return p.Id; }).ToHashSet();
                foreach (var pid in known.Keys.Where(pid => !alive.Contains(pid)).ToList()) known.Remove(pid);
                foreach (var pid in alive.Where(pid => !known.ContainsKey(pid))) known[pid] = Match(pid, games);

                var running = known.Values.FirstOrDefault(g => g is not null);
                var now = DateTime.UtcNow;
                if (running is not null)
                {
                    lastSeen = now;
                    if (Current is null)
                    {
                        lock (Lock) current = running;
                        restore = Thermal.Current();
                        var target = ThermalPolicy.For(running.Id, ThermalPolicy.OnBattery(), ThermalPolicy.Load());
                        if (target != restore) Thermal.Set(target);
                    }
                }
                else if (Current is not null && now - lastSeen > ExitGrace)
                {
                    lock (Lock) current = null;
                    if (restore is not null) Thermal.Set(restore);
                    restore = null;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { } // no thermal interface, a transient WMI error: try again next poll
            Thread.Sleep(Poll);
        }
    }

    // GDK games run behind a WindowsApps junction, so packaged games match by package family instead of path.
    public static Game? Match(int pid, List<Game> games)
    {
        var (path, family) = Session.Identify(pid);
        return path is null && family is null ? null : Match(path, family, games);
    }

    public static Game? Match(string? path, string? family, List<Game> games) =>
        games.FirstOrDefault(g => Session.IsUnder(path, g.InstallDir!) || (g.Store == "xbox" && family == g.Id["xbox:".Length..]));
}
