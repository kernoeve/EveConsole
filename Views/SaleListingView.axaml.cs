using Avalonia.Controls;
using Avalonia.Interactivity;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class SaleListingView : UserControl
{
    public SaleListingView()
    {
        InitializeComponent();
    }

    private void OnOpenSalesTracker(object? sender, RoutedEventArgs e)
        => (DataContext as SaleListingViewModel)?.OpenSalesTracker?.Invoke();
}
