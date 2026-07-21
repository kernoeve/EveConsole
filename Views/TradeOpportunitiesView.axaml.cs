using Avalonia.Controls;
using Avalonia.Input;
using EveConsole.ViewModels;

namespace EveConsole.Views;

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
        if (DataContext is TradeOpportunitiesViewModel vm &&
            sender is DataGrid grid &&
            grid.SelectedItem is TradeRow row)
        {
            vm.RequestItemNavigation(row.TypeId, row.TypeName);
        }
    }
}
