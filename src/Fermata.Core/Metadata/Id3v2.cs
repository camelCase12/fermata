using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Fermata.Metadata;

/// <summary>Reads ID3v2.2, 2.3 and 2.4 tags (MP3, AAC, AIFF, WAV and DSF files).</summary>
/// <remarks>Specification: https://id3.org/id3v2.4.0-structure and id3v2.3.0.</remarks>
internal static class Id3v2
{
    private const int HeaderSize = 10;

    /// <summary>Returns the size of the tag at <paramref name="offset"/> including header and footer, or 0 when there is none.</summary>
    public static long TagSize(ByteSource source, long offset)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (source.Read(offset, header) != HeaderSize || !IsHeader(header))
            return 0;
        long size = HeaderSize + SyncSafe(header[6..10]);
        if (header[3] == 4 && (header[5] & 0x10) != 0)
            size += HeaderSize;
        return size;
    }

    /// <summary>Reads the tag at <paramref name="offset"/>. Returns its total size, or 0 when there is no tag.</summary>
    public static long Read(ByteSource source, long offset, AudioTags tags, TagReadOptions options)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (source.Read(offset, header) != HeaderSize || !IsHeader(header))
            return 0;
        int version = header[3];
        byte flags = header[5];
        long bodySize = SyncSafe(header[6..10]);
        long total = HeaderSize + bodySize + (version == 4 && (flags & 0x10) != 0 ? HeaderSize : 0);
        bodySize = Math.Min(bodySize, source.Length - offset - HeaderSize);

        // ID3v2.2 compression was never defined; such tags cannot be read.
        if (version == 2 && (flags & 0x40) != 0)
            return total;

        ByteSource body = source;
        long start = offset + HeaderSize;
        bool addressable = true;
        if ((flags & 0x80) != 0 && version < 4)
        {
            // Whole-tag unsynchronization (2.2/2.3): frame sizes refer to the decoded bytes.
            byte[] raw = source.ReadArray(start, (int)bodySize);
            body = new MemoryByteSource(RemoveUnsynchronization(raw));
            start = 0;
            bodySize = body.Length;
            addressable = false;
        }

        long position = start;
        long end = start + bodySize;
        if (version >= 3 && (flags & 0x40) != 0)
            position += ExtendedHeaderSize(body, position, version);

        var reader = new FrameReader(body, version, addressable, options, tags);
        while (position < end)
        {
            long next = reader.ReadFrame(position, end);
            if (next <= position)
                break;
            position = next;
        }
        return total;
    }

    private static bool IsHeader(ReadOnlySpan<byte> header) =>
        header[0] == 'I' && header[1] == 'D' && header[2] == '3' && header[3] is >= 2 and <= 4 && header[4] != 0xFF
        && (header[6] | header[7] | header[8] | header[9]) < 0x80;

    private static long ExtendedHeaderSize(ByteSource body, long position, int version)
    {
        Span<byte> size = stackalloc byte[4];
        body.ReadExactly(position, size);
        // 2.3 stores the size excluding its own four bytes; 2.4 stores a sync-safe size including them.
        return version == 3 ? 4 + BinaryPrimitives.ReadUInt32BigEndian(size) : SyncSafe(size);
    }

    internal static long SyncSafe(ReadOnlySpan<byte> bytes) =>
        (bytes[0] & 0x7F) << 21 | (bytes[1] & 0x7F) << 14 | (bytes[2] & 0x7F) << 7 | bytes[3] & 0x7F;

    /// <summary>Reverses unsynchronization: every <c>FF 00</c> pair becomes <c>FF</c>.</summary>
    internal static byte[] RemoveUnsynchronization(ReadOnlySpan<byte> data)
    {
        var result = new byte[data.Length];
        int length = 0;
        for (int i = 0; i < data.Length; i++)
        {
            result[length++] = data[i];
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00)
                i++;
        }
        return length == result.Length ? result : result[..length];
    }

    /// <summary>Parses frames of one tag.</summary>
    private readonly struct FrameReader(ByteSource body, int version, bool addressable, TagReadOptions options, AudioTags tags)
    {
        private int HeaderLength => version == 2 ? 6 : 10;

        /// <summary>Reads the frame at <paramref name="position"/>; returns the position of the next frame, or -1 at padding/end.</summary>
        public long ReadFrame(long position, long end)
        {
            if (position + HeaderLength > end)
                return -1;
            Span<byte> header = stackalloc byte[10];
            body.ReadExactly(position, header[..HeaderLength]);
            if (header[0] == 0 || !IsFrameId(header[..(version == 2 ? 3 : 4)]))
                return -1;

            string id;
            long size;
            byte formatFlags = 0;
            if (version == 2)
            {
                id = MapVersion22(Encoding.ASCII.GetString(header[..3]));
                size = header[3] << 16 | header[4] << 8 | header[5];
            }
            else
            {
                id = Encoding.ASCII.GetString(header[..4]);
                size = version == 4 ? FrameSize24(position, end, header[4..8]) : BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
                formatFlags = header[9];
            }
            long dataStart = position + HeaderLength;
            long next = dataStart + size;
            if (size <= 0 || next > end)
                return -1;

            if (Wants(id))
                ReadFrameContent(id, dataStart, size, formatFlags);
            return next;
        }

        /// <summary>
        /// ID3v2.4 frame sizes are sync-safe, but some encoders wrote plain 32-bit sizes. When the
        /// sync-safe reading does not land on a plausible next frame and the plain one does, use the plain one.
        /// </summary>
        private long FrameSize24(long position, long end, ReadOnlySpan<byte> sizeBytes)
        {
            uint plain = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes);
            if ((sizeBytes[0] | sizeBytes[1] | sizeBytes[2] | sizeBytes[3]) >= 0x80)
                return plain; // Not a valid sync-safe integer, so it can only be a plain size.
            long syncSafe = SyncSafe(sizeBytes);
            if (syncSafe == plain || FrameFollows(position + 10 + syncSafe, end))
                return syncSafe;
            return FrameFollows(position + 10 + plain, end) ? plain : syncSafe;
        }

        private bool FrameFollows(long position, long end)
        {
            if (position == end)
                return true;
            if (position + 10 > end)
                return false;
            Span<byte> next = stackalloc byte[4];
            body.ReadExactly(position, next);
            return next[0] == 0 || IsFrameId(next);
        }

        private static bool Wants(string id) => id switch
        {
            "APIC" => true,
            "USLT" or "SYLT" => true,
            "TXXX" => true,
            "TLEN" => true,
            _ => id[0] == 'T' && TextFrameWanted(id),
        };

        private static bool TextFrameWanted(string id) => id is "TIT2" or "TPE1" or "TPE2" or "TALB" or "TYER"
            or "TDRC" or "TORY" or "TDOR" or "TRCK" or "TPOS" or "TCON" or "TCOM" or "TCMP" or "TSOT" or "TSOP"
            or "TSOA" or "TSO2";

        private void ReadFrameContent(string id, long dataStart, long size, byte formatFlags)
        {
            // Frame format flags differ between versions; normalize them.
            bool compressed, encrypted, unsynchronized, hasDataLength, grouped;
            if (version == 4)
            {
                grouped = (formatFlags & 0x40) != 0;
                compressed = (formatFlags & 0x08) != 0;
                encrypted = (formatFlags & 0x04) != 0;
                unsynchronized = (formatFlags & 0x02) != 0;
                hasDataLength = (formatFlags & 0x01) != 0;
            }
            else
            {
                compressed = (formatFlags & 0x80) != 0;
                encrypted = (formatFlags & 0x40) != 0;
                grouped = (formatFlags & 0x20) != 0;
                unsynchronized = false;
                hasDataLength = compressed; // 2.3 compressed frames start with the decompressed size.
            }
            if (encrypted)
                return;
            long contentStart = dataStart + (grouped ? 1 : 0) + (hasDataLength ? 4 : 0);
            long contentLength = size - (contentStart - dataStart);
            if (contentLength <= 0)
                return;

            bool isPicture = id == "APIC";
            bool isLyrics = id is "USLT" or "SYLT";
            bool direct = addressable && !compressed && !unsynchronized;

            if (isPicture && direct)
            {
                ReadPictureInPlace(contentStart, contentLength);
                return;
            }
            if (isLyrics)
                tags.HasLyrics = true;
            if (isPicture && !options.HasFlag(TagReadOptions.PictureData))
            {
                tags.OfferPicture(new EmbeddedPicture("image/unknown", IsFrontCoverAt(contentStart, contentLength), -1, (int)Math.Min(contentLength, int.MaxValue)));
                return;
            }
            if (isLyrics && !options.HasFlag(TagReadOptions.Lyrics))
                return;
            int limit = isPicture || isLyrics ? TagLimits.MaxFieldBytes : TagLimits.MaxTextBytes;
            if (contentLength > limit)
                return;

            byte[] content = body.ReadArray(contentStart, (int)contentLength);
            if (unsynchronized)
                content = RemoveUnsynchronization(content);
            if (compressed)
                content = Inflate(content);
            ParseContent(id, content);
        }

        private bool IsFrontCoverAt(long contentStart, long contentLength)
        {
            // Best effort for frames we are not decoding (compressed frames yield false): the type byte
            // follows the MIME string, or the three-letter image format in ID3v2.2.
            Span<byte> prefix = stackalloc byte[64];
            int read = body.Read(contentStart, prefix[..(int)Math.Min(64, contentLength)]);
            if (version == 2)
                return read > 4 && prefix[4] == 3;
            int mimeEnd = read > 1 ? prefix[1..read].IndexOf((byte)0) : -1;
            return mimeEnd >= 0 && 2 + mimeEnd < read && prefix[2 + mimeEnd] == 3;
        }

        private void ReadPictureInPlace(long contentStart, long contentLength)
        {
            // The header before the image is short, except for a rare long description.
            byte[] prefix = ArrayPool<byte>.Shared.Rent((int)Math.Min(contentLength, 4096));
            string mime;
            byte type;
            int dataOffset;
            try
            {
                int read = body.Read(contentStart, prefix.AsSpan(0, (int)Math.Min(contentLength, prefix.Length)));
                if (!ParsePictureHeader(prefix.AsSpan(0, read), out mime, out type, out dataOffset))
                    return;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(prefix);
            }
            long dataLength = contentLength - dataOffset;
            if (dataLength <= 0 || dataLength > TagLimits.MaxFieldBytes)
                return;
            var picture = new EmbeddedPicture(mime, type == 3, contentStart + dataOffset, (int)dataLength);
            if (options.HasFlag(TagReadOptions.PictureData))
                picture = picture with { Data = body.ReadArray(picture.Offset, picture.Length) };
            tags.OfferPicture(picture);
        }

        /// <summary>Parses the APIC header (encoding, MIME, type, description); PIC in 2.2 uses a three-letter format instead of a MIME type.</summary>
        private bool ParsePictureHeader(ReadOnlySpan<byte> content, out string mime, out byte type, out int dataOffset)
        {
            mime = "";
            type = 0;
            dataOffset = 0;
            if (content.Length < 4)
                return false;
            var encoding = (TagTextEncoding)Math.Min(content[0], (byte)3);
            int position;
            if (version == 2)
            {
                string format = Encoding.ASCII.GetString(content.Slice(1, 3));
                mime = format.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                position = 4;
            }
            else
            {
                int mimeEnd = content[1..].IndexOf((byte)0);
                if (mimeEnd < 0)
                    return false;
                mime = NormalizeMime(Encoding.Latin1.GetString(content.Slice(1, mimeEnd)));
                position = 1 + mimeEnd + 1;
            }
            if (position >= content.Length)
                return false;
            type = content[position++];
            int descriptionEnd = TextDecoding.IndexOfTerminator(content[position..], encoding);
            if (descriptionEnd < 0)
                return false;
            dataOffset = position + descriptionEnd + TextDecoding.TerminatorWidth(encoding);
            return dataOffset <= content.Length;
        }

        private void ParseContent(string id, byte[] content)
        {
            if (id == "APIC")
            {
                if (ParsePictureHeader(content, out string mime, out byte type, out int dataOffset))
                    tags.OfferPicture(new EmbeddedPicture(mime, type == 3, -1, content.Length - dataOffset) { Data = content[dataOffset..] });
                return;
            }
            if (content.Length < 1)
                return;
            var encoding = (TagTextEncoding)Math.Min(content[0], (byte)3);
            var text = content.AsSpan(1);
            switch (id)
            {
                case "TXXX":
                {
                    int descriptionEnd = TextDecoding.IndexOfTerminator(text, encoding);
                    if (descriptionEnd < 0)
                        return;
                    string description = TextDecoding.DecodeRaw(text[..descriptionEnd], encoding);
                    foreach (string value in TextDecoding.DecodeList(text[(descriptionEnd + TextDecoding.TerminatorWidth(encoding))..], encoding))
                        TagFields.Apply(tags, description, value, options);
                    return;
                }
                case "USLT":
                {
                    if (text.Length < 3)
                        return;
                    var afterLanguage = text[3..];
                    int descriptorEnd = TextDecoding.IndexOfTerminator(afterLanguage, encoding);
                    if (descriptorEnd < 0)
                        return;
                    var lyrics = afterLanguage[(descriptorEnd + TextDecoding.TerminatorWidth(encoding))..];
                    tags.Lyrics = AudioTags.KeepFirst(tags.Lyrics, TextDecoding.DecodeRaw(lyrics, encoding).TrimEnd('\0'));
                    return;
                }
                case "SYLT":
                    tags.Lyrics = AudioTags.KeepFirst(tags.Lyrics, SynchronizedLyricsToLrc(text, encoding));
                    return;
            }

            var values = TextDecoding.DecodeList(text, encoding);
            if (values.Count == 0)
                return;
            string first = values[0];
            switch (id)
            {
                case "TIT2":
                    tags.SetTitle(first);
                    break;
                case "TPE1":
                    tags.SetArtist(values.Count > 1 ? string.Join(", ", values) : first);
                    tags.ArtistValues.AddRange(values);
                    break;
                case "TPE2":
                    tags.SetAlbumArtist(first);
                    break;
                case "TALB":
                    tags.SetAlbum(first);
                    break;
                case "TYER":
                case "TDRC":
                    tags.SetYear(TextDecoding.Year(first));
                    break;
                case "TORY":
                case "TDOR":
                    tags.SetOriginalYear(TextDecoding.Year(first));
                    break;
                case "TRCK":
                {
                    var (number, total) = TextDecoding.NumberPair(first);
                    tags.SetTrack(number, total);
                    break;
                }
                case "TPOS":
                {
                    var (number, total) = TextDecoding.NumberPair(first);
                    tags.SetDisc(number, total);
                    break;
                }
                case "TCON":
                    foreach (string value in values)
                    {
                        foreach (string genre in Genres.ParseId3(value))
                            tags.AddGenre(genre);
                    }
                    break;
                case "TCOM":
                    tags.SetComposer(first);
                    break;
                case "TCMP":
                    tags.Compilation |= TextDecoding.Flag(first);
                    break;
                case "TSOT":
                    tags.TitleSort = AudioTags.KeepFirst(tags.TitleSort, first);
                    break;
                case "TSOP":
                    tags.ArtistSort = AudioTags.KeepFirst(tags.ArtistSort, first);
                    break;
                case "TSOA":
                    tags.AlbumSort = AudioTags.KeepFirst(tags.AlbumSort, first);
                    break;
                case "TSO2":
                    tags.AlbumArtistSort = AudioTags.KeepFirst(tags.AlbumArtistSort, first);
                    break;
                case "TLEN":
                    if (long.TryParse(first, out long milliseconds) && milliseconds > 0)
                        tags.DurationHint = TimeSpan.FromMilliseconds(milliseconds);
                    break;
            }
        }

        /// <summary>Converts SYLT entries with millisecond timestamps into LRC text.</summary>
        private static string? SynchronizedLyricsToLrc(ReadOnlySpan<byte> text, TagTextEncoding encoding)
        {
            // Layout: language(3) timestampFormat(1) contentType(1) descriptor NUL { text NUL, timestamp(4) }*
            if (text.Length < 5 || text[3] != 2)
                return null;
            var rest = text[5..];
            int width = TextDecoding.TerminatorWidth(encoding);
            int descriptorEnd = TextDecoding.IndexOfTerminator(rest, encoding);
            if (descriptorEnd < 0)
                return null;
            rest = rest[(descriptorEnd + width)..];
            var lrc = new StringBuilder();
            while (!rest.IsEmpty)
            {
                int end = TextDecoding.IndexOfTerminator(rest, encoding);
                if (end < 0 || end + width + 4 > rest.Length)
                    break;
                string line = TextDecoding.DecodeRaw(rest[..end], encoding).Trim('\n', '\r');
                uint milliseconds = BinaryPrimitives.ReadUInt32BigEndian(rest.Slice(end + width, 4));
                var time = TimeSpan.FromMilliseconds(milliseconds);
                lrc.Append('[').Append((int)time.TotalMinutes).Append(':')
                    .Append(time.Seconds.ToString("00")).Append('.').Append((time.Milliseconds / 10).ToString("00"))
                    .Append(']').Append(line).Append('\n');
                rest = rest[(end + width + 4)..];
            }
            return lrc.Length > 0 ? lrc.ToString() : null;
        }
    }

    private static byte[] Inflate(byte[] data)
    {
        try
        {
            using var input = new MemoryStream(data);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zlib.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    private static bool IsFrameId(ReadOnlySpan<byte> id)
    {
        foreach (byte b in id)
        {
            if (b is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'))
                return false;
        }
        return true;
    }

    private static string NormalizeMime(string mime) => mime.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "image/jpg" => "image/jpeg",
        "png" => "image/png",
        "" => "image/jpeg",
        var other => other,
    };

    private static string MapVersion22(string id) => id switch
    {
        "TT2" => "TIT2",
        "TP1" => "TPE1",
        "TP2" => "TPE2",
        "TAL" => "TALB",
        "TYE" => "TYER",
        "TOR" => "TORY",
        "TRK" => "TRCK",
        "TPA" => "TPOS",
        "TCO" => "TCON",
        "TCM" => "TCOM",
        "TCP" => "TCMP",
        "TST" => "TSOT",
        "TSP" => "TSOP",
        "TSA" => "TSOA",
        "TS2" => "TSO2",
        "TLE" => "TLEN",
        "TXX" => "TXXX",
        "ULT" => "USLT",
        "SLT" => "SYLT",
        "PIC" => "APIC",
        _ => id,
    };
}
