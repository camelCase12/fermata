using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.ViewModels.Pages;

namespace Fermata.ViewModels;

/// <summary>The main window: sidebar, search, page area, player bar and overlays.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer searchDelay;

    public MainViewModel(Shell shell)
    {
        Shell = shell;
        Player = new PlayerViewModel(shell);
        NowPlaying = new NowPlayingViewModel(shell, Player);
        searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        searchDelay.Tick += (_, _) =>
        {
            searchDelay.Stop();
            RunSearch();
        };

        var services = shell.Services;
        services.Playlists.ListChanged += SyncPlaylists;
        SyncPlaylists();
        shell.Navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Navigator.Current))
            {
                CurrentSection = shell.Navigator.Current?.Section ?? Section.None;
                CurrentPlaylist = shell.Navigator.Current is PlaylistViewModel { Playlist: { } p } ? p : null;
                if (shell.Navigator.Current is not SearchViewModel && SearchText.Length > 0)
                {
                    searchText = "";
                    OnPropertyChanged(nameof(SearchText));
                }
            }
        };
        services.Library.ScanProgressed += progress =>
            LibraryStatus = progress.Enumerating
                ? $"Looking for music… {Formats.Count(progress.FilesFound, "file")}"
                : progress.FilesRead < progress.FilesToRead
                    ? $"Reading {progress.FilesRead:N0} of {progress.FilesToRead:N0}"
                    : null;
        services.Library.ScanCompleted += result =>
        {
            LibraryStatus = null;
            if (result.Unreadable > 0)
                shell.Toasts.Show($"{Formats.Count(result.Unreadable, "file")} could not be read");
        };
        services.Library.ScanFailed += error => shell.Toasts.Show("Scanning failed: " + error.Message);
    }

    public Shell Shell { get; }
    public PlayerViewModel Player { get; }
    public NowPlayingViewModel NowPlaying { get; }
    public ObservableCollection<Playlist> Playlists { get; } = [];

    [ObservableProperty] public partial Section CurrentSection { get; private set; }
    [ObservableProperty] public partial Playlist? CurrentPlaylist { get; private set; }

    /// <summary>The playlist highlighted in the sidebar; choosing one opens it.</summary>
    public Playlist? SelectedPlaylist
    {
        get => CurrentPlaylist;
        set
        {
            if (value is not null && value != CurrentPlaylist)
                Shell.OpenPlaylist(value);
        }
    }

    partial void OnCurrentPlaylistChanged(Playlist? value) => OnPropertyChanged(nameof(SelectedPlaylist));
    [ObservableProperty] public partial string? LibraryStatus { get; private set; }

    public bool IsHome { get => CurrentSection == Section.Home; set { if (value) Shell.GoHome(); } }
    public bool IsExplore { get => CurrentSection == Section.Explore; set { if (value) Shell.GoExplore(); } }
    public bool IsSongs { get => CurrentSection == Section.Songs; set { if (value) Shell.GoSongs(); } }
    public bool IsAlbums { get => CurrentSection == Section.Albums; set { if (value) Shell.GoAlbums(); } }
    public bool IsArtists { get => CurrentSection == Section.Artists; set { if (value) Shell.GoArtists(); } }
    public bool IsLiked { get => CurrentSection == Section.Liked; set { if (value) Shell.GoLiked(); } }
    public bool IsHistory { get => CurrentSection == Section.History; set { if (value) Shell.GoHistory(); } }
    public bool IsSettings { get => CurrentSection == Section.Settings; set { if (value) Shell.GoSettings(); } }

    partial void OnCurrentSectionChanged(Section value)
    {
        foreach (string name in (string[])[nameof(IsHome), nameof(IsExplore), nameof(IsSongs), nameof(IsAlbums),
            nameof(IsArtists), nameof(IsLiked), nameof(IsHistory), nameof(IsSettings)])
            OnPropertyChanged(name);
    }

    private string searchText = "";

    /// <summary>Search as you type: results follow the text after a short pause.</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value))
                return;
            searchDelay.Stop();
            searchDelay.Start();
        }
    }

    private void RunSearch()
    {
        string query = SearchText.Trim();
        if (query.Length > 0)
            Shell.Search(query);
        else if (Shell.Navigator.Current is SearchViewModel)
            Shell.Navigator.GoBack();
    }

    /// <summary>Enter in the search box: search at once.</summary>
    public void SubmitSearch()
    {
        searchDelay.Stop();
        RunSearch();
    }

    private void SyncPlaylists()
    {
        var current = Shell.Services.Playlists.Playlists;
        if (Playlists.SequenceEqual(current))
        {
            // Same playlists; a rename needs the list to refresh its labels.
            for (int i = 0; i < Playlists.Count; i++)
                Playlists[i] = current[i];
            return;
        }
        Playlists.Clear();
        foreach (var playlist in current)
            Playlists.Add(playlist);
    }

    [RelayCommand]
    private void OpenPlaylist(Playlist playlist) => Shell.OpenPlaylist(playlist);

    [RelayCommand]
    private async Task NewPlaylist()
    {
        if (await Shell.CreatePlaylistAsync() is { } playlist)
            Shell.OpenPlaylist(playlist);
    }

    [RelayCommand]
    private void Back() => Shell.Navigator.GoBack();

    [RelayCommand]
    private void Forward() => Shell.Navigator.GoForward();
}
