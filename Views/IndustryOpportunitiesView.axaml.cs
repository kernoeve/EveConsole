using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class IndustryOpportunitiesView : UserControl
{
    private bool _initialized;

    public IndustryOpportunitiesView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (!_initialized && DataContext is IndustryOpportunitiesViewModel vm)
            {
                _initialized = true;

                vm.ShowMarketGroupPickerDialog = async () =>
                {
                    var pickerVm = new MarketGroupPickerViewModel(vm.GetBatchAddService());
                    var win      = new MarketGroupPickerWindow(pickerVm);
                    return await win.ShowDialog<MarketGroupPickerResult?>(GetWindow());
                };

                await vm.InitializeAsync();
            }
        };
    }

    private Window GetWindow() => (TopLevel.GetTopLevel(this) as Window)!;

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is IndustryOpportunitiesViewModel vm &&
            sender is DataGrid grid &&
            grid.SelectedItem is IndustryRow row)
        {
            vm.RequestItemNavigation(row.TypeId, row.TypeName);
        }
    }

    /// <summary>
    /// Single click on the item name.
    ///
    /// <para>Goes through <c>vm.RequestItemNavigation</c>, the same path the double-click above
    /// uses, rather than the shared EntityNavigator. This tool also runs in a window of its own,
    /// and that method is what knows where to open the Item Browser from either host.</para>
    /// </summary>
    private void OnOpenRowItem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is IndustryOpportunitiesViewModel vm &&
            (sender as Control)?.DataContext is IndustryRow row)
            vm.RequestItemNavigation(row.TypeId, row.TypeName);
    }

    // ── Right-click: to the Production Calculator's queue ───────────────────

    /// <summary>The grid the menu was opened on: the Detail grid or one of the Summary's ten.</summary>
    private DataGrid? _menuGrid;

    /// <summary>A right-click on a row that is not among those highlighted acts on that row; on
    /// one that is, on all of them.</summary>
    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        _menuGrid = grid;
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is { DataContext: IndustryRow row }
            && !grid.SelectedItems.Contains(row))
            grid.SelectedItem = row;
    }

    /// <summary>Every highlighted row, at the station of the last standing buy order.</summary>
    private void OnAddBuyOrder(object? sender, RoutedEventArgs e)
    {
        if (_menuGrid is null || DataContext is not IndustryOpportunitiesViewModel { AddToStandingBuyOrders: { } add }) return;
        var typeIds = _menuGrid.SelectedItems.OfType<IndustryRow>().Select(r => r.TypeId).Distinct().ToList();
        if (typeIds.Count > 0) add(typeIds);
    }

    private async void OnNewQueue(object? sender, RoutedEventArgs e) => await QueueAsync(replace: true);
    private async void OnAddQueue(object? sender, RoutedEventArgs e) => await QueueAsync(replace: false);

    /// <summary>New empties the queue, puts the rows in and goes to the calculator; Add puts them
    /// in after what is there, opens the calculator if it was not, stays here and says so.</summary>
    private async Task QueueAsync(bool replace)
    {
        if (_menuGrid is null || DataContext is not IndustryOpportunitiesViewModel { QueueForProduction: { } queue }) return;
        var rows = _menuGrid.SelectedItems.OfType<IndustryRow>().ToList();
        if (rows.Count == 0) return;
        var added = await queue(rows, replace);
        if (!replace)
            ShowToast(EveConsole.Localization.Plurals.Format(IndustryText.ResourceManager, nameof(IndustryText.OppsToastQueuedOther), added));
    }

    private int _toastShown;

    /// <summary>The message in the middle for a moment, then faded; a second while one shows starts
    /// the moment again.</summary>
    private async void ShowToast(string message)
    {
        var mine = ++_toastShown;
        QueueToastText.Text = message;
        QueueToast.Opacity  = 1;
        await Task.Delay(TimeSpan.FromSeconds(1.6));
        if (mine == _toastShown) QueueToast.Opacity = 0;
    }
}
