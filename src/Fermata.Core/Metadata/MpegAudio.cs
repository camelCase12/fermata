using System.Buffers;
using System.Buffers.Binary;

namespace Fermata.Metadata;

/// <summary>MPEG-1/2/2.5 Layer I–III frame headers and VBR headers (Xing, Info, VBRI).</summary>
internal static class MpegAudio
{
    /// <summary>A decoded four-byte frame header.</summary>
    /// <param name="Version">1 = MPEG-1, 2 = MPEG-2, 25 = MPEG-2.5.</param>
    /// <param name="Layer">1, 2 or 3.</param>
    /// <param name="Bitrate">Bits per second.</param>
    internal readonly record struct FrameHeader(int Version, int Layer, int Bitrate, int SampleRate, bool Padding, bool Mono, bool HasCrc)
    {
        public int SamplesPerFrame => Layer switch
        {
            1 => 384,
            2 => 1152,
            _ => Version == 1 ? 1152 : 576,
        };

        public int Length => Layer == 1
            ? (12 * Bitrate / SampleRate + (Padding ? 1 : 0)) * 4
            : SamplesPerFrame / 8 * Bitrate / SampleRate + (Padding ? 1 : 0);

        /// <summary>Offset of a Xing/Info header from the frame start: header plus side information.</summary>
        public int SideInfoEnd => 4 + (Version == 1 ? (Mono ? 17 : 32) : (Mono ? 9 : 17));
    }

    private static readonly int[] BitratesV1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] BitratesV1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] BitratesV1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] BitratesV2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] BitratesV2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    public static bool TryParseHeader(ReadOnlySpan<byte> bytes, out FrameHeader header)
    {
        header = default;
        if (bytes.Length < 4 || bytes[0] != 0xFF || (bytes[1] & 0xE0) != 0xE0)
            return false;
        int versionBits = bytes[1] >> 3 & 3;
        int layerBits = bytes[1] >> 1 & 3;
        int bitrateIndex = bytes[2] >> 4;
        int sampleRateIndex = bytes[2] >> 2 & 3;
        if (versionBits == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
            return false;
        int version = versionBits switch { 3 => 1, 2 => 2, _ => 25 };
        int layer = 4 - layerBits;
        int[] table = version == 1
            ? layer switch { 1 => BitratesV1L1, 2 => BitratesV1L2, _ => BitratesV1L3 }
            : layer == 1 ? BitratesV2L1 : BitratesV2L23;
        int baseRate = sampleRateIndex switch { 0 => 44100, 1 => 48000, _ => 32000 };
        int sampleRate = version switch { 1 => baseRate, 2 => baseRate / 2, _ => baseRate / 4 };
        header = new FrameHeader(version, layer, table[bitrateIndex] * 1000, sampleRate,
            Padding: (bytes[2] & 0x02) != 0, Mono: (bytes[3] >> 6) == 3, HasCrc: (bytes[1] & 1) == 0);
        return true;
    }

    /// <summary>
    /// Finds the first frame in [<paramref name="start"/>, <paramref name="end"/>) whose successor is also a
    /// consistent frame header, which rules out false sync words inside junk data.
    /// </summary>
    public static long FindFirstFrame(ByteSource source, long start, long end, out FrameHeader header)
    {
        // Usually the audio begins right after the tags.
        Span<byte> bytes = stackalloc byte[4];
        if (source.Read(start, bytes) == 4 && TryParseHeader(bytes, out header) && IsFollowedByFrame(source, start, end, header))
            return start;

        // Otherwise skip padding, junk or a damaged tag.
        const int Window = 64 * 1024;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Window);
        try
        {
            int length = source.Read(start, buffer.AsSpan(0, (int)Math.Min(Window, Math.Max(0, end - start))));
            for (int i = 1; i + 4 <= length; i++)
            {
                if (buffer[i] == 0xFF && TryParseHeader(buffer.AsSpan(i, 4), out header) && IsFollowedByFrame(source, start + i, end, header))
                    return start + i;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        header = default;
        return -1;
    }

    private static bool IsFollowedByFrame(ByteSource source, long frameStart, long end, FrameHeader header)
    {
        long following = frameStart + header.Length;
        if (following + 4 > end)
            return true; // A single frame at the end of a tiny file.
        Span<byte> next = stackalloc byte[4];
        return source.Read(following, next) == 4 && TryParseHeader(next, out var second)
            && second.Version == header.Version && second.Layer == header.Layer && second.SampleRate == header.SampleRate;
    }

    /// <summary>Fills codec, duration, bitrate and format from the audio stream between <paramref name="start"/> and <paramref name="end"/>.</summary>
    public static bool ReadStreamInfo(ByteSource source, long start, long end, AudioTags tags)
    {
        long frameStart = FindFirstFrame(source, start, end, out var header);
        if (frameStart < 0)
            return false;
        tags.Codec = header.Layer == 3 ? "MP3" : header.Layer == 2 ? "MP2" : "MP1";
        tags.SampleRate = header.SampleRate;
        tags.Channels = header.Mono ? 1 : 2;

        long audioBytes = end - frameStart;
        Span<byte> frame = stackalloc byte[256];
        int read = source.Read(frameStart, frame);
        frame = frame[..read];

        long frames = ReadXingFrames(frame, header, out long xingBytes);
        if (frames <= 0)
            frames = ReadVbriFrames(frame, out xingBytes);
        if (frames > 0)
        {
            double seconds = (double)frames * header.SamplesPerFrame / header.SampleRate;
            tags.Duration = TimeSpan.FromSeconds(seconds);
            long bytes = xingBytes > 0 ? xingBytes : audioBytes;
            tags.Bitrate = seconds > 0 ? (int)Math.Round(bytes * 8 / seconds / 1000) : header.Bitrate / 1000;
        }
        else
        {
            // Constant bitrate: the stream length follows from its size.
            tags.Bitrate = header.Bitrate / 1000;
            tags.Duration = TimeSpan.FromSeconds(audioBytes * 8.0 / header.Bitrate);
        }
        return true;
    }

    /// <summary>Reads the frame count from a Xing ("Xing" for VBR, "Info" for CBR) header.</summary>
    private static long ReadXingFrames(ReadOnlySpan<byte> frame, FrameHeader header, out long bytes)
    {
        bytes = 0;
        // Some encoders count the optional CRC before the side information; try both placements.
        foreach (int offset in (ReadOnlySpan<int>)[header.SideInfoEnd, header.SideInfoEnd + 2])
        {
            if (offset + 16 > frame.Length)
                continue;
            var tag = frame.Slice(offset, 4);
            if (!tag.SequenceEqual("Xing"u8) && !tag.SequenceEqual("Info"u8))
                continue;
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(frame[(offset + 4)..]);
            int position = offset + 8;
            long frames = 0;
            if ((flags & 1) != 0)
            {
                frames = BinaryPrimitives.ReadUInt32BigEndian(frame[position..]);
                position += 4;
            }
            if ((flags & 2) != 0 && position + 4 <= frame.Length)
                bytes = BinaryPrimitives.ReadUInt32BigEndian(frame[position..]);
            return frames;
        }
        return 0;
    }

    /// <summary>Reads the frame count from a Fraunhofer VBRI header, which sits 32 bytes after the frame header.</summary>
    private static long ReadVbriFrames(ReadOnlySpan<byte> frame, out long bytes)
    {
        bytes = 0;
        const int Offset = 36;
        if (frame.Length < Offset + 18 || !frame.Slice(Offset, 4).SequenceEqual("VBRI"u8))
            return 0;
        bytes = BinaryPrimitives.ReadUInt32BigEndian(frame[(Offset + 10)..]);
        return BinaryPrimitives.ReadUInt32BigEndian(frame[(Offset + 14)..]);
    }
}
