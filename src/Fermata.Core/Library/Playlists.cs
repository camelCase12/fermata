using System.Text;
using Fermata.Storage;

namespace Fermata.Library;

/// <summary>A user playlist.</summary>
/// <remarks>Its entries are track paths, in order, and may repeat.</remarks>
public sealed class Playlist
{
    internal Playlist(string id, string name, string description, DateTime created, DateTime modified, List<string> paths)
    {
        Id = id;
        Name = name;
        Description = description;
        Created = created;
        Modified = modified;
        PathList = paths;
    }

    /// <summary>Stable identifier, also the file name of the saved playlist.</summary>
    public string Id { get; }

    public string Name { get; internal set; }
    public string Description { get; internal set; }
    public DateTime Created { get; }
    public DateTime Modified { get; internal set; }
    public IReadOnlyList<string> Paths => PathList;

    internal List<string> PathList { get; }

    /// <summary>Resolves paths to library tracks, skipping files that are no longer present.</summary>
    public List<Track> Resolve(LibrarySnapshot library)
    {
        var tracks = new List<Track>(PathList.Count);
        foreach (string path in PathList)
        {
            if (library.FindTrack(path) is { } track)
                tracks.Add(track);
        }
        return tracks;
    }

    public override string ToString() => Name;
}

/// <summary>A playlist read from an M3U file.</summary>
/// <param name="Name">The name the file gives, or null.</param>
/// <param name="Paths">The entries, as absolute paths.</param>
public sealed record M3uPlaylist(string? Name, IReadOnlyList<string> Paths);

public sealed class PlaylistDocument : IVersionedFile
{
    public static int CurrentVersion => 1;
    public int Version { get; set; } = CurrentVersion;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime Modified { get; set; }
    public List<string> Tracks { get; set; } = [];
}

/// <summary>The user's playlists.</summary>
/// <remarks>
/// Each playlist is a JSON file in <c>~/.local/share/fermata/playlists</c>. The store is owned by the UI
/// thread, and <see cref="CaptureChanges"/> hands pending edits to a writer.
/// </remarks>
public sealed class PlaylistStore
{
    private readonly string directory;
    private readonly List<Playlist> playlists = [];
    private readonly HashSet<Playlist> dirty = [];
    private readonly HashSet<string> deleted = new(StringComparer.Ordinal);

    public PlaylistStore(string directory)
    {
        this.directory = directory;
    }

    /// <summary>Playlists in creation order.</summary>
    public IReadOnlyList<Playlist> Playlists => playlists;

    /// <summary>Raised when playlists are created, deleted or renamed.</summary>
    public event Action? ListChanged;

    /// <summary>Raised when a playlist's tracks change.</summary>
    public event Action<Playlist>? PlaylistChanged;

    /// <summary>Raised after any change that needs saving.</summary>
    public event Action? Changed;

    /// <summary>The playlist files that the last <see cref="Load"/> could not read.</summary>
    public IReadOnlyList<string> Unreadable => unreadable;
    private readonly List<string> unreadable = [];

    /// <summary>The ids of playlists whose files come from a newer Fermata, which are never written.</summary>
    public IReadOnlyCollection<string> FromNewerVersion => fromNewerVersion;
    private readonly HashSet<string> fromNewerVersion = [];

    public void Load()
    {
        playlists.Clear();
        unreadable.Clear();
        fromNewerVersion.Clear();
        if (!Directory.Exists(directory))
            return;
        foreach (string file in Directory.GetFiles(directory, "*.json"))
        {
            var (document, setAside, isNewer) = FermataJson.LoadOrSetAside(file, FermataJson.Default.PlaylistDocument);
            if (setAside is not null)
                unreadable.Add(setAside);
            if (document is null)
                continue;
            string id = Path.GetFileNameWithoutExtension(file);
            if (isNewer)
                fromNewerVersion.Add(id);
            playlists.Add(new Playlist(id, document.Name, document.Description,
                document.Created, document.Modified, document.Tracks));
        }
        playlists.Sort((a, b) => a.Created.CompareTo(b.Created));
    }

    public Playlist? Find(string id) => playlists.Find(p => p.Id == id);

    public Playlist Create(string name, IEnumerable<string>? paths = null)
    {
        var now = DateTime.UtcNow;
        var playlist = new Playlist(Guid.NewGuid().ToString("N"), UniqueName(name), "", now, now, paths?.ToList() ?? []);
        playlists.Add(playlist);
        MarkDirty(playlist);
        ListChanged?.Invoke();
        return playlist;
    }

    public void Rename(Playlist playlist, string name, string description)
    {
        name = name.Trim();
        if (name.Length == 0 || (name == playlist.Name && description == playlist.Description))
            return;
        playlist.Name = name;
        playlist.Description = description.Trim();
        Touch(playlist);
        ListChanged?.Invoke();
    }

    /// <summary>Appends tracks to a playlist.</summary>
    /// <remarks>Tracks already present are skipped unless <paramref name="allowDuplicates"/> is set.</remarks>
    /// <returns>How many tracks were added.</returns>
    public int Add(Playlist playlist, IEnumerable<string> paths, bool allowDuplicates = false)
    {
        var existing = allowDuplicates ? null : new HashSet<string>(playlist.PathList, StringComparer.Ordinal);
        int added = 0;
        foreach (string path in paths)
        {
            if (existing is null || existing.Add(path))
            {
                playlist.PathList.Add(path);
                added++;
            }
        }
        if (added > 0)
            Touch(playlist);
        return added;
    }

    /// <summary>Removes the entries at <paramref name="indices"/> (positions, since a path may repeat).</summary>
    public void RemoveAt(Playlist playlist, IEnumerable<int> indices)
    {
        bool changed = false;
        foreach (int index in indices.Distinct().OrderDescending())
        {
            if ((uint)index < (uint)playlist.PathList.Count)
            {
                playlist.PathList.RemoveAt(index);
                changed = true;
            }
        }
        if (changed)
            Touch(playlist);
    }

    public void Move(Playlist playlist, int from, int to)
    {
        var list = playlist.PathList;
        if ((uint)from >= (uint)list.Count || from == to)
            return;
        to = Math.Clamp(to, 0, list.Count - 1);
        string path = list[from];
        list.RemoveAt(from);
        list.Insert(to, path);
        Touch(playlist);
    }

    public void Delete(Playlist playlist)
    {
        if (!playlists.Remove(playlist))
            return;
        dirty.Remove(playlist);
        deleted.Add(playlist.Id);
        ListChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Replaces renamed or moved paths (see <see cref="UserData.Relink"/>).</summary>
    public void RenamePaths(IReadOnlyDictionary<string, string> renamed)
    {
        foreach (var playlist in playlists)
        {
            bool changed = false;
            for (int i = 0; i < playlist.PathList.Count; i++)
            {
                if (renamed.TryGetValue(playlist.PathList[i], out string? newPath))
                {
                    playlist.PathList[i] = newPath;
                    changed = true;
                }
            }
            if (changed)
                Touch(playlist);
        }
    }

    /// <summary>Copies pending changes into an action that writes them.</summary>
    /// <remarks>Call it on the owning thread. The action can run on any thread.</remarks>
    /// <returns>The action, or null when nothing changed.</returns>
    public Action? CaptureChanges()
    {
        if (dirty.Count == 0 && deleted.Count == 0)
            return null;
        var writes = dirty.Where(p => !fromNewerVersion.Contains(p.Id)).Select(p => (Path.Combine(directory, p.Id + ".json"), new PlaylistDocument
        {
            Name = p.Name,
            Description = p.Description,
            Created = p.Created,
            Modified = p.Modified,
            Tracks = [.. p.PathList],
        })).ToList();
        var deletions = deleted.Where(id => !fromNewerVersion.Contains(id)).Select(id => Path.Combine(directory, id + ".json")).ToList();
        dirty.Clear();
        deleted.Clear();
        return () =>
        {
            foreach (var (file, document) in writes)
                FermataJson.Save(file, document, FermataJson.Default.PlaylistDocument);
            foreach (string file in deletions)
                File.Delete(file);
        };
    }

    /// <summary>Writes an extended M3U playlist with paths relative to the playlist file where possible.</summary>
    public static void ExportM3u(Playlist playlist, LibrarySnapshot library, string file)
    {
        var text = new StringBuilder("#EXTM3U\n");
        text.Append("#PLAYLIST:").Append(playlist.Name).Append('\n');
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        foreach (string path in playlist.PathList)
        {
            if (library.FindTrack(path) is { } track)
                text.Append("#EXTINF:").Append((int)track.Duration.TotalSeconds).Append(',').Append(track.DisplayArtist).Append(" - ").Append(track.Title).Append('\n');
            string relative = Path.GetRelativePath(baseDirectory, path);
            // A line starting with '#' would read back as a comment.
            if (relative.StartsWith('#'))
                relative = "./" + relative;
            text.Append(relative.StartsWith("..", StringComparison.Ordinal) ? path : relative).Append('\n');
        }
        AtomicFile.WriteAllBytes(file, Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>Reads an M3U or M3U8 playlist.</summary>
    /// <remarks>
    /// Relative entries are resolved against the playlist file, and file:// URLs become paths. Streams and
    /// other URLs are skipped.
    /// </remarks>
    public static M3uPlaylist ReadM3u(string file)
    {
        const string NameDirective = "#PLAYLIST:";
        string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
        string? name = null;
        var paths = new List<string>();
        foreach (string raw in ReadText(file).Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith(NameDirective, StringComparison.Ordinal) && name is null && line.Length > NameDirective.Length)
                name = line[NameDirective.Length..].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.Contains("://", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.IsFile)
                    paths.Add(uri.LocalPath);
                continue;
            }
            // Playlists written on Windows separate folders with backslashes, which are ordinary characters in Linux names.
            string path = Path.GetFullPath(line, baseDirectory);
            if (line.Contains('\\') && !File.Exists(path))
                path = Path.GetFullPath(line.Replace('\\', '/'), baseDirectory);
            paths.Add(path);
        }
        return new M3uPlaylist(string.IsNullOrEmpty(name) ? null : name, paths);
    }

    /// <summary>The text of a playlist file, read as UTF-8, or as Latin-1 when it is not valid UTF-8.</summary>
    private static string ReadText(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private string UniqueName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "New playlist" : name.Trim();
        string candidate = name;
        for (int i = 2; playlists.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)); i++)
            candidate = $"{name} {i}";
        return candidate;
    }

    private void Touch(Playlist playlist)
    {
        playlist.Modified = DateTime.UtcNow;
        MarkDirty(playlist);
        PlaylistChanged?.Invoke(playlist);
    }

    private void MarkDirty(Playlist playlist)
    {
        dirty.Add(playlist);
        Changed?.Invoke();
    }
}
