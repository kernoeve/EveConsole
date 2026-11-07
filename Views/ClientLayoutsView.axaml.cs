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

    // Both only while someone is typing here: these also fire when the values are loaded.
    private void DeviceName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true }) Vm?.TypedDeviceName();
    }

    private void ProfileName_LostFocus(object? sender, RoutedEventArgs e) => Vm?.SaveProfileName();

    private void ProfileName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true }) Vm?.TypedProfileName();
    }

    private void LayoutName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { IsFocused: true }) Vm?.TypedLayoutName();
    }

    private void OtherDeviceName_LostFocus(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.DataContext as OtherDeviceVm)?.SaveName();
}
