using System.Diagnostics;
using Fermata.Integration;

namespace Fermata.Tests;

/// <summary>
/// Drives the MPRIS server with playerctl — the tool behind the desktop's media keys — on a private
/// session bus, so the check never touches the real desktop session.
/// </summary>
internal static class MprisChecks
{
    public static void Run(Checks check)
    {
        if (!File.Exists("/usr/bin/dbus-daemon") || !File.Exists("/usr/bin/playerctl"))
        {
            Console.WriteLine("  (dbus-daemon or playerctl missing; skipping)");
            return;
        }
        using var bus = PrivateBus.Start();
        string? original = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", bus.Address);
        try
        {
            Exercise(check, bus.Address);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", original);
        }
    }

    private static void Exercise(Checks check, string address)
    {
        var context = new TestContext();
        var target = new RecordingTarget();
        using var server = new MprisServer(target, context);
        bool started = server.StartAsync().GetAwaiter().GetResult();
        check.That(started, "the server claims its bus name");
        check.Equal("org.mpris.MediaPlayer2.fermata", server.BusName, "bus name");

        server.Update(new MprisState
        {
            PlaybackStatus = "Playing",
            LoopStatus = "Playlist",
            Shuffle = true,
            Volume = 0.8,
            CanGoNext = true,
            CanGoPrevious = true,
            CanPlay = true,
            CanSeek = true,
            Position = TimeSpan.FromSeconds(42),
            Metadata = new MprisMetadata("/org/fermata/entry/7", "Low Tide", ["Aurora Vale", "Sable & Finch"], "Tidal Hours",
                ["Aurora Vale"], ["Dream Pop"], TimeSpan.FromSeconds(215), 1, 1, "file:///music/low%20tide.flac",
                "file:///tmp/cover.jpg", 3, 1.0),
        });

        string Ctl(params string[] args) => PlayerCtl(address, args);
        check.Equal("Playing", Ctl("status"), "status");
        check.Equal("Low Tide|Aurora Vale, Sable & Finch|Tidal Hours|215000000|file:///tmp/cover.jpg",
            Ctl("metadata", "--format", "{{title}}|{{artist}}|{{album}}|{{mpris:length}}|{{mpris:artUrl}}"), "metadata");
        check.Equal("0.800000", Ctl("volume"), "volume");
        check.Equal("On", Ctl("shuffle"), "shuffle");
        check.Equal("Playlist", Ctl("loop"), "loop status");
        double position = double.Parse(Ctl("position"), System.Globalization.CultureInfo.InvariantCulture);
        check.Near(42, position, 1.0, "position is extrapolated while playing");

        // Commands arrive on the UI context.
        Ctl("next");
        Ctl("previous");
        Ctl("play-pause");
        Ctl("pause");
        Ctl("play");
        Ctl("stop");
        Ctl("volume", "0.25");
        Ctl("shuffle", "Off");
        Ctl("loop", "Track");
        Ctl("position", "30");
        Ctl("position", "5+");
        context.RunUntil(() => target.Calls.Count >= 11, TimeSpan.FromSeconds(3));
        check.Equal("Next,Previous,PlayPause,Pause,Play,Stop,Volume 0.25,Shuffle False,Loop Track,SetPosition /org/fermata/entry/7 30,SeekBy 5",
            string.Join(",", target.Calls), "commands reach the player in order");

        // Changes are published.
        server.Update(new MprisState { PlaybackStatus = "Paused", Metadata = null, CanPlay = true });
        check.Equal("Paused", Ctl("status"), "status change is visible");
        check.Equal("", Ctl("metadata", "--format", "{{title}}"), "no track after the queue is cleared");
    }

    private static string PlayerCtl(string address, string[] args)
    {
        var start = new ProcessStartInfo("playerctl") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--player=fermata");
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = address;
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output.Trim();
    }

    private sealed class RecordingTarget : IMprisTarget
    {
        public List<string> Calls { get; } = [];
        public void Raise() => Calls.Add("Raise");
        public void Quit() => Calls.Add("Quit");
        public void Play() => Calls.Add("Play");
        public void Pause() => Calls.Add("Pause");
        public void PlayPause() => Calls.Add("PlayPause");
        public void Stop() => Calls.Add("Stop");
        public void Next() => Calls.Add("Next");
        public void Previous() => Calls.Add("Previous");
        public void SeekBy(TimeSpan offset) => Calls.Add($"SeekBy {offset.TotalSeconds}");
        public void SetPosition(string trackId, TimeSpan position) => Calls.Add($"SetPosition {trackId} {position.TotalSeconds}");
        public void SetShuffle(bool shuffle) => Calls.Add($"Shuffle {shuffle}");
        public void SetLoopStatus(string status) => Calls.Add($"Loop {status}");
        public void SetVolume(double volume) => Calls.Add($"Volume {volume}");
        public void OpenUri(string uri) => Calls.Add($"Open {uri}");
    }
}

/// <summary>A throwaway session bus for tests.</summary>
internal sealed class PrivateBus : IDisposable
{
    private readonly Process process;

    private PrivateBus(Process process, string address)
    {
        this.process = process;
        Address = address;
    }

    public string Address { get; }

    public static PrivateBus Start()
    {
        var start = new ProcessStartInfo("dbus-daemon", "--session --nofork --print-address=1")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(start)!;
        string address = process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("dbus-daemon printed no address");
        return new PrivateBus(process, address.Trim());
    }

    public void Dispose()
    {
        try
        {
            process.Kill();
            process.WaitForExit(2000);
        }
        catch (InvalidOperationException)
        {
        }
        process.Dispose();
    }
}
