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
}
