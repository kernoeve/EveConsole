using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveConsole.Services;

namespace EveConsole.Controls;

/// <summary>How one node should be painted. Supplied by the view model so the overlay logic
/// (security, sovereignty, kill activity, ...) stays out of the control.</summary>
public sealed record MapNodeStyle(Color Fill, string? Badge = null, string? Detail = null);

/// <summary>
/// Pan/zoom node-and-link map, drawn directly rather than with one visual per node — a region
/// map is up to 189 systems and the universe map 70 regions, which is far cheaper to paint in
/// one pass than to lay out as controls.
///
/// The control knows nothing about EVE: it takes a <see cref="MapGraph"/> for geometry and an
/// <see cref="Overlay"/> dictionary for colour, so the same control serves every map level.
/// </summary>
public class MapCanvas : Control
{
    public static readonly StyledProperty<MapGraph?> GraphProperty =
        AvaloniaProperty.Register<MapCanvas, MapGraph?>(nameof(Graph));

    public static readonly StyledProperty<IReadOnlyDictionary<int, MapNodeStyle>?> OverlayProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyDictionary<int, MapNodeStyle>?>(nameof(Overlay));

    public static readonly StyledProperty<int> SelectedIdProperty =
        AvaloniaProperty.Register<MapCanvas, int>(
            nameof(SelectedId), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Invoked with the node id when a node is double-clicked — the drill-down gesture.</summary>
    public static readonly StyledProperty<ICommand?> ActivateCommandProperty =
        AvaloniaProperty.Register<MapCanvas, ICommand?>(nameof(ActivateCommand));

    public MapGraph? Graph
    {
        get => GetValue(GraphProperty);
        set => SetValue(GraphProperty, value);
    }

    public IReadOnlyDictionary<int, MapNodeStyle>? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public int SelectedId
    {
        get => GetValue(SelectedIdProperty);
        set => SetValue(SelectedIdProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    static MapCanvas()
    {
        AffectsRender<MapCanvas>(GraphProperty, OverlayProperty, SelectedIdProperty);
    }

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Brushes and pens (immutable, allocated once) ─────────────────────────

    private static readonly IBrush BackBrush     = new ImmutableSolidColorBrush(Color.Parse("#0b0b10"));
    private static readonly IPen   EdgePen       = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a3a4e")), 1);
    private static readonly IPen   EdgeOutPen    = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#25252f")), 1);
    private static readonly IPen   NodePen       = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#0b0b10")), 1.5);
    private static readonly IPen   SelectedPen   = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#e8c86a")), 2);
    private static readonly IPen   HoverPen      = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#7fb8d8")), 1.5);
    private static readonly IBrush LabelBrush    = new ImmutableSolidColorBrush(Color.Parse("#b8b8c8"));
    private static readonly IBrush LabelDimBrush = new ImmutableSolidColorBrush(Color.Parse("#5a5a68"));
    private static readonly IBrush BadgeBrush    = new ImmutableSolidColorBrush(Color.Parse("#e8c86a"));
    private static readonly IBrush TipBackBrush  = new ImmutableSolidColorBrush(Color.Parse("#e6141420"));
    private static readonly IPen   TipPen        = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a3a4e")), 1);
    private static readonly IBrush TipTextBrush  = new ImmutableSolidColorBrush(Color.Parse("#d8d8e4"));
    private static readonly Color  DefaultFill   = Color.Parse("#6a6a80");

    private static readonly Typeface Face = Typeface.Default;

    private const double NodeRadius    = 5.0;
    private const double OutNodeRadius = 3.0;
    private const double LabelSize     = 10.0;
    private const double HitRadius     = 9.0;

    // ── View transform ───────────────────────────────────────────────────────

    private double _scale = 1;      // screen pixels per world unit
    private double _fitScale = 1;   // the scale that frames the whole graph
    private double _cx, _cy;        // world point currently at the centre of the view
    private bool   _needsFit = true;

    private Point  _dragFrom;
    private bool   _dragging;
    private bool   _dragMoved;

    private MapNode? _hover;
    private Point    _hoverAt;

    private MapGraph?                     _built;
    private Dictionary<int, MapNode>       _byId   = new();
    private Dictionary<int, FormattedText> _labels = new();

    private Point ToScreen(double x, double y) =>
        new((x - _cx) * _scale + Bounds.Width / 2, (y - _cy) * _scale + Bounds.Height / 2);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
        {
            _needsFit = true;
            _hover    = null;
        }
    }

    /// <summary>Frames the entire graph with a small margin. Deferred to render time because it
    /// needs the final bounds, which are not known when the graph is assigned.</summary>
    private void Fit()
    {
        var nodes = Graph?.Nodes;
        if (nodes is null || nodes.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            if (n.X < minX) minX = n.X;
            if (n.X > maxX) maxX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.Y > maxY) maxY = n.Y;
        }

        _cx = (minX + maxX) / 2;
        _cy = (minY + maxY) / 2;

        // A single-node graph, or one collapsed onto a line, has no extent on some axis;
        // fall back to a scale that at least puts it on screen rather than dividing by zero.
        var w = maxX - minX;
        var h = maxY - minY;
        var sx = w > 0 ? Bounds.Width  * 0.88 / w : double.MaxValue;
        var sy = h > 0 ? Bounds.Height * 0.88 / h : double.MaxValue;
        _fitScale = Math.Min(sx, sy);
        if (double.IsInfinity(_fitScale) || _fitScale <= 0 || _fitScale == double.MaxValue)
            _fitScale = 1;

        _scale    = _fitScale;
        _needsFit = false;
    }

    /// <summary>Reframes the whole graph. Bound to the toolbar's reset button.</summary>
    public void ResetView()
    {
        _needsFit = true;
        InvalidateVisual();
    }

    private void RebuildCaches(MapGraph g)
    {
        _built  = g;
        _byId   = g.Nodes.ToDictionary(n => n.Id);
        _labels = g.Nodes.ToDictionary(n => n.Id, n => new FormattedText(
            n.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, LabelSize, n.IsOutsideRegion ? LabelDimBrush : LabelBrush));
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(BackBrush, new Rect(Bounds.Size));

        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return;

        if (!ReferenceEquals(g, _built)) RebuildCaches(g);
        if (_needsFit) Fit();

        var overlay = Overlay;

        // Edges first so nodes sit on top of them.
        foreach (var e in g.Edges)
        {
            if (!_byId.TryGetValue(e.FromId, out var a) || !_byId.TryGetValue(e.ToId, out var b)) continue;
            var pa = ToScreen(a.X, a.Y);
            var pb = ToScreen(b.X, b.Y);
            ctx.DrawLine(a.IsOutsideRegion || b.IsOutsideRegion ? EdgeOutPen : EdgePen, pa, pb);
        }

        // Labels are only legible once the graph is framed or closer; below that they overlap
        // into noise, so they are dropped rather than drawn on top of each other.
        var showLabels = _scale >= _fitScale * 0.95;

        foreach (var n in g.Nodes)
        {
            var p = ToScreen(n.X, n.Y);
            var r = n.IsOutsideRegion ? OutNodeRadius : NodeRadius;

            // Skip anything scrolled well outside the viewport — at high zoom this is most
            // of the graph.
            if (p.X < -40 || p.Y < -40 || p.X > Bounds.Width + 40 || p.Y > Bounds.Height + 40) continue;

            var style = overlay is not null && overlay.TryGetValue(n.Id, out var s) ? s : null;
            var fill  = new ImmutableSolidColorBrush(style?.Fill ?? DefaultFill);

            ctx.DrawEllipse(fill, NodePen, p, r, r);

            if (n.Id == SelectedId)   ctx.DrawEllipse(null, SelectedPen, p, r + 4, r + 4);
            else if (_hover?.Id == n.Id) ctx.DrawEllipse(null, HoverPen, p, r + 3, r + 3);

            if (showLabels && _labels.TryGetValue(n.Id, out var label))
                ctx.DrawText(label, new Point(p.X + r + 3, p.Y - label.Height / 2));

            if (showLabels && style?.Badge is { Length: > 0 } badge)
            {
                var bt = new FormattedText(badge, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Face, LabelSize - 1.5, BadgeBrush);
                ctx.DrawText(bt, new Point(p.X - bt.Width / 2, p.Y + r + 1));
            }
        }

        if (_hover is not null) DrawTooltip(ctx, _hover);
    }

    private void DrawTooltip(DrawingContext ctx, MapNode n)
    {
        var style  = Overlay is not null && Overlay.TryGetValue(n.Id, out var s) ? s : null;
        var detail = style?.Detail;
        if (n.IsOutsideRegion)
            detail = string.IsNullOrEmpty(detail) ? n.RegionName : $"{n.RegionName}\n{detail}";

        var title = new FormattedText(n.Name, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, 11.5, TipTextBrush);
        var body = string.IsNullOrEmpty(detail) ? null : new FormattedText(
            detail, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, LabelBrush);

        const double pad = 7;
        var w = Math.Max(title.Width, body?.Width ?? 0) + pad * 2;
        var h = title.Height + (body is null ? 0 : body.Height + 3) + pad * 2;

        // Prefer up-and-right of the cursor, but flip whenever that would run off the edge.
        var x = _hoverAt.X + 14;
        var y = _hoverAt.Y + 14;
        if (x + w > Bounds.Width)  x = _hoverAt.X - w - 14;
        if (y + h > Bounds.Height) y = _hoverAt.Y - h - 14;
        x = Math.Max(0, x);
        y = Math.Max(0, y);

        var rect = new RoundedRect(new Rect(x, y, w, h), 3);
        ctx.DrawRectangle(TipBackBrush, TipPen, rect);
        ctx.DrawText(title, new Point(x + pad, y + pad));
        if (body is not null) ctx.DrawText(body, new Point(x + pad, y + pad + title.Height + 3));
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private MapNode? HitTest(Point p)
    {
        var g = Graph;
        if (g is null) return null;

        MapNode? best = null;
        var bestDist = HitRadius * HitRadius;
        foreach (var n in g.Nodes)
        {
            var s  = ToScreen(n.X, n.Y);
            var dx = s.X - p.X;
            var dy = s.Y - p.Y;
            var d  = dx * dx + dy * dy;
            if (d <= bestDist) { bestDist = d; best = n; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;

        Focus();

        if (e.ClickCount == 2)
        {
            var hit = HitTest(p.Position);
            if (hit is not null && ActivateCommand?.CanExecute(hit.Id) == true)
                ActivateCommand.Execute(hit.Id);
            e.Handled = true;
            return;
        }

        _dragFrom  = p.Position;
        _dragging  = true;
        _dragMoved = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_dragging)
        {
            var dx = pos.X - _dragFrom.X;
            var dy = pos.Y - _dragFrom.Y;
            // A few pixels of travel while clicking is normal; only treat it as a pan beyond
            // that, so a slightly shaky click still selects rather than silently moving the map.
            if (!_dragMoved && dx * dx + dy * dy < 9) return;

            _dragMoved = true;
            _cx -= dx / _scale;
            _cy -= dy / _scale;
            _dragFrom = pos;
            InvalidateVisual();
            return;
        }

        var hit = HitTest(pos);
        _hoverAt = pos;
        if (!ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
        else if (hit is not null) InvalidateVisual();   // keep the tooltip glued to the cursor
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);

        if (_dragMoved) return;

        var hit = HitTest(e.GetPosition(this));
        if (hit is not null) SelectedId = hit.Id;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Graph is null) return;

        var pos    = e.GetPosition(this);
        var factor = Math.Pow(1.15, e.Delta.Y);
        var target = Math.Clamp(_scale * factor, _fitScale * 0.4, _fitScale * 60);
        if (Math.Abs(target - _scale) < double.Epsilon) return;

        // Keep the world point under the cursor pinned there, so zooming follows the mouse
        // instead of always pulling toward the centre.
        var wx = (pos.X - Bounds.Width  / 2) / _scale + _cx;
        var wy = (pos.Y - Bounds.Height / 2) / _scale + _cy;
        _scale = target;
        _cx = wx - (pos.X - Bounds.Width  / 2) / _scale;
        _cy = wy - (pos.Y - Bounds.Height / 2) / _scale;

        e.Handled = true;
        InvalidateVisual();
    }
}
