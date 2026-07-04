using Avalonia.Controls;
using Avalonia.Input;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class TradeOpportunitiesView : UserControl
{
    private bool _initialized;

    public TradeOpportunitiesView()
    {
        InitializeComponent();
        DataContextChanged += async (_, _) =>
        {
            if (!_initialized && DataContext is TradeOpportunitiesViewModel vm)
            {
                _initialized = true;
                await vm.InitializeAsync();
            }
        };
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is TradeOpportunitiesViewModel vm &&
            sender is DataGrid grid &&
            grid.SelectedItem is TradeRow row)
        {
            vm.RequestItemNavigation(row.TypeId, row.TypeName);
        }
    }
}
