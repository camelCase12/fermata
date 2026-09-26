using System.Text;
using Fermata.Storage;

namespace Fermata.Library;

/// <summary>A binary copy of the library, read at startup.</summary>
/// <remarks>
/// Repeated strings are stored once in a string table. A missing, stale or unreadable cache only means
/// a full scan.
/// </remarks>
public static class LibraryCache
{
    private static ReadOnlySpan<byte> Magic => "FERMATA-LIBRARY\n"u8;
    private const int FormatVersion = 1;

    private enum TrackFlags : byte { Compilation = 1, HasLyrics = 2, HasEmbeddedArt = 4 }

    public static void Save(string path, LibrarySnapshot snapshot)
    {
        AtomicFile.Write(path, stream =>
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(FormatVersion);

            // Index 0 is null, 1 is the empty string.
            var table = new Dictionary<string, int>(StringComparer.Ordinal) { [""] = 1 };
            var strings = new List<string> { "", "" };
            int Index(string? value)
            {
                if (value is null)
                    return 0;
                if (!table.TryGetValue(value, out int index))
                {
                    table[value] = index = strings.Count;
                    strings.Add(value);
                }
                return index;
            }

            // Collect strings first so the table can be written before the records that use it.
            var tracks = snapshot.Tracks;
            var images = snapshot.FolderImages.ToArray();
            foreach (var (directory, image) in images)
            {
                Index(directory);
                Index(image);
            }
            foreach (var track in tracks)
                VisitStrings(track, value => Index(value));

            writer.Write7BitEncodedInt(strings.Count);
            for (int i = 2; i < strings.Count; i++)
                writer.Write(strings[i]);

            writer.Write7BitEncodedInt(images.Length);
            foreach (var (directory, image) in images)
            {
                writer.Write7BitEncodedInt(Index(directory));
                writer.Write7BitEncodedInt(Index(image));
            }

            writer.Write7BitEncodedInt(tracks.Count);
            foreach (var track in tracks)
            {
                writer.Write7BitEncodedInt(Index(Path.GetDirectoryName(track.Path)));
                writer.Write7BitEncodedInt(Index(Path.GetFileName(track.Path)));
                writer.Write7BitEncodedInt64(track.FileSize);
                writer.Write(track.Modified.Ticks);
                writer.Write(track.Added.Ticks);
                writer.Write7BitEncodedInt(Index(track.Title));
                writer.Write7BitEncodedInt(Index(track.Artist));
                WriteList(writer, track.ArtistNames, Index);
                writer.Write7BitEncodedInt(Index(track.AlbumArtist));
                writer.Write7BitEncodedInt(Index(track.AlbumTitle));
                WriteList(writer, track.Genres, Index);
                writer.Write7BitEncodedInt(Index(track.Composer));
                writer.Write7BitEncodedInt(track.Year);
                writer.Write7BitEncodedInt(track.TrackNumber);
                writer.Write7BitEncodedInt(track.TrackCount);
                writer.Write7BitEncodedInt(track.DiscNumber);
                writer.Write7BitEncodedInt(track.DiscCount);
                var flags = (track.Compilation ? TrackFlags.Compilation : 0) | (track.HasLyrics ? TrackFlags.HasLyrics : 0)
                    | (track.EmbeddedArt is not null ? TrackFlags.HasEmbeddedArt : 0);
                writer.Write((byte)flags);
                writer.Write7BitEncodedInt(Index(track.TitleSort));
                writer.Write7BitEncodedInt(Index(track.ArtistSort));
                writer.Write7BitEncodedInt(Index(track.AlbumSort));
                writer.Write7BitEncodedInt(Index(track.AlbumArtistSort));
                writer.Write7BitEncodedInt(Index(track.MusicBrainzAlbumId));
                writer.Write7BitEncodedInt64(track.Duration.Ticks);
                writer.Write7BitEncodedInt(track.Bitrate);
                writer.Write7BitEncodedInt(track.SampleRate);
                writer.Write7BitEncodedInt(track.Channels);
                writer.Write7BitEncodedInt(track.BitsPerSample);
                writer.Write7BitEncodedInt(Index(track.Codec));
                if (track.EmbeddedArt is { } art)
                {
                    writer.Write7BitEncodedInt64(art.Offset + 1); // -1 (decode from tags) becomes 0
                    writer.Write7BitEncodedInt(art.Length);
                }
            }
        });
    }

    /// <summary>Loads the cache, keeping tracks beneath <paramref name="roots"/>.</summary>
    /// <returns>The cached library, or null when the file is missing or unusable.</returns>
    public static LibrarySnapshot? Load(string path, IReadOnlyList<string> roots)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            Span<byte> magic = stackalloc byte[Magic.Length];
            reader.BaseStream.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
                return null;

            int stringCount = reader.ReadStringCount();
            var strings = new string?[stringCount];
            strings[0] = null;
            strings[1] = "";
            for (int i = 2; i < stringCount; i++)
                strings[i] = reader.ReadString();
            string? String(BinaryReader r) => strings[r.Read7BitEncodedInt()];
            string Required(BinaryReader r) => String(r) ?? "";

            int imageCount = reader.Read7BitEncodedInt();
            var images = new Dictionary<string, string>(imageCount, StringComparer.Ordinal);
            for (int i = 0; i < imageCount; i++)
            {
                string directory = Required(reader);
                images[directory] = Required(reader);
            }

            var prefixes = roots.Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar).ToArray();
            var albumKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            var singles = new Dictionary<string, string[]>(StringComparer.Ordinal);
            int trackCount = reader.Read7BitEncodedInt();
            var tracks = new List<Track>(trackCount);
            for (int i = 0; i < trackCount; i++)
            {
                string trackPath = Path.Join(Required(reader), Required(reader));
                long size = reader.Read7BitEncodedInt64();
                var modified = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                var added = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
                string title = Required(reader);
                string artist = Required(reader);
                var artistNames = ReadList(reader, strings, singles);
                string albumArtist = Required(reader);
                string album = Required(reader);
                var genres = ReadList(reader, strings, singles);
                string composer = Required(reader);
                int year = reader.Read7BitEncodedInt(), number = reader.Read7BitEncodedInt(), count = reader.Read7BitEncodedInt();
                int disc = reader.Read7BitEncodedInt(), discs = reader.Read7BitEncodedInt();
                var flags = (TrackFlags)reader.ReadByte();
                string? titleSort = String(reader), artistSort = String(reader), albumSort = String(reader);
                string? albumArtistSort = String(reader), musicBrainz = String(reader);
                var duration = TimeSpan.FromTicks(reader.Read7BitEncodedInt64());
                int bitrate = reader.Read7BitEncodedInt(), sampleRate = reader.Read7BitEncodedInt();
                int channels = reader.Read7BitEncodedInt(), bits = reader.Read7BitEncodedInt();
                string codec = Required(reader);
                ArtSource? art = null;
                if ((flags & TrackFlags.HasEmbeddedArt) != 0)
                {
                    long offset = reader.Read7BitEncodedInt64() - 1;
                    art = new ArtSource(trackPath, offset, reader.Read7BitEncodedInt());
                }
                if (!prefixes.Any(prefix => trackPath.StartsWith(prefix, StringComparison.Ordinal)))
                    continue;
                tracks.Add(new Track
                {
                    Path = trackPath,
                    FileSize = size,
                    Modified = modified,
                    Added = added,
                    Title = title,
                    Artist = artist,
                    ArtistNames = artistNames,
                    AlbumArtist = albumArtist,
                    AlbumTitle = album,
                    Genres = genres,
                    Composer = composer,
                    Year = year,
                    TrackNumber = number,
                    TrackCount = count,
                    DiscNumber = disc,
                    DiscCount = discs,
                    Compilation = (flags & TrackFlags.Compilation) != 0,
                    HasLyrics = (flags & TrackFlags.HasLyrics) != 0,
                    TitleSort = titleSort,
                    ArtistSort = artistSort,
                    AlbumSort = albumSort,
                    AlbumArtistSort = albumArtistSort,
                    MusicBrainzAlbumId = musicBrainz,
                    Duration = duration,
                    Bitrate = bitrate,
                    SampleRate = sampleRate,
                    Channels = channels,
                    BitsPerSample = bits,
                    Codec = codec,
                    EmbeddedArt = art,
                    AlbumKey = Intern(albumKeys, AlbumGrouping.Key(trackPath, album, musicBrainz)),
                });
            }
            return LibrarySnapshot.Build(tracks, images);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or EndOfStreamException
            or IndexOutOfRangeException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static int ReadStringCount(this BinaryReader reader)
    {
        int count = reader.Read7BitEncodedInt();
        if (count < 2 || count > 50_000_000)
            throw new InvalidDataException("Bad string table.");
        return count;
    }

    private static void VisitStrings(Track track, Action<string?> visit)
    {
        visit(Path.GetDirectoryName(track.Path));
        visit(Path.GetFileName(track.Path));
        visit(track.Title);
        visit(track.Artist);
        foreach (string name in track.ArtistNames)
            visit(name);
        visit(track.AlbumArtist);
        visit(track.AlbumTitle);
        foreach (string genre in track.Genres)
            visit(genre);
        visit(track.Composer);
        visit(track.TitleSort);
        visit(track.ArtistSort);
        visit(track.AlbumSort);
        visit(track.AlbumArtistSort);
        visit(track.MusicBrainzAlbumId);
        visit(track.Codec);
    }

    private static void WriteList(BinaryWriter writer, IReadOnlyList<string> values, Func<string?, int> index)
    {
        writer.Write7BitEncodedInt(values.Count);
        foreach (string value in values)
            writer.Write7BitEncodedInt(index(value));
    }

    private static string? Intern(Dictionary<string, string> pool, string? value)
    {
        if (value is null)
            return null;
        if (pool.TryGetValue(value, out string? existing))
            return existing;
        pool[value] = value;
        return value;
    }

    private static IReadOnlyList<string> ReadList(BinaryReader reader, string?[] strings, Dictionary<string, string[]> singles)
    {
        int count = reader.Read7BitEncodedInt();
        if (count == 0)
            return [];
        if (count == 1)
        {
            string value = strings[reader.Read7BitEncodedInt()] ?? "";
            if (!singles.TryGetValue(value, out string[]? single))
                singles[value] = single = [value];
            return single;
        }
        var values = new string[count];
        for (int i = 0; i < count; i++)
            values[i] = strings[reader.Read7BitEncodedInt()] ?? "";
        return values;
    }
}
