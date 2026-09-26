using System.Globalization;
using Fermata.Metadata;

namespace Fermata.Library;

public readonly record struct LyricLine(TimeSpan Time, string Text);

/// <summary>Song lyrics, synchronized (with a time per line) or plain.</summary>
public sealed class Lyrics
{
    public Lyrics(IReadOnlyList<LyricLine> lines, bool synced, string source)
    {
        Lines = lines;
        IsSynced = synced;
        Source = source;
    }

    public IReadOnlyList<LyricLine> Lines { get; }
    public bool IsSynced { get; }

    /// <summary>Where the lyrics came from, either "file.lrc" or "embedded".</summary>
    public string Source { get; }

    /// <summary>Index of the line sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public int LineAt(TimeSpan position)
    {
        if (!IsSynced)
            return -1;
        int low = 0, high = Lines.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (Lines[middle].Time <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    /// <summary>Finds the lyrics of a track.</summary>
    /// <remarks>
    /// An .lrc file beside the track comes first, then lyrics embedded in its tags, then a .txt file
    /// beside it.
    /// </remarks>
    /// <returns>The lyrics, or null when there are none.</returns>
    public static Lyrics? Load(Track track)
    {
        string directory = track.Directory;
        string stem = Path.GetFileNameWithoutExtension(track.Path);
        string? lrc = FindSibling(directory, stem, ".lrc");
        if (lrc is not null)
        {
            var parsed = Parse(File.ReadAllText(lrc), Path.GetFileName(lrc));
            if (parsed is not null)
                return parsed;
        }
        if (track.HasLyrics)
        {
            var tags = TagReader.Read(track.Path, TagReadOptions.Lyrics);
            if (tags?.Lyrics is { Length: > 0 } text && Parse(text, "embedded") is { } embedded)
                return embedded;
        }
        string? txt = FindSibling(directory, stem, ".txt");
        return txt is not null ? Parse(File.ReadAllText(txt), Path.GetFileName(txt)) : null;
    }

    private static string? FindSibling(string directory, string stem, string extension)
    {
        string exact = Path.Combine(directory, stem + extension);
        if (File.Exists(exact))
            return exact;
        try
        {
            // Case-insensitive fallback ("Song.LRC").
            foreach (string candidate in Directory.EnumerateFiles(directory, "*" + extension, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
            {
                if (Path.GetFileNameWithoutExtension(candidate).Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>
    /// Parses LRC ("[01:23.45]line", several stamps per line, [offset:ms], enhanced &lt;word&gt; stamps)
    /// or, when no line carries a time stamp, plain text.
    /// </summary>
    public static Lyrics? Parse(string text, string source)
    {
        var timed = new List<LyricLine>();
        var plain = new List<LyricLine>();
        long offsetMilliseconds = 0;
        foreach (string rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.AsSpan().Trim();
            var times = new List<TimeSpan>(1);
            while (line.Length > 0 && line[0] == '[')
            {
                int close = line.IndexOf(']');
                if (close < 0)
                    break;
                var tag = line[1..close];
                if (TryParseTime(tag, out var time))
                    times.Add(time);
                else if (tag.StartsWith("offset:", StringComparison.OrdinalIgnoreCase))
                    long.TryParse(tag[7..].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out offsetMilliseconds);
                else if (times.Count == 0 && IsMetadataTag(tag))
                {
                    line = [];
                    break;
                }
                else
                {
                    break;
                }
                line = line[(close + 1)..];
            }
            string content = StripWordStamps(line.Trim());
            if (times.Count > 0)
            {
                foreach (var time in times)
                    timed.Add(new LyricLine(time, content));
            }
            else if (line.Length > 0 || plain.Count > 0)
            {
                plain.Add(new LyricLine(TimeSpan.Zero, content));
            }
        }
        if (timed.Count > 0)
        {
            // A positive offset shows lyrics earlier.
            var shift = TimeSpan.FromMilliseconds(-offsetMilliseconds);
            var lines = timed.Select(l => l with { Time = l.Time + shift < TimeSpan.Zero ? TimeSpan.Zero : l.Time + shift })
                .OrderBy(l => l.Time).ToList();
            return new Lyrics(lines, synced: true, source);
        }
        while (plain.Count > 0 && plain[^1].Text.Length == 0)
            plain.RemoveAt(plain.Count - 1);
        return plain.Count > 0 ? new Lyrics(plain, synced: false, source) : null;
    }

    /// <summary>Parses "mm:ss", "mm:ss.xx", "mm:ss.xxx" or "mm:ss:xx".</summary>
    internal static bool TryParseTime(ReadOnlySpan<char> text, out TimeSpan time)
    {
        time = default;
        int colon = text.IndexOf(':');
        if (colon <= 0 || !int.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            return false;
        var rest = text[(colon + 1)..];
        int separator = rest.IndexOfAny('.', ':');
        var secondsText = separator < 0 ? rest : rest[..separator];
        if (!int.TryParse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds > 59)
            return false;
        double fraction = 0;
        if (separator >= 0)
        {
            var digits = rest[(separator + 1)..];
            if (digits.Length == 0 || digits.Length > 3 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
                return false;
            fraction = value / Math.Pow(10, digits.Length);
        }
        time = TimeSpan.FromSeconds(minutes * 60 + seconds + fraction);
        return true;
    }

    private static bool IsMetadataTag(ReadOnlySpan<char> tag)
    {
        int colon = tag.IndexOf(':');
        return colon > 0 && colon <= 8 && tag[..colon].IndexOfAnyExceptInRange('a', 'z') < 0;
    }

    /// <summary>Removes enhanced-LRC word timings such as "&lt;00:12.34&gt;".</summary>
    private static string StripWordStamps(ReadOnlySpan<char> line)
    {
        if (line.IndexOf('<') < 0)
            return line.ToString();
        var builder = new System.Text.StringBuilder(line.Length);
        while (!line.IsEmpty)
        {
            int open = line.IndexOf('<');
            int close = open < 0 ? -1 : line[open..].IndexOf('>');
            if (open < 0 || close < 0 || !TryParseTime(line.Slice(open + 1, close - 1), out _))
            {
                builder.Append(line);
                break;
            }
            builder.Append(line[..open]);
            line = line[(open + close + 1)..];
        }
        return builder.ToString().Trim();
    }
}
