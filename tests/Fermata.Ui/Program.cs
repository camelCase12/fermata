using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fermata;
using Fermata.Library;
using Fermata.Playback;
using Fermata.Services;
using Fermata.Storage;
using Fermata.ViewModels;
using Fermata.Views;

// Renders Fermata's pages without a display, against a given music folder, using private XDG
// directories so the user's own settings and library are never touched.
//
//   dotnet run --project tests/Fermata.Ui -- --screenshots OUTPUT_DIR MUSIC_DIR
//
// It can also render the ambient backdrop, in both styles, for each of a list of cover images:
//
//   dotnet run --project tests/Fermata.Ui -- --backdrops OUTPUT_DIR IMAGE...
string output, music = "";
string[] covers = [];
if (args is ["--screenshots", var screenshotOutput, var musicFolder])
    (output, music) = (screenshotOutput, musicFolder);
else if (args is ["--backdrops", var backdropOutput, .. var images] && images.Length > 0)
    (output, covers) = (backdropOutput, images);
else
{
    Console.Error.WriteLine("usage: --screenshots OUTPUT_DIR MUSIC_DIR\n       --backdrops OUTPUT_DIR IMAGE...");
    return 2;
}
Directory.CreateDirectory(output);
string home = Path.Combine(Path.GetTempPath(), "fermata-ui-" + Environment.ProcessId);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(home, "config"));
Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(home, "data"));
Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(home, "cache"));
Environment.SetEnvironmentVariable("XDG_STATE_HOME", Path.Combine(home, "state"));
Environment.SetEnvironmentVariable("FERMATA_AUDIO_OUTPUT", "null");
var paths = AppPaths.FromEnvironment();
Directory.CreateDirectory(paths.Config);
FermataJson.Save(paths.SettingsFile, new Settings { MusicFolders = covers.Length > 0 ? [] : [Path.GetFullPath(music)], ResumeSession = false }, FermataJson.Default.Settings);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHarfBuzz()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

var context = SynchronizationContext.Current!;
var services = new AppServices(context, paths);
App.Services = services;
if (covers.Length > 0)
    return Backdrops.Render(services, output, covers);
var shell = new Shell(services);
App.Shell = shell;
var model = new MainViewModel(shell);
var window = new MainWindow { DataContext = model, Width = 1440, Height = 900 };
window.Show();
shell.GoHome();

void Pump(int milliseconds)
{
    var clock = Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < milliseconds)
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.SystemIdle);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(10);
    }
}

bool scanned = false;
services.Library.ScanCompleted += _ => scanned = true;
_ = services.StartLibraryAsync();
var wait = Stopwatch.StartNew();
while (!scanned && wait.Elapsed < TimeSpan.FromSeconds(60))
    Pump(50);
Console.WriteLine($"library: {services.Library.Snapshot.Tracks.Count} tracks, {services.Library.Snapshot.Albums.Count} albums");

// Some listening history, so home has personal sections.
var library = services.Library.Snapshot;
var random = new Random(7);
foreach (var track in library.Tracks.OrderBy(_ => random.Next()).Take(40))
{
    for (int i = random.Next(1, 6); i > 0; i--)
        services.UserData.RecordPlay(track, DateTime.UtcNow.AddHours(-random.Next(1, 24 * 40)));
    if (random.Next(4) == 0)
        services.UserData.SetLiked(track, true);
}
services.Playlists.Create("Late night drive", library.Tracks.OrderBy(_ => random.Next()).Take(14).Select(t => t.Path));
services.Playlists.Create("Focus", library.Tracks.Where(t => t.Genres.Contains("Ambient") || t.Genres.Contains("Classical")).Select(t => t.Path));

// Screen readers: every visible button needs a name, and the seek bars are named sliders.
int namedButtons = 0, unnamedControls = 0;
void CheckNames(string page)
{
    foreach (var control in window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
    {
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(control);
        if (!string.IsNullOrWhiteSpace(peer.GetName()))
        {
            namedButtons++;
            continue;
        }
        unnamedControls++;
        Console.Error.WriteLine($"{page}: button without an accessible name: {control.Name ?? control.GetType().Name} in {control.FindAncestorOfType<UserControl>()?.GetType().Name}");
    }
    foreach (var bar in window.GetVisualDescendants().OfType<Fermata.Controls.SeekBar>())
    {
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(bar);
        if (peer.GetAutomationControlType() != Avalonia.Automation.Peers.AutomationControlType.Slider
            || string.IsNullOrEmpty(peer.GetName()) || peer is not Avalonia.Automation.Provider.IRangeValueProvider)
        {
            unnamedControls++;
            Console.Error.WriteLine($"{page}: seek bar {bar.Name} is not a named slider");
        }
    }
}

void Capture(string name)
{
    Pump(700);
    CheckNames(name);
    var frame = window.CaptureRenderedFrame();
    if (frame is null)
    {
        Console.Error.WriteLine($"{name}: no frame");
        return;
    }
    string file = Path.Combine(output, name + ".png");
    frame.Save(file, PngBitmapEncoderOptions.Default);
    Console.WriteLine("saved " + file);
}

shell.Navigator.Current?.LibraryChanged();
var album = library.Albums.First(a => a.Title == "Tidal Hours");
shell.Play(album.Tracks, 2, new QueueSource("album", album.Title, album.Key));
Pump(300);

Capture("01-home");
shell.GoSongs();
Capture("02-songs");
shell.GoAlbums();
Capture("03-albums");
shell.GoArtists();
Capture("04-artists");
shell.OpenAlbum(album);
Capture("05-album");
shell.OpenArtist(library.FindArtist("Aurora Vale")!);
Capture("06-artist");
shell.GoExplore();
Capture("07-explore");
shell.OpenPlaylist(services.Playlists.Playlists[0]);
Capture("08-playlist");
shell.GoLiked();
Capture("09-liked");
// Headless dispatcher timers are not real-time, so the search is submitted rather than debounced.
model.SearchText = "aurora";
model.SubmitSearch();
Capture("10-search");
shell.GoHistory();
Capture("11-history");
shell.GoSettings();
Capture("12-settings");
shell.IsNowPlayingOpen = true;
Capture("13-now-playing");
model.NowPlaying.Tab = NowPlayingTab.Lyrics;
Capture("14-lyrics");
model.NowPlaying.Tab = NowPlayingTab.Related;
Capture("15-related");
shell.IsNowPlayingOpen = false;
_ = shell.CreatePlaylistAsync(album.Tracks);
Capture("16-dialog");
(shell.Dialog as PromptDialog)?.CancelCommand.Execute(null);

// Keyboard: a focused row shows the focus ring, and Page Down scrolls the page from it.
shell.GoSongs();
Pump(300);
var songList = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "List");
songList.ContainerFromIndex(2)?.Focus(NavigationMethod.Tab);
Capture("17-focus");
window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
Capture("18-page-down");

if (Fermata.Controls.AmbientBackdrop.ShaderErrors is { } errors)
{
    Console.Error.WriteLine("ambient backdrop shader failed to compile:\n" + errors);
    return 1;
}

Console.WriteLine($"accessible names: {namedButtons} buttons named, {unnamedControls} problems");
if (unnamedControls > 0)
    return 1;

services.Shutdown();
services.Dispose();
try { Directory.Delete(home, recursive: true); } catch (IOException) { }
return 0;
