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
/// Keeps a DataGrid's row-details height estimate equal to the MEAN of the drawers that are
/// actually open, so the grid's scroll arithmetic comes out right.
///
/// <para><b>⚠️ Why a grid with expandable rows fights you scrolling UP.</b> The DataGrid cannot
/// know the height of a row it has not realised, so it prices them from two scalars:
/// <c>RowHeightEstimate</c> and <c>RowDetailsHeightEstimate</c> — ONE figure for every drawer in
/// the grid. And the second is not merely used off screen. <c>EdgedRowsHeightCalculated</c>
/// derives the first FROM the second:</para>
/// <code>
///   total  = verticalOffset - negVerticalOffset + sum(displayed row heights)
///   total -= openRowsAboveAndInViewport * RowDetailsHeightEstimate
///   RowHeightEstimate = total / rowsSoFar
/// </code>
/// <para>It subtracts one estimate per open row to recover an average row BODY height. Get the
/// details figure wrong and the row figure is wrong by however much, multiplied by the number of
/// open rows — and it is then applied to every unrealised row in the grid.</para>
///
/// <para><b>⚠️ Both ways of being wrong were measured here, and the second was worse.</b> Avalonia
/// takes the figure by building the details template against <c>GetDataItem(0)</c>, and only when
/// the template is set, when the control template is applied, and at the grid's first measure —
/// all of which happen before a worklist has loaded. Against a null item an empty drawer measures
/// to its padding, so the grid believed every drawer was about fifteen pixels tall and inflated
/// the row estimate instead. Re-measuring it against a real row 0 replaced that with 1264 — row
/// 0's own drawer, far taller than the 424-748 typical here — which over-subtracted so hard that
/// <c>RowHeightEstimate</c> went NEGATIVE (measured: -9.0). Every unrealised row was then priced
/// at minus nine pixels, so the extent SHRANK as rows left the viewport: scrolling up, the
/// scrollbar maximum collapsed 3719, 2515, 1456, 656 while the offset was still 1595, the value
/// was clamped to the maximum, and the grid was pulled back down. That is the flicker, and it is
/// why it only happened upward and only with rows open.</para>
///
/// <para><b>The right figure is the mean of the open drawers</b>, because that is exactly what
/// makes the subtraction above recover the true body height. Checked against a live log: mean
/// 733 over six open rows gives RowHeightEstimate 40.6 where the real closed rows measure 32-43,
/// and a total extent of 5453 against a true 5442. So drawers are measured off the realised rows
/// and the mean is written back.</para>
///
/// <para>⚠️ Written by reflection, and the fallback matters. <c>RowDetailsHeightEstimate</c> is
/// internal with a private setter and there is no public way in; assigning
/// <see cref="DataGrid.RowDetailsTemplate"/> re-runs Avalonia's own measurement, which is the
/// 1264 case above — better than fifteen, worse than the mean, and all that is left if a future
/// Avalonia renames the member.</para>
/// </summary>
public sealed class DetailsEstimate
{
    private DetailsEstimate() { }

    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<DetailsEstimate, DataGrid, bool>("Track");

    public static void SetTrack(DataGrid grid, bool value) => grid.SetValue(TrackProperty, value);
    public static bool GetTrack(DataGrid grid) => grid.GetValue(TrackProperty);

    private const BindingFlags Any =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>The estimate itself. Internal, private setter — see the class note.</summary>
    private static readonly PropertyInfo? Estimate =
        typeof(DataGrid).GetProperty("RowDetailsHeightEstimate", Any);

    /// <summary>
    /// Clears <c>_lastEstimatedRow</c>.
    ///
    /// <para>⚠️ Needed, not tidiness. The row estimate is only recomputed when the viewport has
    /// reached at least as far down the list as it ever has (<c>LastScrollingSlot</c> at or past
    /// <c>_lastEstimatedRow</c>), which is precisely never while scrolling up — so one bad
    /// reading at the bottom is frozen for the rest of the session. The -9.0 above was identical
    /// on all 297 logged wheel ticks.</para>
    /// </summary>
    private static readonly MethodInfo? Reset =
        typeof(DataGrid).GetMethod("InvalidateRowHeightEstimate", Any);

    private sealed class Hook
    {
        public INotifyCollectionChanged?            Watching;
        public NotifyCollectionChangedEventHandler? OnChanged;
        public bool                                 Queued;

        /// <summary>Last value written, so an unchanged pass does not invalidate layout.</summary>
        public double Applied = -1;

        /// <summary>Drawer heights by row key, kept as rows are realised.</summary>
        public readonly Dictionary<string, double> Drawers = [];
    }

    private static readonly ConditionalWeakTable<DataGrid, Hook> Hooks = new();

    static DetailsEstimate()
    {
        TrackProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.GetNewValue<bool>() is not true) return;

            grid.PropertyChanged += (_, args) =>
            {
                if (args.Property == DataGrid.ItemsSourceProperty) Rebind(grid);
            };

            // Opening or closing a drawer changes the set being averaged.
            grid.RowDetailsVisibilityChanged += (_, _) => Queue(grid);

            // A drawer built for the first time, or scrolled back into view: measurable now.
            grid.LoadingRowDetails += (_, _) => Queue(grid);

            // Sorting moves rows, so a drawer may arrive that has never been measured.
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

        // ⚠️ Background, so layout has run and the drawers have a height to read. Several of the
        // triggers above fire together during one refresh; one pass per idle turn is enough.
        Dispatcher.UIThread.Post(
            () => { hook.Queued = false; Apply(grid); }, DispatcherPriority.Background);
    }

    private static void Apply(DataGrid grid)
    {
        if (grid.GetVisualRoot() is null) return;

        var hook = Hooks.GetOrCreateValue(grid);

        // Whatever is realised and open, measured off the presenter itself rather than inferred
        // from the row height, which would need a body height this does not know.
        foreach (var row in grid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (!row.AreDetailsVisible || row.DataContext is not IExpandableRow item) continue;

            var drawer = row.GetVisualDescendants().OfType<DataGridDetailsPresenter>().FirstOrDefault();
            if (drawer is { Bounds.Height: > 1 }) hook.Drawers[item.ExpandKey] = drawer.Bounds.Height;
        }

        if (hook.Drawers.Count == 0) return;

        // The mean over the rows that are OPEN — the ones the grid will actually multiply this by.
        double sum = 0;
        var    n   = 0;

        foreach (var key in OpenKeys(grid.ItemsSource))
            if (hook.Drawers.TryGetValue(key, out var h)) { sum += h; n++; }

        // Nothing open: the grid multiplies this by a count of zero, so leave it be.
        if (n == 0) return;

        var mean = sum / n;
        if (Math.Abs(mean - hook.Applied) < 1) return;

        hook.Applied = mean;

        if (Estimate?.CanWrite is true)
        {
            Estimate.SetValue(grid, mean);
            Reset?.Invoke(grid, null);
        }
        else if (grid.RowDetailsTemplate is { } template)
        {
            // Fallback only. Avalonia re-measures against row 0, which is the 1264 case.
            grid.RowDetailsTemplate = null;
            grid.RowDetailsTemplate = template;
        }

        grid.InvalidateMeasure();
    }

    private static IEnumerable<string> OpenKeys(IEnumerable? items)
    {
        if (items is null) yield break;

        foreach (var item in items)
            if (item is IExpandableRow { IsExpanded: true } row) yield return row.ExpandKey;
    }
}
