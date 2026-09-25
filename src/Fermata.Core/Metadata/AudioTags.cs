namespace Fermata.Metadata;

/// <summary>Tags and stream properties read from one audio file.</summary>
/// <remarks>
/// Format readers fill this incrementally; later values never overwrite earlier ones
/// (see <see cref="KeepFirst"/>), so a file's primary tag wins over fallback tags such
/// as ID3v1.
/// </remarks>
public sealed class AudioTags
{
    public string? Title { get; set; }

    /// <summary>The artist credit exactly as tagged, e.g. "Daft Punk feat. Pharrell Williams".</summary>
    public string? Artist { get; set; }

    /// <summary>
    /// Individual performers: explicit ARTISTS values, the separate values of a multi-valued
    /// artist field, or names split from the credit ("A feat. B"). Completed by <see cref="Complete"/>.
    /// </summary>
    public List<string> ArtistNames { get; } = [];

    /// <summary>Raw values of the artist field when a format stores several.</summary>
    internal List<string> ArtistValues { get; } = [];

    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public List<string> Genres { get; } = [];
    public string? Composer { get; set; }
    public int Year { get; set; }
    public int OriginalYear { get; set; }
    public int TrackNumber { get; set; }
    public int TrackCount { get; set; }
    public int DiscNumber { get; set; }
    public int DiscCount { get; set; }
    public bool Compilation { get; set; }

    public string? TitleSort { get; set; }
    public string? ArtistSort { get; set; }
    public string? AlbumSort { get; set; }
    public string? AlbumArtistSort { get; set; }
    public string? MusicBrainzAlbumId { get; set; }

    /// <summary>Unsynchronized lyrics, or LRC text for synchronized lyrics. Read only when requested.</summary>
    public string? Lyrics { get; set; }

    /// <summary>True when the file carries lyrics, whether or not they were read.</summary>
    public bool HasLyrics { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>Average bitrate in kilobits per second.</summary>
    public int Bitrate { get; set; }

    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitsPerSample { get; set; }
    public string Codec { get; set; } = "";

    /// <summary>True when the container also holds a real video track (a film or music video, not cover art).</summary>
    public bool HasVideo { get; set; }

    /// <summary>The preferred embedded picture (front cover if present).</summary>
    public EmbeddedPicture? Picture { get; set; }

    /// <summary>Duration hint from tags (ID3 TLEN) used only when the stream gives none.</summary>
    internal TimeSpan DurationHint { get; set; }

    internal void SetTitle(string? value) => Title = KeepFirst(Title, value);
    internal void SetArtist(string? value) => Artist = KeepFirst(Artist, value);
    internal void SetAlbumArtist(string? value) => AlbumArtist = KeepFirst(AlbumArtist, value);
    internal void SetAlbum(string? value) => Album = KeepFirst(Album, value);
    internal void SetComposer(string? value) => Composer = KeepFirst(Composer, value);

    /// <summary>Keeps an existing value; otherwise takes the trimmed candidate.</summary>
    internal static string? KeepFirst(string? current, string? candidate) =>
        string.IsNullOrWhiteSpace(current) && !string.IsNullOrWhiteSpace(candidate) ? candidate.Trim() : current;

    /// <summary>Derives values that depend on several fields once all tags have been read.</summary>
    internal void Complete()
    {
        if (ArtistNames.Count == 0)
        {
            if (ArtistValues.Count > 1)
            {
                foreach (string value in ArtistValues)
                    AddArtistName(value);
            }
            else if (Artist is not null)
            {
                foreach (string name in Text.ArtistCredit.Split(Artist))
                    AddArtistName(name);
            }
        }
        if (Year == 0 && OriginalYear != 0)
            Year = OriginalYear;
        if (Duration <= TimeSpan.Zero && DurationHint > TimeSpan.Zero)
            Duration = DurationHint;
    }

    internal void AddGenre(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        value = value.Trim();
        foreach (string existing in Genres)
        {
            if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                return;
        }
        Genres.Add(value);
    }

    internal void AddArtistName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        value = value.Trim();
        if (!ArtistNames.Contains(value, StringComparer.OrdinalIgnoreCase))
            ArtistNames.Add(value);
    }

    /// <summary>Records a picture, preferring a front cover over other picture types.</summary>
    internal void OfferPicture(EmbeddedPicture picture)
    {
        if (Picture is null || (!Picture.IsFrontCover && picture.IsFrontCover))
            Picture = picture;
    }

    internal void SetYear(int year)
    {
        if (Year == 0 && year is > 0 and < 10000)
            Year = year;
    }

    internal void SetOriginalYear(int year)
    {
        if (OriginalYear == 0 && year is > 0 and < 10000)
            OriginalYear = year;
    }

    internal void SetTrack(int number, int count)
    {
        if (TrackNumber == 0 && number > 0)
            TrackNumber = number;
        if (TrackCount == 0 && count > 0)
            TrackCount = count;
    }

    internal void SetDisc(int number, int count)
    {
        if (DiscNumber == 0 && number > 0)
            DiscNumber = number;
        if (DiscCount == 0 && count > 0)
            DiscCount = count;
    }
}

/// <summary>Location and type of a picture stored inside an audio file.</summary>
/// <param name="MimeType">Declared image type, e.g. <c>image/jpeg</c>.</param>
/// <param name="IsFrontCover">True for ID3/FLAC picture type 3, or when the format has no picture types.</param>
/// <param name="Offset">File offset of the raw image bytes, or -1 when they are encoded (Base64, unsynchronized) and must be decoded by re-reading the tag.</param>
/// <param name="Length">Length of the raw image bytes.</param>
public sealed record EmbeddedPicture(string MimeType, bool IsFrontCover, long Offset, int Length)
{
    /// <summary>Decoded image bytes when the picture could not be addressed directly, or when explicitly requested.</summary>
    public byte[]? Data { get; init; }
}
