using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SaleListingView : UserControl
{
    public SaleListingView()
    {
        InitializeComponent();
    }

    private void OnOpenSalesTracker(object? sender, RoutedEventArgs e)
        => (DataContext as SaleListingViewModel)?.OpenSalesTracker?.Invoke();

    // Click rather than Command: a listing row is a plain object, and the row is reachable
    // through the button's own DataContext without giving every row its own commands.
    private void OnOpenBuyer(object? sender, RoutedEventArgs e) => Row(sender)?.OpenBuyer();
    private void OnOpenItem(object? sender, RoutedEventArgs e)  => Row(sender)?.OpenItem();

    private static SaleListingRowVm? Row(object? sender)
        => (sender as Control)?.DataContext as SaleListingRowVm;
}
