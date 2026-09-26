using System.Text;
using Fermata.Library;

namespace Fermata.Tests;

/// <summary>Writes a large synthetic library of tiny tagged MP3 files.</summary>
/// <remarks>Run with <c>dotnet run --project tests/Fermata.Tests -- --make-large-library DIR TRACKS</c>.</remarks>
internal static class LargeLibrary
{
    private static readonly string[] Words =
    [
        "Amber", "Blue", "Crimson", "Distant", "Echo", "Falling", "Golden", "Hollow", "Iron", "Jade", "Kindred", "Lunar",
        "Midnight", "Northern", "Open", "Paper", "Quiet", "River", "Silver", "Tidal", "Under", "Velvet", "Winter", "Young",
        "Glass", "Harbor", "Lantern", "Signal", "Static", "Garden", "Ocean", "Canyon", "Meadow", "Ember", "Satellite", "Hymn",
    ];

    private static readonly string[] Genres = ["Rock", "Pop", "Jazz", "Electronic", "Ambient", "Folk", "Classical", "Hip-Hop", "Metal", "Soul"];

    public static int Run(string directory, int tracks)
    {
        var random = new Random(2024);
        byte[] frame = Frame();
        byte[] cover = TinyJpeg();
        const int TracksPerAlbum = 10, AlbumsPerArtist = 3;
        int albums = (tracks + TracksPerAlbum - 1) / TracksPerAlbum;
        int written = 0;
        for (int album = 0; album < albums && written < tracks; album++)
        {
            int artist = album / AlbumsPerArtist;
            string artistName = $"{Words[artist % Words.Length]} {Words[artist / Words.Length % Words.Length]} {artist}";
            string albumName = $"{Words[random.Next(Words.Length)]} {Words[random.Next(Words.Length)]} {album}";
            int year = 1965 + random.Next(60);
            string genre = Genres[random.Next(Genres.Length)];
            string folder = Path.Combine(directory, Safe(artistName), $"{year} - {Safe(albumName)}");
            Directory.CreateDirectory(folder);
            for (int number = 1; number <= TracksPerAlbum && written < tracks; number++, written++)
            {
                string title = $"{Words[random.Next(Words.Length)]} {Words[random.Next(Words.Length)]} {written}";
                var tag = new List<byte>();
                void Text(string id, string value) => AddFrame(tag, id, [3, .. Encoding.UTF8.GetBytes(value)]);
                Text("TIT2", title);
                Text("TPE1", artistName);
                Text("TPE2", artistName);
                Text("TALB", albumName);
                Text("TDRC", year.ToString());
                Text("TRCK", $"{number}/{TracksPerAlbum}");
                Text("TCON", genre);
                if (album % 5 == 0)
                    AddFrame(tag, "APIC", [0, .. "image/jpeg"u8, 0, 3, 0, .. cover]);
                byte[] header = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0];
                SyncSafe(header.AsSpan(6), tag.Count);
                File.WriteAllBytes(Path.Combine(folder, $"{number:00} - {Safe(title)}.mp3"), [.. header, .. tag, .. frame]);
            }
        }
        Console.WriteLine($"Wrote {written} tracks in {albums} albums to {directory}");
        return 0;
    }

    /// <summary>Times a first scan and an unchanged rescan, with the memory each allocates.</summary>
    /// <remarks>Run with <c>dotnet run --project tests/Fermata.Tests -c Release -- --measure-scan DIR</c>.</remarks>
    public static int MeasureScan(string directory)
    {
        LibrarySnapshot previous = LibrarySnapshot.Empty;
        foreach (string pass in (string[])["first scan", "rescan", "rescan", "rescan"])
        {
            GC.Collect();
            long allocated = GC.GetTotalAllocatedBytes(precise: true);
            var result = LibraryScanner.Scan([directory], previous, null, CancellationToken.None);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Console.WriteLine($"{pass,-10}  {result.Snapshot.Tracks.Count} tracks  {result.Elapsed.TotalMilliseconds,6:0} ms  "
                + $"allocated {allocated / 1048576.0,6:0.0} MB  changed {result.Changed}");
            previous = result.Snapshot;
        }
        GC.Collect();
        Console.WriteLine($"heap with the library: {GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:0.0} MB");
        return 0;
    }

    private static void AddFrame(List<byte> tag, string id, byte[] content)
    {
        tag.AddRange(Encoding.ASCII.GetBytes(id));
        byte[] size = new byte[4];
        SyncSafe(size, content.Length);
        tag.AddRange(size);
        tag.Add(0);
        tag.Add(0);
        tag.AddRange(content);
    }

    private static void SyncSafe(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 21 & 0x7F);
        target[1] = (byte)(value >> 14 & 0x7F);
        target[2] = (byte)(value >> 7 & 0x7F);
        target[3] = (byte)(value & 0x7F);
    }

    /// <summary>Two MPEG-1 Layer III frames at 128 kbps with an Info header claiming a four-minute length.</summary>
    private static byte[] Frame()
    {
        var bytes = new byte[417 * 2];
        foreach (int start in (int[])[0, 417])
        {
            bytes[start] = 0xFF;
            bytes[start + 1] = 0xFB;
            bytes[start + 2] = 0x90;
            bytes[start + 3] = 0x00;
        }
        // Xing/Info header after the side information (32 bytes for MPEG-1 stereo).
        "Info"u8.CopyTo(bytes.AsSpan(36));
        bytes[43] = 1; // frame count present
        int frames = (int)(240 * 44100 / 1152.0);
        bytes[44] = (byte)(frames >> 24);
        bytes[45] = (byte)(frames >> 16);
        bytes[46] = (byte)(frames >> 8);
        bytes[47] = (byte)frames;
        return bytes;
    }

    private static byte[] TinyJpeg()
    {
        string path = Path.Combine(Fixtures.Directory("large"), "cover.jpg");
        Fixtures.Ffmpeg($"-f lavfi -i gradients=s=300x300:c0=0x3050a0:c1=0xe07040:duration=1 -frames:v 1 -q:v 6 \"{path}\"");
        return File.ReadAllBytes(path);
    }

    private static string Safe(string name) => string.Concat(name.Select(c => c is '/' or '\0' ? '_' : c));
}
