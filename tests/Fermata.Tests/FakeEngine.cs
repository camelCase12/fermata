using Fermata.Playback;

namespace Fermata.Tests;

/// <summary>
/// Mimics mpv as Fermata drives it: a playlist with a current position, ids per item, and events that
/// arrive later than the state change that caused them. Tests move the engine forward
/// (<see cref="FinishCurrent"/>) and deliver events (<see cref="Deliver"/>) separately, so listener
/// actions can land in between, as they do in the real application.
/// </summary>
/// <remarks>
/// Like mpv: played items stay in the playlist until cleared; "playlist-clear" keeps only the playing
/// item; appending while idle does not start playback; and after an item ends the engine moves to the
/// following item by itself or goes idle.
/// </remarks>
internal sealed class FakeEngine : IAudioEngine
{
    private readonly List<(long Id, string Path)> playlist = [];
    private readonly Queue<EngineEvent> pending = new();
    private int current = -1;
    private long nextId = 1;

    public event Action<EngineEvent>? EventRaised;

    public HashSet<string> Broken { get; } = [];
    public bool IsAvailable => true;
    public string? UnavailableReason => null;
    public IReadOnlyList<AudioDevice> AudioDevices => [];
    public TimeSpan Position { get; set; }
    public bool Paused { get; set; }
    public double Volume { get; set; }
    public bool Muted { get; set; }
    public ReplayGainMode ReplayGain { get; set; }
    public bool Gapless { get; set; }
    public string AudioDevice { get; set; } = "auto";

    /// <summary>The path the engine is really playing (or holding paused), or null when idle.</summary>
    public string? PlayingPath => current >= 0 ? playlist[current].Path : null;

    /// <summary>What the engine will play after the current item, or null.</summary>
    public string? PreloadedPath => current >= 0 && current + 1 < playlist.Count ? playlist[current + 1].Path : null;

    public long Play(string path, TimeSpan start, bool paused)
    {
        if (current >= 0)
            pending.Enqueue(new ItemEnded(playlist[current].Id, EndReason.Stopped, null));
        playlist.Clear();
        long id = nextId++;
        playlist.Add((id, path));
        current = 0;
        Paused = paused;
        Position = start;
        StartCurrent();
        return id;
    }

    public long Preload(string path)
    {
        ClearPreload();
        long id = nextId++;
        playlist.Add((id, path));
        return id;
    }

    public void ClearPreload()
    {
        if (current < 0)
        {
            playlist.Clear();
            return;
        }
        var playing = playlist[current];
        playlist.Clear();
        playlist.Add(playing);
        current = 0;
    }

    public void Stop()
    {
        if (current >= 0)
            pending.Enqueue(new ItemEnded(playlist[current].Id, EndReason.Stopped, null));
        playlist.Clear();
        GoIdle();
    }

    public void Seek(TimeSpan position) => Position = position;

    /// <summary>The playing item reaches its end (a paused engine never does).</summary>
    public void FinishCurrent()
    {
        if (current < 0 || Paused)
            return;
        pending.Enqueue(new ItemEnded(playlist[current].Id, EndReason.Finished, null));
        Position = TimeSpan.Zero;
        current++;
        StartCurrent();
    }

    private void StartCurrent()
    {
        while (current >= 0 && current < playlist.Count)
        {
            var (id, path) = playlist[current];
            pending.Enqueue(new ItemStarted(id));
            if (!Broken.Contains(path))
            {
                pending.Enqueue(new ItemLoaded(id, TimeSpan.FromSeconds(100)));
                return;
            }
            // A broken file fails to open and the engine moves on to the next item.
            pending.Enqueue(new ItemEnded(id, EndReason.Failed, "unrecognized file format"));
            current++;
        }
        GoIdle();
    }

    private void GoIdle()
    {
        if (current == -1 && pending.Count > 0 && pending.Last() is EngineIdle)
            return;
        current = -1;
        pending.Enqueue(new EngineIdle());
    }

    /// <summary>Delivers queued events; returns how many were delivered.</summary>
    public int Deliver(int limit = int.MaxValue)
    {
        int count = 0;
        while (count < limit && pending.TryDequeue(out var e))
        {
            EventRaised?.Invoke(e);
            count++;
        }
        return count;
    }

    public int PendingEvents => pending.Count;

    public void Dispose()
    {
    }
}
