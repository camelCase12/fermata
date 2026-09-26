using Avalonia;
using Avalonia.Controls;

namespace Fermata.Controls;

/// <summary>A panel that lays its children out left to right, giving each only the width still left.</summary>
public sealed class InlineRow : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double used = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(Math.Max(0, availableSize.Width - used), availableSize.Height));
            used += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (var child in Children)
        {
            double width = Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - x));
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
