using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.ViewModels.Pages;

public sealed class HomeSectionViewModel(HomeSection section)
{
    public string Title => section.Title;
    public string? Subtitle => section.Subtitle;
    public HomeSectionKind Kind => section.Kind;
    public IReadOnlyList<object> Items => section.Items;
    public bool IsTracks => Kind == HomeSectionKind.Tracks;
    public bool IsCards => Kind != HomeSectionKind.Tracks;
}

/// <summary>The Home page.</summary>
public sealed partial class HomeViewModel(Shell shell) : PageViewModel(shell)
{
    private int build;

    public override Section Section => Section.Home;

    [ObservableProperty] public partial IReadOnlyList<HomeSectionViewModel> Sections { get; private set; } = [];
    [ObservableProperty] public partial bool IsLibraryEmpty { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;
    [ObservableProperty] public partial string Greeting { get; private set; } = "";

    /// <summary>What the first-run card says about the music folders.</summary>
    public string FirstRunText
    {
        get
        {
            const string Advice = "Add the folders where you keep music, and Fermata will read them and keep watching for changes.";
            var folders = Shell.Services.Settings.MusicFolders;
            if (folders.Count == 0)
                return $"Fermata plays the music on this computer. No music folders are set yet. {Advice}";
            var missing = folders.Where(f => !Directory.Exists(f)).ToList();
            if (missing.Count == folders.Count)
                return $"Fermata plays the music on this computer. It is set to look in {string.Join(", ", folders)}, which "
                    + (folders.Count == 1 ? "doesn't exist. " : "don't exist. ") + Advice;
            if (Shell.Services.Library.IsScanning)
                return $"Fermata plays the music on this computer. It is looking in {string.Join(", ", folders)}.";
            return $"Fermata plays the music on this computer. It found no music in {string.Join(", ", folders)}. {Advice}";
        }
    }

    protected override async void Refresh()
    {
        int request = ++build;
        var library = Shell.Library;
        var userData = Shell.Services.UserData;
        Greeting = DateTime.Now.Hour switch { < 5 => "Good night", < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        IsLibraryEmpty = library.Tracks.Count == 0;
        OnPropertyChanged(nameof(FirstRunText));
        if (IsLibraryEmpty)
        {
            Sections = [];
            IsLoading = Shell.Services.Library.IsScanning;
            return;
        }
        // The user data belongs to the UI thread, so the feed is built from a copy.
        var copy = UserData.FromDocument(userData.ToDocument());
        var sections = await Task.Run(() => HomeFeed.Build(library, copy));
        if (request != build)
            return;
        Sections = sections.Select(s => new HomeSectionViewModel(s)).ToList();
        IsLoading = false;
    }

    protected override void OnActivated()
    {
        if (DateTime.UtcNow - lastRefresh > TimeSpan.FromMinutes(10))
        {
            lastRefresh = DateTime.UtcNow;
            Refresh();
        }
    }

    private DateTime lastRefresh = DateTime.UtcNow;

    public void PlayQuickPick(HomeSectionViewModel section, Track track)
    {
        var tracks = section.Items.OfType<Track>().ToList();
        Shell.Play(tracks, tracks.IndexOf(track), new QueueSource("home", section.Title));
    }

    [RelayCommand]
    private void ShuffleLibrary() => Shell.Shuffle(Shell.Library.Tracks, new QueueSource("songs", "All songs"));

    [RelayCommand]
    private void OpenSettings() => Shell.GoSettings();
}
