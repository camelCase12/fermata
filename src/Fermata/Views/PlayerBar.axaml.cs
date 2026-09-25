using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Fermata.Controls;
using Fermata.Playback;
using Fermata.ViewModels;

namespace Fermata.Views;

public partial class PlayerBar : UserControl
{
    private PlayerViewModel? model;

    public PlayerBar()
    {
        InitializeComponent();
        Progress.ValueCommitted += (_, value) => model?.SeekTo(value);
        Progress.PropertyChanged += OnProgressHover;
        Progress.SizeChanged += (_, e) => model?.SetProgressWidth(e.NewSize.Width);
        VolumeBar.ValueDragged += (_, value) => model?.SetVolume(value);
        VolumeBar.ValueCommitted += (_, value) => model?.SetVolume(value);
        VolumeBar.PointerWheelChanged += OnVolumeWheel;
        MuteButton.PointerWheelChanged += OnVolumeWheel;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (model is not null)
            model.PropertyChanged -= OnModelChanged;
        model = DataContext as PlayerViewModel;
        if (model is not null)
        {
            model.PropertyChanged += OnModelChanged;
            UpdateRepeat();
            UpdateVolumeIcon();
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Repeat):
                UpdateRepeat();
                break;
            case nameof(PlayerViewModel.Volume) or nameof(PlayerViewModel.Muted):
                UpdateVolumeIcon();
                break;
        }
    }

    private void UpdateRepeat()
    {
        var repeat = model?.Repeat ?? RepeatMode.Off;
        RepeatButton.IsChecked = repeat != RepeatMode.Off;
        RepeatIcon.IsVisible = repeat != RepeatMode.One;
        RepeatOneIcon.IsVisible = repeat == RepeatMode.One;
        ToolTip.SetTip(RepeatButton, repeat switch
        {
            RepeatMode.All => "Repeating all (R)",
            RepeatMode.One => "Repeating one song (R)",
            _ => "Repeat (R)",
        });
    }

    private void UpdateVolumeIcon()
    {
        if (model is null)
            return;
        string key = model.Muted || model.Volume <= 0 ? "Icon.VolumeOff"
            : model.Volume < 0.34 ? "Icon.VolumeLow"
            : model.Volume < 0.67 ? "Icon.VolumeMedium"
            : "Icon.VolumeHigh";
        if (this.TryFindResource(key, out var geometry) && geometry is Geometry data)
            VolumeIcon.Data = data;
        VolumeBar.Opacity = model.Muted ? 0.45 : 1;
    }

    private void OnVolumeWheel(object? sender, PointerWheelEventArgs e)
    {
        model?.ChangeVolume(e.Delta.Y > 0 ? 0.05 : -0.05);
        e.Handled = true;
    }

    /// <summary>Shows the time under the pointer above the progress bar.</summary>
    private void OnProgressHover(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != SeekBar.HoverValueProperty || model is null)
            return;
        if (Progress.HoverValue is not { } value || model.Duration <= TimeSpan.Zero)
        {
            HoverBubble.IsVisible = false;
            return;
        }
        HoverText.Text = Formats.Duration(TimeSpan.FromSeconds(value * model.Duration.TotalSeconds));
        HoverBubble.IsVisible = true;
        HoverBubble.Measure(Size.Infinity);
        double width = HoverBubble.DesiredSize.Width;
        Canvas.SetLeft(HoverBubble, Math.Clamp(value * Progress.Bounds.Width - width / 2, 4, Progress.Bounds.Width - width - 4));
    }
}
