using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fermata.Controls;
using Fermata.Library;
using Fermata.ViewModels;

namespace Fermata.Views;

/// <summary>A virtualized list of songs inside a scrolling page.</summary>
public partial class TrackList : UserControl
{
    public TrackList()
    {
        InitializeComponent();
        List.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control source && source.FindAncestorOfType<TrackRow>(includeSelf: true) is { } row)
                PlayRow(row);
        };
        List.KeyDown += OnKeyDown;
        RowDragging.Attach(List, SongsAt, () => Model?.CanReorder == true, (from, to) => Model?.Move(from, to));
    }

    public TrackListModel? Model => DataContext as TrackListModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is not { } model)
            return;
        TrackRow.SetColumns(Header.ColumnDefinitions, model.Style);
        ArtistHeader.IsVisible = model.Style == TrackListStyle.Library;
    }

    private int IndexOf(TrackRow row) =>
        row.FindAncestorOfType<ListBoxItem>() is { } container ? List.IndexFromContainer(container) : -1;

    public void PlayRow(TrackRow row) => Model?.PlayAt(IndexOf(row));

    /// <summary>The songs a row's menu acts on.</summary>
    public IReadOnlyList<Track> TracksForMenu(TrackRow row) => SongsAt(IndexOf(row));

    /// <summary>The selected songs when the row is part of a multiple selection; otherwise just the row's song.</summary>
    private IReadOnlyList<Track> SongsAt(int index)
    {
        var model = Model;
        if (model is null || (uint)index >= (uint)model.Tracks.Count)
            return [];
        var selected = SelectedIndices();
        if (selected.Count > 1 && selected.Contains(index))
            return selected.Select(i => model.Tracks[i]).ToList();
        return [model.Tracks[index]];
    }

    public Action? RemoveFromPlaylistAction(TrackRow row)
    {
        var model = Model;
        if (model?.Playlist is null)
            return null;
        int index = IndexOf(row);
        var selected = SelectedIndices();
        IReadOnlyList<int> indices = selected.Count > 1 && selected.Contains(index) ? selected : [index];
        return () => model.RemoveFromPlaylist(indices);
    }

    private List<int> SelectedIndices()
    {
        var model = Model;
        if (model is null || List.Selection is not { } selection)
            return [];
        return selection.SelectedIndexes.Where(i => i >= 0 && i < model.Tracks.Count).Order().ToList();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var model = Model;
        if (model is null)
            return;
        switch (e.Key)
        {
            case Key.Enter when List.SelectedIndex >= 0:
                model.PlayAt(List.SelectedIndex);
                e.Handled = true;
                break;
            case Key.Delete when model.Playlist is not null && List.SelectedIndex >= 0:
                model.RemoveFromPlaylist(SelectedIndices());
                e.Handled = true;
                break;
        }
    }
}
