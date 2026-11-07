using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveConsole.Services;
using EveConsole.Services.ClientLayouts;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// One control on a panel. Its DataContext is the main window's view model, so a panel shows the same
/// values as the main screen and adds no polling or clocks of its own.
/// </summary>
public partial class ClientPanelItemView : UserControl
{
    public ClientPanelItemView() => InitializeComponent();

    public void ShowOnly(PanelControlKind kind)
    {
        SettingsItem.IsVisible = kind == PanelControlKind.Settings;
        EveTimeItem.IsVisible = kind == PanelControlKind.EveTime;
        LocalTimeItem.IsVisible = kind == PanelControlKind.LocalTime;
        ServerStatusItem.IsVisible = kind == PanelControlKind.ServerStatus;
        UsersOnlineItem.IsVisible = kind == PanelControlKind.UsersOnline;
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var screens = TopLevel.GetTopLevel(this)?.Screens;
        var screen = screens?.ScreenFromVisual(this) ?? screens?.Primary;
        if (screen != null) vm.OpenSettingsOnMonitor?.Invoke(screen.WorkingArea);
    }

    private void OnEveTimeClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) MainWindow.OpenInBrowser(vm.EveTimeUrl);
    }

    private void OnServerStatusClick(object? sender, PointerPressedEventArgs e) =>
        MainWindow.OpenInBrowser(UiLinkSettings.ServerStatusUrl);
}
