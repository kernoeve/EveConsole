using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using EveConsole.Services;
using EveConsole.Localization;

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
/// Live marks on one node: hostiles believed to be there now, and the user's own characters.
/// Each carries the text its hover shows, so the canvas stays ignorant of intel.
/// </summary>
public sealed record MapMarkers(
    int Hostiles, string? HostileTitle, string? HostileDetail,
    int Own,      string? OwnTitle,     string? OwnDetail,
    IReadOnlyList<MapMarkRow>? HostileRows = null,
    IReadOnlyList<MapMarkRow>? OwnRows     = null);

/// <summary>One line of a mark's hover: the pilot's portrait in front of it, and the ship's icon
/// just before the ship's name in it.</summary>
/// <param name="ShipAt">Where in <see cref="Text"/> the ship's name starts, so the icon goes
/// beside the name whichever order a language puts the words in; -1 for no ship.</param>
public sealed record MapMarkRow(string Text, long CharacterId = 0, int ShipTypeId = 0, int ShipAt = -1);

/// <summary>A jump bridge drawn as an arc between two systems, and what its hover says.</summary>
/// <param name="Complete">Both gates are known; false draws it fainter.</param>
/// <param name="ZoneFrom">The zone of the half at <see cref="FromId"/>: the zone a jump landing
/// there is in. 0 when not known, drawn in the plain bridge colour.</param>
/// <param name="ZoneTo">The same for the half at <see cref="ToId"/>.</param>
public sealed record MapBridgeLine(int FromId, int ToId, string Title, string Detail, bool Complete,
                                   int ZoneFrom = 0, int ZoneTo = 0);

/// <summary>A wormhole mark on a node: the glyph shown (Θ for Thera, T for Turnur), and its hover.</summary>
public sealed record MapHoleMark(string Glyph, string Title, string Detail);

/// <summary>A sovereignty campaign mark on a node, and its hover. <paramref name="Running"/>: the
/// fight has started, and the node is ringed.</summary>
public sealed record MapCampaignMark(string Title, string Detail, bool Running);

/// <summary>A wormhole drawn as a line between two systems on the map — Turnur's, since Thera is
/// not on it.</summary>
public sealed record MapHoleLink(int FromId, int ToId);

/// <summary>One system of a planned route, and whether it was reached by something other than a
/// stargate — a jump bridge or a wormhole — which is drawn dashed.</summary>
public sealed record MapRouteStep(int SystemId, int RegionId, bool Jumped);

/// <summary>Where the view is looking: the world point at its centre and the zoom. Held by the
/// view model so a tab keeps its place when the view is rebuilt.</summary>
public sealed record MapCamera(double CenterX, double CenterY, double Scale);

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

    /// <summary>Hostiles and own characters per node id (systems, and regions on the zoomed-out
    /// tier). Drawn on every form of node — dot, box, region — since "somebody is there" matters
    /// at any zoom.</summary>
    /// <summary>Jump bridges, drawn as arcs between their systems once the map shows systems.</summary>
    public static readonly StyledProperty<IReadOnlyList<MapBridgeLine>?> BridgesProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyList<MapBridgeLine>?>(nameof(Bridges));

    public IReadOnlyList<MapBridgeLine>? Bridges
    {
        get => GetValue(BridgesProperty);
        set => SetValue(BridgesProperty, value);
    }

    /// <summary>Planned routes, each start first — one per tool that planned one (the route
    /// planner, the jump planner) — drawn over the gates and under the systems.</summary>
    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<MapRouteStep>>?> RoutesProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyList<IReadOnlyList<MapRouteStep>>?>(nameof(Routes));

    public IReadOnlyList<IReadOnlyList<MapRouteStep>>? Routes
    {
        get => GetValue(RoutesProperty);
        set => SetValue(RoutesProperty, value);
    }

    /// <summary>Thera and Turnur wormholes per node id (systems, and regions on the zoomed-out tier).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, MapHoleMark>?> HolesProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyDictionary<int, MapHoleMark>?>(nameof(Holes));

    public IReadOnlyDictionary<int, MapHoleMark>? Holes
    {
        get => GetValue(HolesProperty);
        set => SetValue(HolesProperty, value);
    }

    /// <summary>Sovereignty campaigns per node id (systems, and regions zoomed out).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, MapCampaignMark>?> CampaignsProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyDictionary<int, MapCampaignMark>?>(nameof(Campaigns));

    public IReadOnlyDictionary<int, MapCampaignMark>? Campaigns
    {
        get => GetValue(CampaignsProperty);
        set => SetValue(CampaignsProperty, value);
    }

    /// <summary>Metaliminal storms per node id (systems, and regions zoomed out).</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, MapHoleMark>?> StormsProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyDictionary<int, MapHoleMark>?>(nameof(Storms));

    public IReadOnlyDictionary<int, MapHoleMark>? Storms
    {
        get => GetValue(StormsProperty);
        set => SetValue(StormsProperty, value);
    }

    /// <summary>Wormholes between two systems the map shows, drawn as dashed lines.</summary>
    public static readonly StyledProperty<IReadOnlyList<MapHoleLink>?> HoleLinksProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyList<MapHoleLink>?>(nameof(HoleLinks));

    public IReadOnlyList<MapHoleLink>? HoleLinks
    {
        get => GetValue(HoleLinksProperty);
        set => SetValue(HoleLinksProperty, value);
    }

    /// <summary>Systems on the route avoid list, ringed in red where systems are drawn.</summary>
    public static readonly StyledProperty<IReadOnlyCollection<int>?> AvoidedProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyCollection<int>?>(nameof(Avoided));

    public IReadOnlyCollection<int>? Avoided
    {
        get => GetValue(AvoidedProperty);
        set => SetValue(AvoidedProperty, value);
    }

    public static readonly StyledProperty<IReadOnlyDictionary<int, MapMarkers>?> MarkersProperty =
        AvaloniaProperty.Register<MapCanvas, IReadOnlyDictionary<int, MapMarkers>?>(nameof(Markers));

    public IReadOnlyDictionary<int, MapMarkers>? Markers
    {
        get => GetValue(MarkersProperty);
        set => SetValue(MarkersProperty, value);
    }

    /// <summary>
    /// The view's position, two-way. Written back after every pan, zoom and framing, and applied
    /// when set from outside — so a map tab rebuilt after moving to the other side of a split, or
    /// after switching tabs, comes back where it was instead of re-framing all of New Eden.
    /// </summary>
    public static readonly StyledProperty<MapCamera?> CameraProperty =
        AvaloniaProperty.Register<MapCanvas, MapCamera?>(
            nameof(Camera), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public MapCamera? Camera
    {
        get => GetValue(CameraProperty);
        set => SetValue(CameraProperty, value);
    }

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
        AffectsRender<MapCanvas>(GraphProperty, OverlayProperty, SelectedIdProperty, BadgesProperty, MarkersProperty, BridgesProperty, RoutesProperty, AvoidedProperty, HolesProperty, HoleLinksProperty, StormsProperty, CampaignsProperty);
    }

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Names in the interface language ──────────────────────────────────────
    //
    // Labels are laid out once per graph, so names that arrive after that — a slow first load, or
    // an SDE import — would otherwise never reach the map. Listened to only while on screen: the
    // event is static, and holding on would keep every closed map alive.

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SdeNames.Changed += OnSdeNamesChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SdeNames.Changed -= OnSdeNamesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Raised on a background thread, so the relayout is posted to the UI thread.</summary>
    private void OnSdeNamesChanged() => Dispatcher.UIThread.Post(() =>
    {
        _built         = null;
        _builtBoxGraph = null;
        InvalidateVisual();
    });

    // ── Brushes and pens (immutable, allocated once) ─────────────────────────

    private static IPen? _edgePen;
    private static IPen? _gateEdgePen;
    private static IPen? _gatePen;
    private static IPen? _hoverPen;
    private static IPen? _nodePen;
    private static IPen? _selectedPen;
    private static IPen? _tipPen;
    private static IBrush BackBrush     => Palette.SurfaceBase;
    private static IPen   EdgePen       => _edgePen ??= new Pen(Palette.BorderDefault, 1);
    private static IPen   NodePen       => _nodePen ??= new Pen(Palette.SurfaceBase, 1.5);
    private static IPen   SelectedPen   => _selectedPen ??= new Pen(Palette.Accent, 2);
    private static IPen   HoverPen      => _hoverPen ??= new Pen(Palette.Info, 1.5);
    private static IBrush LabelBrush    => Palette.TextSecondary;
    private static IBrush BadgeBrush    => Palette.Accent;
    private static IBrush TipBackBrush  => Palette.SurfaceOverlayStrong;
    private static IPen   TipPen        => _tipPen ??= new Pen(Palette.BorderDefault, 1);
    private static IBrush TipTextBrush  => Palette.TextPrimary;
    private static readonly Color  DefaultFill   = Color.Parse("#6a6a80");

    // Gateways to neighbouring regions: a box rather than a dot, so they read as an exit from
    // the map rather than as one more system on it.
    private static IBrush GateBackBrush   => Palette.SurfacePanel;
    private static IPen   GatePen         => _gatePen ??= new Pen(Palette.BorderStrong, 1);
    private static IBrush GateSysBrush    => Palette.TextPrimary;
    private static IBrush GateRegionBrush => Palette.Info;
    private static IPen   GateEdgePen     => _gateEdgePen ??= new Pen(Palette.BorderDefault, 1);

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

    /// <summary>Half-extent of each region in world units, keyed by region node id. Sizes the
    /// watermark so a label matches the territory it names.</summary>
    private readonly Dictionary<int, double> _regionExtent = new();

    /// <summary>
    /// Where each badge was drawn last frame and what it means, so hovering one explains it.
    ///
    /// <para>Rebuilt every render alongside the node rectangles. A mark is a few pixels across and
    /// carries no text, so without this the only way to learn it is the legend — and a legend you
    /// have to look away to read is one you stop reading.</para>
    /// </summary>
    private readonly List<(Rect Rect, string Title, string Detail, IReadOnlyList<MapMarkRow>? Rows)> _badgeTips = new();

    private (string Title, string Detail, IReadOnlyList<MapMarkRow>? Rows)? _badgeHover;

    /// <summary>Same, for system boxes. Only populated while the boxes are being drawn.</summary>
    private readonly Dictionary<int, Rect> _nodeRects = new();

    private Point ToScreen(double x, double y) =>
        new((x - _cx) * _scale + Bounds.Width / 2, (y - _cy) * _scale + Bounds.Height / 2);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GraphProperty)
        {
            // A graph arriving for a view that already has a place (a rebuilt tab, a refresh)
            // keeps it; only a view with none yet frames the whole graph.
            _needsFit      = Camera is null;
            _fitScaleStale = true;
            _hover         = null;
        }
        else if (change.Property == CameraProperty && !_publishingCamera && Camera is { } cam)
        {
            _cx       = cam.CenterX;
            _cy       = cam.CenterY;
            _scale    = cam.Scale;
            _needsFit = false;
            InvalidateVisual();
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

    /// <summary>The fit scale bounds the zoom, so it is needed even when the view was restored
    /// from a camera rather than fitted. Worked out on the next paint, which knows the bounds.</summary>
    private bool _fitScaleStale = true;

    private bool _publishingCamera;

    /// <summary>Hands the current position back to the view model.</summary>
    private void PublishCamera()
    {
        _publishingCamera = true;
        try { SetCurrentValue(CameraProperty, new MapCamera(_cx, _cy, _scale)); }
        finally { _publishingCamera = false; }
    }

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
        PublishCamera();
    }

    /// <summary>Frames the entire graph with a small margin. Deferred to render time because it
    /// needs the final bounds, which are not known when the graph is assigned.</summary>
    private void Fit()
    {
        if (!ComputeFit(out var cx, out var cy)) return;
        _cx       = cx;
        _cy       = cy;
        _scale    = _fitScale;
        _needsFit = false;
        PublishCamera();
    }

    /// <summary>The framing of the whole graph, and <see cref="_fitScale"/> with it, without
    /// moving the view.</summary>
    private bool ComputeFit(out double cx, out double cy)
    {
        cx = cy = 0;
        var g = Graph;
        // On a continuous map, frame the regions: their extent is the cluster, and fitting to
        // every system would open at a zoom where the system tier is already showing.
        var nodes = g is { IsContinuous: true }
            ? g.Nodes.Where(n => n.Tier == 0).ToList()
            : g?.Nodes;

        if (nodes is null || nodes.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return false;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in nodes)
        {
            if (n.X < minX) minX = n.X;
            if (n.X > maxX) maxX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.Y > maxY) maxY = n.Y;
        }

        cx = (minX + maxX) / 2;
        cy = (minY + maxY) / 2;

        // A single-node graph, or one collapsed onto a line, has no extent on some axis;
        // fall back to a scale that at least puts it on screen rather than dividing by zero.
        var w = maxX - minX;
        var h = maxY - minY;
        var sx = w > 0 ? Bounds.Width  * 0.88 / w : double.MaxValue;
        var sy = h > 0 ? Bounds.Height * 0.88 / h : double.MaxValue;
        _fitScale = Math.Min(sx, sy);
        if (double.IsInfinity(_fitScale) || _fitScale <= 0 || _fitScale == double.MaxValue)
            _fitScale = 1;

        _fitScaleStale = false;
        return true;
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
            new FormattedText(n.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                              Face, LabelSize, LabelBrush));

        _gateLabels = g.Nodes.Where(n => n.IsOutsideRegion).ToDictionary(n => n.Id, n =>
        (
            Sys: new FormattedText(n.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                   Face, LabelSize, GateSysBrush),
            Region: new FormattedText(n.RegionLabel, CultureInfo.CurrentCulture,
                                      FlowDirection.LeftToRight, BoldFace, LabelSize - 1, GateRegionBrush)
        ));

        _gateRects.Clear();
        _regionExtent.Clear();
        if (g.IsContinuous)
        {
            // How far a region reaches, for the watermark's size. Measured from its own systems
            // rather than assumed, so Delve gets a bigger label than Pochven because it is bigger.
            var byRegion = g.Nodes.Where(n => n.Tier == 1 && n.RegionId != 0)
                                  .GroupBy(n => n.RegionId)
                                  .ToDictionary(gr => gr.Key, gr => gr.ToList());

            foreach (var r in g.Nodes.Where(n => n.Tier == 0))
                if (byRegion.TryGetValue(r.Id, out var members) && members.Count > 0)
                    _regionExtent[r.Id] = Math.Max(
                        (members.Max(m => m.X) - members.Min(m => m.X)) / 2,
                        (members.Max(m => m.Y) - members.Min(m => m.Y)) / 2);

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
        var name = new FormattedText(n.Label, CultureInfo.CurrentCulture,
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
        if (_fitScaleStale) ComputeFit(out _, out _);
        if (_pendingFocus is { } area) { ApplyFocus(area); _pendingFocus = null; }
        else if (_needsFit) Fit();

        // On a continuous map the zoom decides which tier is on screen: regions until the
        // systems inside them have room to be told apart, systems from then on. One or the
        // other, never both — overlapping tiers read as a rendering fault rather than as detail.
        _activeTier = g.IsContinuous && _spacingTier1 * _scale >= TierSwitchPx ? 1 : 0;

        var spacing = g.IsContinuous
            ? (_activeTier == 1 ? _spacingTier1 : _spacingTier0)
            : _spacing;

        // Region names, behind everything, once the map is showing systems. Zooming in no longer
        // enters a region, so without this there is nothing on screen saying which one you are
        // looking at — the boxes name systems and the breadcrumb names wherever you last clicked.
        if (g.IsContinuous && _activeTier == 1) DrawRegionWatermarks(ctx, g);

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

        // Jump bridges: over the gates, under the systems. Only where systems are drawn — a
        // bridge joins two systems, and on the region tier it would join nothing on screen.
        _bridgeHits.Clear();
        if (Bridges is { Count: > 0 } bridges && (!g.IsContinuous || _activeTier == 1))
            DrawBridges(ctx, bridges);

        // Wormhole links, like the bridges: where systems are drawn, under them.
        if (HoleLinks is { Count: > 0 } holeLinks && (!g.IsContinuous || _activeTier == 1))
            foreach (var l in holeLinks)
            {
                if (!_byId.TryGetValue(l.FromId, out var a) || !_byId.TryGetValue(l.ToId, out var b)) continue;
                ctx.DrawLine(HoleLinkPen, ToScreen(a.X, a.Y), ToScreen(b.X, b.Y));
            }

        // A planned route over both, under the systems so their names stay readable.
        if (Routes is { Count: > 0 } routes)
            foreach (var route in routes)
                if (route.Count > 0) DrawRoute(ctx, route, g.IsContinuous && _activeTier == 0);

        // How much room neighbouring systems have on screen decides the representation: dots
        // when they are packed together, labelled boxes once they are far enough apart. One or
        // the other, never both.
        var spacingPx     = spacing * _scale;
        var onRegionTier  = g.IsContinuous && _activeTier == 0;
        var useBoxes      = spacingPx >= (onRegionTier ? RegionBoxThreshold : BoxThreshold);
        var showDotLabels = spacingPx >= (onRegionTier ? RegionBoxThreshold : DotLabelMin);

        _gateRects.Clear();
        _nodeRects.Clear();
        _badgeTips.Clear();

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

            if (Markers?.TryGetValue(n.Id, out var marks) == true)
                _pendingMarks.Add((marks, useBoxes && _nodeRects.TryGetValue(n.Id, out var box)
                    ? box
                    : new Rect(p.X - NodeRadius, p.Y - NodeRadius, NodeRadius * 2, NodeRadius * 2), useBoxes));

            if (Campaigns?.TryGetValue(n.Id, out var campaign) == true)
                _pendingCampaigns.Add((campaign, useBoxes && _nodeRects.TryGetValue(n.Id, out var cbox)
                    ? cbox
                    : new Rect(p.X - NodeRadius, p.Y - NodeRadius, NodeRadius * 2, NodeRadius * 2), useBoxes));

            if (Storms?.TryGetValue(n.Id, out var storm) == true)
                _pendingStorms.Add((storm, useBoxes && _nodeRects.TryGetValue(n.Id, out var sbox)
                    ? sbox
                    : new Rect(p.X - NodeRadius, p.Y - NodeRadius, NodeRadius * 2, NodeRadius * 2), useBoxes));

            if (Holes?.TryGetValue(n.Id, out var hole) == true)
                _pendingHoles.Add((hole, useBoxes && _nodeRects.TryGetValue(n.Id, out var hbox)
                    ? hbox
                    : new Rect(p.X - NodeRadius, p.Y - NodeRadius, NodeRadius * 2, NodeRadius * 2), useBoxes));
        }

        // Avoided systems, ringed over their node, where systems are drawn.
        if (Avoided is { Count: > 0 } avoided && (!g.IsContinuous || _activeTier == 1))
            foreach (var id in avoided)
            {
                if (!_byId.TryGetValue(id, out var n)) continue;
                if (_nodeRects.TryGetValue(id, out var box))
                {
                    ctx.DrawRectangle(null, AvoidPen, box.Inflate(3), 4, 4);
                    continue;
                }
                var at = ToScreen(n.X, n.Y);
                if (at.X < -20 || at.Y < -20 || at.X > Bounds.Width + 20 || at.Y > Bounds.Height + 20) continue;
                ctx.DrawEllipse(null, AvoidPen, at, NodeRadius + 4, NodeRadius + 4);
            }

        // Live marks in a pass of their own, after every node: drawn with their node, a
        // neighbour's box painted later covered them — on the region tier, where boxes crowd,
        // most of a mark could vanish under the next region.
        foreach (var (marks, anchor, isBox) in _pendingMarks) DrawMarkers(ctx, marks, anchor, isBox);
        _pendingMarks.Clear();
        foreach (var (hole, anchor, isBox) in _pendingHoles) DrawHole(ctx, hole, anchor, isBox);
        _pendingHoles.Clear();
        foreach (var (storm, anchor, isBox) in _pendingStorms) DrawStorm(ctx, storm, anchor, isBox);
        _pendingStorms.Clear();
        foreach (var (campaign, anchor, isBox) in _pendingCampaigns) DrawCampaign(ctx, campaign, anchor, isBox);
        _pendingCampaigns.Clear();

        // A badge tooltip wins: the cursor is on the mark, so that is what the question is about.
        if (_badgeHover is { } badge)  DrawTooltipBox(ctx, badge.Title, badge.Detail, badge.Rows);
        else if (_hover is not null)   DrawTooltip(ctx, _hover);
        else if (_bridgeHover is { } bridge) DrawTooltipBox(ctx, bridge.Title, bridge.Detail);
    }

    /// <summary>Faint enough to sit under the map without competing with it. Everything drawn
    /// afterwards covers it, which is what makes a label this large usable at all.</summary>
    private static readonly IBrush WatermarkBrush =
        new ImmutableSolidColorBrush(Color.FromArgb(38, 190, 205, 235));

    /// <summary>
    /// The region name written across its own territory, behind the systems.
    ///
    /// <para>Sized from the region's real extent rather than from zoom alone, so Delve carries a
    /// larger name than Pochven and a name never spills far past the space it describes.</para>
    ///
    /// <para>⚠️ Drawn before the edges, so systems and jumps overlay it. A label at this size is
    /// only readable as a background wash; in front of the map it would be a curtain over it.</para>
    /// </summary>
    private void DrawRegionWatermarks(DrawingContext ctx, MapGraph g)
    {
        foreach (var r in g.Nodes)
        {
            if (r.Tier != 0) continue;
            if (!_regionExtent.TryGetValue(r.Id, out var extent) || extent <= 0) continue;

            // Half the region's on-screen width. Below a floor the name is unreadable anyway and
            // several would overlap; above a ceiling it stops being a label and becomes wallpaper.
            var radiusPx = extent * _scale;
            var fontSize = Math.Clamp(radiusPx * 0.42, 22, 190);
            if (radiusPx < 60) continue;

            var p = ToScreen(r.X, r.Y);

            // Generous margin: the text is centred on the point, so a region whose centre is off
            // screen can still have its name reaching into view.
            if (p.X < -1200 || p.Y < -400 || p.X > Bounds.Width + 1200 || p.Y > Bounds.Height + 400)
                continue;

            var text = new FormattedText(r.Label.ToUpperInvariant(), CultureInfo.CurrentCulture,
                                         FlowDirection.LeftToRight, BoldFace, fontSize, WatermarkBrush);

            ctx.DrawText(text, new Point(p.X - text.Width / 2, p.Y - text.Height / 2));
        }
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

    // ── Badge palette ────────────────────────────────────────────────────────
    //
    // ⚠️ Two families, kept apart by saturation as well as by side and shape: docking is
    // saturated and sits left as a bar, services are muted and sit right as squares. That is what
    // lets a violet docking bar and a lavender research square coexist without being read as the
    // same thing.
    //
    // Docking was gold and orange, which at 9px were one colour. Violet and green share no hue,
    // so the three ranks are told apart at a glance rather than by comparison — and the bar's
    // stepped height still says which is which if the colours ever fail someone.

    private static readonly IBrush DockSuper   = new ImmutableSolidColorBrush(Color.Parse("#a855f7"));
    private static readonly IBrush DockCapital = new ImmutableSolidColorBrush(Color.Parse("#22c55e"));

    // ⚠️ Lifted from #7f93a8. Muted grey-blue on a dark map, drawn 2.5px thin, read as nothing at
    // all — a system holding only a Sotiyo looked like one with no structure. "Dockable, but
    // nothing that takes a capital" is worth knowing, so the lowest rank still has to be seen.
    private static readonly IBrush DockSubcap  = new ImmutableSolidColorBrush(Color.Parse("#a8bdd4"));

    // ⚠️ Manufacturing and Reprocessing were #5fa8d3 and #6bbf8a — a sky blue leaning cyan and a
    // sea green leaning teal, which met in the middle and were hard to tell apart at 9px. Moved to
    // a true blue and a true green so roughly 90° of hue separates them instead of 50°.
    private static readonly IBrush SvcManufacturing = new ImmutableSolidColorBrush(Color.Parse("#4a7fe0"));
    private static readonly IBrush SvcResearch      = new ImmutableSolidColorBrush(Color.Parse("#9b7fd4"));
    private static readonly IBrush SvcReprocessing  = new ImmutableSolidColorBrush(Color.Parse("#5fd07a"));
    private static readonly IBrush SvcReactions     = new ImmutableSolidColorBrush(Color.Parse("#d4708a"));
    private static readonly IBrush SvcCloning       = new ImmutableSolidColorBrush(Color.Parse("#d9d2c4"));
    private static readonly IBrush SvcMarket        = new ImmutableSolidColorBrush(Color.Parse("#d8a03c"));

    private static readonly IPen BadgePen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.Parse("#0b0b10")), 1);

    /// <summary>
    /// Legend order, and the order marks are drawn in. Fixed so a given service is always in the
    /// same place on the box and can be learned by position as well as colour.
    ///
    /// <para>Detail says where the mark can have come from, which is the question the colour
    /// cannot answer: a mark on a null-sec system is somebody's fitted module, the same mark in
    /// high sec is usually the NPC station.</para>
    /// </summary>
    internal static readonly (SystemServices Service, IBrush Brush, string Label, string Detail)[] ServiceLegend =
    [
        (SystemServices.Manufacturing, SvcManufacturing, MapText.ServiceManufacturing,
            MapText.TipServiceManufacturing),
        (SystemServices.Research,      SvcResearch,      MapText.ServiceResearch,
            MapText.TipServiceResearch),
        (SystemServices.Reprocessing,  SvcReprocessing,  MapText.ServiceReprocessing,
            MapText.TipServiceReprocessing),
        (SystemServices.Reactions,     SvcReactions,     MapText.ServiceReactions,
            MapText.TipServiceReactions),
        (SystemServices.Cloning,       SvcCloning,       MapText.ServiceCloning,
            MapText.TipServiceCloning),
        (SystemServices.Market,        SvcMarket,        MapText.ServiceMarket,
            MapText.TipServiceMarket),
    ];

    internal static readonly (DockClass Dock, IBrush Brush, string Label, string Detail)[] DockLegend =
    [
        (DockClass.Super,   DockSuper,   MapText.DockSupers,
            MapText.TipDockSupers),
        (DockClass.Capital, DockCapital, MapText.DockCapitals,
            MapText.TipDockCapitals),
        (DockClass.Subcap,  DockSubcap,  MapText.DockSubcaps,
            MapText.TipDockSubcaps),
    ];

    private static IBrush? DockBrush(DockClass d) => d switch
    {
        DockClass.Super   => DockSuper,
        DockClass.Capital => DockCapital,
        DockClass.Subcap  => DockSubcap,
        _                 => null,
    };

    /// <summary>
    /// Docking above the box, services below it, both outside so neither covers the name and
    /// neither depends on the overlay.
    ///
    /// <para>Docking is one bar spanning the full box width, its THICKNESS stepped by class.
    /// Sitting apart from the services it cannot be mistaken for one of them, and the thickness
    /// keeps the rank readable for anyone the colours fail.</para>
    ///
    /// <para>Services run in one row underneath. Stacked beside the box they had to wrap, which
    /// put a service's mark in a different place depending on how many others were present — and
    /// a mark you have to find is a mark you have to decode. A single row keeps each service at a
    /// fixed offset, and the box is always wider than it is tall, so six marks fit across where
    /// they never fit down.</para>
    /// </summary>
    private void DrawBadges(DrawingContext ctx, MapBadges b, Rect box)
    {
        // ⚠️ Sized to be seen, not to be tidy. At 4.5px with a gap these were invisible against a
        // busy map. Doubling to 9 and closing the gap to nothing costs no more room overall — the
        // marks are their own separators, since each carries a dark outline.
        const double size = 9, gap = 0, offset = 3;

        // ── Above: one bar the width of the box, thickness stepped by class ──
        if (DockBrush(b.Dock) is { } dockBrush)
        {
            // ⚠️ A compressed range, not a proportional one. Thickness ranks the three, but the
            // bottom of the range has to clear the floor of what registers at all: 2.5px vanished
            // on a dark map. Ordering survives the compression; visibility did not survive the
            // spread.
            var barH = b.Dock switch
            {
                DockClass.Super   => 6.0,
                DockClass.Capital => 4.5,
                _                 => 3.5,
            };
            var bar = new Rect(box.X, box.Y - offset - barH, box.Width, barH);
            ctx.DrawRectangle(dockBrush, BadgePen, bar);

            // ⚠️ The hover target is padded, not the bar. A 2.5px stripe is drawable but not
            // reliably hittable, and a mark that needs a steady hand to read is a mark nobody
            // reads. The drawn size stays honest to the rank; only the catch area grows.
            var entry = DockLegend.First(d => d.Dock == b.Dock);
            _badgeTips.Add((bar.Inflate(new Thickness(0, 4)), entry.Label, entry.Detail, null));
        }

        // ── Below: services, one row, in fixed legend order ──
        var marks = ServiceLegend.Where(s => b.Services.HasFlag(s.Service)).ToList();
        if (marks.Count == 0) return;

        // Centred under the box rather than left-aligned to it: the box width varies with the
        // system name, and a row hung off one edge would drift relative to the name above it.
        var rowW = marks.Count * size + (marks.Count - 1) * gap;
        var x0   = box.X + (box.Width - rowW) / 2;
        var y0   = box.Bottom + offset;

        for (var i = 0; i < marks.Count; i++)
        {
            var r = new Rect(x0 + i * (size + gap), y0, size, size);
            ctx.DrawRectangle(marks[i].Brush, BadgePen, r);
            _badgeTips.Add((r, marks[i].Label, marks[i].Detail, null));
        }
    }

    // ── Jump bridges ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ansiblex zone colours, index = zone (0 = not known): blue for free through red for 15×.
    /// One table for the bridge halves, the Sovereignty zones overlay and its legend, so the
    /// three always agree.
    /// </summary>
    internal static readonly Color[] ZoneColors =
    [
        Color.Parse("#c084fc"),   // not known — the plain bridge violet
        Color.Parse("#3b82f6"),   // 1: within 5 ly, free
        Color.Parse("#22c55e"),   // 2: 2×
        Color.Parse("#eab308"),   // 3: 6×
        Color.Parse("#f97316"),   // 4: 9×
        Color.Parse("#ef4444"),   // 5: 15×
    ];

    private static readonly Dictionary<(int Zone, bool Faint), IPen> BridgePens = new();

    /// <summary>A dashed pen in a zone's colour; fainter and sparser for a one-ended bridge.</summary>
    private static IPen BridgePen(int zone, bool faint)
    {
        zone = zone is >= 0 and <= 5 ? zone : 0;
        if (BridgePens.TryGetValue((zone, faint), out var pen)) return pen;
        var c = ZoneColors[zone];
        pen = faint
            ? new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B)), 1.4, new ImmutableDashStyle([2, 4], 0))
            : new ImmutablePen(new ImmutableSolidColorBrush(c), 1.8, new ImmutableDashStyle([4, 3], 0));
        BridgePens[(zone, faint)] = pen;
        return pen;
    }

    private static readonly IPen BridgeHoverPen = new ImmutablePen(
        new ImmutableSolidColorBrush(Color.Parse("#e9d5ff")), 2.6);

    /// <summary>Points along each drawn arc, for hovering one.</summary>
    private readonly List<(MapBridgeLine Line, Point[] Points)> _bridgeHits = new();
    private MapBridgeLine? _bridgeHover;

    private static readonly IPen AvoidPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#E0FF4D4D")), 2, new ImmutableDashStyle([3, 2], 0));

    private static readonly ImmutableSolidColorBrush RouteBrush = new ImmutableSolidColorBrush(Color.Parse("#E6FFC23D"));
    private static readonly IPen   RoutePen       = new ImmutablePen(RouteBrush, 4, lineCap: PenLineCap.Round);
    private static readonly IPen   RouteJumpPen   = new ImmutablePen(RouteBrush, 3, new ImmutableDashStyle([2, 2], 0), PenLineCap.Round);
    private static readonly IPen   RouteEndPen    = new ImmutablePen(RouteBrush, 2.5);

    /// <summary>
    /// The route as one line through its systems — or, on the region tier, through the regions it
    /// passes. A hop by bridge or wormhole is dashed; a system the map does not show (Thera, out
    /// in wormhole space) is crossed by a dashed line from the system before it to the one after.
    /// The start and the end are ringed.
    /// </summary>
    private void DrawRoute(DrawingContext ctx, IReadOnlyList<MapRouteStep> route, bool regionTier)
    {
        Point? last = null;
        var dashed = false;
        int? lastId = null;
        foreach (var step in route)
        {
            var id = regionTier ? step.RegionId : step.SystemId;
            dashed |= step.Jumped;
            if (id == lastId) { dashed = false; continue; }
            if (!_byId.TryGetValue(id, out var node)) { dashed = true; continue; }

            var p = ToScreen(node.X, node.Y);
            if (last is { } from) ctx.DrawLine(dashed && !regionTier ? RouteJumpPen : RoutePen, from, p);
            last = p; lastId = id; dashed = false;
        }

        foreach (var end in new[] { route[0], route[^1] })
            if (_byId.TryGetValue(regionTier ? end.RegionId : end.SystemId, out var node))
                ctx.DrawEllipse(null, RouteEndPen, ToScreen(node.X, node.Y), NodeRadius + 6, NodeRadius + 6);
    }

    /// <summary>
    /// An arc rather than a straight line, so a bridge never lies along the gates between the
    /// same two systems, and two bridges from one system fan apart. Bowed to one side by a fifth
    /// of its length.
    /// </summary>
    private void DrawBridges(DrawingContext ctx, IReadOnlyList<MapBridgeLine> bridges)
    {
        foreach (var b in bridges)
        {
            if (!_byId.TryGetValue(b.FromId, out var from) || !_byId.TryGetValue(b.ToId, out var to)) continue;
            var pa = ToScreen(from.X, from.Y);
            var pb = ToScreen(to.X, to.Y);

            if ((pa.X < -200 && pb.X < -200) || (pa.Y < -200 && pb.Y < -200) ||
                (pa.X > Bounds.Width + 200 && pb.X > Bounds.Width + 200) ||
                (pa.Y > Bounds.Height + 200 && pb.Y > Bounds.Height + 200)) continue;

            var mid = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
            var dx  = pb.X - pa.X;
            var dy  = pb.Y - pa.Y;
            var control = new Point(mid.X - dy * 0.2, mid.Y + dx * 0.2);

            // Two halves, split at the middle of the curve: each end's half in the zone a jump
            // landing there is in, since a bridge's two directions can cost differently.
            var m     = new Point(0.25 * pa.X + 0.5 * control.X + 0.25 * pb.X, 0.25 * pa.Y + 0.5 * control.Y + 0.25 * pb.Y);
            var nearA = new Point((pa.X + control.X) / 2, (pa.Y + control.Y) / 2);
            var nearB = new Point((control.X + pb.X) / 2, (control.Y + pb.Y) / 2);
            var hover = ReferenceEquals(b, _bridgeHover);
            DrawHalf(ctx, pa, nearA, m, hover ? BridgeHoverPen : BridgePen(b.ZoneFrom, !b.Complete));
            DrawHalf(ctx, m, nearB, pb, hover ? BridgeHoverPen : BridgePen(b.ZoneTo,   !b.Complete));

            var points = new Point[17];
            for (var i = 0; i <= 16; i++)
            {
                var t = i / 16.0;
                var u = 1 - t;
                points[i] = new Point(u * u * pa.X + 2 * u * t * control.X + t * t * pb.X,
                                      u * u * pa.Y + 2 * u * t * control.Y + t * t * pb.Y);
            }
            _bridgeHits.Add((b, points));
        }
    }

    private static void DrawHalf(DrawingContext ctx, Point from, Point control, Point to, IPen pen)
    {
        var geo = new StreamGeometry();
        using (var s = geo.Open())
        {
            s.BeginFigure(from, false);
            s.QuadraticBezierTo(control, to);
            s.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    /// <summary>The bridge whose arc passes within a few pixels of the pointer.</summary>
    private MapBridgeLine? BridgeAt(Point p)
    {
        const double reach = 5;
        foreach (var (line, pts) in _bridgeHits)
            for (var i = 1; i < pts.Length; i++)
                if (DistanceToSegment(p, pts[i - 1], pts[i]) <= reach) return line;
        return null;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = dx * dx + dy * dy;
        var t = len == 0 ? 0 : Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len, 0, 1);
        var x = a.X + t * dx - p.X;
        var y = a.Y + t * dy - p.Y;
        return Math.Sqrt(x * x + y * y);
    }

    // ── Live markers ─────────────────────────────────────────────────────────
    //
    // ⚠️ Drawn after the node, in screen space, so they stay the same size at every zoom and
    // sit on top of whatever is under them. Hostiles are a round red mark on the right, own
    // characters a square mark on the left: shape and side as well as colour, so the two never
    // read as each other.

    private readonly List<(MapMarkers Marks, Rect Anchor, bool IsBox)> _pendingMarks = new();
    private readonly List<(MapHoleMark Hole, Rect Anchor, bool IsBox)> _pendingHoles = new();
    private readonly List<(MapHoleMark Storm, Rect Anchor, bool IsBox)> _pendingStorms = new();
    private readonly List<(MapCampaignMark Campaign, Rect Anchor, bool IsBox)> _pendingCampaigns = new();

    private static readonly ImmutableSolidColorBrush CampaignBrush = new(Color.Parse("#DC2626"));
    private static readonly IPen CampaignRingPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#E6B91C1C")), 3.5);

    /// <summary>
    /// A red tag with ⚔ at the node's lower right — the other corners are the wormhole's and the
    /// storm's — and, once the fight has started, a solid red ring round the node: solid, where
    /// the avoid list's ring is dashed. Hover for the campaign.
    /// </summary>
    private void DrawCampaign(DrawingContext ctx, MapCampaignMark c, Rect anchor, bool isBox)
    {
        if (c.Running)
        {
            if (isBox) ctx.DrawRectangle(null, CampaignRingPen, anchor.Inflate(5), 6, 6);
            else       ctx.DrawEllipse(null, CampaignRingPen, anchor.Center, anchor.Width / 2 + 6, anchor.Height / 2 + 6);
        }
        var text = new FormattedText("⚔", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldFace, 9.5, MarkInk);
        var w    = Math.Max(13, text.Width + 6);
        var x    = isBox ? anchor.Right - w / 2 : anchor.Right + 1;
        var y    = isBox ? anchor.Bottom - 6 : anchor.Bottom + 2;
        var rect = new Rect(x, y, w, 13);
        ctx.DrawRectangle(CampaignBrush, MarkPen, new RoundedRect(rect, 3));
        ctx.DrawText(text, new Point(rect.Center.X - text.Width / 2, rect.Center.Y - text.Height / 2));
        _badgeTips.Add((rect.Inflate(2), c.Title, c.Detail, null));
    }

    private static readonly ImmutableSolidColorBrush StormBrush = new(Color.Parse("#F5B83D"));
    private static readonly IBrush StormInk = new ImmutableSolidColorBrush(Color.Parse("#2b1d00"));

    /// <summary>An amber tag with ⚡ at the node's upper left — the wormhole tag takes the upper
    /// right. Hover for the storm.</summary>
    private void DrawStorm(DrawingContext ctx, MapHoleMark s, Rect anchor, bool isBox)
    {
        var text = new FormattedText(s.Glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldFace, 9.5, StormInk);
        var w    = Math.Max(13, text.Width + 6);
        var x    = isBox ? anchor.Left - w / 2 : anchor.Left - w - 1;
        var y    = isBox ? anchor.Top - 7 : anchor.Top - 15;
        var rect = new Rect(x, y, w, 13);
        ctx.DrawRectangle(StormBrush, MarkPen, new RoundedRect(rect, 3));
        ctx.DrawText(text, new Point(rect.Center.X - text.Width / 2, rect.Center.Y - text.Height / 2));
        _badgeTips.Add((rect.Inflate(2), s.Title, s.Detail, null));
    }

    private static readonly ImmutableSolidColorBrush HoleBrush = new(Color.Parse("#2DD4BF"));
    private static readonly IBrush HoleInk = new ImmutableSolidColorBrush(Color.Parse("#062925"));
    private static readonly IPen   HoleLinkPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#B32DD4BF")), 1.6, new ImmutableDashStyle([1.5, 2.5], 0));

    /// <summary>
    /// A teal tag with Θ (Thera) or T (Turnur) at the node's upper right — clear of the hostile
    /// mark to the right of centre and the own-character mark to the left. Hover for the holes.
    /// </summary>
    private void DrawHole(DrawingContext ctx, MapHoleMark h, Rect anchor, bool isBox)
    {
        var text = new FormattedText(h.Glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldFace, 9.5, HoleInk);
        var w    = Math.Max(13, text.Width + 6);
        var x    = isBox ? anchor.Right - w / 2 : anchor.Right + 1;
        var y    = isBox ? anchor.Top - 7 : anchor.Top - 15;
        var rect = new Rect(x, y, w, 13);
        ctx.DrawRectangle(HoleBrush, MarkPen, new RoundedRect(rect, 3));
        ctx.DrawText(text, new Point(rect.Center.X - text.Width / 2, rect.Center.Y - text.Height / 2));
        _badgeTips.Add((rect.Inflate(2), h.Title, h.Detail, null));
    }

    private static readonly IBrush MarkInk = new ImmutableSolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IPen   MarkPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#0b0b10")), 1.5);

    private void DrawMarkers(DrawingContext ctx, MapMarkers m, Rect anchor, bool isBox)
    {
        // Beside a box, vertically centred, clear of the docking bar above it and the services
        // below. Above a dot, where its label (to the right) does not run.
        var y = isBox ? anchor.Center.Y : anchor.Top - 5;

        if (m.Hostiles > 0)
        {
            var text = new FormattedText(m.Hostiles.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, BoldFace, 9.5, MarkInk);
            var r  = Math.Max(7.5, text.Width / 2 + 4);
            var cx = isBox ? anchor.Right + r + 3 : anchor.Right + r - 3;
            var c  = new Point(cx, y);
            ctx.DrawEllipse(Palette.Bad, MarkPen, c, r, r);
            ctx.DrawText(text, new Point(c.X - text.Width / 2, c.Y - text.Height / 2));
            if (m.HostileTitle is { } title)
                _badgeTips.Add((new Rect(c.X - r - 2, c.Y - r - 2, r * 2 + 4, r * 2 + 4), title, m.HostileDetail ?? "", m.HostileRows));
        }

        if (m.Own > 0)
        {
            var text = new FormattedText(m.Own.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, BoldFace, 9.5, MarkInk);
            var half = Math.Max(7, text.Width / 2 + 3.5);
            var cx   = isBox ? anchor.Left - half - 3 : anchor.Left - half + 3;
            var rect = new Rect(cx - half, y - 7, half * 2, 14);
            ctx.DrawRectangle(Palette.Info, MarkPen, new RoundedRect(rect, 2));
            ctx.DrawText(text, new Point(cx - text.Width / 2, y - text.Height / 2));
            if (m.OwnTitle is { } title)
                _badgeTips.Add((rect.Inflate(2), title, m.OwnDetail ?? "", m.OwnRows));
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
        if (n.IsOutsideRegion) detail = string.Format(MapText.TipDoubleClickToOpenRegion, n.RegionLabel);

        DrawTooltipBox(ctx, n.Label, detail);
    }

    /// <summary>The tooltip itself, shared by nodes and badges so both look and place the same.</summary>
    private void DrawTooltipBox(DrawingContext ctx, string titleText, string? detail,
                                IReadOnlyList<MapMarkRow>? rows = null)
    {
        var title = new FormattedText(titleText, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, 11.5, TipTextBrush);

        // Rows with pictures replace the plain body when a mark supplies them.
        if (rows is { Count: > 0 })
        {
            DrawRowsTooltip(ctx, title, rows);
            return;
        }

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

    // ── Pictures in the mark hovers ──────────────────────────────────────────

    private const double RowIcon = 20;

    /// <summary>A mark's hover as rows: portrait, ship icon, then the line.</summary>
    private void DrawRowsTooltip(DrawingContext ctx, FormattedText title, IReadOnlyList<MapMarkRow> rows)
    {
        const double pad = 7, gap = 4, rowGap = 2;

        FormattedText Text(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, LabelBrush);

        // Each line in two parts round the ship's icon: what comes before the ship's name, and
        // the name onwards. A line without a ship is one part, with no icon.
        var parts = rows.Select(r => r.ShipTypeId > 0 && r.ShipAt >= 0 && r.ShipAt <= r.Text.Length
                ? (Before: Text(r.Text[..r.ShipAt]), After: Text(r.Text[r.ShipAt..]), Icon: true)
                : (Before: Text(r.Text), After: (FormattedText?)null, Icon: false))
            .ToList();
        double LineWidth((FormattedText Before, FormattedText? After, bool Icon) p) =>
            p.Before.Width + (p.Icon ? gap + RowIcon + gap : 0) + (p.After?.Width ?? 0);

        // The portrait's slot is kept on every row, picture or not, so the text starts in one column.
        var textX = RowIcon + gap * 2;
        var rowH  = Math.Max(RowIcon, parts.Max(p => p.Before.Height)) + rowGap;
        var w = Math.Max(title.Width, textX + parts.Max(LineWidth)) + pad * 2;
        var h = title.Height + 4 + rowH * rows.Count + pad * 2;

        var x = _hoverAt.X + 14;
        var y = _hoverAt.Y + 14;
        if (x + w > Bounds.Width)  x = _hoverAt.X - w - 14;
        if (y + h > Bounds.Height) y = _hoverAt.Y - h - 14;
        x = Math.Max(0, x);
        y = Math.Max(0, y);

        ctx.DrawRectangle(TipBackBrush, TipPen, new RoundedRect(new Rect(x, y, w, h), 3));
        ctx.DrawText(title, new Point(x + pad, y + pad));

        var top = y + pad + title.Height + 4;
        for (var i = 0; i < rows.Count; i++)
        {
            var ry = top + i * rowH;
            var lx = x + pad;
            if (rows[i].CharacterId > 0 &&
                Picture($"https://images.evetech.net/characters/{rows[i].CharacterId}/portrait?size=32") is { } portrait)
                ctx.DrawImage(portrait, new Rect(lx, ry, RowIcon, RowIcon));

            var (before, after, icon) = parts[i];
            var tx = lx + textX;
            var ty = ry + (RowIcon - before.Height) / 2;
            ctx.DrawText(before, new Point(tx, ty));
            if (!icon || after is null) continue;

            // The ship's icon, just before its name.
            var ix = tx + before.Width + gap;
            if (Picture($"https://images.evetech.net/types/{rows[i].ShipTypeId}/icon?size=32") is { } ship)
                ctx.DrawImage(ship, new Rect(ix, ry, RowIcon, RowIcon));
            ctx.DrawText(after, new Point(ix + RowIcon + gap, ty));
        }
    }

    /// <summary>Pictures already fetched, shared by every map. A miss starts the fetch (through
    /// the app's image cache, on disk as well) and repaints this map when it lands; until then
    /// the slot is left empty rather than holding the hover up.</summary>
    private static readonly Dictionary<string, Bitmap> Pictures = new();
    private static readonly HashSet<string> Fetching = new();

    private Bitmap? Picture(string url)
    {
        if (Pictures.TryGetValue(url, out var bitmap)) return bitmap;
        if (!Fetching.Add(url)) return null;

        _ = EveImageCache.GetAsync(url).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            Fetching.Remove(url);
            // A failure is not remembered: the next hover asks again.
            if (t.IsCompletedSuccessfully && t.Result is { } fetched)
            {
                Pictures[url] = fetched;
                InvalidateVisual();
            }
        }), TaskScheduler.Default);
        return null;
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    /// <summary>The node drawn at a point of this control — what a right-click is about.</summary>
    public MapNode? NodeAt(Point p) => HitTest(p);

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

        // Badges first. They sit outside the box so they never overlap a node's own hit area,
        // but the cursor being on one means the question is about the mark, not the system.
        (string Title, string Detail, IReadOnlyList<MapMarkRow>? Rows)? badge = null;
        foreach (var t in _badgeTips)
            if (t.Rect.Contains(pos)) { badge = (t.Title, t.Detail, t.Rows); break; }

        var hit = badge is null ? HitTest(pos) : null;
        var bridgeHit = badge is null && hit is null ? BridgeAt(pos) : null;
        _hoverAt = pos;

        var badgeChanged = badge?.Title != _badgeHover?.Title;
        _badgeHover = badge;
        var bridgeChanged = !ReferenceEquals(bridgeHit, _bridgeHover);
        _bridgeHover = bridgeHit;

        if (badgeChanged || bridgeChanged || !ReferenceEquals(hit, _hover)) { _hover = hit; InvalidateVisual(); }
        else if (hit is not null || badge is not null || bridgeHit is not null) InvalidateVisual();   // glue it to the cursor
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;

        _dragging = false;
        e.Pointer.Capture(null);

        if (_dragMoved) { PublishCamera(); return; }

        var hit = HitTest(e.GetPosition(this));
        if (hit is not null) SelectedId = hit.Id;
    }

    /// <summary>A hover belongs to where the pointer was on the old layout; after a resize (a
    /// split opening or closing) it would be drawn where nothing is under the pointer.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _hover       = null;
        _badgeHover  = null;
        _bridgeHover = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null && _badgeHover is null && _bridgeHover is null) return;
        _hover       = null;
        _badgeHover  = null;
        _bridgeHover = null;
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
        PublishCamera();
        InvalidateVisual();
    }
}
