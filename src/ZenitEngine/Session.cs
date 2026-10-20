using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// Module 2: launch a catalog game, find its processes by install dir, keep focus on it, report when it ends.
// Runs in the user session (not a service): WinEvent hooks and SetForegroundWindow don't work from Session 0.
// Events go to stdout as JSON lines; the frontend will read the same lines over IPC.
static class Session
{
    // Processes that steal focus mid-game. Until the game has a window they are left alone so login dialogs work.
    static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "EADesktop", "EABackgroundService",
        "UbisoftConnect", "upc", "GalaxyClient", "XboxPcApp", "GamingServicesUI",
    };

    static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2); // first launch + launcher update can be slow
    static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);    // launcher.exe -> game.exe handoffs leave a gap

    static IntPtr gameWindow; // written by the poll loop, read by the hook thread; IntPtr reads/writes are atomic
    static WinEventProc? hookProc; // kept in a field so the GC doesn't collect the callback

    public static void Launch(Game g)
    {
        var psi = new ProcessStartInfo(g.Launch) { UseShellExecute = true, Arguments = g.Args ?? "" };
        if (File.Exists(g.Launch)) psi.WorkingDirectory = Path.GetDirectoryName(g.Launch); // GOG exes expect their own dir
        Process.Start(psi);
    }

    public static void Run(Game g)
    {
        if (g.InstallDir is not { } dir)
        {
            // Nothing local to track (cloud URL): launch and hand back to the UI right away.
            Emit("launching", g.Id);
            Launch(g);
            Emit("untracked", g.Id);
            return;
        }
        StartFocusKeeper();
        Emit("launching", g.Id);
        // Thermal profile goes up before launch: shader compilation and loading are the heaviest part.
        var before = TryThermal(() =>
        {
            var current = Thermal.Current();
            var target = ThermalPolicy.For(g.Id, ThermalPolicy.OnBattery(), ThermalPolicy.Load());
            if (target != current) Thermal.Set(target);
            Emit("thermal", target);
            return current;
        });
        Launch(g);

        // GDK games run from XboxGames\<game>\Content behind a WindowsApps junction we can't resolve,
        // so packaged games are matched by package identity instead of path.
        var family = g.Store == "xbox" ? g.Id["xbox:".Length..] : null;
        var started = DateTime.UtcNow;
        DateTime? lastSeen = null;
        while (true)
        {
            var pids = GamePids(dir, family);
            var now = DateTime.UtcNow;
            if (pids.Count > 0)
            {
                lastSeen = now;
                var win = MainWindow(pids);
                if (win != IntPtr.Zero && Volatile.Read(ref gameWindow) == IntPtr.Zero) Emit("running", g.Id);
                if (win != IntPtr.Zero) Volatile.Write(ref gameWindow, win);
            }
            else if (lastSeen is { } seen && now - seen > ExitGrace) { Emit("exited", g.Id); break; }
            else if (lastSeen is null && now - started > StartTimeout) { Emit("timeout", g.Id); break; }
            Thread.Sleep(1000); // ponytail: 1s process poll; switch to ETW/WMI process traces if exit latency matters
        }

        // Back to whatever the user had outside the game.
        if (before is not null) TryThermal(() => { Thermal.Set(before); Emit("thermal", before); return before; });
    }

    // Thermal control is a bonus: no admin rights or a non-Alienware machine must never break the game session.
    static string? TryThermal(Func<string> action)
    {
        try { return action(); }
        catch (Exception e) { Emit("thermal-unavailable", e.Message); return null; }
    }

    public static bool IsUnder(string? path, string dir) =>
        path is not null && path.StartsWith(dir.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);

    static HashSet<int> GamePids(string dir, string? family) =>
        Process.GetProcesses().Where(p => IsGame(p.Id, dir, family)).Select(p => p.Id).ToHashSet();

    // Largest visible, unowned top-level window of the game's processes.
    static IntPtr MainWindow(HashSet<int> pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains((int)pid) && IsWindowVisible(h) && GetWindow(h, GW_OWNER) == IntPtr.Zero && GetWindowRect(h, out var r))
            {
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (area > bestArea) (best, bestArea) = (h, area);
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    static void StartFocusKeeper()
    {
        hookProc = (_, _, hwnd, _, _, _, _) =>
        {
            var game = Volatile.Read(ref gameWindow);
            if (game == IntPtr.Zero || hwnd == game || !IsWindow(game)) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { return; }
            if (!Launchers.Contains(name)) return;
            Refocus(game);
            Emit("refocus", name);
        };
        // Out-of-context WinEvent hooks deliver through the hooking thread's message queue, so it needs its own pump.
        new Thread(() =>
        {
            SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, hookProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref msg);
        }) { IsBackground = true }.Start();
    }

    static void Refocus(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        // Foreground lock: Windows only lets a process take focus right after it saw input, and XInput doesn't count.
        // A synthetic Alt tap satisfies the check.
        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(hwnd);
    }

    static void Emit(string evt, string detail) =>
        Console.WriteLine(JsonSerializer.Serialize(new { evt, detail, t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));

    static bool IsGame(int pid, string dir, string? family)
    {
        var (path, package) = Identify(pid);
        return IsUnder(path, dir) || (family is not null && package == family);
    }

    // Image path and package family (null when not packaged or not readable), with one process handle.
    public static (string? Path, string? Family) Identify(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return (null, null);
        try
        {
            var sb = new StringBuilder(1024);
            int len = sb.Capacity;
            var path = QueryFullProcessImageName(h, 0, sb, ref len) ? sb.ToString() : null;
            uint flen = (uint)sb.Capacity;
            return (path, GetPackageFamilyName(h, ref flen, sb) == 0 ? sb.ToString() : null);
        }
        finally { CloseHandle(h); }
    }

    // ---- Win32
    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, GW_OWNER = 4, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int SW_RESTORE = 9;
    const byte VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 2;

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("kernel32")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref int len);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr h, ref uint len, StringBuilder name);
}
