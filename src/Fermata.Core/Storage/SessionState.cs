namespace Fermata.Storage;

/// <summary>The queue and playback position, saved on exit and restored (paused) at startup.</summary>
public sealed class SessionState : IVersionedFile
{
    public static int CurrentVersion => 1;
    public int Version { get; set; } = CurrentVersion;

    /// <summary>Queue entries in their original (unshuffled) order.</summary>
    public List<string> Queue { get; set; } = [];

    /// <summary>Play order as indices into <see cref="Queue"/> while shuffle is on; null otherwise.</summary>
    public List<int>? ShuffleOrder { get; set; }

    /// <summary>Index of the current entry in play order, or -1.</summary>
    public int Current { get; set; } = -1;

    public double PositionSeconds { get; set; }
    public string? SourceTitle { get; set; }
    public string? SourceKind { get; set; }
    public string? SourceKey { get; set; }
}
