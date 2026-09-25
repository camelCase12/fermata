using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Fermata.Integration;
using Fermata.Library;
using Fermata.Playback;
using Fermata.Services;
using Fermata.Storage;
using Fermata.ViewModels;
using Fermata.Views;

namespace Fermata;

public partial class App : Application
{
    /// <summary>Files named on the command line of the first launch.</summary>
    internal static IReadOnlyList<string> StartupRequests { get; set; } = [];

    internal static SingleInstance? Instance { get; set; }

    /// <summary>Application services, for controls that load art or act on tracks.</summary>
    internal static AppServices? Services { get; set; }

    internal static Shell? Shell { get; set; }

    private MprisBridge? mpris;
    private readonly List<PosixSignalRegistration> signals = [];

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Program.Trace("framework ready");
            var context = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
            var services = new AppServices(context, AppPaths.FromEnvironment());
            Services = services;
            services.Accent = new DynamicAccent(services, this);
            var shell = new Shell(services);
            Shell = shell;
            var model = new MainViewModel(shell);
            var window = new MainWindow { DataContext = model };
            desktop.MainWindow = window;
            window.Opened += (_, _) => Program.Trace("window opened");
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            shell.GoHome();

            mpris = new MprisBridge(services, context, () => window.Raise(), () => window.Close());
            _ = mpris.StartAsync();
            if (Instance is { } instance)
                instance.MessageReceived += lines => Dispatcher.UIThread.Post(() => HandleRequests(lines, window));
            HandleTerminationSignals(desktop);
            // Every shutdown ends here. Closing the main window skips ShutdownRequested, so saves are flushed here.
            desktop.Exit += (_, _) =>
            {
                services.Shutdown();
                foreach (var registration in signals)
                    registration.Dispose();
                mpris?.Dispose();
                services.Dispose();
            };

            _ = StartAsync(services);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// SIGTERM (logging out, kill) and SIGINT (Ctrl+C in a terminal) close Fermata the normal way, so the
    /// queue, position and window size are saved. A second signal exits at once.
    /// </summary>
    private void HandleTerminationSignals(IClassicDesktopStyleApplicationLifetime desktop)
    {
        int received = 0;
        foreach (var signal in (PosixSignal[])[PosixSignal.SIGTERM, PosixSignal.SIGINT])
        {
            signals.Add(PosixSignalRegistration.Create(signal, context =>
            {
                if (Interlocked.Increment(ref received) > 1)
                    return;
                context.Cancel = true;
                Dispatcher.UIThread.Post(() => desktop.Shutdown());
            }));
        }
    }

    private static async Task StartAsync(AppServices services)
    {
        services.Library.SnapshotChanged += snapshot => Program.Trace($"library published ({snapshot.Tracks.Count} tracks)");
        services.Library.ScanCompleted += result => Program.Trace($"scan finished in {result.Elapsed.TotalMilliseconds:0} ms (+{result.Added} ~{result.Updated} -{result.Removed})");
        await services.StartLibraryAsync();
        if (StartupRequests.Count > 0)
            HandleRequests(StartupRequests, null);
    }

    /// <summary>Requests from another launch: files to play, commands, or just raising the window.</summary>
    private static void HandleRequests(IReadOnlyList<string> lines, MainWindow? window)
    {
        if (Services is not { } services || Shell is not { } shell)
            return;
        var files = new List<string>();
        foreach (string line in lines)
        {
            int tab = line.IndexOf('\t');
            string verb = tab < 0 ? line : line[..tab];
            string argument = tab < 0 ? "" : line[(tab + 1)..];
            switch (verb)
            {
                case "open":
                    files.Add(argument);
                    break;
                case "command":
                    switch (argument)
                    {
                        case "play-pause": services.Player.PlayPause(); break;
                        case "next": services.Player.Next(); break;
                        case "previous": services.Player.Previous(); break;
                        case "stop": services.Player.Pause(); break;
                    }
                    break;
                case "raise":
                    window?.Raise();
                    break;
            }
        }
        if (files.Count > 0)
        {
            OpenFiles(services, shell, files);
            window?.Raise();
        }
    }

    /// <summary>Plays files and folders given from outside, reading any that are not in the library.</summary>
    internal static void OpenFiles(AppServices services, Shell shell, IReadOnlyList<string> paths)
    {
        var library = services.Library.Snapshot;
        var tracks = new List<Track>();
        var pool = new StringPool();
        foreach (string path in paths)
        {
            IEnumerable<string> files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Where(f => Metadata.TagReader.AudioExtensions.Contains(Path.GetExtension(f))).Order(StringComparer.Ordinal)
                : [path];
            foreach (string file in files)
            {
                if (library.FindTrack(file) is { } known)
                {
                    tracks.Add(known);
                    continue;
                }
                try
                {
                    var info = new FileInfo(file);
                    var tags = Metadata.TagReader.Read(file);
                    tracks.Add(Track.FromTags(file, tags, info.Length, info.LastWriteTimeUtc, DateTime.UtcNow, pool));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    shell.Toasts.Show($"Couldn't open {Path.GetFileName(file)}");
                }
            }
        }
        if (tracks.Count > 0)
            shell.Play(tracks, 0, new QueueSource("files", SourceTitle(paths, tracks)));
    }

    /// <summary>What opened files play from: their album when they share one, else the folder or the number of files.</summary>
    private static string SourceTitle(IReadOnlyList<string> paths, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 1)
            return tracks[0].Title;
        if (tracks[0].AlbumKey is { } album && tracks.All(t => t.AlbumKey == album))
            return tracks[0].AlbumTitle;
        if (paths is [var folder] && Directory.Exists(folder))
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        return $"{tracks.Count} files";
    }
}
