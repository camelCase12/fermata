using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using Fermata.Metadata;

namespace Fermata.Library;

public readonly record struct ScanProgress(int FilesFound, int FilesRead, int FilesToRead, bool Enumerating);

public sealed record ScanResult(LibrarySnapshot Snapshot, int Added, int Updated, int Removed, int Unreadable, TimeSpan Elapsed)
{
    public bool Changed => Added + Updated + Removed > 0;
}

/// <summary>Builds a library snapshot from music folders.</summary>
/// <remarks>
/// Files whose size and modification time match the previous snapshot are reused without being read.
/// New and changed files are read in parallel.
/// </remarks>
public static class LibraryScanner
{
    private static readonly string[] CoverNames = ["cover", "folder", "front", "album", "albumart", "albumartlarge", "thumb"];
    private static readonly FrozenExtensionSet ImageExtensions = new(".jpg", ".jpeg", ".png", ".webp", ".bmp");

    public static ScanResult Scan(IReadOnlyList<string> roots, LibrarySnapshot previous, IProgress<ScanProgress>? progress,
        CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        var files = new List<FoundFile>(previous.Tracks.Count);
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        long lastReport = 0;
        foreach (string root in roots.Distinct(StringComparer.Ordinal))
        {
            // Without a trailing separator, so that folder images are keyed like tracks' directories.
            if (Directory.Exists(root))
                Walk(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), depth: 0);
        }

        // Reuse unchanged tracks; queue the rest for reading.
        var tracks = new List<Track>(files.Count);
        var toRead = new List<FoundFile>();
        int updated = 0;
        foreach (var file in files)
        {
            var known = file.Known;
            if (known is not null && known.FileSize == file.Size && known.Modified == file.Modified)
                tracks.Add(known);
            else
            {
                toRead.Add(file with { Added = known?.Added ?? AddedTime(file.Path, file.Modified) });
                if (known is not null)
                    updated++;
            }
        }

        int read = 0, unreadable = 0;
        var parsed = new Track?[toRead.Count];
        // Every track of an album shares one key string, whichever worker read it.
        var albumKeys = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (track.AlbumKey is { } existing)
                albumKeys.TryAdd(existing, existing);
        }
        Func<string, string> internKey = key => albumKeys.GetOrAdd(key, key);
        var options = new ParallelOptions
        {
            CancellationToken = cancellation,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
        };
        Parallel.For(0, toRead.Count, options, () => new StringPool(), (index, _, pool) =>
        {
            var file = toRead[index];
            try
            {
                var tags = TagReader.Read(file.Path);
                if (tags is not { HasVideo: true })
                    parsed[index] = Track.FromTags(file.Path, tags, file.Size, file.Modified, file.Added, pool, internKey);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref unreadable);
            }
            int done = Interlocked.Increment(ref read);
            Report(done, toRead.Count, enumerating: false);
            return pool;
        }, _ => { });
        foreach (var track in parsed)
        {
            if (track is not null)
                tracks.Add(track);
        }

        // When nothing changed, the previous snapshot and its indexes are kept.
        if (toRead.Count == 0 && tracks.Count == previous.Tracks.Count && SameImages(images, previous.FolderImages))
        {
            progress?.Report(new ScanProgress(files.Count, 0, 0, false));
            return new ScanResult(previous, 0, 0, 0, unreadable, clock.Elapsed);
        }
        var snapshot = LibrarySnapshot.Build(tracks, images);
        int removed = previous.Tracks.Count(t => snapshot.FindTrack(t.Path) is null);
        progress?.Report(new ScanProgress(files.Count, toRead.Count, toRead.Count, false));
        return new ScanResult(snapshot, toRead.Count - updated, updated, removed, unreadable, clock.Elapsed);

        void Walk(string directory, int depth)
        {
            cancellation.ThrowIfCancellationRequested();
            if (depth > 48 || !visited.Add(directory))
                return;
            var subdirectories = new List<string>();
            string? bestImage = null, onlyImage = null;
            int bestRank = int.MaxValue, imageCount = 0;
            try
            {
                foreach (var entry in new FileSystemEnumerable<Entry>(directory, Classify, EnumerationOptions))
                {
                    switch (entry.Kind)
                    {
                        case EntryKind.Folder:
                            subdirectories.Add(entry.Path!);
                            break;
                        case EntryKind.FolderLink:
                            subdirectories.Add(Resolve(entry.Path!));
                            break;
                        case EntryKind.Audio:
                            files.Add(new FoundFile(entry.Path!, entry.Size, entry.Modified, entry.Known, default));
                            Report(0, 0, enumerating: true);
                            break;
                        case EntryKind.Image:
                            imageCount++;
                            onlyImage = entry.Path;
                            if (entry.CoverRank < bestRank)
                            {
                                bestRank = entry.CoverRank;
                                bestImage = entry.Path;
                            }
                            break;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return;
            }
            if (bestImage is not null)
                images[directory] = bestImage;
            else if (imageCount == 1)
                images[directory] = onlyImage!;
            subdirectories.Sort(StringComparer.Ordinal);
            foreach (string subdirectory in subdirectories)
                Walk(subdirectory, depth + 1);
        }

        // Runs for every directory entry and does not allocate for files it skips or already knows.
        Entry Classify(ref FileSystemEntry entry)
        {
            var name = entry.FileName;
            if (name.StartsWith('.'))
                return default; // hidden files and folders (.git, .thumbnails, …)
            if (entry.IsDirectory)
                return new Entry((entry.Attributes & FileAttributes.ReparsePoint) != 0 ? EntryKind.FolderLink : EntryKind.Folder, entry.ToFullPath());
            var extension = Path.GetExtension(name);
            if (ImageExtensions.Contains(extension))
                return new Entry(EntryKind.Image, entry.ToFullPath()) { CoverRank = CoverRank(Path.GetFileNameWithoutExtension(name)) };
            if (!TagReader.AudioExtensions.Contains(extension))
                return default;

            long size = entry.Length;
            var modified = entry.LastWriteTimeUtc.UtcDateTime;
            int length = entry.Directory.Length + 1 + name.Length;
            char[]? rented = null;
            Span<char> path = length <= 512 ? stackalloc char[512] : (rented = ArrayPool<char>.Shared.Rent(length));
            Track? known = Path.TryJoin(entry.Directory, name, path, out int written) ? previous.FindTrack(path[..written]) : null;
            if (rented is not null)
                ArrayPool<char>.Shared.Return(rented);
            string fullPath = known?.Path ?? entry.ToFullPath();
            return new Entry(EntryKind.Audio, fullPath) { Known = known, Size = size, Modified = modified };
        }

        void Report(int done, int total, bool enumerating)
        {
            if (progress is null)
                return;
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref lastReport);
            if (Stopwatch.GetElapsedTime(last, now).TotalMilliseconds < 100 || Interlocked.CompareExchange(ref lastReport, now, last) != last)
                return;
            progress.Report(new ScanProgress(files.Count, done, total, enumerating));
        }
    }

    private static bool SameImages(Dictionary<string, string> images, IReadOnlyDictionary<string, string> previous)
    {
        if (images.Count != previous.Count)
            return false;
        foreach (var (directory, image) in images)
        {
            if (!previous.TryGetValue(directory, out string? known) || known != image)
                return false;
        }
        return true;
    }

    /// <summary>Resolves a symbolic link to a folder.</summary>
    private static string Resolve(string path)
    {
        try
        {
            return Directory.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    /// <summary>When a file joined the library.</summary>
    /// <remarks>This is its birth time where the file system records one, else its modification time.</remarks>
    private static DateTime AddedTime(string path, DateTime modified)
    {
        try
        {
            var created = File.GetCreationTimeUtc(path);
            var now = DateTime.UtcNow;
            return created > now || created.Year < 1980 ? modified : created;
        }
        catch (IOException)
        {
            return modified;
        }
    }

    /// <summary>Ranks a folder image by its name, lower first.</summary>
    /// <remarks>"cover" ranks before "folder", which ranks before "front", and other names rank last.</remarks>
    private static int CoverRank(ReadOnlySpan<char> name)
    {
        for (int i = 0; i < CoverNames.Length; i++)
        {
            if (name.Equals(CoverNames[i], StringComparison.OrdinalIgnoreCase))
                return i;
        }
        if (name.StartsWith("albumart", StringComparison.OrdinalIgnoreCase) || name.Contains("cover", StringComparison.OrdinalIgnoreCase)
            || name.Contains("front", StringComparison.OrdinalIgnoreCase))
            return CoverNames.Length;
        return int.MaxValue;
    }

    private static readonly EnumerationOptions EnumerationOptions =
        new() { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };

    private enum EntryKind : byte { Skipped, Folder, FolderLink, Audio, Image }

    private readonly record struct Entry(EntryKind Kind, string? Path)
    {
        public Track? Known { get; init; }
        public long Size { get; init; }
        public DateTime Modified { get; init; }
        public int CoverRank { get; init; }
    }

    /// <param name="Known">The previous snapshot's track at this path, if any.</param>
    private readonly record struct FoundFile(string Path, long Size, DateTime Modified, Track? Known, DateTime Added);
}
