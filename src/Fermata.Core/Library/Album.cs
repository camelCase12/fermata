using Fermata.Text;

namespace Fermata.Library;

/// <summary>An album in the library.</summary>
/// <remarks>Its tracks share an album title and folder, and are in disc and track order.</remarks>
public sealed class Album
{
    public const string VariousArtists = "Various Artists";

    internal Album(string key, List<Track> tracks, ArtSource? folderArt)
    {
        Key = key;
        tracks.Sort(CompareTrackOrder);
        Tracks = tracks;
        Title = MostCommon(tracks, t => t.AlbumTitle) ?? Track.UnknownAlbum;
        (string credit, IsCompilation) = DecideArtist(tracks);
        Artist = ArtistCredit.Display(credit);
        ArtistNames = IsCompilation ? [] : ArtistCredit.Split(credit);
        Year = MostCommonYear(tracks);
        Genre = MostCommon(tracks, t => t.Genre) ?? "";
        Directory = AlbumGrouping.AlbumDirectory(tracks[0].Path);
        Art = folderArt ?? tracks.Select(t => t.EmbeddedArt).FirstOrDefault(a => a is not null);
        long ticks = 0;
        DateTime added = DateTime.MinValue;
        int discs = 1;
        foreach (var track in tracks)
        {
            ticks += track.Duration.Ticks;
            if (track.Added > added)
                added = track.Added;
            discs = Math.Max(discs, Math.Max(track.DiscNumber, track.DiscCount));
        }
        Duration = TimeSpan.FromTicks(ticks);
        Added = added;
        DiscCount = discs;
        SortTitle = TextFolding.SortKey(tracks[0].AlbumSort ?? Title);
        SortArtist = IsCompilation ? "￿" : TextFolding.SortKey(tracks[0].AlbumArtistSort ?? Artist);
    }

    public string Key { get; }
    public string Title { get; }

    /// <summary>The album artist, or <see cref="VariousArtists"/> for compilations.</summary>
    public string Artist { get; }

    /// <summary>Individual album artists (empty for compilations).</summary>
    public IReadOnlyList<string> ArtistNames { get; }

    public bool IsCompilation { get; }
    public int Year { get; }
    public string Genre { get; }
    public IReadOnlyList<Track> Tracks { get; }
    public TimeSpan Duration { get; }

    /// <summary>When the album's most recently added track was added.</summary>
    public DateTime Added { get; }

    public int DiscCount { get; }
    public string Directory { get; }
    public ArtSource? Art { get; }

    /// <summary>The folded sort keys. Compilations sort after named artists.</summary>
    public string SortTitle { get; }
    public string SortArtist { get; }

    public override string ToString() => $"{Artist} — {Title}";

    /// <summary>Disc, then track number, then title (for untagged track numbers), then path.</summary>
    public static int CompareTrackOrder(Track a, Track b)
    {
        int result = Math.Max(a.DiscNumber, 1).CompareTo(Math.Max(b.DiscNumber, 1));
        if (result == 0)
            result = (a.TrackNumber == 0 ? int.MaxValue : a.TrackNumber).CompareTo(b.TrackNumber == 0 ? int.MaxValue : b.TrackNumber);
        if (result == 0)
            result = string.CompareOrdinal(a.Path, b.Path);
        return result;
    }

    /// <summary>Decides the album artist and whether the album is a compilation.</summary>
    /// <remarks>
    /// The artist is the tagged album artist, else the performer all tracks share. Without either, the
    /// album is a compilation by various artists.
    /// </remarks>
    private static (string Artist, bool Compilation) DecideArtist(List<Track> tracks)
    {
        string? tagged = MostCommon(tracks, t => t.AlbumArtist);
        if (tagged is not null)
        {
            bool various = TextFolding.Fold(tagged) is "various artists" or "various" or "va";
            return (various ? VariousArtists : tagged, various);
        }
        int compilationVotes = tracks.Count(t => t.Compilation);
        string? credit = tracks[0].Artist;
        if (tracks.All(t => t.Artist == credit) && !string.IsNullOrEmpty(credit))
            return (credit, false);
        // "A" and "A feat. B" share their primary performer.
        string? primary = tracks[0].ArtistNames.Count > 0 ? tracks[0].ArtistNames[0] : null;
        if (primary is not null && compilationVotes * 2 <= tracks.Count
            && tracks.All(t => t.ArtistNames.Count > 0 && string.Equals(t.ArtistNames[0], primary, StringComparison.OrdinalIgnoreCase)))
            return (primary, false);
        if (tracks.Count == 1 && !string.IsNullOrEmpty(credit))
            return (credit, false);
        return (VariousArtists, true);
    }

    private static string? MostCommon(List<Track> tracks, Func<Track, string> selector)
    {
        string? best = null;
        int bestCount = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            string value = selector(track);
            if (value.Length == 0)
                continue;
            int count = counts[value] = counts.GetValueOrDefault(value) + 1;
            if (count > bestCount)
            {
                best = value;
                bestCount = count;
            }
        }
        return best;
    }

    private static int MostCommonYear(List<Track> tracks)
    {
        int best = 0, bestCount = 0;
        var counts = new Dictionary<int, int>();
        foreach (var track in tracks)
        {
            if (track.Year == 0)
                continue;
            int count = counts[track.Year] = counts.GetValueOrDefault(track.Year) + 1;
            if (count > bestCount || (count == bestCount && track.Year < best))
            {
                best = track.Year;
                bestCount = count;
            }
        }
        return best;
    }
}

/// <summary>A performer in the library.</summary>
public sealed class Artist
{
    internal Artist(string name, List<Album> albums, List<Album> appearsOn, List<Track> tracks)
    {
        Name = name;
        Albums = albums;
        AppearsOn = appearsOn;
        Tracks = tracks;
        SortKey = TextFolding.SortKey(name);
        Art = albums.Select(a => a.Art).FirstOrDefault(a => a is not null)
            ?? tracks.Select(t => t.EmbeddedArt).FirstOrDefault(a => a is not null);
        Duration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
    }

    public string Name { get; }
    public IReadOnlyList<Album> Albums { get; }
    public IReadOnlyList<Album> AppearsOn { get; }
    public IReadOnlyList<Track> Tracks { get; }
    public string SortKey { get; }
    public ArtSource? Art { get; }
    public TimeSpan Duration { get; }

    public override string ToString() => Name;
}

public sealed class Genre
{
    internal Genre(string name, List<Track> tracks, List<Album> albums)
    {
        Name = name;
        Tracks = tracks;
        Albums = albums;
        SortKey = TextFolding.SortKey(name);
    }

    public string Name { get; }
    public IReadOnlyList<Track> Tracks { get; }
    public IReadOnlyList<Album> Albums { get; }
    public string SortKey { get; }

    public override string ToString() => Name;
}
