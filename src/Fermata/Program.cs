using Avalonia;
using Fermata.Integration;
using Fermata.Storage;

namespace Fermata;

internal static class Program
{
    /// <summary>Process start, for FERMATA_TRACE_STARTUP timings.</summary>
    internal static readonly long Started = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>With FERMATA_TRACE_STARTUP set, prints how long after process start a milestone was reached.</summary>
    internal static void Trace(string milestone)
    {
        if (Environment.GetEnvironmentVariable("FERMATA_TRACE_STARTUP") is not { Length: > 0 })
            return;
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(Started);
        var gc = GC.GetGCMemoryInfo();
        Console.Error.WriteLine($"fermata: {elapsed.TotalMilliseconds,7:0.0} ms  {milestone}  "
            + $"[managed {GC.GetTotalMemory(false) / 1048576.0:0.0} MB, heap {gc.HeapSizeBytes / 1048576.0:0.0} MB, committed {gc.TotalCommittedBytes / 1048576.0:0.0} MB, gen0 GCs {GC.CollectionCount(0)}]");
    }

    private const string Usage = """
        fermata [FILE|FOLDER…]

        Plays music from your library. Files and folders given on the command line are played
        in the running Fermata window (one is started if needed).

          --play-pause   Toggle playback in the running instance
          --next         Skip to the next track
          --previous     Go back to the previous track
          --stop         Stop playback
          --help         Show this help

        Media keys work through MPRIS (playerctl).
        """;

    [STAThread]
    public static int Main(string[] args)
    {
        var lines = new List<string>();
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--help" or "-h":
                    Console.WriteLine(Usage);
                    return 0;
                case "--play-pause" or "--next" or "--previous" or "--stop":
                    lines.Add("command\t" + arg[2..]);
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"fermata: unknown option {arg}\n\n{Usage}");
                        return 2;
                    }
                    lines.Add("open\t" + Path.GetFullPath(arg));
                    break;
            }
        }
        if (lines.Count == 0)
            lines.Add("raise");

        var paths = AppPaths.FromEnvironment();
        SingleInstance? instance;
        try
        {
            instance = SingleInstance.Acquire(paths.InstanceSocket, lines);
        }
        catch (IOException error)
        {
            Console.Error.WriteLine("fermata: " + error.Message);
            instance = null;
            return 1;
        }
        if (instance is null)
            return 0; // the running instance took the request

        // Commands for a running instance mean nothing when this is the first one.
        App.StartupRequests = lines.Where(l => l.StartsWith("open\t", StringComparison.Ordinal)).ToList();
        App.Instance = instance;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            instance.Dispose();
        }
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        var x11 = new X11PlatformOptions { WmClass = "fermata" };
        // OpenGL unless FERMATA_RENDERING says otherwise (vulkan, gl or software). Measured while playing:
        // on NVIDIA, Vulkan took 40% more memory and more CPU than GL; the software renderer takes ~4× the CPU.
        x11.RenderingMode = Environment.GetEnvironmentVariable("FERMATA_RENDERING") switch
        {
            "software" => [X11RenderingMode.Software],
            "vulkan" => [X11RenderingMode.Vulkan, X11RenderingMode.Glx, X11RenderingMode.Software],
            _ => [X11RenderingMode.Glx, X11RenderingMode.Software],
        };
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(x11);
    }
}
