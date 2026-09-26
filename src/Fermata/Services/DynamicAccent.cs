using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Fermata.Imaging;

namespace Fermata.Services;

/// <summary>The app's accent colour, taken from the cover of the playing song.</summary>
/// <remarks>Without a cover with real colour, or with the setting turned off, the accent is the theme's gold.</remarks>
public sealed class DynamicAccent
{
    private const double Lightness = 0.80, HoverLightness = 0.86, PressedLightness = 0.72;
    private static readonly TimeSpan Crossfade = TimeSpan.FromMilliseconds(450);

    private readonly AppServices services;
    private readonly Application application;
    private readonly SolidColorBrush[] accentBrushes;
    private readonly SolidColorBrush hoverBrush, pressedBrush;
    private readonly Color gold, goldHover, goldPressed;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Oklab from, to;
    private DateTime started;
    private int requests;

    public DynamicAccent(AppServices services, Application application)
    {
        this.services = services;
        this.application = application;
        accentBrushes = [Brush("AccentBrush"), Brush("FocusRingBrush"), Brush("DropTargetBrush")];
        hoverBrush = Brush("AccentHoverBrush");
        pressedBrush = Brush("AccentPressedBrush");
        gold = accentBrushes[0].Color;
        goldHover = hoverBrush.Color;
        goldPressed = pressedBrush.Color;
        from = to = ToOklab(gold);
        timer.Tick += (_, _) => Step();
        services.Player.TrackChanged += Update;
    }

    private SolidColorBrush Brush(string key) =>
        application.TryFindResource(key, out object? value) && value is SolidColorBrush brush
            ? brush
            : throw new InvalidOperationException($"The theme has no {key}.");

    /// <summary>Recomputes the accent.</summary>
    public void Update()
    {
        int request = ++requests;
        var track = services.Player.CurrentTrack;
        if (!services.Settings.AccentFromArt || track is null || services.Library.Snapshot.ArtOf(track) is not { } art)
        {
            MoveTo(null);
            return;
        }
        services.Art.RequestPalette(art, palette =>
        {
            if (request == requests)
                MoveTo(palette?.Accent);
        });
    }

    private void MoveTo(Oklab? accent)
    {
        var target = accent is { } color ? Oklab.FromLch(Lightness, Math.Clamp(color.Chroma * 1.1, 0.11, 0.17), color.Hue) : ToOklab(gold);
        from = Current();
        to = target;
        started = DateTime.UtcNow;
        timer.Start();
    }

    private Oklab Current()
    {
        double t = Math.Clamp((DateTime.UtcNow - started) / Crossfade, 0, 1);
        return Oklab.Mix(from, to, t * t * (3 - 2 * t));
    }

    private void Step()
    {
        var color = Current();
        bool done = DateTime.UtcNow - started >= Crossfade;
        bool isGold = to == ToOklab(gold);
        foreach (var brush in accentBrushes)
            brush.Color = done && isGold ? gold : ToColor(color);
        hoverBrush.Color = done && isGold ? goldHover : ToColor(color.WithLightness(HoverLightness));
        pressedBrush.Color = done && isGold ? goldPressed : ToColor(color.WithLightness(PressedLightness));
        if (!done)
            return;
        timer.Stop();
        // Replacing resources makes every user re-resolve them, so it happens once per change.
        var resources = application.Resources;
        resources["SystemAccentColor"] = accentBrushes[0].Color;
        resources["SystemAccentColorLight1"] = hoverBrush.Color;
        resources["SystemAccentColorDark1"] = pressedBrush.Color;
    }

    private static Oklab ToOklab(Color color) => Oklab.FromSrgb(color.R, color.G, color.B);

    private static Color ToColor(Oklab color)
    {
        var (r, g, b) = color.ToSrgb();
        return Color.FromRgb(r, g, b);
    }
}
