namespace Fermata.Playback;

public enum RepeatMode
{
    Off,
    /// <summary>Repeat the whole queue.</summary>
    All,
    /// <summary>Repeat the current track when it ends naturally; Next still advances.</summary>
    One,
}

public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>Loudness normalization from ReplayGain tags.</summary>
public enum ReplayGainMode
{
    Off,
    Track,
    Album,
}
