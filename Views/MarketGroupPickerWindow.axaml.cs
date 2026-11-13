using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class MarketGroupPickerWindow : Window
{
    /// <param name="title">The window's title, for a pick that adds no items (a filter); null
    /// keeps "add items from market group".</param>
    /// <param name="okText">The confirm button's words, the same way.</param>
    public MarketGroupPickerWindow(MarketGroupPickerViewModel vm, string? title = null, string? okText = null)
    {
        DataContext = vm;
        InitializeComponent();
        if (title  is not null) Title = title;
        if (okText is not null) OkButton.Content = okText;
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
