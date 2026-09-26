using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>Reads MPEG-4 audio files (M4A, M4B, MP4).</summary>
/// <remarks>
/// The iTunes-style metadata items and the sound track's format are read. Box layout per ISO/IEC 14496-12;
/// metadata items per Apple's QuickTime File Format documentation.
/// </remarks>
internal static class Mp4
{
    private readonly record struct Box(uint Type, long Start, long DataStart, long End);

    private static uint FourCC(ReadOnlySpan<byte> type) => BinaryPrimitives.ReadUInt32BigEndian(type);
    private static uint FourCC(string type) => (uint)type[0] << 24 | (uint)type[1] << 16 | (uint)type[2] << 8 | type[3];

    private static readonly uint Moov = FourCC("moov"), Mvhd = FourCC("mvhd"), Trak = FourCC("trak"), Mdia = FourCC("mdia"),
        Mdhd = FourCC("mdhd"), Hdlr = FourCC("hdlr"), Minf = FourCC("minf"), Stbl = FourCC("stbl"), Stsd = FourCC("stsd"),
        Udta = FourCC("udta"), Meta = FourCC("meta"), Ilst = FourCC("ilst"), Data = FourCC("data"),
        Name = FourCC("name"), Freeform = FourCC("----"), Esds = FourCC("esds"), Mvex = FourCC("mvex"), Mehd = FourCC("mehd"),
        Sound = FourCC("soun"), Video = FourCC("vide");

    public static void Read(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        Box? moov = FindChild(source, 0, source.Length, Moov);
        if (moov is not { } movie)
            throw new InvalidDataException("MP4 file has no movie box.");

        double movieSeconds = 0;
        foreach (var box in Children(source, movie.DataStart, movie.End))
        {
            if (box.Type == Mvhd)
                movieSeconds = ReadMediaDuration(source, box);
            else if (box.Type == Trak)
                ReadTrack(source, box, tags);
            else if (box.Type == Udta)
            {
                if (FindChild(source, box.DataStart, box.End, Meta) is { } meta)
                    ReadMeta(source, meta, tags, options);
            }
            else if (box.Type == Meta)
                ReadMeta(source, box, tags, options);
            else if (box.Type == Mvex && movieSeconds <= 0 && FindChild(source, box.DataStart, box.End, Mehd) is { } mehd)
                movieSeconds = ReadFragmentDuration(source, mehd, movie);
        }
        if (tags.Duration <= TimeSpan.Zero && movieSeconds > 0)
            tags.Duration = TimeSpan.FromSeconds(movieSeconds);
        if (tags.Bitrate == 0 && tags.Duration > TimeSpan.Zero)
            tags.Bitrate = (int)Math.Round(source.Length * 8 / tags.Duration.TotalSeconds / 1000);
        if (tags.Codec.Length == 0)
            tags.Codec = "AAC";
    }

    /// <summary>Reads format and duration from the first sound track.</summary>
    private static void ReadTrack(ByteSource source, Box trak, AudioTags tags)
    {
        if (FindChild(source, trak.DataStart, trak.End, Mdia) is not { } mdia
            || FindChild(source, mdia.DataStart, mdia.End, Hdlr) is not { } hdlr)
            return;
        uint handler = HandlerType(source, hdlr);
        if (handler == Video)
            tags.HasVideo = true; // cover art lives in the covr item, never in a video track
        if (handler != Sound || tags.Codec.Length > 0)
            return;
        if (FindChild(source, mdia.DataStart, mdia.End, Mdhd) is { } mdhd)
        {
            double seconds = ReadMediaDuration(source, mdhd);
            if (seconds > 0)
                tags.Duration = TimeSpan.FromSeconds(seconds);
        }
        if (FindChild(source, mdia.DataStart, mdia.End, Minf) is { } minf
            && FindChild(source, minf.DataStart, minf.End, Stbl) is { } stbl
            && FindChild(source, stbl.DataStart, stbl.End, Stsd) is { } stsd)
        {
            ReadSampleDescription(source, stsd, tags);
        }
    }

    /// <summary>The handler type of a track: "soun", "vide", "text"…</summary>
    private static uint HandlerType(ByteSource source, Box hdlr)
    {
        Span<byte> data = stackalloc byte[12];
        return hdlr.End - hdlr.DataStart >= 12 && source.Read(hdlr.DataStart, data) == 12 ? FourCC(data[8..]) : 0;
    }

    /// <summary>Reads the duration from an mvhd or mdhd box.</summary>
    /// <remarks>The box holds the version, flags and times, then the timescale and duration.</remarks>
    private static double ReadMediaDuration(ByteSource source, Box box)
    {
        Span<byte> data = stackalloc byte[32];
        int read = source.Read(box.DataStart, data[..(int)Math.Min(32, box.End - box.DataStart)]);
        if (read < 20)
            return 0;
        uint timescale;
        ulong duration;
        if (data[0] == 1)
        {
            if (read < 32)
                return 0;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
            duration = BinaryPrimitives.ReadUInt64BigEndian(data[24..]);
        }
        else
        {
            timescale = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
            duration = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        }
        // All-ones durations mean "unknown".
        if (timescale == 0 || duration == 0 || duration == uint.MaxValue || duration == ulong.MaxValue)
            return 0;
        return (double)duration / timescale;
    }

    /// <summary>Reads the duration of a fragmented file from its mvex/mehd box.</summary>
    /// <remarks>The duration is in the movie timescale.</remarks>
    private static double ReadFragmentDuration(ByteSource source, Box mehd, Box movie)
    {
        if (FindChild(source, movie.DataStart, movie.End, Mvhd) is not { } mvhd)
            return 0;
        Span<byte> header = stackalloc byte[24];
        if (source.Read(mvhd.DataStart, header) < 24)
            return 0;
        uint timescale = header[0] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(header[20..]) : BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        Span<byte> data = stackalloc byte[12];
        if (timescale == 0 || source.Read(mehd.DataStart, data) < 8)
            return 0;
        ulong duration = data[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(data[4..]) : BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        return (double)duration / timescale;
    }

    /// <summary>Reads the first audio sample entry (mp4a, alac, fLaC, Opus, ac-3…).</summary>
    private static void ReadSampleDescription(ByteSource source, Box stsd, AudioTags tags)
    {
        // Full box header (4) and entry count (4), then the first sample entry box.
        long entryStart = stsd.DataStart + 8;
        if (!TryReadBoxHeader(source, entryStart, stsd.End, out var entry))
            return;
        string type = Latin1Key(entry.Type);
        tags.Codec = type switch
        {
            "mp4a" => "AAC",
            "alac" => "ALAC",
            "fLaC" => "FLAC",
            "Opus" => "Opus",
            "ac-3" => "AC-3",
            "ec-3" => "E-AC-3",
            ".mp3" => "MP3",
            _ => type.Trim(),
        };
        // AudioSampleEntry: reserved(6), data reference index(2), version(2), revision(2), vendor(4),
        // channel count(2), sample size(2), compression id(2), packet size(2), sample rate (16.16 fixed).
        Span<byte> data = stackalloc byte[28];
        if (source.Read(entry.DataStart, data) < 28)
            return;
        tags.Channels = BinaryPrimitives.ReadUInt16BigEndian(data[16..]);
        tags.BitsPerSample = BinaryPrimitives.ReadUInt16BigEndian(data[18..]);
        tags.SampleRate = (int)(BinaryPrimitives.ReadUInt32BigEndian(data[24..]) >> 16);
        int version = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        // QuickTime sound description versions 1 and 2 append extra fields before the child boxes.
        long childStart = entry.DataStart + 28 + version switch { 1 => 16, 2 => 36, _ => 0 };
        if (tags.Codec == "AAC")
        {
            tags.BitsPerSample = 0; // Lossy formats have no meaningful sample size.
            if (FindChild(source, childStart, entry.End, Esds) is { } esds)
                ReadEsds(source, esds, tags);
        }
        else if (tags.Codec == "ALAC" && FindChild(source, childStart, entry.End, FourCC("alac")) is { } alac)
        {
            // ALACSpecificConfig after the full box header: frame length(4), compatible version(1), bit depth(1),
            // pb(1), mb(1), kb(1), channels(1), max run(2), max frame bytes(4), average bitrate(4), sample rate(4).
            Span<byte> config = stackalloc byte[28];
            if (source.Read(alac.DataStart, config) >= 28)
            {
                tags.BitsPerSample = config[9];
                tags.Channels = config[13];
                tags.SampleRate = (int)BinaryPrimitives.ReadUInt32BigEndian(config[24..]);
            }
        }
        else if (tags.Codec is "Opus" or "AC-3" or "E-AC-3" or "MP3")
        {
            tags.BitsPerSample = 0;
        }
    }

    /// <summary>Reads the average bitrate and audio object type (HE-AAC detection) from an ES descriptor.</summary>
    private static void ReadEsds(ByteSource source, Box esds, AudioTags tags)
    {
        int length = (int)Math.Min(esds.End - esds.DataStart, 256);
        byte[] data = source.ReadArray(esds.DataStart, length);
        int position = 4; // full box header
        while (position < data.Length)
        {
            byte tag = data[position++];
            int size = 0;
            for (int i = 0; i < 4 && position < data.Length; i++)
            {
                byte b = data[position++];
                size = size << 7 | b & 0x7F;
                if ((b & 0x80) == 0)
                    break;
            }
            switch (tag)
            {
                case 0x03: // ES_Descriptor: ES_ID(2), flags(1), then optional fields per flags.
                {
                    if (position + 3 > data.Length)
                        return;
                    byte flags = data[position + 2];
                    position += 3;
                    if ((flags & 0x80) != 0)
                        position += 2;
                    if ((flags & 0x40) != 0 && position < data.Length)
                        position += 1 + data[position];
                    if ((flags & 0x20) != 0)
                        position += 2;
                    continue; // children follow
                }
                case 0x04: // DecoderConfigDescriptor: object type(1), stream type(1), buffer size(3), max(4), average bitrate(4).
                {
                    if (position + 13 > data.Length)
                        return;
                    uint average = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 9));
                    if (average > 0)
                        tags.Bitrate = (int)Math.Round(average / 1000.0);
                    position += 13;
                    continue; // DecoderSpecificInfo follows
                }
                case 0x05: // AudioSpecificConfig: 5-bit audio object type.
                {
                    if (position < data.Length)
                    {
                        int objectType = data[position] >> 3;
                        if (objectType is 5 or 29)
                            tags.Codec = objectType == 29 ? "HE-AACv2" : "HE-AAC";
                    }
                    return;
                }
                default:
                    position += size;
                    break;
            }
        }
    }

    private static void ReadMeta(ByteSource source, Box meta, AudioTags tags, TagReadOptions options)
    {
        // "meta" is a full box in ISO files but a plain container in QuickTime files; detect which.
        long start = meta.DataStart;
        Span<byte> probe = stackalloc byte[8];
        if (source.Read(start, probe) == 8 && FourCC(probe[4..]) != Hdlr && FourCC(probe[4..]) != Ilst)
            start += 4;
        if (FindChild(source, start, meta.End, Ilst) is not { } ilst)
            return;
        foreach (var item in Children(source, ilst.DataStart, ilst.End))
            ReadItem(source, item, tags, options);
    }

    private static void ReadItem(ByteSource source, Box item, AudioTags tags, TagReadOptions options)
    {
        if (item.Type == Freeform)
        {
            ReadFreeform(source, item, tags, options);
            return;
        }
        string key = Latin1Key(item.Type);
        foreach (var data in Children(source, item.DataStart, item.End))
        {
            if (data.Type != Data || data.End - data.DataStart < 8)
                continue;
            long valueStart = data.DataStart + 8; // type indicator (4) and locale (4)
            int valueLength = (int)Math.Min(data.End - valueStart, int.MaxValue);
            uint dataType = source.ReadUInt32BigEndian(data.DataStart) & 0xFFFFFF;
            if (key == "covr")
            {
                string mime = dataType switch { 14 => "image/png", 27 => "image/bmp", _ => "image/jpeg" };
                var picture = new EmbeddedPicture(mime, IsFrontCover: true, valueStart, valueLength);
                if (options.HasFlag(TagReadOptions.PictureData))
                    picture = picture with { Data = source.ReadArray(valueStart, valueLength) };
                tags.OfferPicture(picture);
                return; // The first cover is the front cover by convention.
            }
            if (key == "©lyr")
            {
                tags.HasLyrics = true;
                if (!options.HasFlag(TagReadOptions.Lyrics))
                    return;
            }
            else if (valueLength > TagLimits.MaxTextBytes)
            {
                continue;
            }
            byte[] value = source.ReadArray(valueStart, valueLength);
            ApplyItem(key, dataType, value, tags);
            return;
        }
    }

    private static void ApplyItem(string key, uint dataType, byte[] value, AudioTags tags)
    {
        switch (key)
        {
            case "trkn" when value.Length >= 6:
                tags.SetTrack(BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(4)));
                return;
            case "disk" when value.Length >= 6:
                tags.SetDisc(BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(4)));
                return;
            case "gnre" when value.Length >= 2:
                // ID3v1 genre index plus one.
                tags.AddGenre(Genres.FromIndex(BinaryPrimitives.ReadUInt16BigEndian(value) - 1));
                return;
            case "cpil" when value.Length >= 1:
                tags.Compilation |= value[^1] != 0;
                return;
        }
        string text = dataType == 2 ? Encoding.BigEndianUnicode.GetString(value) : TextDecoding.DecodeUtf8(value);
        switch (key)
        {
            case "©nam":
                tags.SetTitle(text);
                break;
            case "©ART":
                tags.SetArtist(text);
                break;
            case "aART":
                tags.SetAlbumArtist(text);
                break;
            case "©alb":
                tags.SetAlbum(text);
                break;
            case "©day":
                tags.SetYear(TextDecoding.Year(text));
                break;
            case "©gen":
                foreach (string genre in Genres.Split(text))
                    tags.AddGenre(genre);
                break;
            case "©wrt":
                tags.SetComposer(text);
                break;
            case "©lyr":
                tags.Lyrics = AudioTags.KeepFirst(tags.Lyrics, text);
                break;
            case "sonm":
                tags.TitleSort = AudioTags.KeepFirst(tags.TitleSort, text);
                break;
            case "soar":
                tags.ArtistSort = AudioTags.KeepFirst(tags.ArtistSort, text);
                break;
            case "soal":
                tags.AlbumSort = AudioTags.KeepFirst(tags.AlbumSort, text);
                break;
            case "soaa":
                tags.AlbumArtistSort = AudioTags.KeepFirst(tags.AlbumArtistSort, text);
                break;
        }
    }

    /// <summary>Reads a freeform item ("----").</summary>
    /// <remarks>A freeform item carries a mean (namespace), a name and data, such as MusicBrainz identifiers.</remarks>
    private static void ReadFreeform(ByteSource source, Box item, AudioTags tags, TagReadOptions options)
    {
        string? name = null;
        foreach (var child in Children(source, item.DataStart, item.End))
        {
            if (child.Type == Name && child.End - child.DataStart > 4 && child.End - child.DataStart < 1024)
                name = TextDecoding.DecodeUtf8(source.ReadArray(child.DataStart + 4, (int)(child.End - child.DataStart - 4)));
            else if (child.Type == Data && name is not null && child.End - child.DataStart >= 8)
            {
                int length = (int)(child.End - child.DataStart - 8);
                if (length <= TagLimits.MaxTextBytes || TagFields.IsLargeFieldWanted(name, options))
                    TagFields.Apply(tags, name, TextDecoding.DecodeUtf8(source.ReadArray(child.DataStart + 8, length)), options);
            }
        }
    }

    private static string Latin1Key(uint type)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, type);
        return Encoding.Latin1.GetString(bytes);
    }

    private static Box? FindChild(ByteSource source, long start, long end, uint type)
    {
        foreach (var box in Children(source, start, end))
        {
            if (box.Type == type)
                return box;
        }
        return null;
    }

    private static IEnumerable<Box> Children(ByteSource source, long start, long end)
    {
        long position = start;
        int guard = 0;
        while (position + 8 <= end && guard++ < 100_000 && TryReadBoxHeader(source, position, end, out var box))
        {
            yield return box;
            position = box.End;
        }
    }

    private static bool TryReadBoxHeader(ByteSource source, long position, long end, out Box box)
    {
        box = default;
        Span<byte> header = stackalloc byte[16];
        int read = source.Read(position, header[..(int)Math.Min(16, end - position)]);
        if (read < 8)
            return false;
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        uint type = FourCC(header[4..]);
        long dataStart = position + 8;
        if (size == 1)
        {
            if (read < 16)
                return false;
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            dataStart += 8;
        }
        else if (size == 0)
        {
            size = end - position; // extends to the end of the enclosing box
        }
        if (size < dataStart - position || position + size > end)
            return false;
        box = new Box(type, position, dataStart, position + size);
        return true;
    }
}
