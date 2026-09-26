using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Fermata.Controls;

/// <summary>
/// A thin horizontal bar for playback progress and volume: click or drag to set a value. It thickens
/// and shows a handle under the pointer.
/// </summary>
/// <remarks>
/// Progress arrives many times per second while playing, so the bar only redraws when the fill would
/// move by at least one physical pixel.
/// </remarks>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SeekBar, IBrush?>(nameof(TrackBrush), new SolidColorBrush(Colors.White, 0.18));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<SeekBar, IBrush?>(nameof(FillBrush), Brushes.White);

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<SeekBar, double>(nameof(Thickness), 3);

    /// <summary>Always show the handle (the volume bar), not only on hover.</summary>
    public static readonly StyledProperty<bool> AlwaysShowHandleProperty =
        AvaloniaProperty.Register<SeekBar, bool>(nameof(AlwaysShowHandle));

    public static readonly DirectProperty<SeekBar, double?> HoverValueProperty =
        AvaloniaProperty.RegisterDirect<SeekBar, double?>(nameof(HoverValue), o => o.HoverValue);

    static SeekBar()
    {
        AffectsRender<SeekBar>(TrackBrushProperty, FillBrushProperty, ThicknessProperty, AlwaysShowHandleProperty);
        FocusableProperty.OverrideDefaultValue<SeekBar>(false);
        CursorProperty.OverrideDefaultValue<SeekBar>(new Cursor(StandardCursorType.Hand));
    }

    private bool hovered;
    private bool dragging;
    private double dragValue;
    private double? hoverValue;
    private int drawnPixel = -1;

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public bool AlwaysShowHandle
    {
        get => GetValue(AlwaysShowHandleProperty);
        set => SetValue(AlwaysShowHandleProperty, value);
    }

    /// <summary>The value under the pointer while hovering or dragging, for a time tooltip.</summary>
    public double? HoverValue
    {
        get => hoverValue;
        private set => SetAndRaise(HoverValueProperty, ref hoverValue, value);
    }

    /// <summary>Raised when the user releases the bar at a new value.</summary>
    public event EventHandler<double>? ValueCommitted;

    /// <summary>Raised continuously while dragging (the volume bar applies it live).</summary>
    public event EventHandler<double>? ValueDragged;

    private double Shown => dragging ? dragValue : Math.Clamp(Value, 0, 1);

    protected override AutomationPeer OnCreateAutomationPeer() => new SeekBarAutomationPeer(this);

    /// <summary>Sets the value and raises <see cref="ValueCommitted"/>.</summary>
    internal void Commit(double value)
    {
        ValueCommitted?.Invoke(this, value);
        SetCurrentValue(ValueProperty, value);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !dragging)
        {
            int pixel = FillPixel(Shown);
            if (pixel != drawnPixel)
                InvalidateVisual();
        }
    }

    private int FillPixel(double value)
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        return (int)(value * Bounds.Width * scale);
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        hovered = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hovered = false;
        if (!dragging)
            HoverValue = null;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double value = ValueAt(e.GetPosition(this));
        HoverValue = value;
        if (dragging)
        {
            dragValue = value;
            ValueDragged?.Invoke(this, value);
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        dragging = true;
        dragValue = ValueAt(e.GetPosition(this));
        e.Pointer.Capture(this);
        ValueDragged?.Invoke(this, dragValue);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!dragging)
            return;
        dragging = false;
        e.Pointer.Capture(null);
        if (!hovered)
            HoverValue = null;
        ValueCommitted?.Invoke(this, dragValue);
        // Show the committed value until the model catches up.
        SetCurrentValue(ValueProperty, dragValue);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragging = false;
        InvalidateVisual();
    }

    private double ValueAt(Point point) => Bounds.Width <= 0 ? 0 : Math.Clamp(point.X / Bounds.Width, 0, 1);

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0)
            return;
        double value = Shown;
        drawnPixel = FillPixel(value);
        bool active = hovered || dragging;
        double thickness = active ? Thickness + 2 : Thickness;
        double y = (bounds.Height - thickness) / 2;
        var track = new Rect(0, y, bounds.Width, thickness);
        var radius = thickness / 2;
        // Transparent hit area across the whole height, so the thin bar is easy to grab.
        context.FillRectangle(Brushes.Transparent, bounds);
        if (TrackBrush is { } trackBrush)
            context.DrawRectangle(trackBrush, null, new RoundedRect(track, radius));
        double fill = value * bounds.Width;
        if (fill > 0 && FillBrush is { } fillBrush)
            context.DrawRectangle(fillBrush, null, new RoundedRect(new Rect(0, y, fill, thickness), radius));
        if ((active || AlwaysShowHandle) && FillBrush is { } handleBrush)
        {
            double handle = active ? 6.5 : 5;
            context.DrawEllipse(handleBrush, null, new Point(Math.Clamp(fill, handle, bounds.Width - handle), bounds.Height / 2), handle, handle);
        }
    }
}
