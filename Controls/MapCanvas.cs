using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using EveConsole.Services;

namespace EveConsole.Controls;

/// <summary>
/// How one node should be painted. Supplied by the view model so the overlay logic (security,
/// sovereignty, kill activity, ...) stays out of the control.
/// </summary>
/// <param name="Fill">Node colour for the current overlay.</param>
/// <param name="Caption">Second line inside the node box — whatever the current overlay is
/// measuring (constellation, security, kill count). Shown under the dot at low zoom.</param>
/// <param name="Detail">Longer text for the hover tooltip.</param>
public sealed record MapNodeStyle(Color Fill, string? Caption = null, string? Detail = null);

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
    /// <summary>
    /// A rectangle in map coordinates to zoom and centre on. Consumed once and reset to null, so
    /// the same area can be asked for again later — and so panning away afterwards is not undone
    /// on the next repaint.
    /// </summary>
    public static readonly StyledProperty<Rect?> FocusBoundsProperty =
        AvaloniaProperty.Register<MapCanvas, Rect?>(nameof(FocusBounds));

    public Rect? FocusBounds
    {
        get => GetValue(FocusBoundsProperty);
        set => SetValue(FocusBoundsProperty, value);
    }

    /// <summary>
    /// What each system offers, drawn beside its box. Deliberately separate from
    /// <see cref="Overlay"/>: these are facts about the place, and switching what the map is
    /// colouring for must not take them away.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, MapBadges>?> BadgesProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyDictionary<int, MapBadges>?>(nameof(Badges));

    public IReadOnlyDictionary<int, MapBadges>? Badges
    {
        get => GetValue(BadgesProperty);
        set => SetValue(BadgesProperty, value);
    }

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
        AffectsRender<MapCanvas>(GraphProperty, OverlayProperty, SelectedIdProperty, BadgesProperty);
    }

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Brushes and pens (immutable, allocated once) ─────────────────────────

    private static readonly IBrush BackBrush     = new ImmutableSolidColorBrush(Color.Parse("#0b0b10"));
    private static readonly IPen   EdgePen       = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a3a4e")), 1);
    private static readonly IPen   NodePen       = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#0b0b10")), 1.5);
    private static readonly IPen   SelectedPen   = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#e8c86a")), 2);
    private static readonly IPen   HoverPen      = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#7fb8d8")), 1.5);
    private static readonly IBrush LabelBrush    = new ImmutableSolidColorBrush(Color.Parse("#b8b8c8"));
    private static readonly IBrush BadgeBrush    = new ImmutableSolidColorBrush(Color.Parse("#e8c86a"));
    private static readonly IBrush TipBackBrush  = new ImmutableSolidColorBrush(Color.Parse("#e6141420"));
    private static readonly IPen   TipPen        = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a3a4e")), 1);
    private static readonly IBrush TipTextBrush  = new ImmutableSolidColorBrush(Color.Parse("#d8d8e4"));
    private static readonly Color  DefaultFill   = Color.Parse("#6a6a80");

    // Gateways to neighbouring regions: a box rather than a dot, so they read as an exit from
    // the map rather than as one more system on it.
    private static readonly IBrush GateBackBrush   = new ImmutableSolidColorBrush(Color.Parse("#1e2630"));
    private static readonly IPen   GatePen         = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#5a7d99")), 1);
    private static readonly IBrush GateSysBrush    = new ImmutableSolidColorBrush(Color.Parse("#c2ccd6"));
    private static readonly IBrush GateRegionBrush = new ImmutableSolidColorBrush(Color.Parse("#79b0d8"));
    private static readonly IPen   GateEdgePen     = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3f5a6e")), 1);

    private static readonly IBrush DarkInk      = new ImmutableSolidColorBrush(Color.Parse("#101018"));
    private static readonly IBrush DarkInkSoft  = new ImmutableSolidColorBrush(Color.Parse("#99101018"));
    private static readonly IBrush LightInk     = new ImmutableSolidColorBrush(Color.Parse("#f2f2f7"));
    private static readonly IBrush LightInkSoft = new ImmutableSolidColorBrush(Color.Parse("#bbf2f2f7"));
    private static readonly IPen   BoxPen       = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#66000000")), 1);

    private static readonly Typeface Face     = Typeface.Default;
    private static readonly Typeface BoldFace =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private const double NodeRadius = 5.0;
    private const double LabelSize  = 10.0;
    private const double HitRadius  = 9.0;

    // Systems switch from dots to labelled boxes once there is room for the boxes, judged by
    // how far apart neighbouring systems actually are on screen rather than by a fixed zoom
    // level — that way a sparse region and a dense one both switch when they look ready.
    // The switch is a clean cutover: crossfading the two forms meant a zoom level where every
    // system was drawn twice, which just looked like a rendering fault.
    // Three thresholds on the same measure — pixels between neighbouring systems — so the map
    // gains detail in steps rather than all at once: bare dots, then names, then boxes.
    private const double BoxThreshold = 88;   // px between neighbours: dots below, boxes above
    private const double DotLabelMin  = 52;   // px: below this even the dot labels are noise

    /// <summary>
    /// The same crossover for the region tier, and much lower on purpose. There are 70 regions
    /// rather than five thousand systems, and their names are the entire point of the zoomed-out
    /// view — a cluster of unlabelled dots says nothing. Low enough that the regions are already
    /// boxed at the zoom the map opens at, and stay boxed some way further out.
    /// </summary>
    private const double RegionBoxThreshold = 34;

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

    /// <summary>System name and region name for each gateway node.</summary>
    private Dictionary<int, (FormattedText Sys, FormattedText Region)> _gateLabels = new();

    /// <summary>Name and caption drawn inside each system box. Text colour depends on the fill,
    /// so this is rebuilt whenever the overlay changes, not only when the graph does.</summary>
    private Dictionary<int, (FormattedText Name, FormattedText? Caption)> _boxLabels = new();
    private IReadOnlyDictionary<int, MapNodeStyle>? _builtOverlay;
    private MapGraph?                               _builtBoxGraph;

    /// <summary>Median distance from a node to its nearest neighbour, in world units. Multiplied
    /// by the scale it gives on-screen spacing, which drives the dot/box crossfade.</summary>
    private double _spacing = 1;

    /// <summary>Spacing within each zoom tier of a continuous graph, kept apart because the two
    /// are orders of magnitude different and one figure cannot drive both.</summary>
    private double _spacingTier0 = 1, _spacingTier1 = 1;

    /// <summary>Tier currently being drawn: 0 regions, 1 systems. Always 0 for a single-tier graph.</summary>
    private int _activeTier;

    /// <summary>
    /// On-screen gap between systems, in pixels, at which the map stops showing regions and
    /// starts showing the systems inside them.
    ///
    /// <para>Sits below <see cref="DotLabelMin"/> on purpose, so systems arrive as bare dots and
    /// only gain names once there is room for them — three steps rather than a single change
    /// from labelled regions to labelled systems.</para>
    /// </summary>
    private const double TierSwitchPx = 40;

    /// <summary>Screen rectangles of the gateway boxes from the last paint, so hit-testing
    /// matches what is actually drawn instead of assuming a dot-sized target.</summary>
    private readonly Dictionary<int, Rect> _gateRects = new();

    /// <summary>Same, for system boxes. Only populated while the boxes are being drawn.</summary>
    private readonly Dictionary<int, Rect> _nodeRects = new();

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
        else if (change.Property == FocusBoundsProperty && FocusBounds is { } area)
        {
            // Held rather than applied here: framing needs the control's final bounds, which are
            // not known when the property is set.
            _pendingFocus = area;
            _needsFit     = false;
            InvalidateVisual();
        }
    }

    /// <summary>Area waiting to be framed on the next paint.</summary>
    private Rect? _pendingFocus;

    /// <summary>Centres on an area and zooms so it fills most of the view.</summary>
    private void ApplyFocus(Rect area)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        _cx = area.X + area.Width  / 2;
        _cy = area.Y + area.Height / 2;

        var sx = area.Width  > 0 ? Bounds.Width  * 0.80 / area.Width  : double.MaxValue;
        var sy = area.Height > 0 ? Bounds.Height * 0.80 / area.Height : double.MaxValue;

        var scale = Math.Min(sx, sy);
        if (!double.IsInfinity(scale) && scale > 0 && scale != double.MaxValue) _scale = scale;

        // Cleared so the same region can be asked for again, and so a later pan is not snapped
        // back on the next repaint.
        SetCurrentValue(FocusBoundsProperty, null);
    }

    /// <summary>Frames the entire graph with a small margin. Deferred to render time because it
    /// needs the final bounds, which are not known when the graph is assigned.</summary>
    private void Fit()
    {
        var g = Graph;
        // On a continuous map, frame the regions: their extent is the cluster, and fitting to
        // every system would open at a zoom where the system tier is already showing.
        var nodes = g is { IsContinuous: true }
            ? g.Nodes.Where(n => n.Tier == 0).ToList()
            : g?.Nodes;

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
        _built = g;
        _byId  = g.Nodes.ToDictionary(n => n.Id);

        _labels = g.Nodes.Where(n => !n.IsOutsideRegion).ToDictionary(n => n.Id, n =>
            new FormattedText(n.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                              Face, LabelSize, LabelBrush));

        _gateLabels = g.Nodes.Where(n => n.IsOutsideRegion).ToDictionary(n => n.Id, n =>
        (
            Sys: new FormattedText(n.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   Face, LabelSize, GateSysBrush),
            Region: new FormattedText(n.RegionName, CultureInfo.CurrentCulture,
                                      FlowDirection.LeftToRight, BoldFace, LabelSize - 1, GateRegionBrush)
        ));

        _gateRects.Clear();
        if (g.IsContinuous)
        {
            _spacingTier0 = MedianNearestNeighbour(g.Nodes.Where(n => n.Tier == 0).ToList());

            // ⚠️ Area estimate, not the median, for the system tier. MedianNearestNeighbour is
            // O(n²) and that tier is every system in known space — thirty million distance tests
            // on the UI thread each time the graph is set. The estimate is coarser than the map
            // can show and costs one pass.
            _spacingTier1 = EstimateSpacing(g.Nodes.Where(n => n.Tier == 1).ToList());
            _spacing      = _spacingTier0;
        }
        else
        {
            _spacing = MedianNearestNeighbour(g.Nodes);
        }
    }

    /// <summary>Spacing from the area the nodes cover and how many there are — O(n), for node
    /// counts where the exact median is not worth its cost.</summary>
    private static double EstimateSpacing(IReadOnlyList<MapNode> nodes)
    {
        if (nodes.Count < 2) return 1;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            if (n.X < minX) minX = n.X;
            if (n.X > maxX) maxX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.Y > maxY) maxY = n.Y;
        }

        var area = (maxX - minX) * (maxY - minY);
        return area > 0 ? Math.Sqrt(area / nodes.Count) : 1;
    }

    /// <summary>
    /// Typical gap between adjacent nodes. The median of each node's nearest neighbour rather
    /// than the average, so a couple of isolated systems cannot drag the estimate out and delay
    /// the switch to boxes for the whole map.
    /// </summary>
    private static double MedianNearestNeighbour(IReadOnlyList<MapNode> nodes)
    {
        if (nodes.Count < 2) return 1;

        var nearest = new List<double>(nodes.Count);
        foreach (var a in nodes)
        {
            var best = double.MaxValue;
            foreach (var b in nodes)
            {
                if (ReferenceEquals(a, b)) continue;
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                var d  = dx * dx + dy * dy;
                if (d < best) best = d;
            }
            if (best is > 0 and < double.MaxValue) nearest.Add(Math.Sqrt(best));
        }

        if (nearest.Count == 0) return 1;
        nearest.Sort();
        return nearest[nearest.Count / 2];
    }

    /// <summary>
    /// Drops the in-box text so it is rebuilt on demand. Separate from the graph cache because
    /// the ink colour is chosen against the overlay fill, so changing overlay alone invalidates
    /// it.
    ///
    /// <para>⚠️ Cleared rather than rebuilt. Building every label up front meant laying out text
    /// for every node in the graph, which on the continuous map is every system in known space —
    /// thousands of FormattedText objects on the UI thread, for the handful that are both zoomed
    /// in far enough to be boxes and inside the viewport. They are now made as they are first
    /// drawn.</para>
    /// </summary>
    private void RebuildBoxLabels(MapGraph g, IReadOnlyDictionary<int, MapNodeStyle>? overlay)
    {
        _builtOverlay = overlay;
        _boxLabels    = new Dictionary<int, (FormattedText, FormattedText?)>();
    }

    /// <summary>The label pair for a node, laid out on first use and kept until the graph or the
    /// overlay changes.</summary>
    private (FormattedText Name, FormattedText? Caption) BoxLabel(MapNode n, MapNodeStyle? style)
    {
        if (_boxLabels.TryGetValue(n.Id, out var cached)) return cached;

        var ink  = PickInk(style?.Fill ?? DefaultFill);
        var name = new FormattedText(n.Name, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, BoldFace, LabelSize, ink.Strong);

        FormattedText? caption = null;
        if (style?.Caption is { Length: > 0 } text)
            caption = new FormattedText(text, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Face, LabelSize - 1.5, ink.Soft);

        var pair = (name, caption);
        _boxLabels[n.Id] = pair;
        return pair;
    }

    /// <summary>
    /// Text colour for a given fill. The security ramp runs from cyan through green and yellow
    /// to red, so a single fixed ink is unreadable at one end or the other — this picks dark
    /// text on light fills and light text on dark ones.
    /// </summary>
    private static (IBrush Strong, IBrush Soft) PickInk(Color fill)
    {
        // Rec. 709 relative luminance, which tracks perceived brightness far better than a
        // plain RGB average — yellow and blue of equal average look nothing alike.
        var l = (0.2126 * fill.R + 0.7152 * fill.G + 0.0722 * fill.B) / 255.0;
        return l > 0.55 ? (DarkInk, DarkInkSoft) : (LightInk, LightInkSoft);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(BackBrush, new Rect(Bounds.Size));

        var g = Graph;
        if (g is null || g.Nodes.Count == 0) return;

        var overlay = Overlay;

        if (!ReferenceEquals(g, _built)) RebuildCaches(g);
        if (!ReferenceEquals(g, _builtBoxGraph) || !ReferenceEquals(overlay, _builtOverlay))
        {
            _builtBoxGraph = g;
            RebuildBoxLabels(g, overlay);
        }
        if (_pendingFocus is { } area) { ApplyFocus(area); _pendingFocus = null; }
        else if (_needsFit) Fit();

        // On a continuous map the zoom decides which tier is on screen: regions until the
        // systems inside them have room to be told apart, systems from then on. One or the
        // other, never both — overlapping tiers read as a rendering fault rather than as detail.
        _activeTier = g.IsContinuous && _spacingTier1 * _scale >= TierSwitchPx ? 1 : 0;

        var spacing = g.IsContinuous
            ? (_activeTier == 1 ? _spacingTier1 : _spacingTier0)
            : _spacing;

        // Edges first so nodes sit on top of them.
        foreach (var e in g.Edges)
        {
            if (g.IsContinuous && e.Tier != _activeTier) continue;
            if (!_byId.TryGetValue(e.FromId, out var a) || !_byId.TryGetValue(e.ToId, out var b)) continue;
            var pa = ToScreen(a.X, a.Y);
            var pb = ToScreen(b.X, b.Y);

            // Both ends off screen means the line cannot cross it either.
            if ((pa.X < -90 && pb.X < -90) || (pa.Y < -90 && pb.Y < -90) ||
                (pa.X > Bounds.Width + 90 && pb.X > Bounds.Width + 90) ||
                (pa.Y > Bounds.Height + 90 && pb.Y > Bounds.Height + 90)) continue;

            ctx.DrawLine(a.IsOutsideRegion || b.IsOutsideRegion ? GateEdgePen : EdgePen, pa, pb);
        }

        // How much room neighbouring systems have on screen decides the representation: dots
        // when they are packed together, labelled boxes once they are far enough apart. One or
        // the other, never both.
        var spacingPx     = spacing * _scale;
        var onRegionTier  = g.IsContinuous && _activeTier == 0;
        var useBoxes      = spacingPx >= (onRegionTier ? RegionBoxThreshold : BoxThreshold);
        var showDotLabels = spacingPx >= (onRegionTier ? RegionBoxThreshold : DotLabelMin);

        _gateRects.Clear();
        _nodeRects.Clear();

        foreach (var n in g.Nodes)
        {
            if (g.IsContinuous && n.Tier != _activeTier) continue;

            var p = ToScreen(n.X, n.Y);

            // Skip anything scrolled well outside the viewport — at high zoom this is most
            // of the graph.
            if (p.X < -90 || p.Y < -90 || p.X > Bounds.Width + 90 || p.Y > Bounds.Height + 90) continue;

            if (n.IsOutsideRegion) { DrawGateway(ctx, n, p); continue; }

            var style = overlay is not null && overlay.TryGetValue(n.Id, out var s) ? s : null;
            var fill  = style?.Fill ?? DefaultFill;

            if (useBoxes) DrawSystemBox(ctx, n, p, fill, style);
            else          DrawDot(ctx, n, p, fill, style, showDotLabels);
        }

        if (_hover is not null) DrawTooltip(ctx, _hover);
    }

    private void DrawDot(
        DrawingContext ctx, MapNode n, Point p, Color fill, MapNodeStyle? style, bool labels)
    {
        ctx.DrawEllipse(new ImmutableSolidColorBrush(fill), NodePen, p, NodeRadius, NodeRadius);

        if (n.Id == SelectedId)      ctx.DrawEllipse(null, SelectedPen, p, NodeRadius + 4, NodeRadius + 4);
        else if (_hover?.Id == n.Id) ctx.DrawEllipse(null, HoverPen,    p, NodeRadius + 3, NodeRadius + 3);

        if (!labels) return;

        if (_labels.TryGetValue(n.Id, out var label))
            ctx.DrawText(label, new Point(p.X + NodeRadius + 3, p.Y - label.Height / 2));

        if (style?.Caption is { Length: > 0 } caption)
        {
            var bt = new FormattedText(caption, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Face, LabelSize - 1.5, BadgeBrush);
            ctx.DrawText(bt, new Point(p.X - bt.Width / 2, p.Y + NodeRadius + 1));
        }
    }

    /// <summary>
    /// The close-up form: a rounded box filled with the overlay colour, holding the system name
    /// and whatever the current overlay is measuring.
    /// </summary>
    private void DrawSystemBox(DrawingContext ctx, MapNode n, Point p, Color fill, MapNodeStyle? style)
    {
        var text = BoxLabel(n, style);

        const double padX = 6, padY = 3;
        var w = Math.Max(text.Name.Width, text.Caption?.Width ?? 0) + padX * 2;
        var h = text.Name.Height + (text.Caption?.Height ?? 0) + padY * 2;
        var rect = new Rect(p.X - w / 2, p.Y - h / 2, w, h);

        _nodeRects[n.Id] = rect;

        var radius = Math.Min(7, h / 2);
        ctx.DrawRectangle(new ImmutableSolidColorBrush(fill), BoxPen, new RoundedRect(rect, radius));

        ctx.DrawText(text.Name, new Point(p.X - text.Name.Width / 2, rect.Y + padY));
        if (text.Caption is not null)
            ctx.DrawText(text.Caption,
                new Point(p.X - text.Caption.Width / 2, rect.Y + padY + text.Name.Height));

        if (n.Id == SelectedId)
            ctx.DrawRectangle(null, SelectedPen, new RoundedRect(rect.Inflate(3), radius + 3));
        else if (_hover?.Id == n.Id)
            ctx.DrawRectangle(null, HoverPen, new RoundedRect(rect.Inflate(2), radius + 2));

        if (Badges?.TryGetValue(n.Id, out var badges) == true && badges.Any)
            DrawBadges(ctx, badges, rect);
    }

    // Badge colours. Docking capability leads, because it is the one that decides whether you can
    // bring the ship you are flying: gold for a Keepstar (supers and titans), orange for anything
    // else a capital can dock at, grey-blue for a subcap-only citadel.
    private static readonly IBrush BadgeKeepstar = new ImmutableSolidColorBrush(Color.Parse("#e8c86a"));
    private static readonly IBrush BadgeCapital  = new ImmutableSolidColorBrush(Color.Parse("#e08a3c"));
    private static readonly IBrush BadgeSubcap   = new ImmutableSolidColorBrush(Color.Parse("#7f93a8"));
    private static readonly IBrush BadgeIndustry = new ImmutableSolidColorBrush(Color.Parse("#5fa8d3"));
    private static readonly IBrush BadgeRefinery = new ImmutableSolidColorBrush(Color.Parse("#6bbf8a"));
    private static readonly IPen   BadgePen      = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#0b0b10")), 1);

    /// <summary>
    /// A column of small squares against the right edge of the system box, in the manner of the
    /// in-game and Dotlan maps. Drawn from the box rectangle rather than the node point so they
    /// sit against the box whatever its width, and outside it so they never cover the name.
    /// </summary>
    private void DrawBadges(DrawingContext ctx, MapBadges b, Rect box)
    {
        const double size = 5, gap = 1.5;

        var marks = new List<IBrush>(4);

        // One docking mark, the best available — three separate marks for a system with all
        // three would say less, not more.
        if (b.Keepstar)                       marks.Add(BadgeKeepstar);
        else if (b.Fortizar || b.NpcStation)  marks.Add(BadgeCapital);
        else if (b.Astrahus)                  marks.Add(BadgeSubcap);

        if (b.EngineeringComplex) marks.Add(BadgeIndustry);
        if (b.Refinery)           marks.Add(BadgeRefinery);
        if (marks.Count == 0) return;

        var totalH = marks.Count * size + (marks.Count - 1) * gap;
        var x = box.Right + 3;
        var y = box.Y + (box.Height - totalH) / 2;

        foreach (var brush in marks)
        {
            ctx.DrawRectangle(brush, BadgePen, new Rect(x, y, size, size));
            y += size + gap;
        }
    }

    /// <summary>
    /// A two-line box naming the system and the region it leads to. Sized in screen space, so
    /// it stays readable at any zoom, and recorded in <see cref="_gateRects"/> so clicks match
    /// the box rather than a dot at its centre.
    /// </summary>
    private void DrawGateway(DrawingContext ctx, MapNode n, Point p)
    {
        if (!_gateLabels.TryGetValue(n.Id, out var text)) return;

        const double padX = 5, padY = 3;
        var w = Math.Max(text.Sys.Width, text.Region.Width) + padX * 2;
        var h = text.Sys.Height + text.Region.Height + padY * 2;
        var rect = new Rect(p.X - w / 2, p.Y - h / 2, w, h);

        _gateRects[n.Id] = rect;

        ctx.DrawRectangle(GateBackBrush, GatePen, new RoundedRect(rect, 2));
        ctx.DrawText(text.Sys,    new Point(p.X - text.Sys.Width / 2,    rect.Y + padY));
        ctx.DrawText(text.Region, new Point(p.X - text.Region.Width / 2, rect.Y + padY + text.Sys.Height));

        if (n.Id == SelectedId)
            ctx.DrawRectangle(null, SelectedPen, new RoundedRect(rect.Inflate(3), 3));
        else if (_hover?.Id == n.Id)
            ctx.DrawRectangle(null, HoverPen, new RoundedRect(rect.Inflate(2), 3));
    }

    private void DrawTooltip(DrawingContext ctx, MapNode n)
    {
        var style  = Overlay is not null && Overlay.TryGetValue(n.Id, out var s) ? s : null;
        var detail = style?.Detail;
        // The box already names the region, so the tooltip explains the gesture instead.
        if (n.IsOutsideRegion) detail = $"Double-click to open {n.RegionName}";

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

        // Boxes are much larger than a node dot, so they are tested against the rectangle
        // actually painted. Checked first: a box may well cover a nearby system's centre.
        foreach (var (id, rect) in _gateRects)
            if (rect.Contains(p) && _byId.TryGetValue(id, out var gate)) return gate;

        foreach (var (id, rect) in _nodeRects)
            if (rect.Contains(p) && _byId.TryGetValue(id, out var node)) return node;

        MapNode? best = null;
        var bestDist = HitRadius * HitRadius;
        foreach (var n in g.Nodes)
        {
            if (n.IsOutsideRegion) continue;

            // Only what is actually on screen can be hit — otherwise a hidden system's centre
            // could win over the region box drawn on top of it.
            if (g.IsContinuous && n.Tier != _activeTier) continue;

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
