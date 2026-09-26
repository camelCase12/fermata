using Fermata.Library;
using Fermata.Playback;

namespace Fermata.Tests;

internal static class QueueChecks
{
    public static Track[] MakeTracks(int count, string prefix = "t") =>
        Enumerable.Range(0, count).Select(i => new Track
        {
            Path = $"/music/{prefix}{i:000}.flac",
            Title = $"{prefix}{i}",
            Duration = TimeSpan.FromSeconds(100),
        }).ToArray();

    private static string Titles(IEnumerable<QueueEntry> entries) => string.Join(",", entries.Select(e => e.Track.Title));

    public static void Run(Checks check)
    {
        Basics(check);
        Repeat(check);
        Shuffle(check);
        Editing(check);
        Fuzz(check);
    }

    private static void Basics(Checks check)
    {
        var tracks = MakeTracks(4);
        var queue = new PlayQueue(new Random(1));
        check.Equal(-1, queue.CurrentIndex, "empty queue has no current entry");
        check.That(queue.PeekNext() is null && queue.Next() is null && queue.Previous() is null, "empty queue does nothing");

        queue.Replace(tracks, 1, new QueueSource("album", "A"));
        check.Equal("t1", queue.Current?.Track.Title, "replace starts at the chosen track");
        check.Equal("t2", queue.PeekNext()?.Track.Title, "peek is the following entry");
        check.Equal("t2", queue.Next()?.Track.Title, "next");
        check.Equal("t3", queue.Next()?.Track.Title, "next to the end");
        check.That(queue.PeekNext() is null, "nothing follows the last entry without repeat");
        check.That(queue.Next() is null && queue.CurrentIndex == 3, "next at the end returns null and keeps position");
        check.Equal("t2", queue.Previous()?.Track.Title, "previous");
        queue.JumpTo(queue.Entries[0]);
        check.Equal("t0", queue.Previous()?.Track.Title, "previous at the start stays");

        long version = queue.Version;
        int notifications = 0;
        queue.Changed += _ => notifications++;
        queue.Next();
        check.That(queue.Version > version && notifications == 1, "changes are versioned and announced once");
    }

    private static void Repeat(Checks check)
    {
        var queue = new PlayQueue(new Random(2));
        queue.Replace(MakeTracks(3), 2, null);
        queue.Repeat = RepeatMode.All;
        check.Equal("t0", queue.PeekNext()?.Track.Title, "repeat-all wraps in peek");
        check.Equal("t0", queue.Advance()?.Track.Title, "repeat-all wraps on advance");
        check.Equal("t2", queue.Previous()?.Track.Title, "previous wraps backwards with repeat");

        queue.Repeat = RepeatMode.One;
        var current = queue.Current;
        check.That(queue.PeekNext() == current, "repeat-one peeks the same entry");
        check.That(queue.Advance() == current, "repeat-one repeats on a natural advance");
        check.Equal("t0", queue.Next()?.Track.Title, "Next still advances under repeat-one (and wraps)");
    }

    private static void Shuffle(Checks check)
    {
        var tracks = MakeTracks(20);
        var queue = new PlayQueue(new Random(3));
        queue.Replace(tracks, 5, null);
        queue.SetShuffle(true);
        check.That(queue.Shuffle, "shuffle on");
        check.Equal(5, queue.CurrentIndex, "turning shuffle on keeps the current entry in place");
        check.Equal("t0,t1,t2,t3,t4", Titles(queue.Entries.Take(5)), "history before the current entry stays in order");
        var upcoming = queue.Entries.Skip(6).Select(e => e.Track.Title).ToHashSet();
        check.That(upcoming.SetEquals(tracks.Skip(6).Select(t => t.Title)), "only the tracks after the current one are shuffled");
        check.That(Titles(queue.Entries.Skip(6)) != Titles(queue.QueuedOrder.Skip(6)), "upcoming order actually changed");

        queue.Next();
        queue.Next();
        var playing = queue.Current;
        queue.SetShuffle(false);
        check.That(queue.Current == playing, "turning shuffle off keeps the playing entry");
        check.Equal(queue.QueuedOrder.ToList().IndexOf(playing!), queue.CurrentIndex, "and continues from it in queued order");

        // Shuffle play from nothing starts at random and plays every entry once.
        queue.SetShuffle(true);
        queue.Replace(tracks, -1, null);
        check.Equal(0, queue.CurrentIndex, "shuffle play starts at the first shuffled entry");
        check.Equal(20, queue.Entries.Distinct().Count(), "every entry appears once");

        // Each repeat-all cycle is a full permutation that never starts with the entry that just ended.
        queue.Repeat = RepeatMode.All;
        QueueEntry? previousLast = null;
        bool predictions = true;
        for (int cycle = 0; cycle < 50; cycle++)
        {
            var first = queue.Current!;
            var seen = new HashSet<QueueEntry> { first };
            for (int i = 1; i < 20; i++)
            {
                var predicted = queue.PeekNext();
                predictions &= predicted == queue.Advance();
                seen.Add(queue.Current!);
            }
            check.Equal(20, seen.Count, $"cycle {cycle} plays every entry once");
            if (previousLast is not null)
                check.That(first != previousLast, $"cycle {cycle} does not start with the entry that ended the previous one");
            previousLast = queue.Current;
            var wrap = queue.PeekNext();
            predictions &= wrap == queue.Advance();
        }
        check.That(predictions, "peek predicts every advance across repeat cycles");

        // Starting a shuffled album from a chosen song plays that song first.
        queue.Replace(tracks, 7, null);
        check.Equal("t7", queue.Current?.Track.Title, "shuffled replace starts with the chosen track");
    }

    private static void Editing(Checks check)
    {
        var queue = new PlayQueue(new Random(4));
        queue.Replace(MakeTracks(5), 0, null);
        queue.SetShuffle(true);
        queue.InsertNext(MakeTracks(2, "n"));
        check.Equal("n0,n1", Titles(queue.Entries.Skip(1).Take(2)), "play next goes right after the current entry");
        queue.SetShuffle(false);
        check.Equal("t0,n0,n1", Titles(queue.Entries.Take(3)), "play-next entries stay next after unshuffling");

        queue.Append(MakeTracks(1, "a"));
        check.Equal("a0", queue.Entries[^1].Track.Title, "add to queue appends");
        queue.Append(MakeTracks(2, "auto"), autoplay: true);
        queue.Append(MakeTracks(1, "b"));
        check.Equal("a0,b0,auto0,auto1", Titles(queue.Entries.TakeLast(4)), "add to queue goes before autoplay entries");
        queue.Remove(queue.Entries.TakeLast(3).ToArray());

        queue.JumpTo(queue.Entries[2]); // n1
        bool currentRemoved = queue.Remove([queue.Entries[0], queue.Entries[1]]);
        check.That(!currentRemoved && queue.Current?.Track.Title == "n1" && queue.CurrentIndex == 0, "removing earlier entries keeps the current one");
        currentRemoved = queue.Remove([queue.Current!]);
        check.That(currentRemoved && queue.Current?.Track.Title == "t1", "removing the current entry makes the following one current");

        queue.Move(0, 3);
        check.Equal(3, queue.CurrentIndex, "moving the current entry moves the current index");
        queue.Move(4, 0);
        check.Equal(4, queue.CurrentIndex, "moving an entry from after to before shifts the current index");
        queue.ClearUpcoming();
        check.Equal(queue.Count - 1, queue.CurrentIndex, "clear upcoming leaves the current entry last");

        // Saving and restoring a shuffled queue keeps its exact order.
        queue.Replace(MakeTracks(8), 3, null);
        queue.SetShuffle(true);
        var order = Titles(queue.Entries);
        var restored = new PlayQueue();
        restored.Restore(queue.QueuedOrder.Select(e => e.Track).ToList(), queue.ShuffleOrder(), queue.CurrentIndex, null);
        check.Equal(order, Titles(restored.Entries), "restored shuffle order");
        check.Equal(queue.CurrentIndex, restored.CurrentIndex, "restored current index");
    }

    /// <summary>Random operations must preserve the queue's invariants.</summary>
    private static void Fuzz(Checks check)
    {
        var random = new Random(12345);
        int violations = 0;
        for (int run = 0; run < 300 && violations < 5; run++)
        {
            var queue = new PlayQueue(new Random(run));
            var pool = MakeTracks(12);
            queue.Replace(pool.Take(random.Next(1, 12)).ToArray(), 0, null);
            for (int step = 0; step < 200 && violations < 5; step++)
            {
                string action = Apply(queue, random, pool);
                string? problem = Invariant(queue);
                if (problem is null && queue.Count > 0)
                {
                    // What PeekNext announces (and the engine preloads) must be exactly what a natural advance produces.
                    var predicted = queue.PeekNext();
                    var actual = queue.Advance();
                    if (predicted != actual)
                        problem = $"peek {predicted} but advance {actual}";
                }
                if (problem is not null)
                {
                    violations++;
                    check.That(false, $"run {run} step {step} after {action}: {problem}");
                }
            }
        }
        check.Equal(0, violations, "queue invariants over 60,000 random operations");
    }

    private static string Apply(PlayQueue queue, Random random, Track[] pool)
    {
        int count = queue.Count;
        switch (random.Next(13))
        {
            case 0: queue.Next(); return "next";
            case 1: queue.Previous(); return "previous";
            case 2: queue.SetShuffle(!queue.Shuffle); return "shuffle toggle";
            case 3: queue.Repeat = (RepeatMode)random.Next(3); return "repeat " + queue.Repeat;
            case 4: queue.InsertNext(pool.Take(random.Next(1, 3)).ToArray()); return "insert next";
            case 5: queue.Append(pool.Skip(random.Next(pool.Length)).Take(2).ToArray()); return "append";
            case 6 when count > 0: queue.Remove([queue.Entries[random.Next(count)]]); return "remove";
            case 7 when count > 1: queue.Move(random.Next(count), random.Next(count)); return "move";
            case 8 when count > 0: queue.JumpTo(queue.Entries[random.Next(count)]); return "jump";
            case 9 when count > 0: queue.AdvanceTo(queue.Entries[random.Next(count)]); return "advance-to (race)";
            case 10: queue.Replace(pool.Take(random.Next(1, pool.Length)).ToArray(), random.Next(-1, 4), null); return "replace";
            case 11 when count > 0 && random.Next(10) == 0: queue.ClearUpcoming(); return "clear upcoming";
            default: queue.Advance(); return "advance";
        }
    }

    private static string? Invariant(PlayQueue queue)
    {
        var play = queue.Entries;
        var queued = queue.QueuedOrder;
        if (play.Count != queued.Count)
            return $"play order has {play.Count} entries, queued order {queued.Count}";
        if (play.Distinct().Count() != play.Count)
            return "an entry appears twice";
        if (!play.ToHashSet().SetEquals(queued))
            return "play order and queued order hold different entries";
        if (queue.CurrentIndex < -1 || queue.CurrentIndex >= Math.Max(play.Count, 0) && play.Count > 0 || play.Count == 0 && queue.CurrentIndex != -1)
            return $"current index {queue.CurrentIndex} out of range for {play.Count}";
        if (queue.Shuffle != (queue.ShuffleOrder() is not null))
            return "shuffle flag disagrees with the shuffle order";
        return null;
    }
}
