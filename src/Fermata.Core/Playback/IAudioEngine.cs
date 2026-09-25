namespace Fermata.Playback;

/// <summary>
/// Decodes and plays audio files. Items are identified by the id returned when they are handed to
/// the engine, so late events about replaced items can be recognized and ignored.
/// </summary>
/// <remarks>Methods are called, and events raised, on the owner's thread (the UI thread).</remarks>
public interface IAudioEngine : IDisposable
{
    event Action<EngineEvent>? EventRaised;

    /// <summary>False when no audio backend could be loaded; see <see cref="UnavailableReason"/>.</summary>
    bool IsAvailable { get; }

    string? UnavailableReason { get; }

    /// <summary>Plays <paramref name="path"/> immediately, replacing the current and preloaded items.</summary>
    long Play(string path, TimeSpan start, bool paused);

    /// <summary>Queues <paramref name="path"/> to follow the current item without a gap, replacing any earlier preload.</summary>
    long Preload(string path);

    void ClearPreload();
    void Stop();
    void Seek(TimeSpan position);

    bool Paused { set; }

    /// <summary>Linear volume, 0–1.</summary>
    double Volume { set; }

    bool Muted { set; }
    ReplayGainMode ReplayGain { set; }
    bool Gapless { set; }

    /// <summary>Output device name from <see cref="AudioDevices"/>, or "auto".</summary>
    string AudioDevice { set; }

    IReadOnlyList<AudioDevice> AudioDevices { get; }

    /// <summary>Position within the current item.</summary>
    TimeSpan Position { get; }
}

public sealed record AudioDevice(string Name, string Description);

public enum EndReason
{
    /// <summary>The item played to its end.</summary>
    Finished,
    /// <summary>The item was replaced or stopped on request.</summary>
    Stopped,
    /// <summary>The item could not be opened or decoded.</summary>
    Failed,
}

public abstract record EngineEvent;

/// <summary>The engine began an item: one it was told to play, or a preloaded one following the previous item.</summary>
public sealed record ItemStarted(long Id) : EngineEvent;

/// <summary>An item was opened; its duration as measured from the stream.</summary>
public sealed record ItemLoaded(long Id, TimeSpan Duration) : EngineEvent;

public sealed record ItemEnded(long Id, EndReason Reason, string? Error) : EngineEvent;

public sealed record PauseChanged(bool Paused) : EngineEvent;

/// <summary>Playback resumed after a seek or a start.</summary>
public sealed record PlaybackRestarted : EngineEvent;

/// <summary>The engine has nothing left to play: an item ended with no item after it.</summary>
public sealed record EngineIdle : EngineEvent;

public sealed record DevicesChanged : EngineEvent;
