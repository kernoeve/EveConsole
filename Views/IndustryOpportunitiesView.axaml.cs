using Avalonia.Controls;
using Avalonia.Input;
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
}
