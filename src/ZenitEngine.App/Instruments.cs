using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

// The instrument cluster: every panel and gauge is drawn with Skia on the GPU, so surfaces have depth (machined rim
// light, grain, inner shadow, a light that follows the pointer) and instruments emit light (bloom on arcs and curves).
// Colors follow one heat scale everywhere: cool blue, cyan, amber, orange, red at the 105 °C throttle point.

abstract class SkiaView : Control
{
    // Captures what to draw on the UI thread; the returned delegate runs on the render thread.
    protected abstract Action<SKCanvas, float, float> Snapshot();

    // Views that change rarely return a key: their drawing is rendered once into a GPU image at screen resolution and
    // reused until the key changes. A fan turning on a panel then costs an image copy underneath, not a full-window
    // shader pass. Null means draw every time.
    protected virtual string? CacheKey() => null;

    (string Key, SKImage Image)? cache; // touched only on the render thread

    public override void Render(DrawingContext context) =>
        context.Custom(new Op(this, new Rect(Bounds.Size), Snapshot(), CacheKey()));

    sealed class Op(SkiaView owner, Rect bounds, Action<SKCanvas, float, float> paint, string? key) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var c = lease.SkCanvas;
            float w = (float)bounds.Width, h = (float)bounds.Height;
            if (key is null || w < 1 || h < 1)
            {
                c.Save();
                paint(c, w, h);
                c.Restore();
                return;
            }
            var scale = Math.Max(1f, c.TotalMatrix.ScaleX);
            var fullKey = $"{key}|{w}x{h}@{scale}";
            if (owner.cache?.Key != fullKey)
            {
                var info = new SKImageInfo((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), SKColorType.Rgba8888, SKAlphaType.Premul);
                using var surface = (lease.GrContext is { } gpu ? SKSurface.Create(gpu, false, info) : null) ?? SKSurface.Create(info);
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Scale(scale);
                paint(surface.Canvas, w, h);
                owner.cache?.Image.Dispose();
                owner.cache = (fullKey, surface.Snapshot());
            }
            c.DrawImage(owner.cache!.Value.Image, new SKRect(0, 0, w, h));
        }
    }
}

static class Heat
{
    // The one temperature scale: 30-110 °C.
    public const float Min = 30, Max = 110;
    public static readonly SKColor[] Colors = [new(0x4C, 0x8D, 0xFF), new(0x43, 0xD1, 0xFF), new(0xFF, 0xB5, 0x47), new(0xFF, 0x7A, 0x3D), new(0xFF, 0x3B, 0x30)];
    public static readonly float[] Stops = [40, 62, 78, 92, 105];
    public static float Frac(float c) => Math.Clamp((c - Min) / (Max - Min), 0, 1);

    public static SKColor At(float c)
    {
        if (c <= Stops[0]) return Colors[0];
        for (var i = 1; i < Stops.Length; i++)
            if (c <= Stops[i])
            {
                var t = (c - Stops[i - 1]) / (Stops[i] - Stops[i - 1]);
                SKColor a = Colors[i - 1], b = Colors[i];
                return new SKColor((byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t));
            }
        return Colors[^1];
    }

    public static Color Brush(float c) { var s = At(c); return Color.FromRgb(s.Red, s.Green, s.Blue); }
}

// A panel's body: graphite with depth, drawn by an SDF shader so the shape, rim light, grain and drop shadow are one
// pass, cached as an image.
sealed class Surface : SkiaView
{
    const string Sksl = """
        uniform float2 res;
        uniform float2 light;
        uniform float lit;
        uniform float rad;
        uniform float4 pad;   // left, top, right, bottom: room around the panel for its shadow

        float box(float2 p, float2 b, float r) {
            float2 q = abs(p) - b + r;
            return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
        }

        half4 main(float2 fc) {
            float2 size = res - float2(pad.x + pad.z, pad.y + pad.w);
            float2 c = float2(pad.x, pad.y) + size * 0.5;
            float d = box(fc - c, size * 0.5 - 0.5, rad);
            float a = clamp(0.5 - d, 0.0, 1.0);
            // soft drop shadow, offset down, fading out over 40 px
            float sd = box(fc - c - float2(0.0, 16.0), size * 0.5 - 6.0, rad);
            half4 shadow = half4(0.0, 0.0, 0.0, 0.55 * (1.0 - smoothstep(-10.0, 40.0, sd)));
            if (a <= 0.0) return shadow;
            float y = (fc.y - pad.y) / size.y;
            float3 col = mix(float3(0.078, 0.094, 0.122), float3(0.040, 0.049, 0.064), smoothstep(0.0, 1.0, y));
            // brushed metal: noise stretched along x
            float n = fract(sin(dot(floor(fc * float2(0.35, 5.0)), float2(12.9898, 78.233))) * 43758.5453);
            col += (n - 0.5) * 0.010;
            // light pool under the pointer
            col += float3(0.50, 0.72, 1.0) * exp(-length(fc - light) / 240.0) * 0.07 * lit;
            // machined rim: bright along the top, fading down the sides; darker floor
            float edge = smoothstep(2.2, 0.0, -d);
            col += edge * mix(0.012, 0.20, pow(1.0 - y, 3.0));
            col *= 1.0 - smoothstep(0.55, 1.0, y) * 0.12;
            return half4(half3(col) * a, a) + shadow * (1.0 - a);
        }
        """;

    static readonly SKRuntimeEffect Effect = SKRuntimeEffect.CreateShader(Sksl, out var errors)
        ?? throw new InvalidOperationException("surface shader: " + errors);

    Point light = new(-1000, -1000);
    double lit;
    public double Radius { get; init; } = 22;
    // Room for the shadow: the control is laid out this much larger than the panel (see MainWindow.Panel).
    public static readonly Thickness ShadowPad = new(40, 24, 40, 60);

    // The pointer light follows the mouse over the whole panel, content included.
    public void Track(Control panel)
    {
        panel.PointerMoved += (_, e) => { light = e.GetPosition(this); lit = 1; InvalidateVisual(); };
        panel.PointerExited += (_, _) => { lit = 0; InvalidateVisual(); };
    }

    protected override string? CacheKey() => $"{light.X:0},{light.Y:0},{lit},{Radius}";

    protected override Action<SKCanvas, float, float> Snapshot()
    {
        var (lx, ly, l, r) = ((float)light.X, (float)light.Y, (float)lit, (float)Radius);
        return (c, w, h) =>
        {
            var pad = new[] { (float)ShadowPad.Left, (float)ShadowPad.Top, (float)ShadowPad.Right, (float)ShadowPad.Bottom };
            var u = new SKRuntimeEffectUniforms(Effect) { ["res"] = new[] { w, h }, ["light"] = new[] { lx, ly }, ["lit"] = l, ["rad"] = r, ["pad"] = pad };
            using var shader = Effect.ToShader(u);
            using var paint = new SKPaint { Shader = shader };
            c.DrawRect(0, 0, w, h, paint);
        };
    }
}

// A 270° dial on the heat scale, with tick marks, the throttle line and an arc that glows its own color.
sealed class RingGauge : SkiaView
{
    public float Target { get; set; } = Heat.Min;
    public float Value { get; private set; } = Heat.Min;

    // Eases toward Target; true while still moving.
    public bool Step()
    {
        var d = Target - Value;
        if (Math.Abs(d) < 0.05f) { Value = Target; return false; }
        Value += d * 0.12f;
        return true;
    }

    protected override string? CacheKey() => Value.ToString("0.00");

    protected override Action<SKCanvas, float, float> Snapshot()
    {
        var v = Value;
        return (c, w, h) =>
        {
            var cx = w / 2; var cy = h / 2;
            var r = Math.Min(w, h) / 2 - 24;
            var rect = new SKRect(cx - r, cy - r, cx + r, cy + r);
            const float start = 135, sweep = 270;

            using (var track = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 14, StrokeCap = SKStrokeCap.Round, Color = new SKColor(0x16, 0x1B, 0x24) })
                c.DrawArc(rect, start, sweep, false, track);
            using (var inner = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(0xFF, 0xFF, 0xFF, 0x10) })
                c.DrawArc(new SKRect(rect.Left + 10, rect.Top + 10, rect.Right - 10, rect.Bottom - 10), start, sweep, false, inner);

            // ticks every 5 °C, longer every 10, red at 105
            for (var t = 30; t <= 110; t += 5)
            {
                var ang = (start + sweep * Heat.Frac(t)) * MathF.PI / 180;
                var major = t % 10 == 0;
                float r0 = r + 13, r1 = r + (major ? 21 : 17);
                using var tick = new SKPaint
                {
                    IsAntialias = true, StrokeWidth = t == 105 ? 2.5f : major ? 1.6f : 1,
                    Color = t == 105 ? Heat.Colors[^1] : new SKColor(0xFF, 0xFF, 0xFF, (byte)(major ? 0x50 : 0x24)),
                };
                c.DrawLine(cx + r0 * MathF.Cos(ang), cy + r0 * MathF.Sin(ang), cx + r1 * MathF.Cos(ang), cy + r1 * MathF.Sin(ang), tick);
            }

            var frac = Heat.Frac(v);
            if (frac <= 0.002f) return;
            // Pinned at both ends of the circle: a sweep gradient wraps, and the round cap at the start would pick up red.
            float[] pos = [0, .. Heat.Stops.Select(s => Heat.Frac(s) * sweep / 360f), 1];
            SKColor[] colors = [Heat.Colors[0], .. Heat.Colors, Heat.Colors[0]];
            using var sweepShader = SKShader.CreateSweepGradient(new SKPoint(cx, cy), colors, pos)
                .WithLocalMatrix(SKMatrix.CreateRotationDegrees(start, cx, cy));
            using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 24, StrokeCap = SKStrokeCap.Round, Shader = sweepShader,
                       MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12), Color = new SKColor(0xFF, 0xFF, 0xFF, 0x70) })
                c.DrawArc(rect, start, sweep * frac, false, glow);
            using (var arc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 14, StrokeCap = SKStrokeCap.Round, Shader = sweepShader })
                c.DrawArc(rect, start, sweep * frac, false, arc);

            var end = (start + sweep * frac) * MathF.PI / 180;
            var p = new SKPoint(cx + r * MathF.Cos(end), cy + r * MathF.Sin(end));
            using (var halo = new SKPaint { IsAntialias = true, Color = Heat.At(v).WithAlpha(0xC0), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9) })
                c.DrawCircle(p, 11, halo);
            using (var knob = new SKPaint { IsAntialias = true, Color = SKColors.White })
                c.DrawCircle(p, 4.5f, knob);
        };
    }
}

// CPU temperature over the last minute: a curve colored by the heat scale, with bloom and a lit head. Redrawn once
// per reading, never per frame: the bloom is a blur.
sealed class GlowGraph : SkiaView
{
    public const int Window = 60;
    const float Left = 34, Bottom = 8, Top = 10;
    readonly List<float> samples = [];

    public void Add(double v)
    {
        samples.Add((float)v);
        if (samples.Count > Window) samples.RemoveAt(0);
        version++;
    }

    int version;
    protected override string? CacheKey() => version.ToString();

    protected override Action<SKCanvas, float, float> Snapshot()
    {
        var s = samples.ToArray();
        return (c, w, h) =>
        {
            float X(int i) => Left + (w - Left - 10) * (Window - 1 - (s.Length - 1 - i)) / (Window - 1); // 10 px for the lit head
            float Y(float v) => Top + (h - Top - Bottom) * (1 - Heat.Frac(v));

            using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI"), 11);
            using var label = new SKPaint { IsAntialias = true, Color = new SKColor(0x8A, 0x93, 0xA6) };
            using var grid = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x0E), StrokeWidth = 1 };
            foreach (var v in new[] { 40, 60, 80, 100 })
            {
                c.DrawLine(Left, Y(v), w, Y(v), grid);
                c.DrawText($"{v}°", 0, Y(v) + 4, SKTextAlign.Left, font, label);
            }
            using (var limit = new SKPaint { IsAntialias = true, Color = Heat.Colors[^1].WithAlpha(0xB0), StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash([4, 5], 0) })
                c.DrawLine(Left, Y(105), w, Y(105), limit);
            if (s.Length < 2) return;

            // the heat scale mapped onto the y axis, so the curve turns amber and red as it climbs
            var span = h - Top - Bottom;
            float[] stops = [.. Heat.Stops.Select(t => Math.Clamp((Y(t) - Top) / span, 0, 1)).Reverse()];
            SKColor[] colors = [.. Heat.Colors.Reverse()];
            using var heat = SKShader.CreateLinearGradient(new SKPoint(0, Top), new SKPoint(0, h - Bottom), colors, stops, SKShaderTileMode.Clamp);

            using var path = new SKPath();
            path.MoveTo(X(0), Y(s[0]));
            for (var i = 1; i < s.Length; i++)
            {
                float x0 = X(i - 1), x1 = X(i), mid = (x0 + x1) / 2;
                path.CubicTo(mid, Y(s[i - 1]), mid, Y(s[i]), x1, Y(s[i]));
            }
            using (var area = new SKPath(path))
            {
                area.LineTo(X(s.Length - 1), h - Bottom);
                area.LineTo(X(0), h - Bottom);
                area.Close();
                using var fade = SKShader.CreateLinearGradient(new SKPoint(0, Top), new SKPoint(0, h - Bottom),
                    [Heat.At(s[^1]).WithAlpha(0x40), Heat.At(s[^1]).WithAlpha(0)], SKShaderTileMode.Clamp);
                using var fill = new SKPaint { IsAntialias = true, Shader = fade };
                c.DrawPath(area, fill);
            }
            using (var bloom = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, Shader = heat, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7) })
                c.DrawPath(path, bloom);
            using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, StrokeJoin = SKStrokeJoin.Round, Shader = heat })
                c.DrawPath(path, line);

            var head = new SKPoint(X(s.Length - 1), Y(s[^1]));
            var col = Heat.At(s[^1]);
            using (var halo = new SKPaint { IsAntialias = true, Color = col.WithAlpha(0xA0), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
                c.DrawCircle(head, 7, halo);
            using (var dot = new SKPaint { IsAntialias = true, Color = SKColors.White })
                c.DrawCircle(head, 3.5f, dot);
        };
    }
}

// A fan that spins at a visible fraction of its real speed, inside a housing that glows with its duty.
sealed class FanView : SkiaView
{
    public double Rpm { get; set; }
    public double MaxRpm { get; set; } = 5400;
    double angle;

    // A readable speed, not the real one: real rpm at any frame rate strobes (7 blades, about 51° apart). 30-180 °/s by
    // duty keeps each frame's step to a few degrees, so faster fans still look faster without the wagon-wheel effect.
    public void Advance(double seconds) =>
        angle = (angle + (30 + 150 * Math.Clamp(MaxRpm > 0 ? Rpm / MaxRpm : 0, 0, 1)) * seconds) % 360;

    // The housing and the blades are drawn once into images (per size and duty band); each frame only rotates the blades.
    (string Key, SKImage Housing, SKImage Blades)? art; // render thread only

    protected override Action<SKCanvas, float, float> Snapshot()
    {
        var a = (float)angle;
        var duty = (float)Math.Clamp(MaxRpm > 0 ? Rpm / MaxRpm : 0, 0, 1);
        return (c, w, h) =>
        {
            var scale = Math.Max(1f, c.TotalMatrix.ScaleX);
            var key = $"{w}x{h}@{scale}:{Math.Round(duty * 20)}";
            if (art?.Key != key)
            {
                art?.Housing.Dispose(); art?.Blades.Dispose();
                art = (key, Layer(w, h, scale, (lc, lw, lh) => DrawHousing(lc, lw, lh, duty)), Layer(w, h, scale, DrawBlades));
            }
            var dst = new SKRect(0, 0, w, h);
            using var smooth = new SKPaint { IsAntialias = true };
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            c.DrawImage(art.Value.Housing, dst, sampling, smooth);
            c.Save();
            c.RotateDegrees(a, w / 2, h / 2);
            c.DrawImage(art.Value.Blades, dst, sampling, smooth);
            c.Restore();
        };
    }

    static SKImage Layer(float w, float h, float scale, Action<SKCanvas, float, float> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo((int)Math.Ceiling(w * scale), (int)Math.Ceiling(h * scale), SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.Scale(scale);
        draw(surface.Canvas, w, h);
        return surface.Snapshot();
    }

    static void DrawHousing(SKCanvas c, float w, float h, float duty)
    {
        var cx = w / 2; var cy = h / 2; var r = Math.Min(w, h) / 2 - 10;
        var accent = duty > 0.85f ? Heat.Colors[3] : Heat.Colors[1];
        using (var well = new SKPaint { IsAntialias = true })
        {
            well.Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r, [new SKColor(0x05, 0x07, 0x0A), new SKColor(0x0C, 0x10, 0x16)], SKShaderTileMode.Clamp);
            c.DrawCircle(cx, cy, r, well);
        }
        using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = accent.WithAlpha((byte)(0x30 + 0x90 * duty)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
            c.DrawCircle(cx, cy, r, glow);
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f })
        {
            rim.Shader = SKShader.CreateSweepGradient(new SKPoint(cx, cy), [accent, accent.WithAlpha(0x20), accent], [0, 0.5f, 1])
                .WithLocalMatrix(SKMatrix.CreateRotationDegrees(-90 + 360 * duty, cx, cy));
            c.DrawCircle(cx, cy, r, rim);
        }
        using (var hub = new SKPaint { IsAntialias = true })
        {
            hub.Shader = SKShader.CreateRadialGradient(new SKPoint(cx - r * 0.06f, cy - r * 0.06f), r * 0.24f, [new SKColor(0x3C, 0x46, 0x56), new SKColor(0x10, 0x14, 0x1B)], SKShaderTileMode.Clamp);
            c.DrawCircle(cx, cy, r * 0.22f, hub);
        }
        using (var dot = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(0xD0) })
            c.DrawCircle(cx, cy, r * 0.05f, dot);
    }

    // Seven swept blades with a brushed-metal gradient. They start outside the hub radius, so the hub in the housing
    // image shows through the middle while the blades turn above it.
    static void DrawBlades(SKCanvas c, float w, float h)
    {
        var cx = w / 2; var cy = h / 2; var r = Math.Min(w, h) / 2 - 10;
        using var blade = new SKPaint { IsAntialias = true };
        blade.Shader = SKShader.CreateLinearGradient(new SKPoint(cx, cy - r), new SKPoint(cx, cy + r),
            [new SKColor(0x9A, 0xA6, 0xB8), new SKColor(0x3A, 0x44, 0x54)], SKShaderTileMode.Clamp);
        for (var i = 0; i < 7; i++)
        {
            c.Save();
            c.RotateDegrees(i * 360f / 7, cx, cy);
            using var p = new SKPath();
            p.MoveTo(cx + r * 0.22f, cy);
            p.CubicTo(cx + r * 0.45f, cy - r * 0.38f, cx + r * 0.80f, cy - r * 0.30f, cx + r * 0.86f, cy - r * 0.06f);
            p.CubicTo(cx + r * 0.70f, cy - r * 0.02f, cx + r * 0.42f, cy + r * 0.06f, cx + r * 0.22f, cy + r * 0.10f);
            p.Close();
            c.DrawPath(p, blade);
            c.Restore();
        }
    }
}
