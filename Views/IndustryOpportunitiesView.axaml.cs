using Avalonia.Controls;
using Avalonia.Input;
using EveCortex.ViewModels;

namespace EveCortex.Views;

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
}
