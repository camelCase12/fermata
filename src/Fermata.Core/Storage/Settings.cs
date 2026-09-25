using Fermata.Playback;

namespace Fermata.Storage;

/// <summary>User preferences, saved as <c>~/.config/fermata/settings.json</c>.</summary>
public sealed class Settings
{
    public List<string> MusicFolders { get; set; } = [];

    /// <summary>Linear volume 0–1; the engine maps it to a perceptual curve.</summary>
    public double Volume { get; set; } = 0.8;

    public bool Muted { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public ReplayGainMode ReplayGain { get; set; } = ReplayGainMode.Track;
    public bool Gapless { get; set; } = true;

    /// <summary>When the queue runs out, keep playing similar music from the library.</summary>
    public bool Autoplay { get; set; } = true;

    /// <summary>mpv audio device name, or "auto".</summary>
    public string AudioDevice { get; set; } = "auto";

    public bool WatchFolders { get; set; } = true;

    /// <summary>Restore the queue and position (paused) at startup.</summary>
    public bool ResumeSession { get; set; } = true;

    /// <summary>Take the accent colour from the playing song's cover instead of the theme's gold.</summary>
    public bool AccentFromArt { get; set; } = true;

    /// <summary>Draw page backdrops as patterns generated from the cover art instead of a soft blur of it.</summary>
    public bool GeneratedBackdrop { get; set; } = true;

    public bool SidebarCollapsed { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public bool WindowMaximized { get; set; }

    /// <summary>Remembered sort choices by view, e.g. "songs" → "artist".</summary>
    public Dictionary<string, string> SortOrders { get; set; } = [];

    public Settings Clone() => new()
    {
        MusicFolders = [.. MusicFolders],
        Volume = Volume,
        Muted = Muted,
        Shuffle = Shuffle,
        Repeat = Repeat,
        ReplayGain = ReplayGain,
        Gapless = Gapless,
        Autoplay = Autoplay,
        AudioDevice = AudioDevice,
        WatchFolders = WatchFolders,
        ResumeSession = ResumeSession,
        AccentFromArt = AccentFromArt,
        GeneratedBackdrop = GeneratedBackdrop,
        SidebarCollapsed = SidebarCollapsed,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowMaximized = WindowMaximized,
        SortOrders = new Dictionary<string, string>(SortOrders),
    };
}
