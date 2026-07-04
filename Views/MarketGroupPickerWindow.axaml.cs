using Avalonia.Controls;
using Avalonia.Interactivity;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class MarketGroupPickerWindow : Window
{
    public MarketGroupPickerWindow(MarketGroupPickerViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        _ = vm.LoadAsync();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        var vm = (MarketGroupPickerViewModel)DataContext!;
        if (vm.SelectedNode is null) return;
        Close(new MarketGroupPickerResult(
            vm.SelectedNode.MarketGroupId,
            vm.SelectedNode.Name,
            vm.TargetQty));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
}
