using Avalonia.Controls;
using Avalonia.Media;
using Fermata.Library;
using Fermata.ViewModels;

namespace Fermata.Views;

/// <summary>The context menu for one or more tracks, the same wherever tracks appear.</summary>
public static class TrackMenu
{
    /// <param name="tracks">The tracks acted on (the selection, or the clicked track).</param>
    /// <param name="removeFromPlaylist">Offered when the tracks are shown in a playlist.</param>
    /// <param name="extra">Items specific to the place the menu opened from (such as queue actions).</param>
    public static ContextMenu Build(Shell shell, IReadOnlyList<Track> tracks, Action? removeFromPlaylist = null,
        IEnumerable<Control>? extra = null)
    {
        var items = new List<Control>();
        var single = tracks.Count == 1 ? tracks[0] : null;
        items.Add(Item("Play next", "Icon.Queue", () => shell.PlayNext(tracks)));
        items.Add(Item("Add to queue", "Icon.PlaylistAdd", () => shell.AddToQueue(tracks)));
        items.Add(PlaylistMenu(shell, tracks));
        if (single is not null)
            items.Add(Item("Start radio", "Icon.Radio", () => shell.StartRadio(single)));
        else
            items.Add(Item("Start radio", "Icon.Radio", () => shell.StartRadio(tracks, $"{tracks.Count} songs")));
        if (extra is not null)
        {
            items.Add(new Separator());
            items.AddRange(extra);
        }
        items.Add(new Separator());
        bool allLiked = tracks.All(shell.IsLiked);
        items.Add(Item(allLiked ? "Remove from liked songs" : "Like", allLiked ? "Icon.Heart" : "Icon.HeartOutline",
            () => shell.SetLiked(tracks, !allLiked)));
        if (single is not null)
            items.Add(Item("Don't recommend this song", "Icon.ThumbDown", () => shell.Dislike(single)));
        if (removeFromPlaylist is not null)
            items.Add(Item(tracks.Count == 1 ? "Remove from this playlist" : $"Remove {tracks.Count} songs from this playlist", "Icon.Delete", removeFromPlaylist));
        if (single is not null)
        {
            items.Add(new Separator());
            if (shell.Library.AlbumOf(single) is not null)
                items.Add(Item("Go to album", "Icon.Album", () => shell.OpenAlbumOf(single)));
            var artists = shell.Library.ArtistsOf(single).ToList();
            if (artists.Count == 1)
                items.Add(Item("Go to artist", "Icon.Artist", () => shell.OpenArtist(artists[0])));
            else if (artists.Count > 1)
            {
                var artistMenu = new MenuItem { Header = "Go to artist", Icon = Icon("Icon.Artist") };
                foreach (var artist in artists)
                    artistMenu.Items.Add(Item(artist.Name, null, () => shell.OpenArtist(artist)));
                items.Add(artistMenu);
            }
            items.Add(Item("Show in folder", "Icon.Folder", () => shell.ShowInFolder(single)));
            items.Add(Item("Song info", "Icon.Info", () => _ = shell.ShowTrackInfoAsync(single)));
        }
        var menu = new ContextMenu();
        foreach (var item in items)
            menu.Items.Add(item);
        return menu;
    }

    private static MenuItem PlaylistMenu(Shell shell, IReadOnlyList<Track> tracks)
    {
        var menu = new MenuItem { Header = "Add to playlist", Icon = Icon("Icon.Playlist") };
        menu.Items.Add(Item("New playlist…", "Icon.Plus", () => _ = shell.CreatePlaylistAsync(tracks)));
        var playlists = shell.Services.Playlists.Playlists;
        if (playlists.Count > 0)
            menu.Items.Add(new Separator());
        foreach (var playlist in playlists.OrderByDescending(p => p.Modified).Take(30))
            menu.Items.Add(Item(playlist.Name, null, () => shell.AddToPlaylist(playlist, tracks)));
        return menu;
    }

    public static MenuItem Item(string header, string? icon, Action action)
    {
        var item = new MenuItem { Header = header, Icon = icon is null ? null : Icon(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private static PathIcon? Icon(string key) =>
        Avalonia.Application.Current!.TryFindResource(key, out var resource) && resource is Geometry geometry
            ? new PathIcon { Data = geometry }
            : null;
}
