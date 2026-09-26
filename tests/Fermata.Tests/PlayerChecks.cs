using Fermata.Library;
using Fermata.Playback;

namespace Fermata.Tests;

internal static class PlayerChecks
{
    public static void Run(Checks check)
    {
        GaplessAdvance(check);
        ShuffleRaces(check);
        RemovalRace(check);
        RepeatOne(check);
        EndOfQueue(check);
        Failures(check);
        Statistics(check);
        Autoplay(check);
        Fuzz(check);
    }

    private static (Player Player, FakeEngine Engine, UserData Data) Create(int seed = 1)
    {
        var engine = new FakeEngine();
        var data = new UserData();
        var player = new Player(engine, new PlayQueue(new Random(seed)), data);
        return (player, engine, data);
    }

    private static void GaplessAdvance(Checks check)
    {
        var (player, engine, _) = Create();
        var tracks = QueueChecks.MakeTracks(4);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        check.Equal(tracks[0].Path, engine.PlayingPath, "plays the chosen track");
        check.Equal(tracks[1].Path, engine.PreloadedPath, "preloads the next track");
        check.Equal(PlaybackState.Playing, player.State, "state is playing");

        int trackChanges = 0;
        player.TrackChanged += () => trackChanges++;
        engine.FinishCurrent();
        engine.Deliver();
        check.Equal("t1", player.CurrentTrack?.Title, "the queue follows the engine to the next track");
        check.Equal(tracks[2].Path, engine.PreloadedPath, "and the one after is preloaded");
        check.Equal(1, trackChanges, "one track change is announced");
        check.Equal(TimeSpan.FromSeconds(100), player.Duration, "duration measured by the engine");
    }

    private static void ShuffleRaces(Checks check)
    {
        // Toggling shuffle mid-track changes the preload immediately.
        var (player, engine, _) = Create(7);
        var tracks = QueueChecks.MakeTracks(30);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        player.SetShuffle(true);
        check.Equal(player.Queue.PeekNext()?.Track.Path, engine.PreloadedPath, "shuffle replaces the preload with the shuffled next");
        check.That(engine.PreloadedPath != tracks[1].Path || player.Queue.PeekNext()?.Track == tracks[1], "the unshuffled next is no longer queued in the engine");

        // The engine finishes the track and starts the old preload before it hears about the shuffle toggle.
        (player, engine, _) = Create(8);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        engine.FinishCurrent();         // the engine starts t1 (the unshuffled preload)…
        player.SetShuffle(true);        // …while the listener turns shuffle on, before the events arrive
        engine.Deliver();
        check.Equal(engine.PlayingPath, player.CurrentTrack?.Path, "after the race, the shown track is the one playing");
        check.Equal(player.Queue.PeekNext()?.Track.Path, engine.PreloadedPath, "after the race, the preload is the queue's next");
        var upcoming = player.Queue.Entries.Skip(player.Queue.CurrentIndex + 1).Select(e => e.Track.Path).ToList();
        check.Equal(28, upcoming.Count, "no upcoming track was lost");
        check.Equal(28, upcoming.Distinct().Count(), "no upcoming track was duplicated");
        for (int i = 0; i < 28; i++)
        {
            engine.FinishCurrent();
            engine.Deliver();
            if (engine.PlayingPath != player.CurrentTrack?.Path)
            {
                check.That(false, $"track {i} after the race: engine plays {engine.PlayingPath}, player shows {player.CurrentTrack?.Path}");
                break;
            }
        }
        check.Equal(30, player.Queue.Entries.Select(e => e.Track.Path).Distinct().Count(), "every track stayed in the queue");
    }

    private static void RemovalRace(Checks check)
    {
        var (player, engine, _) = Create();
        var tracks = QueueChecks.MakeTracks(5);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        engine.FinishCurrent();                                // engine starts t1…
        player.RemoveFromQueue([player.Queue.Entries[1]]);     // …as the listener removes t1
        engine.Deliver();
        check.That(player.Queue.Entries.All(e => e.Track != tracks[1]), "the removed track is gone from the queue");
        check.Equal(tracks[2].Path, engine.PlayingPath, "the engine is moved on to the track that follows");
        check.Equal(engine.PlayingPath, player.CurrentTrack?.Path, "and the player shows it");
    }

    private static void RepeatOne(Checks check)
    {
        var (player, engine, data) = Create();
        var tracks = QueueChecks.MakeTracks(3);
        player.Play(tracks, 1, new QueueSource("album", "A"));
        player.Queue.Repeat = RepeatMode.One;
        engine.Deliver();
        check.Equal(tracks[1].Path, engine.PreloadedPath, "repeat-one preloads the same track for a gapless loop");
        engine.FinishCurrent();
        engine.Deliver();
        check.Equal("t1", player.CurrentTrack?.Title, "the track repeats");
        check.Equal(1, data.PlayCount(tracks[1].Path), "a completed loop counts as a play");
        player.Next();
        engine.Deliver();
        check.Equal("t2", player.CurrentTrack?.Title, "Next leaves the loop");
    }

    private static void EndOfQueue(Checks check)
    {
        var (player, engine, _) = Create();
        var tracks = QueueChecks.MakeTracks(2);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        engine.FinishCurrent();
        engine.Deliver();
        check.That(engine.PreloadedPath is null, "nothing is preloaded after the last track");
        engine.FinishCurrent();
        engine.Deliver();
        check.Equal(PlaybackState.Paused, player.State, "playback stops at the end of the queue");
        check.Equal("t0", player.CurrentTrack?.Title, "and rewinds to the first entry");
        check.That(engine.PlayingPath is null, "the engine is idle");
        player.Resume();
        engine.Deliver();
        check.Equal(tracks[0].Path, engine.PlayingPath, "play starts the queue again");
    }

    private static void Failures(Checks check)
    {
        var (player, engine, _) = Create();
        var tracks = QueueChecks.MakeTracks(4);
        engine.Broken.Add(tracks[1].Path);
        var messages = new List<string>();
        player.PlaybackFailed += messages.Add;
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        engine.FinishCurrent();
        engine.Deliver();
        check.Equal(1, messages.Count, "an unplayable file is reported");
        check.Equal(tracks[2].Path, engine.PlayingPath, "and skipped");
        check.Equal(engine.PlayingPath, player.CurrentTrack?.Path, "the player follows");

        (player, engine, _) = Create();
        foreach (var track in tracks)
            engine.Broken.Add(track.Path);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        for (int i = 0; i < 10 && engine.PendingEvents > 0; i++)
            engine.Deliver();
        check.That(player.State != PlaybackState.Playing, "a queue of unplayable files stops instead of looping");
    }

    private static void Statistics(Checks check)
    {
        var (player, engine, data) = Create();
        var tracks = QueueChecks.MakeTracks(3);
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        engine.FinishCurrent();
        engine.Deliver();
        check.Equal(1, data.PlayCount(tracks[0].Path), "a finished track counts as played");
        check.Equal(1, data.History.Count, "and enters the history");
        Thread.Sleep(1100);
        player.Next();
        check.Equal(0, data.PlayCount(tracks[1].Path), "a skipped track is not a play");
        check.Equal(1, data.StatsFor(tracks[1].Path)?.Skips, "but counts as a skip");
    }

    private static void Autoplay(Checks check)
    {
        var (player, engine, _) = Create();
        var tracks = QueueChecks.MakeTracks(2);
        var extra = QueueChecks.MakeTracks(3, "auto");
        player.AutoplaySource = _ => extra;
        player.Autoplay = true;
        player.Play(tracks, 0, new QueueSource("album", "A"));
        engine.Deliver();
        check.Equal(2, player.Queue.Count, "autoplay waits until the last track");
        engine.FinishCurrent();
        engine.Deliver();
        check.Equal(5, player.Queue.Count, "on the last track, similar tracks are appended");
        check.That(player.Queue.Entries[2].IsAutoplay, "and marked as autoplay");
        check.Equal(extra[0].Path, engine.PreloadedPath, "so playback continues without a gap");
    }

    /// <summary>
    /// Random listener actions interleaved with engine progress and delayed events. After all events
    /// arrive, the shown track must be the playing file and the preload must be the queue's next entry.
    /// </summary>
    private static void Fuzz(Checks check)
    {
        // FERMATA_FUZZ_SEED and FERMATA_FUZZ_RUNS widen the search; FERMATA_TRACE_RUN prints one run step by step.
        var random = new Random(int.TryParse(Environment.GetEnvironmentVariable("FERMATA_FUZZ_SEED"), out int seed) ? seed : 99);
        int runs = int.TryParse(Environment.GetEnvironmentVariable("FERMATA_FUZZ_RUNS"), out int r) ? r : 400;
        int failures = 0;
        int traceRun = int.TryParse(Environment.GetEnvironmentVariable("FERMATA_TRACE_RUN"), out int t) ? t : -1;
        for (int run = 0; run < runs && failures < 5; run++)
        {
            var (player, engine, _) = Create(run);
            var pool = QueueChecks.MakeTracks(10);
            player.Play(pool.Take(random.Next(2, 10)).ToArray(), 0, new QueueSource("x", "x"));
            for (int step = 0; step < 150 && failures < 5; step++)
            {
                string action = Step(player, engine, random, pool);
                if (run == traceRun)
                    Console.WriteLine($"  {step,3} {action,-24} engine={Path.GetFileName(engine.PlayingPath ?? "-")} paused={engine.Paused} next={Path.GetFileName(engine.PreloadedPath ?? "-")} pending={engine.PendingEvents} | player={player.CurrentTrack?.Title} {player.State} idx={player.Queue.CurrentIndex}/{player.Queue.Count} repeat={player.Queue.Repeat}");
                if (random.Next(3) == 0)
                    continue; // leave events pending: the next action races with them
                while (engine.Deliver() > 0)
                {
                }
                string? problem = null;
                if (engine.PlayingPath is { } path && player.CurrentTrack?.Path != path)
                    problem = $"engine plays {Path.GetFileName(path)} but the player shows {player.CurrentTrack?.Title}";
                else if (player.State == PlaybackState.Playing && engine.PlayingPath is null)
                    problem = "the player shows playing but the engine is idle";
                else if (engine.PlayingPath is not null && engine.PreloadedPath != player.Queue.PeekNext()?.Track.Path)
                    problem = $"engine preloads {engine.PreloadedPath} but the queue's next is {player.Queue.PeekNext()?.Track.Path}";
                else if (engine.Paused != (player.State != PlaybackState.Playing) && engine.PlayingPath is not null)
                    problem = $"engine paused={engine.Paused} but the player is {player.State}";
                if (problem is not null)
                {
                    failures++;
                    check.That(false, $"run {run} step {step} after {action}: {problem}");
                }
            }
        }
        check.Equal(0, failures, $"shown track and preload stay consistent through {runs * 150:N0} interleaved steps");
    }

    private static string Step(Player player, FakeEngine engine, Random random, Track[] pool)
    {
        var queue = player.Queue;
        switch (random.Next(14))
        {
            case 0:
            case 1:
            case 2:
                engine.FinishCurrent();
                return "engine finishes a track";
            case 3: player.Next(); return "next";
            case 4: player.Previous(); return "previous";
            case 5: player.SetShuffle(!queue.Shuffle); return "shuffle toggle";
            case 6: player.CycleRepeat(); return "repeat cycle";
            case 7: player.PlayNext([pool[random.Next(pool.Length)]]); return "play next";
            case 8: player.AddToQueue([pool[random.Next(pool.Length)]]); return "add to queue";
            case 9 when queue.Count > 1: player.RemoveFromQueue([queue.Entries[random.Next(queue.Count)]]); return "remove";
            case 10 when queue.Count > 1: queue.Move(random.Next(queue.Count), random.Next(queue.Count)); return "move";
            case 11 when queue.Count > 0: player.JumpTo(queue.Entries[random.Next(queue.Count)]); return "jump";
            case 12: player.PlayPause(); return "play/pause";
            default: player.Play(pool.Skip(random.Next(5)).Take(random.Next(1, 6)).ToArray(), random.Next(-1, 2), new QueueSource("x", "x")); return "play new";
        }
    }
}
