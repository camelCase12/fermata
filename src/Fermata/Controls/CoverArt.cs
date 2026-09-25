using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Fermata.Library;
using Fermata.Services;

namespace Fermata.Controls;

/// <summary>
/// Cover art that loads in the background at the size it is shown. Until the image arrives (or when
/// there is none) it draws a gradient placeholder whose colours derive from the art's identity, so
/// placeholders are stable and distinguishable rather than a wall of identical grey squares.
/// </summary>
/// <remarks>
/// Rows in virtualized lists are recycled: when <see cref="Source"/> changes, the previous image is
/// released at once, and a load that completes for an earlier source is discarded.
/// </remarks>
public sealed class CoverArt : Control
{
    public static readonly StyledProperty<ArtSource?> SourceProperty =
        AvaloniaProperty.Register<CoverArt, ArtSource?>(nameof(Source));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<CoverArt, double>(nameof(Radius), 6);

    /// <summary>Draws the image as a circle (artists).</summary>
    public static readonly StyledProperty<bool> IsRoundProperty =
        AvaloniaProperty.Register<CoverArt, bool>(nameof(IsRound));

    /// <summary>Text used to vary the placeholder when there is no art (an album or artist name).</summary>
    public static readonly StyledProperty<string?> PlaceholderKeyProperty =
        AvaloniaProperty.Register<CoverArt, string?>(nameof(PlaceholderKey));

    static CoverArt()
    {
        AffectsRender<CoverArt>(RadiusProperty, IsRoundProperty, PlaceholderKeyProperty);
    }

    private ArtLease? lease;
    private int requestedSize;
    private int generation;
    private bool attached;

    public ArtSource? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public bool IsRound
    {
        get => GetValue(IsRoundProperty);
        set => SetValue(IsRoundProperty, value);
    }

    public string? PlaceholderKey
    {
        get => GetValue(PlaceholderKeyProperty);
        set => SetValue(PlaceholderKeyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            ReleaseImage();
            Load();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Load();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        attached = false;
        ReleaseImage();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // A much larger size (a resized now-playing view) deserves a sharper decode.
        if (lease is not null && PixelSize() > requestedSize)
        {
            ReleaseImage();
            Load();
        }
    }

    private int PixelSize()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return ArtCache.BucketFor(Math.Max(Bounds.Width, Bounds.Height) * scale);
    }

    private void Load()
    {
        var source = Source;
        if (source is null || lease is not null || !attached || App.Services is not { } services)
            return;
        if (Bounds.Width <= 0)
        {
            // Not measured yet; the first arrange will call OnSizeChanged → Load via LayoutUpdated.
            LayoutUpdated += LoadAfterLayout;
            return;
        }
        int size = PixelSize();
        requestedSize = size;
        int current = ++generation;
        var cached = services.Art.TryGet(source, size);
        if (cached is not null)
        {
            lease = cached;
            InvalidateVisual();
            return;
        }
        services.Art.Request(source, size, result =>
        {
            if (current != generation || !attached || Source != source)
            {
                result?.Dispose(); // this control has moved on
                return;
            }
            lease = result;
            InvalidateVisual();
        });
    }

    private void LoadAfterLayout(object? sender, EventArgs e)
    {
        LayoutUpdated -= LoadAfterLayout;
        Load();
    }

    private void ReleaseImage()
    {
        generation++;
        if (lease is null)
            return;
        lease.Dispose();
        lease = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        double radius = IsRound ? Math.Min(bounds.Width, bounds.Height) / 2 : Radius;
        using (context.PushClip(new RoundedRect(bounds, radius)))
        {
            if (lease?.Bitmap is { } bitmap)
                DrawCover(context, bitmap, bounds);
            else
                DrawPlaceholder(context, bounds);
        }
    }

    /// <summary>Scales to fill the square and centres (like UniformToFill).</summary>
    private static void DrawCover(DrawingContext context, Bitmap bitmap, Rect bounds)
    {
        var size = bitmap.Size;
        double scale = Math.Max(bounds.Width / size.Width, bounds.Height / size.Height);
        var source = new Rect(
            (size.Width - bounds.Width / scale) / 2, (size.Height - bounds.Height / scale) / 2,
            bounds.Width / scale, bounds.Height / scale);
        context.DrawImage(bitmap, source, bounds);
    }

    private void DrawPlaceholder(DrawingContext context, Rect bounds)
    {
        string key = PlaceholderKey ?? Source?.Path ?? "";
        uint hash = 2166136261;
        foreach (char c in key)
            hash = (hash ^ c) * 16777619;
        context.FillRectangle(PlaceholderBrush((int)(hash % 360)), bounds);
        // A music note, sized to the tile.
        if (Application.Current?.TryGetResource("Icon.Song", null, out var resource) == true && resource is Geometry note)
        {
            double size = Math.Min(bounds.Width, bounds.Height) * 0.38;
            var noteBounds = note.Bounds;
            double scale = size / Math.Max(noteBounds.Width, noteBounds.Height);
            var transform = Matrix.CreateTranslation(-noteBounds.Center.X, -noteBounds.Center.Y)
                * Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation(bounds.Center.X, bounds.Center.Y);
            using (context.PushTransform(transform))
                context.DrawGeometry(PlaceholderInk, null, note);
        }
    }

    private static readonly IBrush PlaceholderInk = new SolidColorBrush(Colors.White, 0.35);
    private static readonly Dictionary<int, IBrush> PlaceholderBrushes = [];

    /// <summary>One immutable gradient per hue, shared by every placeholder of that hue.</summary>
    private static IBrush PlaceholderBrush(int hue)
    {
        if (!PlaceholderBrushes.TryGetValue(hue, out var brush))
        {
            PlaceholderBrushes[hue] = brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(HsvColor.ToRgb(hue, 0.35, 0.30), 0),
                    new GradientStop(HsvColor.ToRgb((hue + 35) % 360, 0.45, 0.16), 1),
                },
            }.ToImmutable();
        }
        return brush;
    }
}
