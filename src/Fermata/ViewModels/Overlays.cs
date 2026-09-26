using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fermata.Library;

namespace Fermata.ViewModels;

/// <summary>A short message above the player bar, optionally with an action such as Undo.</summary>
public sealed partial class Toast(string message, string? actionLabel, Action? action) : ObservableObject
{
    public string Message { get; } = message;
    public string? ActionLabel { get; } = actionLabel;
    public bool HasAction => ActionLabel is not null;

    [RelayCommand]
    private void RunAction() => action?.Invoke();
}

/// <summary>Shows one toast at a time; a new one replaces the current one.</summary>
public sealed partial class ToastService : ObservableObject
{
    private readonly DispatcherTimer timer;

    public ToastService()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) => Dismiss();
    }

    [ObservableProperty]
    public partial Toast? Current { get; private set; }

    public void Show(string message, string? actionLabel = null, Action? action = null, TimeSpan? duration = null)
    {
        Current = new Toast(message, actionLabel, action is null ? null : () =>
        {
            action();
            Dismiss();
        });
        timer.Stop();
        timer.Interval = duration ?? TimeSpan.FromSeconds(action is null ? 3.5 : 6);
        timer.Start();
    }

    [RelayCommand]
    public void Dismiss()
    {
        timer.Stop();
        Current = null;
    }
}

/// <summary>An in-window dialog: a question with a text field, a confirmation, or information.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    public required string Title { get; init; }
}

public sealed partial class PromptDialog : DialogViewModel
{
    private readonly TaskCompletionSource<string?> result = new();

    public string? Message { get; init; }
    public string ConfirmLabel { get; init; } = "Save";
    public string Placeholder { get; init; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Text { get; set; } = "";

    public Task<string?> Result => result.Task;

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => result.TrySetResult(Text.Trim());

    [RelayCommand]
    private void Cancel() => result.TrySetResult(null);
}

public sealed partial class ConfirmDialog : DialogViewModel
{
    private readonly TaskCompletionSource<bool> result = new();

    public required string Message { get; init; }
    public string ConfirmLabel { get; init; } = "OK";
    public bool IsDestructive { get; init; }
    public Task<bool> Result => result.Task;

    [RelayCommand]
    private void Confirm() => result.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => result.TrySetResult(false);
}

/// <summary>File and tag details of a track.</summary>
public sealed partial class TrackInfoDialog : DialogViewModel
{
    private readonly TaskCompletionSource closed = new();

    public TrackInfoDialog(Track track, TrackStats? stats)
    {
        Track = track;
        var rows = new List<(string, string)>
        {
            ("Title", track.Title),
            ("Artist", track.DisplayArtist),
            ("Album", track.AlbumTitle.Length > 0 ? track.AlbumTitle : "—"),
            ("Album artist", track.AlbumArtist.Length > 0 ? track.AlbumArtist : "—"),
            ("Track", track.TrackNumber > 0 ? (track.TrackCount > 0 ? $"{track.TrackNumber} of {track.TrackCount}" : track.TrackNumber.ToString()) : "—"),
            ("Disc", track.DiscNumber > 0 ? (track.DiscCount > 0 ? $"{track.DiscNumber} of {track.DiscCount}" : track.DiscNumber.ToString()) : "—"),
            ("Year", track.Year > 0 ? track.Year.ToString() : "—"),
            ("Genre", track.Genres.Count > 0 ? string.Join(", ", track.Genres) : "—"),
            ("Composer", track.Composer.Length > 0 ? track.Composer : "—"),
            ("Length", Formats.Duration(track.Duration)),
            ("Format", track.FormatDescription),
            ("Channels", track.Channels switch { 1 => "Mono", 2 => "Stereo", 0 => "—", var n => $"{n} channels" }),
            ("File size", Formats.Size(track.FileSize)),
            ("Plays", (stats?.Plays ?? 0).ToString()),
            ("Last played", stats?.LastPlayed is { } last ? last.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Never"),
            ("Added", track.Added.ToLocalTime().ToString("yyyy-MM-dd")),
            ("Location", track.Path),
        };
        Rows = rows.Select(r => new InfoRow(r.Item1, r.Item2)).ToList();
    }

    public Track Track { get; }
    public IReadOnlyList<InfoRow> Rows { get; }
    public Task Closed => closed.Task;

    [RelayCommand]
    private void Close() => closed.TrySetResult();
}

public sealed record InfoRow(string Label, string Value);
