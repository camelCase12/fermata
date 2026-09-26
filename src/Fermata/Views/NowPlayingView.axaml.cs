using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fermata.Controls;
using Fermata.Library;
using Fermata.Playback;
using Fermata.ViewModels;

namespace Fermata.Views;

public partial class NowPlayingView : UserControl
{
    private NowPlayingViewModel? model;

    public NowPlayingView()
    {
        InitializeComponent();
        // Tapped treats a quick second click as a double tap, so the release is handled instead.
        Cover.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && new Rect(Cover.Bounds.Size).Contains(e.GetPosition(Cover)))
                model?.Player.PlayPauseCommand.Execute(null);
        };
        QueueList.DoubleTapped += (_, e) =>
        {
            if (FindData<QueueEntry>(e.Source) is { } entry)
                model?.JumpTo(entry);
        };
        // Tunnelling runs before the list's own key handling, which takes Enter.
        QueueList.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (model is null)
                return;
            if (e.Key == Key.Enter && QueueList.SelectedItem is QueueEntry entry)
            {
                model.JumpTo(entry);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && QueueList.SelectedItems?.Count > 0)
            {
                int index = QueueList.SelectedIndex;
                model.Remove(QueueList.SelectedItems.OfType<QueueEntry>().ToList());
                e.Handled = true;
                // Focus stays in the queue, on the row that took the removed one's place.
                Dispatcher.UIThread.Post(() =>
                {
                    int count = QueueList.ItemCount;
                    if (count == 0)
                        return;
                    QueueList.SelectedIndex = Math.Min(index, count - 1);
                    QueueList.ContainerFromIndex(QueueList.SelectedIndex)?.Focus(NavigationMethod.Directional);
                }, DispatcherPriority.Loaded);
            }
        }, RoutingStrategies.Tunnel);
        RowDragging.Attach(QueueList,
            index => model is { } m && (uint)index < (uint)m.Entries.Count ? [m.Entries[index].Track] : [],
            () => model is not null,
            (from, to) => model?.Move(from, to));
        LyricsList.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && FindData<LyricLineViewModel>(e.Source) is { } line)
                model?.SeekToLine(line);
        };
        RelatedList.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && FindData<Track>(e.Source) is { } track && model is not null && App.Shell is { } shell)
            {
                var tracks = model.Related;
                shell.Play(tracks, tracks.ToList().IndexOf(track), new QueueSource("radio", $"Similar to {model.Player.Title}"));
            }
        };
        RelatedList.ContextRequested += (_, e) =>
        {
            if (FindData<Track>(e.Source) is { } track && App.Shell is { } shell && e.Source is Control source)
            {
                TrackMenu.Build(shell, [track]).Open(source);
                e.Handled = true;
            }
        };
    }

    private static T? FindData<T>(object? source) where T : class
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: T data })
                return data;
        }
        return null;
    }

    /// <summary>The selection in the queue, for row menus acting on several entries.</summary>
    public IReadOnlyList<QueueEntry> SelectedEntries(QueueEntry clicked)
    {
        var selected = QueueList.SelectedItems?.OfType<QueueEntry>().ToList() ?? [];
        return selected.Count > 1 && selected.Contains(clicked) ? selected : [clicked];
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (model is not null)
            model.PropertyChanged -= OnModelChanged;
        model = DataContext as NowPlayingViewModel;
        if (model is not null)
            model.PropertyChanged += OnModelChanged;
    }

    /// <summary>Runs after layout, when containers exist for new items.</summary>
    private static void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action, Avalonia.Threading.DispatcherPriority.Loaded);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingViewModel.ActiveLine):
                Post(CenterActiveLine);
                break;
            case nameof(NowPlayingViewModel.Entries) or nameof(NowPlayingViewModel.CurrentIndex):
                Post(ShowCurrentEntry);
                break;
            case nameof(NowPlayingViewModel.LyricLines):
                LyricsScroller.Offset = default;
                break;
        }
    }

    /// <summary>Keeps the line being sung in the middle of the lyrics.</summary>
    private void CenterActiveLine()
    {
        if (model?.ActiveLine is not { } line || LyricsList.ContainerFromItem(line) is not Control container)
            return;
        var top = container.TranslatePoint(new Point(0, 0), LyricsList);
        if (top is null)
            return;
        double target = top.Value.Y + LyricsList.Margin.Top - LyricsScroller.Viewport.Height * 0.38;
        double max = Math.Max(0, LyricsScroller.Extent.Height - LyricsScroller.Viewport.Height);
        LyricsScroller.Offset = new Vector(0, Math.Clamp(target, 0, max));
    }

    private void ShowCurrentEntry()
    {
        if (model is null || model.CurrentIndex < 0 || model.CurrentIndex >= model.Entries.Count)
            return;
        QueueList.ScrollIntoView(model.CurrentIndex);
    }
}
