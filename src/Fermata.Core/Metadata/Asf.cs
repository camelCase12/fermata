using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>Windows Media Audio (ASF container) header objects.</summary>
/// <remarks>Specification: Advanced Systems Format (ASF) Specification, revision 01.20.05.</remarks>
internal static class Asf
{
    public static readonly Guid HeaderObject = new("75B22630-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid FileProperties = new("8CABDCA1-A947-11CF-8EE4-00C00C205365");
    private static readonly Guid StreamProperties = new("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
    private static readonly Guid ContentDescription = new("75B22633-668E-11CF-A6D9-00AA0062CE6C");
    private static readonly Guid ExtendedContentDescription = new("D2D0A440-E307-11D2-97F0-00A0C95EA850");
    private static readonly Guid HeaderExtension = new("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
    private static readonly Guid MetadataLibrary = new("44231C94-9498-49D1-A141-1D134E457054");
    private static readonly Guid Metadata = new("C5F8CBEA-5BAF-4877-8467-AA8C44FA4CCA");
    private static readonly Guid AudioMedia = new("F8699E40-5B4D-11CF-A8FD-00805F5C442B");

    private enum ValueType : ushort { Unicode = 0, Bytes = 1, Bool = 2, DWord = 3, QWord = 4, Word = 5 }

    public static void Read(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        Span<byte> header = stackalloc byte[30];
        source.ReadExactly(0, header);
        long headerEnd = (long)BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        tags.Codec = "WMA";
        ReadObjects(source, 30, Math.Min(headerEnd, source.Length), tags, options);
    }

    private static void ReadObjects(ByteSource source, long position, long end, AudioTags tags, TagReadOptions options)
    {
        Span<byte> objectHeader = stackalloc byte[24];
        while (position + 24 <= end)
        {
            source.ReadExactly(position, objectHeader);
            var id = new Guid(objectHeader[..16]);
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(objectHeader[16..]);
            long body = position + 24;
            if (size < 24 || position + size > end)
                return;
            long bodyLength = size - 24;
            if (id == FileProperties)
                ReadFileProperties(source, body, tags);
            else if (id == StreamProperties)
                ReadStreamProperties(source, body, bodyLength, tags);
            else if (id == ContentDescription)
                ReadContentDescription(source, body, bodyLength, tags);
            else if (id == ExtendedContentDescription)
                ReadExtendedContent(source, body, body + bodyLength, tags, options);
            else if (id == HeaderExtension && bodyLength > 22)
                ReadObjects(source, body + 22, body + bodyLength, tags, options); // reserved GUID, reserved word, data size
            else if (id == MetadataLibrary || id == Metadata)
                ReadMetadataRecords(source, body, body + bodyLength, tags, options);
            position += size;
        }
    }

    private static void ReadFileProperties(ByteSource source, long body, AudioTags tags)
    {
        Span<byte> data = stackalloc byte[80];
        if (source.Read(body, data) < 80)
            return;
        // Play duration counts 100 ns units and includes the preroll, which is in milliseconds.
        ulong playDuration = BinaryPrimitives.ReadUInt64LittleEndian(data[40..]);
        ulong preroll = BinaryPrimitives.ReadUInt64LittleEndian(data[56..]);
        double seconds = playDuration / 1e7 - preroll / 1e3;
        if (seconds > 0)
            tags.Duration = TimeSpan.FromSeconds(seconds);
        uint maxBitrate = BinaryPrimitives.ReadUInt32LittleEndian(data[76..]);
        if (tags.Bitrate == 0 && maxBitrate > 0)
            tags.Bitrate = (int)(maxBitrate / 1000);
    }

    private static void ReadStreamProperties(ByteSource source, long body, long length, AudioTags tags)
    {
        if (length < 54 + 16)
            return;
        Span<byte> data = stackalloc byte[54 + 16];
        source.ReadExactly(body, data);
        if (new Guid(data[..16]) != AudioMedia)
            return;
        // WAVEFORMATEX follows the fixed 54-byte stream properties header.
        var format = data[54..];
        int formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        tags.Channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        tags.Bitrate = (int)(BinaryPrimitives.ReadUInt32LittleEndian(format[8..]) * 8 / 1000);
        tags.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        tags.Codec = formatTag switch
        {
            0x160 or 0x161 => "WMA",
            0x162 => "WMA Pro",
            0x163 => "WMA Lossless",
            0x55 => "MP3",
            _ => "WMA",
        };
        if (formatTag != 0x163)
            tags.BitsPerSample = 0;
    }

    private static void ReadContentDescription(ByteSource source, long body, long length, AudioTags tags)
    {
        if (length < 10 || length > TagLimits.MaxTextBytes)
            return;
        byte[] data = source.ReadArray(body, (int)length);
        int titleLength = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int authorLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        if (10 + titleLength + authorLength > data.Length)
            return;
        tags.SetTitle(Utf16(data.AsSpan(10, titleLength)));
        tags.SetArtist(Utf16(data.AsSpan(10 + titleLength, authorLength)));
    }

    private static void ReadExtendedContent(ByteSource source, long position, long end, AudioTags tags, TagReadOptions options)
    {
        Span<byte> word = stackalloc byte[2];
        source.ReadExactly(position, word);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(word);
        position += 2;
        for (int i = 0; i < count && position + 6 <= end; i++)
        {
            source.ReadExactly(position, word);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(word);
            if (position + 2 + nameLength + 4 > end)
                return;
            string name = Utf16(source.ReadArray(position + 2, nameLength));
            long typePosition = position + 2 + nameLength;
            source.ReadExactly(typePosition, word);
            var type = (ValueType)BinaryPrimitives.ReadUInt16LittleEndian(word);
            source.ReadExactly(typePosition + 2, word);
            int valueLength = BinaryPrimitives.ReadUInt16LittleEndian(word);
            long valueStart = typePosition + 4;
            if (valueStart + valueLength > end)
                return;
            ApplyValue(source, name, type, valueStart, valueLength, tags, options);
            position = valueStart + valueLength;
        }
    }

    /// <summary>Metadata and Metadata Library objects: records with 32-bit lengths, which can hold large pictures.</summary>
    private static void ReadMetadataRecords(ByteSource source, long position, long end, AudioTags tags, TagReadOptions options)
    {
        Span<byte> record = stackalloc byte[12];
        source.ReadExactly(position, record[..2]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(record);
        position += 2;
        for (int i = 0; i < count && position + 12 <= end; i++)
        {
            source.ReadExactly(position, record);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var type = (ValueType)BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            long valueLength = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            long nameStart = position + 12;
            long valueStart = nameStart + nameLength;
            if (valueStart + valueLength > end)
                return;
            string name = Utf16(source.ReadArray(nameStart, nameLength));
            if (valueLength <= int.MaxValue)
                ApplyValue(source, name, type, valueStart, (int)valueLength, tags, options);
            position = valueStart + valueLength;
        }
    }

    private static void ApplyValue(ByteSource source, string name, ValueType type, long start, int length, AudioTags tags, TagReadOptions options)
    {
        if (name == "WM/Picture" && type == ValueType.Bytes)
        {
            ReadPicture(source, start, length, tags, options);
            return;
        }
        if (name == "WM/Lyrics")
            tags.HasLyrics = true;
        if (length > TagLimits.MaxTextBytes && !TagFields.IsLargeFieldWanted(name, options))
            return;
        byte[] data = source.ReadArray(start, length);
        string? value = type switch
        {
            ValueType.Unicode => Utf16(data),
            ValueType.DWord when data.Length >= 4 => BinaryPrimitives.ReadUInt32LittleEndian(data).ToString(),
            ValueType.QWord when data.Length >= 8 => BinaryPrimitives.ReadUInt64LittleEndian(data).ToString(),
            ValueType.Word when data.Length >= 2 => BinaryPrimitives.ReadUInt16LittleEndian(data).ToString(),
            ValueType.Bool when data.Length >= 2 => data[0] != 0 ? "1" : "0",
            _ => null,
        };
        if (value is null)
            return;
        if (name == "WM/Track" && int.TryParse(value, out int zeroBased))
            tags.SetTrack(zeroBased + 1, 0); // WM/Track counts from zero; WM/TrackNumber is preferred when present
        else
            TagFields.Apply(tags, name, value, options);
    }

    /// <summary>WM/Picture: type(1), data length(4), MIME (UTF-16, NUL), description (UTF-16, NUL), image.</summary>
    private static void ReadPicture(ByteSource source, long start, int length, AudioTags tags, TagReadOptions options)
    {
        int prefixLength = Math.Min(length, 1024);
        byte[] prefix = source.ReadArray(start, prefixLength);
        if (prefix.Length < 5)
            return;
        byte type = prefix[0];
        int dataLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(1));
        int position = 5;
        int mimeEnd = TextDecoding.IndexOfTerminator(prefix.AsSpan(position), TagTextEncoding.Utf16);
        if (mimeEnd < 0)
            return;
        string mime = Utf16(prefix.AsSpan(position, mimeEnd));
        position += mimeEnd + 2;
        int descriptionEnd = TextDecoding.IndexOfTerminator(prefix.AsSpan(position), TagTextEncoding.Utf16);
        if (descriptionEnd < 0)
            return;
        position += descriptionEnd + 2;
        if (position + (long)dataLength > length)
            return;
        var picture = new EmbeddedPicture(mime.Length > 0 ? mime.ToLowerInvariant() : "image/jpeg", type == 3, start + position, dataLength);
        if (options.HasFlag(TagReadOptions.PictureData))
            picture = picture with { Data = source.ReadArray(picture.Offset, dataLength) };
        tags.OfferPicture(picture);
    }

    private static string Utf16(ReadOnlySpan<byte> bytes) => Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]).TrimEnd('\0');
}
