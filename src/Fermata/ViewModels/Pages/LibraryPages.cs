using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.Playback;
using Fermata.Text;

namespace Fermata.ViewModels.Pages;

public enum SongSort
{
    Library,
    Title,
    Artist,
    Album,
    Year,
    Duration,
    Added,
    Plays,
}

public enum AlbumSort
{
    Artist,
    Title,
    Year,
    Added,
    Plays,
}

/// <summary>An entry of a sort menu.</summary>
public sealed record SortChoice(Enum Value, string Label);

/// <summary>A page with a filter box.</summary>
public abstract partial class FilteredPageViewModel : PageViewModel
{
    private readonly DispatcherTimer delay;

    protected FilteredPageViewModel(Shell shell) : base(shell)
    {
        delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            Refresh();
        };
    }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value)
    {
        delay.Stop();
        delay.Start();
    }

    /// <summary>Gets the current library's search index.</summary>
    protected async Task<SearchIndex> IndexAsync() => await Shell.Services.Library.GetSearchIndexAsync();
}

/// <summary>All songs, sortable and filterable.</summary>
public sealed partial class SongsViewModel : FilteredPageViewModel
{
    private int build;

    public SongsViewModel(Shell shell) : base(shell)
    {
        List = new TrackListModel(shell, new QueueSource("songs", "All songs"));
        if (Enum.TryParse(shell.Services.Settings.SortOrders.GetValueOrDefault("songs"), out SongSort saved))
            Sort = saved;
    }

    public override Section Section => Section.Songs;
    public TrackListModel List { get; }

    public IReadOnlyList<SortChoice> SortChoices { get; } =
    [
        new(SongSort.Library, "Artist & album"),
        new(SongSort.Title, "Title"),
        new(SongSort.Artist, "Artist"),
        new(SongSort.Album, "Album"),
        new(SongSort.Year, "Year"),
        new(SongSort.Duration, "Length"),
        new(SongSort.Added, "Recently added"),
        new(SongSort.Plays, "Most played"),
    ];

    [ObservableProperty] public partial SongSort Sort { get; set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial bool HasSongs { get; private set; } = true;
    [ObservableProperty] public partial bool IsLibraryEmpty { get; private set; }

    [RelayCommand]
    private void OpenSettings() => Shell.GoSettings();

    public SortChoice SelectedSort
    {
        get => SortChoices.First(c => (SongSort)c.Value == Sort);
        set
        {
            if (value is null || (SongSort)value.Value == Sort)
                return;
            Sort = (SongSort)value.Value;
            Shell.Services.Settings.SortOrders["songs"] = value.Value.ToString();
            Shell.Services.SaveSettings();
            Refresh();
        }
    }

    protected override async void Refresh()
    {
        int request = ++build;
        var library = Shell.Library;
        var sort = Sort;
        string filter = Filter;
        var userData = Shell.Services.UserData;
        // Play counts are read on the UI thread, which owns the user data, before sorting in the background.
        Dictionary<string, int>? plays = sort == SongSort.Plays ? userData.Tracks.ToDictionary(p => p.Key, p => p.Value.Plays) : null;
        var index = filter.Length > 0 ? await IndexAsync() : null;
        var tracks = await Task.Run(() => SortTracks(index is null ? [.. library.Tracks] : index.FilterTracks(filter), sort, plays));
        if (request != build)
            return;
        List.Tracks = tracks;
        List.Source = new QueueSource("songs", filter.Length > 0 ? $"Songs matching “{filter}”" : "All songs");
        var total = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks));
        Summary = tracks.Count == 0 ? "" : $"{Formats.Count(tracks.Count, "song")} · {Formats.LongDuration(total)}";
        HasSongs = tracks.Count > 0;
        IsLibraryEmpty = library.Tracks.Count == 0;
    }

    internal static List<Track> SortTracks(List<Track> tracks, SongSort sort, IReadOnlyDictionary<string, int>? plays)
    {
        switch (sort)
        {
            case SongSort.Title:
                return OrderByKey(tracks, t => TextFolding.SortKey(t.TitleSort ?? t.Title));
            case SongSort.Artist:
                return OrderByKey(tracks, t => TextFolding.SortKey(t.ArtistSort ?? t.DisplayArtist));
            case SongSort.Album:
                return OrderByKey(tracks, t => TextFolding.SortKey(t.AlbumSort ?? t.AlbumTitle));
            case SongSort.Year:
                return [.. tracks.OrderByDescending(t => t.Year)];
            case SongSort.Duration:
                return [.. tracks.OrderByDescending(t => t.Duration)];
            case SongSort.Added:
                return [.. tracks.OrderByDescending(t => t.Added)];
            case SongSort.Plays:
                return [.. tracks.OrderByDescending(t => plays?.GetValueOrDefault(t.Path) ?? 0)];
            default:
                return tracks; // library order: by album artist, year and album
        }
    }

    /// <summary>Sorts tracks by a string key, keeping library order for ties.</summary>
    private static List<Track> OrderByKey(List<Track> tracks, Func<Track, string> key)
    {
        var keys = new string[tracks.Count];
        var order = new int[tracks.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = key(tracks[i]);
            order[i] = i;
        }
        Array.Sort(order, (a, b) => string.CompareOrdinal(keys[a], keys[b]) is var c and not 0 ? c : a.CompareTo(b));
        return order.Select(i => tracks[i]).ToList();
    }

    [RelayCommand]
    private void PlayAll() => List.PlayAll();

    [RelayCommand]
    private void ShuffleAll() => List.ShuffleAll();
}

/// <summary>All albums as a grid.</summary>
public sealed partial class AlbumsViewModel : FilteredPageViewModel
{
    private int build;

    public AlbumsViewModel(Shell shell) : base(shell)
    {
        if (Enum.TryParse(shell.Services.Settings.SortOrders.GetValueOrDefault("albums"), out AlbumSort saved))
            Sort = saved;
    }

    public override Section Section => Section.Albums;

    public IReadOnlyList<SortChoice> SortChoices { get; } =
    [
        new(AlbumSort.Artist, "Artist"),
        new(AlbumSort.Title, "Title"),
        new(AlbumSort.Year, "Year"),
        new(AlbumSort.Added, "Recently added"),
        new(AlbumSort.Plays, "Most played"),
    ];

    [ObservableProperty] public partial AlbumSort Sort { get; set; }
    [ObservableProperty] public partial IReadOnlyList<object> Albums { get; private set; } = [];
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    public SortChoice SelectedSort
    {
        get => SortChoices.First(c => (AlbumSort)c.Value == Sort);
        set
        {
            if (value is null || (AlbumSort)value.Value == Sort)
                return;
            Sort = (AlbumSort)value.Value;
            Shell.Services.Settings.SortOrders["albums"] = value.Value.ToString();
            Shell.Services.SaveSettings();
            Refresh();
        }
    }

    protected override async void Refresh()
    {
        int request = ++build;
        var library = Shell.Library;
        string filter = Filter;
        var index = filter.Length > 0 ? await IndexAsync() : null;
        List<Album> albums = index is null ? [.. library.Albums] : index.FilterAlbums(filter);
        var userData = Shell.Services.UserData;
        albums = Sort switch
        {
            AlbumSort.Title => [.. albums.OrderBy(a => a.SortTitle, StringComparer.Ordinal)],
            AlbumSort.Year => [.. albums.OrderByDescending(a => a.Year)],
            AlbumSort.Added => [.. albums.OrderByDescending(a => a.Added)],
            AlbumSort.Plays => [.. albums.OrderByDescending(a => a.Tracks.Sum(t => userData.PlayCount(t.Path)))],
            _ => albums,
        };
        if (request != build)
            return;
        Albums = albums;
        Summary = Formats.Count(albums.Count, "album");
    }
}

/// <summary>All artists as a grid.</summary>
public sealed partial class ArtistsViewModel(Shell shell) : FilteredPageViewModel(shell)
{
    private int build;

    public override Section Section => Section.Artists;

    [ObservableProperty] public partial IReadOnlyList<object> Artists { get; private set; } = [];
    [ObservableProperty] public partial string Summary { get; private set; } = "";

    /// <summary>Whether performers who only appear as guests are listed too.</summary>
    [ObservableProperty]
    public partial bool IncludeGuests { get; set; }

    partial void OnIncludeGuestsChanged(bool value) => Refresh();

    protected override async void Refresh()
    {
        int request = ++build;
        var library = Shell.Library;
        string filter = Filter;
        var index = filter.Length > 0 ? await IndexAsync() : null;
        List<Artist> artists = index is null ? [.. library.Artists] : index.FilterArtists(filter);
        if (!IncludeGuests)
            artists = artists.Where(a => a.Albums.Count > 0 || a.Tracks.Any(t => t.AlbumKey is null)).ToList();
        if (request != build)
            return;
        Artists = artists;
        Summary = Formats.Count(artists.Count, "artist");
    }
}
