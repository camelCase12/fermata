using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>WAV (RIFF and RF64) and AIFF/AIFC files: PCM formats with chunked metadata.</summary>
internal static class RiffAiff
{
    public static void ReadWav(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        bool rf64 = source.Matches(0, "RF64"u8);
        long position = 12;
        long dataSize64 = -1;
        int byteRate = 0;
        long dataSize = 0;
        Span<byte> header = stackalloc byte[8];
        Span<byte> ds64 = stackalloc byte[16];
        while (position + 8 <= source.Length)
        {
            source.ReadExactly(position, header);
            string id = Encoding.ASCII.GetString(header[..4]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long body = position + 8;
            if (id == "data" && rf64 && size == uint.MaxValue && dataSize64 >= 0)
                size = dataSize64;
            long available = Math.Min(size, source.Length - body);
            switch (id)
            {
                case "ds64" when available >= 24:
                    // RF64 keeps the real 64-bit sizes here: RIFF size, data size, sample count.
                    source.ReadExactly(body, ds64);
                    dataSize64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(ds64[8..]);
                    break;
                case "fmt " when available >= 16:
                    byteRate = ReadWaveFormat(source, body, (int)Math.Min(available, 64), tags);
                    break;
                case "data":
                    dataSize = available;
                    break;
                case "LIST" when available >= 4 && source.Matches(body, "INFO"u8):
                    ReadInfoList(source, body + 4, body + available, tags);
                    break;
                case "id3 " or "ID3 ":
                    Id3v2.Read(source, body, tags, options);
                    break;
            }
            position = body + size + (size & 1); // chunks are padded to even sizes
        }
        if (byteRate > 0 && dataSize > 0)
        {
            tags.Duration = TimeSpan.FromSeconds((double)dataSize / byteRate);
            tags.Bitrate = byteRate * 8 / 1000;
        }
    }

    /// <summary>Parses WAVEFORMAT(EX/EXTENSIBLE); returns the byte rate.</summary>
    private static int ReadWaveFormat(ByteSource source, long body, int length, AudioTags tags)
    {
        Span<byte> format = stackalloc byte[64];
        format = format[..source.Read(body, format[..length])];
        int formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        tags.Channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        int byteRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
        tags.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        if (formatTag == 0xFFFE && format.Length >= 26)
            formatTag = BinaryPrimitives.ReadUInt16LittleEndian(format[24..]); // WAVE_FORMAT_EXTENSIBLE sub-format
        tags.Codec = formatTag switch
        {
            1 => "PCM",
            3 => "PCM (float)",
            6 => "A-law",
            7 => "µ-law",
            0x50 or 0x55 => "MP3",
            _ => "WAV",
        };
        return byteRate;
    }

    private static void ReadInfoList(ByteSource source, long position, long end, AudioTags tags)
    {
        Span<byte> header = stackalloc byte[8];
        while (position + 8 <= end)
        {
            source.ReadExactly(position, header);
            string id = Encoding.ASCII.GetString(header[..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long body = position + 8;
            if (size > end - body)
                break;
            if (size is > 0 and <= TagLimits.MaxTextBytes)
            {
                string value = DecodeInfoText(source.ReadArray(body, (int)size));
                switch (id)
                {
                    case "INAM": tags.SetTitle(value); break;
                    case "IART": tags.SetArtist(value); break;
                    case "IPRD": tags.SetAlbum(value); break;
                    case "ICRD": tags.SetYear(TextDecoding.Year(value)); break;
                    case "IGNR": tags.AddGenre(value); break;
                    case "IMUS": tags.SetComposer(value); break;
                    case "ITRK" or "IPRT":
                        var (number, total) = TextDecoding.NumberPair(value);
                        tags.SetTrack(number, total);
                        break;
                }
            }
            position = body + size + (size & 1);
        }
    }

    /// <summary>INFO strings have no declared encoding: UTF-8 when valid, Latin-1 otherwise.</summary>
    private static string DecodeInfoText(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0)
            bytes = bytes[..end];
        return System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    public static void ReadAiff(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        bool compressed = source.Matches(8, "AIFC"u8);
        tags.Codec = compressed ? "AIFF-C" : "PCM";
        long position = 12;
        Span<byte> header = stackalloc byte[8];
        Span<byte> comm = stackalloc byte[22];
        while (position + 8 <= source.Length)
        {
            source.ReadExactly(position, header);
            string id = Encoding.ASCII.GetString(header[..4]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            long body = position + 8;
            switch (id)
            {
                case "COMM" when size >= 18:
                {
                    source.ReadExactly(body, comm[..(int)Math.Min(22, size)]);
                    tags.Channels = BinaryPrimitives.ReadUInt16BigEndian(comm);
                    uint frames = BinaryPrimitives.ReadUInt32BigEndian(comm[2..]);
                    tags.BitsPerSample = BinaryPrimitives.ReadUInt16BigEndian(comm[6..]);
                    double rate = ReadExtended(comm.Slice(8, 10));
                    tags.SampleRate = (int)Math.Round(rate);
                    if (compressed && size >= 22)
                    {
                        string compression = Encoding.ASCII.GetString(comm.Slice(18, 4));
                        tags.Codec = compression is "NONE" or "twos" or "sowt" or "fl32" or "fl64" ? "PCM" : compression.Trim();
                    }
                    if (rate > 0)
                    {
                        tags.Duration = TimeSpan.FromSeconds(frames / rate);
                        tags.Bitrate = (int)(rate * tags.Channels * tags.BitsPerSample / 1000);
                    }
                    break;
                }
                case "ID3 " or "id3 ":
                    Id3v2.Read(source, body, tags, options);
                    break;
                case "NAME" when size <= TagLimits.MaxTextBytes:
                    tags.SetTitle(Encoding.Latin1.GetString(source.ReadArray(body, (int)size)).TrimEnd('\0'));
                    break;
                case "AUTH" when size <= TagLimits.MaxTextBytes:
                    tags.SetArtist(Encoding.Latin1.GetString(source.ReadArray(body, (int)size)).TrimEnd('\0'));
                    break;
            }
            position = body + size + (size & 1);
        }
    }

    /// <summary>Converts an IEEE 754 80-bit extended float (big-endian), as used for AIFF sample rates.</summary>
    internal static double ReadExtended(ReadOnlySpan<byte> bytes)
    {
        int exponent = (bytes[0] & 0x7F) << 8 | bytes[1];
        ulong mantissa = BinaryPrimitives.ReadUInt64BigEndian(bytes[2..]);
        if (exponent == 0 && mantissa == 0)
            return 0;
        double value = mantissa * Math.Pow(2, exponent - 16383 - 63);
        return (bytes[0] & 0x80) != 0 ? -value : value;
    }
}
