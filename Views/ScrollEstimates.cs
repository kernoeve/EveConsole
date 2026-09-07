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
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// Pins a DataGrid's two scroll estimates to measured reality, so a grid with expandable rows can
/// be scrolled upward without fighting back.
///
/// <para><b>⚠️ The DataGrid cannot price a row it has not realised</b>, so it works from two
/// scalars — <c>RowHeightEstimate</c> and ONE <c>RowDetailsHeightEstimate</c> for every drawer in
/// the grid — and the scrollable extent is those multiplied out over everything off screen. What
/// makes this unfixable from the outside by tuning is HOW it derives the first:</para>
/// <code>
///   total  = verticalOffset - negVerticalOffset + sum(displayed row heights)
///   total -= openRowsUpToTheViewport * RowDetailsHeightEstimate
///   RowHeightEstimate = total / rowsSeenSoFar
/// </code>
/// <para>⚠️ That subtracts a GRID-WIDE mean from a LOCAL sample. Whatever single value the details
/// estimate holds, some window of rows has drawers taller than it and some has drawers shorter,
/// so the row estimate swings with wherever the viewport happens to be. Measured across one drag:
/// -9.0, -4.8, 1.5, 5.6, 39.6, 132.0, 337.5 — and the extent with it, from 3230 to 18856 against a
/// true content height of 6040. A negative row estimate means the extent SHRINKS as rows leave the
/// viewport; an inflated one means the scrollbar maximum collapses on the next pass. Either way
/// the offset is clamped to a maximum that has moved under it and the view is yanked back, which
/// is the sticking and jumping, and it is why the wheel and the scrollbar both do it while the
/// arrow keys do not — arrows walk slots exactly and never convert pixels at all.</para>
///
/// <para><b>So neither estimate is left to the grid.</b> Both are measured off the rows that have
/// been realised — body heights from the rows, drawer heights from the details presenters — and
/// written back, with <c>_lastEstimatedRow</c> pinned past the end so the formula above can never
/// run again and re-poison the row figure from a local sample.</para>
///
/// <para>What the extent then reduces to, with the two details terms cancelling:</para>
/// <code>
///   extent = realHeightAbove + realHeightDisplayed
///          + meanBody * rowsBelow + meanOpenDrawer * openRowsBelow
/// </code>
/// <para>which is the true remaining height to within the spread of the drawers themselves —
/// about 250px on 6040 here, where it had been thousands.</para>
///
/// <para>⚠️ Both members are internal with private setters and there is no public way in, so this
/// is reflection. It degrades rather than breaks: if a future Avalonia renames either one, the
/// grid keeps its own arithmetic and the scroll is no worse than it was.</para>
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

    private static readonly PropertyInfo? RowEstimate =
        typeof(DataGrid).GetProperty("RowHeightEstimate", Any);

    private static readonly PropertyInfo? DetailsEstimate =
        typeof(DataGrid).GetProperty("RowDetailsHeightEstimate", Any);

    /// <summary>
    /// The guard field on the recomputation: <c>if (LastScrollingSlot &gt;= _lastEstimatedRow)</c>.
    /// Pinned past any slot the grid can reach, so the row estimate stays whatever was measured.
    /// </summary>
    private static readonly FieldInfo? LastEstimatedRow =
        typeof(DataGrid).GetField("_lastEstimatedRow", Any);

    private static bool Usable =>
        RowEstimate?.CanWrite is true && DetailsEstimate?.CanWrite is true && LastEstimatedRow is not null;

    private sealed class Hook
    {
        public INotifyCollectionChanged?            Watching;
        public NotifyCollectionChangedEventHandler? OnChanged;
        public bool                                 Queued;

        public double AppliedRow     = -1;
        public double AppliedDrawer  = -1;

        /// <summary>Row heights WITHOUT their drawer, by row key.</summary>
        public readonly Dictionary<string, double> Bodies = [];

        /// <summary>Drawer heights, by row key.</summary>
        public readonly Dictionary<string, double> Drawers = [];
    }

    private static readonly ConditionalWeakTable<DataGrid, Hook> Hooks = new();

    static ScrollEstimates()
    {
        TrackProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.GetNewValue<bool>() is not true) return;

            grid.PropertyChanged += (_, args) =>
            {
                if (args.Property == DataGrid.ItemsSourceProperty) Rebind(grid);
            };

            // Opening or closing changes which drawers are being averaged.
            grid.RowDetailsVisibilityChanged += (_, _) => Queue(grid);

            // A drawer built for the first time, or scrolled back into view: measurable now.
            grid.LoadingRowDetails += (_, _) => Queue(grid);

            // A row realised while scrolling gives another body height.
            grid.LoadingRow += (_, _) => Queue(grid);

            grid.Sorting += (_, _) => Queue(grid);

            Rebind(grid);
        });
    }

    private static void Rebind(DataGrid grid)
    {
        var hook = Hooks.GetOrCreateValue(grid);

        if (hook.Watching is not null && hook.OnChanged is not null)
            hook.Watching.CollectionChanged -= hook.OnChanged;

        hook.Watching  = grid.ItemsSource as INotifyCollectionChanged;
        hook.OnChanged = (_, _) => Queue(grid);

        if (hook.Watching is not null) hook.Watching.CollectionChanged += hook.OnChanged;

        Queue(grid);
    }

    private static void Queue(DataGrid grid)
    {
        var hook = Hooks.GetOrCreateValue(grid);
        if (hook.Queued) return;
        hook.Queued = true;

        // ⚠️ Background, so layout has run and there are heights to read. Several triggers fire
        // together during one refresh; one pass per idle turn is enough.
        Dispatcher.UIThread.Post(
            () => { hook.Queued = false; Apply(grid); }, DispatcherPriority.Background);
    }

    private static void Apply(DataGrid grid)
    {
        if (!Usable || grid.GetVisualRoot() is null) return;

        var hook = Hooks.GetOrCreateValue(grid);

        // Measure whatever is realised. A row's body is its own height less its drawer, so an open
        // row contributes to both figures rather than being skipped.
        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.DataContext is not IExpandableRow item || row.Bounds.Height <= 1) continue;

            var drawer = row.AreDetailsVisible
                ? row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault()
                : null;

            var open = drawer is { Bounds.Height: > 1 } ? drawer.Bounds.Height : 0;
            if (open > 0) hook.Drawers[item.ExpandKey] = open;

            var body = row.Bounds.Height - open;
            if (body > 1) hook.Bodies[item.ExpandKey] = body;
        }

        if (hook.Bodies.Count == 0) return;

        // The row figure is the mean BODY, which is what the grid's own formula is trying to
        // recover and keeps getting wrong. Bodies barely vary, so this settles at once.
        var rows = hook.Bodies.Values.Average();

        // The drawer figure is the mean over the rows that are OPEN — the ones the grid will
        // actually multiply it by. Anything open but never realised has no measurement of its own
        // and falls back to the mean of the rest, which is the best guess available.
        double sum = 0;
        var    n   = 0;

        foreach (var key in OpenKeys(grid.ItemsSource))
            if (hook.Drawers.TryGetValue(key, out var h)) { sum += h; n++; }

        var drawers = n > 0                     ? sum / n
                    : hook.Drawers.Count > 0    ? hook.Drawers.Values.Average()
                    :                             0;

        var moved = Math.Abs(rows - hook.AppliedRow) >= 1 || Math.Abs(drawers - hook.AppliedDrawer) >= 1;

        hook.AppliedRow    = rows;
        hook.AppliedDrawer = drawers;

        RowEstimate!.SetValue(grid, rows);
        DetailsEstimate!.SetValue(grid, drawers);

        // ⚠️ AFTER the writes, every pass. This is what stops the grid recomputing the row figure
        // from whatever narrow window the viewport is sitting on.
        LastEstimatedRow!.SetValue(grid, int.MaxValue);

        if (moved) grid.InvalidateMeasure();
    }

    private static IEnumerable<string> OpenKeys(IEnumerable? items)
    {
        if (items is null) yield break;

        foreach (var item in items)
            if (item is IExpandableRow { IsExpanded: true } row) yield return row.ExpandKey;
    }
}
