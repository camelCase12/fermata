using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Fermata.Library;
using Fermata.Playback;
using Fermata.Services;
using Fermata.ViewModels.Pages;

namespace Fermata.ViewModels;

/// <summary>The actions available throughout the app.</summary>
public sealed partial class Shell : ObservableObject
{
    private HomeViewModel? home;
    private ExploreViewModel? explore;
    private SongsViewModel? songs;
    private AlbumsViewModel? albums;
    private ArtistsViewModel? artists;
    private HistoryViewModel? history;
    private SettingsViewModel? settings;

    public Shell(AppServices services)
    {
        Services = services;
        services.Library.SnapshotChanged += _ =>
        {
            Navigator.Current?.LibraryChanged();
            Navigator.Forget(page => page is AlbumViewModel { IsGone: true } or ArtistViewModel { IsGone: true });
        };
        services.Playlists.ListChanged += () =>
            Navigator.Forget(page => page is PlaylistViewModel { Playlist: { } p } && !services.Playlists.Playlists.Contains(p));
    }

    public AppServices Services { get; }
    public Navigator Navigator { get; } = new();
    public ToastService Toasts { get; } = new();
    public Player Player => Services.Player;
    public LibrarySnapshot Library => Services.Library.Snapshot;

    /// <summary>The dialog on screen, if any.</summary>
    [ObservableProperty]
    public partial DialogViewModel? Dialog { get; private set; }

    /// <summary>Whether the now-playing view covers the page.</summary>
    [ObservableProperty]
    public partial bool IsNowPlayingOpen { get; set; }

    // Navigation -----------------------------------------------------------------------------

    public void GoHome() => Navigator.Navigate(home ??= new HomeViewModel(this));
    public void GoExplore() => Navigator.Navigate(explore ??= new ExploreViewModel(this));
    public void GoSongs() => Navigator.Navigate(songs ??= new SongsViewModel(this));
    public void GoAlbums() => Navigator.Navigate(albums ??= new AlbumsViewModel(this));
    public void GoArtists() => Navigator.Navigate(artists ??= new ArtistsViewModel(this));
    public void GoLiked() => Navigator.Navigate(new PlaylistViewModel(this, PlaylistKind.Liked));
    public void GoHistory() => Navigator.Navigate(history ??= new HistoryViewModel(this));
    public void GoSettings() => Navigator.Navigate(settings ??= new SettingsViewModel(this));
    public void OpenAlbum(Album album) => Navigator.Navigate(new AlbumViewModel(this, album));
    public void OpenArtist(Artist artist) => Navigator.Navigate(new ArtistViewModel(this, artist));
    public void OpenGenre(Genre genre) => Navigator.Navigate(new GenreViewModel(this, genre));
    public void OpenDecade(int decade) => Navigator.Navigate(new GenreViewModel(this, decade));
    public void OpenPlaylist(Playlist playlist) => Navigator.Navigate(new PlaylistViewModel(this, playlist));
    public void OpenMix(Mix mix) => Navigator.Navigate(new PlaylistViewModel(this, mix));

    public void OpenArtist(string name)
    {
        if (Library.FindArtist(name) is { } artist)
            OpenArtist(artist);
    }

    /// <summary>Opens whatever a card or search result stands for.</summary>
    public void Open(object item)
    {
        IsNowPlayingOpen = false;
        switch (item)
        {
            case Album album: OpenAlbum(album); break;
            case Artist artist: OpenArtist(artist); break;
            case Genre genre: OpenGenre(genre); break;
            case Playlist playlist: OpenPlaylist(playlist); break;
            case Mix mix: OpenMix(mix); break;
            case Track track: Play([track], 0, new QueueSource("song", track.Title)); break;
        }
    }

    public void OpenAlbumOf(Track track)
    {
        if (Library.AlbumOf(track) is { } album)
        {
            IsNowPlayingOpen = false;
            OpenAlbum(album);
        }
    }

    public void OpenArtistOf(Track track)
    {
        var artist = Library.ArtistsOf(track).FirstOrDefault() ?? (track.AlbumArtist.Length > 0 ? Library.FindArtist(track.AlbumArtist) : null);
        if (artist is not null)
        {
            IsNowPlayingOpen = false;
            OpenArtist(artist);
        }
    }

    private SearchViewModel? search;

    /// <summary>Shows the search results for <paramref name="query"/>.</summary>
    public void Search(string query)
    {
        IsNowPlayingOpen = false;
        if (Navigator.Current is SearchViewModel current)
        {
            current.Query = query;
            return;
        }
        search = new SearchViewModel(this) { Query = query };
        Navigator.Navigate(search);
    }

    // Playback -------------------------------------------------------------------------------

    public void Play(IReadOnlyList<Track> tracks, int index, QueueSource source)
    {
        if (tracks.Count > 0)
            Player.Play(tracks, index, source);
    }

    /// <summary>Plays in a random order, turning shuffle on.</summary>
    public void Shuffle(IReadOnlyList<Track> tracks, QueueSource source)
    {
        if (tracks.Count > 0)
            Player.Play(tracks, -1, source, shuffle: true);
    }

    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
            return;
        if (Player.CurrentEntry is null)
        {
            Play(tracks, 0, new QueueSource("queue", "Queue"));
            return;
        }
        Player.PlayNext(tracks);
        Toasts.Show(tracks.Count == 1 ? $"“{tracks[0].Title}” will play next" : $"{Formats.Count(tracks.Count, "song")} will play next");
    }

    public void AddToQueue(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
            return;
        if (Player.CurrentEntry is null)
        {
            Play(tracks, 0, new QueueSource("queue", "Queue"));
            return;
        }
        Player.AddToQueue(tracks);
        Toasts.Show(tracks.Count == 1 ? $"Added “{tracks[0].Title}” to the queue" : $"Added {Formats.Count(tracks.Count, "song")} to the queue");
    }

    /// <summary>Starts a radio station from a track.</summary>
    public void StartRadio(Track seed)
    {
        var tracks = new Recommender(Library, Services.UserData).Radio(seed);
        Play(tracks, 0, new QueueSource("radio", $"{seed.Title} radio"));
    }

    public void StartRadio(IReadOnlyList<Track> seeds, string title)
    {
        var tracks = new Recommender(Library, Services.UserData).Radio(seeds);
        if (tracks.Count == 0)
            return;
        Play(tracks, 0, new QueueSource("radio", title + " radio"));
    }

    // Likes ----------------------------------------------------------------------------------

    public bool IsLiked(Track track) => Services.UserData.IsLiked(track.Path);

    public void ToggleLike(Track track) => SetLiked([track], !IsLiked(track));

    public void SetLiked(IReadOnlyList<Track> tracks, bool liked)
    {
        foreach (var track in tracks)
            Services.UserData.SetLiked(track, liked);
        if (tracks.Count > 1)
            Toasts.Show(liked ? $"Liked {Formats.Count(tracks.Count, "song")}" : $"Removed {Formats.Count(tracks.Count, "song")} from liked songs");
    }

    public void Dislike(Track track)
    {
        Services.UserData.SetDisliked(track, true);
        Toasts.Show($"You won't hear “{track.Title}” in mixes or radio", "Undo", () => Services.UserData.SetDisliked(track, false));
        if (Player.CurrentTrack == track)
            Player.Next();
    }

    // Playlists ------------------------------------------------------------------------------

    public async Task<Playlist?> CreatePlaylistAsync(IReadOnlyList<Track>? tracks = null)
    {
        int outside = 0;
        if (tracks is not null)
        {
            (tracks, outside) = InLibrary(tracks);
            if (tracks.Count == 0)
            {
                Toasts.Show(OutsideLibraryMessage);
                return null;
            }
        }
        var dialog = new PromptDialog
        {
            Title = "New playlist",
            ConfirmLabel = "Create",
            Placeholder = "Playlist name",
            Text = tracks is [{ AlbumTitle.Length: > 0 } first] ? first.AlbumTitle : "",
        };
        string? name = await Show(dialog, dialog.Result);
        if (name is null)
            return null;
        var playlist = Services.Playlists.Create(name, tracks?.Select(t => t.Path));
        string created = tracks is { Count: > 0 } ? $"Created “{playlist.Name}” with {Formats.Count(tracks.Count, "song")}" : $"Created “{playlist.Name}”";
        Toasts.Show(outside > 0 ? $"{created}. {Formats.Count(outside, "song")} outside your library were left out." : created);
        return playlist;
    }

    private const string OutsideLibraryMessage = "Only songs in your library can go in playlists. Add their folder in Settings.";

    /// <summary>The tracks that are in the library, and how many are not.</summary>
    private (IReadOnlyList<Track> Tracks, int Outside) InLibrary(IReadOnlyList<Track> tracks)
    {
        var inside = tracks.Where(t => Library.FindTrack(t.Path) is not null).ToList();
        return (inside, tracks.Count - inside.Count);
    }

    public void AddToPlaylist(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        (tracks, int outside) = InLibrary(tracks);
        if (tracks.Count == 0)
        {
            Toasts.Show(OutsideLibraryMessage);
            return;
        }
        int before = playlist.Paths.Count;
        int added = Services.Playlists.Add(playlist, tracks.Select(t => t.Path));
        if (added == 0)
        {
            Toasts.Show(tracks.Count == 1 ? $"Already in “{playlist.Name}”" : $"All already in “{playlist.Name}”");
            return;
        }
        string message = $"Added {Formats.Count(added, "song")} to “{playlist.Name}”";
        Toasts.Show(outside > 0 ? $"{message}. {Formats.Count(outside, "song")} outside your library were left out." : message, "Undo",
            () => Services.Playlists.RemoveAt(playlist, Enumerable.Range(before, added)));
    }

    /// <summary>Creates a playlist from each M3U file.</summary>
    /// <remarks>Each playlist takes the name its file gives, or else the file's name.</remarks>
    public void ImportPlaylists(IReadOnlyList<string> files)
    {
        Playlist? imported = null;
        int count = 0, missing = 0;
        foreach (string file in files)
        {
            M3uPlaylist m3u;
            try
            {
                m3u = PlaylistStore.ReadM3u(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Toasts.Show($"Couldn't read {Path.GetFileName(file)}");
                continue;
            }
            imported = Services.Playlists.Create(m3u.Name ?? Path.GetFileNameWithoutExtension(file), m3u.Paths);
            count++;
            missing += m3u.Paths.Count(path => Library.FindTrack(path) is null);
        }
        if (imported is null)
            return;
        string what = count == 1 ? $"“{imported.Name}”" : Formats.Count(count, "playlist");
        Toasts.Show(missing == 0 ? $"Imported {what}" : $"Imported {what}; {Formats.Count(missing, "song")} not in your library");
        if (count == 1)
            OpenPlaylist(imported);
    }

    public void ExportPlaylist(Playlist playlist, string file)
    {
        try
        {
            PlaylistStore.ExportM3u(playlist, Library, file);
            Toasts.Show($"Exported “{playlist.Name}”");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Toasts.Show($"Couldn't write {Path.GetFileName(file)}");
        }
    }

    public void RemoveFromPlaylist(Playlist playlist, IReadOnlyList<int> indices)
    {
        var removed = indices.Where(i => (uint)i < (uint)playlist.Paths.Count).Order().Select(i => (Index: i, Path: playlist.Paths[i])).ToList();
        if (removed.Count == 0)
            return;
        Services.Playlists.RemoveAt(playlist, indices);
        Toasts.Show($"Removed {Formats.Count(removed.Count, "song")} from “{playlist.Name}”", "Undo", () =>
        {
            // Put each path back at its old position, lowest first.
            foreach (var (index, path) in removed)
            {
                Services.Playlists.Add(playlist, [path], allowDuplicates: true);
                Services.Playlists.Move(playlist, playlist.Paths.Count - 1, index);
            }
        });
    }

    public async Task RenamePlaylistAsync(Playlist playlist)
    {
        var dialog = new PromptDialog { Title = "Rename playlist", ConfirmLabel = "Rename", Text = playlist.Name };
        string? name = await Show(dialog, dialog.Result);
        if (name is null)
            return;
        Services.Playlists.Rename(playlist, name, playlist.Description);
        Player.Queue.RenameSource(playlist.Id, playlist.Name);
    }

    public async Task DeletePlaylistAsync(Playlist playlist)
    {
        var dialog = new ConfirmDialog
        {
            Title = "Delete playlist?",
            Message = $"“{playlist.Name}” will be deleted. The songs stay in your library.",
            ConfirmLabel = "Delete",
            IsDestructive = true,
        };
        if (!await Show(dialog, dialog.Result))
            return;
        if (Navigator.Current is PlaylistViewModel { Playlist: var shown } && shown == playlist)
            Navigator.GoBack();
        Services.Playlists.Delete(playlist);
        Toasts.Show($"Deleted “{playlist.Name}”");
    }

    public async Task ClearHistoryAsync()
    {
        var dialog = new ConfirmDialog
        {
            Title = "Clear listening history?",
            Message = "Recently played will be emptied. Play counts and likes are kept.",
            ConfirmLabel = "Clear",
            IsDestructive = true,
        };
        if (!await Show(dialog, dialog.Result))
            return;
        Services.UserData.ClearHistory();
        Toasts.Show("Cleared listening history");
    }

    // Files ----------------------------------------------------------------------------------

    public void ShowInFolder(Track track) => Launch("xdg-open", track.Directory);

    /// <summary>Shows a notice about saved files that could not be read.</summary>
    public void ReportUnreadable(IReadOnlyList<UnreadableFile> files)
    {
        if (files.Count == 0)
            return;
        var names = files.Where(f => f.What != "a playlist").Select(f => f.What).ToList();
        int playlists = files.Count(f => f.What == "a playlist");
        if (playlists > 0)
            names.Add(playlists == 1 ? "a playlist" : $"{playlists} playlists");
        string list = names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " and " + names[^1];
        bool one = files.Count == 1;
        string folder = Path.GetDirectoryName(files[0].Path) ?? "";
        Toasts.Show($"Couldn't read your {list}, so {(one ? "the file was" : "the files were")} set aside.",
            "Show", () => Launch("xdg-open", folder), TimeSpan.FromSeconds(15));
    }

    public async Task ShowTrackInfoAsync(Track track)
    {
        var dialog = new TrackInfoDialog(track, Services.UserData.StatsFor(track.Path)) { Title = "Song info" };
        await Show(dialog, dialog.Closed);
    }

    private async Task<T> Show<T>(DialogViewModel dialog, Task<T> result)
    {
        Dialog = dialog;
        try
        {
            return await result;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    private async Task Show(DialogViewModel dialog, Task result)
    {
        Dialog = dialog;
        try
        {
            await result;
        }
        finally
        {
            if (Dialog == dialog)
                Dialog = null;
        }
    }

    private void Launch(string program, string argument)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false };
            start.ArgumentList.Add(argument);
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Toasts.Show($"Couldn't run {program}");
        }
    }
}
