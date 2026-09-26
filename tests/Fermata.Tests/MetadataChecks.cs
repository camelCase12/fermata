using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Fermata.Metadata;

namespace Fermata.Tests;

internal static class MetadataChecks
{
    public static void Run(Checks check)
    {
        SyntheticId3(check);
        EncodedFixtures(check);
    }

    /// <summary>Unusual ID3 layouts found in real files.</summary>
    private static void SyntheticId3(Checks check)
    {
        byte[] frame = Mp3Frame();

        var v24 = new Id3Builder(4)
            .Text("TIT2", "Café Night", encoding: 3)
            .Text("TPE1", "Alpha\0Beta", encoding: 3)
            .Text("TPE2", "Alpha", encoding: 0)
            .Text("TALB", "Nocturnes", encoding: 1)
            .Text("TDRC", "2011-04-02", encoding: 0)
            .Text("TDOR", "1999", encoding: 0)
            .Text("TRCK", "4/11", encoding: 0)
            .Text("TPOS", "2/2", encoding: 0)
            .Text("TCON", "(17)\0Shoegaze", encoding: 3)
            .Text("TCMP", "1", encoding: 0)
            .UserText("MusicBrainz Album Id", "4f7e-album")
            .UserText("ARTISTS", "Alpha\0Beta")
            .Picture(type: 0, [0xFF, 0xD8, 0x00, 0x01])
            .Picture(type: 3, [0xFF, 0xD8, 0x00, 0x02, 0x03])
            .Build();
        var tags = Parse([.. v24, .. frame]);
        check.Equal("Café Night", tags?.Title, "v2.4 UTF-8 title");
        check.Equal("Alpha, Beta", tags?.Artist, "v2.4 multi-value artist credit");
        check.That(tags?.ArtistNames.SequenceEqual(["Alpha", "Beta"]) == true, "v2.4 ARTISTS names");
        check.Equal("Nocturnes", tags?.Album, "v2.4 UTF-16 album");
        check.Equal(2011, tags?.Year, "TDRC year");
        check.Equal(1999, tags?.OriginalYear, "TDOR original year");
        check.Equal("4/11 2/2", $"{tags?.TrackNumber}/{tags?.TrackCount} {tags?.DiscNumber}/{tags?.DiscCount}", "track and disc numbers");
        check.That(tags?.Genres.SequenceEqual(["Rock", "Shoegaze"]) == true, "genre reference and text");
        check.That(tags?.Compilation == true, "iTunes compilation flag");
        check.Equal("4f7e-album", tags?.MusicBrainzAlbumId, "TXXX MusicBrainz id");
        check.Equal(5, tags?.Picture?.Length, "front cover preferred over other picture types");
        check.That(tags?.Picture?.IsFrontCover == true && tags.Picture.Offset > 0, "front cover addressable");
        check.Equal("MP3", tags?.Codec, "MPEG stream after tag");

        // ID3v2.3 with whole-tag unsynchronization: frame bytes contain FF 00 pairs.
        var unsynchronized = new Id3Builder(3, unsynchronize: true)
            .Text("TIT2", "ÿÿ Unsync ÿ", encoding: 0) // Latin-1 ÿ is 0xFF
            .Text("TPE1", "Band", encoding: 0)
            .Picture(type: 3, [0xFF, 0x00, 0xFF, 0xE0, 0x42])
            .Build();
        tags = Parse([.. unsynchronized, .. frame]);
        check.Equal("ÿÿ Unsync ÿ", tags?.Title, "unsynchronized v2.3 text");
        check.Equal("Band", tags?.Artist, "frame after unsynchronized frame");
        check.Equal(-1L, tags?.Picture?.Offset, "unsynchronized picture is not addressable");
        byte[]? picture = ParsePicture([.. unsynchronized, .. frame]);
        check.That(picture is not null && picture.SequenceEqual(new byte[] { 0xFF, 0x00, 0xFF, 0xE0, 0x42 }), "unsynchronized picture decoded");

        // ID3v2.2: three-letter frames and PIC with an image format instead of a MIME type.
        var v22 = new Id3Builder(2).Text("TT2", "Old Tag", 0).Text("TP1", "Old Band", 0).Text("TCO", "(8)", 0)
            .Picture(type: 3, [1, 2, 3]).Build();
        tags = Parse([.. v22, .. frame]);
        check.Equal("Old Tag", tags?.Title, "v2.2 title");
        check.Equal("Old Band", tags?.Artist, "v2.2 artist");
        check.That(tags?.Genres.SequenceEqual(["Jazz"]) == true, "v2.2 numeric genre");
        check.Equal(3, tags?.Picture?.Length, "v2.2 picture");

        // ID3v2.3 zlib-compressed frame.
        var compressed = new Id3Builder(3).CompressedText("TIT2", "Squeezed Title").Text("TPE1", "Zip", 0).Build();
        tags = Parse([.. compressed, .. frame]);
        check.Equal("Squeezed Title", tags?.Title, "compressed v2.3 frame");
        check.Equal("Zip", tags?.Artist, "frame after compressed frame");

        // ID3v2.4 with plain, not sync-safe, frame sizes.
        var plainSizes = new Id3Builder(4, plainFrameSizes: true)
            .Text("TIT2", new string('x', 200), 0).Text("TPE1", "After Big Frame", 0).Build();
        tags = Parse([.. plainSizes, .. frame]);
        check.Equal(200, tags?.Title?.Length, "v2.4 plain-size frame");
        check.Equal("After Big Frame", tags?.Artist, "frame after plain-size frame");

        // ID3v1.1 fills gaps only.
        byte[] v1 = Id3v1("V1 Title", "V1 Artist", "V1 Album", "1987", track: 9, genre: 17);
        tags = Parse([.. new Id3Builder(3).Text("TIT2", "V2 Title", 0).Build(), .. frame, .. v1]);
        check.Equal("V2 Title", tags?.Title, "ID3v2 wins over ID3v1");
        check.Equal("V1 Artist", tags?.Artist, "ID3v1 fills a missing artist");
        check.Equal(9, tags?.TrackNumber, "ID3v1.1 track");
        check.Equal(1987, tags?.Year, "ID3v1 year");

        // Garbage and truncated input must not throw.
        Parse([0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0x7F, 0x7F]); // A truncated tag does not throw.
        check.That(Parse(new byte[64]) is null, "zeros are not audio");
        check.That(Parse("OggS"u8.ToArray()) is null, "truncated Ogg");
        var random = new Random(5);
        int survived = 0;
        for (int i = 0; i < 400; i++)
        {
            byte[] noise = new byte[random.Next(4, 600)];
            random.NextBytes(noise);
            ReadOnlySpan<byte> prefix = (i % 4) switch { 0 => "ID3"u8, 1 => "fLaC"u8, 2 => "OggS"u8, _ => "RIFF"u8 };
            prefix.CopyTo(noise);
            try
            {
                TagReader.Read(new MemoryByteSource(noise), TagReadOptions.Lyrics | TagReadOptions.PictureData);
                survived++;
            }
            catch (Exception error)
            {
                check.That(false, $"random input {i} threw {error.GetType().Name}: {error.Message}");
            }
        }
        check.Equal(400, survived, "random inputs parse without exceptions");
    }

    /// <summary>Real encoder output for every supported container.</summary>
    private static void EncodedFixtures(Checks check)
    {
        if (!Fixtures.FfmpegAvailable)
        {
            Console.WriteLine("  (ffmpeg not found; skipping encoded fixtures)");
            return;
        }
        string directory = Fixtures.Directory("metadata");
        string cover = Path.Combine(directory, "cover.jpg");
        Fixtures.Ffmpeg($"-f lavfi -i color=c=0x336699:s=64x64:d=1 -frames:v 1 \"{cover}\"");

        (string Name, string Arguments, bool EmbeddedPicture, string Codec)[] formats =
        [
            ("flac.flac", "-c:a flac", true, "FLAC"),
            ("v23.mp3", "-c:a libmp3lame -q:a 7 -id3v2_version 3", true, "MP3"),
            ("v24.mp3", "-c:a libmp3lame -b:a 128k -id3v2_version 4", true, "MP3"),
            ("aac.m4a", "-c:a aac -b:a 96k", true, "AAC"),
            ("alac.m4a", "-c:a alac", true, "ALAC"),
            ("opus.opus", "-c:a libopus -b:a 48k", false, "Opus"),
            ("vorbis.ogg", "-c:a libvorbis -q:a 2", false, "Vorbis"),
            ("oggflac.oga", "-c:a flac", false, "FLAC"),
            ("wavpack.wv", "-c:a wavpack", false, "WavPack"),
            ("pcm.wav", "-c:a pcm_s16le", false, "PCM"),
            ("pcm.aiff", "-c:a pcm_s16be -write_id3v2 1", false, "PCM"),
            // ffmpeg stores cover art in WMA and MKA as a video stream.
            ("wma.wma", "-c:a wmav2 -b:a 64k", false, "WMA"),
            ("opus.mka", "-c:a libopus -b:a 48k", false, "Opus"),
            ("opus.webm", "-c:a libopus -b:a 48k", false, "Opus"),
            ("tta.tta", "-c:a tta", false, "TTA"),
            ("adts.aac", "-c:a aac -b:a 64k -write_id3v2 1", false, "AAC"),
        ];
        foreach (var (name, arguments, embed, codec) in formats)
        {
            string path = Path.Combine(directory, name);
            string picture = embed ? $"-i \"{cover}\" -map 0:a -map 1:v -c:v copy -disposition:v attached_pic" : "";
            string tagArguments = name.EndsWith(".wav") || name.EndsWith(".webm") || name.EndsWith(".aac")
                ? "-metadata title=\"Fixture Title\" -metadata artist=\"Fixture Artist\" -metadata album=\"Fixture Album\" -metadata date=2020"
                : "-metadata title=\"Fixture Title\" -metadata artist=\"Fixture Artist\" -metadata album=\"Fixture Album\" "
                  + "-metadata album_artist=\"Fixture Band\" -metadata date=2020 -metadata track=3/9 -metadata disc=1/2 -metadata genre=\"Trip-Hop\"";
            if (name.EndsWith(".wv"))
                tagArguments += " -metadata albumartist=\"Fixture Band\"";
            Fixtures.Ffmpeg($"-f lavfi -i sine=frequency=330:sample_rate=44100:duration=2.5 {picture} {arguments} {tagArguments} \"{path}\"");
            AudioTags? tags;
            try
            {
                tags = TagReader.Read(path);
            }
            catch (Exception error)
            {
                check.That(false, $"{name}: threw {error}");
                continue;
            }
            if (tags is null)
            {
                check.That(false, $"{name}: not recognized");
                continue;
            }
            check.Equal("Fixture Title", tags.Title, $"{name} title");
            check.Equal("Fixture Artist", tags.Artist, $"{name} artist");
            if (!name.EndsWith(".aac"))
                check.Equal("Fixture Album", tags.Album, $"{name} album");
            check.Equal(2020, tags.Year, $"{name} year");
            check.Equal(codec, tags.Codec, $"{name} codec");
            check.Near(2.5, tags.Duration.TotalSeconds, name.EndsWith(".aac") || name.EndsWith(".mp3") || name.EndsWith(".wma") ? 0.15 : 0.03, $"{name} duration");
            check.That(tags.SampleRate is 44100 or 48000, $"{name} sample rate {tags.SampleRate}");
            check.That(tags.Channels == 1, $"{name} channels {tags.Channels}");
            if (!name.EndsWith(".wav") && !name.EndsWith(".webm") && !name.EndsWith(".aac") && !name.EndsWith(".tta"))
            {
                check.Equal("Fixture Band", tags.AlbumArtist, $"{name} album artist");
                check.Equal(3, tags.TrackNumber, $"{name} track number");
                check.Equal(9, tags.TrackCount, $"{name} track count");
                check.That(tags.Genres.Contains("Trip-Hop"), $"{name} genre [{string.Join(",", tags.Genres)}]");
            }
            if (embed)
            {
                check.That(tags.Picture is not null, $"{name} embedded picture");
                byte[]? image = TagReader.ReadPicture(path);
                check.That(image is { Length: > 100 } && image[0] == 0xFF && image[1] == 0xD8, $"{name} picture bytes are a JPEG");
                if (tags.Picture is { Offset: >= 0 } located)
                {
                    using var file = File.OpenRead(path);
                    file.Position = located.Offset;
                    var direct = new byte[located.Length];
                    file.ReadExactly(direct);
                    check.That(image is not null && direct.AsSpan().SequenceEqual(image), $"{name} picture offset addresses the image");
                }
            }
        }

        // Lyrics and Base64 pictures in Vorbis comments, read only when asked.
        string lyricPath = Path.Combine(directory, "lyrics.flac");
        Fixtures.Ffmpeg($"-f lavfi -i sine=d=1 -c:a flac -metadata title=L -metadata LYRICS=\"line one\nline two\" -metadata ARTIST=First -metadata ARTISTS=\"First;Second\" \"{lyricPath}\"");
        var withoutLyrics = TagReader.Read(lyricPath);
        var withLyrics = TagReader.Read(lyricPath, TagReadOptions.Lyrics);
        check.That(withoutLyrics is { HasLyrics: true, Lyrics: null }, "lyrics are detected without being read");
        check.Equal("line one\nline two", withLyrics?.Lyrics, "lyrics read on request");

        string opusPicture = Path.Combine(directory, "picture.opus");
        byte[] jpeg = File.ReadAllBytes(cover);
        Fixtures.Ffmpeg($"-f lavfi -i sine=d=1 -c:a libopus -metadata title=P -metadata METADATA_BLOCK_PICTURE={PictureBlock(jpeg)} \"{opusPicture}\"");
        var opus = TagReader.Read(opusPicture);
        check.That(opus?.Picture is { Offset: -1, IsFrontCover: true }, "Base64 picture detected as front cover");
        check.That(TagReader.ReadPicture(opusPicture)?.SequenceEqual(jpeg) == true, "Base64 picture decoded exactly");
    }

    private static AudioTags? Parse(byte[] file) => TagReader.Read(new MemoryByteSource(file), TagReadOptions.Lyrics);

    private static byte[]? ParsePicture(byte[] file) =>
        TagReader.Read(new MemoryByteSource(file), TagReadOptions.PictureData)?.Picture?.Data;

    /// <summary>Two consecutive silent MPEG-1 Layer III frames (128 kbps, 44.1 kHz, stereo).</summary>
    private static byte[] Mp3Frame()
    {
        var frame = new byte[417 * 2];
        foreach (int start in (int[])[0, 417])
        {
            frame[start] = 0xFF;
            frame[start + 1] = 0xFB;
            frame[start + 2] = 0x90;
            frame[start + 3] = 0x00;
        }
        return frame;
    }

    private static byte[] Id3v1(string title, string artist, string album, string year, byte track, byte genre)
    {
        var tag = new byte[128];
        "TAG"u8.CopyTo(tag);
        Encoding.Latin1.GetBytes(title).CopyTo(tag, 3);
        Encoding.Latin1.GetBytes(artist).CopyTo(tag, 33);
        Encoding.Latin1.GetBytes(album).CopyTo(tag, 63);
        Encoding.Latin1.GetBytes(year).CopyTo(tag, 93);
        tag[125] = 0;
        tag[126] = track;
        tag[127] = genre;
        return tag;
    }

    private static string PictureBlock(byte[] image)
    {
        var block = new List<byte>();
        void Int(uint value) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); block.AddRange(b); }
        Int(3);
        byte[] mime = "image/jpeg"u8.ToArray();
        Int((uint)mime.Length);
        block.AddRange(mime);
        Int(0);
        Int(64); Int(64); Int(24); Int(0);
        Int((uint)image.Length);
        block.AddRange(image);
        return Convert.ToBase64String(block.ToArray());
    }

    /// <summary>Builds ID3v2 tags byte by byte.</summary>
    private sealed class Id3Builder(int version, bool unsynchronize = false, bool plainFrameSizes = false)
    {
        private readonly List<byte> frames = [];

        public Id3Builder Text(string id, string value, byte encoding)
        {
            byte[] text = encoding switch
            {
                0 => Encoding.Latin1.GetBytes(value),
                1 => [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(value)],
                2 => Encoding.BigEndianUnicode.GetBytes(value),
                _ => Encoding.UTF8.GetBytes(value),
            };
            return Frame(id, [encoding, .. text]);
        }

        public Id3Builder UserText(string description, string value) =>
            Frame(version == 2 ? "TXX" : "TXXX", [3, .. Encoding.UTF8.GetBytes(description), 0, .. Encoding.UTF8.GetBytes(value)]);

        public Id3Builder Picture(byte type, byte[] data)
        {
            if (version == 2)
                return Frame("PIC", [0, .. "JPG"u8, type, 0, .. data]);
            return Frame("APIC", [0, .. "image/jpeg"u8, 0, type, .. "desc"u8, 0, .. data]);
        }

        public Id3Builder CompressedText(string id, string value)
        {
            byte[] raw = [0, .. Encoding.Latin1.GetBytes(value)];
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            byte[] size = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(size, (uint)raw.Length);
            return Frame(id, [.. size, .. output.ToArray()], flags: 0x0080);
        }

        private Id3Builder Frame(string id, byte[] content, ushort flags = 0)
        {
            frames.AddRange(Encoding.ASCII.GetBytes(id));
            if (version == 2)
            {
                frames.Add((byte)(content.Length >> 16));
                frames.Add((byte)(content.Length >> 8));
                frames.Add((byte)content.Length);
            }
            else
            {
                byte[] size = new byte[4];
                if (version == 4 && !plainFrameSizes)
                    SyncSafe(size, content.Length);
                else
                    BinaryPrimitives.WriteUInt32BigEndian(size, (uint)content.Length);
                frames.AddRange(size);
                frames.Add((byte)(flags >> 8));
                frames.Add((byte)flags);
            }
            frames.AddRange(content);
            return this;
        }

        public byte[] Build()
        {
            byte[] body = [.. frames, .. new byte[16]]; // padding
            if (unsynchronize)
            {
                var encoded = new List<byte>(body.Length);
                for (int i = 0; i < body.Length; i++)
                {
                    encoded.Add(body[i]);
                    if (body[i] == 0xFF && (i + 1 == body.Length || body[i + 1] == 0 || (body[i + 1] & 0xE0) == 0xE0))
                        encoded.Add(0);
                }
                body = [.. encoded];
            }
            byte[] header = [(byte)'I', (byte)'D', (byte)'3', (byte)version, 0, (byte)(unsynchronize ? 0x80 : 0), 0, 0, 0, 0];
            SyncSafe(header.AsSpan(6), body.Length);
            return [.. header, .. body];
        }

        private static void SyncSafe(Span<byte> target, int value)
        {
            target[0] = (byte)(value >> 21 & 0x7F);
            target[1] = (byte)(value >> 14 & 0x7F);
            target[2] = (byte)(value >> 7 & 0x7F);
            target[3] = (byte)(value & 0x7F);
        }
    }
}

/// <summary>Scratch directories and ffmpeg invocation for generated fixtures.</summary>
internal static class Fixtures
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "fermata-tests-" + Environment.ProcessId);

    public static bool FfmpegAvailable { get; } = Environment.GetEnvironmentVariable("PATH")?.Split(':')
        .Any(directory => File.Exists(Path.Combine(directory, "ffmpeg"))) == true;

    public static string Directory(string name)
    {
        string path = Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { System.IO.Directory.Delete(Root, recursive: true); } catch (IOException) { }
        };
        return path;
    }

    public static void Ffmpeg(string arguments)
    {
        var start = new ProcessStartInfo("ffmpeg", "-loglevel error -y " + arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(start)!;
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg {arguments} failed: {errors}");
    }
}
