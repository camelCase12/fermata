using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Fermata.Playback;

namespace Fermata.Views;

/// <summary>
/// An entry in "Up next". The playing entry is highlighted, entries already played are dimmed, and the
/// first entry added by autoplay has a divider above it.
/// </summary>
public partial class QueueRow : UserControl
{
    private QueueEntry? entry;
    private bool subscribed;

    public QueueRow()
    {
        InitializeComponent();
        RemoveButton.Click += (_, _) =>
        {
            if (entry is not null)
                App.Services?.Player.RemoveFromQueue([entry]);
        };
        ContextRequested += (_, e) =>
        {
            if (entry is null || App.Shell is not { } shell || App.Services is not { } services)
                return;
            var entries = this.FindAncestorOfType<NowPlayingView>()?.SelectedEntries(entry) ?? [entry];
            var tracks = entries.Select(x => x.Track).ToList();
            var extra = new List<Control>
            {
                TrackMenu.Item("Play from here", "Icon.Play", () => services.Player.JumpTo(entry)),
                TrackMenu.Item(entries.Count == 1 ? "Remove from queue" : $"Remove {entries.Count} from queue", "Icon.Close",
                    () => services.Player.RemoveFromQueue(entries)),
            };
            TrackMenu.Build(shell, tracks, extra: extra).Open(this);
            e.Handled = true;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        entry = DataContext as QueueEntry;
        if (entry is not null)
            Art.Source = App.Shell?.Library.ArtOf(entry.Track);
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (App.Services is { } services && !subscribed)
        {
            services.Player.Queue.Changed += OnQueueChanged;
            subscribed = true;
        }
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (App.Services is { } services && subscribed)
        {
            services.Player.Queue.Changed -= OnQueueChanged;
            subscribed = false;
        }
    }

    private void OnQueueChanged(QueueChange change) => Update();

    private void Update()
    {
        var queue = App.Services?.Player.Queue;
        bool current = entry is not null && queue?.Current == entry;
        Classes.Set("current", current);
        // Entries before the current one have played (or were skipped). The row's position in the list
        // is its position in the queue, which avoids searching the queue for every visible row.
        int index = this.FindAncestorOfType<ListBoxItem>() is { } container && container.FindAncestorOfType<ListBox>() is { } list
            ? list.IndexFromContainer(container)
            : -1;
        Classes.Set("played", !current && queue is not null && index >= 0 && index < queue.CurrentIndex);
        Classes.Set("first-autoplay", entry is { IsAutoplay: true } && queue is not null && index >= 0 && index < queue.Count
            && (index == 0 || !queue.Entries[index - 1].IsAutoplay));
    }
}
