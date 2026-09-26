using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.ViewModels.Pages;

/// <summary>Disc header or track, for albums split into discs.</summary>
public sealed record DiscHeader(int Disc);

/// <summary>The page of an album.</summary>
public sealed partial class AlbumViewModel : PageViewModel
{
    private string key;

    public AlbumViewModel(Shell shell, Album album) : base(shell)
    {
        key = album.Key;
        Album = album;
        List = new TrackListModel(shell, new QueueSource("album", album.Title, album.Key), TrackListStyle.Album);
    }

    public override Section Section => Section.Albums;

    [ObservableProperty] public partial Album Album { get; private set; }
    [ObservableProperty] public partial string Kind { get; private set; } = "Album";
    [ObservableProperty] public partial string Details { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<object> MoreByArtist { get; private set; } = [];
    [ObservableProperty] public partial string MoreTitle { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<Artist> Artists { get; private set; } = [];

    public TrackListModel List { get; }

    /// <summary>Whether the album is no longer in the library.</summary>
    public bool IsGone { get; private set; }

    protected override void Refresh()
    {
        var library = Shell.Library;
        if (library.FindAlbum(key) is { } fresh)
            Album = fresh;
        else
        {
            IsGone = true;
            return;
        }
        var album = Album;
        Backdrop = album.Art is { } art ? [art] : [];
        List.Tracks = album.Tracks;
        Kind = album.IsCompilation ? "Compilation" : album.Tracks.Count <= 3 && album.Duration < TimeSpan.FromMinutes(15) ? "Single" : album.Tracks.Count <= 6 && album.Duration < TimeSpan.FromMinutes(30) ? "EP" : "Album";
        Details = string.Join(" · ", new[]
        {
            album.Year > 0 ? album.Year.ToString() : "",
            Formats.Count(album.Tracks.Count, "song"),
            Formats.LongDuration(album.Duration),
            album.Genre,
        }.Where(s => s.Length > 0));
        Artists = album.ArtistNames.Select(library.FindArtist).OfType<Artist>().ToList();
        var main = Artists.FirstOrDefault();
        MoreByArtist = main is null ? [] : main.Albums.Where(a => a != album).Cast<object>().ToList();
        MoreTitle = main is null ? "" : $"More by {main.Name}";
    }

    [RelayCommand]
    private void Play() => List.PlayAll();

    [RelayCommand]
    private void Shuffle() => List.ShuffleAll();

    [RelayCommand]
    private void AddToQueue() => Shell.AddToQueue(Album.Tracks);

    [RelayCommand]
    private void PlayNext() => Shell.PlayNext(Album.Tracks);

    [RelayCommand]
    private void Radio() => Shell.StartRadio(Album.Tracks, Album.Title);

    [RelayCommand]
    private void OpenArtist(Artist artist) => Shell.OpenArtist(artist);

    [RelayCommand]
    private void ShowFolder() => Shell.ShowInFolder(Album.Tracks[0]);
}

/// <summary>The page of an artist.</summary>
public sealed partial class ArtistViewModel : PageViewModel
{
    private readonly string name;

    public ArtistViewModel(Shell shell, Artist artist) : base(shell)
    {
        name = artist.Name;
        Artist = artist;
        TopSongs = new TrackListModel(shell, new QueueSource("artist", artist.Name, artist.Name), TrackListStyle.Artist);
    }

    public override Section Section => Section.Artists;

    [ObservableProperty] public partial Artist Artist { get; private set; }
    [ObservableProperty] public partial string Details { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<object> Albums { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<object> AppearsOn { get; private set; } = [];
    [ObservableProperty] public partial bool HasAppearances { get; private set; }
    [ObservableProperty] public partial bool ShowAllSongs { get; set; }

    public TrackListModel TopSongs { get; }
    public bool IsGone { get; private set; }

    partial void OnShowAllSongsChanged(bool value) => UpdateSongs();

    protected override void Refresh()
    {
        if (Shell.Library.FindArtist(name) is { } fresh)
            Artist = fresh;
        else
        {
            IsGone = true;
            return;
        }
        Backdrop = Artist.Art is { } art ? [art] : [];
        Albums = Artist.Albums.Cast<object>().ToList();
        AppearsOn = Artist.AppearsOn.Cast<object>().ToList();
        HasAppearances = AppearsOn.Count > 0;
        Details = string.Join(" · ", new[]
        {
            Artist.Albums.Count > 0 ? Formats.Count(Artist.Albums.Count, "album") : "",
            Formats.Count(Artist.Tracks.Count, "song"),
            Formats.LongDuration(Artist.Duration),
        }.Where(s => s.Length > 0));
        UpdateSongs();
    }

    /// <summary>Updates the artist's popular songs.</summary>
    /// <remarks>The most played come first, then library order, and five are shown until the list is expanded.</remarks>
    private void UpdateSongs()
    {
        var userData = Shell.Services.UserData;
        var ranked = Artist.Tracks.Select((track, index) => (Track: track, Index: index, Plays: userData.PlayCount(track.Path)))
            .OrderByDescending(e => e.Plays).ThenBy(e => e.Index).Select(e => e.Track);
        TopSongs.Tracks = ShowAllSongs ? ranked.ToList() : ranked.Take(5).ToList();
    }

    public bool HasMoreSongs => Artist.Tracks.Count > 5;

    [RelayCommand]
    private void Play() => Shell.Play(Artist.Tracks, 0, TopSongs.Source);

    [RelayCommand]
    private void Shuffle() => Shell.Shuffle(Artist.Tracks, TopSongs.Source);

    [RelayCommand]
    private void Radio() => Shell.StartRadio(Artist.Tracks, Artist.Name);

    [RelayCommand]
    private void ToggleAllSongs() => ShowAllSongs = !ShowAllSongs;
}

public enum PlaylistKind
{
    Playlist,
    Liked,
    Mix,
}

/// <summary>The page of a playlist, the liked songs or a generated mix.</summary>
public sealed partial class PlaylistViewModel : PageViewModel
{
    private readonly Mix? mix;

    public PlaylistViewModel(Shell shell, Playlist playlist) : base(shell)
    {
        Kind = PlaylistKind.Playlist;
        Playlist = playlist;
        List = new TrackListModel(shell, new QueueSource("playlist", playlist.Name, playlist.Id)) { Playlist = playlist, CanReorder = true };
        shell.Services.Playlists.PlaylistChanged += changed =>
        {
            if (changed == playlist)
                Refresh();
        };
        shell.Services.Playlists.ListChanged += () => Title = playlist.Name;
    }

    public PlaylistViewModel(Shell shell, PlaylistKind kind) : base(shell)
    {
        Kind = kind;
        List = new TrackListModel(shell, new QueueSource("liked", "Liked songs"));
        shell.Services.UserData.TrackChanged += _ =>
        {
            if (shell.Navigator.Current == this)
                Refresh();
        };
    }

    public PlaylistViewModel(Shell shell, Mix mix) : base(shell)
    {
        Kind = PlaylistKind.Mix;
        this.mix = mix;
        List = new TrackListModel(shell, new QueueSource("mix", mix.Title));
    }

    public PlaylistKind Kind { get; }
    public Playlist? Playlist { get; }
    public TrackListModel List { get; }
    public override Section Section => Kind switch { PlaylistKind.Liked => Section.Liked, PlaylistKind.Playlist => Section.Playlist, _ => Section.Home };
    public bool IsUserPlaylist => Kind == PlaylistKind.Playlist;
    public bool IsMix => Kind == PlaylistKind.Mix;
    public string KindLabel => Kind switch { PlaylistKind.Liked => "Collection", PlaylistKind.Mix => "Mix", _ => "Playlist" };

    /// <summary>Writes the playlist to an M3U file.</summary>
    public void Export(string file)
    {
        if (Playlist is { } playlist)
            Shell.ExportPlaylist(playlist, file);
    }

    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Description { get; private set; } = "";
    [ObservableProperty] public partial string Details { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<ArtSource> Covers { get; private set; } = [];
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial string EmptyMessage { get; private set; } = "";

    protected override void Refresh()
    {
        var library = Shell.Library;
        IReadOnlyList<Track> tracks;
        int unavailable = 0;
        switch (Kind)
        {
            case PlaylistKind.Playlist:
                var resolved = new List<Track>(Playlist!.Paths.Count);
                var positions = new List<int>(Playlist.Paths.Count);
                for (int i = 0; i < Playlist.Paths.Count; i++)
                {
                    if (library.FindTrack(Playlist.Paths[i]) is { } track)
                    {
                        resolved.Add(track);
                        positions.Add(i);
                    }
                }
                tracks = resolved;
                List.PlaylistIndices = positions;
                unavailable = Playlist.Paths.Count - resolved.Count;
                Title = Playlist.Name;
                Description = Playlist.Description;
                EmptyMessage = unavailable > 0
                    ? "The songs in this playlist are not in your library right now. They come back when their folder is available again."
                    : "Add songs with “Add to playlist” in any song's menu, or by dragging them onto the playlist in the sidebar.";
                break;
            case PlaylistKind.Liked:
                var userData = Shell.Services.UserData;
                tracks = userData.Tracks.Where(p => p.Value.Liked is not null)
                    .OrderByDescending(p => p.Value.Liked)
                    .Select(p => library.FindTrack(p.Key)).OfType<Track>().ToList();
                Title = "Liked songs";
                Description = "";
                EmptyMessage = "Songs you like appear here. Press the heart next to a song, or F while it plays.";
                break;
            default:
                tracks = mix!.Tracks;
                Title = mix.Title;
                Description = mix.Description;
                EmptyMessage = "";
                break;
        }
        List.Tracks = tracks;
        IsEmpty = tracks.Count == 0;
        Covers = HomeFeed.Covers(library, tracks);
        Backdrop = Covers;
        var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        Details = tracks.Count == 0 ? "No songs yet" : $"{Formats.Count(tracks.Count, "song")} · {Formats.LongDuration(total)}";
        if (unavailable > 0)
            Details = tracks.Count == 0 ? $"{unavailable} unavailable" : $"{Details} · {unavailable} unavailable";
    }

    [RelayCommand]
    private void Play() => List.PlayAll();

    [RelayCommand]
    private void Shuffle() => List.ShuffleAll();

    [RelayCommand]
    private void AddToQueue() => Shell.AddToQueue(List.Tracks);

    [RelayCommand]
    private void Radio() => Shell.StartRadio(List.Tracks, Title);

    [RelayCommand]
    private async Task Rename()
    {
        if (Playlist is { } playlist)
            await Shell.RenamePlaylistAsync(playlist);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Playlist is { } playlist)
            await Shell.DeletePlaylistAsync(playlist);
    }

    /// <summary>Saves a mix or the liked songs as a playlist.</summary>
    [RelayCommand]
    private async Task SaveAsPlaylist()
    {
        if (await Shell.CreatePlaylistAsync(List.Tracks) is { } playlist)
            Shell.OpenPlaylist(playlist);
    }
}

/// <summary>The page of a genre or a decade.</summary>
public sealed partial class GenreViewModel : PageViewModel
{
    private readonly string? genreName;
    private readonly int decade;

    public GenreViewModel(Shell shell, Genre genre) : base(shell)
    {
        genreName = genre.Name;
        Title = genre.Name;
        List = new TrackListModel(shell, new QueueSource("genre", genre.Name, genre.Name));
    }

    public GenreViewModel(Shell shell, int decade) : base(shell)
    {
        this.decade = decade;
        Title = $"{decade}s";
        List = new TrackListModel(shell, new QueueSource("decade", $"The {decade}s"));
    }

    public override Section Section => Section.Explore;
    public TrackListModel List { get; }
    public string Title { get; }
    public string KindLabel => genreName is null ? "Decade" : "Genre";

    [ObservableProperty] public partial IReadOnlyList<object> Albums { get; private set; } = [];
    [ObservableProperty] public partial string Details { get; private set; } = "";

    protected override void Refresh()
    {
        var library = Shell.Library;
        IReadOnlyList<Track> tracks;
        IReadOnlyList<Album> albums;
        if (genreName is not null)
        {
            var genre = library.FindGenre(genreName);
            tracks = genre?.Tracks ?? [];
            albums = genre?.Albums ?? [];
        }
        else
        {
            tracks = library.Tracks.Where(t => t.Year / 10 * 10 == decade).ToList();
            albums = library.Albums.Where(a => a.Year / 10 * 10 == decade).OrderBy(a => a.Year).ToList();
        }
        List.Tracks = tracks;
        Albums = albums.Cast<object>().ToList();
        Details = albums.Count == 0
            ? Formats.Count(tracks.Count, "song")
            : $"{Formats.Count(albums.Count, "album")} · {Formats.Count(tracks.Count, "song")}";
    }

    [RelayCommand]
    private void Shuffle() => List.ShuffleAll();

    [RelayCommand]
    private void Radio() => Shell.StartRadio(List.Tracks, Title);
}
