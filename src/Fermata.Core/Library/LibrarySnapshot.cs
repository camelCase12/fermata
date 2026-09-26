using Fermata.Text;

namespace Fermata.Library;

/// <summary>An immutable, fully indexed view of the library.</summary>
public sealed class LibrarySnapshot
{
    private static long lastVersion;

    private readonly Dictionary<string, Track> byPath;
    private readonly Dictionary<string, Album> albumsByKey;
    private readonly Dictionary<string, Artist> artistsByKey;
    private readonly Dictionary<string, Genre> genresByKey;
    private readonly IReadOnlyDictionary<string, string> folderImages;

    public static LibrarySnapshot Empty { get; } = Build([], new Dictionary<string, string>());

    private LibrarySnapshot(IReadOnlyCollection<Track> tracks, IReadOnlyDictionary<string, string> folderImages)
    {
        Version = Interlocked.Increment(ref lastVersion);
        this.folderImages = folderImages;
        byPath = new Dictionary<string, Track>(tracks.Count, StringComparer.Ordinal);
        foreach (var track in tracks)
            byPath[track.Path] = track;

        // Albums.
        var albumTracks = new Dictionary<string, List<Track>>(StringComparer.Ordinal);
        foreach (var track in byPath.Values)
        {
            if (track.AlbumKey is null)
                continue;
            if (!albumTracks.TryGetValue(track.AlbumKey, out var list))
                albumTracks[track.AlbumKey] = list = [];
            list.Add(track);
        }
        albumsByKey = new Dictionary<string, Album>(albumTracks.Count, StringComparer.Ordinal);
        foreach (var (key, list) in albumTracks)
            albumsByKey[key] = new Album(key, list, FindFolderArt(list[0]));
        var albums = albumsByKey.Values.ToList();
        albums.Sort(CompareAlbums);
        Albums = albums;

        // Tracks in library order, which is albums in album order and then loose tracks by artist and title.
        var ordered = new List<Track>(byPath.Count);
        foreach (var album in albums)
            ordered.AddRange(album.Tracks);
        var loose = byPath.Values.Where(t => t.AlbumKey is null)
            .Select(t => (Track: t, Artist: TextFolding.SortKey(t.ArtistSort ?? t.DisplayArtist), Title: TextFolding.SortKey(t.Title)))
            .OrderBy(e => e.Artist, StringComparer.Ordinal).ThenBy(e => e.Title, StringComparer.Ordinal)
            .Select(e => e.Track);
        ordered.AddRange(loose);
        Tracks = ordered;

        (Artists, artistsByKey) = BuildArtists(ordered, albums);
        (Genres, genresByKey) = BuildGenres(ordered);

        long ticks = 0, bytes = 0;
        foreach (var track in ordered)
        {
            ticks += track.Duration.Ticks;
            bytes += track.FileSize;
        }
        TotalDuration = TimeSpan.FromTicks(ticks);
        TotalSize = bytes;
    }

    public static LibrarySnapshot Build(IReadOnlyCollection<Track> tracks, IReadOnlyDictionary<string, string> folderImages) =>
        new(tracks, folderImages);

    /// <summary>A number that increases with every build.</summary>
    public long Version { get; }

    /// <summary>All tracks, grouped by album in album order, followed by tracks without an album.</summary>
    public IReadOnlyList<Track> Tracks { get; }

    public IReadOnlyList<Album> Albums { get; }
    public IReadOnlyList<Artist> Artists { get; }
    public IReadOnlyList<Genre> Genres { get; }
    public TimeSpan TotalDuration { get; }
    public long TotalSize { get; }

    /// <summary>Image files that serve as folder art, by directory.</summary>
    public IReadOnlyDictionary<string, string> FolderImages => folderImages;

    public Track? FindTrack(string path) => byPath.GetValueOrDefault(path);

    public Track? FindTrack(ReadOnlySpan<char> path) =>
        byPath.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(path, out var track) ? track : null;
    public Album? FindAlbum(string key) => albumsByKey.GetValueOrDefault(key);
    public Album? AlbumOf(Track track) => track.AlbumKey is { } key ? albumsByKey.GetValueOrDefault(key) : null;
    public Artist? FindArtist(string name) => artistsByKey.GetValueOrDefault(TextFolding.Fold(name));
    public Genre? FindGenre(string name) => genresByKey.GetValueOrDefault(TextFolding.Fold(name));

    /// <summary>The cover art for a track.</summary>
    /// <remarks>This is its embedded picture, else its album's cover, else its folder's image.</remarks>
    public ArtSource? ArtOf(Track track) =>
        track.EmbeddedArt ?? AlbumOf(track)?.Art ?? (folderImages.TryGetValue(track.Directory, out string? image) ? ArtSource.ImageFile(image) : null);

    /// <summary>The artists credited on a track, resolved to library artists.</summary>
    public IEnumerable<Artist> ArtistsOf(Track track)
    {
        foreach (string name in track.ArtistNames)
        {
            if (FindArtist(name) is { } artist)
                yield return artist;
        }
    }

    private ArtSource? FindFolderArt(Track track)
    {
        // Prefer the album folder (parent of disc folders), then the track's own folder.
        string albumDirectory = AlbumGrouping.AlbumDirectory(track.Path);
        if (folderImages.TryGetValue(albumDirectory, out string? image) || folderImages.TryGetValue(track.Directory, out image))
            return ArtSource.ImageFile(image);
        return null;
    }

    private static int CompareAlbums(Album a, Album b)
    {
        int result = string.CompareOrdinal(a.SortArtist, b.SortArtist);
        if (result == 0)
            result = a.Year.CompareTo(b.Year);
        if (result == 0)
            result = string.CompareOrdinal(a.SortTitle, b.SortTitle);
        if (result == 0)
            result = string.CompareOrdinal(a.Directory, b.Directory);
        return result;
    }

    /// <summary>Builds the library's artists.</summary>
    /// <remarks>
    /// Artists are keyed by folded name, so "Björk" and "Bjork" are one artist. Album artists own albums,
    /// and performers credited on another artist's album appear on it.
    /// </remarks>
    private static (IReadOnlyList<Artist>, Dictionary<string, Artist>) BuildArtists(List<Track> tracks, List<Album> albums)
    {
        var builders = new Dictionary<string, ArtistBuilder>(StringComparer.Ordinal);
        ArtistBuilder For(string name)
        {
            string key = TextFolding.Fold(name);
            if (!builders.TryGetValue(key, out var builder))
                builders[key] = builder = new ArtistBuilder();
            builder.Names[name] = builder.Names.GetValueOrDefault(name) + 1;
            return builder;
        }

        foreach (var album in albums)
        {
            foreach (string name in album.ArtistNames)
                For(name).Albums.Add(album);
        }
        foreach (var track in tracks)
        {
            IReadOnlyList<string> names = track.ArtistNames;
            if (names.Count == 0 && track.AlbumArtist.Length > 0)
                names = ArtistCredit.Split(track.AlbumArtist);
            foreach (string name in names)
            {
                var builder = For(name);
                builder.Tracks.Add(track);
                if (track.AlbumKey is not null && builder.LastAppearance != track.AlbumKey)
                {
                    builder.LastAppearance = track.AlbumKey;
                    builder.AppearanceKeys.Add(track.AlbumKey);
                }
            }
        }

        var albumByKey = albums.ToDictionary(a => a.Key, StringComparer.Ordinal);
        var byKey = new Dictionary<string, Artist>(builders.Count, StringComparer.Ordinal);
        var artists = new List<Artist>(builders.Count);
        foreach (var (key, builder) in builders)
        {
            string name = builder.Names.MaxBy(pair => pair.Value).Key;
            var own = builder.Albums.Distinct().ToList();
            var ownKeys = own.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
            var appearsOn = builder.AppearanceKeys.Where(k => !ownKeys.Contains(k)).Select(k => albumByKey[k]).Distinct().ToList();
            own.Sort((a, b) => b.Year.CompareTo(a.Year) is var byYear and not 0 ? byYear : string.CompareOrdinal(a.SortTitle, b.SortTitle));
            appearsOn.Sort((a, b) => b.Year.CompareTo(a.Year));
            var artist = new Artist(name, own, appearsOn, builder.Tracks);
            byKey[key] = artist;
            artists.Add(artist);
        }
        artists.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        return (artists, byKey);
    }

    private sealed class ArtistBuilder
    {
        public readonly Dictionary<string, int> Names = new(StringComparer.Ordinal);
        public readonly List<Album> Albums = [];
        public readonly List<Track> Tracks = [];
        public readonly HashSet<string> AppearanceKeys = new(StringComparer.Ordinal);
        public string? LastAppearance;
    }

    private (IReadOnlyList<Genre>, Dictionary<string, Genre>) BuildGenres(List<Track> tracks)
    {
        var groups = new Dictionary<string, (Dictionary<string, int> Names, List<Track> Tracks)>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            foreach (string genre in track.Genres)
            {
                string key = TextFolding.Fold(genre);
                if (key.Length == 0)
                    continue;
                if (!groups.TryGetValue(key, out var group))
                    groups[key] = group = (new Dictionary<string, int>(StringComparer.Ordinal), []);
                group.Names[genre] = group.Names.GetValueOrDefault(genre) + 1;
                group.Tracks.Add(track);
            }
        }
        var byKey = new Dictionary<string, Genre>(groups.Count, StringComparer.Ordinal);
        var genres = new List<Genre>(groups.Count);
        foreach (var (key, group) in groups)
        {
            var albums = new List<Album>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in group.Tracks)
            {
                if (track.AlbumKey is { } albumKey && seen.Add(albumKey) && AlbumOf(track) is { } album)
                    albums.Add(album);
            }
            var genre = new Genre(group.Names.MaxBy(pair => pair.Value).Key, group.Tracks, albums);
            byKey[key] = genre;
            genres.Add(genre);
        }
        genres.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        return (genres, byKey);
    }
}
