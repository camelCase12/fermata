using CommunityToolkit.Mvvm.ComponentModel;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.ViewModels;

/// <summary>How a track list is laid out.</summary>
public enum TrackListStyle
{
    /// <summary>Cover, title, artist, album, duration (songs, search, playlists).</summary>
    Library,

    /// <summary>Track numbers instead of covers, no artist or album column (an album's own page).</summary>
    Album,

    /// <summary>Cover, title and album, no artist column (an artist's own page).</summary>
    Artist,
}

/// <summary>A list of tracks and the source they are played from.</summary>
public sealed partial class TrackListModel(Shell shell, QueueSource source, TrackListStyle style = TrackListStyle.Library) : ObservableObject
{
    public Shell Shell { get; } = shell;
    public TrackListStyle Style { get; } = style;

    public QueueSource Source { get; set; } = source;

    [ObservableProperty]
    public partial IReadOnlyList<Track> Tracks { get; set; } = [];

    /// <summary>The playlist shown, when the list is one.</summary>
    public Playlist? Playlist { get; init; }

    /// <summary>The position in the playlist of each listed track.</summary>
    /// <remarks>Files that are no longer in the library are not listed, so the positions can skip.</remarks>
    public IReadOnlyList<int>? PlaylistIndices { get; set; }

    /// <summary>Whether the tracks can be reordered by dragging.</summary>
    [ObservableProperty]
    public partial bool CanReorder { get; set; }

    /// <summary>Whether column titles are shown.</summary>
    public bool ShowHeader => Style != TrackListStyle.Album;

    public void PlayAt(int index)
    {
        if ((uint)index < (uint)Tracks.Count)
            Shell.Play(Tracks, index, Source);
    }

    public void PlayAll() => Shell.Play(Tracks, 0, Source);

    public void ShuffleAll() => Shell.Shuffle(Tracks, Source);

    public void RemoveFromPlaylist(IReadOnlyList<int> indices)
    {
        if (Playlist is { } playlist)
            Shell.RemoveFromPlaylist(playlist, indices.Select(ToPlaylistIndex).ToList());
    }

    public void Move(int from, int to)
    {
        if (Playlist is { } playlist && CanReorder)
            Shell.Services.Playlists.Move(playlist, ToPlaylistIndex(from), ToPlaylistIndex(to));
    }

    private int ToPlaylistIndex(int listIndex) =>
        PlaylistIndices is { } map && (uint)listIndex < (uint)map.Count ? map[listIndex] : listIndex;
}
