using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.ViewModels;

/// <summary>State and commands of the player bar and the now-playing view.</summary>
/// <remarks>
/// The position is polled only while playing and visible, at the rate at which the progress bar can
/// move by a whole pixel (bounded to 25 per second and at least once a second); paused or hidden,
/// nothing runs.
/// </remarks>
public sealed partial class PlayerViewModel : ObservableObject
{
    private readonly Shell shell;
    private readonly Player player;
    private readonly DispatcherTimer positionTimer;
    private bool windowVisible = true;
    private double progressWidth = 500;
    private int shownSecond = -1;

    public PlayerViewModel(Shell shell)
    {
        this.shell = shell;
        player = shell.Player;
        positionTimer = new DispatcherTimer(DispatcherPriority.Render);
        positionTimer.Tick += (_, _) => UpdatePosition();
        player.StateChanged += OnStateChanged;
        player.TrackChanged += OnTrackChanged;
        player.DurationChanged += OnDurationChanged;
        player.Seeked += UpdatePosition;
        player.VolumeChanged += OnVolumeChanged;
        player.Queue.Changed += OnQueueChanged;
        player.PlaybackFailed += message => shell.Toasts.Show(message);
        shell.Services.UserData.TrackChanged += path =>
        {
            if (path == Track?.Path)
                IsLiked = shell.IsLiked(Track);
        };
        OnTrackChanged();
        OnStateChanged();
        OnVolumeChanged();
        OnQueueChanged(QueueChange.Mode);
    }

    public Shell Shell => shell;

    [ObservableProperty] public partial Track? Track { get; private set; }
    /// <summary>Something is loaded; transport buttons are disabled until then.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(NextCommand), nameof(PreviousCommand), nameof(ToggleNowPlayingCommand))]
    public partial bool HasTrack { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Artist { get; private set; } = "";
    [ObservableProperty] public partial string Details { get; private set; } = "";
    [ObservableProperty] public partial ArtSource? Art { get; private set; }
    [ObservableProperty] public partial bool IsPlaying { get; private set; }
    [ObservableProperty] public partial bool IsLiked { get; private set; }
    [ObservableProperty] public partial bool Shuffle { get; private set; }
    [ObservableProperty] public partial RepeatMode Repeat { get; private set; }
    [ObservableProperty] public partial double Volume { get; private set; }
    [ObservableProperty] public partial bool Muted { get; private set; }
    [ObservableProperty] public partial TimeSpan Duration { get; private set; }
    [ObservableProperty] public partial double Progress { get; private set; }
    [ObservableProperty] public partial string PositionText { get; private set; } = "0:00";
    [ObservableProperty] public partial string DurationText { get; private set; } = "0:00";
    [ObservableProperty] public partial string? SourceText { get; private set; }
    [ObservableProperty] public partial bool EngineMissing { get; private set; }

    public string? EngineMessage => player.EngineUnavailableReason;

    /// <summary>Position of the playing track, read on demand.</summary>
    public TimeSpan Position => player.Position;

    /// <summary>Raised whenever the position is sampled (for synchronized lyrics).</summary>
    public event Action<TimeSpan>? PositionSampled;

    /// <summary>The window reports whether it is visible; nothing is polled while it is not.</summary>
    public void SetWindowVisible(bool visible)
    {
        windowVisible = visible;
        UpdateTimer();
        if (visible)
            UpdatePosition();
    }

    /// <summary>The widest visible progress bar reports its width so updates match its resolution.</summary>
    public void SetProgressWidth(double width)
    {
        if (width > 0 && Math.Abs(width - progressWidth) > 1)
        {
            progressWidth = width;
            UpdateTimer();
        }
    }

    private void OnStateChanged()
    {
        IsPlaying = player.State == PlaybackState.Playing;
        UpdateTimer();
        UpdatePosition();
    }

    private void OnTrackChanged()
    {
        var track = player.CurrentTrack;
        Track = track;
        HasTrack = track is not null;
        EngineMissing = !player.IsEngineAvailable;
        if (track is null)
        {
            Title = Artist = Details = "";
            Art = null;
            IsLiked = false;
        }
        else
        {
            Title = track.Title;
            Artist = track.DisplayArtist;
            Details = string.Join(" • ", new[] { track.AlbumTitle, track.Year > 0 ? track.Year.ToString() : "" }.Where(s => s.Length > 0));
            Art = shell.Library.ArtOf(track);
            IsLiked = shell.IsLiked(track);
        }
        OnDurationChanged();
        shownSecond = -1;
        UpdatePosition();
    }

    private void OnDurationChanged()
    {
        Duration = player.Duration;
        DurationText = Formats.Duration(player.Duration);
        UpdateTimer();
    }

    private void OnVolumeChanged()
    {
        Volume = player.Volume;
        Muted = player.Muted;
    }

    private void OnQueueChanged(QueueChange change)
    {
        var queue = player.Queue;
        Shuffle = queue.Shuffle;
        Repeat = queue.Repeat;
        SourceText = queue.Source is { } source && source.Title.Length > 0 ? source.Title : null;
    }

    private void UpdateTimer()
    {
        bool run = IsPlaying && windowVisible && Duration > TimeSpan.Zero;
        if (!run)
        {
            positionTimer.Stop();
            return;
        }
        // One pixel of progress, within 40 ms – 1 s.
        double perPixel = Duration.TotalMilliseconds / Math.Max(100, progressWidth);
        positionTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(perPixel, 40, 1000));
        if (!positionTimer.IsEnabled)
            positionTimer.Start();
    }

    private void UpdatePosition()
    {
        var position = player.Position;
        Progress = Duration > TimeSpan.Zero ? Math.Clamp(position.TotalSeconds / Duration.TotalSeconds, 0, 1) : 0;
        int second = (int)position.TotalSeconds;
        if (second != shownSecond)
        {
            shownSecond = second;
            PositionText = Formats.Duration(position);
        }
        PositionSampled?.Invoke(position);
    }

    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void PlayPause() => player.PlayPause();

    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void Next() => player.Next();

    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void Previous() => player.Previous();

    [RelayCommand]
    private void ToggleShuffle() => player.SetShuffle(!player.Queue.Shuffle);

    [RelayCommand]
    private void CycleRepeat() => player.CycleRepeat();

    [RelayCommand]
    private void ToggleMute() => player.Muted = !player.Muted;

    [RelayCommand]
    private void ToggleLike()
    {
        if (Track is { } track)
            shell.ToggleLike(track);
    }

    [RelayCommand(CanExecute = nameof(HasTrack))]
    private void ToggleNowPlaying() => shell.IsNowPlayingOpen = !shell.IsNowPlayingOpen && HasTrack;

    [RelayCommand]
    private void OpenArtist()
    {
        if (Track is { } track)
            shell.OpenArtistOf(track);
    }

    [RelayCommand]
    private void OpenAlbum()
    {
        if (Track is { } track)
            shell.OpenAlbumOf(track);
    }

    [RelayCommand]
    private void OpenSource()
    {
        var source = player.Queue.Source;
        var library = shell.Library;
        switch (source?.Kind)
        {
            case "album" when source.Key is { } key && library.FindAlbum(key) is { } album:
                shell.IsNowPlayingOpen = false;
                shell.OpenAlbum(album);
                break;
            case "artist" when source.Key is { } name && library.FindArtist(name) is { } artist:
                shell.IsNowPlayingOpen = false;
                shell.OpenArtist(artist);
                break;
            case "playlist" when source.Key is { } id && shell.Services.Playlists.Find(id) is { } playlist:
                shell.IsNowPlayingOpen = false;
                shell.OpenPlaylist(playlist);
                break;
            case "liked":
                shell.IsNowPlayingOpen = false;
                shell.GoLiked();
                break;
        }
    }

    /// <summary>Seeks to a fraction of the track (from the progress bar).</summary>
    public void SeekTo(double fraction)
    {
        if (Duration > TimeSpan.Zero)
            player.Seek(TimeSpan.FromSeconds(Math.Clamp(fraction, 0, 1) * Duration.TotalSeconds));
    }

    public void SeekBy(TimeSpan offset) => player.Seek(player.Position + offset);

    /// <summary>Sets the volume from the volume bar (unmuting).</summary>
    public void SetVolume(double volume)
    {
        player.Muted = false;
        player.Volume = volume;
    }

    public void ChangeVolume(double delta) => SetVolume(Math.Clamp(player.Volume + delta, 0, 1));
}
