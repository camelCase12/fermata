using Fermata.Library;

namespace Fermata.Playback;

/// <summary>One occurrence of a track in the queue; the same track may be queued more than once.</summary>
public sealed class QueueEntry
{
    private static long lastId;

    internal QueueEntry(Track track, bool isAutoplay = false)
    {
        Track = track;
        IsAutoplay = isAutoplay;
        Id = Interlocked.Increment(ref lastId);
    }

    public Track Track { get; }

    /// <summary>Unique for the lifetime of the process.</summary>
    public long Id { get; }

    /// <summary>Added by autoplay rather than chosen by the listener.</summary>
    public bool IsAutoplay { get; }

    public override string ToString() => $"#{Id} {Track.Title}";
}

/// <summary>What the queue was started from, shown as "Playing from …".</summary>
/// <param name="Kind">album, artist, playlist, liked, search, radio, songs, folder…</param>
/// <param name="Key">Identifies the source for navigation (album key, playlist id, artist name).</param>
public sealed record QueueSource(string Kind, string Title, string? Key = null);

[Flags]
public enum QueueChange
{
    None = 0,
    /// <summary>Entries were added, removed or reordered.</summary>
    Entries = 1,
    /// <summary>A different entry became current.</summary>
    Current = 2,
    /// <summary>Shuffle or repeat changed.</summary>
    Mode = 4,
    /// <summary>The source was renamed.</summary>
    Source = 8,
}

/// <summary>The play queue, with shuffle and repeat.</summary>
/// <remarks>
/// <para>
/// The queue keeps two orders over the same entries, <c>linear</c> in the order they were queued and,
/// while shuffle is on, <c>shuffled</c> in the order they play. <see cref="Entries"/> is the order that
/// plays.
/// </para>
/// <para>
/// Turning shuffle on keeps everything up to the current entry in place and shuffles the rest. Turning
/// it off continues from the current entry in the original order. With repeat-all, each new cycle is a
/// fresh permutation whose first entry differs from the last one played.
/// </para>
/// </remarks>
public sealed class PlayQueue
{
    private readonly List<QueueEntry> linear = [];
    private List<QueueEntry>? shuffled;
    private List<QueueEntry>? nextCycle;
    private readonly Random random;
    private int current = -1;
    private RepeatMode repeat;

    public PlayQueue(Random? random = null)
    {
        this.random = random ?? Random.Shared;
    }

    /// <summary>Raised after every change, describing what changed.</summary>
    public event Action<QueueChange>? Changed;

    /// <summary>Entries in play order.</summary>
    public IReadOnlyList<QueueEntry> Entries => shuffled ?? linear;

    /// <summary>Entries in the order they were queued (the unshuffled order).</summary>
    public IReadOnlyList<QueueEntry> QueuedOrder => linear;

    public int Count => linear.Count;
    public int CurrentIndex => current;
    public QueueEntry? Current => current >= 0 ? Entries[current] : null;
    public bool Shuffle => shuffled is not null;
    public QueueSource? Source { get; private set; }

    /// <summary>Gives the source a new title when it is the one with this key, such as a renamed playlist.</summary>
    public void RenameSource(string key, string title)
    {
        if (Source is not { } source || source.Key != key || source.Title == title)
            return;
        Source = source with { Title = title };
        Notify(QueueChange.Source);
    }

    /// <summary>Increments on every change.</summary>
    public long Version { get; private set; }

    public RepeatMode Repeat
    {
        get => repeat;
        set
        {
            if (repeat == value)
                return;
            repeat = value;
            Notify(QueueChange.Mode);
        }
    }

    /// <summary>Replaces the queue. With shuffle on, <paramref name="startIndex"/> plays first (or a random entry when it is -1).</summary>
    public void Replace(IReadOnlyList<Track> tracks, int startIndex, QueueSource? source)
    {
        linear.Clear();
        foreach (var track in tracks)
            linear.Add(new QueueEntry(track));
        Source = source;
        nextCycle = null;
        if (linear.Count == 0)
        {
            current = -1;
            if (shuffled is not null)
                shuffled = [];
        }
        else if (shuffled is not null)
        {
            shuffled = [.. linear];
            if (startIndex >= 0 && startIndex < linear.Count)
            {
                (shuffled[0], shuffled[startIndex]) = (shuffled[startIndex], shuffled[0]);
                ShuffleRange(shuffled, 1);
            }
            else
            {
                ShuffleRange(shuffled, 0);
            }
            current = 0;
        }
        else
        {
            current = Math.Clamp(startIndex, 0, linear.Count - 1);
        }
        Notify(QueueChange.Entries | QueueChange.Current);
    }

    /// <summary>Restores a saved queue exactly, including its shuffle order.</summary>
    public void Restore(IReadOnlyList<Track> tracks, IReadOnlyList<int>? shuffleOrder, int currentIndex, QueueSource? source)
    {
        linear.Clear();
        foreach (var track in tracks)
            linear.Add(new QueueEntry(track));
        Source = source;
        nextCycle = null;
        bool validOrder = shuffleOrder is not null && shuffleOrder.Count == linear.Count
            && shuffleOrder.All(i => (uint)i < (uint)linear.Count) && shuffleOrder.Distinct().Count() == linear.Count;
        shuffled = shuffleOrder is null ? null : validOrder ? shuffleOrder.Select(i => linear[i]).ToList() : [.. linear];
        current = linear.Count == 0 ? -1 : Math.Clamp(currentIndex, 0, linear.Count - 1);
        Notify(QueueChange.Entries | QueueChange.Current | QueueChange.Mode);
    }

    public void SetShuffle(bool enabled)
    {
        if (enabled == Shuffle)
            return;
        nextCycle = null;
        if (enabled)
        {
            // Entries up to the current one are history and stay in place; only what is left is shuffled.
            shuffled = [.. linear];
            ShuffleRange(shuffled, current + 1);
        }
        else
        {
            var playing = Current;
            shuffled = null;
            current = playing is null ? -1 : linear.IndexOf(playing);
        }
        Notify(QueueChange.Mode | QueueChange.Entries | QueueChange.Current);
    }

    /// <summary>The entry that plays when the current one ends on its own, or null at the end of the queue.</summary>
    public QueueEntry? PeekNext()
    {
        var entries = Entries;
        if (entries.Count == 0)
            return null;
        if (current < 0)
            return entries[0];
        if (repeat == RepeatMode.One)
            return entries[current];
        if (current + 1 < entries.Count)
            return entries[current + 1];
        if (repeat == RepeatMode.All)
            return shuffled is not null ? (nextCycle ??= DrawCycle())[0] : entries[0];
        return null;
    }

    /// <summary>Moves to the entry that follows when the current one ends on its own; null at the end of the queue.</summary>
    public QueueEntry? Advance()
    {
        if (repeat == RepeatMode.One && Current is { } again)
        {
            Notify(QueueChange.Current); // the same entry starts again
            return again;
        }
        return Next();
    }

    /// <summary>Skips forward (repeat-one does not apply). Returns null at the end of a non-repeating queue.</summary>
    public QueueEntry? Next()
    {
        var entries = Entries;
        if (entries.Count == 0)
            return null;
        if (current + 1 < entries.Count)
        {
            current++;
        }
        else if (repeat != RepeatMode.Off)
        {
            if (shuffled is not null)
            {
                shuffled = nextCycle ?? DrawCycle();
                nextCycle = null;
                current = 0;
                Notify(QueueChange.Entries | QueueChange.Current);
                return shuffled[0];
            }
            current = 0;
        }
        else
        {
            return null;
        }
        Notify(QueueChange.Current);
        return Entries[current];
    }

    /// <summary>Steps back one entry; wraps around with repeat, otherwise stays on the first entry.</summary>
    public QueueEntry? Previous()
    {
        var entries = Entries;
        if (entries.Count == 0)
            return null;
        if (current > 0)
            current--;
        else if (repeat != RepeatMode.Off && entries.Count > 1)
            current = entries.Count - 1;
        else
            current = 0;
        Notify(QueueChange.Current);
        return entries[current];
    }

    /// <summary>Makes <paramref name="entry"/> current. Returns false if it is not in the queue.</summary>
    public bool JumpTo(QueueEntry entry)
    {
        int index = IndexOf(entry);
        if (index < 0)
            return false;
        current = index;
        Notify(QueueChange.Current);
        return true;
    }

    /// <summary>Records that <paramref name="entry"/> started playing by itself after the current one.</summary>
    /// <remarks>
    /// Normally it is the next entry. If the queue changed while the player was switching, the entry is
    /// moved up to follow the current one.
    /// </remarks>
    /// <returns>False if the entry is no longer queued.</returns>
    public bool AdvanceTo(QueueEntry entry)
    {
        if (entry == Current)
        {
            Notify(QueueChange.Current); // repeat-one, or the same entry restarting
            return true;
        }
        var entries = Entries;
        int index = IndexOf(entry);
        if (index < 0)
            return false;
        if (index == current + 1)
        {
            current = index;
            Notify(QueueChange.Current);
            return true;
        }
        if (current == entries.Count - 1 && repeat != RepeatMode.Off && (nextCycle?[0] == entry || (shuffled is null && index == 0)))
        {
            Next();
            return true;
        }
        // The engine is ahead of a queue edit, so the entry moves to right after the current one.
        var list = (shuffled ?? linear);
        list.RemoveAt(index);
        if (index < current)
            current--;
        list.Insert(current + 1, entry);
        current++;
        nextCycle = null;
        Notify(QueueChange.Entries | QueueChange.Current);
        return true;
    }

    /// <summary>Queues tracks to play right after the current entry ("Play next").</summary>
    public void InsertNext(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
            return;
        var entries = tracks.Select(t => new QueueEntry(t)).ToList();
        var playing = Current;
        if (shuffled is not null)
        {
            shuffled.InsertRange(current + 1, entries);
            // They also follow the current entry in the unshuffled order.
            int linearIndex = playing is null ? 0 : linear.IndexOf(playing) + 1;
            linear.InsertRange(linearIndex, entries);
        }
        else
        {
            linear.InsertRange(current + 1, entries);
        }
        nextCycle = null;
        Notify(QueueChange.Entries);
    }

    /// <summary>Adds tracks to the end of the queue ("Add to queue").</summary>
    /// <remarks>Tracks the listener adds go before any upcoming entries added by autoplay.</remarks>
    public void Append(IReadOnlyList<Track> tracks, bool autoplay = false)
    {
        if (tracks.Count == 0)
            return;
        var entries = tracks.Select(t => new QueueEntry(t, autoplay)).ToList();
        if (autoplay)
        {
            linear.AddRange(entries);
            shuffled?.AddRange(entries);
        }
        else
        {
            var playing = Current;
            int linearFrom = playing is null ? 0 : linear.IndexOf(playing) + 1;
            linear.InsertRange(FirstAutoplay(linear, linearFrom), entries);
            shuffled?.InsertRange(FirstAutoplay(shuffled, current + 1), entries);
        }
        nextCycle = null;
        Notify(QueueChange.Entries);
    }

    /// <summary>The index of the first autoplay entry at or after <paramref name="from"/>, or the end of the list.</summary>
    private static int FirstAutoplay(List<QueueEntry> list, int from)
    {
        for (int i = Math.Max(from, 0); i < list.Count; i++)
        {
            if (list[i].IsAutoplay)
                return i;
        }
        return list.Count;
    }

    /// <summary>Removes entries.</summary>
    /// <remarks>If the current entry is removed, the following entry becomes current, or the previous one at the end.</remarks>
    /// <returns>Whether the current entry was removed.</returns>
    public bool Remove(IReadOnlyCollection<QueueEntry> entries)
    {
        if (entries.Count == 0)
            return false;
        var remove = entries as ISet<QueueEntry> ?? new HashSet<QueueEntry>(entries);
        var playing = Current;
        var order = Entries;
        int removedBefore = 0;
        for (int i = 0; i < current; i++)
        {
            if (remove.Contains(order[i]))
                removedBefore++;
        }
        bool currentRemoved = playing is not null && remove.Contains(playing);
        int before = linear.Count;
        linear.RemoveAll(remove.Contains);
        shuffled?.RemoveAll(remove.Contains);
        if (linear.Count == before)
            return false;
        nextCycle = null;
        if (linear.Count == 0)
            current = -1;
        else if (current >= 0)
            current = Math.Min(current - removedBefore, linear.Count - 1);
        Notify(QueueChange.Entries | (currentRemoved ? QueueChange.Current : QueueChange.None));
        return currentRemoved;
    }

    /// <summary>Moves an entry within the play order.</summary>
    public void Move(int from, int to)
    {
        var list = shuffled ?? linear;
        if ((uint)from >= (uint)list.Count || from == to)
            return;
        to = Math.Clamp(to, 0, list.Count - 1);
        var entry = list[from];
        list.RemoveAt(from);
        list.Insert(to, entry);
        if (from == current)
            current = to;
        else if (from < current && to >= current)
            current--;
        else if (from > current && to <= current)
            current++;
        nextCycle = null;
        Notify(QueueChange.Entries);
    }

    /// <summary>Removes everything after the current entry.</summary>
    public void ClearUpcoming()
    {
        var entries = Entries;
        if (current + 1 >= entries.Count)
            return;
        var upcoming = entries.Skip(current + 1).ToHashSet();
        Remove(upcoming);
    }

    public void Clear()
    {
        linear.Clear();
        if (shuffled is not null)
            shuffled = [];
        nextCycle = null;
        current = -1;
        Source = null;
        Notify(QueueChange.Entries | QueueChange.Current);
    }

    /// <summary>Position of an entry in play order, or -1.</summary>
    public int IndexOf(QueueEntry entry)
    {
        var entries = Entries;
        // The current entry and its neighbours are checked first.
        if (current >= 0)
        {
            for (int i = Math.Max(0, current - 1); i < Math.Min(entries.Count, current + 3); i++)
            {
                if (entries[i] == entry)
                    return i;
            }
        }
        return (shuffled ?? linear).IndexOf(entry);
    }

    /// <summary>The play order as indices into <see cref="QueuedOrder"/> (for saving a shuffled queue).</summary>
    public List<int>? ShuffleOrder()
    {
        if (shuffled is null)
            return null;
        var positions = new Dictionary<QueueEntry, int>(linear.Count);
        for (int i = 0; i < linear.Count; i++)
            positions[linear[i]] = i;
        return shuffled.Select(e => positions[e]).ToList();
    }

    /// <summary>A fresh permutation for the next repeat cycle that does not start with the entry that just played.</summary>
    private List<QueueEntry> DrawCycle()
    {
        var cycle = new List<QueueEntry>(linear);
        ShuffleRange(cycle, 0);
        var last = Entries.Count > 0 ? Entries[^1] : null;
        if (cycle.Count > 1 && cycle[0] == last)
        {
            int swap = random.Next(1, cycle.Count);
            (cycle[0], cycle[swap]) = (cycle[swap], cycle[0]);
        }
        return cycle;
    }

    /// <summary>Fisher–Yates shuffle of <paramref name="list"/> from <paramref name="start"/> to the end.</summary>
    private void ShuffleRange(List<QueueEntry> list, int start)
    {
        for (int i = list.Count - 1; i > start; i--)
        {
            int j = random.Next(start, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private void Notify(QueueChange change)
    {
        Version++;
        Changed?.Invoke(change);
    }
}
