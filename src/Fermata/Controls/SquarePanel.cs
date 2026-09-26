using Avalonia;
using Avalonia.Controls;

namespace Fermata.Controls;

/// <summary>A panel that is as tall as it is wide.</summary>
public sealed class SquarePanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double side = double.IsInfinity(availableSize.Width) ? 160 : availableSize.Width;
        var square = new Size(side, side);
        foreach (var child in Children)
            child.Measure(square);
        return square;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var square = new Rect(0, 0, finalSize.Width, finalSize.Width);
        foreach (var child in Children)
            child.Arrange(square);
        return new Size(finalSize.Width, finalSize.Width);
    }
}
