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
        AppVersionItem.IsVisible = kind == PanelControlKind.AppVersion;
        SdeVersionItem.IsVisible = kind == PanelControlKind.SdeVersion;
        DatabaseItem.IsVisible = kind == PanelControlKind.Database;
        BackgroundOwnerItem.IsVisible = kind == PanelControlKind.BackgroundOwner;
        EsiStatusItem.IsVisible = kind == PanelControlKind.EsiStatus;
        ThemeItem.IsVisible = kind == PanelControlKind.Theme;
        UiScaleItem.IsVisible = kind == PanelControlKind.UiScale;
        AlarmsItem.IsVisible = kind == PanelControlKind.Alarms;
        SchedulerItem.IsVisible = kind == PanelControlKind.Scheduler;
        EveMailItem.IsVisible = kind == PanelControlKind.EveMail;
    }

    /// <summary>The kinds this view shows; the others are controls of their own.</summary>
    public static bool Shows(PanelControlKind kind) => kind is PanelControlKind.Settings or PanelControlKind.EveTime
        or PanelControlKind.LocalTime or PanelControlKind.ServerStatus or PanelControlKind.UsersOnline
        or PanelControlKind.AppVersion or PanelControlKind.SdeVersion or PanelControlKind.Database
        or PanelControlKind.BackgroundOwner or PanelControlKind.EsiStatus or PanelControlKind.Theme
        or PanelControlKind.UiScale or PanelControlKind.Alarms or PanelControlKind.Scheduler or PanelControlKind.EveMail;

    private MainWindowViewModel? Main => DataContext as MainWindowViewModel;

    /// <summary>Settings, opened on this panel's monitor, at a tab by its header (null for the first).</summary>
    private void OpenSettings(string? tab = null)
    {
        if (Main is not { } vm) return;
        var screens = TopLevel.GetTopLevel(this)?.Screens;
        var screen = screens?.ScreenFromVisual(this) ?? screens?.Primary;
        if (screen != null) vm.OpenSettingsOnMonitor?.Invoke(screen.WorkingArea, tab);
    }

    /// <summary>A menu from a window that never takes focus may not close when clicked away from.</summary>
    private void TakeFocus() => (TopLevel.GetTopLevel(this) as ClientPanelWindow)?.TakeFocus();

    private void OnSettingsClick(object? sender, RoutedEventArgs e) => OpenSettings();

    private void OnEveTimeClick(object? sender, PointerPressedEventArgs e)
    {
        if (Main is { } vm) MainWindow.OpenInBrowser(vm.EveTimeUrl);
    }

    private void OnServerStatusClick(object? sender, PointerPressedEventArgs e) =>
        MainWindow.OpenInBrowser(UiLinkSettings.ServerStatusUrl);

    private void OnReleaseLinkClick(object? sender, RoutedEventArgs e)
    {
        if (Main is { } vm) MainWindow.OpenInBrowser(vm.UpdateVm.ReleaseUrl);
    }

    // Importing the SDE is the app's job, so the link opens the Settings tab that does it.
    private void OnSdeUpdateLinkClick(object? sender, RoutedEventArgs e) => OpenSettings(EveConsole.Localization.SettingsText.TabSde);

    private void OnBackgroundLabelClick(object? sender, RoutedEventArgs e)
    {
        if (Main is { } vm && (sender as Control)?.DataContext is StatusBarItem item)
        {
            vm.OpenBackgroundProcesses(item.Tab);
            vm.ShowTool("background");
        }
    }

    // A bad token is put right by authorising it again, on the ESI Tokens page.
    private void OnStatusWarningClick(object? sender, RoutedEventArgs e) => OpenSettings(EveConsole.Localization.SettingsText.TabEsiTokens);

    private void OnThemeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        TakeFocus();
        ShellMenus.Theme(PlacementMode.BottomEdgeAlignedLeft, null).ShowAt(anchor);
    }

    private void OnUiScaleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;
        TakeFocus();
        ShellMenus.UiScale(PlacementMode.BottomEdgeAlignedLeft).ShowAt(anchor);
    }

    private void OnAlarmsClick(object? sender, RoutedEventArgs e) => Main?.ShowTool("alarms");

    // No confirmation, as on the main window: muting loses nothing, and it is reached for mid-fight.
    private void OnAlarmMuteClick(object? sender, RoutedEventArgs e)
    {
        if (Main is { } vm) vm.AlarmsMuted = !vm.AlarmsMuted;
    }

    private void OnSchedulerClick(object? sender, RoutedEventArgs e) => Main?.ShowTool("scheduler");

    private void OnEveMailClick(object? sender, RoutedEventArgs e)
    {
        if (Main is not { } vm) return;
        vm.OpenEveMail();
        vm.ShowTool("eve_mail");
    }
}
