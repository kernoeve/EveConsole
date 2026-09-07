using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Makes a DataGrid whose rows carry variable-height drawers scroll upward without fighting back.
///
/// <para><b>⚠️ Two faults, both in how the grid turns pixels into a position.</b> Diagnosed in
/// <c>tools/gridsim</c>, a headless rig that drives a grid of this shape and compares what it
/// believes against geometry known exactly. Run it after touching this file.</para>
///
/// <para><b>One: the extent.</b> The grid prices rows it has not realised from two scalars, and it
/// derives the first FROM the second — <c>EdgedRowsHeightCalculated</c> sums the offset and the
/// displayed rows, subtracts one <c>RowDetailsHeightEstimate</c> per open row, and divides. That
/// subtracts a GRID-WIDE mean from a LOCAL sample, so whatever single value the details estimate
/// holds, some window of rows has drawers taller than it and some shorter. Measured across one
/// drag: -9.0, -4.8, 1.5, 5.6, 39.6, 132.0, 337.5, and the scrollbar maximum with it, 3230 to
/// 18856 against a true 6040. No choice of constant fixes that, so neither estimate is left to the
/// grid: both are measured off realised rows and written back, with <c>_lastEstimatedRow</c>
/// pinned past the end so the formula can never run again.</para>
///
/// <para><b>Two: the landing.</b> Avalonia converts a scroll by walking rows and measuring each one
/// on the way past, and a row measured while it is OFF screen reports the height of whichever row
/// was recycled into it — <c>SetDetailsVisibilityInternal</c> does nothing when the flag is
/// unchanged, so a row reused from one open row to another keeps the previous drawer height. In
/// the rig slot 3 read 1231, row 7's height, when it is 842: the walk landed 1181px into an 842px
/// row, the guard bounced it to the next slot, the next tick put it back, and the view sat in a
/// two-cycle while the offset drained. That is the sticking, and the jump is it breaking out.
/// So the position is computed here instead, from measured heights, and the grid's landing is
/// overwritten.</para>
///
/// <para>Arrow keys were always fine, which is what separated the two paths: they walk slots and
/// never convert pixels at all.</para>
///
/// <para>⚠️ Internal members, by reflection, and it degrades rather than breaks — if a future
/// Avalonia renames one, the grid keeps its own arithmetic and nothing is worse than before.</para>
/// </summary>
public sealed class ScrollEstimates
{
    private ScrollEstimates() { }

    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<ScrollEstimates, DataGrid, bool>("Track");

    public static void SetTrack(DataGrid grid, bool value) => grid.SetValue(TrackProperty, value);
    public static bool GetTrack(DataGrid grid) => grid.GetValue(TrackProperty);

    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly PropertyInfo? RowEstimate     = typeof(DataGrid).GetProperty("RowHeightEstimate", Any);
    private static readonly PropertyInfo? DrawerEstimate  = typeof(DataGrid).GetProperty("RowDetailsHeightEstimate", Any);
    private static readonly PropertyInfo? NegOffset       = typeof(DataGrid).GetProperty("NegVerticalOffset", Any);
    private static readonly PropertyInfo? Cells           = typeof(DataGrid).GetProperty("CellsEstimatedHeight", Any);
    private static readonly PropertyInfo? Display         = typeof(DataGrid).GetProperty("DisplayData", Any);
    private static readonly FieldInfo?    LastEstimated   = typeof(DataGrid).GetField("_lastEstimatedRow", Any);
    private static readonly FieldInfo?    VerticalOffset  = typeof(DataGrid).GetField("_verticalOffset", Any);
    private static readonly FieldInfo?    VScrollBar      = typeof(DataGrid).GetField("_vScrollBar", Any);
    private static readonly MethodInfo?   UpdateRows      = typeof(DataGrid).GetMethod("UpdateDisplayedRows", Any);

    private static readonly bool Usable =
        RowEstimate?.CanWrite is true && DrawerEstimate?.CanWrite is true &&
        NegOffset?.CanWrite is true && LastEstimated is not null &&
        VerticalOffset is not null && VScrollBar is not null &&
        Cells is not null && Display is not null && UpdateRows is not null;

    /// <summary>One wheel notch, matching DataGrid's own DATAGRID_mouseWheelDelta.</summary>
    private const double Notch = 50;

    private sealed class Hook
    {
        public INotifyCollectionChanged?            Watching;
        public NotifyCollectionChangedEventHandler? OnChanged;
        public ScrollBar?                           Bar;
        public bool                                 Busy;

        /// <summary>Trusted row heights, by index.</summary>
        public readonly Dictionary<int, double> Heights = [];

        /// <summary>Heights seen once. ⚠️ A height is only trusted after two passes agree: a
        /// recycled row can be consistently wrong for a whole pass, and one bad sample poisons the
        /// table for good — row 0 went in at 1231 against its real 1292, and every later step
        /// across that boundary moved 111px for a 50px ask.</summary>
        public readonly Dictionary<int, double> Seen = [];

        public double MeanBody   = 22;
        public double MeanDrawer;

        /// <summary>Our own offset, moved only by what was asked for. ⚠️ Not simply read back:
        /// the grid overwrites its own offset when its walk reaches the first row, assigning
        /// NegVerticalOffset outright, so an error in the walk becomes an error in the offset.</summary>
        public double Offset = -1;
    }

    private static readonly ConditionalWeakTable<DataGrid, Hook> Hooks = new();

    static ScrollEstimates()
    {
        TrackProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.GetNewValue<bool>() is not true || !Usable) return;

            grid.PropertyChanged += (_, args) =>
            {
                if (args.Property == DataGrid.ItemsSourceProperty) Rebind(grid);
            };

            // What a scroll ASKED for, before the grid gets to interpret it.
            grid.AddHandler(InputElement.PointerWheelChangedEvent,
                (_, args) => Ask(grid, -args.Delta.Y * Notch), RoutingStrategies.Tunnel);

            grid.LayoutUpdated += (_, _) => Pass(grid);

            Rebind(grid);
        });
    }

    private static void Rebind(DataGrid grid)
    {
        var hook = Hooks.GetOrCreateValue(grid);

        if (hook.Watching is not null && hook.OnChanged is not null)
            hook.Watching.CollectionChanged -= hook.OnChanged;

        // ⚠️ Heights are keyed by row index, and a rebuild moves every index.
        hook.Heights.Clear();
        hook.Seen.Clear();
        hook.Offset = -1;

        hook.Watching  = grid.ItemsSource as INotifyCollectionChanged;
        hook.OnChanged = (_, _) => { hook.Heights.Clear(); hook.Seen.Clear(); hook.Offset = -1; };

        if (hook.Watching is not null) hook.Watching.CollectionChanged += hook.OnChanged;
    }

    // ── Reading and writing the grid's own numbers ───────────────────────────

    private static double D(object? v) => v is double d ? d : 0;
    private static int    I(object? v) => v is int i ? i : 0;

    private static double Height(Hook hook, IList? items, int index)
    {
        if (hook.Heights.TryGetValue(index, out var h)) return h;

        var open = items is not null && index < items.Count &&
                   items[index] is IExpandableRow { IsExpanded: true };

        return hook.MeanBody + (open ? hook.MeanDrawer : 0);
    }

    private static double Total(Hook hook, IList? items, int count)
    {
        var total = 0.0;
        for (var i = 0; i < count; i++) total += Height(hook, items, i);
        return total;
    }

    private static void Ask(DataGrid grid, double pixels)
    {
        var hook = Hooks.GetOrCreateValue(grid);
        if (hook.Offset < 0) hook.Offset = D(VerticalOffset!.GetValue(grid));

        var items = grid.ItemsSource as IList;
        var max   = Math.Max(0, Total(hook, items, items?.Count ?? 0) - D(Cells!.GetValue(grid)));

        hook.Offset = Math.Clamp(hook.Offset + pixels, 0, max);
    }

    // ── The pass, after every layout ─────────────────────────────────────────

    private static void Pass(DataGrid grid)
    {
        var hook = Hooks.GetOrCreateValue(grid);
        if (hook.Busy || grid.GetVisualRoot() is null) return;

        hook.Busy = true;
        try { Measure(grid, hook); Place(grid, hook); }
        catch { /* never break the grid over a diagnostic correction */ }
        finally { hook.Busy = false; }
    }

    private static void Measure(DataGrid grid, Hook hook)
    {
        var bodies  = new List<double>();
        var drawers = new List<double>();

        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.Index < 0 || row.Bounds.Height <= 1) continue;

            var presenter = row.AreDetailsVisible
                ? row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault()
                : null;

            var drawer = presenter is { Bounds.Height: > 1 } ? presenter.Bounds.Height : 0;
            if (drawer > 0) drawers.Add(drawer);

            var body = row.Bounds.Height - drawer;
            if (body > 1) bodies.Add(body);

            // Settled only: mid-recycle a row's arranged bounds and its measured size disagree.
            if (Math.Abs(row.DesiredSize.Height - row.Bounds.Height) >= 0.5) continue;

            if (hook.Seen.TryGetValue(row.Index, out var once) && Math.Abs(once - row.Bounds.Height) < 0.5)
                hook.Heights[row.Index] = row.Bounds.Height;

            hook.Seen[row.Index] = row.Bounds.Height;
        }

        if (bodies.Count == 0) return;

        hook.MeanBody   = bodies.Average();
        hook.MeanDrawer = drawers.Count > 0 ? drawers.Average() : 0;

        RowEstimate!.SetValue(grid, hook.MeanBody);
        DrawerEstimate!.SetValue(grid, hook.MeanDrawer);
        LastEstimated!.SetValue(grid, int.MaxValue);
    }

    private static void Place(DataGrid grid, Hook hook)
    {
        var items = grid.ItemsSource as IList;
        var count = items?.Count ?? 0;
        if (count == 0) return;

        var cells  = D(Cells!.GetValue(grid));
        var max    = Math.Max(0, Total(hook, items, count) - cells);
        var theirs = D(VerticalOffset!.GetValue(grid));

        // ⚠️ Ours only while the two are CLOSE. The grid loses a little of the offset whenever its
        // walk reaches the first row; but it also relocates wholesale — snapping to the bottom,
        // resetting to the top — and overriding THAT would mean moving the displayed set a long way
        // from outside a layout pass, which corrupts DisplayData outright. Small disagreements are
        // ours to correct; large ones are the grid relocating, so resync to it.
        var span   = hook.MeanBody + hook.MeanDrawer;
        var target = hook.Offset < 0 || Math.Abs(hook.Offset - theirs) > span ? theirs : hook.Offset;

        target      = Math.Clamp(target, 0, max);
        hook.Offset = target;

        if (Math.Abs(theirs - target) > 0.5) VerticalOffset.SetValue(grid, target);

        // Which row the view starts on, and how far into it, from heights that were measured.
        var slot = 0;
        var acc  = 0.0;
        while (slot < count - 1 && acc + Height(hook, items, slot) <= target)
        {
            acc += Height(hook, items, slot);
            slot++;
        }

        var neg     = Math.Max(0, target - acc);
        var display = Display!.GetValue(grid);

        if (display is not null)
        {
            if (Math.Abs(D(NegOffset!.GetValue(grid)) - neg) > 0.5) NegOffset.SetValue(grid, neg);

            // ⚠️ Only when the FIRST ROW changes. UpdateDisplayedRows expects to run inside a layout
            // pass; rebuilding the displayed set from outside one is survivable for a neighbouring
            // slot and not for a distant one — hence the resync above, which keeps this small.
            var first = display.GetType().GetProperty("FirstScrollingSlot", Any);
            if (first is not null && I(first.GetValue(display)) != slot)
                UpdateRows!.Invoke(grid, [slot, cells]);
        }

        // The extent, summed from measured heights rather than multiplied out over rows the grid
        // has never seen. ⚠️ It rewrites this from its own arithmetic on every layout, which is why
        // this runs in LayoutUpdated — after, not before.
        if (VScrollBar!.GetValue(grid) is not ScrollBar bar) return;

        hook.Bar = bar;
        if (Math.Abs(bar.Maximum - max) > 0.5)      bar.Maximum      = max;
        if (Math.Abs(bar.ViewportSize - cells) > 0.5) bar.ViewportSize = cells;

        var value = Math.Clamp(target, 0, max);
        if (Math.Abs(bar.Value - value) > 0.5) bar.Value = value;
    }
}
