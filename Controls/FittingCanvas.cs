using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using EveConsole.Services;

namespace EveConsole.Controls;

/// <summary>
/// A band of slots. Named after the game's own grouping rather than after structures, because the
/// same control fits ships: a hull uses High/Mid/Low/Rig and adds Subsystem, a structure uses the
/// same four and adds Service.
/// </summary>
public enum FittingBand
{
    High,
    Mid,
    Low,
    Rig,
    Service,
    Subsystem,
}

/// <summary>
/// One slot, filled or empty. <see cref="TypeId"/> of 0 means empty — an empty slot is still a
/// slot and must be drawn, because "this hull has three free mid slots" is exactly what a fitting
/// view exists to show.
/// </summary>
public sealed record FittingSlot(
    FittingBand Band,
    int         Index,
    int         TypeId,
    string      Name,
    Bitmap?     Icon = null,
    bool        FromAssets = false,
    SlotActivity Activity = SlotActivity.None,
    Bitmap?     ChargeIcon = null,
    string?     Detail = null,
    object?     Tag = null)
{
    public bool IsEmpty => TypeId == 0;
}

/// <summary>A fitted module's state, where the caller knows it. <see cref="None"/> draws nothing —
/// the structure view, which has no states, never sets it.</summary>
public enum SlotActivity { None, Offline, Online, Active, Overheated }

/// <summary>
/// The fitting ring: slots arranged around a hull render, in the manner of the in-game fitting
/// window. Click a slot to act on it.
///
/// <para>⚠️ Knows nothing about structures, ships, or where slot counts come from. It is handed a
/// list of slots and draws them. That is deliberate — the caller resolves capacity from the type's
/// dogma attributes (hiSlots, medSlots, lowSlots, rigSlots, serviceSlots), which are populated
/// identically for hulls, so pointing this at a ship later needs no change here.</para>
/// </summary>
public class FittingCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<FittingSlot>?> SlotsProperty =
        AvaloniaProperty.Register<FittingCanvas, IReadOnlyList<FittingSlot>?>(nameof(Slots));

    public static readonly StyledProperty<Bitmap?> HullRenderProperty =
        AvaloniaProperty.Register<FittingCanvas, Bitmap?>(nameof(HullRender));

    /// <summary>Invoked with the <see cref="FittingSlot"/> that was clicked.</summary>
    public static readonly StyledProperty<ICommand?> SlotClickedCommandProperty =
        AvaloniaProperty.Register<FittingCanvas, ICommand?>(nameof(SlotClickedCommand));

    /// <summary>
    /// Suppresses editing. Set when the fitting is known from assets: the game is then the
    /// authority on what is fitted, and letting someone type over it would create a disagreement
    /// with no way to tell which side is right.
    /// </summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<FittingCanvas, bool>(nameof(IsReadOnly));

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>The largest a slot box is drawn. The structure view keeps the compact default; the
    /// fitting tool, where the ring is the main thing on screen, asks for more.</summary>
    public static readonly StyledProperty<double> MaxSlotSizeProperty =
        AvaloniaProperty.Register<FittingCanvas, double>(nameof(MaxSlotSize), BaseSlotSize);

    public double MaxSlotSize
    {
        get => GetValue(MaxSlotSizeProperty);
        set => SetValue(MaxSlotSizeProperty, value);
    }

    /// <summary>The slot whose <see cref="FittingSlot.Tag"/> equals this is outlined as selected.</summary>
    public static readonly StyledProperty<object?> SelectedTagProperty =
        AvaloniaProperty.Register<FittingCanvas, object?>(nameof(SelectedTag));

    public object? SelectedTag
    {
        get => GetValue(SelectedTagProperty);
        set => SetValue(SelectedTagProperty, value);
    }

    /// <summary>Raised on a right-click on a slot, with where it happened, for a context menu.</summary>
    public event Action<FittingSlot, Point>? SlotContextRequested;

    public IReadOnlyList<FittingSlot>? Slots
    {
        get => GetValue(SlotsProperty);
        set => SetValue(SlotsProperty, value);
    }

    public Bitmap? HullRender
    {
        get => GetValue(HullRenderProperty);
        set => SetValue(HullRenderProperty, value);
    }

    public ICommand? SlotClickedCommand
    {
        get => GetValue(SlotClickedCommandProperty);
        set => SetValue(SlotClickedCommandProperty, value);
    }

    static FittingCanvas() =>
        AffectsRender<FittingCanvas>(SlotsProperty, HullRenderProperty, IsReadOnlyProperty, MaxSlotSizeProperty, SelectedTagProperty);

    public FittingCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    private static IPen? _emptyPen;
    private static IPen? _hoverPen;
    private static IPen? _ringPen;
    private static IBrush BackBrush  => Palette.SurfaceBase;
    private static IBrush EmptyFill  => Palette.SurfacePanelAlt;
    private static IPen   EmptyPen   => _emptyPen ??= new Pen(Palette.BorderDefault, 1);
    private static IPen   HoverPen   => _hoverPen ??= new Pen(Palette.Info, 1.5);
    private static IBrush RingBrush  => Palette.SurfacePanel;
    private static readonly IBrush OfflineVeil = new ImmutableSolidColorBrush(Color.FromArgb(150, 10, 10, 14));
    private static IPen ActivePen   => new Pen(Palette.Good, 2);
    private static IPen HeatPen     => new Pen(Palette.Bad, 2);
    private static IPen SelectedPen => new Pen(Palette.Accent, 1.5);
    private static IPen   RingPen    => _ringPen ??= new Pen(Palette.BorderSubtle, 1);

    // One colour per band, so a glance says which ring you are looking at without reading labels.
    private static readonly IBrush HighFill    = new ImmutableSolidColorBrush(Color.Parse("#3b5f7a"));
    private static readonly IBrush MidFill     = new ImmutableSolidColorBrush(Color.Parse("#3f6b5c"));
    private static readonly IBrush LowFill     = new ImmutableSolidColorBrush(Color.Parse("#6b4a3f"));
    private static readonly IBrush RigFill     = new ImmutableSolidColorBrush(Color.Parse("#5a4a6b"));
    private static readonly IBrush ServiceFill = new ImmutableSolidColorBrush(Color.Parse("#6b6440"));

    private static IBrush LabelBrush => Palette.TextMuted;
    private static IBrush TipBack    => Palette.SurfaceOverlayStrong;
    private static readonly IPen   TipPen     = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a4a58")), 1);
    private static IBrush TipTitle   => Palette.TextBright;
    private static IBrush TipBody    => Palette.TextMuted;

    private static readonly Typeface Face = Typeface.Default;
    private static readonly Typeface BoldFace =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    /// <summary>Box edge when there is room; shrinks on a crowded hull.</summary>
    private const double BaseSlotSize = 30;

    /// <summary>Below this a box cannot hold a readable icon. Nothing published needs it, but the
    /// floor stops a future hull degrading silently into dots.</summary>
    private const double MinSlotSize = 20;

    /// <summary>Largest box edge as a share of the ring's radius, so the boxes shrink with the ring
    /// — beside another fit, or in a small window — instead of crowding it.</summary>
    private const double MaxSlotFraction = 0.2;

    /// <summary>
    /// Box edge actually used, recomputed each layout so every band shares one size — differing
    /// sizes around a single ring would read as meaning something.
    ///
    /// <para>⚠️ Measured across all published types the ceiling is eight slots for high, mid, low
    /// and service alike. The binding case is not the obvious one: eight LOWS sit on the
    /// horizontal rule beside eight highs and fit, while eight MIDS on the vertical rule do not.
    /// The Leviathan and the Palatine Keepstar both carry 8/8, so this is a real hull.</para>
    /// </summary>
    private double _slotSize = BaseSlotSize;

    // How far each band reaches from its centre, as a fraction of the radius. A shared budget,
    // not independent knobs: widening one pushes its end slots into the next.
    //
    // Rigs cap at three, so their band is never the constraint and is kept deliberately narrow —
    // the space is worth more to the highs and lows, which can reach eight.
    private const double HorizontalExtent = 0.78;
    private const double MidExtent        = 0.56;
    private const double RigExtent        = 0.24;

    private static IBrush FillFor(FittingBand band) => band switch
    {
        FittingBand.High      => HighFill,
        FittingBand.Mid       => MidFill,
        FittingBand.Low       => LowFill,
        FittingBand.Rig       => RigFill,
        FittingBand.Service   => ServiceFill,
        _                     => RigFill,
    };

    private static string LabelFor(FittingBand band) => band switch
    {
        FittingBand.High      => "HIGH",
        FittingBand.Mid       => "MID",
        FittingBand.Low       => "LOW",
        FittingBand.Rig       => "RIGS",
        FittingBand.Service   => "SERVICES",
        _                     => "SUBSYSTEMS",
    };

    // ── Layout ───────────────────────────────────────────────────────────────

    /// <summary>Where each slot was last drawn, so hit-testing matches what is on screen rather
    /// than recomputing the geometry and risking the two disagreeing.</summary>
    private readonly List<(FittingSlot Slot, Rect Rect)> _placed = [];

    private FittingSlot? _hover;
    private Point        _hoverAt;

    /// <summary>
    /// Clear space between neighbouring slot boxes, measured on screen rather than in degrees.
    /// A band uses this until it would outgrow the extent it is allotted, then tightens to fit.
    ///
    /// <para>Tightening this also opens the corners where two bands meet, which is not obvious:
    /// a band's span is (count-1) × step, so a smaller gap pulls its end slots back toward its
    /// own centre and away from the neighbouring band. That is what clears the Keepstar's eighth
    /// high slot from the first mid.</para>
    /// </summary>
    private const double SlotGap = 5;

    /// <summary>
    /// Places the slots in the arrangement the game uses — high at the top, mid on the right, low
    /// at the bottom, rigs on the left, services in a row underneath.
    /// </summary>
    private void Layout()
    {
        _placed.Clear();

        var slots = Slots;
        if (slots is null || slots.Count == 0) return;

        var services = slots.Where(s => s.Band == FittingBand.Service)
                            .OrderBy(s => s.Index).ToList();

        // The service row lives below the circle, so the circle gives up that height.
        var maxSlot  = Math.Max(MinSlotSize, MaxSlotSize);
        var reserved = services.Count > 0 ? maxSlot + 16 : 0;

        var cx = Bounds.Width / 2;
        var cy = (Bounds.Height - reserved) / 2;
        _radius = Math.Min(Bounds.Width, Bounds.Height - reserved) / 2 - maxSlot * 0.75;
        _centre = new Point(cx, cy);

        if (_radius <= maxSlot) return;

        // ── One box size, set by the tightest band ───────────────────────────
        // Decided before anything is placed: a band forced to tighten its step caps how big a box
        // can be everywhere.
        //
        // Rigs are excluded. They cap at three slots on a narrow band, so they can never be the
        // constraint — including them would only risk shrinking every box for no reason.
        double Room(FittingBand b, double halfExtent)
        {
            var n = slots.Count(s => s.Band == b);
            return n > 1 ? halfExtent * 2 / (n - 1) : double.MaxValue;
        }

        var tightest = Math.Min(
            Math.Min(Room(FittingBand.High, _radius * HorizontalExtent),
                     Room(FittingBand.Low,  _radius * HorizontalExtent)),
                     Room(FittingBand.Mid,  _radius * MidExtent));

        _slotSize = Math.Clamp(tightest - SlotGap, MinSlotSize, Math.Max(MinSlotSize, Math.Min(maxSlot, _radius * MaxSlotFraction)));

        // ⚠️ Slots are spaced along a straight axis, then pushed out to the circle — NOT spread
        // by equal angles. Equal angles look even in degrees and uneven on screen: across the top
        // the horizontal gap per degree is r·cos(θ)·Δθ, which shrinks toward the ends of the arc,
        // so the outermost slots collide while the middle ones sit far apart. Spacing the axis
        // and solving the circle for the other coordinate gives a constant visible gap.

        /// <summary>Slots evenly spaced in X, riding the top or bottom of the circle.</summary>
        void Horizontal(FittingBand band, bool top, double maxHalfExtent)
        {
            var inBand = slots.Where(s => s.Band == band).OrderBy(s => s.Index).ToList();
            if (inBand.Count == 0) return;

            var step = _slotSize + SlotGap;
            if (inBand.Count > 1)
                step = Math.Min(step, maxHalfExtent * 2 / (inBand.Count - 1));

            var start = -step * (inBand.Count - 1) / 2.0;

            for (var i = 0; i < inBand.Count; i++)
            {
                var dx = start + step * i;
                var dy = Math.Sqrt(Math.Max(0, _radius * _radius - dx * dx));
                var y  = top ? cy - dy : cy + dy;

                _placed.Add((inBand[i],
                    new Rect(cx + dx - _slotSize / 2, y - _slotSize / 2, _slotSize, _slotSize)));
            }
        }

        /// <summary>Slots evenly spaced in Y, riding the left or right of the circle.</summary>
        void Vertical(FittingBand band, bool right, double centreOffsetY, double maxHalfExtent)
        {
            var inBand = slots.Where(s => s.Band == band).OrderBy(s => s.Index).ToList();
            if (inBand.Count == 0) return;

            var step = _slotSize + SlotGap;
            if (inBand.Count > 1)
                step = Math.Min(step, maxHalfExtent * 2 / (inBand.Count - 1));

            var start = centreOffsetY - step * (inBand.Count - 1) / 2.0;

            for (var i = 0; i < inBand.Count; i++)
            {
                var dy = start + step * i;
                var dx = Math.Sqrt(Math.Max(0, _radius * _radius - dy * dy));
                var x  = right ? cx + dx : cx - dx;

                _placed.Add((inBand[i],
                    new Rect(x - _slotSize / 2, cy + dy - _slotSize / 2, _slotSize, _slotSize)));
            }
        }

        // ⚠️ The extents are a budget shared between neighbouring bands, not independent knobs.
        // A Keepstar's eight high slots are the worst case: at 0.70r the clamp squeezed the step
        // down to about the box width and they touched. Widening the highs alone would have run
        // their end slots into the mids, so the mids move down and narrow by the same argument.
        //
        // With eight highs the band now reaches roughly 49° from vertical while the topmost mid
        // sits around 20° from horizontal, leaving a clear arc between them rather than the few
        // pixels there were before.
        void PlaceBands()
        {
            Horizontal(FittingBand.High, top: true,  maxHalfExtent: _radius * HorizontalExtent);
            Horizontal(FittingBand.Low,  top: false, maxHalfExtent: _radius * HorizontalExtent);

            Vertical(FittingBand.Mid, right: true,  centreOffsetY: 0,              maxHalfExtent: _radius * MidExtent);
            Vertical(FittingBand.Rig, right: false, centreOffsetY: _radius * 0.36, maxHalfExtent: _radius * RigExtent);
            Vertical(FittingBand.Subsystem, right: false, centreOffsetY: -_radius * 0.38,
                     maxHalfExtent: _radius * RigExtent);
        }

        // ⚠️ Spacing within a band does not keep bands apart: where two meet — the outer lows and
        // the rigs above them, the outer highs and the top mid — the boxes can overlap however the
        // band itself is spaced. Three rigs beside six or more lows do this at any size above about
        // a sixth of the radius. So shrink the boxes, a pixel at a time, until every band is clear.
        while (true)
        {
            _placed.Clear();
            PlaceBands();
            if (_slotSize <= MinSlotSize || BandsClear()) break;
            _slotSize = Math.Max(MinSlotSize, _slotSize - 1);
        }

        // Services are a straight row below the circle: there can be seven, and an arc that long
        // reads as another module band rather than as something different in kind.
        if (services.Count > 0)
        {
            // SlotGap here too, so the row spaces exactly like the bands on the circle rather
            // than carrying its own number that would drift from them.
            var totalW = services.Count * _slotSize + (services.Count - 1) * SlotGap;
            var x0     = cx - totalW / 2;
            var y0     = Bounds.Height - _slotSize - 6;

            for (var i = 0; i < services.Count; i++)
                _placed.Add((services[i],
                    new Rect(x0 + i * (_slotSize + SlotGap), y0, _slotSize, _slotSize)));
        }
    }

    private Point  _centre;
    private double _radius;

    /// <summary>No box closer than the slot gap to a box of another band.</summary>
    private bool BandsClear()
    {
        for (var i = 0; i < _placed.Count; i++)
        for (var j = i + 1; j < _placed.Count; j++)
        {
            var (a, ra) = _placed[i];
            var (b, rb) = _placed[j];
            if (a.Band != b.Band && ra.Inflate(SlotGap / 2).Intersects(rb.Inflate(SlotGap / 2))) return false;
        }
        return true;
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    private FittingSlot? SlotAt(Point p)
    {
        foreach (var (slot, rect) in _placed)
            if (rect.Contains(p)) return slot;
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var p   = e.GetPosition(this);
        var hit = SlotAt(p);
        _hoverAt = p;

        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            // Hover still resolves when read-only — the tooltip is worth having either way — but
            // the cursor does not promise a click that will do nothing.
            Cursor = new Cursor(hit is null || IsReadOnly
                ? StandardCursorType.Arrow
                : StandardCursorType.Hand);
        }

        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (IsReadOnly) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            if (SlotAt(e.GetPosition(this)) is { } hit && SlotContextRequested is { } handler)
            {
                handler(hit, e.GetPosition(this));
                e.Handled = true;
            }
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        if (SlotAt(e.GetPosition(this)) is { } slot &&
            SlotClickedCommand?.CanExecute(slot) == true)
        {
            SlotClickedCommand.Execute(slot);
            e.Handled = true;
        }
    }

    // ── Render ───────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(BackBrush, new Rect(Bounds.Size));

        Layout();
        if (_placed.Count == 0)
        {
            DrawCentred(ctx, "No fitting information for this type.");
            return;
        }

        // The hull fills the circle and is clipped by it, so the render reads as the subject the
        // slots are arranged around rather than as a picture floating inside a ring.
        if (HullRender is { } hull)
        {
            var circle = new EllipseGeometry(
                new Rect(_centre.X - _radius, _centre.Y - _radius, _radius * 2, _radius * 2));

            using (ctx.PushGeometryClip(circle))
            {
                // Square, sized to the circle's diameter, so the image touches the edge on every
                // side and the corners are what gets clipped away.
                var d = _radius * 2;
                ctx.DrawImage(hull, new Rect(_centre.X - d / 2, _centre.Y - d / 2, d, d));
            }
        }

        // The ring the slots attach to, drawn over the hull so the edge stays crisp.
        ctx.DrawEllipse(null, RingPen, _centre, _radius, _radius);

        foreach (var (slot, rect) in _placed)
        {
            // Box first, then the icon on top — the outline is what ties the slot to the ring,
            // and it stays visible behind a transparent icon.
            var fill = slot.IsEmpty ? EmptyFill : FillFor(slot.Band);
            ctx.DrawRectangle(fill, EmptyPen, new RoundedRect(rect, 3));

            if (slot.Icon is { } icon)
                ctx.DrawImage(icon, rect.Deflate(2));

            // The loaded charge, small in the lower right corner, as the game shows it.
            if (slot.ChargeIcon is { } charge)
            {
                var s = rect.Width * 0.42;
                var r = new Rect(rect.Right - s - 1, rect.Bottom - s - 1, s, s);
                ctx.DrawRectangle(BackBrush, null, new RoundedRect(r, 2));
                ctx.DrawImage(charge, r.Deflate(1));
            }

            // State: dimmed when offline, an edge in the "good" colour when running and the
            // "bad" colour when overheated. Online draws nothing — it is the resting state.
            switch (slot.Activity)
            {
                case SlotActivity.Offline:
                    ctx.DrawRectangle(OfflineVeil, null, new RoundedRect(rect, 3));
                    break;
                case SlotActivity.Active:
                    ctx.DrawRectangle(null, ActivePen, new RoundedRect(rect.Inflate(1), 4));
                    break;
                case SlotActivity.Overheated:
                    ctx.DrawRectangle(null, HeatPen, new RoundedRect(rect.Inflate(1), 4));
                    break;
            }

            if (SelectedTag is not null && Equals(slot.Tag, SelectedTag))
                ctx.DrawRectangle(null, SelectedPen, new RoundedRect(rect.Inflate(3), 5));

            if (ReferenceEquals(slot, _hover))
                ctx.DrawRectangle(null, HoverPen, new RoundedRect(rect.Inflate(2), 4));
        }

        DrawBandLabels(ctx);

        if (_hover is { } h) DrawTooltip(ctx, h);
    }

    /// <summary>Names each band once, beside its first slot.</summary>
    private void DrawBandLabels(DrawingContext ctx)
    {
        foreach (var band in _placed.Select(p => p.Slot.Band).Distinct())
        {
            var first = _placed.First(p => p.Slot.Band == band).Rect;
            var t = new FormattedText(LabelFor(band), System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, Face, 9, LabelBrush);

            ctx.DrawText(t, new Point(first.X, first.Y - t.Height - 2));
        }
    }

    private void DrawTooltip(DrawingContext ctx, FittingSlot slot)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        var title = new FormattedText(
            slot.IsEmpty ? $"Empty {LabelFor(slot.Band).TrimEnd('S').ToLowerInvariant()} slot" : slot.Name,
            culture, FlowDirection.LeftToRight, BoldFace, 12, TipTitle);

        var body = new FormattedText(
            slot.Detail is { Length: > 0 } detail ? detail
            : IsReadOnly
                ? $"{LabelFor(slot.Band)} {slot.Index} · from assets — the game is the authority here"
                : slot.IsEmpty
                    ? "Click to fit a module"
                    : $"{LabelFor(slot.Band)} {slot.Index} · entered by hand",
            culture, FlowDirection.LeftToRight, Face, 10, TipBody);

        var w = Math.Max(title.Width, body.Width);
        var h = title.Height + body.Height + 4;

        var x = _hoverAt.X + 16;
        var y = _hoverAt.Y + 16;
        if (x + w + 16 > Bounds.Width)  x = _hoverAt.X - w - 22;
        if (y + h + 14 > Bounds.Height) y = _hoverAt.Y - h - 20;

        ctx.DrawRectangle(TipBack, TipPen, new RoundedRect(new Rect(x - 8, y - 6, w + 16, h + 12), 3));
        ctx.DrawText(title, new Point(x, y));
        ctx.DrawText(body,  new Point(x, y + title.Height + 4));
    }

    private void DrawCentred(DrawingContext ctx, string text)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, 12, LabelBrush);
        ctx.DrawText(t, new Point((Bounds.Width - t.Width) / 2, (Bounds.Height - t.Height) / 2));
    }

}
