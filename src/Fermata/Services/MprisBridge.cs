using System.Security.Cryptography;
using System.Text;
using Fermata.Integration;
using Fermata.Library;
using Fermata.Metadata;
using Fermata.Playback;

namespace Fermata.Services;

/// <summary>The connection between the player and MPRIS.</summary>
/// <remarks>
/// It publishes what is playing, with cover art exported to a file, and carries out media-key commands.
/// </remarks>
public sealed class MprisBridge : IMprisTarget, IDisposable
{
    private readonly AppServices services;
    private readonly MprisServer server;
    private readonly Action raise;
    private readonly Action quit;
    private string? artUrl;
    private ArtSource? exportedFor;

    public MprisBridge(AppServices services, SynchronizationContext context, Action raise, Action quit)
    {
        this.services = services;
        this.raise = raise;
        this.quit = quit;
        server = new MprisServer(this, context);
        var player = services.Player;
        player.StateChanged += Publish;
        player.TrackChanged += OnTrackChanged;
        player.DurationChanged += Publish;
        player.VolumeChanged += Publish;
        player.Queue.Changed += _ => Publish();
        player.Seeked += () =>
        {
            Publish();
            server.EmitSeeked(player.Position);
        };
        services.UserData.TrackChanged += path =>
        {
            if (path == player.CurrentTrack?.Path)
                Publish();
        };
    }

    public async Task StartAsync()
    {
        if (await server.StartAsync())
            Publish();
    }

    private void OnTrackChanged()
    {
        artUrl = null;
        var track = services.Player.CurrentTrack;
        var art = track is null ? null : services.Library.Snapshot.ArtOf(track);
        if (art is not null)
            ExportArt(art);
        Publish();
    }

    /// <summary>Sets the URL of the playing track's cover art.</summary>
    /// <remarks>Image files are referenced directly, and embedded pictures are written to the cache.</remarks>
    private void ExportArt(ArtSource art)
    {
        if (art.IsImageFile)
        {
            artUrl = new Uri(art.Path).AbsoluteUri;
            return;
        }
        exportedFor = art;
        string directory = services.Paths.ArtExportDirectory;
        string stem = Path.Combine(directory, Hash($"{art.Path}\n{art.Offset}\n{art.Length}"));
        foreach (string extension in (string[])[".jpg", ".png", ".webp", ".img"])
        {
            if (File.Exists(stem + extension))
            {
                artUrl = new Uri(stem + extension).AbsoluteUri;
                return;
            }
        }
        var context = SynchronizationContext.Current!;
        Task.Run(() =>
        {
            string file;
            try
            {
                byte[]? bytes = art.Offset >= 0 ? ReadRange(art) : TagReader.ReadPicture(art.Path);
                if (bytes is null || bytes.Length < 4)
                    return;
                // Consumers such as notification daemons may choose a decoder by extension.
                file = stem + (bytes[0] == 0xFF && bytes[1] == 0xD8 ? ".jpg"
                    : bytes[0] == 0x89 && bytes[1] == (byte)'P' ? ".png"
                    : bytes[0] == (byte)'R' && bytes[1] == (byte)'I' ? ".webp" : ".img");
                Directory.CreateDirectory(directory);
                Storage.AtomicFile.WriteAllBytes(file, bytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Without the picture, widgets show no cover.
                return;
            }
            context.Post(_ =>
            {
                if (exportedFor == art)
                {
                    artUrl = new Uri(file).AbsoluteUri;
                    Publish();
                }
            }, null);
        });
    }

    private static byte[]? ReadRange(ArtSource art)
    {
        using var handle = File.OpenHandle(art.Path);
        var bytes = new byte[art.Length];
        return RandomAccess.Read(handle, bytes, art.Offset) == bytes.Length ? bytes : null;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    private void Publish()
    {
        var player = services.Player;
        var queue = player.Queue;
        var entry = player.CurrentEntry;
        MprisMetadata? metadata = null;
        if (entry is not null)
        {
            var track = entry.Track;
            var stats = services.UserData.StatsFor(track.Path);
            metadata = new MprisMetadata(
                $"/org/fermata/entry/{entry.Id}",
                track.Title,
                track.ArtistNames.Count > 0 ? track.ArtistNames : [track.DisplayArtist],
                track.AlbumTitle,
                track.AlbumArtist.Length > 0 ? [track.AlbumArtist] : [],
                track.Genres,
                player.Duration,
                track.TrackNumber,
                track.DiscNumber,
                new Uri(track.Path).AbsoluteUri,
                artUrl,
                stats?.Plays ?? 0,
                stats?.Liked is not null ? 1.0 : null);
        }
        server.Update(new MprisState
        {
            PlaybackStatus = player.State switch
            {
                PlaybackState.Playing => "Playing",
                PlaybackState.Paused => "Paused",
                _ => "Stopped",
            },
            LoopStatus = queue.Repeat switch
            {
                RepeatMode.One => "Track",
                RepeatMode.All => "Playlist",
                _ => "None",
            },
            Shuffle = queue.Shuffle,
            Volume = player.Muted ? 0 : player.Volume,
            Metadata = metadata,
            CanGoNext = queue.Count > 0,
            CanGoPrevious = queue.Count > 0,
            CanPlay = queue.Count > 0,
            CanSeek = entry is not null,
            Position = player.Position,
        });
    }

    // Commands from media keys and desktop widgets.
    public void Raise() => raise();
    public void Quit() => quit();
    public void Play() => services.Player.Resume();
    public void Pause() => services.Player.Pause();
    public void PlayPause() => services.Player.PlayPause();
    public void Stop() => services.Player.Stop();
    public void Next() => services.Player.Next();
    public void Previous() => services.Player.Previous();
    public void SeekBy(TimeSpan offset) => services.Player.Seek(services.Player.Position + offset);

    public void SetPosition(string trackId, TimeSpan position)
    {
        if (services.Player.CurrentEntry is { } entry && trackId == $"/org/fermata/entry/{entry.Id}")
            services.Player.Seek(position);
    }

    public void SetShuffle(bool shuffle) => services.Player.SetShuffle(shuffle);

    public void SetLoopStatus(string status) => services.Player.Queue.Repeat = status switch
    {
        "Track" => RepeatMode.One,
        "Playlist" => RepeatMode.All,
        _ => RepeatMode.Off,
    };

    public void SetVolume(double volume)
    {
        services.Player.Muted = false;
        services.Player.Volume = volume;
    }

    public void OpenUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            && services.Library.Snapshot.FindTrack(parsed.LocalPath) is { } track)
            services.Player.Play([track], 0, new QueueSource("file", track.Title));
    }

    public void Dispose() => server.Dispose();
}
