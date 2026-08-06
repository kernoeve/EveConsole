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
    bool   IsPinned = false);

/// <summary>A system drawn only for context — no part of the route.</summary>
public sealed record JumpMapDot(int Id, double X, double Y, double Security);

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

    static JumpMapCanvas() => AffectsRender<JumpMapCanvas>(RouteProperty, DotsProperty);

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

    private static readonly IBrush LabelBrush  = new ImmutableSolidColorBrush(Color.Parse("#ccccd8"));
    private static readonly IBrush CaptionBrush = new ImmutableSolidColorBrush(Color.Parse("#7a8896"));
    private static readonly IBrush PlateBrush  = new ImmutableSolidColorBrush(Color.Parse("#cc12121a"));
    private static readonly IBrush HintBrush   = new ImmutableSolidColorBrush(Color.Parse("#55606e"));

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
            _drag     = null;
        }
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
            _dragAt = p;
            _dragMoved = true;
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
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
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

        var route = Route;

        // Context systems first, so the route always draws on top of them.
        if (Dots is { Count: > 0 } dots)
        {
            var view = new Rect(Bounds.Size).Inflate(20);
            foreach (var d in dots)
            {
                var s = ToScreen(d.X, d.Y);
                if (!view.Contains(s)) continue;
                var brush = d.Security >= 0.45 ? DotHigh : d.Security > 0 ? DotLow : DotNull;
                ctx.DrawEllipse(brush, null, s, 1.6, 1.6);
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

            if (ReferenceEquals(n, _hover) || ReferenceEquals(n, _drag))
                ctx.DrawEllipse(null, HoverPen, s, r + 4, r + 4);

            var fill = n.IsWaypoint ? WaypointFill : n.IsPinned ? PinnedFill : MidpointFill;
            ctx.DrawEllipse(fill, MarkerPen, s, r, r);
        }

        // Labels last, so no marker paints over them.
        foreach (var n in route)
        {
            var s    = PointFor(n);
            var name = new FormattedText(n.Name, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, BoldFace, 11, LabelBrush);
            var cap = n.Caption.Length > 0
                ? new FormattedText(n.Caption, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Face, 9.5, CaptionBrush)
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

        DrawHint(ctx, "drag a midpoint to move it · click one for alternatives · scroll to zoom");
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
