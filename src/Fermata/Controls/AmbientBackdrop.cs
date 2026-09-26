using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Fermata.Library;
using Fermata.Imaging;
using Fermata.Services;
using Fermata.Storage;
using SkiaSharp;

namespace Fermata.Controls;

/// <summary>A field of cover colours drawn behind a page.</summary>
public sealed class AmbientBackdrop : Control
{
    public static readonly StyledProperty<IReadOnlyList<ArtSource>?> CoversProperty =
        AvaloniaProperty.Register<AmbientBackdrop, IReadOnlyList<ArtSource>?>(nameof(Covers));

    /// <summary>Where the fade into the page background begins and ends, as fractions of the height.</summary>
    public static readonly StyledProperty<double> FadeStartProperty =
        AvaloniaProperty.Register<AmbientBackdrop, double>(nameof(FadeStart));

    public static readonly StyledProperty<double> FadeEndProperty =
        AvaloniaProperty.Register<AmbientBackdrop, double>(nameof(FadeEnd), 1);

    /// <summary>
    /// How far the fade goes towards the background. At 1, only the background is left below the end.
    /// </summary>
    public static readonly StyledProperty<double> FadeAmountProperty =
        AvaloniaProperty.Register<AmbientBackdrop, double>(nameof(FadeAmount), 1);

    /// <summary>Whether the cover's top lines up with the backdrop's top instead of being centred.</summary>
    public static readonly StyledProperty<bool> AlignTopProperty =
        AvaloniaProperty.Register<AmbientBackdrop, bool>(nameof(AlignTop));

    private const int Cells = ArtCache.ColorGridSize;
    private static readonly TimeSpan CrossfadeTime = TimeSpan.FromMilliseconds(650);

    // The field being faded out and the field being faded in. Null means the page background alone.
    private Field? from;
    private Field? to;
    private double progress = 1;
    private TimeSpan? crossfadeStart;
    private int requests;

    static AmbientBackdrop()
    {
        AffectsRender<AmbientBackdrop>(FadeStartProperty, FadeEndProperty, FadeAmountProperty, AlignTopProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<AmbientBackdrop>(false);
    }

    public IReadOnlyList<ArtSource>? Covers
    {
        get => GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    public double FadeStart
    {
        get => GetValue(FadeStartProperty);
        set => SetValue(FadeStartProperty, value);
    }

    public double FadeEnd
    {
        get => GetValue(FadeEndProperty);
        set => SetValue(FadeEndProperty, value);
    }

    public double FadeAmount
    {
        get => GetValue(FadeAmountProperty);
        set => SetValue(FadeAmountProperty, value);
    }

    public bool AlignTop
    {
        get => GetValue(AlignTopProperty);
        set => SetValue(AlignTopProperty, value);
    }

    /// <summary>Why the shaders could not be compiled, or null.</summary>
    internal static string? ShaderErrors => SoftShader.Errors ?? FlowShader.Errors;

    /// <summary>Whether the generated style is drawn even when frames are drawn without a GPU.</summary>
    internal static bool PatternWithoutGpu { get; set; }

    /// <summary>Whether a frame has been drawn without a GPU.</summary>
    /// <remarks>Once it is set, backdrops load the soft style unless <see cref="PatternWithoutGpu"/> is set.</remarks>
    private static volatile bool drawnWithoutGpu;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoversProperty)
            Load();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (App.Services is { } services)
            services.AppearanceChanged += Load;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (App.Services is { } services)
            services.AppearanceChanged -= Load;
    }

    /// <summary>Requests what the field needs for the current covers.</summary>
    private void Load()
    {
        int request = ++requests;
        var covers = Covers;
        ArtSource[] sources = covers is { Count: >= 4 } ? [covers[0], covers[1], covers[2], covers[3]]
            : covers is { Count: > 0 } ? [covers[0]] : [];
        if (sources.Length == 0 || App.Services is not { } services)
        {
            CrossfadeTo(null);
            return;
        }
        int pending = sources.Length;
        if (services.Settings.GeneratedBackdrop && (!drawnWithoutGpu || PatternWithoutGpu))
        {
            var palettes = new CoverPalette?[sources.Length];
            for (int i = 0; i < sources.Length; i++)
            {
                int index = i;
                services.Art.RequestPalette(sources[i], palette =>
                {
                    palettes[index] = palette;
                    if (--pending == 0 && request == requests)
                        CrossfadeTo(FlowField.From(palettes));
                });
            }
            return;
        }
        var grids = new byte[]?[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            int index = i;
            services.Art.RequestColorGrid(sources[i], grid =>
            {
                grids[index] = grid;
                if (--pending == 0 && request == requests)
                    CrossfadeTo(grids.Length == 1 ? GridField.From(grids[0]) : GridField.From(Quadrants(grids)));
            });
        }
    }

    /// <summary>Combines four covers' colour grids into one, a quadrant each.</summary>
    private byte[]? Quadrants(byte[]?[] grids)
    {
        if (grids.All(g => g is null))
            return null;
        var background = Fill(Background());
        var result = new byte[Cells * Cells * 4];
        const int half = Cells / 2;
        for (int q = 0; q < 4; q++)
        {
            var grid = grids[q] ?? background;
            for (int y = 0; y < half; y++)
            {
                for (int x = 0; x < half; x++)
                {
                    int o = ((q / 2 * half + y) * Cells + q % 2 * half + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int sum = 0;
                        for (int dy = 0; dy < 2; dy++)
                        {
                            for (int dx = 0; dx < 2; dx++)
                                sum += grid[((2 * y + dy) * Cells + 2 * x + dx) * 4 + c];
                        }
                        result[o + c] = (byte)((sum + 2) / 4);
                    }
                }
            }
        }
        return result;
    }

    private void CrossfadeTo(Field? target)
    {
        if (Equals(target, to) && progress >= 1)
            return;
        // An interrupted crossfade continues from whichever field was more visible.
        from = progress >= 0.5 ? to : from;
        to = target;
        progress = 0;
        crossfadeStart = null;
        if (Equals(from, to) || TopLevel.GetTopLevel(this) is not { } top)
        {
            progress = 1;
            InvalidateVisual();
            return;
        }
        top.RequestAnimationFrame(Step);
    }

    private void Step(TimeSpan time)
    {
        crossfadeStart ??= time;
        progress = Math.Min(1, (time - crossfadeStart.Value) / CrossfadeTime);
        InvalidateVisual();
        if (progress < 1)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
    }

    private Color Background() =>
        this.TryFindResource("BackgroundColor", ActualThemeVariant, out object? value) && value is Color color ? color : Colors.Black;

    private static byte[] Fill(Color color)
    {
        var grid = new byte[Cells * Cells * 4];
        for (int i = 0; i < grid.Length; i += 4)
        {
            grid[i] = color.R;
            grid[i + 1] = color.G;
            grid[i + 2] = color.B;
            grid[i + 3] = 255;
        }
        return grid;
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || (from is null && to is null))
            return;
        var size = Bounds.Size;
        // The field covers the whole area with the cover's square, like a cover image cropped to fill it.
        double side = Math.Max(size.Width, size.Height);
        var origin = new Point((size.Width - side) / 2, AlignTop ? 0 : (size.Height - side) / 2);
        // Nothing is drawn where the fade reaches the plain background.
        var drawn = FadeAmount >= 1 ? new Rect(0, 0, size.Width, size.Height * Math.Clamp(FadeEnd, 0, 1)) : new Rect(size);
        var layout = new Layout(drawn, origin, side, size.Height, Background(), FadeStart, FadeEnd, FadeAmount);
        if (progress >= 1)
        {
            if (to is not null)
                context.Custom(new FieldOperation(this, to, layout, 1));
            return;
        }
        double t = progress * progress * (3 - 2 * progress);
        if (from is not null)
            context.Custom(new FieldOperation(this, from, layout, to is null ? 1 - t : 1));
        if (to is not null)
            context.Custom(new FieldOperation(this, to, layout, t));
    }

    /// <summary>Where and how a field is drawn.</summary>
    private readonly record struct Layout(Rect Drawn, Point Origin, double Side, double Height, Color Background,
        double FadeStart, double FadeEnd, double FadeAmount)
    {
        public float[] BaseColor => [Background.R / 255f, Background.G / 255f, Background.B / 255f];

        public float[] Fade => [(float)FadeStart, (float)FadeEnd, (float)FadeAmount];
    }

    /// <summary>What a backdrop shows.</summary>
    private abstract class Field
    {
        /// <summary>Makes the shader that draws this field.</summary>
        /// <param name="layout">Where and how the field is drawn.</param>
        /// <param name="owned">Receives objects that must live as long as the shader.</param>
        public abstract SKShader? CreateShader(Layout layout, List<IDisposable> owned);
    }

    /// <summary>The soft style, drawn from a grid of average colours.</summary>
    private sealed class GridField(byte[] grid) : Field
    {
        public static GridField? From(byte[]? grid) => grid is null ? null : new GridField(grid);

        public override SKShader? CreateShader(Layout layout, List<IDisposable> owned)
        {
            if (SoftShader.Effect is not { } effect)
                return null;
            var image = SKImage.FromPixelCopy(new SKImageInfo(Cells, Cells, SKColorType.Rgba8888, SKAlphaType.Opaque), grid);
            owned.Add(image);
            var colors = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, new SKSamplingOptions(SKFilterMode.Linear));
            owned.Add(colors);
            // SKRuntimeShaderBuilder is not used because disposing it frees the shared effect.
            using var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["origin"] = new[] { (float)layout.Origin.X, (float)layout.Origin.Y },
                ["cell"] = (float)(layout.Side / Cells),
                ["height"] = (float)layout.Height,
                ["fade"] = layout.Fade,
                ["base"] = layout.BaseColor,
            };
            using var children = new SKRuntimeEffectChildren(effect) { ["colors"] = colors };
            return effect.ToShader(uniforms, children);
        }
    }

    /// <summary>The generated style, drawn from a cover's hue families and structure.</summary>
    private sealed class FlowField(CoverPalette palette) : Field
    {
        private const int MaxBlobs = CoverPalette.MaxBlobs;
        private const double NeutralPenalty = 0.75;

        // The uniforms that depend only on the palette.
        private readonly float[] colors = new float[MaxBlobs * 4];
        private readonly float[] shapes = new float[MaxBlobs * 4];
        private readonly float[] shears = new float[MaxBlobs * 4];
        private readonly float[] flow = new float[4];
        private readonly float[] noise = new float[4];

        public static FlowField? From(CoverPalette?[] palettes)
        {
            if (palettes.All(p => p is null))
                return null;
            // A cover that cannot be read takes the palette of one that can.
            var known = palettes.Select(p => p ?? palettes.First(q => q is not null)!).ToList();
            var field = new FlowField(CoverPalette.Mosaic(known));
            field.Prepare();
            return field;
        }

        private void Prepare()
        {
            for (int i = 0; i < MaxBlobs; i++)
            {
                if (i >= palette.Blobs.Count)
                {
                    // An unused slot has a weight so low that it never shows.
                    colors[i * 4 + 3] = -1e4f;
                    shapes[i * 4 + 2] = shapes[i * 4 + 3] = 1;
                    continue;
                }
                var blob = palette.Blobs[i];
                // Each family spreads a little further than it does in the cover.
                double sxx = 1.3 * blob.Sxx + 0.01, sxy = 1.3 * blob.Sxy, syy = 1.3 * blob.Syy + 0.01;
                double determinant = sxx * syy - sxy * sxy;
                colors[i * 4] = (float)blob.Color.L;
                colors[i * 4 + 1] = (float)blob.Color.A;
                colors[i * 4 + 2] = (float)blob.Color.B;
                // The score is the logarithm of the family's density as a Gaussian mixture component, with its
                // area raised to the power 0.75, less a penalty for neutral families.
                colors[i * 4 + 3] = (float)(0.75 * Math.Log(Math.Max(blob.Weight, 1e-4)) - 0.5 * Math.Log(determinant)
                    - (blob.IsNeutral ? NeutralPenalty : 0));
                shapes[i * 4] = (float)blob.X;
                shapes[i * 4 + 1] = (float)blob.Y;
                shapes[i * 4 + 2] = (float)(syy / determinant);
                shapes[i * 4 + 3] = (float)(sxx / determinant);
                shears[i * 4] = (float)(-sxy / determinant);
            }
            // Busier covers give a finer pattern, and more coherent structure stretches it along its direction.
            flow[0] = (float)Math.Cos(palette.Orientation);
            flow[1] = (float)Math.Sin(palette.Orientation);
            flow[2] = (float)(1 + 2.5 * palette.Coherence);
            flow[3] = (float)(0.2 + 0.25 * (1 - palette.Coherence));
            noise[0] = (float)(1.5 + 2.5 * palette.Detail);
            noise[1] = palette.Seed % 1000 / 10f;
            noise[2] = palette.Seed / 1000 % 1000 / 10f;
            noise[3] = (float)(0.35 + 0.35 * palette.Detail);
        }

        public override SKShader? CreateShader(Layout layout, List<IDisposable> owned)
        {
            if (FlowShader.Effect is not { } effect)
                return null;
            using var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["origin"] = new[] { (float)layout.Origin.X, (float)layout.Origin.Y },
                ["side"] = (float)layout.Side,
                ["height"] = (float)layout.Height,
                ["fade"] = layout.Fade,
                ["base"] = layout.BaseColor,
                ["blobColor"] = colors,
                ["blobShape"] = shapes,
                ["blobShear"] = shears,
                ["flow"] = flow,
                ["noise"] = noise,
            };
            return effect.ToShader(uniforms);
        }
    }

    /// <summary>A recorded drawing of a field.</summary>
    private sealed class FieldOperation(AmbientBackdrop owner, Field field, Layout layout, double opacity) : ICustomDrawOperation
    {
        private readonly AmbientBackdrop owner = owner;
        private readonly Field field = field;
        private readonly Layout layout = layout;
        private readonly double opacity = opacity;
        private readonly List<IDisposable> owned = [];
        private SKShader? shader;
        private readonly SKPaint paint = new();

        public Rect Bounds => layout.Drawn;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) =>
            other is FieldOperation o && o.field == field && o.layout == layout && o.opacity == opacity;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia)
                return;
            using var lease = skia.Lease();
            if (lease.GrContext is null && field is FlowField && !PatternWithoutGpu)
            {
                if (!drawnWithoutGpu)
                {
                    drawnWithoutGpu = true;
                    Dispatcher.UIThread.Post(owner.Load);
                }
                return;
            }
            if (shader is null)
            {
                shader = field.CreateShader(layout, owned);
                if (shader is null)
                    return;
                paint.Shader = shader;
            }
            paint.Color = SKColors.White.WithAlpha((byte)Math.Round(255 * opacity * lease.CurrentOpacity));
            lease.SkCanvas.DrawRect(SKRect.Create((float)layout.Drawn.Width, (float)layout.Drawn.Height), paint);
        }

        public void Dispose()
        {
            paint.Dispose();
            shader?.Dispose();
            foreach (var item in owned)
                item.Dispose();
        }
    }

    /// <summary>The SkSL both shaders use to turn a linear colour into the final pixel.</summary>
    private const string Finish = """
        uniform float height;
        uniform float3 fade;
        uniform float3 base;

        const float ceiling = 0.045;
        const float3 luma = float3(0.2126, 0.7152, 0.0722);

        float hash(float2 p) {
            float3 q = fract(float3(p.x, p.y, p.x) * 0.1031);
            q += dot(q, q.yzx + 33.33);
            return fract((q.x + q.y) * q.z);
        }

        float3 tone(float3 lin, float vividness) {
            float luminance = dot(lin, luma);
            lin = max(luminance + (lin - luminance) * vividness, 0.0);
            luminance = dot(lin, luma);
            return lin * ceiling * (1.0 - exp(-luminance / ceiling)) / max(luminance, 1e-6);
        }

        half4 finish(float3 lin, float2 p) {
            float3 c = pow(max(lin, 0.0), float3(1.0 / 2.2));
            c = mix(c, base, fade.z * smoothstep(fade.x, fade.y, p.y / height));
            c += (hash(p) + hash(p + 0.618) - 1.0) / 255.0;
            return half4(half3(c), 1.0);
        }
        """;

    private static SKRuntimeEffect? Compile(string source, out string? failure)
    {
        var effect = SKRuntimeEffect.CreateShader(source, out string? errors);
        failure = effect is null ? errors : null;
        return effect;
    }

    private static class SoftShader
    {
        public static readonly SKRuntimeEffect? Effect = Compile(Finish + Source, out Errors);
        public static readonly string? Errors;

        // Colours are interpolated with a cubic B-spline, computed from four bilinear lookups.
        private const string Source = """
            uniform shader colors;
            uniform float2 origin;
            uniform float cell;

            float3 at(float2 p) {
                return float3(colors.eval(p).rgb);
            }

            float3 bspline(float2 u) {
                float2 t = u - 0.5;
                float2 i = floor(t);
                float2 f = t - i;
                float2 f2 = f * f;
                float2 f3 = f2 * f;
                float2 w0 = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
                float2 w1 = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
                float2 w2 = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
                float2 w3 = f3 / 6.0;
                float2 g0 = w0 + w1;
                float2 g1 = w2 + w3;
                float2 p0 = i - 0.5 + w1 / g0;
                float2 p1 = i + 1.5 + w3 / g1;
                return g0.y * (g0.x * at(float2(p0.x, p0.y)) + g1.x * at(float2(p1.x, p0.y)))
                     + g1.y * (g0.x * at(float2(p0.x, p1.y)) + g1.x * at(float2(p1.x, p1.y)));
            }

            half4 main(float2 p) {
                float3 lin = pow(bspline((p - origin) / cell), float3(2.2));
                return finish(tone(lin, 1.35), p);
            }
            """;
    }

    private static class FlowShader
    {
        public static readonly SKRuntimeEffect? Effect = Compile(Finish + Source, out Errors);
        public static readonly string? Errors;

        // Positions are in the cover's square, from 0 to 1. Each position is displaced by domain-warped
        // value noise, in a frame turned to the cover's structure direction. Each family then scores by its
        // area, a Gaussian of its shape and a noise field of its own, and the sharpened scores mix the
        // families' colours in OKLab. A further noise value varies the lightness.
        private const string Source = """
            uniform float2 origin;
            uniform float side;
            uniform float4 blobColor[6];
            uniform float4 blobShape[6];
            uniform float4 blobShear[6];
            uniform float4 flow;
            uniform float4 noise;

            const float sharpness = 2.5;
            const float patches = 3.5;

            float vnoise(float2 p) {
                float2 i = floor(p);
                float2 f = p - i;
                float2 u = f * f * (3.0 - 2.0 * f);
                return mix(mix(hash(i), hash(i + float2(1.0, 0.0)), u.x),
                           mix(hash(i + float2(0.0, 1.0)), hash(i + float2(1.0, 1.0)), u.x), u.y);
            }

            float fbm(float2 p) {
                float sum = 0.0;
                float amplitude = 0.5;
                for (int i = 0; i < 4; i++) {
                    sum += amplitude * vnoise(p);
                    p = float2(1.6 * p.x - 1.2 * p.y, 1.2 * p.x + 1.6 * p.y) + 17.0;
                    amplitude *= 0.5;
                }
                return sum / 0.9375;
            }

            float3 oklabToLinear(float3 c) {
                float l = c.x + 0.3963377774 * c.y + 0.2158037573 * c.z;
                float m = c.x - 0.1055613458 * c.y - 0.0638541728 * c.z;
                float s = c.x - 0.0894841775 * c.y - 1.2914855480 * c.z;
                l = l * l * l;
                m = m * m * m;
                s = s * s * s;
                return float3(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                             -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                             -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
            }

            half4 main(float2 p) {
                float2 uv = (p - origin) / side;
                float2 along = flow.xy;
                float2 across = float2(-along.y, along.x);
                float2 d = uv - 0.5;
                float2 q = float2(dot(d, along) / flow.z, dot(d, across)) * noise.x + noise.yz;
                float2 w1 = float2(fbm(q), fbm(q + float2(5.2, 1.3)));
                float2 w2 = float2(fbm(q + 4.0 * w1 + float2(1.7, 9.2)), fbm(q + 4.0 * w1 + float2(8.3, 2.8)));
                float2 offset = (w2 - 0.5) * flow.w;
                uv += offset.x * flow.z * along + offset.y * across;

                float3 n = float3(fbm(q * 0.7 + float2(11.0, 3.0)), fbm(q * 0.7 + float2(23.0, 7.0)), fbm(q * 0.7 + float2(37.0, 5.0))) - 0.5;
                float top = -1e9;
                for (int i = 0; i < 6; i++) {
                    float2 v = uv - blobShape[i].xy;
                    float a = float(i) * 2.39996;
                    float3 direction = float3(0.8 * cos(a), 0.8 * sin(a), mod(float(i), 2.0) < 0.5 ? 0.6 : -0.6);
                    float s = blobColor[i].w - 0.5 * (v.x * v.x * blobShape[i].z + 2.0 * v.x * v.y * blobShear[i].x + v.y * v.y * blobShape[i].w)
                            + patches * dot(n, direction);
                    top = max(top, s);
                }
                float3 lab = float3(0.0);
                float total = 0.0;
                for (int i = 0; i < 6; i++) {
                    float2 v = uv - blobShape[i].xy;
                    float a = float(i) * 2.39996;
                    float3 direction = float3(0.8 * cos(a), 0.8 * sin(a), mod(float(i), 2.0) < 0.5 ? 0.6 : -0.6);
                    float s = blobColor[i].w - 0.5 * (v.x * v.x * blobShape[i].z + 2.0 * v.x * v.y * blobShear[i].x + v.y * v.y * blobShape[i].w)
                            + patches * dot(n, direction);
                    float w = exp(sharpness * (s - top));
                    lab += w * blobColor[i].xyz;
                    total += w;
                }
                lab /= total;

                float3 lin = tone(oklabToLinear(lab), 1.0);
                float grain = fbm(q * 1.7 + 3.0 * w2);
                lin *= 1.0 - noise.w * 0.5 + noise.w * grain;
                return finish(lin, p);
            }
            """;
    }
}
