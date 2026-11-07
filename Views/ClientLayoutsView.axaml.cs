using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ClientLayoutsView : UserControl
{
    public ClientLayoutsView() => InitializeComponent();

    private ClientLayoutsViewModel? Vm => DataContext as ClientLayoutsViewModel;

    private void DeviceName_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveDeviceName();

    private void LayoutName_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveLayoutName();

    private void Selection_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveSelection();

    private void OtherDeviceName_LostFocus(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.DataContext as OtherDeviceVm)?.SaveName();
}
