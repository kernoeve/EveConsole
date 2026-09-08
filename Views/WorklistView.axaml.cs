using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.ViewModels;

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

    /// <summary>
    /// Applies a tab the Overview asked for, once this view's own TabControl binding is live.
    /// See WorklistViewModel.RequestedTab for why it cannot simply be set from the caller.
    /// </summary>
    protected override void OnLoaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is WorklistViewModel vm && vm.TakeRequestedTab() is { } tab)
            vm.OuterTabIndex = tab;

        Dispatcher.UIThread.Post(ReopenPanels, DispatcherPriority.Background);
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

    /// <summary>A manifest line, which is a WorklistLine rather than a row.</summary>
    private void OnOpenLineItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistLineVm)?.OpenItem();

    /// <summary>A job waiting on this cargo. Opens the item it would produce.</summary>
    private void OnOpenWaitingJob(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistWaitingJobVm)?.OpenItem();

    private void OnOpenNeedStation(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StationNeedRowVm)?.OpenStation();

    private void OnOpenNeedItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as StationNeedRowVm)?.OpenItem();

    private void OnOpenNeedDriver(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as NeedDriverRowVm)?.Open();

    private void OnOpenPrintProduct(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as PrintPressureRowVm)?.Open();

    private void OnOpenShortageItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ItemShortageRowVm)?.Open();

    /// <summary>Opens the "asked for by" panel over a need. Same mechanism as the
    /// haul manifest above, and for the same reasons — see OnManifestToggle.</summary>
    private void OnNeedToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: StationNeedRowVm { HasDrivers: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>Opens the tasks behind a contention row's counts. Same shape as the toggles above it.</summary>
    /// <summary>
    /// Opens the tasks behind a contention row's counts.
    ///
    /// <para>⚠️ Flips the flag on the ITEM and touches nothing else. What opens is a Popup bound to
    /// that flag, not RowDetails — see the note beside the Popup in the XAML for why the drawer had
    /// to go, and tools/gridsim for the measurements.</para>
    /// </summary>
    private void OnShortageToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ItemShortageRowVm { HasTasks: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>Opens the tasks behind a BPO / Formula row's counts.</summary>
    private void OnPrintToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PrintPressureRowVm { HasTasks: true } vm })
            vm.IsExpanded = !vm.IsExpanded;
    }

    /// <summary>Opens the tasks behind a Hauling row's counts.</summary>
    private void OnHaulToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HaulPressureRowVm { HasTasks: true } vm })
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
    /// Opens and closes the manifest under a haul row.
    ///
    /// <para>⚠️ Flips the flag on the ITEM and touches nothing else. What opens is a Popup bound to
    /// that flag, not RowDetails — a drawer inside the row is what made rows wildly different
    /// heights, and that is the whole reason scrolling up used to stick and jump. The note beside
    /// the Popup in the XAML carries the mechanism; tools/gridsim carries the measurements.</para>
    ///
    /// <para>⚠️ The flag lives on the item rather than the row because the grid recycles rows: one
    /// keyed to the row would open against whatever item landed in it next.</para>
    /// </summary>
    private void OnManifestToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorklistRowVm vm }) vm.IsExpanded = !vm.IsExpanded;
    }
    /// <summary>
    /// A detail panel closed. Clears the flag only if this tool is still on screen.
    ///
    /// <para>⚠️ Not a TwoWay binding on IsOpen, which is what it looks like it ought to be. Clicking
    /// an item link switches to the Item Browser tab, and that detaches this view and closes every
    /// popup with it — a TwoWay binding writes that back, so returning to the Worklist found the
    /// panel shut. A popup whose row is scrolled out of view closes the same way.</para>
    ///
    /// <para>Light dismiss inside the tool still closes it, because the view is attached when that
    /// happens. That is the whole difference between the two cases.</para>
    /// </summary>
    private void OnDetailClosed(object? sender, EventArgs e)
    {
        if (sender is Popup { DataContext: IExpandableRow row } popup && popup.GetVisualRoot() is not null)
            row.IsExpanded = false;
    }

    /// <summary>
    /// Reopens what was open, once this view is attached again.
    ///
    /// <para>⚠️ Needed because the binding is one-way and its source never changed, so nothing
    /// tells the popup to come back on its own. Posted rather than run inline, so the grid has
    /// realised its rows and there are popups to open.</para>
    /// </summary>
    private void ReopenPanels()
    {
        foreach (var popup in this.GetVisualDescendants().OfType<Popup>())
            if (popup.DataContext is IExpandableRow { IsExpanded: true }) popup.IsOpen = true;
    }

    /// <summary>A summary finding that names an item.</summary>
    private void OnOpenPoint(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ObservationPointVm vm }) vm.Open();
    }

}
