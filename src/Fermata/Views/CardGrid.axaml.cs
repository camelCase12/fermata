using Avalonia;
using Avalonia.Controls;

namespace Fermata.Views;

/// <summary>One row of cards in a <see cref="CardGrid"/>.</summary>
public sealed record CardRow(IReadOnlyList<object> Items, int Columns);

/// <summary>A virtualized grid of cards.</summary>
public partial class CardGrid : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<object>?> ItemsProperty =
        AvaloniaProperty.Register<CardGrid, IReadOnlyList<object>?>(nameof(Items));

    public static readonly StyledProperty<double> MinCardWidthProperty =
        AvaloniaProperty.Register<CardGrid, double>(nameof(MinCardWidth), 172);

    private int columns;

    public CardGrid()
    {
        InitializeComponent();
    }

    public IReadOnlyList<object>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double MinCardWidth
    {
        get => GetValue(MinCardWidthProperty);
        set => SetValue(MinCardWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
            Rebuild(force: true);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Rebuild(force: false);
    }

    private void Rebuild(bool force)
    {
        const double Gap = 20;
        double width = Bounds.Width;
        if (width <= 0)
            return;
        int fit = Math.Max(2, (int)((width + Gap) / (MinCardWidth + Gap)));
        if (!force && fit == columns)
            return;
        columns = fit;
        var items = Items ?? [];
        var rows = new List<CardRow>((items.Count + fit - 1) / fit);
        for (int start = 0; start < items.Count; start += fit)
        {
            int count = Math.Min(fit, items.Count - start);
            var row = new object[count];
            for (int i = 0; i < count; i++)
                row[i] = items[start + i];
            rows.Add(new CardRow(row, fit));
        }
        Rows.ItemsSource = rows;
    }
}
