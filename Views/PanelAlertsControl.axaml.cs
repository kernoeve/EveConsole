using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The main Overview's alerts on a panel. See <see cref="OverviewViewModel.WantAlerts"/>.</summary>
public partial class PanelAlertsControl : UserControl
{
    private readonly MainWindowViewModel? _main;
    private IDisposable? _wanted;

    public PanelAlertsControl() => InitializeComponent();

    public PanelAlertsControl(MainWindowViewModel main) : this()
    {
        _main = main;
        DataContext = main.OverviewVm;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _wanted ??= _main?.OverviewVm.WantAlerts();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _wanted?.Dispose();
        _wanted = null;
    }

    /// <summary>Goes where the alert is about, and brings that tool's window forward: the panel is often in front of it.</summary>
    private void OnNavigate(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AlertRowVm { NavigateCommand: { } go }) return;
        go.Execute().Subscribe();
        if (_main is { } main) Dispatcher.UIThread.Post(main.BringForwardLastOpened, DispatcherPriority.Background);
    }

    /// <summary>Settings on this panel's monitor, at its Alerts tab.</summary>
    private void OnOpenAlertSettings(object? sender, RoutedEventArgs e)
    {
        var screens = TopLevel.GetTopLevel(this)?.Screens;
        var screen = screens?.ScreenFromVisual(this) ?? screens?.Primary;
        if (screen != null) _main?.OpenSettingsOnMonitor?.Invoke(screen.WorkingArea, EveConsole.Localization.SettingsText.TabAlerts);
    }
}
