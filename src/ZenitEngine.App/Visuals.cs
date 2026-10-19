using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

static class Palette
{
    public static readonly Color FlareColor = Color.Parse("#8EC5FF");
    public static readonly IBrush Panel = Brush.Parse("#B80D1117");
    public static readonly IBrush Line = Brush.Parse("#1E2531");
    public static readonly IBrush Ink = Brush.Parse("#EEF1F6");
    public static readonly IBrush Muted = Brush.Parse("#8A93A6");
    public static readonly IBrush Flare = new SolidColorBrush(FlareColor);
    public static readonly IBrush Hot = Brush.Parse("#FF8A5C");
    public static readonly IBrush Warn = Brush.Parse("#F5C26B");
    public static readonly IBrush Ok = Brush.Parse("#7AD7A8");
    public static readonly IBrush Track = Brush.Parse("#1A202A");

    public static IBrush For(Severity s) => s switch
    {
        Severity.Critical => Hot, Severity.Warning => Warn, Severity.Info => Flare, _ => Ok,
    };

    public static IBrush ForTemp(double c) => c >= 95 ? Hot : c >= 85 ? Warn : Ink;
}

static class Fonts
{
    public static readonly FontFamily Body = new("avares://ZenitEngine.App/Assets/Fonts#Archivo");
    // Expanded faces are referenced by file: folder-wide lookups resolve every Archivo name to the normal width.
    public static readonly FontFamily Display = new("avares://ZenitEngine.App/Assets/Fonts/ArchivoExpanded-SemiBold.ttf#Archivo Expanded SemiBold");
}

// CPU temperature over the last 60 s, on a fixed 30-110 °C scale with the 105 °C throttle line.
sealed class TraceGraph : Control
{
    public const int Window = 60;
    const double Min = 30, Max = 110, Left = 40, Bottom = 6, Top = 8;
    public List<double> Samples { get; } = [];

    public void Add(double value)
    {
        Samples.Add(value);
        if (Samples.Count > Window) Samples.RemoveAt(0);
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        double X(int i) => Left + (w - Left) * (Window - 1 - (Samples.Count - 1 - i)) / (Window - 1);
        double Y(double v) => Top + (h - Top - Bottom) * (1 - Math.Clamp((v - Min) / (Max - Min), 0, 1));

        var grid = new Pen(Palette.Line, 1);
        foreach (var v in new[] { 40, 60, 80, 100 })
        {
            ctx.DrawLine(grid, new Point(Left, Y(v)), new Point(w, Y(v)));
            ctx.DrawText(Label($"{v}°", Palette.Muted), new Point(0, Y(v) - 8));
        }
        ctx.DrawLine(new Pen(Palette.Hot, 1, new DashStyle([4, 5], 0)), new Point(Left, Y(105)), new Point(w, Y(105)));
        if (Samples.Count < 2) return;

        var last = Samples.Count - 1;
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(X(0), Y(Min)), true);
            for (var i = 0; i <= last; i++) g.LineTo(new Point(X(i), Y(Samples[i])));
            g.LineTo(new Point(X(last), Y(Min)));
            g.EndFigure(true);
        }
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x55, 0x8E, 0xC5, 0xFF), 0), new GradientStop(Color.FromArgb(0, 0x8E, 0xC5, 0xFF), 1) },
        };
        ctx.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(new Point(X(0), Y(Samples[0])), false);
            for (var i = 1; i <= last; i++) g.LineTo(new Point(X(i), Y(Samples[i])));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, new Pen(Palette.Flare, 2.5, lineJoin: PenLineJoin.Round), line);
        ctx.DrawEllipse(Palette.ForTemp(Samples[last]), null, new Point(X(last), Y(Samples[last])), 4.5, 4.5);
    }

    static FormattedText Label(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(Fonts.Body), 11, brush);
}

// A thin horizontal gauge (fan speed).
sealed class Meter : Control
{
    public double Value { get; set; }
    public IBrush Fill { get; set; } = Palette.Flare;

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.DrawRectangle(Palette.Track, null, r, 3, 3);
        ctx.DrawRectangle(Fill, null, r.WithWidth(r.Width * Math.Clamp(Value, 0, 1)), 3, 3);
    }
}