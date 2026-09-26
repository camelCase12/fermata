using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;
using Fermata.Playback;

namespace Fermata.ViewModels;

public enum NowPlayingTab
{
    UpNext,
    Lyrics,
    Related,
}

public sealed partial class LyricLineViewModel(LyricLine line, int index) : ObservableObject
{
    public LyricLine Line { get; } = line;
    public int Index { get; } = index;
    public string Text => Line.Text.Length > 0 ? Line.Text : "♪";

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsPast { get; set; }
}

/// <summary>The view model of the now-playing view.</summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly Shell shell;
    private readonly PlayerViewModel player;
    private bool entriesStale = true;
    private bool lyricsStale = true;
    private bool relatedStale = true;
    private int lyricsRequest;

    public NowPlayingViewModel(Shell shell, PlayerViewModel player)
    {
        this.shell = shell;
        this.player = player;
        var queue = shell.Player.Queue;
        queue.Changed += change =>
        {
            if (change.HasFlag(QueueChange.Entries) || change.HasFlag(QueueChange.Mode))
                entriesStale = true;
            if (change.HasFlag(QueueChange.Current))
                CurrentIndex = queue.CurrentIndex;
            Refresh();
        };
        shell.Player.TrackChanged += () =>
        {
            lyricsStale = relatedStale = true;
            Refresh();
        };
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Shell.IsNowPlayingOpen))
                Refresh();
        };
        player.PositionSampled += OnPosition;
        Autoplay = shell.Services.Settings.Autoplay;
    }

    public PlayerViewModel Player => player;

    [ObservableProperty] public partial NowPlayingTab Tab { get; set; }
    [ObservableProperty] public partial IReadOnlyList<QueueEntry> Entries { get; private set; } = [];
    [ObservableProperty] public partial int CurrentIndex { get; private set; } = -1;
    [ObservableProperty] public partial IReadOnlyList<LyricLineViewModel> LyricLines { get; private set; } = [];
    [ObservableProperty] public partial bool LyricsSynced { get; private set; }
    [ObservableProperty] public partial string LyricsStatus { get; private set; } = "";
    [ObservableProperty] public partial LyricLineViewModel? ActiveLine { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<Track> Related { get; private set; } = [];
    [ObservableProperty] public partial string UpNextSummary { get; private set; } = "";

    [ObservableProperty]
    public partial bool Autoplay { get; set; }

    public bool IsUpNext => Tab == NowPlayingTab.UpNext;
    public bool IsLyrics => Tab == NowPlayingTab.Lyrics;
    public bool IsRelated => Tab == NowPlayingTab.Related;

    partial void OnTabChanged(NowPlayingTab value)
    {
        OnPropertyChanged(nameof(IsUpNext));
        OnPropertyChanged(nameof(IsLyrics));
        OnPropertyChanged(nameof(IsRelated));
        Refresh();
    }

    partial void OnAutoplayChanged(bool value)
    {
        shell.Services.Settings.Autoplay = value;
        shell.Services.SaveSettings();
        shell.Player.Autoplay = value;
    }

    [RelayCommand]
    private void ShowTab(NowPlayingTab tab) => Tab = tab;

    private void Refresh()
    {
        if (!shell.IsNowPlayingOpen)
            return;
        switch (Tab)
        {
            case NowPlayingTab.UpNext when entriesStale:
                entriesStale = false;
                var queue = shell.Player.Queue;
                Entries = queue.Entries.ToList();
                CurrentIndex = queue.CurrentIndex;
                int upcoming = Math.Max(0, queue.Count - queue.CurrentIndex - 1);
                var remaining = TimeSpan.FromTicks(queue.Entries.Skip(queue.CurrentIndex + 1).Sum(e => e.Track.Duration.Ticks));
                UpNextSummary = upcoming == 0 ? "Nothing up next" : $"{upcoming} up next · {Formats.LongDuration(remaining)}";
                break;
            case NowPlayingTab.Lyrics when lyricsStale:
                lyricsStale = false;
                LoadLyrics();
                break;
            case NowPlayingTab.Related when relatedStale:
                relatedStale = false;
                Related = player.Track is { } track
                    ? new Recommender(shell.Library, shell.Services.UserData).Radio(track, 21).Skip(1).ToList()
                    : [];
                break;
        }
    }

    private async void LoadLyrics()
    {
        int request = ++lyricsRequest;
        ActiveLine = null;
        if (player.Track is not { } track)
        {
            LyricLines = [];
            LyricsStatus = "";
            return;
        }
        LyricsStatus = "Loading lyrics…";
        LyricLines = [];
        Lyrics? lyrics;
        try
        {
            lyrics = await Task.Run(() => Lyrics.Load(track));
        }
        catch (IOException)
        {
            lyrics = null;
        }
        if (request != lyricsRequest)
            return;
        if (lyrics is null)
        {
            LyricsStatus = "No lyrics for this song.\nAdd a .lrc file with the same name beside the audio file for synchronized lyrics.";
            return;
        }
        LyricsStatus = "";
        LyricsSynced = lyrics.IsSynced;
        LyricLines = lyrics.Lines.Select((line, i) => new LyricLineViewModel(line, i)).ToList();
        OnPosition(player.Position);
    }

    private void OnPosition(TimeSpan position)
    {
        if (!LyricsSynced || !shell.IsNowPlayingOpen || Tab != NowPlayingTab.Lyrics || LyricLines.Count == 0)
            return;
        int index = FindLine(position);
        if (ActiveLine?.Index == index)
            return;
        foreach (var line in LyricLines)
        {
            line.IsActive = line.Index == index;
            line.IsPast = line.Index < index;
        }
        ActiveLine = index >= 0 ? LyricLines[index] : null;
    }

    private int FindLine(TimeSpan position)
    {
        int low = 0, high = LyricLines.Count - 1, found = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (LyricLines[middle].Line.Time <= position)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return found;
    }

    /// <summary>Seeks to the time of a synchronized lyric line.</summary>
    public void SeekToLine(LyricLineViewModel line)
    {
        if (LyricsSynced)
            shell.Player.Seek(line.Line.Time);
    }

    public void JumpTo(QueueEntry entry) => shell.Player.JumpTo(entry);

    public void Remove(IReadOnlyCollection<QueueEntry> entries) => shell.Player.RemoveFromQueue(entries);

    public void Move(int from, int to) => shell.Player.Queue.Move(from, to);

    [RelayCommand]
    private void ClearUpcoming()
    {
        shell.Player.Queue.ClearUpcoming();
        shell.Toasts.Show("Cleared the rest of the queue");
    }

    [RelayCommand]
    private async Task SaveQueueAsPlaylist()
    {
        var tracks = shell.Player.Queue.Entries.Select(e => e.Track).ToList();
        if (tracks.Count > 0)
            await shell.CreatePlaylistAsync(tracks);
    }

    [RelayCommand]
    private void Close() => shell.IsNowPlayingOpen = false;
}
