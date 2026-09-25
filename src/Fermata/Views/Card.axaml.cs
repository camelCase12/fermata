using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Fermata.Library;
using Fermata.Playback;
using Fermata.ViewModels;

namespace Fermata.Views;

/// <summary>
/// A tile for an album, artist, mix or playlist: cover, name and a short description. Clicking opens
/// it; the play button on hover plays it; right-click offers queue and playlist actions.
/// </summary>
public partial class Card : UserControl
{
    public Card()
    {
        InitializeComponent();
        PlayButton.Click += (_, e) =>
        {
            Play(shuffle: false);
            e.Handled = true;
        };
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && DataContext is { } item && App.Shell is { } shell)
                shell.Open(item);
        };
        ContextRequested += (_, e) =>
        {
            OpenMenu();
            e.Handled = true;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var shell = App.Shell;
        Art.IsRound = false;
        Art.IsVisible = true;
        MosaicArt.IsVisible = false;
        Title.HorizontalAlignment = HorizontalAlignment.Left;
        Subtitle.HorizontalAlignment = HorizontalAlignment.Left;
        switch (DataContext)
        {
            case Album album:
                Art.Source = album.Art;
                Art.PlaceholderKey = album.Key;
                Title.Text = album.Title;
                Subtitle.Text = album.Year > 0 ? $"{album.Artist} • {album.Year}" : album.Artist;
                break;
            case Artist artist:
                Art.Source = artist.Art;
                Art.PlaceholderKey = artist.Name;
                Art.IsRound = true;
                Title.Text = artist.Name;
                Title.HorizontalAlignment = HorizontalAlignment.Center;
                Subtitle.HorizontalAlignment = HorizontalAlignment.Center;
                Subtitle.Text = artist.Albums.Count > 0 ? Formats.Count(artist.Albums.Count, "album") : Formats.Count(artist.Tracks.Count, "song");
                break;
            case Mix mix:
                ShowMosaic(mix.Covers, mix.Title);
                Title.Text = mix.Title;
                Subtitle.Text = mix.Description;
                break;
            case Playlist playlist when shell is not null:
                var tracks = playlist.Resolve(shell.Library);
                ShowMosaic(HomeFeed.Covers(shell.Library, tracks), playlist.Id);
                Title.Text = playlist.Name;
                Subtitle.Text = $"Playlist • {Formats.Count(tracks.Count, "song")}";
                break;
        }
    }

    private void ShowMosaic(IReadOnlyList<ArtSource> covers, string key)
    {
        Art.IsVisible = false;
        MosaicArt.IsVisible = true;
        MosaicArt.PlaceholderKey = key;
        MosaicArt.Covers = covers;
    }

    private (IReadOnlyList<Track> Tracks, QueueSource Source)? Contents()
    {
        var shell = App.Shell;
        return DataContext switch
        {
            Album album => (album.Tracks, new QueueSource("album", album.Title, album.Key)),
            Artist artist => (artist.Tracks, new QueueSource("artist", artist.Name, artist.Name)),
            Mix mix => (mix.Tracks, new QueueSource("mix", mix.Title)),
            Playlist playlist when shell is not null => (playlist.Resolve(shell.Library), new QueueSource("playlist", playlist.Name, playlist.Id)),
            _ => null,
        };
    }

    private void Play(bool shuffle)
    {
        if (App.Shell is not { } shell || Contents() is not var (tracks, source))
            return;
        if (shuffle)
            shell.Shuffle(tracks, source);
        else
            shell.Play(tracks, 0, source);
    }

    private void OpenMenu()
    {
        if (App.Shell is not { } shell || Contents() is not var (tracks, _))
            return;
        var menu = new ContextMenu();
        menu.Items.Add(TrackMenu.Item("Play", "Icon.Play", () => Play(shuffle: false)));
        menu.Items.Add(TrackMenu.Item("Shuffle", "Icon.Shuffle", () => Play(shuffle: true)));
        menu.Items.Add(TrackMenu.Item("Play next", "Icon.Queue", () => shell.PlayNext(tracks)));
        menu.Items.Add(TrackMenu.Item("Add to queue", "Icon.PlaylistAdd", () => shell.AddToQueue(tracks)));
        menu.Items.Add(TrackMenu.Item("Start radio", "Icon.Radio", () => shell.StartRadio(tracks, Title.Text ?? "")));
        menu.Items.Add(TrackMenu.Item("Add to a new playlist…", "Icon.Playlist", () => _ = shell.CreatePlaylistAsync(tracks)));
        if (DataContext is Album album && album.ArtistNames.Count > 0 && shell.Library.FindArtist(album.ArtistNames[0]) is { } artist)
            menu.Items.Add(TrackMenu.Item("Go to artist", "Icon.Artist", () => shell.OpenArtist(artist)));
        if (DataContext is Playlist playlist)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(TrackMenu.Item("Rename…", "Icon.Edit", () => _ = shell.RenamePlaylistAsync(playlist)));
            menu.Items.Add(TrackMenu.Item("Delete…", "Icon.Delete", () => _ = shell.DeletePlaylistAsync(playlist)));
        }
        menu.Open(this);
    }
}
