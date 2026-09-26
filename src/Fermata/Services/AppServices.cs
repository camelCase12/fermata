using Fermata.Library;
using Fermata.Playback;
using Fermata.Storage;

namespace Fermata.Services;

/// <summary>
/// Creates and connects Fermata's long-lived parts, and saves their state. Everything here is owned by
/// the UI thread; saving captures state there and writes it on a background thread.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly DeferredSave settingsSave;
    private readonly DeferredSave userDataSave;
    private readonly DeferredSave playlistsSave;
    private readonly DeferredSave sessionSave;

    public AppServices(SynchronizationContext context, AppPaths paths)
    {
        Paths = paths;
        var saved = FermataJson.LoadOrSetAside(paths.SettingsFile, FermataJson.Default.Settings);
        IsFirstRun = saved.Value is null;
        Settings = saved.Value ?? new Settings { MusicFolders = [AppPaths.DefaultMusicFolder()] };
        var userData = FermataJson.LoadOrSetAside(paths.UserDataFile, FermataJson.Default.UserDataDocument);
        UserData = UserData.FromDocument(userData.Value);
        Playlists = new PlaylistStore(paths.PlaylistsDirectory);
        Playlists.Load();
        var unreadable = new List<UnreadableFile>();
        if (saved.SetAside is { } settingsFile)
            unreadable.Add(new("settings", settingsFile));
        if (userData.SetAside is { } userDataFile)
            unreadable.Add(new("likes and history", userDataFile));
        unreadable.AddRange(Playlists.Unreadable.Select(file => new UnreadableFile("a playlist", file)));
        Unreadable = unreadable;
        foreach (var file in unreadable)
            Console.Error.WriteLine($"fermata: could not read {file.What}; the file was kept as {file.Path}");
        Library = new MusicLibrary(paths, context);
        Art = new ArtCache();

        // FERMATA_AUDIO_OUTPUT=null plays silently (useful for testing without speakers).
        string? output = Environment.GetEnvironmentVariable("FERMATA_AUDIO_OUTPUT");
        IAudioEngine engine = new MpvEngine(context, audioOutput: string.IsNullOrEmpty(output) ? null : output);
        Player = new Player(engine, new PlayQueue(), UserData)
        {
            Volume = Settings.Volume,
            Muted = Settings.Muted,
        };
        Player.Queue.Repeat = Settings.Repeat;
        Player.Queue.SetShuffle(Settings.Shuffle);
        engine.ReplayGain = Settings.ReplayGain;
        engine.Gapless = Settings.Gapless;
        engine.AudioDevice = Settings.AudioDevice;
        Player.AutoplaySource = queue => new Recommender(Library.Snapshot, UserData).ContinueQueue(queue);
        Player.Autoplay = Settings.Autoplay;

        settingsSave = new DeferredSave(CaptureSettings, TimeSpan.FromSeconds(1), context);
        userDataSave = new DeferredSave(CaptureUserData, TimeSpan.FromSeconds(3), context);
        playlistsSave = new DeferredSave(Playlists.CaptureChanges, TimeSpan.FromSeconds(1), context);
        sessionSave = new DeferredSave(CaptureSession, TimeSpan.FromSeconds(5), context);
        UserData.Changed += userDataSave.Request;
        Playlists.Changed += playlistsSave.Request;
        Player.Queue.Changed += change =>
        {
            SyncModeSettings();
            sessionSave.Request();
        };
        Player.VolumeChanged += () =>
        {
            Settings.Volume = Player.Volume;
            Settings.Muted = Player.Muted;
            settingsSave.Request();
        };
        Library.SnapshotChanged += OnLibraryChanged;
    }

    public AppPaths Paths { get; }
    public Settings Settings { get; }

    /// <summary>
    /// Saved files that existed but could not be read at startup. Each was renamed so that Fermata does not
    /// save over it, and Fermata started without its contents.
    /// </summary>
    public IReadOnlyList<UnreadableFile> Unreadable { get; }

    /// <summary>True when no settings existed: the library folder was guessed and should be confirmed.</summary>
    public bool IsFirstRun { get; }

    public MusicLibrary Library { get; }
    public UserData UserData { get; }
    public PlaylistStore Playlists { get; }
    public Player Player { get; }
    public ArtCache Art { get; }

    /// <summary>Colours the accent after the playing cover; set by the app once its theme is loaded.</summary>
    public DynamicAccent? Accent { get; set; }

    /// <summary>Raised when a setting that changes how cover art colours the app is changed.</summary>
    public event Action? AppearanceChanged;

    public void RaiseAppearanceChanged() => AppearanceChanged?.Invoke();

    /// <summary>Starts the library: the cached index first, then a scan, then watching for changes.</summary>
    public async Task StartLibraryAsync()
    {
        if (await Library.LoadCacheAsync(Settings.MusicFolders))
            RestoreSession();
        else
        {
            // Without a cached index the queue can only be restored once the first scan has found the files.
            void RestoreAfterScan(ScanResult result)
            {
                Library.ScanCompleted -= RestoreAfterScan;
                RestoreSession();
            }
            Library.ScanCompleted += RestoreAfterScan;
        }
        Library.SetFolders(Settings.MusicFolders);
        if (Settings.WatchFolders)
            Library.StartWatching();
    }

    public void SaveSettings() => settingsSave.Request();

    /// <summary>Shuffle and repeat are remembered between sessions.</summary>
    private void SyncModeSettings()
    {
        var queue = Player.Queue;
        if (Settings.Shuffle == queue.Shuffle && Settings.Repeat == queue.Repeat)
            return;
        Settings.Shuffle = queue.Shuffle;
        Settings.Repeat = queue.Repeat;
        settingsSave.Request();
    }

    private void OnLibraryChanged(LibrarySnapshot snapshot)
    {
        var renames = UserData.Relink(snapshot);
        if (renames.Count > 0)
            Playlists.RenamePaths(renames);
    }

    private Action? CaptureSettings()
    {
        var copy = Settings.Clone();
        return () => FermataJson.Save(Paths.SettingsFile, copy, FermataJson.Default.Settings);
    }

    private Action? CaptureUserData()
    {
        var document = UserData.ToDocument();
        return () => FermataJson.Save(Paths.UserDataFile, document, FermataJson.Default.UserDataDocument);
    }

    private Action? CaptureSession()
    {
        var queue = Player.Queue;
        var state = new SessionState
        {
            Queue = queue.QueuedOrder.Select(e => e.Track.Path).ToList(),
            ShuffleOrder = queue.ShuffleOrder(),
            Current = queue.CurrentIndex,
            PositionSeconds = Player.Position.TotalSeconds,
            SourceKind = queue.Source?.Kind,
            SourceTitle = queue.Source?.Title,
            SourceKey = queue.Source?.Key,
        };
        return () => FermataJson.Save(Paths.SessionFile, state, FermataJson.Default.SessionState);
    }

    /// <summary>Puts back the queue of the last session, paused where it was left.</summary>
    private void RestoreSession()
    {
        if (!Settings.ResumeSession || Player.Queue.Count > 0)
            return;
        var state = FermataJson.Load(Paths.SessionFile, FermataJson.Default.SessionState);
        if (state is null || state.Queue.Count == 0)
            return;
        var library = Library.Snapshot;
        var tracks = new List<Track>(state.Queue.Count);
        var kept = new List<int>(state.Queue.Count); // original index of each track still present
        for (int i = 0; i < state.Queue.Count; i++)
        {
            if (library.FindTrack(state.Queue[i]) is { } track)
            {
                tracks.Add(track);
                kept.Add(i);
            }
        }
        if (tracks.Count == 0)
            return;
        List<int>? order = null;
        int current = state.Current;
        if (state.ShuffleOrder is { } saved && saved.Count == state.Queue.Count)
        {
            // Map the saved play order onto the tracks that still exist.
            var remap = new Dictionary<int, int>();
            for (int i = 0; i < kept.Count; i++)
                remap[kept[i]] = i;
            int playing = (uint)current < (uint)saved.Count ? saved[current] : -1;
            order = saved.Where(remap.ContainsKey).Select(i => remap[i]).ToList();
            current = remap.TryGetValue(playing, out int mapped) ? order.IndexOf(mapped) : 0;
        }
        else
        {
            int index = kept.IndexOf(current);
            current = index >= 0 ? index : 0;
        }
        var source = state.SourceKind is { } kind ? new QueueSource(kind, state.SourceTitle ?? "", state.SourceKey) : null;
        Player.Restore(tracks, order, current, TimeSpan.FromSeconds(state.PositionSeconds), source);
    }

    /// <summary>Writes everything that is pending; called once at exit.</summary>
    public void Shutdown()
    {
        foreach (var save in (DeferredSave[])[settingsSave, userDataSave, playlistsSave, sessionSave])
        {
            try
            {
                save.Flush();
            }
            catch (AggregateException)
            {
                // A failed save must not prevent the others or the exit.
            }
        }
    }

    public void Dispose()
    {
        Library.Dispose();
        Player.Dispose();
        foreach (var save in (DeferredSave[])[settingsSave, userDataSave, playlistsSave, sessionSave])
            save.Dispose();
    }
}

/// <summary>A saved file that could not be read. What names its contents, as it reads in a sentence.</summary>
public sealed record UnreadableFile(string What, string Path);
