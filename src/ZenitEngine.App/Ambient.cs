using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

// The window's living background, drawn by a GPU shader: light falling from the zenith at the top edge, a slow
// anamorphic streak, a faint scan band and fine grain over graphite. Its color follows the machine:
// cool blue when healthy, amber as the CPU heats up (Warm, 0 to 1).
sealed class Ambient : Control
{
    const string Sksl = """
        uniform float2 res;
        uniform float t;
        uniform float warm;

        half4 main(float2 fc) {
            float2 uv = fc / res;
            float aspect = res.x / res.y;
            float2 p = float2((uv.x - 0.5) * aspect, uv.y);
            float3 base = float3(0.024, 0.031, 0.045);
            float3 tint = mix(float3(0.34, 0.56, 0.95), float3(1.0, 0.47, 0.22), warm);
            float breathe = 0.86 + 0.14 * sin(t * 0.55);

            float zenith = exp(-length(p * float2(0.85, 2.3)) * 2.3) * 0.38 * breathe;
            float streak = exp(-abs(p.y) * 70.0) * exp(-abs(p.x) * 1.1) * 0.24 * breathe;
            float band = exp(-pow((uv.y - fract(t * 0.035)) * 28.0, 2.0)) * 0.010;
            float n = fract(sin(dot(fc + t * 37.0, float2(12.9898, 78.233))) * 43758.5453);

            float3 col = base + tint * (zenith + streak) + float3(band) + (n - 0.5) * 0.012;
            return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
        }
        """;

    static readonly SKRuntimeEffect Shader = SKRuntimeEffect.CreateShader(Sksl, out var errors)
        ?? throw new InvalidOperationException("ambient shader: " + errors);

    public double Time { get; set; }
    public double Warm { get; set; }

    public override void Render(DrawingContext context) =>
        context.Custom(new Draw(new Rect(Bounds.Size), (float)Time, (float)Warm));

    sealed class Draw(Rect bounds, float time, float warm) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var uniforms = new SKRuntimeEffectUniforms(Shader)
            {
                ["res"] = new[] { (float)bounds.Width, (float)bounds.Height },
                ["t"] = time,
                ["warm"] = warm,
            };
            using var shader = Shader.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };
            lease.SkCanvas.DrawRect(0, 0, (float)bounds.Width, (float)bounds.Height, paint);
        }
    }
}