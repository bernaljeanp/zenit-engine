using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

// One screen, read top to bottom: what is wrong (diagnosis), the live readings, the thermal profile and fans,
// and the drivers. Data refreshes every second through the Engine; the shader background runs every frame.
sealed class MainWindow : Window
{
    static readonly Dictionary<string, string> ProfileNames = new()
    {
        ["performance"] = "Performance", ["balanced-performance"] = "Balanced+", ["balanced"] = "Balanced",
        ["cool"] = "Cool", ["quiet"] = "Quiet", ["low-power"] = "Battery saver", ["gmode"] = "G-Mode",
    };

    readonly Ambient ambient = new();
    readonly Border engineDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock engineText = T(13, Palette.Muted);
    readonly Border headlineStripe = new() { Width = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 20, 0) };
    readonly TextBlock headline = new() { FontFamily = Fonts.Display, FontSize = 30, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock explanation = T(16, Palette.Muted, wrap: true);
    readonly StackPanel findings = new() { Spacing = 14, Margin = new Thickness(0, 10, 0, 0) };
    readonly TextBlock cpuTemp = Big(), gpuTemp = Big();
    readonly TextBlock cpuLoad = T(14, Palette.Muted);
    readonly TraceGraph graph = new() { Height = 180 };
    readonly StackPanel fans = new() { Spacing = 16 };
    readonly WrapPanel profiles = new();
    readonly StackPanel driverList = new() { Spacing = 14 };
    readonly TextBlock driverStatus = T(14, Palette.Muted, wrap: true);

    Status? status;
    double load;
    PowerPlan? plan;
    DriverReport? drivers;
    bool windowsUpdateChecked, scanning, polling, closed;
    string? shownProfiles;
    double warmTarget;
    int tick;
    TimeSpan? clockStart;

    public MainWindow()
    {
        Title = "Zenit Engine";
        Width = 1280; Height = 880; MinWidth = 1040; MinHeight = 700;
        Background = Brushes.Black;

        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center,
            Children = { new Wordmark { Scale = 0.2, Fill = Palette.Ink, VerticalAlignment = VerticalAlignment.Center }, Label("ENGINE") },
        };
        var engine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { engineDot, engineText }, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(engine, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(32, 20, 32, 0), Children = { engine, brand } };

        var diagnosis = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var diagnosisText = new StackPanel { Spacing = 10, Children = { headline, explanation, findings } };
        Grid.SetColumn(diagnosisText, 1);
        diagnosis.Children.AddRange([headlineStripe, diagnosisText]);

        var temps = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,Auto") };
        var gpuBlock = Reading(gpuTemp, "GPU");
        Grid.SetColumn(gpuBlock, 1);
        Grid.SetColumn(cpuLoad, 2);
        cpuLoad.VerticalAlignment = VerticalAlignment.Bottom;
        temps.Children.AddRange([Reading(cpuTemp, "CPU"), gpuBlock, cpuLoad]);
        var readings = new StackPanel { Spacing = 18, Children = { temps, graph, T(13, Palette.Muted, "CPU temperature, last 60 seconds. The dashed line is 105 °C, where the chip slows itself down.", wrap: true) } };

        var profileBody = new StackPanel { Spacing = 8, Children = { profiles, T(13, Palette.Muted, "Games launched through Zenit switch to their profile automatically and put this one back when you quit.", wrap: true) } };
        var right = new StackPanel { Spacing = 20, Children = { Card("Thermal profile", profileBody), Card("Fans", fans) } };

        var driverActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        driverActions.Children.Add(Action("Scan again", () => _ = ScanDrivers(false)));
        driverActions.Children.Add(Action("Check Windows Update", () => _ = ScanDrivers(true)));
        DockPanel.SetDock(driverActions, Dock.Right);
        var driverBody = new StackPanel { Spacing = 16, Children = { new DockPanel { Children = { driverActions, driverStatus } }, driverList } };

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            ColumnSpacing = 20, RowSpacing = 20, Margin = new Thickness(32, 22, 32, 36),
        };
        Place(content, Card("Diagnosis", diagnosis), 0, 0, 2);
        Place(content, Card("Live readings", readings), 1, 0, 1);
        Place(content, right, 1, 1, 1);
        Place(content, Card("Drivers", driverBody), 2, 0, 2);

        var page = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        page.Children.AddRange([header, new ScrollViewer { Content = content }]);
        Content = new Grid { Children = { ambient, page } };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Poll();
        Opened += (_, _) => { RequestAnimationFrame(Frame); Poll(); timer.Start(); _ = ScanDrivers(false); };
        Closed += (_, _) => { closed = true; timer.Stop(); };
    }

    // Background shader clock. The color eases toward the temperature-driven target so it never jumps.
    void Frame(TimeSpan now)
    {
        if (closed) return;
        clockStart ??= now;
        if (WindowState != WindowState.Minimized)
        {
            ambient.Time = (now - clockStart.Value).TotalSeconds;
            ambient.Warm += (warmTarget - ambient.Warm) * 0.03;
            ambient.InvalidateVisual();
        }
        RequestAnimationFrame(Frame);
    }

    async void Poll()
    {
        if (polling) return;
        polling = true;
        try
        {
            var refreshPlan = tick++ % 20 == 0;
            (status, load, plan) = await Task.Run(() => (Machine.EngineStatus(), SafeLoad(), refreshPlan ? SafePlan() : plan));
            Update();
        }
        finally { polling = false; }
    }

    static double SafeLoad() { try { return Machine.CpuLoad(); } catch { return 0; } }
    static PowerPlan? SafePlan() { try { return Machine.ActivePowerPlan(); } catch { return null; } }

    void Update()
    {
        engineDot.Background = status is null ? Palette.Hot : Palette.Ok;
        engineText.Text = status is null ? "Engine not running" : "Engine running";

        if (status is not null && status.TempsC.TryGetValue("cpu", out var cpu))
        {
            cpuTemp.Text = $"{cpu:0}°";
            cpuTemp.Foreground = Palette.ForTemp(cpu);
            graph.Add(cpu);
            warmTarget = Math.Clamp((cpu - 60) / 40, 0, 1);
        }
        if (status is not null && status.TempsC.TryGetValue("gpu", out var gpu))
        {
            gpuTemp.Text = $"{gpu:0}°";
            gpuTemp.Foreground = Palette.ForTemp(gpu);
        }
        cpuLoad.Text = $"{load:0} % CPU load";

        fans.Children.Clear();
        var index = 0;
        foreach (var fan in status?.Fans ?? [])
        {
            var ratio = fan.MaxRpm > 0 ? fan.Rpm / fan.MaxRpm : 0;
            var head = new DockPanel();
            var rpm = T(14, Palette.Muted, $"{fan.Rpm:N0} rpm");
            DockPanel.SetDock(rpm, Dock.Right);
            head.Children.AddRange([rpm, T(14, Palette.Ink, index++ == 0 ? "CPU fan" : "GPU fan")]);
            fans.Children.Add(new StackPanel { Spacing = 8, Children = { head, new Meter { Height = 6, Value = ratio, Fill = ratio > 0.85 ? Palette.Warn : Palette.Flare } } });
        }
        if (fans.Children.Count == 0) fans.Children.Add(T(14, Palette.Muted, "No fan readings without the Engine.", wrap: true));

        var key = status is null ? "" : status.Profile + "|" + string.Join(",", status.Profiles);
        if (key != shownProfiles)
        {
            shownProfiles = key;
            profiles.Children.Clear();
            foreach (var p in status?.Profiles ?? [])
                profiles.Children.Add(Pill(ProfileNames.GetValueOrDefault(p) ?? p, p == status!.Profile, () => _ = SetProfile(p)));
            if (profiles.Children.Count == 0) profiles.Children.Add(T(14, Palette.Muted, "Profiles appear when the Engine is running.", wrap: true));
        }

        var list = Diagnosis.Evaluate(status, load, plan, drivers);
        var summary = Diagnosis.Summarize(list);
        headline.Text = summary.Title;
        explanation.Text = summary.Detail;
        headlineStripe.Background = Palette.For(summary.Level);
        findings.Children.Clear();
        foreach (var f in list.Where(f => f != summary)) findings.Children.Add(Row(f.Level, f.Title, f.Detail));
    }

    async Task SetProfile(string profile)
    {
        await Task.Run(() => Machine.SetProfile(profile));
        shownProfiles = null;
        Poll();
    }

    async Task ScanDrivers(bool online)
    {
        if (scanning) return;
        scanning = true;
        driverStatus.Text = online ? "Checking Windows Update for driver updates. This can take a minute." : "Scanning devices.";
        var report = await Task.Run(() => { try { return Machine.Drivers(online); } catch { return null; } });
        scanning = false;
        if (report is not null) { drivers = report; windowsUpdateChecked |= online && report.UpdateError is null; }
        ShowDrivers(online && report?.UpdateError is { } error ? error : null);
        Update();
    }

    void ShowDrivers(string? updateError)
    {
        driverList.Children.Clear();
        if (drivers is null) { driverStatus.Text = "The driver check needs ZenitEngine.exe next to this app. Reinstall with install.ps1."; return; }

        var updates = windowsUpdateChecked ? $"{drivers.Available.Length} updates on Windows Update" : "Windows Update not checked yet";
        driverStatus.Text = $"{drivers.Problems.Length} devices with problems, {drivers.Outdated.Length} old drivers, {updates}."
            + (updateError is null ? "" : $" Windows Update didn't answer: {updateError}");
        foreach (var d in drivers.Problems)
            driverList.Children.Add(Row(Severity.Warning, d.Name, $"Status {d.Status}" + (d.DriverVersion is null ? "" : $", driver {d.DriverVersion}") + (d.Provider is null ? "" : $" from {d.Provider}") + "."));
        foreach (var d in drivers.Outdated)
            driverList.Children.Add(Row(Severity.Info, d.Name, $"Driver {d.DriverVersion} from {d.Provider} is over three years old."));
        foreach (var u in drivers.Available)
            driverList.Children.Add(Row(Severity.Info, u.Title, "Available from Windows Update, Optional updates."));
        if (driverList.Children.Count == 0) driverList.Children.Add(Row(Severity.Good, "No driver problems found", "Every device reports a working driver."));
    }

    // ---- building blocks

    static TextBlock T(double size, IBrush brush, string text = "", bool wrap = false) =>
        new() { FontFamily = Fonts.Body, FontSize = size, Foreground = brush, Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };

    static TextBlock Big() => new() { FontFamily = Fonts.Display, FontSize = 56, Foreground = Palette.Ink, Text = "--" };

    static TextBlock Label(string text) => new() { FontFamily = Fonts.Body, FontSize = 12, Foreground = Palette.Muted, Text = text, LetterSpacing = 3, VerticalAlignment = VerticalAlignment.Center };

    static Control Reading(TextBlock value, string name) => new StackPanel { Spacing = 2, Children = { value, T(13, Palette.Muted, name) } };

    static Border Card(string title, Control body) => new()
    {
        Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(26, 22), Child = new StackPanel { Spacing = 18, Children = { T(13, Palette.Muted, title), body } },
    };

    static Control Row(Severity level, string title, string detail)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var text = new StackPanel { Spacing = 3, Children = { T(15, Palette.Ink, title, wrap: true), T(14, Palette.Muted, detail, wrap: true) } };
        Grid.SetColumn(text, 1);
        row.Children.AddRange([new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Palette.For(level), Margin = new Thickness(0, 2, 14, 2) }, text]);
        return row;
    }

    static Border Pill(string text, bool active, Action onClick)
    {
        var label = new TextBlock { Text = text, FontFamily = Fonts.Body, FontSize = 14, FontWeight = FontWeight.Medium, Foreground = active ? Brushes.Black : Palette.Ink };
        var pill = new Border
        {
            CornerRadius = new CornerRadius(999), Padding = new Thickness(16, 9), Margin = new Thickness(0, 0, 10, 10), BorderThickness = new Thickness(1),
            BorderBrush = active ? Palette.Flare : Palette.Line, Background = active ? Palette.Flare : Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand), Child = label,
        };
        pill.PointerEntered += (_, _) => { if (!active) pill.BorderBrush = Palette.Flare; };
        pill.PointerExited += (_, _) => { if (!active) pill.BorderBrush = Palette.Line; };
        pill.PointerPressed += (_, _) => onClick();
        return pill;
    }

    static Border Action(string text, Action onClick)
    {
        var button = Pill(text, false, onClick);
        button.Margin = new Thickness(0);
        return button;
    }

    static void Place(Grid grid, Control child, int row, int column, int span)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        Grid.SetColumnSpan(child, span);
        grid.Children.Add(child);
    }
}