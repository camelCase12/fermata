using Avalonia;
using Avalonia.Controls;
using Fermata.Library;

namespace Fermata.Controls;

/// <summary>Cover art for a collection, as four covers in a 2×2 grid or one cover when there are fewer.</summary>
public sealed class Mosaic : Panel
{
    public static readonly StyledProperty<IReadOnlyList<ArtSource>?> CoversProperty =
        AvaloniaProperty.Register<Mosaic, IReadOnlyList<ArtSource>?>(nameof(Covers));

    public static readonly StyledProperty<string?> PlaceholderKeyProperty =
        AvaloniaProperty.Register<Mosaic, string?>(nameof(PlaceholderKey));

    public IReadOnlyList<ArtSource>? Covers
    {
        get => GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    public string? PlaceholderKey
    {
        get => GetValue(PlaceholderKeyProperty);
        set => SetValue(PlaceholderKeyProperty, value);
    }

    public Mosaic()
    {
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoversProperty || change.Property == PlaceholderKeyProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        Children.Clear();
        var covers = Covers ?? [];
        int count = covers.Count >= 4 ? 4 : Math.Min(1, covers.Count);
        if (count == 0)
        {
            Children.Add(new CoverArt { PlaceholderKey = PlaceholderKey, Radius = 0 });
            return;
        }
        for (int i = 0; i < count; i++)
            Children.Add(new CoverArt { Source = covers[i], Radius = 0 });
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 4)
        {
            double half = finalSize.Width / 2, halfHeight = finalSize.Height / 2;
            for (int i = 0; i < 4; i++)
                Children[i].Arrange(new Rect(i % 2 * half, i / 2 * halfHeight, half, halfHeight));
        }
        else if (Children.Count == 1)
        {
            Children[0].Arrange(new Rect(finalSize));
        }
        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
            child.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }
}
