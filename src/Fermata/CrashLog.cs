using System.Runtime.InteropServices;

namespace Fermata;

/// <summary>
/// Records unhandled exceptions in <c>crash.log</c> in Fermata's state folder, so that a crash in a window
/// started from a launcher still leaves details for a bug report. Newer entries are appended, and the
/// file is cut back to its newest half when it grows past 256 KiB.
/// </summary>
internal static class CrashLog
{
    private const int Limit = 256 * 1024;
    private static string? file;

    public static void Install(string path)
    {
        file = path;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (Write(e.ExceptionObject as Exception, "Crash") is { } written)
                Console.Error.WriteLine($"fermata: crashed; details are in {written}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => Write(e.Exception, "Unobserved background error");
    }

    /// <summary>Appends an entry and returns the log's path, or null when it could not be written.</summary>
    private static string? Write(Exception? error, string kind)
    {
        if (file is null)
            return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string version = typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "unknown";
            string entry = $"""
                === {kind} at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
                Fermata {version}, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}
                {error?.ToString() ?? "(no exception details)"}


                """;
            File.AppendAllText(file, entry);
            var info = new FileInfo(file);
            if (info.Length > Limit)
            {
                byte[] bytes = File.ReadAllBytes(file);
                File.WriteAllBytes(file, bytes[(bytes.Length - Limit / 2)..]);
            }
            return file;
        }
        catch
        {
            // A crash handler must not throw. Without a log the crash still ends the process as before.
            return null;
        }
    }
}
