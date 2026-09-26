using System.Text;
using Fermata.Text;

namespace Fermata.Library;

public sealed record SearchResults(
    string Query,
    IReadOnlyList<Track> Tracks,
    IReadOnlyList<Album> Albums,
    IReadOnlyList<Artist> Artists,
    IReadOnlyList<Genre> Genres,
    object? TopResult)
{
    public static SearchResults Empty(string query) => new(query, [], [], [], [], null);

    public bool IsEmpty => Tracks.Count == 0 && Albums.Count == 0 && Artists.Count == 0 && Genres.Count == 0;
}

/// <summary>Accent- and case-insensitive search over a library snapshot.</summary>
/// <remarks>
/// Every word of the query must occur in an item, at the start of a word or inside one. Matches at word
/// starts, in titles and on popular tracks rank higher.
/// </remarks>
public sealed class SearchIndex
{
    private readonly LibrarySnapshot library;
    private readonly string[] trackText;
    private readonly string[] albumText;
    private readonly string[] artistText;
    private readonly string[] genreText;

    public SearchIndex(LibrarySnapshot library)
    {
        this.library = library;
        var builder = new StringBuilder(128);
        trackText = new string[library.Tracks.Count];
        for (int i = 0; i < trackText.Length; i++)
        {
            var track = library.Tracks[i];
            builder.Clear();
            TextFolding.AppendFolded(builder, track.Title);
            builder.Append('\n');
            TextFolding.AppendFolded(builder, track.DisplayArtist);
            builder.Append('\n');
            TextFolding.AppendFolded(builder, track.AlbumTitle);
            trackText[i] = builder.ToString();
        }
        albumText = library.Albums.Select(a => TextFolding.Fold(a.Title) + "\n" + TextFolding.Fold(a.Artist)).ToArray();
        artistText = library.Artists.Select(a => TextFolding.Fold(a.Name)).ToArray();
        genreText = library.Genres.Select(g => TextFolding.Fold(g.Name)).ToArray();
    }

    public LibrarySnapshot Library => library;

    public SearchResults Search(string query, UserData? userData, int trackLimit = 200, int otherLimit = 50, CancellationToken cancellation = default)
    {
        string folded = TextFolding.Fold(query);
        string[] tokens = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return SearchResults.Empty(query);

        var tracks = Rank(trackText, tokens, folded, trackLimit, cancellation, index =>
        {
            // Popular and liked tracks surface first among similar matches.
            if (userData is null)
                return 0;
            string path = library.Tracks[index].Path;
            return Math.Log2(1 + userData.PlayCount(path)) * 0.6 + (userData.IsLiked(path) ? 1.5 : 0);
        });
        var albums = Rank(albumText, tokens, folded, otherLimit, cancellation, _ => 0);
        var artists = Rank(artistText, tokens, folded, otherLimit, cancellation, index => Math.Log2(1 + library.Artists[index].Tracks.Count) * 0.3);
        var genres = Rank(genreText, tokens, folded, otherLimit, cancellation, _ => 0);

        object? top = null;
        double best = double.MinValue;
        void Consider(object? item, double score)
        {
            if (item is not null && score > best)
            {
                best = score;
                top = item;
            }
        }
        // An exact name ranks above a partial one, and artists and albums win ties.
        if (artists.Count > 0)
            Consider(library.Artists[artists[0].Index], artists[0].Score + 1.0);
        if (albums.Count > 0)
            Consider(library.Albums[albums[0].Index], albums[0].Score + 0.5);
        if (tracks.Count > 0)
            Consider(library.Tracks[tracks[0].Index], tracks[0].Score);

        return new SearchResults(query,
            tracks.Select(m => library.Tracks[m.Index]).ToList(),
            albums.Select(m => library.Albums[m.Index]).ToList(),
            artists.Select(m => library.Artists[m.Index]).ToList(),
            genres.Select(m => library.Genres[m.Index]).ToList(),
            top);
    }

    /// <summary>Tracks containing every word of <paramref name="filter"/>, in library order (for filtering lists).</summary>
    public List<Track> FilterTracks(string filter) => Filter(trackText, filter, library.Tracks);

    public List<Album> FilterAlbums(string filter) => Filter(albumText, filter, library.Albums);

    public List<Artist> FilterArtists(string filter) => Filter(artistText, filter, library.Artists);

    private static List<T> Filter<T>(string[] texts, string filter, IReadOnlyList<T> items)
    {
        string[] tokens = TextFolding.Fold(filter).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return [.. items];
        var result = new List<T>();
        for (int i = 0; i < texts.Length; i++)
        {
            bool all = true;
            foreach (string token in tokens)
            {
                if (!texts[i].Contains(token, StringComparison.Ordinal))
                {
                    all = false;
                    break;
                }
            }
            if (all)
                result.Add(items[i]);
        }
        return result;
    }

    private readonly record struct Match(int Index, double Score);

    private static List<Match> Rank(string[] texts, string[] tokens, string query, int limit, CancellationToken cancellation, Func<int, double> bonus)
    {
        var matches = new List<Match>();
        for (int i = 0; i < texts.Length; i++)
        {
            if ((i & 4095) == 0)
                cancellation.ThrowIfCancellationRequested();
            string text = texts[i];
            // Every token must occur somewhere in the item.
            bool all = true;
            foreach (string token in tokens)
            {
                if (!text.Contains(token, StringComparison.Ordinal))
                {
                    all = false;
                    break;
                }
            }
            if (all)
                matches.Add(new Match(i, Score(text, tokens, query) + bonus(i)));
        }
        matches.Sort((a, b) => b.Score.CompareTo(a.Score) is var byScore and not 0 ? byScore : a.Index.CompareTo(b.Index));
        if (matches.Count > limit)
            matches.RemoveRange(limit, matches.Count - limit);
        return matches;
    }

    /// <summary>Scores a candidate against the query.</summary>
    /// <remarks>
    /// The fields of <paramref name="text"/> are separated by '\n' in order of importance, which is
    /// title, artist and album. Word-start matches count fully and inner matches partly, and a field equal
    /// to or beginning with the whole query earns a bonus.
    /// </remarks>
    internal static double Score(string text, string[] tokens, string query)
    {
        double score = 0;
        foreach (string token in tokens)
        {
            double best = 0;
            int field = 0, fieldStart = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n')
                    continue;
                var span = text.AsSpan(fieldStart, i - fieldStart);
                double weight = field switch { 0 => 3, 1 => 2, _ => 1 };
                int at = span.IndexOf(token, StringComparison.Ordinal);
                while (at >= 0)
                {
                    bool wordStart = at == 0 || span[at - 1] == ' ';
                    best = Math.Max(best, weight * (wordStart ? 1.0 : 0.35));
                    if (wordStart)
                        break;
                    int next = span[(at + 1)..].IndexOf(token, StringComparison.Ordinal);
                    at = next < 0 ? -1 : at + 1 + next;
                }
                field++;
                fieldStart = i + 1;
            }
            score += best;
        }
        // Whole-query bonuses on the first field (the name).
        int end = text.IndexOf('\n');
        var name = end < 0 ? text.AsSpan() : text.AsSpan(0, end);
        if (name.SequenceEqual(query))
            score += 6;
        else if (name.StartsWith(query, StringComparison.Ordinal))
            score += 3;
        // Shorter names are closer matches for the same words.
        foreach (string token in tokens)
        {
            if (name.Contains(token, StringComparison.Ordinal))
            {
                score -= name.Length * 0.004;
                break;
            }
        }
        return score;
    }
}
