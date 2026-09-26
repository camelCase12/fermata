using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.ViewModels.Pages;

/// <summary>A coloured tile for a genre or a decade.</summary>
public sealed record BrowseTile(string Title, string Subtitle, IBrush Background, Genre? Genre, int Decade);

/// <summary>The Explore page.</summary>
public sealed partial class ExploreViewModel : PageViewModel
{
    public ExploreViewModel(Shell shell) : base(shell)
    {
        TopSongs = new TrackListModel(shell, new QueueSource("chart", "Your top songs"));
    }

    public override Section Section => Section.Explore;
    public TrackListModel TopSongs { get; }

    [ObservableProperty] public partial IReadOnlyList<BrowseTile> Genres { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BrowseTile> Decades { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<object> NewAlbums { get; private set; } = [];
    [ObservableProperty] public partial bool HasTopSongs { get; private set; }

    protected override void Refresh()
    {
        var library = Shell.Library;
        Genres = library.Genres.OrderByDescending(g => g.Tracks.Count).Take(24)
            .Select(g => new BrowseTile(g.Name, Formats.Count(g.Tracks.Count, "song"), TileBrush(g.Name), g, 0)).ToList();
        Decades = library.Tracks.Where(t => t.Year > 0).GroupBy(t => t.Year / 10 * 10).OrderByDescending(g => g.Key)
            .Select(g => new BrowseTile($"{g.Key}s", Formats.Count(g.Count(), "song"), TileBrush(g.Key.ToString()), null, g.Key)).ToList();
        NewAlbums = library.Albums.OrderByDescending(a => a.Added).Take(12).Cast<object>().ToList();
        var userData = Shell.Services.UserData;
        TopSongs.Tracks = userData.Tracks.Where(p => p.Value.Plays > 0).OrderByDescending(p => p.Value.Plays)
            .Select(p => library.FindTrack(p.Key)).OfType<Track>().Take(20).ToList();
        HasTopSongs = TopSongs.Tracks.Count > 0;
    }

    /// <summary>Makes the gradient of a tile from its name.</summary>
    public static IBrush TileBrush(string name)
    {
        uint hash = 2166136261;
        foreach (char c in name.ToLowerInvariant())
            hash = (hash ^ c) * 16777619;
        double hue = hash % 360;
        return new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(HsvColor.ToRgb(hue, 0.55, 0.62), 0),
                new GradientStop(HsvColor.ToRgb((hue + 40) % 360, 0.65, 0.38), 1),
            },
        };
    }

    [RelayCommand]
    private void OpenTile(BrowseTile tile)
    {
        if (tile.Genre is { } genre)
            Shell.OpenGenre(genre);
        else
            Shell.OpenDecade(tile.Decade);
    }
}

/// <summary>Search results for the text in the search box.</summary>
public sealed partial class SearchViewModel : PageViewModel
{
    private int request;

    public SearchViewModel(Shell shell) : base(shell)
    {
        Songs = new TrackListModel(shell, new QueueSource("search", "Search"));
    }

    public TrackListModel Songs { get; }

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial object? TopResult { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<object> Albums { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<object> Artists { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<object> Playlists { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<BrowseTile> Genres { get; private set; } = [];
    [ObservableProperty] public partial bool HasResults { get; private set; }
    [ObservableProperty] public partial bool ShowAllSongs { get; set; }
    [ObservableProperty] public partial string SongsSummary { get; private set; } = "";

    private IReadOnlyList<Track> allSongs = [];

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnShowAllSongsChanged(bool value) => Songs.Tracks = value ? allSongs : allSongs.Take(6).ToList();

    protected override async void Refresh()
    {
        int current = ++request;
        string query = Query;
        var index = await Shell.Services.Library.GetSearchIndexAsync();
        var userData = Shell.Services.UserData;
        // The user data belongs to the UI thread, so ranking in the background uses a copy.
        var ranking = UserData.FromDocument(userData.ToDocument());
        var results = await Task.Run(() => index.Search(query, ranking));
        if (current != request)
            return;
        allSongs = results.Tracks;
        ShowAllSongs = false;
        Songs.Tracks = allSongs.Take(6).ToList();
        Songs.Source = new QueueSource("search", $"Search “{query}”");
        SongsSummary = allSongs.Count > 6 ? $"Show all {allSongs.Count}" : "";
        TopResult = results.TopResult;
        Albums = results.Albums.Take(18).Cast<object>().ToList();
        Artists = results.Artists.Take(18).Cast<object>().ToList();
        Genres = results.Genres.Take(8).Select(g => new BrowseTile(g.Name, Formats.Count(g.Tracks.Count, "song"), ExploreViewModel.TileBrush(g.Name), g, 0)).ToList();
        string folded = Text.TextFolding.Fold(query);
        Playlists = Shell.Services.Playlists.Playlists
            .Where(p => folded.Length > 0 && Text.TextFolding.Fold(p.Name).Contains(folded, StringComparison.Ordinal))
            .Cast<object>().ToList();
        HasResults = !results.IsEmpty || Playlists.Count > 0;
    }

    [RelayCommand]
    private void ToggleAllSongs() => ShowAllSongs = !ShowAllSongs;

    [RelayCommand]
    private void PlayTopResult()
    {
        switch (TopResult)
        {
            case Track track:
                Shell.Play([track], 0, new QueueSource("search", track.Title));
                break;
            case Album album:
                Shell.Play(album.Tracks, 0, new QueueSource("album", album.Title, album.Key));
                break;
            case Artist artist:
                Shell.Play(artist.Tracks, 0, new QueueSource("artist", artist.Name, artist.Name));
                break;
        }
    }

    [RelayCommand]
    private void OpenTopResult()
    {
        if (TopResult is not null)
            Shell.Open(TopResult);
    }

    [RelayCommand]
    private void OpenTile(BrowseTile tile)
    {
        if (tile.Genre is { } genre)
            Shell.OpenGenre(genre);
    }
}

public sealed record HistoryGroup(string Title, TrackListModel List);

/// <summary>The Recently played page.</summary>
public sealed partial class HistoryViewModel(Shell shell) : PageViewModel(shell)
{
    private const int Shown = 400;

    public override Section Section => Section.History;

    [ObservableProperty] public partial IReadOnlyList<HistoryGroup> Groups { get; private set; } = [];
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    protected override void Refresh()
    {
        var library = Shell.Library;
        var history = Shell.Services.UserData.History;
        var today = DateTime.Now;
        var groups = new List<HistoryGroup>();
        string? title = null;
        List<Track>? tracks = null;
        for (int i = history.Count - 1, count = 0; i >= 0 && count < Shown; i--)
        {
            if (library.FindTrack(history[i].Path) is not { } track)
                continue;
            string day = Formats.Day(history[i].At.ToLocalTime(), today);
            if (day != title)
            {
                title = day;
                tracks = [];
                groups.Add(new HistoryGroup(day, new TrackListModel(Shell, new QueueSource("history", "Recently played")) { Tracks = tracks }));
            }
            tracks!.Add(track);
            count++;
        }
        Groups = groups;
        IsEmpty = groups.Count == 0;
    }

    protected override void OnActivated() => Refresh();
}

public sealed record ShortcutRow(string Keys, string Action);

/// <summary>The Settings page.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(Shell shell) : base(shell)
    {
        var settings = shell.Services.Settings;
        foreach (string folder in settings.MusicFolders)
            Folders.Add(folder);
        ReplayGain = settings.ReplayGain;
        Gapless = settings.Gapless;
        Autoplay = settings.Autoplay;
        ResumeSession = settings.ResumeSession;
        AccentFromArt = settings.AccentFromArt;
        GeneratedBackdrop = settings.GeneratedBackdrop;
        WatchFolders = settings.WatchFolders;
        shell.Services.Library.ScanCompleted += _ => UpdateStatistics();
        shell.Player.Engine.EventRaised += e =>
        {
            if (e is DevicesChanged)
                UpdateDevices();
        };
        UpdateDevices();
    }

    public override Section Section => Section.Settings;

    public ObservableCollection<string> Folders { get; } = [];
    public IReadOnlyList<ReplayGainMode> ReplayGainModes { get; } = [ReplayGainMode.Off, ReplayGainMode.Track, ReplayGainMode.Album];

    [ObservableProperty] public partial ReplayGainMode ReplayGain { get; set; }
    [ObservableProperty] public partial bool Gapless { get; set; }
    [ObservableProperty] public partial bool Autoplay { get; set; }
    [ObservableProperty] public partial bool ResumeSession { get; set; }
    [ObservableProperty] public partial bool AccentFromArt { get; set; }
    [ObservableProperty] public partial bool GeneratedBackdrop { get; set; }
    [ObservableProperty] public partial bool WatchFolders { get; set; }
    [ObservableProperty] public partial string Statistics { get; private set; } = "";
    [ObservableProperty] public partial IReadOnlyList<AudioDevice> Devices { get; private set; } = [];
    [ObservableProperty] public partial AudioDevice? Device { get; set; }
    [ObservableProperty] public partial string NewFolder { get; set; } = "";

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";
    public string EngineStatus => Shell.Player.IsEngineAvailable ? "libmpv" : Shell.Player.EngineUnavailableReason ?? "Unavailable";
    public string DataLocations => $"Settings: {Shell.Services.Paths.Config}\nPlaylists and likes: {Shell.Services.Paths.Data}\nLibrary index: {Shell.Services.Paths.Cache}";

    public IReadOnlyList<ShortcutRow> Shortcuts { get; } =
    [
        new("Space  or  K", "Play or pause"),
        new("Ctrl+→  or  Shift+N", "Next song"),
        new("Ctrl+←  or  Shift+P", "Previous song (restarts after 3 seconds)"),
        new("J  /  L", "Back / forward 10 seconds"),
        new("←  /  →  (not in a list)", "Back / forward 5 seconds"),
        new("Ctrl+↑  /  Ctrl+↓", "Volume up / down"),
        new("M", "Mute"),
        new("S", "Shuffle on or off"),
        new("R", "Repeat: off, all, one"),
        new("F", "Like the playing song"),
        new("Q", "Now playing and queue"),
        new("Ctrl+F  or  /", "Search"),
        new("Alt+←  /  Alt+→", "Back / forward between pages"),
        new("Ctrl+1 … Ctrl+7", "Home, Explore, Songs, Albums, Artists, Liked songs, Recently played"),
        new("Ctrl+,", "Settings"),
        new("Enter", "Play the selected song"),
        new("Delete", "Remove the selection from a playlist or the queue"),
        new("Alt+↑  /  Alt+↓", "Move the selected song in a playlist or the queue (or drag it)"),
        new("Ctrl+Q", "Quit"),
    ];

    protected override void Refresh() => UpdateStatistics();

    private void UpdateStatistics()
    {
        var library = Shell.Library;
        Statistics = library.Tracks.Count == 0
            ? "No music found yet."
            : $"{Formats.Count(library.Tracks.Count, "song")} • {Formats.Count(library.Albums.Count, "album")} • "
              + $"{Formats.Count(library.Artists.Count, "artist")} • {Formats.LongDuration(library.TotalDuration)} • {Formats.Size(library.TotalSize)}";
    }

    private void UpdateDevices()
    {
        var devices = Shell.Player.Engine.AudioDevices;
        Devices = devices.Count > 0 ? devices : [new AudioDevice("auto", "System default")];
        Device = Devices.FirstOrDefault(d => d.Name == Shell.Services.Settings.AudioDevice) ?? Devices[0];
    }

    partial void OnDeviceChanged(AudioDevice? value)
    {
        if (value is null || value.Name == Shell.Services.Settings.AudioDevice)
            return;
        Shell.Services.Settings.AudioDevice = value.Name;
        Shell.Player.Engine.AudioDevice = value.Name;
        Save();
    }

    partial void OnReplayGainChanged(ReplayGainMode value)
    {
        Shell.Services.Settings.ReplayGain = value;
        Shell.Player.Engine.ReplayGain = value;
        Save();
    }

    partial void OnGaplessChanged(bool value)
    {
        Shell.Services.Settings.Gapless = value;
        Shell.Player.Engine.Gapless = value;
        Save();
    }

    partial void OnAutoplayChanged(bool value)
    {
        Shell.Services.Settings.Autoplay = value;
        Shell.Player.Autoplay = value;
        Save();
    }

    partial void OnAccentFromArtChanged(bool value)
    {
        Shell.Services.Settings.AccentFromArt = value;
        Shell.Services.Accent?.Update();
        Save();
    }

    partial void OnGeneratedBackdropChanged(bool value)
    {
        Shell.Services.Settings.GeneratedBackdrop = value;
        Shell.Services.RaiseAppearanceChanged();
        Save();
    }

    partial void OnResumeSessionChanged(bool value)
    {
        Shell.Services.Settings.ResumeSession = value;
        Save();
    }

    partial void OnWatchFoldersChanged(bool value)
    {
        Shell.Services.Settings.WatchFolders = value;
        if (value)
            Shell.Services.Library.StartWatching();
        else
            Shell.Services.Library.StopWatching();
        Save();
    }

    private void Save() => Shell.Services.SaveSettings();

    public void AddFolder(string path)
    {
        path = path.Trim();
        if (path.StartsWith('~'))
            path = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..]);
        if (path.Length == 0)
            return;
        if (!Directory.Exists(path))
        {
            Shell.Toasts.Show($"There is no folder at {path}");
            return;
        }
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Folders.Contains(path))
            return;
        Folders.Add(path);
        ApplyFolders();
        NewFolder = "";
        Shell.Toasts.Show($"Scanning {path}");
    }

    [RelayCommand]
    private void AddTypedFolder() => AddFolder(NewFolder);

    public void ImportPlaylists(IReadOnlyList<string> files) => Shell.ImportPlaylists(files);

    [RelayCommand]
    private void RemoveFolder(string folder)
    {
        Folders.Remove(folder);
        ApplyFolders();
    }

    [RelayCommand]
    private void Rescan()
    {
        Shell.Services.Library.Scan();
        Shell.Toasts.Show("Scanning your music folders");
    }

    [RelayCommand]
    private void ClearHistory()
    {
        Shell.Services.UserData.ClearHistory();
        Shell.Toasts.Show("Cleared listening history (play counts are kept)");
    }

    private void ApplyFolders()
    {
        Shell.Services.Settings.MusicFolders = [.. Folders];
        Save();
        Shell.Services.Library.SetFolders(Shell.Services.Settings.MusicFolders);
    }
}
