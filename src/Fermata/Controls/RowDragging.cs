using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fermata.Library;
using AdornerLayer = Avalonia.Controls.Primitives.AdornerLayer;

namespace Fermata.Controls;

/// <summary>Songs being dragged, with the list and row they come from.</summary>
public sealed record DraggedSongs(IReadOnlyList<Track> Tracks, ListBox Source, int Index);

/// <summary>Drag and drop for the rows of a list.</summary>
/// <remarks>
/// Rows can be dropped on other targets and, when the list allows it, moved within the list. Alt+↑ and
/// Alt+↓ move the selected row.
/// </remarks>
public sealed class RowDragging
{
    public static readonly DataFormat<DraggedSongs> Format = DataFormat.CreateInProcessFormat<DraggedSongs>("fermata-songs");

    private const double Threshold = 6;
    private const double EdgeZone = 48;
    private const double MaxScrollStep = 16;

    private readonly ListBox list;
    private readonly Func<int, IReadOnlyList<Track>> songsAt;
    private readonly Func<bool> canReorder;
    private readonly Action<int, int> move;
    private readonly Border marker = new() { Classes = { "drop-marker" }, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly DispatcherTimer edgeScroll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private PointerPressedEventArgs? press;
    private Point pressPoint;
    private int pressedIndex;
    private IReadOnlyList<Track> pressedSongs = [];
    private ScrollViewer? scroller;
    private Point pointerInScroller;

    private RowDragging(ListBox list, Func<int, IReadOnlyList<Track>> songsAt, Func<bool> canReorder, Action<int, int> move)
    {
        this.list = list;
        this.songsAt = songsAt;
        this.canReorder = canReorder;
        this.move = move;
        list.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => press = null, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnelling, because the list itself takes arrow keys to move the selection.
        list.AddHandler(InputElement.KeyDownEvent, OnMoveKey, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(list, true);
        // Entering a new part of a row may be the last event before a drop, so it updates the marker too.
        list.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        list.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        list.AddHandler(DragDrop.DropEvent, OnDrop);
        list.DetachedFromVisualTree += (_, _) => EndHover();
        edgeScroll.Tick += (_, _) => ScrollAtEdge();
    }

    /// <param name="list">The list whose rows can be dragged.</param>
    /// <param name="songsAt">The songs to drag from a row: its song, or the selection it is part of.</param>
    /// <param name="canReorder">Whether rows may be moved within the list at the moment.</param>
    /// <param name="move">Moves a row from one index to another (the index it has afterwards).</param>
    public static void Attach(ListBox list, Func<int, IReadOnlyList<Track>> songsAt, Func<bool> canReorder, Action<int, int> move) =>
        _ = new RowDragging(list, songsAt, canReorder, move);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        press = null;
        var point = e.GetCurrentPoint(list);
        // Buttons in a row (like, more, artist and album links) keep their clicks.
        if (!point.Properties.IsLeftButtonPressed || e.Source is not Visual source
            || source.FindAncestorOfType<Button>(includeSelf: true) is not null
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } container)
        {
            return;
        }
        int index = list.IndexFromContainer(container);
        if (index < 0)
            return;
        press = e;
        pressPoint = point.Position;
        pressedIndex = index;
        // Taken before the press changes the selection, so that a multiple selection can be dragged.
        pressedSongs = songsAt(index);
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (press is not { } started)
            return;
        var position = e.GetPosition(list);
        if (Math.Abs(position.X - pressPoint.X) < Threshold && Math.Abs(position.Y - pressPoint.Y) < Threshold)
            return;
        press = null;
        if (pressedSongs.Count == 0)
            return;
        var container = list.ContainerFromIndex(pressedIndex);
        container?.Classes.Add("dragging");
        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(Format, new DraggedSongs(pressedSongs, list, pressedIndex)));
            await DragDrop.DoDragDropAsync(started, transfer, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            container?.Classes.Remove("dragging");
            EndHover();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(Format))
            return; // files from other applications are the window's to take
        e.Handled = true;
        if (!IsOwnReorder(e))
        {
            e.DragEffects = DragDropEffects.None;
            EndHover();
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        scroller ??= list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() ?? list.FindAncestorOfType<ScrollViewer>();
        if (scroller is not null)
        {
            pointerInScroller = e.GetPosition(scroller);
            if (EdgeStep() != 0)
                edgeScroll.Start();
        }
        ShowMarker(e.GetPosition(list));
    }

    /// <summary>Ends the hover when the pointer leaves the list.</summary>
    /// <remarks>Moving from one row to the next also raises this event.</remarks>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (!new Rect(list.Bounds.Size).Contains(e.GetPosition(list)))
            EndHover();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(Format))
            return;
        e.Handled = true;
        EndHover();
        if (!IsOwnReorder(e) || e.DataTransfer.TryGetValue(Format) is not { } songs)
            return;
        int insertion = InsertionIndex(e.GetPosition(list), out _);
        if (insertion < 0)
            return;
        int to = insertion > songs.Index ? insertion - 1 : insertion;
        if (to == songs.Index)
            return;
        e.DragEffects = DragDropEffects.Move;
        MoveRow(songs.Index, to, NavigationMethod.Pointer);
    }

    private void OnMoveKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Alt || e.Key is not (Key.Up or Key.Down) || !canReorder())
            return;
        e.Handled = true;
        int from = list.SelectedIndex, to = from + (e.Key == Key.Up ? -1 : 1);
        if (from >= 0 && (uint)to < (uint)list.ItemCount)
            MoveRow(from, to, NavigationMethod.Directional);
    }

    private void MoveRow(int from, int to, NavigationMethod focus)
    {
        move(from, to);
        // The move rebuilds the list; select and focus the row where it landed once it is laid out.
        Dispatcher.UIThread.Post(() =>
        {
            list.SelectedIndex = to;
            list.ContainerFromIndex(to)?.Focus(focus);
        }, DispatcherPriority.Background);
    }

    /// <summary>The row under a point of the list.</summary>
    public static ListBoxItem? ContainerAt(ListBox list, Point point)
    {
        foreach (var container in list.GetRealizedContainers())
        {
            if (container is ListBoxItem item && container.TranslatePoint(default, list) is { } origin
                && new Rect(origin, container.Bounds.Size).Contains(point))
            {
                return item;
            }
        }
        return null;
    }

    private bool IsOwnReorder(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(Format) is { } songs && songs.Source == list && canReorder();

    /// <summary>Where a row dropped at <paramref name="point"/> lands.</summary>
    /// <returns>The index the row takes, or -1 when the point is not over the list.</returns>
    private int InsertionIndex(Point point, out double lineY)
    {
        lineY = 0;
        int lastIndex = -1;
        double lastBottom = double.NegativeInfinity;
        foreach (var container in list.GetRealizedContainers())
        {
            int index = list.IndexFromContainer(container);
            if (index < 0 || container.TranslatePoint(default, list) is not { } origin)
                continue;
            double top = origin.Y, bottom = top + container.Bounds.Height;
            if (point.Y >= top && point.Y < bottom)
            {
                bool after = point.Y >= (top + bottom) / 2;
                lineY = after ? bottom : top;
                return after ? index + 1 : index;
            }
            if (index > lastIndex)
                (lastIndex, lastBottom) = (index, bottom);
        }
        if (lastIndex >= 0 && lastIndex == list.ItemCount - 1 && point.Y >= lastBottom)
        {
            lineY = lastBottom;
            return lastIndex + 1;
        }
        return -1;
    }

    private void ShowMarker(Point point)
    {
        if (InsertionIndex(point, out double y) < 0)
        {
            marker.IsVisible = false;
            return;
        }
        if (marker.Parent is null && AdornerLayer.GetAdornerLayer(list) is { } layer)
        {
            AdornerLayer.SetAdornedElement(marker, list);
            layer.Children.Add(marker);
        }
        marker.Margin = new Thickness(0, y - 1, 0, 0);
        marker.IsVisible = true;
    }

    private void EndHover()
    {
        edgeScroll.Stop();
        marker.IsVisible = false;
        if (marker.Parent is AdornerLayer layer)
            layer.Children.Remove(marker);
    }

    /// <summary>The pixels to scroll this tick, more the closer the pointer is to the edge.</summary>
    private double EdgeStep()
    {
        if (scroller is null)
            return 0;
        double y = pointerInScroller.Y, height = scroller.Bounds.Height;
        if (y < EdgeZone)
            return -MaxScrollStep * (EdgeZone - Math.Max(y, 0)) / EdgeZone;
        if (y > height - EdgeZone)
            return MaxScrollStep * (Math.Min(y, height) - (height - EdgeZone)) / EdgeZone;
        return 0;
    }

    private void ScrollAtEdge()
    {
        double step = EdgeStep();
        if (step == 0 || scroller is null)
        {
            edgeScroll.Stop();
            return;
        }
        double limit = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y + step, 0, limit));
        // The rows moved under the pointer; the marker follows them.
        if (scroller.TranslatePoint(pointerInScroller, list) is { } point)
            ShowMarker(point);
    }
}
