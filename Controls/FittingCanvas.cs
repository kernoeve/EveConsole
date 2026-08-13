using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;

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
    bool        FromAssets = false)
{
    public bool IsEmpty => TypeId == 0;
}

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
        AffectsRender<FittingCanvas>(SlotsProperty, HullRenderProperty);

    public FittingCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
    }

    // ── Appearance ───────────────────────────────────────────────────────────

    private static readonly IBrush BackBrush  = new ImmutableSolidColorBrush(Color.Parse("#0b0b10"));
    private static readonly IBrush EmptyFill  = new ImmutableSolidColorBrush(Color.Parse("#14141e"));
    private static readonly IPen   EmptyPen   = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2a2a38")), 1);
    private static readonly IPen   HoverPen   = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#7fb8d8")), 1.5);
    private static readonly IBrush RingBrush  = new ImmutableSolidColorBrush(Color.Parse("#10101a"));
    private static readonly IPen   RingPen    = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#1e1e2a")), 1);

    // One colour per band, so a glance says which ring you are looking at without reading labels.
    private static readonly IBrush HighFill    = new ImmutableSolidColorBrush(Color.Parse("#3b5f7a"));
    private static readonly IBrush MidFill     = new ImmutableSolidColorBrush(Color.Parse("#3f6b5c"));
    private static readonly IBrush LowFill     = new ImmutableSolidColorBrush(Color.Parse("#6b4a3f"));
    private static readonly IBrush RigFill     = new ImmutableSolidColorBrush(Color.Parse("#5a4a6b"));
    private static readonly IBrush ServiceFill = new ImmutableSolidColorBrush(Color.Parse("#6b6440"));

    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.Parse("#8d8d9e"));
    private static readonly IBrush TipBack    = new ImmutableSolidColorBrush(Color.Parse("#f00e0e16"));
    private static readonly IPen   TipPen     = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a4a58")), 1);
    private static readonly IBrush TipTitle   = new ImmutableSolidColorBrush(Color.Parse("#e8e8f2"));
    private static readonly IBrush TipBody    = new ImmutableSolidColorBrush(Color.Parse("#9aa8b6"));

    private static readonly Typeface Face = Typeface.Default;
    private static readonly Typeface BoldFace =
        new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private const double SlotSize = 34;

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

    /// <summary>Clock position to degrees, measured the way atan2 does on screen: 0° east, angles
    /// increasing clockwise because Y grows downward. Twelve o'clock is therefore -90°.</summary>
    private static double Clock(double hour) => hour * 30 - 90;

    /// <summary>
    /// Preferred gap between neighbouring slots. A band uses this until it would outgrow the arc
    /// it is allotted, then tightens to fit.
    ///
    /// <para>⚠️ Both halves matter. A fixed step alone made a Fortizar's six high slots span 105°,
    /// which ran them into the mid band; an arc divided by the count alone strands three slots
    /// across a wide sweep and packs eight into the same space. Preferred-but-clamped keeps small
    /// bands compact and large ones inside their own territory.</para>
    /// </summary>
    private const double SlotStepDeg = 17;

    /// <summary>
    /// Places the slots in the arrangement the game uses — high at the top, mid on the right, low
    /// at the bottom, rigs on the left, services in a row underneath.
    ///
    /// <para>Each band is centred on a clock position and its slots step outward from that centre
    /// at a fixed angle, so a Keepstar's eight high slots and a Rifter's three both stay evenly
    /// spaced and neither collides with the band next door.</para>
    /// </summary>
    private void Layout()
    {
        _placed.Clear();

        var slots = Slots;
        if (slots is null || slots.Count == 0) return;

        var services = slots.Where(s => s.Band == FittingBand.Service)
                            .OrderBy(s => s.Index).ToList();

        // The service row lives below the circle, so the circle gives up that height.
        var reserved = services.Count > 0 ? SlotSize + 16 : 0;

        var cx = Bounds.Width / 2;
        var cy = (Bounds.Height - reserved) / 2;
        _radius = Math.Min(Bounds.Width, Bounds.Height - reserved) / 2 - SlotSize * 0.75;
        _centre = new Point(cx, cy);

        if (_radius <= SlotSize) return;

        void Band(FittingBand band, double centreHour, double halfWidthDeg)
        {
            var inBand = slots.Where(s => s.Band == band).OrderBy(s => s.Index).ToList();
            if (inBand.Count == 0) return;

            // Tighten only when the preferred step would push the band past its own arc.
            var step = inBand.Count > 1
                ? Math.Min(SlotStepDeg, halfWidthDeg * 2 / (inBand.Count - 1))
                : 0;

            // Centred on the clock position: with four slots the two middle ones straddle it.
            var start = Clock(centreHour) - step * (inBand.Count - 1) / 2.0;

            for (var i = 0; i < inBand.Count; i++)
            {
                var rad = (start + step * i) * Math.PI / 180;
                var x   = cx + Math.Cos(rad) * _radius;
                var y   = cy + Math.Sin(rad) * _radius;

                _placed.Add((inBand[i],
                    new Rect(x - SlotSize / 2, y - SlotSize / 2, SlotSize, SlotSize)));
            }
        }

        // Clock positions and arc widths taken from the in-game window: high 10-2, mid 2-3:30,
        // low 5-6:30, rigs 8-9. The half-widths are what keep neighbouring bands apart — they
        // are chosen so no two bands' arcs can meet however many slots a hull has.
        Band(FittingBand.High,      12,   45);
        Band(FittingBand.Mid,        2.75, 22);
        Band(FittingBand.Low,        5.75, 22);
        Band(FittingBand.Rig,        8.5,  16);
        Band(FittingBand.Subsystem, 10.5,  16);

        // Services are a straight row below the circle: there can be seven, and an arc that long
        // reads as another module band rather than as something different in kind.
        if (services.Count > 0)
        {
            var totalW = services.Count * SlotSize + (services.Count - 1) * 6;
            var x0     = cx - totalW / 2;
            var y0     = Bounds.Height - SlotSize - 6;

            for (var i = 0; i < services.Count; i++)
                _placed.Add((services[i],
                    new Rect(x0 + i * (SlotSize + 6), y0, SlotSize, SlotSize)));
        }
    }

    private Point  _centre;
    private double _radius;

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
            Cursor = new Cursor(hit is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

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
            slot.IsEmpty
                ? "Click to fit a module"
                : $"{LabelFor(slot.Band)} {slot.Index} · known from {(slot.FromAssets ? "assets" : "manual entry")}",
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
