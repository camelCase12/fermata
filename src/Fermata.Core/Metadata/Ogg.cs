using System.Buffers;
using System.Buffers.Binary;

namespace Fermata.Metadata;

/// <summary>Ogg containers holding Vorbis, Opus, FLAC or Speex.</summary>
/// <remarks>Specification: RFC 3533 (Ogg), RFC 7845 (Opus in Ogg), Vorbis I specification §4.2.</remarks>
internal static class Ogg
{
    private const int PageHeaderSize = 27;

    public static void Read(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        var packets = new PacketReader(source, 0);
        Span<byte> identification = stackalloc byte[64];
        int length = packets.ReadPacketPrefix(identification);
        identification = identification[..length];

        long preSkip = 0;
        int granuleRate;
        Action<PacketReader>? readComment;
        if (identification.StartsWith("\u0001vorbis"u8) && length >= 16)
        {
            tags.Codec = "Vorbis";
            tags.Channels = identification[11];
            tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(identification[12..]);
            granuleRate = tags.SampleRate;
            readComment = reader => ReadCommentPacket(reader, "\u0003vorbis"u8.Length, tags, options);
        }
        else if (identification.StartsWith("OpusHead"u8) && length >= 19)
        {
            tags.Codec = "Opus";
            tags.Channels = identification[9];
            preSkip = BinaryPrimitives.ReadUInt16LittleEndian(identification[10..]);
            // Opus always decodes at 48 kHz; the header's rate only records the original input.
            tags.SampleRate = 48000;
            granuleRate = 48000;
            readComment = reader => ReadCommentPacket(reader, "OpusTags"u8.Length, tags, options);
        }
        else if (identification.StartsWith("\u007FFLAC"u8) && length >= 51)
        {
            // Mapping header (9 bytes), "fLaC", then the STREAMINFO block header (4 bytes) and body.
            tags.Codec = "FLAC";
            Flac.ReadStreamInfo(identification.Slice(17, 34), tags);
            granuleRate = tags.SampleRate;
            // The next packet is a VORBIS_COMMENT metadata block: a 4-byte block header precedes the comment.
            readComment = reader => ReadCommentPacket(reader, 4, tags, options);
        }
        else if (identification.StartsWith("Speex   "u8) && length >= 40)
        {
            tags.Codec = "Speex";
            tags.SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(identification[36..]);
            tags.Channels = identification.Length >= 52 ? (int)BinaryPrimitives.ReadUInt32LittleEndian(identification[48..]) : 1;
            granuleRate = tags.SampleRate;
            readComment = reader => VorbisComment.Read(reader, tags, options);
        }
        else
        {
            throw new InvalidDataException("Unsupported Ogg stream.");
        }

        packets.NextPacket();
        readComment(packets);

        long granule = LastGranule(source, packets.Serial);
        if (granule > preSkip && granuleRate > 0)
        {
            tags.Duration = TimeSpan.FromSeconds((double)(granule - preSkip) / granuleRate);
            tags.Bitrate = (int)Math.Round(source.Length * 8 / tags.Duration.TotalSeconds / 1000);
        }
    }

    private static void ReadCommentPacket(PacketReader reader, int signatureLength, AudioTags tags, TagReadOptions options)
    {
        reader.Skip(signatureLength);
        VorbisComment.Read(reader, tags, options);
    }

    /// <summary>
    /// The granule position of the last page of the stream: total samples (plus pre-skip for Opus).
    /// Found by scanning the file's tail backwards for a page of the same logical stream.
    /// </summary>
    private static long LastGranule(ByteSource source, uint serial)
    {
        const int Tail = 64 * 1024;
        long start = Math.Max(0, source.Length - Tail);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Tail);
        try
        {
            int read = source.Read(start, buffer.AsSpan(0, (int)(source.Length - start)));
            for (int i = read - PageHeaderSize; i >= 0; i--)
            {
                if (buffer[i] != 'O' || !buffer.AsSpan(i, 4).SequenceEqual("OggS"u8))
                    continue;
                var header = buffer.AsSpan(i, PageHeaderSize);
                long granule = BinaryPrimitives.ReadInt64LittleEndian(header[6..]);
                if (BinaryPrimitives.ReadUInt32LittleEndian(header[14..]) == serial && granule > 0)
                    return granule;
            }
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Reads the packets of the first logical stream sequentially across pages.</summary>
    private sealed class PacketReader : IPacketReader
    {
        private readonly ByteSource source;
        private readonly byte[] segments = new byte[255];
        private long pageStart;
        private long segmentDataStart;
        private int segmentCount;
        private int segmentIndex;
        private int segmentOffset;      // bytes already consumed in the current segment
        private long segmentFileOffset; // file offset of the current segment's first byte
        private bool packetEnded;

        public PacketReader(ByteSource source, long start)
        {
            this.source = source;
            pageStart = start;
            LoadPage(first: true);
        }

        public uint Serial { get; private set; }

        public long FileOffset => -1; // Packets may continue on the next page; nothing is addressable.

        /// <summary>Copies the start of the current packet into <paramref name="buffer"/>; returns the bytes copied.</summary>
        public int ReadPacketPrefix(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length && !packetEnded)
                total += ReadSome(buffer[total..]);
            return total;
        }

        /// <summary>Skips the rest of the current packet and positions at the start of the next one.</summary>
        public void NextPacket()
        {
            while (!packetEnded)
                SkipSome(long.MaxValue);
            packetEnded = false;
            AdvanceSegment();
        }

        public void ReadExactly(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                if (packetEnded)
                    throw new InvalidDataException("Ogg packet ended early.");
                total += ReadSome(buffer[total..]);
            }
        }

        public void Skip(long count)
        {
            while (count > 0)
            {
                if (packetEnded)
                    throw new InvalidDataException("Ogg packet ended early.");
                count -= SkipSome(count);
            }
        }

        private int ReadSome(Span<byte> buffer)
        {
            int available = segments[segmentIndex] - segmentOffset;
            int count = Math.Min(available, buffer.Length);
            if (count > 0)
            {
                source.ReadExactly(segmentFileOffset + segmentOffset, buffer[..count]);
                segmentOffset += count;
            }
            if (segmentOffset == segments[segmentIndex])
                FinishSegment();
            return count;
        }

        private long SkipSome(long count)
        {
            int available = segments[segmentIndex] - segmentOffset;
            int skipped = (int)Math.Min(available, count);
            segmentOffset += skipped;
            if (segmentOffset == segments[segmentIndex])
                FinishSegment();
            return skipped;
        }

        /// <summary>A lacing value below 255 ends the packet; 255 means it continues in the next segment.</summary>
        private void FinishSegment()
        {
            if (segments[segmentIndex] < 255)
                packetEnded = true;
            else
                AdvanceSegment();
        }

        private void AdvanceSegment()
        {
            segmentFileOffset += segments[segmentIndex];
            segmentOffset = 0;
            segmentIndex++;
            if (segmentIndex >= segmentCount)
                LoadNextPage();
        }

        private void LoadNextPage()
        {
            pageStart = segmentDataStart + SumSegments();
            LoadPage(first: false);
        }

        private long SumSegments()
        {
            long sum = 0;
            for (int i = 0; i < segmentCount; i++)
                sum += segments[i];
            return sum;
        }

        private void LoadPage(bool first)
        {
            Span<byte> header = stackalloc byte[PageHeaderSize];
            while (true)
            {
                source.ReadExactly(pageStart, header);
                if (!header[..4].SequenceEqual("OggS"u8))
                    throw new InvalidDataException("Missing Ogg page.");
                uint serial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
                segmentCount = header[26];
                source.ReadExactly(pageStart + PageHeaderSize, segments.AsSpan(0, segmentCount));
                segmentDataStart = pageStart + PageHeaderSize + segmentCount;
                if (first)
                    Serial = serial;
                if (serial == Serial && segmentCount > 0)
                    break;
                // Pages of other multiplexed streams are skipped.
                pageStart = segmentDataStart + SumSegments();
            }
            segmentIndex = 0;
            segmentOffset = 0;
            segmentFileOffset = segmentDataStart;
        }
    }
}
