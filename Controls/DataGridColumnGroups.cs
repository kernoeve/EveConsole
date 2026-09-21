using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace EveConsole.Controls;

/// <summary>
/// A row of headings over a DataGrid, each spanning the columns it names — "Market value" over
/// unit, total and per cent — which the grid's own headers cannot do. Each heading's place and
/// width follow the grid's columns as they are laid out and resized, and the headings scroll
/// sideways with them; one over the frozen columns stays put, and one that slides under the
/// frozen columns is cut short there.
///
/// <para>Groups are named by index in the grid's Columns collection, so the grid must not let
/// its columns be reordered.</para>
/// </summary>
/// <summary>One heading: over <c>Count</c> columns from <c>First</c>, with the key of the brush
/// that washes its cells so the heading wears the same, and whatever else belongs under the
/// title — a total, a note, a button.</summary>
public sealed record ColumnGroup(int First, int Count, string Title, string? Wash = null, Control? Detail = null);

public sealed class DataGridColumnGroups : Panel
{
    private DataGrid?  _target;
    private ScrollBar? _bar;
    private readonly List<(int First, int Count)> _spans = [];
    private double[] _widths = [];
    private double   _offset;

    public DataGridColumnGroups() { ClipToBounds = true; }

    /// <summary>The grid whose columns the headings follow.</summary>
    public DataGrid? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            if (_target is not null)
            {
                _target.LayoutUpdated   -= OnTargetLayout;
                _target.TemplateApplied -= OnTargetTemplate;
                Bar(null);
            }
            _target = value;
            if (_target is null) return;
            _target.LayoutUpdated   += OnTargetLayout;
            _target.TemplateApplied += OnTargetTemplate;
            Bar(HorizontalBarOf(_target));
            Sync();
        }
    }

    /// <summary>The headings, left to right.</summary>
    public void SetGroups(IEnumerable<ColumnGroup> groups)
    {
        Children.Clear();
        _spans.Clear();
        foreach (var g in groups)
        {
            _spans.Add((g.First, g.Count));
            Children.Add(Heading(g));
        }
        InvalidateMeasure();
    }

    private static Control Heading(ColumnGroup g)
    {
        var title = g.Title;
        var wash  = g.Wash;
        var text = new TextBlock
        {
            Text                = title,
            FontSize            = 10,
            FontWeight          = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextTrimming        = TextTrimming.CharacterEllipsis,
        };
        text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("TextPrimaryBrush"));

        Control content = text;
        if (g.Detail is { } detail)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(text);
            stack.Children.Add(detail);
            content = stack;
        }

        // The wash sits on a raised surface, as the cells' wash sits on the rows.
        var inner = new Border { Child = content, Padding = new Thickness(6, 3) };
        if (wash is not null) inner.Bind(Border.BackgroundProperty, inner.GetResourceObservable(wash));

        var border = new Border
        {
            Child           = inner,
            Margin          = new Thickness(0, 0, 2, 0),   // the gap that parts one heading from the next
            BorderThickness = new Thickness(0, 0, 0, 2),
            CornerRadius    = new CornerRadius(3, 3, 0, 0),
            ClipToBounds    = true,
        };
        border.Bind(Border.BackgroundProperty,  border.GetResourceObservable("SurfaceRaisedBrush"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("AccentBrush"));
        ToolTip.SetTip(border, title);   // the whole name when the columns are narrower than it
        return border;
    }

    // ── Following the grid ─────────────────────────────────────────────────

    private static ScrollBar? HorizontalBarOf(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Orientation == Orientation.Horizontal);

    private void OnTargetTemplate(object? sender, TemplateAppliedEventArgs e) =>
        Bar(e.NameScope.Find<ScrollBar>("PART_HorizontalScrollbar") ?? (_target is { } t ? HorizontalBarOf(t) : null));

    private void Bar(ScrollBar? bar)
    {
        if (ReferenceEquals(_bar, bar)) return;
        if (_bar is not null) _bar.PropertyChanged -= OnBarChanged;
        _bar = bar;
        if (_bar is not null) _bar.PropertyChanged += OnBarChanged;
    }

    private void OnBarChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty) Sync();
    }

    private void OnTargetLayout(object? sender, EventArgs e) => Sync();

    /// <summary>Takes the columns' widths and the scroll offset as they now stand, and lays the
    /// headings out again only when one of them moved.</summary>
    private void Sync()
    {
        if (_target is null) return;
        var widths = new double[_target.Columns.Count];
        for (var i = 0; i < widths.Length; i++)
            widths[i] = _target.Columns[i].IsVisible ? _target.Columns[i].ActualWidth : 0;
        var offset = _bar?.Value ?? 0;

        var widthsMoved = !widths.SequenceEqual(_widths);
        if (!widthsMoved && offset == _offset) return;
        _widths = widths;
        _offset = offset;
        if (widthsMoved) InvalidateMeasure(); else InvalidateArrange();
    }

    private double WidthOf(int first, int count)
    {
        double w = 0;
        for (var i = first; i < first + count && i < _widths.Length; i++) w += _widths[i];
        return w;
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(WidthOf(_spans[i].First, _spans[i].Count), double.PositiveInfinity));
            height = Math.Max(height, Children[i].DesiredSize.Height);
        }
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : WidthOf(0, _widths.Length);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var frozenCount = _target?.FrozenColumnCount ?? 0;
        var frozenWidth = WidthOf(0, frozenCount);
        for (var i = 0; i < Children.Count; i++)
        {
            var (first, count) = _spans[i];
            var x = WidthOf(0, first);
            var w = WidthOf(first, count);
            if (first >= frozenCount)
            {
                x -= _offset;
                if (x < frozenWidth) { w -= frozenWidth - x; x = frozenWidth; }   // slid under the frozen columns
            }
            Children[i].Arrange(w > 0 ? new Rect(x, 0, w, finalSize.Height) : new Rect(0, 0, 0, 0));
        }
        return finalSize;
    }
}
