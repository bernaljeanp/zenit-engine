using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

// The ZENIT logotype, drawn as geometry rather than set in a font: hairline bars on a 100-unit cap height,
// wide proportions. The I is the intro's beam, shorter than the caps, with the zenith star as its point.
sealed class Wordmark : Control
{
    // Scale: 100 units of cap height -> Scale * 100 px.
    public double Scale { get; init; } = 0.82;
    const double Cap = 100, Stroke = 6, Letter = 120, Gap = 56;
    const double Diagonal = 10; // horizontal width of diagonals, so they read as thick as the bars
    

    // Extra space between letters; the intro animates it from wide open down to 0.
    public double Spread { get; set; }
    public IBrush? Fill { get; set; }

    // x of each letter at Spread 0: Z, E, N, I, T. The I is a single stroke, so it takes less room.
    static readonly double[] Starts = [0, Letter + Gap, 2 * (Letter + Gap), 3 * (Letter + Gap), 3 * (Letter + Gap) + Stroke + Gap];
    static readonly double Total = Starts[^1] + Letter;

    protected override Size MeasureOverride(Size available) => new((Total + 4 * Spread) * Scale, Cap * Scale);

    public void Refresh()
    {
        InvalidateMeasure();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Fill is null) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            double X(int i) => Starts[i] + i * Spread;

            var z = X(0); // Z: two bars and a diagonal from top right to bottom left
            Rect(c, z, 0, Letter, Stroke);
            Rect(c, z, Cap - Stroke, Letter, Stroke);
            Poly(c, new(z + Letter - Diagonal, Stroke), new(z + Letter, Stroke), new(z + Diagonal, Cap - Stroke), new(z, Cap - Stroke));

            var e = X(1); // E: spine, full top and bottom bars, a shorter middle bar
            Rect(c, e, 0, Stroke, Cap);
            Rect(c, e, 0, Letter, Stroke);
            Rect(c, e, (Cap - Stroke) / 2, Letter * 0.82, Stroke);
            Rect(c, e, Cap - Stroke, Letter, Stroke);

            var n = X(2); // N: two stems and the diagonal between them
            Rect(c, n, 0, Stroke, Cap);
            Rect(c, n + Letter - Stroke, 0, Stroke, Cap);
            Poly(c, new(n, 0), new(n + Diagonal, 0), new(n + Letter, Cap), new(n + Letter - Diagonal, Cap));

            var i = X(3); // I: the beam, rising short of the cap line, with the zenith star above it
            Rect(c, i, Cap * 0.24, Stroke, Cap * 0.76);
            Rect(c, i, 0, Stroke, Stroke);

            var t = X(4); // T
            Rect(c, t, 0, Letter, Stroke);
            Rect(c, t + (Letter - Stroke) / 2, 0, Stroke, Cap);
        }
        using (context.PushTransform(Matrix.CreateScale(Scale, Scale))) context.DrawGeometry(Fill, null, g);
    }

    static void Rect(StreamGeometryContext c, double x, double y, double w, double h) =>
        Poly(c, new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h));

    static void Poly(StreamGeometryContext c, params Point[] points)
    {
        c.BeginFigure(points[0], isFilled: true);
        foreach (var p in points[1..]) c.LineTo(p);
        c.EndFigure(isClosed: true);
    }
}
