using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace EveConsole.Views;

/// <summary>One column's total in a group's heading: its text, and its colour where it has one.</summary>
public readonly record struct ColumnTotal(string Text, IBrush? Foreground = null);

/// <summary>
/// A grouped grid's totals in its group heading, each one under its own column.
///
/// <para>⚠️ Placed from the grid's own column headers, every time they move. A group heading is
/// one cell across the whole row, so it has no columns of its own to line up with; and a fixed
/// layout would drift the first time a column was resized.</para>
///
/// <para>The heading's group is its DataContext. <see cref="Totals"/> works out a column's total
/// from the group's rows, or null for a column with none.</para>
/// </summary>
public sealed class GroupColumnTotals : Panel
{
    public static readonly StyledProperty<Func<DataGridColumn, IReadOnlyList<object>, ColumnTotal?>?> TotalsProperty =
        AvaloniaProperty.Register<GroupColumnTotals, Func<DataGridColumn, IReadOnlyList<object>, ColumnTotal?>?>(nameof(Totals));

    public Func<DataGridColumn, IReadOnlyList<object>, ColumnTotal?>? Totals
    {
        get => GetValue(TotalsProperty);
        set => SetValue(TotalsProperty, value);
    }

    /// <summary>
    /// Where a cell's text starts inside its column, so a total sits exactly over the numbers
    /// below it: the Fluent theme's 12 margin on a text column's TextBlock, none on a template
    /// column's own, and 1 more on both. Measured off a render.
    /// </summary>
    private static double CellInset(DataGridColumn column) => (column is DataGridTextColumn ? 12 : 0) + 1;

    private DataGrid? _grid;
    private string _layoutKey = "";

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _grid = this.FindAncestorOfType<DataGrid>();
        if (_grid is not null) _grid.LayoutUpdated += OnGridLayout;
        Fill();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_grid is not null) _grid.LayoutUpdated -= OnGridLayout;
        _grid = null;
        base.OnDetachedFromVisualTree(e);
    }

    // A heading is reused for another group as the grid scrolls.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Fill();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TotalsProperty) Fill();
    }

    private void Fill()
    {
        Children.Clear();
        if (_grid is null || Totals is not { } totals || DataContext is not DataGridCollectionViewGroup group) return;

        var rows = group.Items.Cast<object>().ToList();
        foreach (var column in _grid.Columns.Where(c => c.IsVisible))
        {
            if (totals(column, rows) is not { } total || total.Text.Length == 0) continue;
            var text = new TextBlock
            {
                Text = total.Text, Tag = column, FontSize = 10, FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (total.Foreground is { } brush) text.Foreground = brush;
            Children.Add(text);
        }
        InvalidateArrange();
    }

    /// <summary>A column resized or the grid scrolled sideways: the totals follow. Only then, since
    /// layout passes come often and most of them move nothing.</summary>
    private void OnGridLayout(object? sender, EventArgs e)
    {
        if (_grid is null) return;
        var key = string.Join(",", _grid.Columns.Select(c => $"{c.DisplayIndex}:{c.ActualWidth:F0}"))
                + "|" + (HeaderOf(_grid.Columns.FirstOrDefault()) is { } h ? h.TranslatePoint(default, this)?.X.ToString("F0") : "");
        if (key == _layoutKey) return;
        _layoutKey = key;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        // As wide as it is given: the heading stretches it across the row.
        return new Size(0, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child.Tag is not DataGridColumn column || HeaderOf(column) is not { } header
             || header.TranslatePoint(default, this) is not { } at)
            {
                child.Arrange(default);
                continue;
            }
            var inset = CellInset(column);
            var width = Math.Max(0, header.Bounds.Width - inset * 2);
            child.Arrange(new Rect(at.X + inset, 0, width, finalSize.Height));
        }
        return finalSize;
    }

    /// <summary>The grid's row of column headers, found once: searching the whole grid on every
    /// layout pass, for every heading on screen, would be most of the work the grid does.</summary>
    private DataGridColumnHeadersPresenter? _headers;

    /// <summary>A column's header on screen, matched by the header it was given.</summary>
    private DataGridColumnHeader? HeaderOf(DataGridColumn? column)
    {
        if (column is null || _grid is null) return null;
        if (_headers is null || !_headers.IsAttachedToVisualTree())
            _headers = _grid.GetVisualDescendants().OfType<DataGridColumnHeadersPresenter>().FirstOrDefault();
        return _headers?.Children.OfType<DataGridColumnHeader>().FirstOrDefault(h => ReferenceEquals(h.Content, column.Header));
    }
}
