using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
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

    /// <summary>
    /// Opens and closes the manifest under a haul row.
    ///
    /// <para>Done here rather than by binding <see cref="DataGridRow.AreDetailsVisible"/> in a
    /// style, because the DataGrid writes that property itself as it loads each row, and a local
    /// write outranks a style setter — the binding would be overwritten on scroll. Setting it on
    /// the row goes through the grid's own bookkeeping, which is keyed by item index and so
    /// survives the row being recycled to a different position.</para>
    ///
    /// <para>The grid-wide mode stays Collapsed for the same reason the earlier attempt failed
    /// visibly: <c>Visible</c> attaches a details presenter to every row, which skews the row
    /// height estimate and paints blank bands into the middle of the list while scrolling.</para>
    /// </summary>
    private void OnOpenLocation(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenLocation();

    private void OnOpenCharacter(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as WorklistRowVm)?.OpenCharacter();

    /// <summary>A manifest line, which is a WorklistLine rather than a row.</summary>
    private void OnOpenLineItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as EveConsole.Services.Worklist.WorklistLine)?.OpenItem();

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

    /// <summary>Opens and closes the "asked for by" panel under a need. Same mechanism as the
    /// haul manifest above, and for the same reasons — see OnManifestToggle.</summary>
    private void OnNeedToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;
        if (row.DataContext is not StationNeedRowVm vm || !vm.HasDrivers) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;
        vm.IsExpanded = row.AreDetailsVisible;
    }

    /// <summary>Opens the tasks behind a contention row's counts. Same shape as the two toggles
    /// above it — the glyph lives on the item so it survives row recycling.</summary>
    private void OnShortageToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;
        if (row.DataContext is not ItemShortageRowVm vm || !vm.HasTasks) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;
        vm.IsExpanded = row.AreDetailsVisible;
    }

    /// <summary>Opens the tasks behind a BPO / Formula row's counts.</summary>
    private void OnPrintToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;
        if (row.DataContext is not PrintPressureRowVm vm || !vm.HasTasks) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;
        vm.IsExpanded = row.AreDetailsVisible;
    }

    /// <summary>Opens the tasks behind a Hauling row's counts.</summary>
    private void OnHaulToggle(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;
        if (row.DataContext is not HaulPressureRowVm vm || !vm.HasTasks) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;
        vm.IsExpanded = row.AreDetailsVisible;
    }

    private void OnOpenHaulItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HaulPressureRowVm vm }) vm.OpenItem();
    }

    private void OnOpenHaulStation(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: HaulPressureRowVm vm }) vm.OpenStation();
    }

    private void OnManifestToggle(object? sender, RoutedEventArgs e)



    {
        if (sender is not Control control) return;
        if (control.FindAncestorOfType<DataGridRow>() is not { } row) return;

        row.AreDetailsVisible = !row.AreDetailsVisible;

        // The glyph lives on the item so it stays correct when the row is recycled.
        if (row.DataContext is WorklistRowVm vm) vm.IsExpanded = row.AreDetailsVisible;
    }
}
