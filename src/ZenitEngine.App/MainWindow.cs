using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

// An instrument cluster on machined graphite. Top: the two heat dials and what is wrong, in plain words. Then the
// thermal profile as a mode bar, the last minute of CPU heat and the fans, the drivers and the lighting.
// Data refreshes every second through the Engine; the background, dials, curve and fans animate every frame.
sealed class MainWindow : Window
{
    static string ProfileName(string id) => id switch
    {
        "performance" => Lang.T("Performance", "Rendimiento"), "balanced-performance" => Lang.T("Balanced+", "Equilibrado+"),
        "balanced" => Lang.T("Balanced", "Equilibrado"), "cool" => Lang.T("Cool", "Fresco"), "quiet" => Lang.T("Quiet", "Silencioso"),
        "low-power" => Lang.T("Battery saver", "Ahorro"), "gmode" => "G-Mode", _ => id,
    };

    static string ProfileHint(string id) => id switch
    {
        "performance" => Lang.T("Max clocks, fans up", "Máximo, ventiladores arriba"),
        "balanced-performance" => Lang.T("Gaming, a bit quieter", "Juego, algo más silencioso"),
        "balanced" => Lang.T("Everyday", "Uso diario"), "cool" => Lang.T("Lower temperatures", "Temperaturas más bajas"),
        "quiet" => Lang.T("Fans down", "Ventiladores bajos"), "low-power" => Lang.T("Longest battery", "Más batería"),
        _ => "",
    };

    readonly Ambient ambient = new();
    readonly Border enginePill = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 6), BorderThickness = new Thickness(1) };
    readonly Border engineDot = new() { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock engineText = T(12.5, Palette.Ink);
    readonly Border gameChip = new() { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 6), BorderThickness = new Thickness(1), BorderBrush = Palette.Hot, IsVisible = false };
    readonly TextBlock gameText = T(12.5, Palette.Ink);

    readonly RingGauge cpuRing = new(), gpuRing = new();
    readonly TextBlock cpuValue = Big(), gpuValue = Big(), cpuSub = T(12.5, Palette.Muted), gpuSub = T(12.5, Palette.Muted);
    readonly Border severityDot = new() { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock severityText = Caps("");
    readonly TextBlock headline = new() { FontFamily = Fonts.Display, FontSize = 26, Foreground = Palette.Ink, TextWrapping = TextWrapping.Wrap, LineHeight = 34 };
    readonly TextBlock explanation = T(15, Palette.Muted, wrap: true);
    readonly StackPanel findings = new() { Spacing = 12, Margin = new Thickness(0, 6, 0, 0) };

    readonly Grid modeCells = new();
    readonly Border glider = new() { CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    readonly TextBlock modeNote = T(12.5, Palette.Muted, wrap: true);

    readonly GlowGraph graph = new() { Height = 210 };
    readonly TextBlock graphNow = T(12.5, Palette.Ink);
    readonly FanView cpuFan = new() { Width = 132, Height = 132 }, gpuFan = new() { Width = 132, Height = 132 };
    readonly TextBlock cpuFanText = T(13, Palette.Ink), gpuFanText = T(13, Palette.Ink);

    readonly WrapPanel swatches = new();
    readonly Slider brightness = new() { Minimum = 0, Maximum = 100, Value = 100 };
    readonly TextBlock lightNote = T(12.5, Palette.Muted, wrap: true);

    readonly StackPanel batteryChoices = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly TextBlock batteryNote = T(12.5, Palette.Muted, wrap: true);
    readonly ToggleSwitch autoPower = new() { OnContent = "", OffContent = "" }, startup = new() { OnContent = "", OffContent = "" };
    readonly StackPanel languageChoices = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    readonly StackPanel gameRows = new() { Spacing = 10 };
    BatteryCharge? battery;
    readonly Queue<double> recentCpu = new(); // the last 15 s of CPU temperature
    readonly ScrollViewer scroller = new();

    // The settings are the last panel: scroll to the end once the layout has its size, and again once the game list
    // has loaded (it makes the panel taller).
    bool settingsShown;
    public void ShowSettings()
    {
        settingsShown = true;
        Dispatcher.UIThread.Post(scroller.ScrollToEnd, DispatcherPriority.Background);
    }

    readonly StackPanel driverList = new() { Spacing = 12 };
    readonly TextBlock driverStatus = T(13.5, Palette.Muted, wrap: true);

    Status? status;
    double load;
    PowerPlan? plan;
    DriverReport? drivers;
    Hog? hog;
    string? game, lightColor;
    DispatcherTimer? brightnessDelay;
    bool loadingLights, windowsUpdateChecked, scanning, installing, polling, closed;
    string? shownProfiles, shownFindings;
    string[] modeIds = [];
    double warmTarget;
    int tick;
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    TimeSpan lastFrame;
    long frame;

    public MainWindow()
    {
        Title = "Zenit Engine";
        Width = 1380; Height = 940; MinWidth = 1120; MinHeight = 720;
        // Never taller than the screen above the taskbar (at 150 % scaling 940 doesn't fit on a 1080p-class panel).
        if (Screens.Primary is { } screen) Height = Math.Max(MinHeight, Math.Min(Height, screen.WorkingArea.Height / screen.Scaling - 32));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brushes.Black;

        // ---- header
        var brand = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center,
            Children = { new Wordmark { Scale = 0.2, Fill = Palette.Ink, VerticalAlignment = VerticalAlignment.Center }, Caps("ENGINE") },
        };
        gameChip.Child = Row3(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Palette.Hot, VerticalAlignment = VerticalAlignment.Center }, Caps(Lang.T("IN GAME", "EN JUEGO")), gameText);
        enginePill.Child = Row3(engineDot, engineText, null);
        var settingsButton = Button(Lang.T("Settings", "Ajustes"), ShowSettings);
        settingsButton.Padding = new Thickness(14, 6);
        var statusBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { gameChip, enginePill, settingsButton } };
        DockPanel.SetDock(statusBar, Dock.Right);
        var header = new DockPanel { Margin = new Thickness(36, 22, 36, 4), Children = { statusBar, brand } };

        // ---- hero: two dials and the diagnosis
        var dials = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Dial(cpuRing, cpuValue, "CPU", cpuSub), Dial(gpuRing, gpuValue, "GPU", gpuSub) } };
        var verdict = new StackPanel
        {
            Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(36, 0, 6, 0),
            Children = { Row3(severityDot, severityText, null), headline, explanation, findings },
        };
        var divider = new Border
        {
            Width = 1, Margin = new Thickness(18, 24, 0, 24),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.Parse("#2E3A4C"), 0.5), new GradientStop(Colors.Transparent, 1) },
            },
        };
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*") };
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(verdict, 2);
        hero.Children.AddRange([dials, divider, verdict]);

        // ---- mode bar
        glider.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#B9DBFF"), 0), new GradientStop(Color.Parse("#6FA8F5"), 1) },
        };
        glider.BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, Color = Color.Parse("#668EC5FF") });
        glider.Transitions = [new ThicknessTransition { Property = MarginProperty, Duration = TimeSpan.FromMilliseconds(320), Easing = new CubicEaseOut() }];
        modeCells.SizeChanged += (_, _) => PlaceGlider();
        var modeTrack = new Border
        {
            CornerRadius = new CornerRadius(17), Padding = new Thickness(4), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.Parse("#07090D")), BorderBrush = new SolidColorBrush(Color.Parse("#1C2430")),
            Child = new Panel { Children = { glider, modeCells } },
        };
        modeNote.Text = Lang.T("Zenit spots a game when it starts, from any launcher, switches to its profile and puts this one back when you quit. F6 is Performance Boost.",
                               "Zenit detecta un juego al abrirse, desde cualquier launcher, cambia a su perfil y vuelve a este al cerrarlo. F6 activa el Performance Boost.");
        var modeBody = new StackPanel { Spacing = 12, Children = { modeTrack, modeNote } };

        // ---- graph and fans
        var graphBody = new StackPanel { Spacing = 10, Children = { graph, T(12.5, Palette.Muted, Lang.T("The dashed line is 105 °C, where the chip slows itself down.", "La línea discontinua es 105 °C, donde el chip se frena solo."), wrap: true) } };
        var fansBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var gpuFanBlock = FanBlock(gpuFan, Lang.T("GPU FAN", "VENTILADOR GPU"), gpuFanText);
        Grid.SetColumn(gpuFanBlock, 1);
        fansBody.Children.AddRange([FanBlock(cpuFan, Lang.T("CPU FAN", "VENTILADOR CPU"), cpuFanText), gpuFanBlock]);

        // ---- lighting
        foreach (var (name, hex) in LightPresets) swatches.Children.Add(Swatch(name, hex));
        brightness.ValueChanged += (_, _) =>
        {
            if (loadingLights) return;
            // Apply once the slider settles: each change is several USB reports to the lighting controller.
            brightnessDelay?.Stop();
            brightnessDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { brightnessDelay?.Stop(); _ = ApplyLights(lightColor); });
            brightnessDelay.Start();
        };
        var lightBody = new StackPanel
        {
            Spacing = 14,
            Children = { swatches, new DockPanel { Children = { Docked(Caps(Lang.T("BRIGHTNESS", "BRILLO")), Dock.Left, new Thickness(0, 0, 16, 0)), brightness } }, lightNote },
        };

        // ---- drivers
        var driverActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        driverActions.Children.Add(Button(Lang.T("Scan again", "Analizar"), () => _ = ScanDrivers(false)));
        driverActions.Children.Add(Button(Lang.T("Check for updates", "Buscar actualizaciones"), () => _ = ScanDrivers(true)));
        driverActions.Children.Add(Button(Lang.T("Update drivers", "Actualizar controladores"), () => _ = InstallDrivers(), primary: true));
        var driverBody = new StackPanel { Spacing = 16, Children = { driverStatus, driverList } };

        // ---- settings: stored in settings.json, the BIOS (battery), the Run key (startup) and game-profiles.json
        autoPower.IsChecked = Settings.Current.AutoPowerProfile;
        autoPower.IsCheckedChanged += (_, _) => (Settings.Current with { AutoPowerProfile = autoPower.IsChecked == true }).Save();
        startup.IsChecked = Machine.StartsWithWindows();
        startup.IsCheckedChanged += (_, _) => Machine.StartWithWindows(startup.IsChecked == true);
        Choices(languageChoices, [("", Lang.T("Windows", "Windows")), ("en", "English"), ("es", "Español")], Settings.Current.Language ?? "", id =>
        {
            (Settings.Current with { Language = id == "" ? null : id }).Save();
            Dispatcher.UIThread.Post(((App)Application.Current!).Reload); // rebuilt in the new language
        });
        var settingsLeft = new StackPanel
        {
            Spacing = 26,
            Children =
            {
                Setting(Lang.T("BATTERY CHARGE LIMIT", "LÍMITE DE CARGA"), batteryChoices, batteryNote),
                Toggle(autoPower, Lang.T("Profile by power source", "Perfil según la alimentación"),
                       Lang.T("Balanced+ on the charger, Balanced on battery, switched when you plug or unplug.", "Equilibrado+ con cargador y Equilibrado con batería, al enchufar o desenchufar.")),
                Toggle(startup, Lang.T("Start with Windows", "Iniciar con Windows"),
                       Lang.T("In the tray, with the temperatures in its tooltip.", "En la bandeja, con las temperaturas al pasar el ratón.")),
                Setting(Lang.T("LANGUAGE", "IDIOMA"), languageChoices, null),
            },
        };
        var settingsRight = Setting(Lang.T("PROFILE PER GAME", "PERFIL POR JUEGO"), gameRows,
            T(12.5, Palette.Muted, Lang.T("Auto: Performance on the charger, Balanced+ on battery.", "Auto: Rendimiento con cargador, Equilibrado+ con batería."), wrap: true));
        var settingsBody = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 48 };
        Grid.SetColumn(settingsRight, 1);
        settingsBody.Children.AddRange([settingsLeft, settingsRight]);

        // ---- layout
        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,460"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 22, RowSpacing = 22, Margin = new Thickness(36, 18, 36, 40),
        };
        Place(content, Panel(null, hero, padding: new Thickness(18, 14, 30, 14)), 0, 0, 2);
        Place(content, Panel(Lang.T("THERMAL PROFILE", "PERFIL TÉRMICO"), modeBody), 1, 0, 2);
        Place(content, Panel(Lang.T("CPU · LAST 60 SECONDS", "CPU · ÚLTIMOS 60 SEGUNDOS"), graphBody, graphNow), 2, 0, 1);
        Place(content, Panel(Lang.T("COOLING", "REFRIGERACIÓN"), fansBody), 2, 1, 1);
        Place(content, Panel(Lang.T("DRIVERS", "CONTROLADORES"), driverBody, driverActions), 3, 0, 1);
        Place(content, Panel(Lang.T("LIGHTING", "ILUMINACIÓN"), lightBody), 3, 1, 1);
        Place(content, Panel(Lang.T("SETTINGS", "AJUSTES"), settingsBody), 4, 0, 2);

        var page = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        page.Children.AddRange([header, scroller]);
        scroller.Content = content;
        Content = new Grid { Children = { ambient, page } };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Poll();
        // ponytail: a fixed 30 fps budget instead of the display's refresh rate; a thermal monitor must not heat the laptop.
        var frames = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Frame());
        Opened += (_, _) => { frames.Start(); Poll(); timer.Start(); _ = ScanDrivers(false); _ = LoadLights(); _ = LoadSettings(); };
        Closed += (_, _) => { closed = true; timer.Stop(); frames.Stop(); };
    }

    // Each tick repaints only what moves, at a rate that suits it: the fans turn at 30 fps, the dials ease only while
    // their reading changes, the background shifts every 5 s (repainting it recomposes the whole window), and the
    // curve repaints once per reading. Blur filters are kept off anything that moves. Measured on the 16X Aurora:
    // uncapped 60 fps cost 66 % of a core and a third of the GPU. Out of focus the fans stop; hidden in the tray nothing animates. Measured: about 11 % of a core focused, 6 % behind other windows.
    void Frame()
    {
        if (closed || !IsVisible || WindowState == WindowState.Minimized) return;
        var now = clock.Elapsed;
        frame++;
        if (frame % 150 == 0)
        {
            ambient.Time = now.TotalSeconds;
            ambient.Warm += (warmTarget - ambient.Warm) * 0.6;
            ambient.InvalidateVisual();
        }
        if (cpuRing.Step()) cpuRing.InvalidateVisual();
        if (gpuRing.Step()) gpuRing.InvalidateVisual();
        if (IsActive)
        {
            var dt = Math.Min((now - lastFrame).TotalSeconds, 0.5);
            lastFrame = now;
            cpuFan.Advance(dt); cpuFan.InvalidateVisual();
            gpuFan.Advance(dt); gpuFan.InvalidateVisual();

        }
    }

    async void Poll()
    {
        if (polling) return;
        polling = true;
        try
        {
            var refreshPlan = tick++ % 20 == 0;
            (status, load, plan, game) = await Task.Run(() => (Machine.EngineStatus(), SafeLoad(), refreshPlan ? SafePlan() : plan, SafeGame()));
            if (status?.TempsC.TryGetValue("cpu", out var reading) == true)
            {
                recentCpu.Enqueue(reading);
                if (recentCpu.Count > 15) recentCpu.Dequeue();
            }
            // Who is to blame, every 5 s and only while the CPU stays hot doing little.
            var hot = recentCpu.Count > 0 && Diagnosis.IsIdleHeat(recentCpu.Min(), load);
            if (!hot) hog = null;
            else if (tick % 5 == 0) hog = await Task.Run(() => { try { return Machine.TopProcess(); } catch { return null; } });
            Update();
        }
        finally { polling = false; }
    }

    static double SafeLoad() { try { return Machine.CpuLoad(); } catch { return 0; } }
    static string? SafeGame() { try { return Machine.RunningGame(); } catch { return null; } }
    static PowerPlan? SafePlan() { try { return Machine.ActivePowerPlan(); } catch { return null; } }

    void Update()
    {
        var running = status is not null;
        if (engineDot.Background != (running ? Palette.Ok : Palette.Hot))
        {
            engineDot.Background = running ? Palette.Ok : Palette.Hot;
            engineDot.BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = running ? Color.Parse("#AA7AD7A8") : Color.Parse("#AAFF8A5C") });
            engineText.Text = running ? Lang.T("Engine running", "Engine en marcha") : Lang.T("Engine stopped", "Engine detenido");
            enginePill.BorderBrush = new SolidColorBrush(Color.Parse(running ? "#2A3A34" : "#4A2A22"));
        }
        gameChip.IsVisible = game is not null;
        gameText.Text = game ?? "";

        if (running && status!.TempsC.TryGetValue("cpu", out var cpu))
        {
            cpuRing.Target = (float)cpu;
            cpuValue.Text = $"{cpu:0}°";
            cpuValue.Foreground = new SolidColorBrush(Heat.Brush((float)cpu));
            graph.Add(cpu);
            graph.InvalidateVisual();
            graphNow.Text = $"{cpu:0} °C";
            warmTarget = Math.Clamp((cpu - 60) / 40, 0, 1);
        }
        if (running && status!.TempsC.TryGetValue("gpu", out var gpu))
        {
            gpuRing.Target = (float)gpu;
            gpuValue.Text = $"{gpu:0}°";
            gpuValue.Foreground = new SolidColorBrush(Heat.Brush((float)gpu));
        }
        cpuSub.Text = Lang.T($"{load:0} % load", $"{load:0} % de carga");
        gpuSub.Text = running ? ProfileName(status!.Profile) : "";

        var fans = status?.Fans ?? [];
        if (fans.Length > 0) { cpuFan.Rpm = fans[0].Rpm; cpuFan.MaxRpm = fans[0].MaxRpm; cpuFanText.Text = $"{fans[0].Rpm:N0} rpm"; }
        if (fans.Length > 1) { gpuFan.Rpm = fans[1].Rpm; gpuFan.MaxRpm = fans[1].MaxRpm; gpuFanText.Text = $"{fans[1].Rpm:N0} rpm"; }

        var key = running ? status!.Profile + "|" + string.Join(",", status.Profiles) : "";
        if (key != shownProfiles)
        {
            shownProfiles = key;
            BuildModes(running ? status!.Profiles : [], running ? status!.Profile : null);
        }

        // The diagnosis judges the lowest CPU reading of the last 15 s: this class of CPU jumps to 90-100 °C for a
        // second or two whenever a core boosts, which is normal; heat that holds is what matters. The dial stays live.
        var steady = status is not null && recentCpu.Count > 0
            ? status with { TempsC = new(status.TempsC) { ["cpu"] = recentCpu.Min() } } : status;
        var list = Diagnosis.Evaluate(steady, load, plan, drivers, hog);
        var summary = Diagnosis.Summarize(list);
        headline.Text = summary.Title;
        explanation.Text = summary.Detail;
        var levelColor = Palette.For(summary.Level);
        if (severityDot.Background != levelColor)
        {
            severityDot.Background = levelColor;
            severityDot.BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, Color = ((ISolidColorBrush)levelColor).Color });
        }
        severityText.Text = summary.Level switch
        {
            Severity.Critical => Lang.T("NEEDS ACTION NOW", "REQUIERE ACCIÓN YA"), Severity.Warning => Lang.T("NEEDS ATTENTION", "REQUIERE ATENCIÓN"),
            Severity.Info => Lang.T("FOR YOUR INFO", "PARA TU INFORMACIÓN"), _ => Lang.T("ALL CLEAR", "TODO EN ORDEN"),
        };
        // Rebuilt only when the findings change: recreating glowing rows every second costs a repaint of the hero panel.
        var rest = list.Where(f => f != summary).Take(3).ToList();
        var end = list.FirstOrDefault(f => f.End is not null)?.End;
        var shown = string.Join("|", rest.Select(f => f.Title + f.Detail)) + end?.Pid;
        if (shown == shownFindings) return;
        shownFindings = shown;
        findings.Children.Clear();
        foreach (var f in rest) findings.Children.Add(Row(f.Level, f.Title, f.Detail));
        if (end is not null)
            findings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { Button(Lang.T($"Stop {end.Name}", $"Detener {end.Name}"), () => Stop(end), primary: true) } });
    }

    // ---- mode bar

    void BuildModes(string[] ids, string? active)
    {
        modeIds = ids;
        modeCells.Children.Clear();
        modeCells.ColumnDefinitions.Clear();
        if (ids.Length == 0)
        {
            glider.IsVisible = false;
            modeCells.Children.Add(new Border { Padding = new Thickness(16, 14), Child = T(13.5, Palette.Muted, Lang.T("Profiles appear when the Engine is running.", "Los perfiles aparecen cuando el Engine está en marcha.")) });
            return;
        }
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            var on = id == active;
            modeCells.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var name = new TextBlock
            {
                Text = ProfileName(id), FontFamily = Fonts.Body, FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = on ? new SolidColorBrush(Color.Parse("#06101C")) : Palette.Ink,
            };
            var hint = new TextBlock
            {
                Text = ProfileHint(id), FontFamily = Fonts.Body, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = on ? new SolidColorBrush(Color.Parse("#2A4466")) : Palette.Muted,
            };
            var cell = new Border
            {
                Padding = new Thickness(10, 12), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), CornerRadius = new CornerRadius(13),
                Child = new StackPanel { Spacing = 3, Children = { name, hint } },
            };
            if (!on)
            {
                cell.PointerEntered += (_, _) => cell.Background = new SolidColorBrush(Color.Parse("#0FFFFFFF"));
                cell.PointerExited += (_, _) => cell.Background = Brushes.Transparent;
            }
            cell.PointerPressed += (_, _) => _ = SetProfile(id);
            Grid.SetColumn(cell, i);
            modeCells.Children.Add(cell);
        }
        PlaceGlider();
    }

    // The lit pill slides to the active profile.
    void PlaceGlider()
    {
        var index = status is null ? -1 : Array.IndexOf(modeIds, status.Profile);
        if (index < 0 || modeCells.Bounds.Width <= 0) { glider.IsVisible = index >= 0; return; }
        var cellWidth = modeCells.Bounds.Width / modeIds.Length;
        glider.IsVisible = true;
        glider.Width = cellWidth;
        glider.Height = modeCells.Bounds.Height;
        glider.Margin = new Thickness(index * cellWidth, 0, 0, 0);
    }

    // ---- lighting

    // Curated, not a rainbow picker: colors that read well through keycaps and the logo diffuser.
    static (string Name, string Hex)[] LightPresets =>
    [
        ("Aurora", "AURORA"), ("Zenith", "8EC5FF"), ("Ice", "E6F2FF"), ("Ember", "FF5A1F"), ("Crimson", "FF1E3C"),
        ("Solar", "FFB000"), ("Plasma", "B04BFF"), ("Toxic", "39FF88"), (Lang.T("Off", "Apagado"), "000000"),
    ];

    Control Swatch(string name, string hex)
    {
        var off = hex == "000000";
        var aurora = hex == "AURORA";
        // Aurora is the drifting effect: shown as its own northern-lights gradient. Solid colors get a lit glass bead.
        var color = aurora ? Color.Parse("#00E0D0") : Color.Parse("#" + hex);
        IBrush fill = aurora
            ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#19FF7A"), 0), new GradientStop(Color.Parse("#1E7BFF"), 0.55), new GradientStop(Color.Parse("#D02BFF"), 1) },
            }
            : new RadialGradientBrush
            {
                Center = new RelativePoint(0.35, 0.3, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.35, 0.3, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb((byte)Math.Min(255, color.R + 70), (byte)Math.Min(255, color.G + 70), (byte)Math.Min(255, color.B + 70)), 0),
                    new GradientStop(color, 0.65),
                },
            };
        var dot = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(15),
            Background = off ? Brushes.Transparent : fill,
            BorderBrush = Palette.Muted, BorderThickness = new Thickness(off ? 1.5 : 0),
            BoxShadow = off ? default : new BoxShadows(new BoxShadow { Blur = 18, Spread = 1, Color = Color.FromArgb(0x80, color.R, color.G, color.B) }),
        };
        var ring = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(21), BorderThickness = new Thickness(2), Tag = hex,
            BorderBrush = Brushes.Transparent, Child = dot, Cursor = new Cursor(StandardCursorType.Hand), Margin = new Thickness(0, 0, 6, 8),
        };
        ToolTip.SetTip(ring, name);
        ring.PointerPressed += (_, _) => _ = ApplyLights(hex);
        return ring;
    }

    void ShowLights()
    {
        foreach (var s in swatches.Children.OfType<Border>())
            s.BorderBrush = (string?)s.Tag == lightColor ? Palette.Ink : Brushes.Transparent;
    }

    async Task LoadLights()
    {
        var saved = await Task.Run(() => { try { return Machine.Lights(); } catch { return null; } });
        loadingLights = true;
        if (saved is not null) brightness.Value = saved.Brightness;
        loadingLights = false;
        lightColor = saved is null ? LightPresets[0].Hex : saved.Brightness == 0 ? "000000" : saved.Color;
        lightNote.Text = Lang.T("Keyboard and logo, straight to the AlienFX controller, no Alienware Command Center needed. F9 steps the brightness, F7 is Stealth.",
                                "Teclado y logo, directo al controlador AlienFX, sin Alienware Command Center. F9 cambia el brillo, F7 activa Stealth.");
        ShowLights();
    }

    async Task ApplyLights(string? hex)
    {
        if (hex is null) return;
        lightColor = hex;
        ShowLights();
        var off = hex == "000000";
        var level = off ? 0 : (int)brightness.Value;
        var error = await Task.Run(() => { try { return Machine.SetLights(off ? "FFFFFF" : hex, level); } catch (Exception e) { return e.Message; } });
        if (error is not null) lightNote.Text = error;
    }

    void Stop(Hog h)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(h.Pid); p.Kill(); hog = null; Update(); }
        catch (Exception e) { explanation.Text = Lang.T($"Couldn't stop {h.Name}: {e.Message}", $"No se pudo detener {h.Name}: {e.Message}"); }
    }

    async Task SetProfile(string profile)
    {
        await Task.Run(() => Machine.SetProfile(profile));
        shownProfiles = null;
        Poll();
    }

    // ---- settings

    async Task LoadSettings()
    {
        battery = await Task.Run(() => { try { return Machine.Battery(); } catch { return null; } });
        ShowBattery();
        var (games, chosen) = await Task.Run(() =>
        {
            try { return (Machine.Games(), Machine.GameProfiles()); }
            catch { return (Array.Empty<GameEntry>(), new Dictionary<string, string>()); }
        });
        ShowGames(games, chosen);
        if (settingsShown) ShowSettings();
    }

    // Zenit offers Dell's Adaptive mode and two fixed limits; a mode set elsewhere (Dell's BIOS setup, Power Manager)
    // shows as its own choice so the current state is never hidden.
    void ShowBattery()
    {
        if (battery is null)
        {
            batteryChoices.Children.Clear();
            batteryNote.Text = Lang.T("Not available here: it needs the Engine running on a Dell or Alienware laptop whose BIOS offers charge settings.",
                                      "No disponible aquí: necesita el Engine en marcha en un Dell o Alienware cuya BIOS ofrezca ajustes de carga.");
            return;
        }
        var current = battery.Mode switch { "Custom" => $"{battery.Stop}", "Adaptive" => "adaptive", var mode => mode };
        var options = new List<(string, string)> { ("adaptive", Lang.T("Adaptive", "Adaptativo")), ("80", "80 %"), ("60", "60 %") };
        if (!options.Any(o => o.Item1 == current))
            options.Add((current, battery.Mode switch
            {
                "Custom" => $"{battery.Stop} %", "Standard" => Lang.T("Standard", "Estándar"), "Express" => "ExpressCharge",
                "PrimAcUse" => Lang.T("Mainly on AC", "Uso con CA"), var mode => mode,
            }));
        Choices(batteryChoices, [.. options], current, id => _ = SetBattery(id));
        batteryNote.Text = battery.Mode == "Custom"
            ? Lang.T($"Charging stops at {battery.Stop} % and resumes below {battery.Start} %. A battery that isn't kept full wears more slowly. Saved in the laptop's BIOS.",
                     $"La carga se detiene en {battery.Stop} % y se reanuda bajo {battery.Start} %. Una batería que no se mantiene llena se desgasta más lento. Se guarda en la BIOS del portátil.")
            : Lang.T("Adaptive is Dell's default: it picks the limit from how you use the laptop. 80 % suits a laptop that lives on the charger. Saved in the BIOS.",
                     "Adaptativo es el modo de Dell: elige el límite según cómo usas el portátil. 80 % conviene si vive enchufado. Se guarda en la BIOS.");
    }

    async Task SetBattery(string id)
    {
        if (id is not ("adaptive" or "80" or "60")) return; // a mode set elsewhere is only shown
        batteryNote.Text = Lang.T("Saving in the BIOS…", "Guardando en la BIOS…");
        var error = await Task.Run(() => Machine.SetBattery(id));
        battery = await Task.Run(() => { try { return Machine.Battery(); } catch { return null; } });
        ShowBattery();
        if (error is not null) batteryNote.Text = error;
    }

    void ShowGames(GameEntry[] games, Dictionary<string, string> chosen)
    {
        gameRows.Children.Clear();
        if (games.Length == 0)
        {
            gameRows.Children.Add(T(13.5, Palette.Muted, Lang.T("No installed games found yet.", "Aún no se encontraron juegos instalados."), wrap: true));
            return;
        }
        string[] ids = ["auto", "performance", "balanced-performance", "balanced", "quiet", "cool"];
        foreach (var game in games.DistinctBy(g => g.Id).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var box = new ComboBox
            {
                Width = 200, ItemsSource = ids.Select(id => id == "auto" ? "Auto" : ProfileName(id)).ToArray(),
                SelectedIndex = Math.Max(0, Array.IndexOf(ids, chosen.GetValueOrDefault(game.Id) ?? "auto")),
            };
            box.SelectionChanged += (_, _) => { var id = ids[box.SelectedIndex]; _ = Task.Run(() => Machine.SetGameProfile(game.Id, id)); };
            var title = T(14, Palette.Ink, game.Title);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            gameRows.Children.Add(new DockPanel { Children = { Docked(box, Dock.Right, new Thickness(16, 0, 0, 0)), title } });
        }
    }

    // A row of choices: the selected one is the lit button.
    static void Choices(StackPanel host, (string Id, string Label)[] options, string selected, Action<string> pick)
    {
        host.Children.Clear();
        foreach (var (id, label) in options) host.Children.Add(Button(label, () => pick(id), primary: id == selected));
    }

    static Control Setting(string title, Control body, Control? note)
    {
        var stack = new StackPanel { Spacing = 12, Children = { Caps(title), body } };
        if (note is not null) stack.Children.Add(note);
        return stack;
    }

    static Control Toggle(ToggleSwitch toggle, string title, string detail)
    {
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { T(14.5, Palette.Ink, title), T(13, Palette.Muted, detail, wrap: true) } };
        return new DockPanel { Children = { Docked(toggle, Dock.Right, new Thickness(24, 0, 0, 0)), text } };
    }

    // ---- drivers

    async Task ScanDrivers(bool online)
    {
        if (scanning) return;
        scanning = true;
        driverStatus.Text = online
            ? Lang.T("Checking Windows Update and the laptop maker's catalog for official driver updates. This can take a minute.",
                     "Buscando actualizaciones oficiales en Windows Update y en el catálogo del fabricante. Puede tardar un minuto.")
            : Lang.T("Scanning devices.", "Analizando dispositivos.");
        var report = await Task.Run(() => { try { return Machine.Drivers(online); } catch { return null; } });
        scanning = false;
        if (report is not null) { drivers = report; windowsUpdateChecked |= online && report.UpdateError is null; }
        ShowDrivers(online && report?.UpdateError is { } error ? error : null);
        Update();
    }

    // Official drivers only: Windows Update's driver catalog and the laptop maker's, newer versions only.
    async Task InstallDrivers()
    {
        if (installing || scanning) return;
        installing = true;
        driverStatus.Text = Lang.T("Creating a restore point, then downloading and installing official drivers from Windows Update and the laptop maker. Each package is checked before it runs. This can take several minutes.",
                                   "Creando un punto de restauración y luego descargando e instalando controladores oficiales de Windows Update y del fabricante. Cada paquete se verifica antes de ejecutarse. Puede tardar varios minutos.");
        string message;
        try
        {
            var r = await Task.Run(Machine.InstallDrivers);
            message = r.Installed.Length == 0 && r.Failed.Length == 0
                ? Lang.T("Your drivers are up to date: neither Windows Update nor the laptop maker has anything newer.",
                         "Tus controladores están al día: ni Windows Update ni el fabricante tienen nada más nuevo.")
                : Lang.T($"Installed {r.Installed.Length} driver updates", $"Se instalaron {r.Installed.Length} actualizaciones")
                  + (r.Failed.Length > 0 ? Lang.T($", {r.Failed.Length} failed: ", $", {r.Failed.Length} fallaron: ") + string.Join("; ", r.Failed) : "") + "."
                  + (r.RestorePoint ? Lang.T(" A restore point was created first.", " Antes se creó un punto de restauración.") : "")
                  + (r.RebootRequired ? Lang.T(" Restart the laptop to finish.", " Reinicia el equipo para terminar.") : "");
        }
        catch (Exception e) { message = Lang.T($"The install didn't finish: {e.Message}", $"La instalación no terminó: {e.Message}"); }
        installing = false;
        await ScanDrivers(true);
        driverStatus.Text = message + " " + driverStatus.Text;
    }

    void ShowDrivers(string? updateError)
    {
        driverList.Children.Clear();
        if (drivers is null)
        {
            driverStatus.Text = Lang.T("The driver check needs ZenitEngine.exe next to this app. Reinstall with install.ps1.",
                                       "La revisión de controladores necesita ZenitEngine.exe junto a esta app. Reinstala con install.ps1.");
            return;
        }
        var count = drivers.Available.Length + (drivers.Maker?.Length ?? 0);
        var updates = windowsUpdateChecked ? Lang.T($"{count} official updates", $"{count} actualizaciones oficiales") : Lang.T("updates not checked yet", "actualizaciones aún sin revisar");
        driverStatus.Text = Lang.T($"{drivers.Problems.Length} devices with problems, {drivers.Outdated.Length} old drivers, {updates}.",
                                   $"{drivers.Problems.Length} dispositivos con problemas, {drivers.Outdated.Length} controladores antiguos, {updates}.")
            + (updateError is null ? "" : Lang.T($" Windows Update didn't answer: {updateError}", $" Windows Update no respondió: {updateError}"))
            + (drivers.MakerError is { } makerError ? Lang.T($" The laptop maker's catalog didn't answer: {makerError}", $" El catálogo del fabricante no respondió: {makerError}") : "");
        foreach (var d in drivers.Problems)
            driverList.Children.Add(Row(Severity.Warning, d.Name, Lang.T("Status", "Estado") + $" {d.Status}" + (d.DriverVersion is null ? "" : $", {Lang.T("driver", "controlador")} {d.DriverVersion}") + (d.Provider is null ? "" : $" · {d.Provider}") + "."));
        foreach (var d in drivers.Outdated)
            driverList.Children.Add(Row(Severity.Info, d.Name, Lang.T($"Driver {d.DriverVersion} from {d.Provider} is over three years old.", $"El controlador {d.DriverVersion} de {d.Provider} tiene más de tres años.")));
        foreach (var p in drivers.Maker ?? [])
            driverList.Children.Add(Row(Severity.Info, p.Name, Lang.T($"{p.Version} from the laptop maker for {p.Device}, which runs {p.Installed}. {p.Size / 1048576.0:0} MB.", $"{p.Version} del fabricante para {p.Device}, que usa {p.Installed}. {p.Size / 1048576.0:0} MB.")));
        foreach (var u in drivers.Available)
            driverList.Children.Add(Row(Severity.Info, u.Title, Lang.T("Available from Windows Update, Optional updates.", "Disponible en Windows Update, Actualizaciones opcionales.")));
        if (driverList.Children.Count == 0)
            driverList.Children.Add(Row(Severity.Good, Lang.T("No driver problems found", "Sin problemas de controladores"), Lang.T("Every device reports a working driver.", "Todos los dispositivos informan un controlador que funciona.")));
    }

    // ---- building blocks

    static TextBlock T(double size, IBrush brush, string text = "", bool wrap = false) =>
        new() { FontFamily = Fonts.Body, FontSize = size, Foreground = brush, Text = text, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };

    static TextBlock Big() => new() { FontFamily = Fonts.Display, FontSize = 46, Foreground = Palette.Ink, Text = "--", HorizontalAlignment = HorizontalAlignment.Center };

    static TextBlock Caps(string text) => new()
    {
        FontFamily = Fonts.Body, FontSize = 11.5, FontWeight = FontWeight.Medium, Foreground = Palette.Muted, Text = text, LetterSpacing = 2.4, VerticalAlignment = VerticalAlignment.Center,
    };

    static StackPanel Row3(Control a, Control b, Control? c)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center, Children = { a, b } };
        if (c is not null) row.Children.Add(c);
        return row;
    }

    // A dial: the ring with the reading, its name and a sub-line inside.
    static Control Dial(RingGauge ring, TextBlock value, string name, TextBlock sub)
    {
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        var center = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
            Children = { new TextBlock { Text = name, FontFamily = Fonts.Body, FontSize = 12, LetterSpacing = 3, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center }, value, sub },
        };
        return new Grid { Width = 250, Height = 250, Children = { ring, center } };
    }

    static Control FanBlock(FanView fan, string label, TextBlock rpm)
    {
        rpm.HorizontalAlignment = HorizontalAlignment.Center;
        var caps = Caps(label);
        caps.HorizontalAlignment = HorizontalAlignment.Center;
        fan.HorizontalAlignment = HorizontalAlignment.Center;
        return new StackPanel { Spacing = 10, Children = { fan, caps, rpm } };
    }

    // A panel: the shader surface (which paints its own soft drop shadow), a caps title (and optional controls on the right), then the body.
    static Control Panel(string? title, Control body, Control? right = null, Thickness? padding = null)
    {
        // The surface reaches past the panel on every side to paint its own shadow (cheaper than a BoxShadow, which
        // is re-blurred on every repaint underneath a turning fan).
        var pad = Surface.ShadowPad;
        var surface = new Surface { Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, -pad.Bottom) };
        var stack = new StackPanel { Spacing = 18 };
        if (title is not null)
        {
            var head = new DockPanel();
            if (right is not null) { DockPanel.SetDock(right, Dock.Right); right.VerticalAlignment = VerticalAlignment.Center; head.Children.Add(right); }
            head.Children.Add(Caps(title));
            stack.Children.Add(head);
        }
        stack.Children.Add(body);
        var inner = new Border { Padding = padding ?? new Thickness(28, 22, 28, 26), Child = stack };
        var grid = new Grid { Children = { surface, inner } };
        surface.Track(grid);
        return grid;
    }

    static Control Row(Severity level, string title, string detail)
    {
        var color = ((ISolidColorBrush)Palette.For(level)).Color;
        var dot = new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Palette.For(level), Margin = new Thickness(0, 7, 14, 0), VerticalAlignment = VerticalAlignment.Top,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = color }),
        };
        var text = new StackPanel { Spacing = 3, Children = { T(14.5, Palette.Ink, title, wrap: true), T(13.5, Palette.Muted, detail, wrap: true) } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(text, 1);
        row.Children.AddRange([dot, text]);
        return row;
    }

    // Glass buttons with a machined rim; the primary one is lit.
    static Border Button(string text, Action onClick, bool primary = false)
    {
        var label = new TextBlock { Text = text, FontFamily = Fonts.Body, FontSize = 13.5, FontWeight = FontWeight.SemiBold, Foreground = primary ? new SolidColorBrush(Color.Parse("#06101C")) : Palette.Ink };
        IBrush idleBorder = new SolidColorBrush(Color.Parse(primary ? "#C9E3FF" : "#2A3444"));
        BoxShadows idleShadow = primary ? new BoxShadows(new BoxShadow { Blur = 18, Color = Color.Parse("#558EC5FF") }) : default;
        var button = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 9), BorderThickness = new Thickness(1), BorderBrush = idleBorder,
            Cursor = new Cursor(StandardCursorType.Hand), Child = label, BoxShadow = idleShadow,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = primary
                    ? [new GradientStop(Color.Parse("#B9DBFF"), 0), new GradientStop(Color.Parse("#6FA8F5"), 1)]
                    : [new GradientStop(Color.Parse("#1B222D"), 0), new GradientStop(Color.Parse("#11161E"), 1)],
            },
        };
        button.PointerEntered += (_, _) => { button.BorderBrush = Palette.Flare; button.BoxShadow = new BoxShadows(new BoxShadow { Blur = 20, Color = Color.Parse("#668EC5FF") }); };
        button.PointerExited += (_, _) => { button.BorderBrush = idleBorder; button.BoxShadow = idleShadow; };
        button.PointerPressed += (_, _) => onClick();
        return button;
    }

    static Control Docked(Control c, Dock dock, Thickness margin)
    {
        DockPanel.SetDock(c, dock);
        c.Margin = margin;
        c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }

    static void Place(Grid grid, Control child, int row, int column, int span)
    {
        Grid.SetRow(child, row);
        Grid.SetColumn(child, column);
        Grid.SetColumnSpan(child, span);
        grid.Children.Add(child);
    }
}
