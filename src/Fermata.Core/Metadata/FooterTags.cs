using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>ID3v1 and APEv2 tags, both stored at the end of a file.</summary>
internal static class FooterTags
{
    public const int Id3v1Size = 128;
    private const int ApeFooterSize = 32;

    /// <summary>
    /// Reads the tags at the end of the file (APEv2, then ID3v1 as a fallback) and
    /// returns the offset where they begin, i.e. the end of the audio data.
    /// </summary>
    public static long Read(ByteSource source, AudioTags tags, TagReadOptions options, long end)
    {
        long id3v1 = HasId3v1(source, end) ? end - Id3v1Size : -1;
        if (id3v1 >= 0)
            end = id3v1;
        end = ReadApe(source, end, tags, options);
        if (id3v1 >= 0)
            ReadId3v1(source, id3v1, tags);
        return end;
    }

    public static bool HasId3v1(ByteSource source, long end) =>
        end >= Id3v1Size && source.Matches(end - Id3v1Size, "TAG"u8);

    /// <summary>Reads an ID3v1 or ID3v1.1 tag; its fields only fill gaps left by richer tags.</summary>
    public static void ReadId3v1(ByteSource source, long offset, AudioTags tags)
    {
        Span<byte> tag = stackalloc byte[Id3v1Size];
        source.ReadExactly(offset, tag);
        tags.SetTitle(Field(tag.Slice(3, 30)));
        tags.SetArtist(Field(tag.Slice(33, 30)));
        tags.SetAlbum(Field(tag.Slice(63, 30)));
        tags.SetYear(TextDecoding.Year(Field(tag.Slice(93, 4))));
        // ID3v1.1 stores the track number in the last comment byte after a zero byte.
        if (tag[125] == 0 && tag[126] != 0)
            tags.SetTrack(tag[126], 0);
        if (tags.Genres.Count == 0 && Genres.FromIndex(tag[127]) is { } genre)
            tags.AddGenre(genre);
    }

    private static string Field(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0)
            bytes = bytes[..end];
        return Encoding.Latin1.GetString(bytes).Trim();
    }

    /// <summary>Reads an APEv2 tag ending at <paramref name="end"/>, returning where it starts (or <paramref name="end"/>).</summary>
    /// <remarks>Specification: https://wiki.hydrogenaud.io/index.php?title=APEv2_specification</remarks>
    public static long ReadApe(ByteSource source, long end, AudioTags tags, TagReadOptions options)
    {
        if (end < ApeFooterSize)
            return end;
        Span<byte> footer = stackalloc byte[ApeFooterSize];
        source.ReadExactly(end - ApeFooterSize, footer);
        if (!footer[..8].SequenceEqual("APETAGEX"u8))
            return end;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(footer[12..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(footer[20..]);
        if (size < ApeFooterSize || size > end || size > TagLimits.MaxFieldBytes)
            return end;
        long itemsStart = end - size;
        bool hasHeader = (flags & 0x80000000) != 0;
        long tagStart = hasHeader ? itemsStart - ApeFooterSize : itemsStart;
        ReadApeItems(source, itemsStart, end - ApeFooterSize, (int)Math.Min(count, 4096), tags, options);
        return Math.Max(0, tagStart);
    }

    /// <summary>Reads APEv2 items between <paramref name="position"/> and <paramref name="end"/>.</summary>
    public static void ReadApeItems(ByteSource source, long position, long end, int count, AudioTags tags, TagReadOptions options)
    {
        Span<byte> itemHeader = stackalloc byte[8];
        Span<byte> keyBuffer = stackalloc byte[256];
        for (int i = 0; i < count && position + 9 < end; i++)
        {
            source.ReadExactly(position, itemHeader);
            uint valueSize = BinaryPrimitives.ReadUInt32LittleEndian(itemHeader);
            uint itemFlags = BinaryPrimitives.ReadUInt32LittleEndian(itemHeader[4..]);
            int keyRead = source.Read(position + 8, keyBuffer);
            int keyLength = keyBuffer[..keyRead].IndexOf((byte)0);
            if (keyLength < 1)
                return;
            string key = Encoding.ASCII.GetString(keyBuffer[..keyLength]);
            long valueStart = position + 8 + keyLength + 1;
            if (valueSize > end - valueStart)
                return;
            int kind = (int)(itemFlags >> 1) & 3;
            if (key.Equals("Lyrics", StringComparison.OrdinalIgnoreCase))
                tags.HasLyrics = true;
            if (kind == 1 && key.StartsWith("Cover Art", StringComparison.OrdinalIgnoreCase))
                ReadApePicture(source, key, valueStart, (int)valueSize, tags, options);
            else if (kind == 0 && (valueSize <= TagLimits.MaxTextBytes || TagFields.IsLargeFieldWanted(key, options)))
            {
                byte[] value = source.ReadArray(valueStart, (int)valueSize);
                foreach (var part in TextDecoding.DecodeList(value, TagTextEncoding.Utf8))
                    TagFields.Apply(tags, key, part, options);
            }
            position = valueStart + valueSize;
        }
    }

    private static void ReadApePicture(ByteSource source, string key, long start, int size, AudioTags tags, TagReadOptions options)
    {
        // Binary cover items hold "filename\0" followed by the image.
        Span<byte> prefix = stackalloc byte[Math.Min(size, 256)];
        source.ReadExactly(start, prefix);
        int nameEnd = prefix.IndexOf((byte)0);
        if (nameEnd < 0)
            return;
        string name = Encoding.UTF8.GetString(prefix[..nameEnd]);
        string mime = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
        long dataStart = start + nameEnd + 1;
        int dataLength = size - nameEnd - 1;
        if (dataLength <= 0)
            return;
        var picture = new EmbeddedPicture(mime, key.Contains("Front", StringComparison.OrdinalIgnoreCase), dataStart, dataLength);
        if (options.HasFlag(TagReadOptions.PictureData))
            picture = picture with { Data = source.ReadArray(dataStart, dataLength) };
        tags.OfferPicture(picture);
    }
}
