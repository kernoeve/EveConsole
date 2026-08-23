using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SalesTrackerView : ReactiveUserControl<SalesTrackerViewModel>
{
    public SalesTrackerView()
    {
        InitializeComponent();
    }

    // Click rather than Command. A sale row is a plain object built inside SalesQuery, not a
    // ReactiveObject, and giving several thousand of them four ICommands each would cost more
    // than reaching the row back through the button's own DataContext here.
    private void OnOpenOwner(object? sender, RoutedEventArgs e)    => Row(sender)?.OpenOwner();
    private void OnOpenLocation(object? sender, RoutedEventArgs e) => Row(sender)?.OpenLocation();
    private void OnOpenBuyer(object? sender, RoutedEventArgs e)    => Row(sender)?.OpenBuyer();
    private void OnOpenItem(object? sender, RoutedEventArgs e)     => Row(sender)?.OpenItem();

    /// <summary>
    /// Opens the contract behind a sale, the way the Order Tracker does.
    ///
    /// <para>Double-click rather than a link in a cell: the whole row is the sale, and there is
    /// no one column that means "the contract". Market rows do nothing — a wallet transaction has
    /// no contract to open.</para>
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SalesGrid.SelectedItem is SaleRowVm row) row.OpenContract();
    }

    private static SaleRowVm? Row(object? sender) => (sender as Control)?.DataContext as SaleRowVm;

    // The rollups up top carry their destination as an Action rather than ids, because a group
    // row is a name and a total — it borrows the link from the first sale that made it.
    private void OnOpenGroup(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as GroupRowVm)?.Open?.Invoke();

    private void OnOpenProfitGroup(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ProfitGroupRowVm)?.Open?.Invoke();

    private void OnMarkNotForProfit(object? sender, RoutedEventArgs e) => SetNotForProfit(true);
    private void OnRestoreToProfit(object? sender, RoutedEventArgs e)  => SetNotForProfit(false);

    private void SetNotForProfit(bool value)
    {
        if (ViewModel is not { } vm) return;

        // Copy the selection out first. Marking re-filters the grid, and with "show not for
        // profit" off the marked rows leave it — which clears SelectedItems underneath us.
        var rows = SalesGrid.SelectedItems.OfType<SaleRowVm>().ToList();
        if (rows.Count == 0) return;

        _ = vm.SetNotForProfitAsync(rows, value);
    }
}
