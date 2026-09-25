namespace Fermata.Storage;

/// <summary>
/// Coalesces saves: after <see cref="Request"/>, waits for changes to settle, captures state on the
/// owning thread, then writes on a background thread. Writes never overlap and happen in request order.
/// </summary>
public sealed class DeferredSave : IDisposable
{
    private readonly Func<Action?> capture;
    private readonly TimeSpan delay;
    private readonly SynchronizationContext context;
    private readonly Timer timer;
    private readonly object gate = new();
    private Task writing = Task.CompletedTask;
    private bool disposed;

    /// <param name="capture">Runs on <paramref name="context"/>; returns the write to perform, or null when there is nothing to save.</param>
    public DeferredSave(Func<Action?> capture, TimeSpan delay, SynchronizationContext context)
    {
        this.capture = capture;
        this.delay = delay;
        this.context = context;
        timer = new Timer(_ => this.context.Post(_ => CaptureAndWrite(), null));
    }

    /// <summary>Raised on a background thread if a write fails.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Schedules a save; repeated requests within the delay are merged.</summary>
    public void Request()
    {
        if (!disposed)
            timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Captures and writes immediately, waiting for the write to finish (for shutdown).</summary>
    public void Flush()
    {
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        CaptureAndWrite();
        Task pending;
        lock (gate)
            pending = writing;
        pending.Wait();
    }

    private void CaptureAndWrite()
    {
        if (disposed)
            return;
        var write = capture();
        if (write is null)
            return;
        lock (gate)
        {
            writing = writing.ContinueWith(_ =>
            {
                try
                {
                    write();
                }
                catch (Exception error)
                {
                    Failed?.Invoke(error);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
    }
}
