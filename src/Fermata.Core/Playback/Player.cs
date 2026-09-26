using System.Diagnostics;
using Fermata.Library;

namespace Fermata.Playback;

/// <summary>Plays the <see cref="PlayQueue"/> through an <see cref="IAudioEngine"/>.</summary>
/// <remarks>
/// <para>
/// The queue decides what plays, and the player keeps the engine in step with it. The player remembers
/// the engine ids of the playing item and of the item preloaded for gapless playback, and replaces the
/// preloaded item whenever the queue's next entry changes.
/// </para>
/// <para>
/// The engine runs independently and can start the preloaded item before a queue edit reaches it.
/// Events for items that are no longer relevant are ignored, and a preloaded item that starts after the
/// queue changed is reconciled with the queue through <see cref="PlayQueue.AdvanceTo"/>. Everything runs
/// on one thread.
/// </para>
/// </remarks>
public sealed class Player : IDisposable
{
    /// <summary>Pressing Previous later than this into a track restarts it instead.</summary>
    public static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);

    private readonly IAudioEngine engine;
    private readonly UserData userData;
    private readonly Stopwatch listening = new();
    private EngineItem? playing;
    private EngineItem? preloaded;
    // Preloads replaced after a queue edit, which the engine may already have started.
    private readonly List<EngineItem> superseded = [];
    private const int SupersededLimit = 4;
    private int suppressSync;
    private TimeSpan resumePosition;
    private int consecutiveFailures;
    // The playing item finished with an item preloaded, which the engine should start next. If the engine
    // goes idle instead, the player advances explicitly.
    private bool awaitingPreloadStart;
    private bool autoplay;
    private double volume = 0.8;
    private bool muted;

    private sealed record EngineItem(long Id, QueueEntry Entry);

    public Player(IAudioEngine engine, PlayQueue queue, UserData userData)
    {
        this.engine = engine;
        this.userData = userData;
        Queue = queue;
        engine.EventRaised += OnEngineEvent;
        queue.Changed += OnQueueChanged;
        engine.Volume = volume;
    }

    public PlayQueue Queue { get; }
    public PlaybackState State { get; private set; }
    public QueueEntry? CurrentEntry => Queue.Current;
    public Track? CurrentTrack => Queue.Current?.Track;

    /// <summary>The length of the current track, as measured by the engine once loaded, else from its tags.</summary>
    public TimeSpan Duration { get; private set; }

    public TimeSpan Position => playing is null ? resumePosition : engine.Position;

    public bool IsEngineAvailable => engine.IsAvailable;
    public string? EngineUnavailableReason => engine.UnavailableReason;
    public IAudioEngine Engine => engine;

    /// <summary>Supplies tracks to continue with when the queue runs out; see <see cref="Autoplay"/>.</summary>
    public Func<PlayQueue, IReadOnlyList<Track>>? AutoplaySource { get; set; }

    /// <summary>Whether a folder exists.</summary>
    public Func<string, bool> FolderExists { get; set; } = Directory.Exists;

    /// <summary>Raised when playing, paused or stopped changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised when a different entry becomes current (including when a track repeats).</summary>
    public event Action? TrackChanged;

    /// <summary>Raised when the duration becomes known or changes.</summary>
    public event Action? DurationChanged;

    /// <summary>Raised after the position jumps (seek, track change) so observers can resynchronize.</summary>
    public event Action? Seeked;

    /// <summary>Raised when a file cannot be played, with a message for the listener.</summary>
    public event Action<string>? PlaybackFailed;

    public double Volume
    {
        get => volume;
        set
        {
            volume = Math.Clamp(value, 0, 1);
            engine.Volume = volume;
            VolumeChanged?.Invoke();
        }
    }

    public bool Muted
    {
        get => muted;
        set
        {
            muted = value;
            engine.Muted = value;
            VolumeChanged?.Invoke();
        }
    }

    public event Action? VolumeChanged;

    /// <summary>Continue with similar tracks from the library when the queue ends (unless repeating).</summary>
    public bool Autoplay
    {
        get => autoplay;
        set
        {
            autoplay = value;
            ExtendWithAutoplayIfNeeded();
        }
    }

    /// <summary>Replaces the queue and starts playing. With <paramref name="shuffle"/> set, shuffle is switched first.</summary>
    public void Play(IReadOnlyList<Track> tracks, int startIndex, QueueSource source, bool? shuffle = null)
    {
        if (tracks.Count == 0)
            return;
        FinishListening(completed: false);
        suppressSync++;
        try
        {
            if (shuffle is { } enabled)
                Queue.SetShuffle(enabled);
            Queue.Replace(tracks, startIndex, source);
        }
        finally
        {
            suppressSync--;
        }
        LoadCurrent(TimeSpan.Zero, play: true);
    }

    public void PlayPause()
    {
        if (State == PlaybackState.Playing)
            Pause();
        else
            Resume();
    }

    public void Resume()
    {
        if (Queue.Current is null)
        {
            if (Queue.Count == 0)
                return;
            Queue.JumpTo(Queue.Entries[0]);
        }
        if (playing is null || playing.Entry != Queue.Current)
        {
            LoadCurrent(resumePosition, play: true);
            return;
        }
        engine.Paused = false;
        SetState(PlaybackState.Playing);
    }

    public void Pause()
    {
        if (State != PlaybackState.Playing)
            return;
        engine.Paused = true;
        SetState(PlaybackState.Paused);
    }

    public void Stop()
    {
        FinishListening(completed: false);
        engine.Stop();
        ForgetEngineItems();
        resumePosition = TimeSpan.Zero;
        SetState(PlaybackState.Stopped);
        Seeked?.Invoke();
    }

    public void Next()
    {
        FinishListening(completed: false, skipped: true);
        QueueEntry? next;
        suppressSync++;
        try
        {
            next = Queue.Next();
        }
        finally
        {
            suppressSync--;
        }
        if (next is null)
            EndOfQueue();
        else
            LoadCurrent(TimeSpan.Zero, play: true);
    }

    /// <summary>Restarts the track if it has played for a few seconds, otherwise goes to the previous entry.</summary>
    public void Previous()
    {
        if (Position > RestartThreshold || Queue.CurrentIndex <= 0 && Queue.Repeat == RepeatMode.Off)
        {
            Seek(TimeSpan.Zero);
            if (State != PlaybackState.Playing)
                Resume();
            return;
        }
        FinishListening(completed: false);
        suppressSync++;
        try
        {
            Queue.Previous();
        }
        finally
        {
            suppressSync--;
        }
        LoadCurrent(TimeSpan.Zero, play: true);
    }

    public void JumpTo(QueueEntry entry)
    {
        FinishListening(completed: false);
        bool found;
        suppressSync++;
        try
        {
            found = Queue.JumpTo(entry);
        }
        finally
        {
            suppressSync--;
        }
        if (found)
            LoadCurrent(TimeSpan.Zero, play: true);
    }

    public void Seek(TimeSpan position)
    {
        if (Duration > TimeSpan.Zero)
            position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
        if (playing is null)
            resumePosition = position;
        else
            engine.Seek(position);
        Seeked?.Invoke();
    }

    public void PlayNext(IReadOnlyList<Track> tracks) => Queue.InsertNext(tracks);

    public void AddToQueue(IReadOnlyList<Track> tracks) => Queue.Append(tracks);

    /// <summary>Removes entries; if the playing entry is among them, playback moves on to what is now current.</summary>
    public void RemoveFromQueue(IReadOnlyCollection<QueueEntry> entries)
    {
        var before = Queue.Current;
        bool currentRemoved = Queue.Remove(entries);
        if (!currentRemoved)
            return;
        FinishListening(completed: false, entry: before);
        if (Queue.Current is null)
            Stop();
        else if (State == PlaybackState.Playing)
            LoadCurrent(TimeSpan.Zero, play: true);
        else
        {
            engine.Stop();
            ForgetEngineItems();
            resumePosition = TimeSpan.Zero;
            UpdateDuration(Queue.Current.Track.Duration);
            TrackChanged?.Invoke();
        }
    }

    public void SetShuffle(bool enabled) => Queue.SetShuffle(enabled);

    public void CycleRepeat() => Queue.Repeat = Queue.Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    /// <summary>Restores a saved session, paused at its position; nothing is loaded until playback resumes.</summary>
    public void Restore(IReadOnlyList<Track> tracks, IReadOnlyList<int>? shuffleOrder, int current, TimeSpan position, QueueSource? source)
    {
        Queue.Restore(tracks, shuffleOrder, current, source);
        ForgetEngineItems();
        resumePosition = position;
        UpdateDuration(Queue.Current?.Track.Duration ?? TimeSpan.Zero);
        SetState(Queue.Current is null ? PlaybackState.Stopped : PlaybackState.Paused);
        TrackChanged?.Invoke();
    }

    private void LoadCurrent(TimeSpan start, bool play)
    {
        var entry = Queue.Current;
        if (entry is null)
        {
            Stop();
            return;
        }
        ForgetEngineItems(); // replacing playback discards the engine's playlist, preloads included
        resumePosition = start;
        long id = engine.Play(entry.Track.Path, start, paused: !play);
        playing = id >= 0 ? new EngineItem(id, entry) : null;
        UpdateDuration(entry.Track.Duration);
        SetState(play ? PlaybackState.Playing : PlaybackState.Paused);
        StartListening();
        TrackChanged?.Invoke();
        Seeked?.Invoke();
        ExtendWithAutoplayIfNeeded();
        SyncPreload();
    }

    /// <summary>Makes sure the engine's preloaded item is the entry the queue will play next.</summary>
    private void SyncPreload()
    {
        if (playing is null || suppressSync > 0)
            return;
        var next = Queue.PeekNext();
        if (preloaded?.Entry == next)
            return;
        if (preloaded is not null)
        {
            if (superseded.Count == SupersededLimit)
                superseded.RemoveAt(0);
            superseded.Add(preloaded);
        }
        if (next is null)
        {
            engine.ClearPreload();
            preloaded = null;
            return;
        }
        long id = engine.Preload(next.Track.Path);
        preloaded = id >= 0 ? new EngineItem(id, next) : null;
    }

    private void OnQueueChanged(QueueChange change)
    {
        if (suppressSync > 0)
            return;
        if (change.HasFlag(QueueChange.Mode))
            ExtendWithAutoplayIfNeeded();
        // Any edit, shuffle or repeat change can change what comes next.
        SyncPreload();
    }

    private void OnEngineEvent(EngineEvent e)
    {
        switch (e)
        {
            case ItemStarted started:
                OnItemStarted(started.Id);
                break;
            case ItemLoaded loaded when loaded.Id == playing?.Id && loaded.Duration > TimeSpan.Zero:
                UpdateDuration(loaded.Duration);
                break;
            case ItemEnded ended when ended.Id == playing?.Id:
                OnItemEnded(ended);
                break;
            // PauseChanged is not handled. It only echoes the player's own requests, and the player's state
            // is authoritative.
            case PlaybackRestarted:
                consecutiveFailures = 0;
                break;
            case EngineIdle when awaitingPreloadStart:
                awaitingPreloadStart = false;
                FinishListening(completed: true);
                AdvanceExplicitly(Queue.Advance());
                break;
        }
    }

    private void OnItemStarted(long id)
    {
        if (id == playing?.Id)
            return; // the item we asked for
        EngineItem? next = preloaded?.Id == id ? preloaded : superseded.Find(item => item.Id == id);
        if (next is null)
            return; // a replaced item reporting late

        // The engine moved on to a preloaded item by itself. A superseded item is adopted by the queue
        // through AdvanceTo, and the preload is resynchronized. Engine ids increase, so items handed over
        // before this one can never start, while later ones may still follow it.
        superseded.RemoveAll(item => item.Id <= id);
        awaitingPreloadStart = false;
        FinishListening(completed: true);
        playing = next;
        if (preloaded?.Id == id)
            preloaded = null;
        resumePosition = TimeSpan.Zero;
        bool adopted;
        suppressSync++;
        try
        {
            adopted = Queue.AdvanceTo(next.Entry);
        }
        finally
        {
            suppressSync--;
        }
        if (!adopted)
        {
            // The entry was removed after it was preloaded, so playback continues with whatever follows now.
            AdvanceExplicitly(Queue.Next());
            return;
        }
        UpdateDuration(next.Entry.Track.Duration);
        StartListening();
        TrackChanged?.Invoke();
        Seeked?.Invoke();
        ExtendWithAutoplayIfNeeded();
        SyncPreload();
    }

    private void OnItemEnded(ItemEnded ended)
    {
        if (ended.Reason == EndReason.Stopped)
            return;
        if (ended.Reason == EndReason.Failed)
        {
            // The broken file is skipped with Next, so that repeat-one cannot loop on it.
            consecutiveFailures++;
            var track = playing!.Entry.Track;
            FinishListening(completed: false);
            // Without its folder, the songs after it are most likely unavailable too.
            if (Path.GetDirectoryName(track.Path) is { } folder && !FolderExists(folder))
            {
                PlaybackFailed?.Invoke($"Couldn't play “{track.Title}”: its folder is not available");
                Stop();
                return;
            }
            PlaybackFailed?.Invoke($"Couldn't play “{track.Title}”: {ended.Error}");
            if (consecutiveFailures >= Math.Min(Math.Max(Queue.Count, 1), 8))
            {
                Stop();
                return;
            }
            AdvanceExplicitly(Queue.Next());
            return;
        }
        consecutiveFailures = 0;
        if (preloaded is not null)
        {
            // The engine continues with the preloaded item and reports it with ItemStarted.
            awaitingPreloadStart = true;
            return;
        }
        FinishListening(completed: true);
        AdvanceExplicitly(Queue.Advance());
    }

    /// <summary>Forgets every item handed to the engine.</summary>
    /// <remarks>Call it whenever the engine's playlist is discarded. Late events about those items are then ignored.</remarks>
    private void ForgetEngineItems()
    {
        playing = preloaded = null;
        superseded.Clear();
        awaitingPreloadStart = false;
    }

    /// <summary>Plays the entry the queue moved to, or ends playback when there is none.</summary>
    private void AdvanceExplicitly(QueueEntry? next)
    {
        if (next is null)
            EndOfQueue();
        else
            LoadCurrent(TimeSpan.Zero, play: true);
    }

    /// <summary>Stops at the end of the queue and rewinds to its first entry.</summary>
    private void EndOfQueue()
    {
        engine.Stop();
        ForgetEngineItems();
        resumePosition = TimeSpan.Zero;
        if (Queue.Count > 0)
            Queue.JumpTo(Queue.Entries[0]);
        UpdateDuration(Queue.Current?.Track.Duration ?? TimeSpan.Zero);
        SetState(Queue.Count > 0 ? PlaybackState.Paused : PlaybackState.Stopped);
        TrackChanged?.Invoke();
        Seeked?.Invoke();
    }

    /// <summary>Appends similar tracks when autoplay is on and the last entry is playing.</summary>
    private void ExtendWithAutoplayIfNeeded()
    {
        if (!autoplay || AutoplaySource is null || Queue.Repeat != RepeatMode.Off || Queue.Current is null
            || Queue.CurrentIndex < Queue.Count - 1)
            return;
        var more = AutoplaySource(Queue);
        if (more.Count > 0)
            Queue.Append(more, autoplay: true);
    }

    private void UpdateDuration(TimeSpan duration)
    {
        if (Duration == duration)
            return;
        Duration = duration;
        DurationChanged?.Invoke();
    }

    private void SetState(PlaybackState state)
    {
        if (State == state)
            return;
        State = state;
        if (state == PlaybackState.Playing)
            listening.Start();
        else
            listening.Stop();
        StateChanged?.Invoke();
    }

    private QueueEntry? listened;

    private void StartListening()
    {
        listened = Queue.Current;
        listening.Reset();
        if (State == PlaybackState.Playing)
            listening.Start();
    }

    /// <summary>Records the end of listening to the current track as a play or a skip.</summary>
    /// <remarks>
    /// A track heard for half its length or four minutes, whichever is shorter, counts as a play. Leaving
    /// earlier counts as a skip.
    /// </remarks>
    private void FinishListening(bool completed, bool skipped = false, QueueEntry? entry = null)
    {
        entry ??= listened;
        if (entry is null)
            return;
        var track = entry.Track;
        var heard = listening.Elapsed;
        var length = track.Duration > TimeSpan.Zero ? track.Duration : Duration;
        var threshold = TimeSpan.FromTicks(Math.Min(length.Ticks / 2, TimeSpan.FromMinutes(4).Ticks));
        if (completed || (heard >= threshold && heard >= TimeSpan.FromSeconds(10)))
            userData.RecordPlay(track, DateTime.UtcNow);
        else if (skipped && heard > TimeSpan.FromSeconds(1))
            userData.RecordSkip(track);
        listened = null;
        listening.Reset();
    }

    public void Dispose()
    {
        engine.EventRaised -= OnEngineEvent;
        Queue.Changed -= OnQueueChanged;
        engine.Dispose();
    }
}
