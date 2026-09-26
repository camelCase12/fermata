using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Fermata.Playback;

/// <summary>The audio engine that plays through libmpv.</summary>
/// <remarks>
/// Video, scripts, user configuration and input handling are disabled. mpv reports events through a
/// wakeup callback on one of its own threads, and the callback posts a drain to the owner's
/// <see cref="SynchronizationContext"/>, so state changes and <see cref="EventRaised"/> handlers run on
/// the owner's thread.
/// <para>
/// mpv's internal playlist holds at most two items, the current one and the preloaded next one. Each item
/// is identified by mpv's playlist entry id.
/// </para>
/// </remarks>
public sealed unsafe class MpvEngine : IAudioEngine
{
    private const ulong PauseObserver = 1, DurationObserver = 2, DevicesObserver = 3, IdleObserver = 4;

    private readonly SynchronizationContext context;
    private readonly GCHandle self;
    private readonly bool hasLoadfileIndex;
    private nint handle;
    private int drainPending;
    private long currentId = -1;
    private IReadOnlyList<AudioDevice> devices = [];

    /// <param name="context">The owner's thread, where events are raised.</param>
    /// <param name="clientName">Application name shown by PipeWire/PulseAudio.</param>
    /// <param name="audioOutput">The mpv audio output driver. "null" plays silently.</param>
    public MpvEngine(SynchronizationContext context, string clientName = "Fermata", string? audioOutput = null)
    {
        this.context = context;
        try
        {
            uint version = LibMpv.ClientApiVersion();
            // mpv 0.38 (client API 2.3) added the index argument to loadfile, shifting the options argument.
            hasLoadfileIndex = version >= (2u << 16 | 3u);
            handle = LibMpv.Create();
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            UnavailableReason = "libmpv could not be loaded. Install mpv, which provides it.";
            return;
        }
        if (handle == 0)
        {
            UnavailableReason = "libmpv could not create a player.";
            return;
        }

        (string Name, string Value)[] options =
        [
            ("config", "no"), ("terminal", "no"), ("msg-level", "all=error"), ("idle", "yes"),
            ("vid", "no"), ("video", "no"), ("audio-display", "no"), ("sub-auto", "no"), ("audio-file-auto", "no"),
            ("cover-art-auto", "no"), ("input-default-bindings", "no"), ("input-builtin-bindings", "no"),
            ("input-vo-keyboard", "no"), ("keep-open", "no"),
            // mpv's own scripts, such as the on-screen controller and the console, are not loaded.
            ("load-scripts", "no"), ("ytdl", "no"), ("osc", "no"), ("load-stats-overlay", "no"),
            ("load-console", "no"), ("load-select", "no"), ("load-positioning", "no"), ("load-commands", "no"),
            ("load-context-menu", "no"), ("load-auto-profiles", "no"),
            ("gapless-audio", "weak"), ("prefetch-playlist", "yes"), ("volume-max", "100"),
            ("audio-client-name", clientName), ("replaygain-clip", "no"), ("replaygain-fallback", "0"),
        ];
        // An older mpv skips options it does not know. They only switch features off.
        foreach (var (name, value) in options)
            LibMpv.SetOptionString(handle, name, value);
        if (!string.IsNullOrEmpty(audioOutput))
            LibMpv.SetOptionString(handle, "ao", audioOutput);
        // FERMATA_MPV_LOG names a file for mpv's detailed log, for troubleshooting playback.
        if (Environment.GetEnvironmentVariable("FERMATA_MPV_LOG") is { Length: > 0 } log)
        {
            LibMpv.SetOptionString(handle, "log-file", log);
            LibMpv.SetOptionString(handle, "msg-level", "all=debug");
        }
        int status = LibMpv.Initialize(handle);
        if (status < 0)
        {
            UnavailableReason = "libmpv failed to start: " + LibMpv.Describe(status);
            LibMpv.TerminateDestroy(handle);
            handle = 0;
            return;
        }
        LibMpv.ObserveProperty(handle, PauseObserver, "pause", LibMpv.Format.Flag);
        LibMpv.ObserveProperty(handle, DurationObserver, "duration", LibMpv.Format.Double);
        LibMpv.ObserveProperty(handle, DevicesObserver, "audio-device-list", LibMpv.Format.Node);
        LibMpv.ObserveProperty(handle, IdleObserver, "idle-active", LibMpv.Format.Flag);
        self = GCHandle.Alloc(this);
        LibMpv.SetWakeupCallback(handle, &OnWakeup, GCHandle.ToIntPtr(self));
    }

    public event Action<EngineEvent>? EventRaised;

    public bool IsAvailable => handle != 0;
    public string? UnavailableReason { get; }
    public IReadOnlyList<AudioDevice> AudioDevices => devices;

    public TimeSpan Position
    {
        get
        {
            double seconds = 0;
            if (handle == 0 || LibMpv.GetProperty(handle, "time-pos", LibMpv.Format.Double, &seconds) < 0)
                return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
    }

    public bool Paused
    {
        set => SetFlag("pause", value);
    }

    public double Volume
    {
        // mpv's volume is already perceptual (cubic), so a linear control maps straight onto it.
        set => SetDouble("volume", Math.Clamp(value, 0, 1) * 100);
    }

    public bool Muted
    {
        set => SetFlag("mute", value);
    }

    public ReplayGainMode ReplayGain
    {
        set => SetString("replaygain", value switch { ReplayGainMode.Track => "track", ReplayGainMode.Album => "album", _ => "no" });
    }

    public bool Gapless
    {
        set => SetString("gapless-audio", value ? "weak" : "no");
    }

    public string AudioDevice
    {
        set => SetString("audio-device", string.IsNullOrEmpty(value) ? "auto" : value);
    }

    public long Play(string path, TimeSpan start, bool paused)
    {
        if (handle == 0)
            return -1;
        SetFlag("pause", paused);
        string startOption = "start=" + start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        long id = hasLoadfileIndex
            ? LoadFile(["loadfile", path, "replace", "-1", startOption])
            : LoadFile(["loadfile", path, "replace", startOption]);
        currentId = id;
        return id;
    }

    public long Preload(string path)
    {
        if (handle == 0)
            return -1;
        // playlist-clear keeps the item that is playing, so this replaces only an earlier preload.
        LibMpv.Run(handle, ["playlist-clear"]);
        return LoadFile(["loadfile", path, "append"]);
    }

    public void ClearPreload()
    {
        if (handle != 0)
            LibMpv.Run(handle, ["playlist-clear"]);
    }

    public void Stop()
    {
        if (handle != 0)
            LibMpv.Run(handle, ["stop"]);
        currentId = -1;
    }

    public void Seek(TimeSpan position)
    {
        if (handle != 0)
            LibMpv.Run(handle, ["seek", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact"]);
    }

    /// <summary>Runs loadfile and returns the playlist entry id it created.</summary>
    private long LoadFile(ReadOnlySpan<string> args)
    {
        LibMpv.Node result = default;
        int status = LibMpv.Run(handle, args, &result);
        long id = -1;
        if (status >= 0)
        {
            var map = result.AsList();
            if (map != null && map->Find("playlist_entry_id") is var entry && entry != null)
                id = entry->AsInt64();
            LibMpv.FreeNodeContents(&result);
        }
        return id;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWakeup(nint data)
    {
        // Called on an mpv thread; must not call back into mpv. One pending drain is enough.
        if (GCHandle.FromIntPtr(data).Target is MpvEngine engine && Interlocked.Exchange(ref engine.drainPending, 1) == 0)
            engine.context.Post(static state => ((MpvEngine)state!).Drain(), engine);
    }

    private void Drain()
    {
        // The flag is cleared first, so a wakeup that arrives while draining schedules another drain.
        Volatile.Write(ref drainPending, 0);
        while (handle != 0)
        {
            var e = LibMpv.WaitEvent(handle, 0);
            if (e->Id == LibMpv.EventId.None)
                break;
            var translated = Translate(e);
            if (translated is not null)
                EventRaised?.Invoke(translated);
        }
    }

    private EngineEvent? Translate(LibMpv.Event* e)
    {
        switch (e->Id)
        {
            case LibMpv.EventId.StartFile:
            {
                long id = ((LibMpv.EventStartFile*)e->Data)->PlaylistEntryId;
                currentId = id;
                return new ItemStarted(id);
            }
            case LibMpv.EventId.EndFile:
            {
                var end = (LibMpv.EventEndFile*)e->Data;
                var reason = end->Reason switch
                {
                    LibMpv.EndFileReason.Eof => EndReason.Finished,
                    LibMpv.EndFileReason.Error => EndReason.Failed,
                    _ => EndReason.Stopped,
                };
                string? error = reason == EndReason.Failed ? LibMpv.Describe(end->Error) : null;
                return new ItemEnded(end->PlaylistEntryId, reason, error);
            }
            case LibMpv.EventId.PlaybackRestart:
                return new PlaybackRestarted();
            case LibMpv.EventId.PropertyChange:
                return TranslateProperty(e->ReplyUserData, (LibMpv.EventProperty*)e->Data);
            default:
                return null;
        }
    }

    private EngineEvent? TranslateProperty(ulong observer, LibMpv.EventProperty* property)
    {
        switch (observer)
        {
            case PauseObserver when property->Format == LibMpv.Format.Flag:
                return new PauseChanged(*(int*)property->Data != 0);
            case DurationObserver when property->Format == LibMpv.Format.Double:
                return new ItemLoaded(currentId, TimeSpan.FromSeconds(*(double*)property->Data));
            case DevicesObserver when property->Format == LibMpv.Format.Node:
                devices = ReadDevices((LibMpv.Node*)property->Data);
                return new DevicesChanged();
            case IdleObserver when property->Format == LibMpv.Format.Flag && *(int*)property->Data != 0:
                currentId = -1;
                return new EngineIdle();
            default:
                return null;
        }
    }

    private static List<AudioDevice> ReadDevices(LibMpv.Node* node)
    {
        var result = new List<AudioDevice>();
        var list = node->AsList();
        if (list == null)
            return result;
        for (int i = 0; i < list->Count; i++)
        {
            var map = list->Values[i].AsList();
            if (map == null)
                continue;
            string? name = map->Find("name") is var n && n != null ? n->AsString() : null;
            string? description = map->Find("description") is var d && d != null ? d->AsString() : null;
            if (name is not null)
                result.Add(new AudioDevice(name, description ?? name));
        }
        return result;
    }

    private void SetFlag(string name, bool value)
    {
        if (handle == 0)
            return;
        int flag = value ? 1 : 0;
        LibMpv.SetProperty(handle, name, LibMpv.Format.Flag, &flag);
    }

    private void SetDouble(string name, double value)
    {
        if (handle != 0)
            LibMpv.SetProperty(handle, name, LibMpv.Format.Double, &value);
    }

    private void SetString(string name, string value)
    {
        if (handle != 0)
            LibMpv.SetPropertyString(handle, name, value);
    }

    public void Dispose()
    {
        if (handle == 0)
            return;
        LibMpv.SetWakeupCallback(handle, null, 0);
        LibMpv.TerminateDestroy(handle);
        handle = 0;
        if (self.IsAllocated)
            self.Free();
    }
}

/// <summary>The engine used when no audio backend is available.</summary>
public sealed class UnavailableEngine(string reason) : IAudioEngine
{
    public event Action<EngineEvent>? EventRaised { add { } remove { } }

    public bool IsAvailable => false;
    public string? UnavailableReason => reason;
    public IReadOnlyList<AudioDevice> AudioDevices => [];
    public TimeSpan Position => TimeSpan.Zero;
    public bool Paused { set { } }
    public double Volume { set { } }
    public bool Muted { set { } }
    public ReplayGainMode ReplayGain { set { } }
    public bool Gapless { set { } }
    public string AudioDevice { set { } }
    public long Play(string path, TimeSpan start, bool paused) => -1;
    public long Preload(string path) => -1;
    public void ClearPreload() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }
    public void Dispose() { }
}
