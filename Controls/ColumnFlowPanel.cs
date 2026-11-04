using Avalonia;
using Avalonia.Controls;

namespace EveConsole.Controls;

/// <summary>
/// Cards in as many equal columns as fit at <see cref="MinColumnWidth"/>, filled left to right and
/// then top to bottom, each row as tall as its tallest card and every card in it stretched to
/// match. One column when the width is unbounded or too narrow for two.
///
/// <para>Not a WrapPanel: that one leaves each card at its own width, so a row ends ragged and
/// the space past it unused. Here the columns share the width between them, and grow back to
/// fewer, wider columns as the window narrows.</para>
/// </summary>
public sealed class ColumnFlowPanel : Panel
{
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(MinColumnWidth), 480);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(ColumnSpacing), 8);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ColumnFlowPanel, double>(nameof(RowSpacing), 8);

    static ColumnFlowPanel()
    {
        AffectsMeasure<ColumnFlowPanel>(MinColumnWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public double ColumnSpacing  { get => GetValue(ColumnSpacingProperty);  set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing     { get => GetValue(RowSpacingProperty);     set => SetValue(RowSpacingProperty, value); }

    /// <summary>How many columns a width holds, and how wide each is.</summary>
    private (int Count, double Width) Columns(double width)
    {
        if (double.IsInfinity(width) || width <= 0) return (1, width);
        var count = Math.Max(1, (int)((width + ColumnSpacing) / (Math.Max(1, MinColumnWidth) + ColumnSpacing)));
        return (count, Math.Max(0, (width - ColumnSpacing * (count - 1)) / count));
    }

    private readonly List<double> _rowHeights = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var (count, width) = Columns(availableSize.Width);
        _rowHeights.Clear();

        double widest = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(width, double.PositiveInfinity));
            if (i % count == 0) _rowHeights.Add(0);
            _rowHeights[^1] = Math.Max(_rowHeights[^1], visible[i].DesiredSize.Height);
            widest = Math.Max(widest, visible[i].DesiredSize.Width);
        }

        var height = _rowHeights.Sum() + RowSpacing * Math.Max(0, _rowHeights.Count - 1);
        var used   = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
        return new Size(used, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = Children.Where(c => c.IsVisible).ToList();
        var (count, width) = Columns(finalSize.Width);

        double y = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            var row = i / count;
            var col = i % count;
            var height = row < _rowHeights.Count ? _rowHeights[row] : visible[i].DesiredSize.Height;
            visible[i].Arrange(new Rect(col * (width + ColumnSpacing), y, width, height));
            if (col == count - 1 || i == visible.Count - 1) y += height + RowSpacing;
        }
        return finalSize;
    }
}
