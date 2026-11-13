using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class WorklistView : ReactiveUserControl<WorklistViewModel>
{
    public WorklistView()
    {
        InitializeComponent();

        // Bubble rather than tunnel: the inner TextBox of an AutoCompleteBox is what actually
        // takes focus, and it is only identifiable once the event reaches us carrying it as the
        // source.
        AddHandler(GotFocusEvent, OnFieldFocused, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble);
    }

    private async void OnCopyBuyList(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorklistViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        var main = top as MainWindow;
        try
        {
            var text = WorklistViewModel.FormatBuyList(vm.Rows);
            if (text.Length == 0)
            {
                main?.ShowToast(IndustryText.ToastNothingToCopy);
                return;
            }
            if (top?.Clipboard is not { } clipboard) return;
            await clipboard.SetTextAsync(text);
            main?.ShowToast(IndustryText.ToastCopied);
        }
        catch (Exception ex)
        {
            main?.ShowToast(string.Format(CommonText.ErrorWithMessage, ex.Message));
        }
    }

    // ── Item Needs headings ───────────────────────────────────────────────────

    /// <summary>
    /// An item's heading in Item Needs: each column from Total to the right summed over the
    /// stations under it, by the same numbers the rows sort on. The "asked for by" rows under a
    /// station are its detail and aren't counted again.
    /// </summary>
    public static readonly Func<DataGridColumn, IReadOnlyList<object>, ColumnTotal?> ItemNeedTotals = (column, items) =>
    {
        var rows = items.OfType<StationNeedRowVm>().Where(r => !r.IsChild).ToList();
        if (rows.Count == 0) return null;
        static string Some(long v) => v > 0 ? v.ToString("N0") : "";
        // Value and volume as the rows have them: only where a station is short.
        var shortRows = rows.Where(r => r.ShortRaw > 0).ToList();
        return column.SortMemberPath switch
        {
            nameof(StationNeedRowVm.TotalRaw)  => new ColumnTotal(rows.Sum(r => r.TotalRaw).ToString("N0")),
            nameof(StationNeedRowVm.OnHandRaw) => new ColumnTotal(rows.Sum(r => r.OnHandRaw).ToString("N0")),
            nameof(StationNeedRowVm.InBuildRaw) => new ColumnTotal(Some(rows.Sum(r => r.InBuildRaw))),
            nameof(StationNeedRowVm.ShortAfterBuildRaw) => rows.Sum(r => r.ShortAfterBuildRaw) is var s and > 0
                ? new ColumnTotal(s.ToString("N0"), EveConsole.Services.Palette.Bad) : null,
            nameof(StationNeedRowVm.ShortValueRaw)  => shortRows.Count == 0 ? null
                : new ColumnTotal(StationNeedRowVm.Isk(shortRows.Sum(r => r.ShortValueRaw))),
            nameof(StationNeedRowVm.ShortVolumeRaw) => shortRows.Count == 0 ? null
                : new ColumnTotal($"{shortRows.Sum(r => r.ShortVolumeRaw):N0} m³"),
            nameof(StationNeedRowVm.OrderJobsRaw) => new ColumnTotal(Some(rows.Sum(r => r.OrderJobsRaw))),
            nameof(StationNeedRowVm.JobsRaw)      => new ColumnTotal(Some(rows.Sum(r => r.JobsRaw))),
            nameof(StationNeedRowVm.InvLevelsRaw) => new ColumnTotal(Some(rows.Sum(r => r.InvLevelsRaw))),
            nameof(StationNeedRowVm.StnLevelsRaw) => new ColumnTotal(Some(rows.Sum(r => r.StnLevelsRaw))),
            _ => null,
        };
    };

    /// <summary>An item's heading names the item: open it, by the id its rows carry.</summary>
    private void OnOpenNeedGroupItem(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DataGridCollectionViewGroup group)
            group.Items.OfType<StationNeedRowVm>().FirstOrDefault(r => r.HasItemLink)?.OpenItem();
    }

    /// <summary>The items opened by hand, by name, kept open when the list is grouped afresh.</summary>
    private readonly HashSet<object> _openItemNeeds = [];
    private WorklistViewModel? _itemNeedsFor;

    /// <summary>
    /// Item Needs opens with every item closed: hundreds of items, read by their totals first, and
    /// opened where a total needs explaining. Grouped afresh (a reload, a sort), they close again,
    /// except those the reader opened.
    /// </summary>
    private void WatchItemNeeds()
    {
        if (_itemNeedsFor is not null) _itemNeedsFor.ItemNeedsView.CollectionChanged -= OnItemNeedsChanged;
        _itemNeedsFor = DataContext as WorklistViewModel;
        if (_itemNeedsFor is null) return;
        _itemNeedsFor.ItemNeedsView.CollectionChanged += OnItemNeedsChanged;
        CloseItemNeedsLater();
    }

    private void OnItemNeedsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) CloseItemNeedsLater();
    }

    // Posted: the grid builds its groups from the same Reset, after this hears of it.
    private void CloseItemNeedsLater() => Dispatcher.UIThread.Post(CloseItemNeeds, DispatcherPriority.Background);

    private void CloseItemNeeds()
    {
        if (_itemNeedsFor is not { } vm || this.FindControl<DataGrid>("ItemNeedsGrid") is not { } grid) return;
        foreach (var group in vm.ItemNeedsView.Groups?.OfType<DataGridCollectionViewGroup>() ?? [])
            if (group.Key is not { } key || !_openItemNeeds.Contains(key)) grid.CollapseRowGroup(group, true);
    }

    /// <summary>The reader opened or closed an item: remembered for the next time it's grouped.</summary>
    private void OnItemNeedsGroupToggled(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: DataGridCollectionViewGroup { Key: { } key } } toggle) return;
        if (toggle.IsChecked == true) _openItemNeeds.Add(key);
        else _openItemNeeds.Remove(key);
    }

    private WorklistViewModel? _shownFor;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_shownFor is not null) _shownFor.ShowRow -= OnShowRow;
        _shownFor = DataContext as WorklistViewModel;
        if (_shownFor is not null) _shownFor.ShowRow += OnShowRow;
        WatchItemNeeds();
    }

    private void OnShowRow(WorklistRowVm row)
    {
        if (this.FindControl<DataGrid>("TaskGrid") is not { } grid) return;
        grid.SelectedItem = row;
        grid.ScrollIntoView(row, null);
    }

    /// <summary>
    /// Fixes a grid's row height at what its rows already measure, once.
    ///
    /// <para>⚠️ What lets a loop open in the grid without breaking scrolling. A DataGrid prices the
    /// rows it has not drawn from those it has, and scrolling up through rows of different heights
    /// lands in the wrong place — the flicker the old drawers had. A loop's stops are rows of their
    /// own, and with every row the SAME height there is nothing to price: tools/gridsim scores that
    /// perfect. Measured rather than written down, so it holds at any font and UI scale; the tasks'
    /// rows are single lines by design, so the tallest one realised is the height of them all.</para>
    ///
    /// <para>Set through the public property and posted out of the layout pass — nothing here
    /// touches the grid's own bookkeeping, which is what went wrong the last time a fix reached in.</para>
    /// </summary>
    private void PinRowHeight(DataGrid grid)
    {
        if (!_pinning.Contains(grid) || !double.IsNaN(grid.RowHeight)) return;

        var heights = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.IsVisible && r.DataContext is IFamilyRow { IsChild: false } && r.Bounds.Height > 1)
            .Select(r => r.Bounds.Height)
            .ToList();
        if (heights.Count < 3) return;

        // Removed from the set rather than unsubscribed: the handler is a closure, and a grid that
        // has its height needs nothing more from it.
        _pinning.Remove(grid);
        var height = Math.Ceiling(heights.Max());
        Dispatcher.UIThread.Post(() => grid.RowHeight = height);
    }

    private void OnRowLoading(object? sender, DataGridRowEventArgs e)
    {
        var grid = (DataGrid)sender!;

        // A detail row draws only in the columns marked for it — see the style at the top of the
        // view. Set on every load, because a recycled row can change from one to the other.
        e.Row.Classes.Set("child", e.Row.DataContext is IFamilyRow { IsChild: true });

        if (e.Row.DataContext is IFamilyRow && double.IsNaN(grid.RowHeight) && _pinning.Add(grid))
            grid.LayoutUpdated += (_, _) => PinRowHeight(grid);

        // Rows load before anything in them can take focus, so the watch is in place first.
        if (grid.ItemsSource is DataGridCollectionView { SourceCollection: IAnnouncesChanges list } && _watched.Add((grid, list)))
            list.Changing += (_, _) => DropRowFocus(grid);
    }

    /// <summary>Grids watching their list for a change about to happen, with the list watched.</summary>
    private readonly HashSet<(DataGrid, IAnnouncesChanges)> _watched = [];

    /// <summary>
    /// Takes keyboard focus out of <paramref name="grid"/> just before its rows change.
    ///
    /// <para>⚠️ Without this a clicked row turns into a blank line. The DataGrid keeps the row holding
    /// focus out of its recycling, hidden when scrolled away, and adds and removes rows around it by
    /// hand; a refresh or a loop opened while it is kept leaves a hidden row standing in a slot on
    /// screen — see IAnnouncesChanges. Focus leaving the grid is what makes it let that row go.
    /// Only when focus is in the grid: a refresh never takes focus from a field elsewhere.</para>
    /// </summary>
    private static void DropRowFocus(DataGrid grid)
    {
        if (grid.IsKeyboardFocusWithin) TopLevel.GetTopLevel(grid)?.FocusManager?.ClearFocus();
    }

    /// <summary>Grids whose row height is waiting to be pinned.</summary>
    private readonly HashSet<DataGrid> _pinning = [];

    /// <summary>
    /// Applies a tab the Overview asked for, once this view's own TabControl binding is live.
    /// See WorklistViewModel.RequestedTab for why it cannot simply be set from the caller.
    /// </summary>
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is WorklistViewModel vm && vm.TakeRequestedTab() is { } tab)
            vm.OuterTabIndex = tab;
    }

    /// <summary>Set when a click focused a select-all field, so the selection can be reapplied
    /// after the click finishes placing the caret.</summary>
    private TextBox? _selectAfterClick;

    /// <summary>
    /// Selects what is already in a field marked <c>selectall</c>, so typing replaces it.
    ///
    /// <para>These fields deliberately keep their value after an add — rules and levels are
    /// entered a station at a time — so the retained value has to be trivial to clear. Scoped by
    /// class rather than applied to every text field: select-on-focus is right for a field you
    /// retype wholesale and wrong for one you edit, and the job-length boxes are the latter.</para>
    ///
    /// <para>⚠️ Focus alone only covers tabbing. A mouse click focuses the box and <em>then</em>
    /// places the caret, which clears whatever was selected here — so a pointer-driven focus is
    /// remembered and reapplied in <see cref="OnPointerReleased"/> once the click is done.</para>
    /// </summary>
    private void OnFieldFocused(object? sender, GotFocusEventArgs e)
    {
        if (e.Source is not TextBox { Text.Length: > 0 } box || !IsSelectAll(box)) return;

        box.SelectAll();
        if (e.NavigationMethod == NavigationMethod.Pointer) _selectAfterClick = box;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_selectAfterClick is not { } box) return;
        _selectAfterClick = null;
        box.SelectAll();
    }

    /// <summary>The class sits on the AutoCompleteBox; the TextBox that takes focus is its
    /// templated child.</summary>
    private static bool IsSelectAll(TextBox box) =>
        (box.FindAncestorOfType<AutoCompleteBox>() as StyledElement ?? box).Classes.Contains("selectall");

    /// <summary>The station a haul is bound for.</summary>
    private void OnOpenLocation(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenLocation();

    private void OnOpenCharacter(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenCharacter();

    /// <summary>The row's own item — what a job makes, or what a buy order is for.</summary>
    private void OnOpenRowItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenItem();

    /// <summary>A loop's stop or a trip ticked off as hauled — or a hauling service's trip as
    /// contracted, once its cargo has been corrected to what went into the contract.</summary>
    private void OnMarkHauled(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: WorklistRowVm row } || DataContext is not WorklistViewModel vm) return;
        if (vm.ContractedEditor(row) is { } editor) _ = ContractedAsync(vm, row, editor);
        else if (!row.IsCourierTick) _ = vm.MarkHauledAsync(row);
    }

    private async Task ContractedAsync(WorklistViewModel vm, WorklistRowVm row, ContractedCargoVm editor)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var cargo = await new ContractedDialog { DataContext = editor }
            .ShowDialog<IReadOnlyList<(int TypeId, long Quantity)>?>(owner);
        if (cargo is { Count: > 0 }) await vm.MarkContractedAsync(row, cargo);
    }

    /// <summary>The item a detail row names — on a manifest, at a stop, or waiting on this.</summary>
    private void OnOpenChildItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenChildItem();

    private void OnOpenNeedStation(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StationNeedRowVm)?.OpenStation();

    private void OnOpenNeedItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StationNeedRowVm)?.OpenItem();

    /// <summary>The item an "asked for by" row names.</summary>
    private void OnOpenNeedDriver(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StationNeedRowVm)?.OpenChild();

    private void OnOpenPrintProduct(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as PrintPressureRowVm)?.Open();

    private void OnOpenShortageItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ItemShortageRowVm)?.Open();

    /// <summary>Opens a need's "asked for by" rows under it. Same mechanism as the task rows
    /// above — see OnManifestToggle.</summary>
    private void OnNeedToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StationNeedRowVm { HasDrivers: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>Opens a contention row's suggestion and the tasks behind its counts, as rows under
    /// it. Same shape as the toggles above it — see OnManifestToggle.</summary>
    private void OnShortageToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ItemShortageRowVm { CanExpand: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>Opens the tasks behind a BPO / Formula row's counts.</summary>
    private void OnPrintToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PrintPressureRowVm { CanExpand: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>Opens the tasks behind a Hauling row's counts.</summary>
    private void OnHaulToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HaulPressureRowVm { CanExpand: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    private void OnOpenHaulItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HaulPressureRowVm vm }) vm.OpenItem();
    }

    private void OnOpenHaulStation(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HaulPressureRowVm vm }) vm.OpenStation();
    }

    /// <summary>
    /// Opens and closes what a task row opens into: its reason, what waits on it, its manifest or
    /// its stops.
    ///
    /// <para>⚠️ Flips the flag on the ITEM and touches nothing else; the view model puts the detail
    /// rows into the grid under it. Rows of the grid's one height, not a drawer and not a floating
    /// panel — see IFamilyRow, and tools/gridsim for the measurements.</para>
    ///
    /// <para>⚠️ The flag lives on the item rather than the row because the grid recycles rows: one
    /// keyed to the row would open against whatever item landed in it next.</para>
    /// </summary>
    private void OnManifestToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorklistRowVm vm }) vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>A summary finding that names an item.</summary>
    private void OnOpenPoint(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ObservationPointVm vm }) vm.Open();
    }

}
