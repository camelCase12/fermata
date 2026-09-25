namespace Fermata.Metadata;

/// <summary>Reads tags and stream properties from audio files, identifying the format by its content.</summary>
public static class TagReader
{
    /// <summary>Extensions scanned into the library (lowercase, with the dot).</summary>
    public static readonly FrozenExtensionSet AudioExtensions = new(
        ".mp3", ".mp2", ".flac", ".ogg", ".oga", ".opus", ".spx", ".m4a", ".m4b", ".mp4", ".aac", ".alac",
        ".wav", ".wave", ".aif", ".aiff", ".aifc", ".wv", ".ape", ".mpc", ".tta", ".wma", ".asf", ".mka",
        ".webm", ".dsf");

    /// <summary>
    /// Reads <paramref name="path"/>. Returns null for unrecognized content. I/O errors propagate;
    /// malformed tags yield whatever was read before the damage.
    /// </summary>
    public static AudioTags? Read(string path, TagReadOptions options = TagReadOptions.None)
    {
        using var source = new FileByteSource(path);
        return Read(source, options);
    }

    internal static AudioTags? Read(ByteSource source, TagReadOptions options)
    {
        var tags = new AudioTags();
        try
        {
            if (!ReadFormat(source, tags, options))
                return null;
        }
        catch (InvalidDataException)
        {
            // A damaged structure after the useful parts is common; keep what was found.
            if (tags.Codec.Length == 0 && tags.Title is null)
                return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            if (tags.Codec.Length == 0 && tags.Title is null)
                return null;
        }
        tags.Complete();
        return tags;
    }

    /// <summary>Reads only the embedded picture's bytes (decoding Base64 or unsynchronized data as needed).</summary>
    public static byte[]? ReadPicture(string path)
    {
        using var source = new FileByteSource(path);
        var tags = Read(source, TagReadOptions.PictureData);
        return tags?.Picture?.Data;
    }

    private static bool ReadFormat(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        Span<byte> magic = stackalloc byte[16];
        int length = source.Read(0, magic);
        magic = magic[..length];
        if (length < 4)
            return false;

        if (magic.StartsWith("fLaC"u8))
        {
            Flac.Read(source, 0, tags, options);
            return true;
        }
        if (magic.StartsWith("OggS"u8))
        {
            Ogg.Read(source, tags, options);
            return true;
        }
        if (length >= 12 && magic[4..8].SequenceEqual("ftyp"u8))
        {
            Mp4.Read(source, tags, options);
            return true;
        }
        if (length >= 12 && (magic.StartsWith("RIFF"u8) || magic.StartsWith("RF64"u8)) && magic[8..12].SequenceEqual("WAVE"u8))
        {
            RiffAiff.ReadWav(source, tags, options);
            return true;
        }
        if (length >= 12 && magic.StartsWith("FORM"u8) && (magic[8..12].SequenceEqual("AIFF"u8) || magic[8..12].SequenceEqual("AIFC"u8)))
        {
            RiffAiff.ReadAiff(source, tags, options);
            return true;
        }
        if (magic.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]))
        {
            Matroska.Read(source, tags, options);
            return true;
        }
        if (length >= 16 && new Guid(magic) == Asf.HeaderObject)
        {
            Asf.Read(source, tags, options);
            return true;
        }
        if (magic.StartsWith("DSD "u8))
        {
            LosslessFormats.ReadDsf(source, tags, options);
            return true;
        }
        if (magic.StartsWith("wvpk"u8))
        {
            long end = FooterTags.Read(source, tags, options, source.Length);
            LosslessFormats.ReadWavPack(source, tags, end);
            return true;
        }
        if (magic.StartsWith("MAC "u8))
        {
            long end = FooterTags.Read(source, tags, options, source.Length);
            LosslessFormats.ReadMonkeysAudio(source, tags, end);
            return true;
        }
        if (magic.StartsWith("MPCK"u8) || magic.StartsWith("MP+"u8))
        {
            long end = FooterTags.Read(source, tags, options, source.Length);
            LosslessFormats.ReadMusepack(source, tags, end);
            return true;
        }
        return ReadTaggedStream(source, tags, options);
    }

    /// <summary>
    /// Streams that may begin with ID3v2 tags: MP3, ADTS AAC, TTA, and FLAC files that were given
    /// an (unofficial) ID3 prefix.
    /// </summary>
    private static bool ReadTaggedStream(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        long start = 0;
        // Several consecutive ID3v2 tags occur in files that were re-tagged by careless tools.
        for (int i = 0; i < 4; i++)
        {
            long size = Id3v2.Read(source, start, tags, options);
            if (size == 0)
                break;
            start += size;
        }
        Span<byte> magic = stackalloc byte[4];
        if (source.Read(start, magic) < 4)
            return start > 0;
        if (magic.SequenceEqual("fLaC"u8))
        {
            Flac.Read(source, start, tags, options);
            return true;
        }
        long end = FooterTags.Read(source, tags, options, source.Length);
        if (magic.SequenceEqual("TTA1"u8))
        {
            LosslessFormats.ReadTta(source, start, tags, end - start);
            return true;
        }
        if (magic[0] == 0xFF && (magic[1] & 0xF6) == 0xF0)
        {
            Adts.Read(source, start, end, tags);
            return true;
        }
        return MpegAudio.ReadStreamInfo(source, start, end, tags) || start > 0;
    }
}

/// <summary>A small immutable set of file extensions, compared case-insensitively.</summary>
public sealed class FrozenExtensionSet(params string[] extensions)
{
    private readonly HashSet<string> set = new(extensions, StringComparer.OrdinalIgnoreCase);

    public bool Contains(ReadOnlySpan<char> extension) => set.GetAlternateLookup<ReadOnlySpan<char>>().Contains(extension);

    public IReadOnlyCollection<string> Items => set;
}
