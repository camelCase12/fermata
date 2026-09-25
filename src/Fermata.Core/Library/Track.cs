using Fermata.Metadata;
using Fermata.Text;

namespace Fermata.Library;

/// <summary>One audio file and its metadata. Immutable; a rescan that sees a change creates a new instance.</summary>
public sealed class Track
{
    private string? displayArtist; // built on first use; most tracks of a large library are never shown

    public required string Path { get; init; }
    public long FileSize { get; init; }

    /// <summary>Last write time (UTC) when the file was read; with <see cref="FileSize"/> it detects changes.</summary>
    public DateTime Modified { get; init; }

    /// <summary>When the file first appeared in the library (UTC).</summary>
    public DateTime Added { get; init; }

    public required string Title { get; init; }

    /// <summary>The artist credit as tagged; empty when unknown.</summary>
    public string Artist { get; init; } = "";

    /// <summary>Individual performers, e.g. both names of "A feat. B".</summary>
    public IReadOnlyList<string> ArtistNames { get; init; } = [];

    public string AlbumArtist { get; init; } = "";
    public string AlbumTitle { get; init; } = "";
    public IReadOnlyList<string> Genres { get; init; } = [];
    public string Composer { get; init; } = "";
    public int Year { get; init; }
    public int TrackNumber { get; init; }
    public int TrackCount { get; init; }
    public int DiscNumber { get; init; }
    public int DiscCount { get; init; }
    public bool Compilation { get; init; }
    public string? TitleSort { get; init; }
    public string? ArtistSort { get; init; }
    public string? AlbumSort { get; init; }
    public string? AlbumArtistSort { get; init; }
    public string? MusicBrainzAlbumId { get; init; }

    public TimeSpan Duration { get; init; }
    public int Bitrate { get; init; }
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public string Codec { get; init; } = "";
    public bool HasLyrics { get; init; }

    /// <summary>Embedded cover art location, if any.</summary>
    public ArtSource? EmbeddedArt { get; init; }

    /// <summary>Groups the track into an album (see <see cref="AlbumGrouping"/>); null for tracks without an album tag.</summary>
    public string? AlbumKey { get; init; }

    /// <summary>The performer credit as tagged, or the album artist when a track names none.</summary>
    public string Credit => Artist.Length > 0 ? Artist : AlbumArtist.Length > 0 ? AlbumArtist : UnknownArtist;

    /// <summary>The credit as shown: list separators from tags ("A; B") read as "A &amp; B".</summary>
    public string DisplayArtist => displayArtist ??= ArtistCredit.Display(Credit);
    public string Genre => Genres.Count > 0 ? Genres[0] : "";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public bool IsLossless => Codec is "FLAC" or "ALAC" or "WavPack" or "Monkey's Audio" or "TTA" or "PCM" or "WMA Lossless";

    public const string UnknownArtist = "Unknown Artist";
    public const string UnknownAlbum = "Unknown Album";

    /// <summary>Human-readable format summary, e.g. "FLAC · 24-bit / 96 kHz" or "MP3 · 320 kbps".</summary>
    public string FormatDescription
    {
        get
        {
            string rate = SampleRate % 1000 == 0 ? $"{SampleRate / 1000} kHz" : $"{SampleRate / 1000.0:0.#} kHz";
            if (IsLossless && BitsPerSample > 0 && SampleRate > 0)
                return $"{Codec} · {BitsPerSample}-bit / {rate}";
            if (Bitrate > 0)
                return $"{Codec} · {Bitrate} kbps";
            return Codec;
        }
    }

    public override string ToString() => $"{DisplayArtist} — {Title}";

    /// <summary>Creates a track from tags, falling back to the file name when tags are missing.</summary>
    public static Track FromTags(string path, AudioTags? tags, long size, DateTime modified, DateTime added, StringPool pool,
        Func<string, string>? internKey = null)
    {
        tags ??= new AudioTags();
        string title = tags.Title ?? "";
        string artist = tags.Artist ?? "";
        int trackNumber = tags.TrackNumber;
        if (title.Length == 0)
            (title, artist, trackNumber) = FileNameGuess(path, artist, trackNumber);

        string albumArtist = pool.Get(tags.AlbumArtist ?? "");
        string album = pool.Get(tags.Album ?? "");
        var embedded = tags.Picture is { } picture ? new ArtSource(path, picture.Offset, picture.Length) : null;
        return new Track
        {
            Path = path,
            FileSize = size,
            Modified = modified,
            Added = added,
            Title = title,
            Artist = pool.Get(artist),
            ArtistNames = tags.ArtistNames.Count > 0 ? pool.GetAll(tags.ArtistNames) : artist.Length > 0 ? pool.GetAll(ArtistCredit.Split(artist)) : [],
            AlbumArtist = albumArtist,
            AlbumTitle = album,
            Genres = pool.GetAll(tags.Genres),
            Composer = pool.Get(tags.Composer ?? ""),
            Year = tags.Year,
            TrackNumber = trackNumber,
            TrackCount = tags.TrackCount,
            DiscNumber = tags.DiscNumber,
            DiscCount = tags.DiscCount,
            Compilation = tags.Compilation,
            TitleSort = tags.TitleSort,
            ArtistSort = pool.GetOrNull(tags.ArtistSort),
            AlbumSort = pool.GetOrNull(tags.AlbumSort),
            AlbumArtistSort = pool.GetOrNull(tags.AlbumArtistSort),
            MusicBrainzAlbumId = pool.GetOrNull(tags.MusicBrainzAlbumId),
            Duration = tags.Duration,
            Bitrate = tags.Bitrate,
            SampleRate = tags.SampleRate,
            Channels = tags.Channels,
            BitsPerSample = tags.BitsPerSample,
            Codec = pool.Get(tags.Codec),
            HasLyrics = tags.HasLyrics,
            EmbeddedArt = embedded,
            AlbumKey = AlbumGrouping.Key(path, album, tags.MusicBrainzAlbumId) is { } key ? internKey?.Invoke(key) ?? pool.Get(key) : null,
        };
    }

    /// <summary>
    /// Guesses a title from names like "03 - Artist - Title.mp3", "03. Title.flac" or "Artist - Title.ogg".
    /// </summary>
    internal static (string Title, string Artist, int TrackNumber) FileNameGuess(string path, string artist, int trackNumber)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim();
        int digits = 0;
        while (digits < name.Length && digits < 3 && char.IsAsciiDigit(name[digits]))
            digits++;
        if (digits is > 0 and <= 3 && digits < name.Length && name[digits] is ' ' or '.' or '-')
        {
            if (trackNumber == 0)
                trackNumber = int.Parse(name.AsSpan(0, digits));
            name = name[digits..].TrimStart(' ', '.', '-').Trim();
        }
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash + 3 < name.Length)
        {
            if (artist.Length == 0)
                artist = name[..dash].Trim();
            name = name[(dash + 3)..].Trim();
        }
        return (name.Length > 0 ? name : System.IO.Path.GetFileName(path), artist, trackNumber);
    }
}

/// <summary>
/// Where to find cover art: an image file, or a picture embedded in an audio file.
/// </summary>
/// <param name="Path">The image file, or the audio file containing the picture.</param>
/// <param name="Offset">
/// <see cref="ImageFileOffset"/> for an image file; the byte offset of the raw image inside the audio file;
/// or -1 when the picture must be decoded from the tag (Base64 or unsynchronized data).
/// </param>
/// <param name="Length">Length of the raw image when <paramref name="Offset"/> is non-negative.</param>
public sealed record ArtSource(string Path, long Offset, int Length)
{
    public const long ImageFileOffset = -2;

    public static ArtSource ImageFile(string path) => new(path, ImageFileOffset, 0);

    public bool IsImageFile => Offset == ImageFileOffset;
}

/// <summary>
/// Interns repeated metadata strings (artists, albums, genres, codecs) so that a large
/// library holds each distinct value once. Not thread-safe; use one pool per thread.
/// </summary>
public sealed class StringPool
{
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> singles = new(StringComparer.Ordinal);

    public string Get(string value)
    {
        if (value.Length == 0)
            return "";
        if (strings.TryGetValue(value, out string? existing))
            return existing;
        strings[value] = value;
        return value;
    }

    public string? GetOrNull(string? value) => string.IsNullOrEmpty(value) ? null : Get(value);

    public IReadOnlyList<string> GetAll(IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
            return [];
        if (values.Count == 1)
        {
            // Most tracks have one artist and one genre: share one list per value.
            string value = Get(values.First());
            if (!singles.TryGetValue(value, out string[]? single))
                singles[value] = single = [value];
            return single;
        }
        var result = new string[values.Count];
        int i = 0;
        foreach (string value in values)
            result[i++] = Get(value);
        return result;
    }
}
