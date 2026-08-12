using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace EveConsole.Controls;

/// <summary>
/// A stop on the drawn route. <paramref name="Index"/> is its position in the route, counting the
/// origin as 0. <paramref name="IsWaypoint"/> marks a stop the user asked for, which is fixed;
/// everything else was filled in by the planner and may be moved. A pinned stop is one the user
/// chose by hand in place of a filled-in one — it is honoured like a waypoint but stays movable.
/// </summary>
public sealed record JumpMapNode(
    int    Id,
    string Name,
    double X,
    double Y,
    int    Index,
    bool   IsWaypoint,
    string Caption,
    bool   IsPinned = false,
    string Region   = "");

/// <summary>
/// A system drawn for context around the route. Carries what a pilot needs to judge it as a
/// midpoint, because that judgement is made while hovering it on the map — the name and region
/// to know where it is, and what can be docked at once you arrive.
/// </summary>
public sealed record JumpMapDot(
    int    Id,
    string Name,
    string Region,
    double X,
    double Y,
    double Security,
    string Badges);

/// <summary>A stargate connection, in map coordinates.</summary>
public sealed record JumpMapLink(double X1, double Y1, double X2, double Y2);

/// <summary>
/// A system the dragged midpoint could legally be dropped on, with what choosing it would cost.
/// The text is formatted by the view model, which knows the ship and its fuel.
/// </summary>
public sealed record JumpMapCandidate(int Id, string CostText);

/// <summary>Where a dragged midpoint was dropped, in map coordinates.</summary>
public sealed record JumpMapDrop(JumpMapNode Node, double X, double Y);

/// <summary>
/// The planned route drawn over CCP's published 2D map layout — the same arrangement the in-game
/// map and Dotlan use, so the shape is recognisable rather than a projection of our own.
///
/// Distances shown on the route are true 3D light years computed by the planner; this control
/// only positions systems, and never measures them.
/// </summary>
public class JumpMapCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<JumpMapNode>?> RouteProperty =
        AvaloniaProperty.Register<JumpMapCanvas, IReadOnlyList<JumpMapNode>?>(nameof(Route));

    public static readonly StyledProperty<IReadOnlyList<JumpMapDot>?> DotsProperty =
        AvaloniaProperty.Register<JumpMapCanvas, IReadOnlyList<JumpMapDot>?>(nameof(Dots));

    public static readonly StyledProperty<IReadOnlyList<JumpMapLink>?> LinksProperty =
        AvaloniaProperty.Register<JumpMapCanvas, IReadOnlyList<JumpMapLink>?>(nameof(Links));

    public static readonly StyledProperty<IReadOnlyDictionary<string, double>?> RegionHuesProperty =
        AvaloniaProperty.Register<JumpMapCanvas, IReadOnlyDictionary<string, double>?>(nameof(RegionHues));

    public IReadOnlyDictionary<string, double>? RegionHues
    {
        get => GetValue(RegionHuesProperty);
        set => SetValue(RegionHuesProperty, value);
    }

    /// <summary>
    /// Systems the midpoint being dragged could be dropped on. Set when a drag starts and
    /// cleared when it ends: knowing which systems are legal is the whole difficulty of picking
    /// one by hand, and it cannot be guessed from the map.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, JumpMapCandidate>?> CandidatesProperty =
        AvaloniaProperty.Register<JumpMapCanvas, IReadOnlyDictionary<int, JumpMapCandidate>?>(nameof(Candidates));

    /// <summary>Raised with the node whose drag has begun, so the view model can work out and
    /// publish <see cref="Candidates"/>.</summary>
    public static readonly StyledProperty<ICommand?> DragStartedCommandProperty =
        AvaloniaProperty.Register<JumpMapCanvas, ICommand?>(nameof(DragStartedCommand));

    public IReadOnlyList<JumpMapLink>? Links
    {
        get => GetValue(LinksProperty);
        set => SetValue(LinksProperty, value);
    }

    public IReadOnlyDictionary<int, JumpMapCandidate>? Candidates
    {
        get => GetValue(CandidatesProperty);
        set => SetValue(CandidatesProperty, value);
    }

    public ICommand? DragStartedCommand
    {
        get => GetValue(DragStartedCommandProperty);
        set => SetValue(DragStartedCommandProperty, value);
    }

    /// <summary>Invoked with the <see cref="JumpMapNode"/> whose marker was clicked.</summary>
    public static readonly StyledProperty<ICommand?> NodeClickedCommandProperty =
        AvaloniaProperty.Register<JumpMapCanvas, ICommand?>(nameof(NodeClickedCommand));

    /// <summary>Invoked with a <see cref="JumpMapDrop"/> when a midpoint is dragged and released.</summary>
    public static readonly StyledProperty<ICommand?> NodeMovedCommandProperty =
        AvaloniaProperty.Register<JumpMapCanvas, ICommand?>(nameof(NodeMovedCommand));

    public IReadOnlyList<JumpMapNode>? Route
    {
        get => GetValue(RouteProperty);
        set => SetValue(RouteProperty, value);
    }

    public IReadOnlyList<JumpMapDot>? Dots
    {
        get => GetValue(DotsProperty);
        set => SetValue(DotsProperty, value);
    }

    public ICommand? NodeClickedCommand
    {
        get => GetValue(NodeClickedCommandProperty);
        set => SetValue(NodeClickedCommandProperty, value);
    }

    public ICommand? NodeMovedCommand
    {
        get => GetValue(NodeMovedCommandProperty);
        set => SetValue(NodeMovedCommandProperty, value);
    }

    static JumpMapCanvas() => AffectsRender<JumpMapCanvas>(
        RouteProperty, DotsProperty, LinksProperty, CandidatesProperty, RegionHuesProperty);

    public JumpMapCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Brushes and pens ─────────────────────────────────────────────────────

    private static readonly IBrush BackBrush   = new ImmutableSolidColorBrush(Color.Parse("#0b0b10"));
    private static readonly IBrush DotHigh     = new ImmutableSolidColorBrush(Color.Parse("#2c4a3a"));
    private static readonly IBrush DotLow      = new ImmutableSolidColorBrush(Color.Parse("#4a4030"));
    private static readonly IBrush DotNull     = new ImmutableSolidColorBrush(Color.Parse("#3a2c34"));

    private static readonly IPen   LegPen      = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#c8a84b")), 1.6);
    private static readonly IPen   MarkerPen   = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#0b0b10")), 1.5);
    private static readonly IPen   DragPen     = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#7fb8d8")), 1.5,
                                                                  new ImmutableDashStyle([4, 3], 0));
    private static readonly IBrush WaypointFill = new ImmutableSolidColorBrush(Color.Parse("#e8c86a"));
    private static readonly IBrush MidpointFill = new ImmutableSolidColorBrush(Color.Parse("#5599aa"));
    private static readonly IBrush PinnedFill   = new ImmutableSolidColorBrush(Color.Parse("#8fd06a"));
    private static readonly IPen   HoverPen     = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#7fb8d8")), 1.5);
    private static readonly IPen   GhostPen     = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#66788a")), 1,
                                                                  new ImmutableDashStyle([2, 2], 0));

    private static readonly IBrush LabelBrush  = new ImmutableSolidColorBrush(Color.Parse("#ccccd8"));
    private static readonly IBrush CaptionBrush = new ImmutableSolidColorBrush(Color.Parse("#7a8896"));
    private static readonly IBrush PlateBrush  = new ImmutableSolidColorBrush(Color.Parse("#cc12121a"));
    private static readonly IBrush HintBrush   = new ImmutableSolidColorBrush(Color.Parse("#55606e"));

    private static readonly IPen   LinkPen      = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#22283a")), 1);
    private static readonly IBrush CandidateFill = new ImmutableSolidColorBrush(Color.Parse("#4ad991"));
    private static readonly IPen   CandidatePen  = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2f7d55")), 1);
    private static readonly IBrush DotLabelBrush = new ImmutableSolidColorBrush(Color.Parse("#8d8d9e"));

    private static readonly IBrush TipBackBrush = new ImmutableSolidColorBrush(Color.Parse("#f00e0e16"));
    private static readonly IPen   TipPen       = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a4a58")), 1);
    private static readonly IBrush TipTitle     = new ImmutableSolidColorBrush(Color.Parse("#e8e8f2"));
    private static readonly IBrush TipBody      = new ImmutableSolidColorBrush(Color.Parse("#9aa8b6"));
    private static readonly IBrush TipCost      = new ImmutableSolidColorBrush(Color.Parse("#c8a84b"));

    private static readonly Typeface Face     = Typeface.Default;
    private static readonly Typeface BoldFace =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private const double WaypointRadius = 6.5;
    private const double MidpointRadius = 4.5;
    private const double HitRadius      = 11.0;

    // ── View transform ───────────────────────────────────────────────────────

    private double _scale = 1;
    private double _cx, _cy;
    private bool   _needsFit = true;

    private Point _panFrom;
    private bool  _panning;
    private bool  _panMoved;

    private JumpMapNode? _hover;

    /// <summary>Context system under the pointer, when it is not over a route node.</summary>
    private JumpMapDot? _hoverDot;

    /// <summary>Where the pointer was when the hover was last resolved — the tooltip anchors here.</summary>
    private Point _hoverAt;

    /// <summary>Rough distance between neighbouring systems in map units, from the area the dots
    /// cover and how many there are. Multiplied by the scale it gives on-screen spacing, which is
    /// what decides whether names have room to be drawn — the same judgement the region map makes,
    /// without the cost of measuring every nearest neighbour.</summary>
    private double _dotSpacing = 1;

    /// <summary>The midpoint being dragged, and where the pointer currently is on screen.</summary>
    private JumpMapNode? _drag;
    private Point        _dragAt;
    private bool         _dragMoved;

    private Point ToScreen(double x, double y) =>
        new((x - _cx) * _scale + Bounds.Width / 2, (y - _cy) * _scale + Bounds.Height / 2);

    private (double X, double Y) ToWorld(Point p) =>
        ((p.X - Bounds.Width / 2) / _scale + _cx, (p.Y - Bounds.Height / 2) / _scale + _cy);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RouteProperty)
        {
            _needsFit = true;
            _hover    = null;
            _hoverDot = null;
            _drag     = null;
        }
        else if (change.Property == DotsProperty)
        {
            _hoverDot   = null;
            _dotSpacing = EstimateSpacing(Dots);
        }
        else if (change.Property == RegionHuesProperty)
        {
            _regionBrushes.Clear();
        }
    }

    /// <summary>Brush per region, built from <see cref="RegionHues"/> as regions are first drawn.</summary>
    private readonly Dictionary<string, IBrush> _regionBrushes = new(StringComparer.Ordinal);

    /// <summary>
    /// Hue per region name, supplied by the planner, which assigns them against the real
    /// adjacency graph so bordering regions land far apart on the wheel. A region with no hue
    /// here simply draws in the neutral label colour — a wrong colour would be worse than none,
    /// because the whole signal is "different colour means different region".
    /// </summary>
    private IBrush RegionBrush(string region)
    {
        if (region.Length == 0) return DotLabelBrush;
        if (_regionBrushes.TryGetValue(region, out var found)) return found;

        if (RegionHues?.TryGetValue(region, out var hue) != true) return DotLabelBrush;

        // Only the hue varies. Saturation and lightness are fixed at values that stay legible on
        // the dark background, so no region can come out unreadable.
        var brush = new ImmutableSolidColorBrush(new HslColor(1.0, hue, 0.45, 0.70).ToRgb());
        _regionBrushes[region] = brush;
        return brush;
    }

    private static double EstimateSpacing(IReadOnlyList<JumpMapDot>? dots)
    {
        if (dots is null || dots.Count < 2) return 1;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var d in dots)
        {
            if (d.X < minX) minX = d.X;
            if (d.X > maxX) maxX = d.X;
            if (d.Y < minY) minY = d.Y;
            if (d.Y > maxY) maxY = d.Y;
        }

        var area = (maxX - minX) * (maxY - minY);
        return area > 0 ? Math.Sqrt(area / dots.Count) : 1;
    }

    /// <summary>Frames the route with a margin. Deferred to render time, when bounds are known.</summary>
    private void Fit()
    {
        var route = Route;
        if (route is null || route.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in route)
        {
            if (n.X < minX) minX = n.X;
            if (n.X > maxX) maxX = n.X;
            if (n.Y < minY) minY = n.Y;
            if (n.Y > maxY) maxY = n.Y;
        }

        _cx = (minX + maxX) / 2;
        _cy = (minY + maxY) / 2;

        var w  = maxX - minX;
        var h  = maxY - minY;
        var sx = w > 0 ? Bounds.Width  * 0.72 / w : double.MaxValue;
        var sy = h > 0 ? Bounds.Height * 0.72 / h : double.MaxValue;

        _scale = Math.Min(sx, sy);
        if (double.IsInfinity(_scale) || _scale <= 0 || _scale == double.MaxValue)
            _scale = Bounds.Width / 4e17;   // a lone system: show it at a sane universe scale

        _needsFit = false;
    }

    public void ResetView()
    {
        _needsFit = true;
        InvalidateVisual();
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private JumpMapNode? NodeAt(Point p)
    {
        var route = Route;
        if (route is null) return null;

        JumpMapNode? best = null;
        var bestDist = HitRadius * HitRadius;
        foreach (var n in route)
        {
            var s  = ToScreen(n.X, n.Y);
            var dx = s.X - p.X;
            var dy = s.Y - p.Y;
            var d  = dx * dx + dy * dy;
            if (d <= bestDist) { bestDist = d; best = n; }
        }
        return best;
    }

    /// <summary>Nearest context system to the pointer, within the same reach as a route node.</summary>
    private JumpMapDot? DotAt(Point p)
    {
        var dots = Dots;
        if (dots is null) return null;

        JumpMapDot? best = null;
        var bestDist = HitRadius * HitRadius;
        foreach (var d in dots)
        {
            var s  = ToScreen(d.X, d.Y);
            var dx = s.X - p.X;
            var dy = s.Y - p.Y;
            var sq = dx * dx + dy * dy;
            if (sq <= bestDist) { bestDist = sq; best = d; }
        }
        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);

        // Only a filled-in midpoint may be dragged: moving a stop the user asked for would
        // silently rewrite their own route.
        var node = NodeAt(p);
        if (node is { IsWaypoint: false })
        {
            _drag      = node;
            _dragAt    = p;
            _dragMoved = false;

            // Ask for the legal drop targets straight away, so they are lit before the pointer
            // has moved far enough to need them.
            if (DragStartedCommand?.CanExecute(node) == true) DragStartedCommand.Execute(node);
        }
        else
        {
            _panning  = true;
            _panFrom  = p;
            _panMoved = false;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);

        if (_drag is not null)
        {
            _dragAt    = p;
            _dragMoved = true;

            // What is under the pointer matters most while dragging — that is the system about
            // to be chosen — so the hovered dot is tracked during the drag, not only outside it.
            _hoverDot = DotAt(p);
            InvalidateVisual();
            return;
        }

        if (_panning)
        {
            if (Math.Abs(p.X - _panFrom.X) > 2 || Math.Abs(p.Y - _panFrom.Y) > 2) _panMoved = true;
            _cx -= (p.X - _panFrom.X) / _scale;
            _cy -= (p.Y - _panFrom.Y) / _scale;
            _panFrom = p;
            InvalidateVisual();
            return;
        }

        var hit = NodeAt(p);
        var dot = hit is null ? DotAt(p) : null;
        _hoverAt = p;

        if (!ReferenceEquals(hit, _hover) || !ReferenceEquals(dot, _hoverDot))
        {
            _hover    = hit;
            _hoverDot = dot;
            Cursor = new Cursor(hit is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetPosition(this);

        if (_drag is { } dragged)
        {
            var node = dragged;
            _drag = null;

            if (_dragMoved)
            {
                var (wx, wy) = ToWorld(p);
                var drop = new JumpMapDrop(node, wx, wy);
                if (NodeMovedCommand?.CanExecute(drop) == true) NodeMovedCommand.Execute(drop);
            }
            else if (NodeClickedCommand?.CanExecute(node) == true)
            {
                // A press with no movement is a click: the user wants the alternatives list.
                NodeClickedCommand.Execute(node);
            }

            InvalidateVisual();
        }
        else if (_panning)
        {
            _panning = false;
            if (!_panMoved &&
                NodeAt(p) is { IsWaypoint: true } wp &&
                NodeClickedCommand?.CanExecute(wp) == true)
            {
                NodeClickedCommand.Execute(wp);
            }
        }

        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var p         = e.GetPosition(this);
        var (bx, by)  = ToWorld(p);
        var factor    = e.Delta.Y > 0 ? 1.18 : 1 / 1.18;

        _scale *= factor;

        // Keep the world point under the cursor pinned there, so zooming follows the mouse.
        _cx = bx - (p.X - Bounds.Width  / 2) / _scale;
        _cy = by - (p.Y - Bounds.Height / 2) / _scale;

        InvalidateVisual();
        e.Handled = true;
    }

    // ── Render ───────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(BackBrush, new Rect(Bounds.Size));
        if (_needsFit) Fit();

        var route      = Route;
        var candidates = Candidates;
        var view       = new Rect(Bounds.Size).Inflate(20);

        // Gate network first and faintest — it is the backdrop the route is read against, never
        // the subject. A jump ignores gates; seeing them is how you tell where a midpoint sits.
        if (Links is { Count: > 0 } links)
        {
            foreach (var l in links)
            {
                var a = ToScreen(l.X1, l.Y1);
                var b = ToScreen(l.X2, l.Y2);
                if (!view.Contains(a) && !view.Contains(b)) continue;
                ctx.DrawLine(LinkPen, a, b);
            }
        }

        // Context systems next, so the route always draws on top of them.
        var showDotNames = _dotSpacing * _scale > 34;

        if (Dots is { Count: > 0 } dots)
        {
            foreach (var d in dots)
            {
                var s = ToScreen(d.X, d.Y);
                if (!view.Contains(s)) continue;

                // A legal drop target is lit and enlarged: while dragging, which systems are
                // eligible is the only thing the pilot needs, and nothing on the map implies it.
                var isCandidate = candidates?.ContainsKey(d.Id) == true;

                if (isCandidate)
                    ctx.DrawEllipse(CandidateFill, CandidatePen, s, 4, 4);
                else
                    ctx.DrawEllipse(d.Security >= 0.45 ? DotHigh : d.Security > 0 ? DotLow : DotNull,
                                    null, s, 1.6, 1.6);

                if (showDotNames || isCandidate)
                {
                    var t = new FormattedText(d.Name, System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, Face, 9, RegionBrush(d.Region));
                    ctx.DrawText(t, new Point(s.X + (isCandidate ? 7 : 4), s.Y - t.Height / 2));
                }
            }
        }

        if (route is null || route.Count == 0)
        {
            DrawCentred(ctx, "Plan a route to see it on the map.");
            return;
        }

        // Legs. The dragged midpoint follows the pointer, so its two legs are drawn dashed to
        // where it currently is rather than to where it was planned.
        for (var i = 0; i < route.Count - 1; i++)
        {
            var a = route[i];
            var b = route[i + 1];
            var pa = PointFor(a);
            var pb = PointFor(b);
            var dragging = ReferenceEquals(a, _drag) || ReferenceEquals(b, _drag);
            ctx.DrawLine(dragging ? DragPen : LegPen, pa, pb);
        }

        foreach (var n in route)
        {
            var s = PointFor(n);
            var r = n.IsWaypoint ? WaypointRadius : MidpointRadius;

            // Where the midpoint being dragged actually still is. Without it the marker and its
            // name travel with the pointer and there is nothing left showing what is being
            // moved, or how far it has been moved from.
            if (ReferenceEquals(n, _drag) && _dragMoved)
                ctx.DrawEllipse(null, GhostPen, ToScreen(n.X, n.Y), r, r);

            if (ReferenceEquals(n, _hover) || ReferenceEquals(n, _drag))
                ctx.DrawEllipse(null, HoverPen, s, r + 4, r + 4);

            var fill = n.IsWaypoint ? WaypointFill : n.IsPinned ? PinnedFill : MidpointFill;
            ctx.DrawEllipse(fill, MarkerPen, s, r, r);
        }

        // Labels last, so no marker paints over them.
        foreach (var n in route)
        {
            // ⚠️ The dragged node's label stays at the system it still occupies, rather than
            // riding the pointer. Moving with the marker meant the one name you needed while
            // choosing a replacement — the one you are replacing — was the one that ran away.
            var s    = ReferenceEquals(n, _drag) ? ToScreen(n.X, n.Y) : PointFor(n);
            var name = new FormattedText(n.Name, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, BoldFace, 11, LabelBrush);
            // Caption carries the region, so it takes the region's colour — the same code the
            // context system names use, which is what makes a route crossing a border legible.
            var cap = n.Caption.Length > 0
                ? new FormattedText(n.Caption, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Face, 9.5,
                    n.Region.Length > 0 ? RegionBrush(n.Region) : CaptionBrush)
                : null;

            var w  = Math.Max(name.Width, cap?.Width ?? 0);
            var h  = name.Height + (cap?.Height ?? 0);
            var at = new Point(s.X + (n.IsWaypoint ? WaypointRadius : MidpointRadius) + 5,
                               s.Y - h / 2);

            ctx.DrawRectangle(PlateBrush, null,
                new RoundedRect(new Rect(at.X - 3, at.Y - 2, w + 6, h + 4), 2));
            ctx.DrawText(name, at);
            if (cap is not null) ctx.DrawText(cap, new Point(at.X, at.Y + name.Height));
        }

        if (_hoverDot is { } tipDot) DrawTooltip(ctx, tipDot);

        DrawHint(ctx, "drag a midpoint to move it · click one for alternatives · scroll to zoom");
    }

    /// <summary>
    /// What the hovered system offers, next to the pointer. Shown while dragging as well as on a
    /// plain hover, because the moment the choice is actually made is mid-drag — a tooltip that
    /// vanished as soon as the button went down would be missing exactly when it is needed.
    /// </summary>
    private void DrawTooltip(DrawingContext ctx, JumpMapDot dot)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        var title = new FormattedText(dot.Name, culture, FlowDirection.LeftToRight, BoldFace, 12, TipTitle);

        var lines = new List<FormattedText>
        {
            new($"{dot.Region} · {dot.Security:N2}", culture, FlowDirection.LeftToRight, Face, 10, TipBody),
        };

        if (dot.Badges.Length > 0)
            lines.Add(new FormattedText(dot.Badges, culture, FlowDirection.LeftToRight, Face, 10, TipBody));
        else
            lines.Add(new FormattedText("no known station or structure", culture,
                FlowDirection.LeftToRight, Face, 10, TipBody));

        // Only meaningful mid-drag, and only for a system that could actually be chosen.
        if (Candidates?.TryGetValue(dot.Id, out var candidate) == true && candidate.CostText.Length > 0)
            lines.Add(new FormattedText(candidate.CostText, culture, FlowDirection.LeftToRight,
                BoldFace, 10, TipCost));

        var w = Math.Max(title.Width, lines.Max(l => l.Width));
        var h = title.Height + lines.Sum(l => l.Height) + 6;

        // Flip to the other side of the pointer rather than being clipped at an edge.
        var at = _drag is not null ? _dragAt : _hoverAt;
        var x  = at.X + 16;
        var y  = at.Y + 16;
        if (x + w + 16 > Bounds.Width)  x = at.X - w - 22;
        if (y + h + 14 > Bounds.Height) y = at.Y - h - 20;

        ctx.DrawRectangle(TipBackBrush, TipPen,
            new RoundedRect(new Rect(x - 8, y - 6, w + 16, h + 12), 3));

        ctx.DrawText(title, new Point(x, y));
        var lineY = y + title.Height + 4;
        foreach (var l in lines)
        {
            ctx.DrawText(l, new Point(x, lineY));
            lineY += l.Height;
        }
    }

    /// <summary>Screen position of a node, following the pointer while it is being dragged.</summary>
    private Point PointFor(JumpMapNode n) =>
        ReferenceEquals(n, _drag) && _dragMoved ? _dragAt : ToScreen(n.X, n.Y);

    private void DrawCentred(DrawingContext ctx, string text)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, 12, CaptionBrush);
        ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, (Bounds.Height - t.Height) / 2));
    }

    private void DrawHint(DrawingContext ctx, string text)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, 9.5, HintBrush);
        ctx.DrawText(t, new Point(10, Bounds.Height - t.Height - 8));
    }
}
