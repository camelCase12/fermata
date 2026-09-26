using Fermata.Text;

namespace Fermata.Library;

/// <summary>The listening data of one track.</summary>
public sealed class TrackStats
{
    public int Plays { get; set; }
    public int Skips { get; set; }
    public DateTime? LastPlayed { get; set; }

    /// <summary>When the track was liked, or null.</summary>
    public DateTime? Liked { get; set; }

    /// <summary>Whether the track is disliked.</summary>
    /// <remarks>Disliked tracks are left out of mixes, radio and autoplay.</remarks>
    public bool Disliked { get; set; }

    /// <summary>The track's folded "title / artist / album".</summary>
    /// <remarks>It identifies the file after it is renamed or moved.</remarks>
    public string? Identity { get; set; }

    internal bool IsEmpty => Plays == 0 && Skips == 0 && Liked is null && !Disliked;
}

public sealed record PlayRecord(string Path, DateTime At);

/// <summary>Serialized form of <see cref="UserData"/>.</summary>
public sealed class UserDataDocument
{
    public int Version { get; set; } = 1;
    public Dictionary<string, TrackStats> Tracks { get; set; } = [];
    public List<PlayRecord> History { get; set; } = [];
}

/// <summary>Likes, play counts and history, keyed by file path.</summary>
/// <remarks>It is owned by the UI thread. <see cref="ToDocument"/> copies the state for saving on another thread.</remarks>
public sealed class UserData
{
    public const int HistoryLimit = 5000;

    private readonly Dictionary<string, TrackStats> tracks;
    private readonly List<PlayRecord> history;

    public UserData() : this(new UserDataDocument())
    {
    }

    private UserData(UserDataDocument document)
    {
        tracks = new Dictionary<string, TrackStats>(document.Tracks, StringComparer.Ordinal);
        history = document.History;
        if (history.Count > HistoryLimit)
            history.RemoveRange(0, history.Count - HistoryLimit);
    }

    public static UserData FromDocument(UserDataDocument? document) => new(document ?? new UserDataDocument());

    /// <summary>Raised after a track's data changes, with its path.</summary>
    public event Action<string>? TrackChanged;

    /// <summary>Raised after any change, including history.</summary>
    public event Action? Changed;

    /// <summary>Plays, oldest first.</summary>
    public IReadOnlyList<PlayRecord> History => history;

    public IReadOnlyDictionary<string, TrackStats> Tracks => tracks;

    public TrackStats? StatsFor(string path) => tracks.GetValueOrDefault(path);
    public bool IsLiked(string path) => tracks.TryGetValue(path, out var stats) && stats.Liked is not null;
    public bool IsDisliked(string path) => tracks.TryGetValue(path, out var stats) && stats.Disliked;
    public int PlayCount(string path) => tracks.TryGetValue(path, out var stats) ? stats.Plays : 0;

    public void SetLiked(Track track, bool liked)
    {
        string path = track.Path;
        var stats = Edit(track);
        if ((stats.Liked is not null) == liked && !(liked && stats.Disliked))
            return;
        stats.Liked = liked ? DateTime.UtcNow : null;
        if (liked)
            stats.Disliked = false;
        Commit(path, stats);
    }

    public void SetDisliked(Track track, bool disliked)
    {
        string path = track.Path;
        var stats = Edit(track);
        if (stats.Disliked == disliked)
            return;
        stats.Disliked = disliked;
        if (disliked)
            stats.Liked = null;
        Commit(path, stats);
    }

    public void RecordPlay(Track track, DateTime at)
    {
        string path = track.Path;
        var stats = Edit(track);
        stats.Plays++;
        stats.LastPlayed = at;
        history.Add(new PlayRecord(path, at));
        if (history.Count > HistoryLimit + 500)
            history.RemoveRange(0, history.Count - HistoryLimit); // Trims in batches.
        Commit(path, stats);
    }

    public void RecordSkip(Track track)
    {
        string path = track.Path;
        var stats = Edit(track);
        stats.Skips++;
        Commit(path, stats);
    }

    /// <summary>Forgets the listening history.</summary>
    /// <remarks>Play counts, likes and skips remain.</remarks>
    public void ClearHistory()
    {
        if (history.Count == 0)
            return;
        history.Clear();
        Changed?.Invoke();
    }

    /// <summary>Removes all plays of a track from history.</summary>
    public void ForgetHistory(string path)
    {
        if (history.RemoveAll(record => record.Path == path) > 0)
            Changed?.Invoke();
    }

    /// <summary>Carries data over to renamed or moved files.</summary>
    /// <remarks>
    /// When a path with data has vanished and exactly one track without data has the same title, artist and
    /// album, the data moves to that track.
    /// </remarks>
    /// <returns>The renames, from old path to new path.</returns>
    public IReadOnlyDictionary<string, string> Relink(LibrarySnapshot library)
    {
        var orphans = tracks.Where(pair => pair.Value.Identity is not null && library.FindTrack(pair.Key) is null).ToList();
        if (orphans.Count == 0)
            return new Dictionary<string, string>();
        var candidates = new Dictionary<string, List<Track>>(StringComparer.Ordinal);
        foreach (var track in library.Tracks)
        {
            if (tracks.ContainsKey(track.Path))
                continue;
            string identity = Identity(track);
            if (!candidates.TryGetValue(identity, out var list))
                candidates[identity] = list = [];
            list.Add(track);
        }
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (oldPath, stats) in orphans)
        {
            if (!candidates.TryGetValue(stats.Identity!, out var matches) || matches.Count != 1)
                continue;
            tracks.Remove(oldPath);
            tracks[matches[0].Path] = stats;
            renamed[oldPath] = matches[0].Path;
            matches.Clear(); // a second orphan with the same identity must not claim it too
        }
        if (renamed.Count == 0)
            return renamed;
        for (int i = 0; i < history.Count; i++)
        {
            if (renamed.TryGetValue(history[i].Path, out string? newPath))
                history[i] = history[i] with { Path = newPath };
        }
        Changed?.Invoke();
        return renamed;
    }

    public UserDataDocument ToDocument() => new()
    {
        Tracks = tracks.Where(pair => !pair.Value.IsEmpty).ToDictionary(pair => pair.Key, pair => new TrackStats
        {
            Plays = pair.Value.Plays,
            Skips = pair.Value.Skips,
            LastPlayed = pair.Value.LastPlayed,
            Liked = pair.Value.Liked,
            Disliked = pair.Value.Disliked,
            Identity = pair.Value.Identity,
        }, StringComparer.Ordinal),
        History = [.. history],
    };

    private static string Identity(Track track) =>
        TextFolding.Fold(track.Title) + "\u001F" + TextFolding.Fold(track.Credit) + "\u001F" + TextFolding.Fold(track.AlbumTitle);

    private TrackStats Edit(Track track)
    {
        if (!tracks.TryGetValue(track.Path, out var stats))
            tracks[track.Path] = stats = new TrackStats();
        stats.Identity ??= Identity(track);
        return stats;
    }

    private void Commit(string path, TrackStats stats)
    {
        if (stats.IsEmpty)
            tracks.Remove(path);
        TrackChanged?.Invoke(path);
        Changed?.Invoke();
    }
}
