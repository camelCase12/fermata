using System.Buffers.Binary;

namespace Fermata.Metadata;

/// <summary>
/// Stream properties of formats tagged with APEv2 (WavPack, Monkey's Audio, Musepack, TTA) and of DSF.
/// Their tags are read separately from the end of the file.
/// </summary>
internal static class LosslessFormats
{
    private static readonly int[] MusepackRates = [44100, 48000, 37800, 32000];

    private static readonly int[] WavPackRates =
        [6000, 8000, 9600, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000, 64000, 88200, 96000, 192000];

    /// <summary>WavPack block header ("wvpk"). Specification: https://www.wavpack.com/WavPack5FileFormat.pdf</summary>
    public static void ReadWavPack(ByteSource source, AudioTags tags, long audioEnd)
    {
        Span<byte> header = stackalloc byte[32];
        source.ReadExactly(0, header);
        tags.Codec = "WavPack";
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
        long totalSamples = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        if (totalSamples == uint.MaxValue)
            totalSamples = -1;
        else
            totalSamples += (long)header[11] << 32; // high byte of the 40-bit sample count
        tags.BitsPerSample = ((int)(flags & 3) + 1) * 8;
        tags.Channels = (flags & 4) != 0 ? 1 : 2;
        int rateIndex = (int)(flags >> 23 & 0xF);
        tags.SampleRate = rateIndex < WavPackRates.Length ? WavPackRates[rateIndex] : 0;
        SetDuration(tags, totalSamples, audioEnd);
    }

    /// <summary>Monkey's Audio ("MAC "), both the current descriptor layout (3.98+) and the older one.</summary>
    public static void ReadMonkeysAudio(ByteSource source, AudioTags tags, long audioEnd)
    {
        tags.Codec = "Monkey's Audio";
        Span<byte> data = stackalloc byte[76];
        int read = source.Read(0, data);
        if (read < 32)
            return;
        int version = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        uint blocksPerFrame, finalFrameBlocks, totalFrames;
        if (version >= 3980)
        {
            uint descriptorBytes = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            Span<byte> header = stackalloc byte[24];
            if (source.Read(descriptorBytes, header) < 24)
                return;
            blocksPerFrame = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            tags.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(header[16..]);
            tags.Channels = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
            tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        }
        else
        {
            int compression = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            int formatFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
            tags.Channels = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
            totalFrames = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]);
            finalFrameBlocks = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]);
            tags.BitsPerSample = (formatFlags & 1) != 0 ? 8 : (formatFlags & 8) != 0 ? 24 : 16;
            blocksPerFrame = version >= 3950 ? 73728 * 4 : version >= 3900 || (version >= 3800 && compression == 4000) ? 73728u : 9216u;
        }
        if (totalFrames > 0)
            SetDuration(tags, (long)(totalFrames - 1) * blocksPerFrame + finalFrameBlocks, audioEnd);
    }

    /// <summary>True Audio ("TTA1"): format, channels, bits, sample rate, sample count.</summary>
    public static void ReadTta(ByteSource source, long start, AudioTags tags, long audioEnd)
    {
        Span<byte> header = stackalloc byte[18];
        if (source.Read(start, header) < 18)
            return;
        tags.Codec = "TTA";
        tags.Channels = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        tags.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]);
        tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(header[10..]);
        SetDuration(tags, BinaryPrimitives.ReadUInt32LittleEndian(header[14..]), audioEnd);
    }

    /// <summary>Musepack stream version 7 ("MP+") and 8 ("MPCK").</summary>
    public static void ReadMusepack(ByteSource source, AudioTags tags, long audioEnd)
    {
        tags.Codec = "Musepack";
        Span<byte> data = stackalloc byte[64];
        int read = source.Read(0, data);
        data = data[..read];
        if (data.StartsWith("MP+"u8) && read >= 12)
        {
            uint frames = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            tags.SampleRate = MusepackRates[flags >> 16 & 3];
            tags.Channels = 2;
            SetDuration(tags, frames * 1152L, audioEnd);
            return;
        }
        // SV8: packets of key(2), variable-length size, payload. The stream header ("SH") holds the sample count.
        int position = 4;
        while (position + 3 < data.Length)
        {
            int packetStart = position;
            bool isStreamHeader = data[position] == 'S' && data[position + 1] == 'H';
            position += 2;
            long size = ReadVarint(data, ref position); // includes the key and this size field
            int payload = position;
            if (isStreamHeader)
            {
                payload += 4 + 1; // CRC and stream version
                long samples = ReadVarint(data, ref payload);
                long silence = ReadVarint(data, ref payload);
                if (payload + 2 <= data.Length)
                {
                    tags.SampleRate = MusepackRates[data[payload] >> 5 & 3];
                    tags.Channels = (data[payload + 1] >> 4) + 1;
                }
                SetDuration(tags, samples - silence, audioEnd);
                return;
            }
            if (size < 3)
                return;
            position = (int)Math.Min(int.MaxValue, packetStart + size);
        }
    }

    private static long ReadVarint(ReadOnlySpan<byte> data, ref int position)
    {
        long value = 0;
        while (position < data.Length)
        {
            byte b = data[position++];
            value = value << 7 | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                break;
        }
        return value;
    }

    /// <summary>
    /// Sony DSD stream files: a "DSD " chunk pointing to an ID3v2 tag at the end, then "fmt ".
    /// Specification: https://dsd-guide.com/sites/default/files/white-papers/DSFFileFormatSpec_E.pdf
    /// </summary>
    public static void ReadDsf(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        Span<byte> data = stackalloc byte[80];
        if (source.Read(0, data) < 80 || !data[28..32].SequenceEqual("fmt "u8))
            return;
        tags.Codec = "DSD";
        long metadata = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[20..]);
        tags.Channels = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[56..]);
        tags.BitsPerSample = 1;
        long samples = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[64..]);
        SetDuration(tags, samples, metadata > 0 ? metadata : source.Length);
        if (metadata > 0 && metadata < source.Length)
            Id3v2.Read(source, metadata, tags, options);
    }

    private static void SetDuration(AudioTags tags, long samples, long audioBytes)
    {
        if (samples <= 0 || tags.SampleRate <= 0)
            return;
        tags.Duration = TimeSpan.FromSeconds((double)samples / tags.SampleRate);
        tags.Bitrate = (int)Math.Round(audioBytes * 8 / tags.Duration.TotalSeconds / 1000);
    }
}
