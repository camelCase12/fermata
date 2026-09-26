using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>Reads native FLAC files.</summary>
/// <remarks>
/// The STREAMINFO, VORBIS_COMMENT and PICTURE metadata blocks are read. Specification:
/// https://xiph.org/flac/format.html (RFC 9639).
/// </remarks>
internal static class Flac
{
    private const int StreamInfo = 0, VorbisCommentBlock = 4, Picture = 6;

    /// <summary>Reads a native FLAC stream whose "fLaC" marker is at <paramref name="start"/>.</summary>
    public static void Read(ByteSource source, long start, AudioTags tags, TagReadOptions options)
    {
        tags.Codec = "FLAC";
        long position = start + 4;
        Span<byte> header = stackalloc byte[4];
        Span<byte> info = stackalloc byte[34];
        long totalSamples = 0;
        bool last = false;
        while (!last && position + 4 <= source.Length)
        {
            source.ReadExactly(position, header);
            last = (header[0] & 0x80) != 0;
            int type = header[0] & 0x7F;
            int length = header[1] << 16 | header[2] << 8 | header[3];
            long body = position + 4;
            if (body + length > source.Length)
                break;
            switch (type)
            {
                case StreamInfo when length >= 34:
                    source.ReadExactly(body, info);
                    totalSamples = ReadStreamInfo(info, tags);
                    break;
                case VorbisCommentBlock:
                    VorbisComment.Read(new RangeReader(source, body, body + length), tags, options);
                    break;
                case Picture:
                    ReadPicture(source, body, length, tags, options);
                    break;
            }
            position = body + length;
        }

        if (tags.SampleRate > 0 && totalSamples > 0)
        {
            tags.Duration = TimeSpan.FromSeconds((double)totalSamples / tags.SampleRate);
            long audioBytes = source.Length - position;
            tags.Bitrate = (int)Math.Round(audioBytes * 8 / tags.Duration.TotalSeconds / 1000);
        }
    }

    /// <summary>Parses the 34-byte STREAMINFO block; returns the total sample count (0 when unknown).</summary>
    public static long ReadStreamInfo(ReadOnlySpan<byte> info, AudioTags tags)
    {
        // Bytes 10..17: sample rate (20 bits), channels - 1 (3), bits per sample - 1 (5), total samples (36).
        ulong packed = BinaryPrimitives.ReadUInt64BigEndian(info[10..]);
        tags.SampleRate = (int)(packed >> 44);
        tags.Channels = (int)(packed >> 41 & 0x7) + 1;
        tags.BitsPerSample = (int)(packed >> 36 & 0x1F) + 1;
        return (long)(packed & 0xFFFFFFFFF);
    }

    private static void ReadPicture(ByteSource source, long body, int length, AudioTags tags, TagReadOptions options)
    {
        // Only the fixed fields and strings are read; the image itself stays in the file.
        int prefixLength = Math.Min(length, 64 * 1024);
        byte[] prefix = source.ReadArray(body, prefixLength);
        if (!ParsePictureBlock(prefix, out string mime, out bool front, out int dataOffset, out int dataLength, declaredOnly: true))
            return;
        if (dataOffset + (long)dataLength > length)
            return;
        var picture = new EmbeddedPicture(mime, front, body + dataOffset, dataLength);
        if (options.HasFlag(TagReadOptions.PictureData))
            picture = picture with { Data = source.ReadArray(picture.Offset, dataLength) };
        tags.OfferPicture(picture);
    }

    /// <summary>
    /// Parses a PICTURE block (also used Base64-encoded in Vorbis comments). All integers are big-endian.
    /// With <paramref name="declaredOnly"/>, the image bytes may extend beyond <paramref name="block"/>.
    /// </summary>
    public static bool ParsePictureBlock(ReadOnlySpan<byte> block, out string mime, out bool front, out int dataOffset,
        out int dataLength, bool declaredOnly = false)
    {
        mime = "";
        front = false;
        dataOffset = dataLength = 0;
        if (block.Length < 32)
            return false;
        uint type = BinaryPrimitives.ReadUInt32BigEndian(block);
        uint mimeLength = BinaryPrimitives.ReadUInt32BigEndian(block[4..]);
        if (mimeLength > 256 || 8 + mimeLength + 4 > block.Length)
            return false;
        mime = Encoding.ASCII.GetString(block.Slice(8, (int)mimeLength)).ToLowerInvariant();
        int position = 8 + (int)mimeLength;
        uint descriptionLength = BinaryPrimitives.ReadUInt32BigEndian(block[position..]);
        if (descriptionLength > block.Length)
            return false;
        position += 4 + (int)descriptionLength + 16; // description, then width, height, depth, colours
        if (position + 4 > block.Length)
            return false;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(block[position..]);
        dataOffset = position + 4;
        if (length > TagLimits.MaxFieldBytes || (!declaredOnly && dataOffset + length > block.Length))
            return false;
        dataLength = (int)length;
        front = type == 3;
        if (mime is "" or "image/jpg")
            mime = "image/jpeg";
        return true;
    }
}
