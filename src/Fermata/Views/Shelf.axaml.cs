using Avalonia;
using Avalonia.Controls;

namespace Fermata.Views;

/// <summary>A titled, horizontally scrolling row of cards.</summary>
public partial class Shelf : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<Shelf, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<Shelf, string?>(nameof(Subtitle));
    public static readonly StyledProperty<IReadOnlyList<object>?> ItemsProperty =
        AvaloniaProperty.Register<Shelf, IReadOnlyList<object>?>(nameof(Items));

    public Shelf()
    {
        InitializeComponent();
        Previous.Click += (_, _) => ScrollBy(-1);
        Next.Click += (_, _) => ScrollBy(1);
        Scroller.ScrollChanged += (_, _) => UpdateArrows();
        Scroller.SizeChanged += (_, _) => UpdateArrows();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public IReadOnlyList<object>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
            TitleText.Text = Title;
        else if (change.Property == SubtitleProperty)
        {
            SubtitleText.Text = Subtitle;
            SubtitleText.IsVisible = !string.IsNullOrEmpty(Subtitle);
        }
        else if (change.Property == ItemsProperty)
        {
            ItemsList.ItemsSource = Items;
            Scroller.Offset = default;
        }
    }

    /// <summary>Scrolls so that the last visible card becomes the first.</summary>
    private void ScrollBy(int direction)
    {
        double step = Math.Max(196, Scroller.Viewport.Width - 196);
        double x = Math.Clamp(Scroller.Offset.X + direction * step, 0, Math.Max(0, Scroller.Extent.Width - Scroller.Viewport.Width));
        Scroller.Offset = new Vector(x, 0);
    }

    private void UpdateArrows()
    {
        bool scrollable = Scroller.Extent.Width > Scroller.Viewport.Width + 1;
        Previous.IsVisible = Next.IsVisible = scrollable;
        Previous.IsEnabled = Scroller.Offset.X > 1;
        Next.IsEnabled = Scroller.Offset.X + Scroller.Viewport.Width < Scroller.Extent.Width - 1;
    }
}
