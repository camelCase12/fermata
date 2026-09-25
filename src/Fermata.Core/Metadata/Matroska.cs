using System.Buffers.Binary;
using System.Text;

namespace Fermata.Metadata;

/// <summary>Matroska audio and WebM (MKA, WEBM): track format, duration, tags and cover attachments.</summary>
/// <remarks>Specification: RFC 9559 (Matroska) and https://www.matroska.org/technical/tagging.html.</remarks>
internal static class Matroska
{
    private const uint Ebml = 0x1A45DFA3, Segment = 0x18538067, SeekHead = 0x114D9B74, Seek = 0x4DBB, SeekId = 0x53AB,
        SeekPosition = 0x53AC, Info = 0x1549A966, TimecodeScale = 0x2AD7B1, Duration = 0x4489, SegmentTitle = 0x7BA9, Tracks = 0x1654AE6B,
        TrackEntry = 0xAE, TrackType = 0x83, CodecId = 0x86, Audio = 0xE1, SamplingFrequency = 0xB5, Channels = 0x9F,
        BitDepth = 0x6264, Tags = 0x1254C367, Tag = 0x7373, Targets = 0x63C0, TargetTypeValue = 0x68CA, SimpleTag = 0x67C8,
        TagName = 0x45A3, TagString = 0x4487, Attachments = 0x1941A469, AttachedFile = 0x61A7, FileName = 0x466E,
        FileMimeType = 0x4660, FileData = 0x465C, Cluster = 0x1F43B675;

    private readonly record struct Element(uint Id, long DataStart, long End);

    public static void Read(ByteSource source, AudioTags tags, TagReadOptions options)
    {
        if (!TryReadElement(source, 0, source.Length, out var ebml) || ebml.Id != Ebml)
            throw new InvalidDataException("Not an EBML file.");
        if (!TryReadElement(source, ebml.End, source.Length, out var segment) || segment.Id != Segment)
            throw new InvalidDataException("Missing Matroska segment.");

        var pending = new HashSet<uint> { Info, Tracks, Tags, Attachments };
        var seekTargets = new List<(uint Id, long Position)>();
        long position = segment.DataStart;
        while (position < segment.End && pending.Count > 0 && TryReadElement(source, position, segment.End, out var element))
        {
            if (element.Id == Cluster)
                break; // Media data follows; remaining metadata is reached through the seek head.
            if (element.Id == SeekHead)
                ReadSeekHead(source, element, segment.DataStart, seekTargets);
            else if (pending.Remove(element.Id))
                ReadTopLevel(source, element, tags, options);
            position = element.End;
        }
        foreach (var (id, target) in seekTargets)
        {
            if (pending.Remove(id) && TryReadElement(source, target, segment.End, out var element) && element.Id == id)
                ReadTopLevel(source, element, tags, options);
        }
        if (tags.Duration > TimeSpan.Zero && tags.Bitrate == 0)
            tags.Bitrate = (int)Math.Round(source.Length * 8 / tags.Duration.TotalSeconds / 1000);
    }

    private static void ReadTopLevel(ByteSource source, Element element, AudioTags tags, TagReadOptions options)
    {
        if (element.Id == Info)
            ReadInfo(source, element, tags);
        else if (element.Id == Tracks)
            ReadTracks(source, element, tags);
        else if (element.Id == Tags)
            ReadTags(source, element, tags, options);
        else if (element.Id == Attachments)
            ReadAttachments(source, element, tags, options);
    }

    private static void ReadSeekHead(ByteSource source, Element seekHead, long segmentData, List<(uint, long)> targets)
    {
        foreach (var seek in Children(source, seekHead))
        {
            if (seek.Id != Seek)
                continue;
            uint id = 0;
            long offset = -1;
            foreach (var field in Children(source, seek))
            {
                if (field.Id == SeekId)
                    id = (uint)ReadUnsigned(source, field);
                else if (field.Id == SeekPosition)
                    offset = (long)ReadUnsigned(source, field);
            }
            if (id != 0 && offset >= 0)
                targets.Add((id, segmentData + offset));
        }
    }

    private static void ReadInfo(ByteSource source, Element info, AudioTags tags)
    {
        ulong scale = 1_000_000;
        double duration = 0;
        foreach (var field in Children(source, info))
        {
            if (field.Id == TimecodeScale)
                scale = ReadUnsigned(source, field);
            else if (field.Id == Duration)
                duration = ReadFloat(source, field);
            else if (field.Id == SegmentTitle)
                tags.SetTitle(ReadString(source, field)); // ffmpeg stores the "title" metadata here
        }
        if (duration > 0)
            tags.Duration = TimeSpan.FromSeconds(duration * scale / 1e9);
    }

    private static void ReadTracks(ByteSource source, Element tracks, AudioTags tags)
    {
        foreach (var entry in Children(source, tracks))
        {
            if (entry.Id != TrackEntry)
                continue;
            ulong type = 0;
            string codec = "";
            Element? audio = null;
            foreach (var field in Children(source, entry))
            {
                if (field.Id == TrackType)
                    type = ReadUnsigned(source, field);
                else if (field.Id == CodecId)
                    codec = ReadString(source, field);
                else if (field.Id == Audio)
                    audio = field;
            }
            // ffmpeg stores cover art as a one-frame MJPEG or PNG video track; anything else is real video.
            if (type == 1 && codec is not ("V_MJPEG" or "V_PNG"))
                tags.HasVideo = true;
            if (type != 2 || audio is not { } audioElement || tags.Codec.Length > 0)
                continue;
            tags.Codec = codec switch
            {
                "A_OPUS" => "Opus",
                "A_VORBIS" => "Vorbis",
                "A_FLAC" => "FLAC",
                "A_MPEG/L3" => "MP3",
                "A_ALAC" => "ALAC",
                "A_AC3" => "AC-3",
                "A_EAC3" => "E-AC-3",
                "A_WAVPACK4" => "WavPack",
                _ when codec.StartsWith("A_AAC", StringComparison.Ordinal) => "AAC",
                _ when codec.StartsWith("A_PCM", StringComparison.Ordinal) => "PCM",
                _ => codec.StartsWith("A_", StringComparison.Ordinal) ? codec[2..] : codec,
            };
            foreach (var field in Children(source, audioElement))
            {
                if (field.Id == SamplingFrequency)
                    tags.SampleRate = (int)Math.Round(ReadFloat(source, field));
                else if (field.Id == Channels)
                    tags.Channels = (int)ReadUnsigned(source, field);
                else if (field.Id == BitDepth)
                    tags.BitsPerSample = (int)ReadUnsigned(source, field);
            }
        }
    }

    private static void ReadTags(ByteSource source, Element tagsElement, AudioTags tags, TagReadOptions options)
    {
        foreach (var tag in Children(source, tagsElement))
        {
            if (tag.Id != Tag)
                continue;
            // Untargeted tags describe the file itself, which ffmpeg and yt-dlp use for track metadata.
            ulong level = 0;
            foreach (var field in Children(source, tag))
            {
                if (field.Id == Targets)
                {
                    foreach (var target in Children(source, field))
                    {
                        if (target.Id == TargetTypeValue)
                            level = ReadUnsigned(source, target);
                    }
                }
            }
            foreach (var field in Children(source, tag))
            {
                if (field.Id == SimpleTag)
                    ReadSimpleTag(source, field, level >= 50, tags, options);
            }
        }
    }

    private static void ReadSimpleTag(ByteSource source, Element simpleTag, bool albumLevel, AudioTags tags, TagReadOptions options)
    {
        string? name = null, value = null;
        foreach (var field in Children(source, simpleTag))
        {
            if (field.Id == TagName)
                name = ReadString(source, field);
            else if (field.Id == TagString && field.End - field.DataStart <= TagLimits.MaxTextBytes)
                value = ReadString(source, field);
        }
        if (name is null || value is null)
            return;
        if (albumLevel)
        {
            // At album level, TITLE and ARTIST name the album and its artist.
            if (name.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
                name = "ALBUM";
            else if (name.Equals("ARTIST", StringComparison.OrdinalIgnoreCase))
                name = "ALBUMARTIST";
            else if (name.Equals("TOTAL_PARTS", StringComparison.OrdinalIgnoreCase))
                name = "TRACKTOTAL";
        }
        TagFields.Apply(tags, name, value, options);
    }

    private static void ReadAttachments(ByteSource source, Element attachments, AudioTags tags, TagReadOptions options)
    {
        foreach (var file in Children(source, attachments))
        {
            if (file.Id != AttachedFile)
                continue;
            string name = "", mime = "";
            Element? data = null;
            foreach (var field in Children(source, file))
            {
                if (field.Id == FileName)
                    name = ReadString(source, field);
                else if (field.Id == FileMimeType)
                    mime = ReadString(source, field);
                else if (field.Id == FileData)
                    data = field;
            }
            if (data is not { } image || !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                continue;
            long length = image.End - image.DataStart;
            if (length is <= 0 or > TagLimits.MaxFieldBytes)
                continue;
            bool front = name.StartsWith("cover", StringComparison.OrdinalIgnoreCase) && !name.Contains("small", StringComparison.OrdinalIgnoreCase);
            var picture = new EmbeddedPicture(mime.ToLowerInvariant(), front, image.DataStart, (int)length);
            if (options.HasFlag(TagReadOptions.PictureData))
                picture = picture with { Data = source.ReadArray(image.DataStart, (int)length) };
            tags.OfferPicture(picture);
        }
    }

    private static IEnumerable<Element> Children(ByteSource source, Element parent)
    {
        long position = parent.DataStart;
        while (position < parent.End && TryReadElement(source, position, parent.End, out var child))
        {
            yield return child;
            position = child.End;
        }
    }

    /// <summary>Reads an element ID (marker bits kept) and size (marker removed); unknown sizes extend to <paramref name="limit"/>.</summary>
    private static bool TryReadElement(ByteSource source, long position, long limit, out Element element)
    {
        element = default;
        Span<byte> header = stackalloc byte[12];
        int read = source.Read(position, header[..(int)Math.Min(12, limit - position)]);
        if (read < 2)
            return false;
        int idLength = VintLength(header[0]);
        if (idLength is 0 or > 4 || idLength >= read)
            return false;
        uint id = 0;
        for (int i = 0; i < idLength; i++)
            id = id << 8 | header[i];
        int sizeLength = VintLength(header[idLength]);
        if (sizeLength == 0 || idLength + sizeLength > read)
            return false;
        ulong size = (ulong)(header[idLength] & (0xFF >> sizeLength));
        bool unknown = size == (ulong)(0xFF >> sizeLength);
        for (int i = 1; i < sizeLength; i++)
        {
            size = size << 8 | header[idLength + i];
            unknown &= header[idLength + i] == 0xFF;
        }
        long dataStart = position + idLength + sizeLength;
        long end = unknown ? limit : dataStart + (long)Math.Min(size, (ulong)long.MaxValue / 2);
        if (end > limit)
            end = limit;
        element = new Element(id, dataStart, end);
        return true;
    }

    private static int VintLength(byte first) => first == 0 ? 0 : System.Numerics.BitOperations.LeadingZeroCount((uint)first) - 23;

    private static ulong ReadUnsigned(ByteSource source, Element element)
    {
        int length = (int)Math.Min(8, element.End - element.DataStart);
        Span<byte> bytes = stackalloc byte[8];
        source.ReadExactly(element.DataStart, bytes[..length]);
        ulong value = 0;
        for (int i = 0; i < length; i++)
            value = value << 8 | bytes[i];
        return value;
    }

    private static double ReadFloat(ByteSource source, Element element)
    {
        long length = element.End - element.DataStart;
        Span<byte> bytes = stackalloc byte[8];
        if (length == 4)
        {
            source.ReadExactly(element.DataStart, bytes[..4]);
            return BinaryPrimitives.ReadSingleBigEndian(bytes);
        }
        if (length == 8)
        {
            source.ReadExactly(element.DataStart, bytes);
            return BinaryPrimitives.ReadDoubleBigEndian(bytes);
        }
        return 0;
    }

    private static string ReadString(ByteSource source, Element element)
    {
        long length = element.End - element.DataStart;
        if (length is <= 0 or > TagLimits.MaxTextBytes)
            return "";
        return Encoding.UTF8.GetString(source.ReadArray(element.DataStart, (int)length)).TrimEnd('\0');
    }
}
