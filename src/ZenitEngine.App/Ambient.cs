using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

// The window's living background, the icon's scene drawn by a GPU shader: a zenith glow at the top with a faint beam
// falling from it, a planet's horizon along the bottom whose rim light runs amber to blue, sparse twinkling stars and
// fine grain over graphite. Its warmth follows the machine: cooler when healthy, amber as the CPU heats up (Warm, 0 to 1).
sealed class Ambient : SkiaView
{
    const string Sksl = """
        uniform float2 res;
        uniform float t;
        uniform float warm;

        float hash(float2 p) { return fract(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

        half4 main(float2 fc) {
            float2 uv = fc / res;
            float aspect = res.x / res.y;
            float2 p = float2((uv.x - 0.5) * aspect, uv.y);
            float3 col = mix(float3(0.030, 0.036, 0.050), float3(0.016, 0.019, 0.027), uv.y);
            float3 cool = float3(0.36, 0.58, 0.98);
            float3 hot = float3(1.0, 0.46, 0.20);
            float3 tint = mix(cool, hot, warm);
            float breathe = 0.88 + 0.12 * sin(t * 0.5);

            // zenith glow and the beam falling from it
            col += tint * exp(-length(p * float2(0.9, 2.4)) * 2.4) * 0.30 * breathe;
            col += mix(cool, tint, 0.5) * exp(-abs(p.x) * 160.0) * exp(-uv.y * 2.6) * 0.16 * breathe;

            // planet horizon: a huge circle below the window, its rim lit amber on the left to blue on the right
            float2 c = float2(0.0, 1.0 + 2.6);
            float d = length(float2(p.x, uv.y) - c) - 2.72;
            float3 rimColor = mix(mix(hot, cool, smoothstep(-0.6, 0.6, p.x)), hot, warm * 0.6);
            float rim = exp(-abs(d) * 90.0) * 0.55 + exp(-abs(d) * 14.0) * 0.10;
            col = d > 0.0 ? mix(col, float3(0.010, 0.012, 0.017), 0.85) : col;
            col += rimColor * rim * smoothstep(1.2, 0.2, abs(p.x));

            // sparse stars above the horizon, each twinkling at its own pace
            float2 cell = floor(fc / 46.0);
            float h = hash(cell);
            if (h > 0.86 && d < -0.02) {
                float2 star = (cell + float2(hash(cell + 1.7), hash(cell + 4.3))) * 46.0;
                float tw = 0.55 + 0.45 * sin(t * (0.6 + h * 1.4) + h * 40.0);
                col += float3(0.75, 0.85, 1.0) * exp(-length(fc - star) * 1.6) * 0.55 * tw * (1.0 - uv.y * 0.5);
            }

            float n = fract(sin(dot(fc + t * 37.0, float2(12.9898, 78.233))) * 43758.5453);
            col += (n - 0.5) * 0.012;
            return half4(half3(clamp(col, 0.0, 1.0)), 1.0);
        }
        """;

    static readonly SKRuntimeEffect Shader = SKRuntimeEffect.CreateShader(Sksl, out var errors)
        ?? throw new InvalidOperationException("ambient shader: " + errors);

    public double Time { get; set; }
    public double Warm { get; set; }

    // Cached between its own updates: anything repainting on top (a fan turning) copies this image instead of rerunning
    // the shader over the window.
    protected override string? CacheKey() => $"{Time:0.0}:{Warm:0.000}";

    protected override Action<SKCanvas, float, float> Snapshot()
    {
        var (time, warm) = ((float)Time, (float)Warm);
        return (c, w, h) =>
        {
            var uniforms = new SKRuntimeEffectUniforms(Shader) { ["res"] = new[] { w, h }, ["t"] = time, ["warm"] = warm };
            using var shader = Shader.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };
            c.DrawRect(0, 0, w, h, paint);
        };
    }
}
