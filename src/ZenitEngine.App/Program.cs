using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

// One Zenit per user: a second launch (Start menu while it sits in the tray) asks the first one to show itself.
using var single = new Mutex(true, @"Local\ZenitEngineApp", out var first);
using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ZenitEngineAppShow");
if (!first) { showSignal.Set(); return; }

App.StartHidden = args.Contains("--tray"); // started with Windows: stay in the tray until clicked
App.ShowSignal = showSignal;
AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

sealed class App : Application
{
    public static bool StartHidden;
    public static EventWaitHandle? ShowSignal;

    static readonly (string Id, string Name)[] TrayProfiles =
        [("performance", "Performance"), ("balanced-performance", "Balanced+"), ("balanced", "Balanced"), ("quiet", "Quiet"), ("cool", "Cool")];

    MainWindow? window;
    TrayIcon? tray;
    bool quitting;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ZenitEngine.App/Assets/zenit.ico")));
            window = new MainWindow { Icon = icon };
            // Closing hides to the tray; only Quit ends the app.
            window.Closing += (_, e) => { if (!quitting) { e.Cancel = true; window.Hide(); } };
            if (!StartHidden) window.Show();

            var menu = new NativeMenu();
            menu.Add(Item("Open Zenit", Show));
            menu.Add(new NativeMenuItemSeparator());
            foreach (var (id, name) in TrayProfiles) menu.Add(Item(name, () => Task.Run(() => Machine.SetProfile(id))));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Item("Quit", () => { quitting = true; tray?.Dispose(); desktop.Shutdown(); }));
            tray = new TrayIcon { Icon = icon, ToolTipText = "Zenit Engine", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => Show();
            TrayIcon.SetIcons(this, [tray]);

            var tooltip = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            tooltip.Tick += async (_, _) => tray.ToolTipText = await Task.Run(TrayText);
            tooltip.Start();

            new Thread(() => { while (ShowSignal!.WaitOne()) Dispatcher.UIThread.Post(Show); }) { IsBackground = true }.Start();

            // The Engine handles F6/F7/F9 and signals this event; the app shows what happened.
            var osd = new Osd();
            var noticeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ZenitEngineNotice");
            new Thread(() =>
            {
                while (noticeSignal.WaitOne())
                    if (Machine.LastNotice() is { Seq: > 0 } n) Dispatcher.UIThread.Post(() => osd.Notify(n.Title, n.Detail));
            }) { IsBackground = true }.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void Show()
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => onClick();
        return item;
    }

    // "Zenit · CPU 62° · GPU 46° · Balanced", read from the Engine.
    static string TrayText()
    {
        try
        {
            if (Machine.EngineStatus() is not { } s) return "Zenit Engine · Engine not running";
            var parts = new List<string> { "Zenit" };
            if (s.TempsC.TryGetValue("cpu", out var cpu)) parts.Add($"CPU {cpu:0}°");
            if (s.TempsC.TryGetValue("gpu", out var gpu)) parts.Add($"GPU {gpu:0}°");
            parts.Add(TrayProfiles.FirstOrDefault(p => p.Id == s.Profile).Name ?? s.Profile);
            return string.Join(" · ", parts);
        }
        catch { return "Zenit Engine"; }
    }
}
