using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>Sequential access to a structure that may be split across container pages.</summary>
internal interface IPacketReader
{
    /// <summary>Fills <paramref name="buffer"/> or throws <see cref="InvalidDataException"/>.</summary>
    void ReadExactly(Span<byte> buffer);

    void Skip(long count);

    /// <summary>File offset of the next byte when the remaining data is stored contiguously, otherwise -1.</summary>
    long FileOffset { get; }
}

/// <summary>A contiguous byte range of a file.</summary>
internal sealed class RangeReader(ByteSource source, long position, long end) : IPacketReader
{
    public long FileOffset => position;

    public void ReadExactly(Span<byte> buffer)
    {
        if (position + buffer.Length > end)
            throw new InvalidDataException("Read past the end of a block.");
        source.ReadExactly(position, buffer);
        position += buffer.Length;
    }

    public void Skip(long count)
    {
        if (count < 0 || position + count > end)
            throw new InvalidDataException("Skip past the end of a block.");
        position += count;
    }
}

/// <summary>Vorbis comments, used by FLAC, Ogg Vorbis, Opus and Speex.</summary>
/// <remarks>Specification: https://xiph.org/vorbis/doc/v-comment.html (all integers little-endian).</remarks>
internal static class VorbisComment
{
    public static void Read(IPacketReader reader, AudioTags tags, TagReadOptions options)
    {
        Span<byte> word = stackalloc byte[4];
        reader.ReadExactly(word);
        reader.Skip(BinaryPrimitives.ReadUInt32LittleEndian(word)); // vendor string
        reader.ReadExactly(word);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(word);
        Span<byte> head = stackalloc byte[64];
        for (uint i = 0; i < count && i < 65536; i++)
        {
            reader.ReadExactly(word);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(word);
            if (length > TagLimits.MaxFieldBytes)
                throw new InvalidDataException("Comment field is too large.");

            // Read enough to see the field name, then decide whether the value is worth reading.
            int headLength = (int)Math.Min(length, (uint)head.Length);
            reader.ReadExactly(head[..headLength]);
            int equals = head[..headLength].IndexOf((byte)'=');
            if (equals <= 0)
            {
                reader.Skip(length - headLength);
                continue;
            }
            string name = Encoding.ASCII.GetString(head[..equals]);
            int valueLength = (int)length - equals - 1;
            bool isPicture = name.Equals("METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase);
            // Base64 pictures are never addressable in the file, so their bytes are decoded only on request.
            bool wanted = isPicture
                ? options.HasFlag(TagReadOptions.PictureData)
                : valueLength <= TagLimits.MaxTextBytes || TagFields.IsLargeFieldWanted(name, options);
            if (!wanted)
            {
                if (isPicture)
                    tags.OfferPicture(PictureFromBase64Prefix(head[(equals + 1)..headLength]));
                else if (name.Contains("LYRICS", StringComparison.OrdinalIgnoreCase))
                    tags.HasLyrics = true;
                reader.Skip(length - headLength);
                continue;
            }

            var value = new byte[valueLength];
            int alreadyRead = headLength - equals - 1;
            head[(equals + 1)..headLength].CopyTo(value);
            reader.ReadExactly(value.AsSpan(alreadyRead));
            if (isPicture)
                ReadPictureField(value, tags, options);
            else if (name.Equals("COVERART", StringComparison.OrdinalIgnoreCase))
                ReadLegacyCoverArt(value, tags, options);
            else
                TagFields.Apply(tags, name, TextDecoding.DecodeUtf8(value), options);
        }
    }

    private static void ReadPictureField(byte[] base64, AudioTags tags, TagReadOptions options)
    {
        byte[] block;
        try
        {
            block = Convert.FromBase64String(Encoding.ASCII.GetString(base64));
        }
        catch (FormatException)
        {
            return;
        }
        if (Flac.ParsePictureBlock(block, out string mime, out bool front, out int dataOffset, out int dataLength))
        {
            var picture = new EmbeddedPicture(mime, front, -1, dataLength);
            if (options.HasFlag(TagReadOptions.PictureData))
                picture = picture with { Data = block.AsSpan(dataOffset, dataLength).ToArray() };
            tags.OfferPicture(picture);
        }
    }

    /// <summary>Describes a picture from the first bytes of its Base64 value without decoding the whole image.</summary>
    private static EmbeddedPicture PictureFromBase64Prefix(ReadOnlySpan<byte> base64Prefix)
    {
        int usable = base64Prefix.Length / 4 * 4;
        Span<byte> decoded = stackalloc byte[48];
        bool front = false;
        if (System.Buffers.Text.Base64.DecodeFromUtf8(base64Prefix[..usable], decoded, out _, out int written) is
            System.Buffers.OperationStatus.Done && written >= 4)
        {
            front = BinaryPrimitives.ReadUInt32BigEndian(decoded) == 3;
        }
        return new EmbeddedPicture("image/unknown", front, -1, 0);
    }

    /// <summary>Reads the unofficial COVERART field.</summary>
    /// <remarks>COVERART holds a Base64 image, and COVERARTMIME holds its type.</remarks>
    private static void ReadLegacyCoverArt(byte[] base64, AudioTags tags, TagReadOptions options)
    {
        if (!options.HasFlag(TagReadOptions.PictureData))
        {
            tags.OfferPicture(new EmbeddedPicture("image/unknown", true, -1, base64.Length * 3 / 4));
            return;
        }
        try
        {
            byte[] image = Convert.FromBase64String(Encoding.ASCII.GetString(base64));
            tags.OfferPicture(new EmbeddedPicture("image/unknown", true, -1, image.Length) { Data = image });
        }
        catch (FormatException)
        {
        }
    }
}
