namespace Fermata.Library;

/// <summary>A generated playlist, such as a genre or artist mix.</summary>
public sealed class Mix
{
    public Mix(string title, string description, IReadOnlyList<Track> tracks, IReadOnlyList<ArtSource> covers)
    {
        Title = title;
        Description = description;
        Tracks = tracks;
        Covers = covers;
    }

    public string Title { get; }
    public string Description { get; }
    public IReadOnlyList<Track> Tracks { get; }

    /// <summary>Up to four distinct covers for a mosaic.</summary>
    public IReadOnlyList<ArtSource> Covers { get; }

    public override string ToString() => Title;
}

public enum HomeSectionKind
{
    Tracks,
    Albums,
    Artists,
    Mixes,
}

public sealed record HomeSection(string Title, string? Subtitle, HomeSectionKind Kind, IReadOnlyList<object> Items);

/// <summary>The sections of the home page, computed from the library and listening history.</summary>
public static class HomeFeed
{
    public static List<HomeSection> Build(LibrarySnapshot library, UserData userData, Random? random = null)
    {
        random ??= Random.Shared;
        var sections = new List<HomeSection>();
        if (library.Tracks.Count == 0)
            return sections;
        var recommender = new Recommender(library, userData, random);
        var now = DateTime.UtcNow;
        var frecency = Frecency(userData, now);

        var picks = QuickPicks(library, userData, recommender, frecency, random);
        if (picks.Count > 0)
            sections.Add(new HomeSection("Quick picks", frecency.Count > 0 ? "Based on what you play" : "Start here", HomeSectionKind.Tracks, picks));

        var recentAlbums = RecentlyPlayedAlbums(library, userData, 16);
        if (recentAlbums.Count > 0)
            sections.Add(new HomeSection("Listen again", null, HomeSectionKind.Albums, recentAlbums));

        var mixes = Mixes(library, userData, recommender, frecency, random);
        if (mixes.Count > 0)
            sections.Add(new HomeSection("Your mixes", "Made from your library", HomeSectionKind.Mixes, mixes));

        var added = library.Albums.OrderByDescending(a => a.Added).Take(16).Cast<object>().ToList();
        if (added.Count > 0)
            sections.Add(new HomeSection("Recently added", null, HomeSectionKind.Albums, added));

        var forgotten = library.Tracks
            .Select(t => (Track: t, Stats: userData.StatsFor(t.Path)))
            .Where(p => p.Stats is { Plays: >= 3, LastPlayed: { } last } && now - last > TimeSpan.FromDays(30))
            .OrderByDescending(p => p.Stats!.Plays).Take(20).Select(p => (object)p.Track).ToList();
        if (forgotten.Count >= 4)
            sections.Add(new HomeSection("Forgotten favorites", "Songs you loved but haven't played lately", HomeSectionKind.Tracks, forgotten));

        var topArtists = library.Artists
            .Select(a => (Artist: a, Plays: a.Tracks.Sum(t => userData.PlayCount(t.Path))))
            .Where(p => p.Plays > 0).OrderByDescending(p => p.Plays).Take(12).Select(p => (object)p.Artist).ToList();
        if (topArtists.Count >= 3)
            sections.Add(new HomeSection("Your top artists", null, HomeSectionKind.Artists, topArtists));

        var unplayed = library.Albums.Where(a => a.Tracks.All(t => userData.PlayCount(t.Path) == 0))
            .OrderBy(_ => random.Next()).Take(12).Cast<object>().ToList();
        if (unplayed.Count > 0)
            sections.Add(new HomeSection("Rediscover your library", "Albums you haven't played yet", HomeSectionKind.Albums, unplayed));

        if (topArtists.Count < 3)
        {
            var artists = library.Artists.Where(a => a.Albums.Count > 0).OrderBy(_ => random.Next()).Take(12).Cast<object>().ToList();
            if (artists.Count > 0)
                sections.Add(new HomeSection("Artists", null, HomeSectionKind.Artists, artists));
        }
        return sections;
    }

    /// <summary>Play frequency weighted by recency: each play counts half as much for every two weeks of age.</summary>
    private static Dictionary<string, double> Frecency(UserData userData, DateTime now)
    {
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var play in userData.History)
        {
            double age = Math.Max(0, (now - play.At).TotalDays);
            scores[play.Path] = scores.GetValueOrDefault(play.Path) + Math.Pow(0.5, age / 14);
        }
        return scores;
    }

    /// <summary>Favourites mixed with similar tracks you play less, or a sample of the library for new listeners.</summary>
    private static List<object> QuickPicks(LibrarySnapshot library, UserData userData, Recommender recommender,
        Dictionary<string, double> frecency, Random random)
    {
        const int Count = 12;
        var picks = new List<Track>(Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var recent = userData.History.TakeLast(8).Select(p => p.Path).ToHashSet(StringComparer.Ordinal);
        var favourites = frecency.Where(p => !recent.Contains(p.Key)).OrderByDescending(p => p.Value)
            .Select(p => library.FindTrack(p.Key)).OfType<Track>().Take(Count / 2).ToList();
        foreach (var track in favourites)
        {
            if (seen.Add(track.Path))
                picks.Add(track);
        }
        var seeds = favourites.Count > 0 ? favourites : library.Tracks.OrderBy(_ => random.Next()).Take(6).ToList();
        foreach (var track in recommender.Radio(seeds, Count * 2))
        {
            if (picks.Count >= Count)
                break;
            if (!recent.Contains(track.Path) && seen.Add(track.Path))
                picks.Add(track);
        }
        // Interleave favourites and discoveries instead of listing all favourites first.
        return picks.OrderBy(_ => random.Next()).Cast<object>().ToList();
    }

    private static List<object> RecentlyPlayedAlbums(LibrarySnapshot library, UserData userData, int count)
    {
        var albums = new List<object>(count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var history = userData.History;
        for (int i = history.Count - 1; i >= 0 && albums.Count < count; i--)
        {
            if (library.FindTrack(history[i].Path) is { AlbumKey: { } key } && seen.Add(key) && library.FindAlbum(key) is { } album)
                albums.Add(album);
        }
        return albums;
    }

    private static List<object> Mixes(LibrarySnapshot library, UserData userData, Recommender recommender,
        Dictionary<string, double> frecency, Random random)
    {
        var mixes = new List<object>();
        // Genres and artists ranked by listening, or by size for a library without history.
        double GenreWeight(Genre genre) => frecency.Count > 0 ? genre.Tracks.Sum(t => frecency.GetValueOrDefault(t.Path)) : genre.Tracks.Count;
        double ArtistWeight(Artist artist) => frecency.Count > 0 ? artist.Tracks.Sum(t => frecency.GetValueOrDefault(t.Path)) : artist.Tracks.Count;

        foreach (var genre in library.Genres.Where(g => g.Tracks.Count >= 4).OrderByDescending(GenreWeight).Take(3))
        {
            var tracks = recommender.Radio(genre.Tracks.OrderBy(_ => random.Next()).Take(8).ToList(), 40);
            if (tracks.Count >= 4)
                mixes.Add(new Mix($"{genre.Name} Mix", $"{genre.Name} and more from your library", tracks, Covers(library, tracks)));
        }
        foreach (var artist in library.Artists.Where(a => a.Tracks.Count >= 3).OrderByDescending(ArtistWeight).Take(2))
        {
            var tracks = recommender.Radio(artist.Tracks, 40);
            if (tracks.Count >= 4)
                mixes.Add(new Mix($"{artist.Name} Mix", $"{artist.Name} and similar artists", tracks, Covers(library, tracks)));
        }
        var liked = library.Tracks.Where(t => userData.IsLiked(t.Path)).ToList();
        if (liked.Count >= 5)
        {
            var tracks = recommender.Radio(liked, 50);
            mixes.Add(new Mix("Liked Mix", "Your liked songs and songs like them", tracks, Covers(library, tracks)));
        }
        var unheard = library.Tracks.Where(t => userData.PlayCount(t.Path) == 0).ToList();
        if (unheard.Count >= 10 && frecency.Count > 0)
        {
            var tracks = unheard.OrderBy(_ => random.Next()).Take(40).ToList();
            mixes.Add(new Mix("Discover Mix", "Songs from your library you haven't heard yet", tracks, Covers(library, tracks)));
        }
        return mixes;
    }

    /// <summary>Up to four distinct album covers for a mosaic.</summary>
    public static List<ArtSource> Covers(LibrarySnapshot library, IEnumerable<Track> tracks)
    {
        var covers = new List<ArtSource>(4);
        var albums = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (!albums.Add(track.AlbumKey ?? track.Path))
                continue;
            if (library.ArtOf(track) is { } art)
                covers.Add(art);
            if (covers.Count == 4)
                break;
        }
        return covers;
    }
}
