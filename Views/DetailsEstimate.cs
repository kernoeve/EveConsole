using System;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace EveConsole.Views;

/// <summary>
/// Keeps a DataGrid's row-details height estimate measured against a row that exists.
///
/// <para><b>⚠️ Why a grid with expandable rows fights you when you scroll UP.</b> The DataGrid
/// does not know the height of a row it has not realised. It works with two numbers instead:
/// <c>RowHeightEstimate</c>, an average, and <c>RowDetailsHeightEstimate</c> — ONE figure for
/// every drawer in the grid. The scrollable extent is those two multiplied out over everything
/// off screen, so the extent is only as good as they are.</para>
///
/// <para>The details figure is taken by building the details template against
/// <c>GetDataItem(0)</c> and measuring it, and it is taken exactly three times: when the template
/// is set, when the control template is applied, and at the grid's first measure. All three
/// happen before a worklist has loaded, when there is no row 0 — so the template is built against
/// a null item, an empty list measures to its padding, and the grid spends the rest of the
/// session believing every open drawer is about fifteen pixels tall.</para>
///
/// <para>That number is not merely used for rows off screen. <c>EdgedRowsHeightCalculated</c>
/// SUBTRACTS it once per open row to recover an average row height — so with it near zero, the
/// real height of every open drawer above the viewport is averaged into the row estimate
/// instead, and every unrealised row is then priced as if it were a drawer. Scrolling down the
/// average is recomputed constantly and the thumb merely breathes; scrolling up it is frozen
/// (the guard is <c>LastScrollingSlot &gt;= _lastEstimatedRow</c>), so passing one tall row
/// removes its real height from the offset and adds back one average — the extent collapses, the
/// scrollbar maximum falls below where the view already is, and the grid is pulled back down.
/// That is the flicker, and it is why it only happens upward and only with rows open.</para>
///
/// <para>There is no public setter for the estimate. There is a public trigger: assigning
/// <see cref="DataGrid.RowDetailsTemplate"/> re-runs the measurement. So this re-assigns it once
/// the grid actually has rows.</para>
/// </summary>
public sealed class DetailsEstimate
{
    private DetailsEstimate() { }

    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<DetailsEstimate, DataGrid, bool>("Track");

    public static void SetTrack(DataGrid grid, bool value) => grid.SetValue(TrackProperty, value);
    public static bool GetTrack(DataGrid grid) => grid.GetValue(TrackProperty);

    /// <summary>Per-grid state: what we are listening to, and whether a pass is already queued.</summary>
    private sealed class Hook
    {
        public INotifyCollectionChanged?             Watching;
        public NotifyCollectionChangedEventHandler?  OnChanged;
        public bool                                  Queued;
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

            // Sorting changes which row is first, and the first row is the one measured.
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

        // ⚠️ Background, so the grid has taken the new rows first. Asking before it has any is
        // precisely how the estimate came to be measured against nothing, and a refresh raises
        // several notifications — one pass per idle turn is enough.
        Dispatcher.UIThread.Post(
            () => { hook.Queued = false; Remeasure(grid); }, DispatcherPriority.Background);
    }

    private static void Remeasure(DataGrid grid)
    {
        if (grid.RowDetailsTemplate is not { } template) return;
        if (grid.GetVisualRoot() is null) return;
        if (!HasAny(grid.ItemsSource)) return;

        // ⚠️ Null and back reads like a no-op and is not. DataGridRow.ApplyDetailsTemplate skips
        // a null template and then skips an unchanged one, so no row is rebuilt and no drawer
        // closes — but the grid's own property handler calls UpdateRowDetailsHeightEstimate on
        // the way through, which is the whole point and has no other way in from outside.
        grid.RowDetailsTemplate = null;
        grid.RowDetailsTemplate = template;
    }

    private static bool HasAny(IEnumerable? items)
    {
        if (items is null) return false;

        var e = items.GetEnumerator();
        try { return e.MoveNext(); }
        finally { (e as IDisposable)?.Dispose(); }
    }
}
