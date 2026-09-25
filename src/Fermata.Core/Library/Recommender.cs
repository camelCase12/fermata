using Fermata.Playback;
using Fermata.Text;

namespace Fermata.Library;

/// <summary>
/// Builds radio stations, autoplay continuations and mixes from the local library and listening history.
/// </summary>
/// <remarks>
/// Similarity comes from shared artists, album artists and genres and from release years; liked and
/// often-played tracks are favoured, disliked ones never chosen, and recently played or often skipped
/// ones held back. Candidates are then drawn at random in proportion to their score, with limits per
/// artist and album, so a station feels varied rather than sorted.
/// </remarks>
public sealed class Recommender
{
    private readonly LibrarySnapshot library;
    private readonly UserData userData;
    private readonly Random random;

    public Recommender(LibrarySnapshot library, UserData userData, Random? random = null)
    {
        this.library = library;
        this.userData = userData;
        this.random = random ?? Random.Shared;
    }

    /// <summary>A station around one track: the track first, then similar ones.</summary>
    public List<Track> Radio(Track seed, int count = 50)
    {
        var result = new List<Track>(count) { seed };
        result.AddRange(Draw([seed], count - 1, exclude: new HashSet<string>(StringComparer.Ordinal) { seed.Path }));
        return result;
    }

    /// <summary>A station around several tracks (an album, an artist, a playlist).</summary>
    public List<Track> Radio(IReadOnlyList<Track> seeds, int count = 50)
    {
        if (seeds.Count == 0)
            return [];
        // A handful of representative seeds keeps scoring cheap for large collections.
        var sample = seeds.Count <= 8 ? seeds : seeds.OrderBy(_ => random.Next()).Take(8).ToList();
        return Draw(sample, count, exclude: new HashSet<string>());
    }

    /// <summary>Tracks to continue with after the queue ends, similar to what has just played.</summary>
    public List<Track> ContinueQueue(PlayQueue queue, int count = 10)
    {
        var entries = queue.Entries;
        if (entries.Count == 0)
            return [];
        int end = Math.Max(queue.CurrentIndex, 0);
        var seeds = new List<Track>();
        for (int i = end; i >= 0 && seeds.Count < 5; i--)
            seeds.Add(entries[i].Track);
        var queued = entries.Select(e => e.Track.Path).ToHashSet(StringComparer.Ordinal);
        return Draw(seeds, count, queued);
    }

    private List<Track> Draw(IReadOnlyList<Track> seeds, int count, IReadOnlySet<string> exclude)
    {
        var profile = new Profile(seeds);
        var recent = RecentlyPlayed(50);
        var candidates = new List<(Track Track, double Weight)>();
        foreach (var track in library.Tracks)
        {
            if (exclude.Contains(track.Path) || userData.IsDisliked(track.Path))
                continue;
            double similarity = profile.Similarity(track);
            if (similarity <= 0.2)
                continue;
            var stats = userData.StatsFor(track.Path);
            double score = similarity;
            if (stats is not null)
            {
                score += Math.Log2(1 + stats.Plays) * 0.4 + (stats.Liked is not null ? 1.5 : 0);
                score -= Math.Min(stats.Skips, 5) * 0.3;
            }
            if (recent.Contains(track.Path))
                score -= 2.5;
            if (score > 0)
                candidates.Add((track, Math.Exp(score * 0.9)));
        }
        return WeightedSample(candidates, count);
    }

    /// <summary>Draws without replacement, proportionally to weight, capping tracks per artist and album.</summary>
    private List<Track> WeightedSample(List<(Track Track, double Weight)> candidates, int count)
    {
        var result = new List<Track>(count);
        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
        var perAlbum = new Dictionary<string, int>(StringComparer.Ordinal);
        // Efraimidis–Spirakis keys give weighted sampling without replacement in one sort.
        var keyed = candidates.Select(c => (c.Track, Key: Math.Log(random.NextDouble() + 1e-12) / c.Weight))
            .OrderByDescending(c => c.Key);
        foreach (var (track, _) in keyed)
        {
            string artist = TextFolding.Fold(track.ArtistNames.Count > 0 ? track.ArtistNames[0] : track.DisplayArtist);
            string album = track.AlbumKey ?? track.Path;
            if (perArtist.GetValueOrDefault(artist) >= 3 || perAlbum.GetValueOrDefault(album) >= 2)
                continue;
            perArtist[artist] = perArtist.GetValueOrDefault(artist) + 1;
            perAlbum[album] = perAlbum.GetValueOrDefault(album) + 1;
            result.Add(track);
            if (result.Count == count)
                break;
        }
        return result;
    }

    private HashSet<string> RecentlyPlayed(int count)
    {
        var history = userData.History;
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = history.Count - 1; i >= 0 && set.Count < count; i--)
            set.Add(history[i].Path);
        return set;
    }

    /// <summary>What a set of seed tracks has in common, for scoring candidates against it.</summary>
    private sealed class Profile
    {
        private readonly Dictionary<string, double> artists = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> genres = new(StringComparer.Ordinal);
        private readonly HashSet<string> albums = new(StringComparer.Ordinal);
        private readonly double year;

        public Profile(IReadOnlyList<Track> seeds)
        {
            double share = 1.0 / Math.Max(1, seeds.Count);
            int years = 0;
            double yearSum = 0;
            foreach (var seed in seeds)
            {
                foreach (string name in Names(seed))
                    artists[name] = artists.GetValueOrDefault(name) + share;
                foreach (string genre in seed.Genres)
                    genres[TextFolding.Fold(genre)] = genres.GetValueOrDefault(TextFolding.Fold(genre)) + share;
                if (seed.AlbumKey is not null)
                    albums.Add(seed.AlbumKey);
                if (seed.Year > 0)
                {
                    yearSum += seed.Year;
                    years++;
                }
            }
            year = years > 0 ? yearSum / years : 0;
        }

        public double Similarity(Track track)
        {
            double score = 0;
            foreach (string name in Names(track))
                score += 3 * Math.Min(1, artists.GetValueOrDefault(name) * 2);
            foreach (string genre in track.Genres)
                score += 2 * Math.Min(1, genres.GetValueOrDefault(TextFolding.Fold(genre)) * 2);
            if (track.AlbumKey is not null && albums.Contains(track.AlbumKey))
                score += 0.5;
            if (year > 0 && track.Year > 0)
                score += 1.5 * Math.Max(0, 1 - Math.Abs(track.Year - year) / 12);
            return score;
        }

        private static IEnumerable<string> Names(Track track)
        {
            foreach (string name in track.ArtistNames)
                yield return TextFolding.Fold(name);
            if (track.AlbumArtist.Length > 0 && !track.Compilation)
                yield return TextFolding.Fold(track.AlbumArtist);
        }
    }
}
