using Fermata.Storage;

namespace Fermata.Library;

/// <summary>The application's music library.</summary>
/// <remarks>
/// It loads the cached index at startup, scans the music folders in the background, watches them for
/// changes, and publishes each new <see cref="LibrarySnapshot"/> on the owner's thread.
/// </remarks>
public sealed class MusicLibrary : IDisposable
{
    private readonly AppPaths paths;
    private readonly SynchronizationContext context;
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Timer rescanTimer;
    private IReadOnlyList<string> folders = [];
    private CancellationTokenSource? scanCancellation;
    private Task? scanTask;
    private bool rescanRequested;
    private Task<SearchIndex>? searchIndex;
    private bool watching;
    private int releaseScheduled;

    public MusicLibrary(AppPaths paths, SynchronizationContext context)
    {
        this.paths = paths;
        this.context = context;
        rescanTimer = new Timer(_ => this.context.Post(_ => Scan(), null));
    }

    public LibrarySnapshot Snapshot { get; private set; } = LibrarySnapshot.Empty;
    public IReadOnlyList<string> Folders => folders;
    public bool IsScanning => scanTask is { IsCompleted: false };

    /// <summary>Raised on the owner's thread when a new snapshot is published.</summary>
    public event Action<LibrarySnapshot>? SnapshotChanged;

    /// <summary>Raised on the owner's thread while scanning (throttled) and once when a scan ends.</summary>
    public event Action<ScanProgress>? ScanProgressed;

    /// <summary>Raised on the owner's thread after each scan.</summary>
    public event Action<ScanResult>? ScanCompleted;

    /// <summary>Raised on the owner's thread when a scan fails.</summary>
    public event Action<Exception>? ScanFailed;

    /// <summary>Loads the cached index off the calling thread and publishes it.</summary>
    /// <returns>False when there is no usable cache.</returns>
    public async Task<bool> LoadCacheAsync(IReadOnlyList<string> musicFolders)
    {
        folders = Normalize(musicFolders);
        var cached = await Task.Run(() => LibraryCache.Load(paths.LibraryCacheFile, folders)).ConfigureAwait(true);
        if (cached is null)
            return false;
        Publish(cached);
        return true;
    }

    /// <summary>Changes the music folders and rescans.</summary>
    public void SetFolders(IReadOnlyList<string> musicFolders)
    {
        folders = Normalize(musicFolders);
        if (watching)
            StartWatching();
        Scan();
    }

    /// <summary>Scans the music folders in the background. A request during a scan queues one more scan.</summary>
    public void Scan()
    {
        if (IsScanning)
        {
            rescanRequested = true;
            return;
        }
        rescanRequested = false;
        var previous = Snapshot;
        var roots = folders;
        scanCancellation = new CancellationTokenSource();
        var token = scanCancellation.Token;
        var progress = new ContextProgress(context, p => ScanProgressed?.Invoke(p));
        var task = Task.Run(() =>
        {
            var result = LibraryScanner.Scan(roots, previous, progress, token);
            if (result.Changed || !File.Exists(paths.LibraryCacheFile))
                LibraryCache.Save(paths.LibraryCacheFile, result.Snapshot);
            return result;
        }, token);
        scanTask = task;
        task.ContinueWith(t =>
        {
            if (t.IsCanceled)
                return;
            if (t.Exception is { } error)
                ScanFailed?.Invoke(error.GetBaseException());
            else
            {
                var result = t.Result;
                // The folders may have changed while scanning; a queued rescan picks that up.
                if (result.Changed || Snapshot.Tracks.Count != result.Snapshot.Tracks.Count)
                    Publish(result.Snapshot);
                ScanCompleted?.Invoke(result);
            }
            if (rescanRequested)
                Scan();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Watches the music folders and rescans (after changes settle) when files are added, changed or removed.</summary>
    public void StartWatching()
    {
        StopWatching();
        watching = true;
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnFileSystemChange;
                watcher.Deleted += OnFileSystemChange;
                watcher.Changed += OnFileSystemChange;
                watcher.Renamed += OnFileSystemChange;
                watcher.Error += (_, _) => RequestRescan();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Folders beyond the inotify limit, or unreadable ones, are not watched. Rescans still
                // happen on request.
            }
        }
    }

    public void StopWatching()
    {
        watching = false;
        foreach (var watcher in watchers)
            watcher.Dispose();
        watchers.Clear();
    }

    private void OnFileSystemChange(object sender, FileSystemEventArgs e)
    {
        // Ignore editors' and our own temporary files; everything else may affect the library.
        string name = Path.GetFileName(e.FullPath);
        if (name.StartsWith('.') || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return;
        RequestRescan();
    }

    /// <summary>Schedules a rescan after a quiet period.</summary>
    private void RequestRescan() => rescanTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

    /// <summary>The search index for the current snapshot, built once per snapshot off the UI thread.</summary>
    public Task<SearchIndex> GetSearchIndexAsync()
    {
        var snapshot = Snapshot;
        if (searchIndex is { } existing && (!existing.IsCompleted || existing.Result.Library == snapshot))
            return existing;
        return searchIndex = Task.Run(() => new SearchIndex(snapshot));
    }

    private static List<string> Normalize(IReadOnlyList<string> musicFolders) =>
        musicFolders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).Distinct().ToList();

    private void Publish(LibrarySnapshot snapshot)
    {
        Snapshot = snapshot;
        searchIndex = null;
        SnapshotChanged?.Invoke(snapshot);
        if (snapshot.Tracks.Count >= 5000)
            ReleaseMemorySoon();
    }

    /// <summary>Compacts the heap shortly after a large index is built or loaded.</summary>
    private void ReleaseMemorySoon()
    {
        if (Interlocked.Exchange(ref releaseScheduled, 1) == 1)
            return;
        Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
        {
            Volatile.Write(ref releaseScheduled, 0);
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        StopWatching();
        rescanTimer.Dispose();
        scanCancellation?.Cancel();
    }

    /// <summary>Reports progress on the owner's thread, dropping reports that arrive while one is pending.</summary>
    private sealed class ContextProgress(SynchronizationContext context, Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        private int pending;

        public void Report(ScanProgress value)
        {
            if (Interlocked.Exchange(ref pending, 1) == 1 && value.FilesRead < value.FilesToRead)
                return;
            context.Post(_ =>
            {
                Volatile.Write(ref pending, 0);
                report(value);
            }, null);
        }
    }
}
