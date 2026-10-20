using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

// The on-screen notice for the Alienware function keys (Performance Boost, Stealth, keyboard lights): a dark glass
// pill above the taskbar that fades in, stays a moment and fades out. Never takes focus, so it can't steal a game's.
sealed class Osd : Window
{
    readonly TextBlock title = new() { FontFamily = Fonts.Display, FontSize = 12, LetterSpacing = 3, Foreground = Palette.Muted };
    readonly TextBlock detail = new() { FontFamily = Fonts.Body, FontSize = 17, Foreground = Palette.Ink };
    readonly Border glyph = new() { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer hide;

    public Osd()
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Opacity = 0;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];

        Content = new Border
        {
            Margin = new Thickness(24), // room for the shadow
            CornerRadius = new CornerRadius(22), Padding = new Thickness(22, 14, 26, 14),
            Background = new SolidColorBrush(Color.Parse("#F00B0E14")),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#2A3444")),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 28, Color = Color.Parse("#80000000"), OffsetY = 6 }),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 16,
                Children = { glyph, new StackPanel { Spacing = 2, Children = { title, detail } } },
            },
        };
        hide = new DispatcherTimer(TimeSpan.FromMilliseconds(1600), DispatcherPriority.Normal, (_, _) => FadeOut());
        // The size is only known after layout, and changes with the text: place it again whenever it does.
        SizeChanged += (_, _) => Place();
    }

    // Bottom center of the primary screen, 40 px above the taskbar.
    void Place()
    {
        if (Screens.Primary is not { } screen || Bounds.Width <= 0) return;
        var area = screen.WorkingArea;
        var size = PixelSize.FromSize(Bounds.Size, screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - size.Width) / 2, area.Bottom - size.Height - (int)(40 * screen.Scaling));
    }

    public void Notify(string heading, string text)
    {
        title.Text = heading.ToUpperInvariant();
        detail.Text = text;
        var color = heading.StartsWith("Performance") ? Color.Parse("#FF8A5C") : heading.StartsWith("Stealth") ? Color.Parse("#B9C2D0") : Palette.FlareColor;
        glyph.Background = new SolidColorBrush(color);
        glyph.BoxShadow = new BoxShadows(new BoxShadow { Blur = 12, Spread = 1, Color = Color.FromArgb(0xB0, color.R, color.G, color.B) });

        if (!IsVisible) Show();
        Place();
        Opacity = 1;
        hide.Stop();
        hide.Start();
    }

    void FadeOut()
    {
        hide.Stop();
        Opacity = 0;
        DispatcherTimer.RunOnce(() => { if (Opacity == 0) Hide(); }, TimeSpan.FromMilliseconds(220));
    }
}
