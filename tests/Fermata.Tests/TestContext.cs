using System.Collections.Concurrent;

namespace Fermata.Tests;

/// <summary>A single-threaded message loop standing in for the UI thread in engine tests.</summary>
internal sealed class TestContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();

    public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));

    public override void Send(SendOrPostCallback callback, object? state) => throw new NotSupportedException();

    /// <summary>Runs posted work on the calling thread until <paramref name="done"/> holds or the timeout passes.</summary>
    public bool RunUntil(Func<bool> done, TimeSpan timeout)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!done())
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    return false;
                if (queue.TryTake(out var work, remaining < TimeSpan.FromMilliseconds(20) ? remaining : TimeSpan.FromMilliseconds(20)))
                    work.Callback(work.State);
            }
            return true;
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
