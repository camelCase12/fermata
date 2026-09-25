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

/// <summary>
/// A list of tracks and where they came from. Playing from it queues the whole list, starting at the
/// chosen track, so Next continues through the list the listener was looking at.
/// </summary>
public sealed partial class TrackListModel(Shell shell, QueueSource source, TrackListStyle style = TrackListStyle.Library) : ObservableObject
{
    public Shell Shell { get; } = shell;
    public TrackListStyle Style { get; } = style;

    public QueueSource Source { get; set; } = source;

    [ObservableProperty]
    public partial IReadOnlyList<Track> Tracks { get; set; } = [];

    /// <summary>The playlist shown, which enables removing and reordering entries.</summary>
    public Playlist? Playlist { get; init; }

    /// <summary>
    /// For a playlist, the position in the playlist of each listed track. They differ when a playlist
    /// names files that are no longer in the library, which are not listed.
    /// </summary>
    public IReadOnlyList<int>? PlaylistIndices { get; set; }

    /// <summary>Reordering by dragging is only meaningful for a playlist shown in its own order.</summary>
    [ObservableProperty]
    public partial bool CanReorder { get; set; }

    /// <summary>Column titles are shown above covers and columns, not above an album's numbered list.</summary>
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
