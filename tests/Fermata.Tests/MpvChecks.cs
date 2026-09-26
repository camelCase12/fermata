using System.Diagnostics;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.Tests;

/// <summary>
/// The player driving real libmpv with its null audio output (silent): gapless transitions, failed
/// files, the end of the queue, seeking and pausing. Confirms that mpv behaves as <see cref="FakeEngine"/> assumes.
/// </summary>
internal static class MpvChecks
{
    public static void Run(Checks check)
    {
        if (!Fixtures.FfmpegAvailable)
        {
            Console.WriteLine("  (ffmpeg not found; skipping libmpv checks)");
            return;
        }
        var context = new TestContext();
        using var engine = new MpvEngine(context, "Fermata tests", audioOutput: "null");
        if (!engine.IsAvailable)
        {
            Console.WriteLine($"  (libmpv unavailable: {engine.UnavailableReason}; skipping)");
            return;
        }

        string directory = Fixtures.Directory("mpv");
        var tracks = new List<Track>();
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(directory, $"tone{i}.flac");
            Fixtures.Ffmpeg($"-f lavfi -i sine=frequency={330 + 110 * i}:duration=1.2 -c:a flac \"{path}\"");
            tracks.Add(new Track { Path = path, Title = $"tone{i}", Duration = TimeSpan.FromSeconds(1.2) });
        }
        string broken = Path.Combine(directory, "broken.flac");
        File.WriteAllBytes(broken, [.. "not audio at all"u8, .. new byte[256]]);
        tracks.Insert(2, new Track { Path = broken, Title = "broken", Duration = TimeSpan.FromSeconds(1) });

        var player = new Player(engine, new PlayQueue(new Random(1)), new UserData());
        var started = new List<string>();
        var failures = new List<string>();
        player.TrackChanged += () => started.Add(player.CurrentTrack?.Title ?? "-");
        player.PlaybackFailed += failures.Add;

        var clock = Stopwatch.StartNew();
        player.Play(tracks, 0, new QueueSource("test", "test"));
        bool finished = context.RunUntil(() => player.State != PlaybackState.Playing, TimeSpan.FromSeconds(15));
        check.That(finished, "the queue plays to its end");
        check.Equal("tone0,tone1,broken,tone2,tone0", string.Join(",", started), "tracks start in order, the broken one is skipped, then the queue rewinds");
        check.Equal(1, failures.Count, "the broken file is reported once");
        check.Near(3.6, clock.Elapsed.TotalSeconds, 1.0, "three 1.2-second tracks take about 3.6 seconds");
        check.Equal(PlaybackState.Paused, player.State, "paused at the start of the queue afterwards");

        // Seeking, pausing and position.
        player.Play(tracks.Take(2).ToList(), 0, new QueueSource("test", "test"));
        // Wait for real progress: the duration and position of the previous queue can still be showing.
        context.RunUntil(() => player.State == PlaybackState.Playing && engine.Position.TotalSeconds > 0.1, TimeSpan.FromSeconds(5));
        check.Near(1.2, player.Duration.TotalSeconds, 0.05, "duration measured by mpv");
        player.Pause();
        context.RunUntil(() => false, TimeSpan.FromMilliseconds(150));
        var paused = player.Position;
        context.RunUntil(() => false, TimeSpan.FromMilliseconds(300));
        check.Near(paused.TotalSeconds, player.Position.TotalSeconds, 0.02, "position holds while paused");
        player.Seek(TimeSpan.FromSeconds(0.9));
        context.RunUntil(() => Math.Abs(player.Position.TotalSeconds - 0.9) < 0.05, TimeSpan.FromSeconds(2));
        // mpv 0.37 and older (client API before 2.3) can end a seek made while paused short of its target
        // when playing through the null audio output these checks use.
        if (LibMpv.ClientApiVersion() >= (2u << 16 | 3u))
            check.Near(0.9, player.Position.TotalSeconds, 0.05, $"seek while paused (from {paused.TotalSeconds:0.###} s)");
        else
            Console.WriteLine("  (mpv before 0.38; skipping the paused seek position check)");
        player.Resume();
        bool advanced = context.RunUntil(() => player.CurrentTrack?.Title == "tone1", TimeSpan.FromSeconds(3));
        check.That(advanced, "resumes and advances after the seek point");

        // Rapid commands must not confuse the event stream.
        for (int i = 0; i < 20; i++)
        {
            player.Next();
            player.Previous();
            player.SetShuffle(i % 2 == 0);
        }
        context.RunUntil(() => false, TimeSpan.FromMilliseconds(400));
        check.That(player.State == PlaybackState.Playing && player.CurrentTrack is not null, "rapid commands leave a consistent playing state");
        player.Stop();
        context.RunUntil(() => false, TimeSpan.FromMilliseconds(100));
        check.Equal(PlaybackState.Stopped, player.State, "stop");
    }
}
