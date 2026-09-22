using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The Background Processes tool: a tab per background process, each saying what that process is
/// doing, on this client or on the one holding the worker lease. Hosted as a tab of the main
/// window from Data / Logs, and by <see cref="ApiActivityWindow"/> for the tray, which may have no
/// main window at all.
///
/// <para>The main window builds a fresh view each time the tab is shown while the view model keeps
/// its state, so everything the view starts — the timers, the tab selection — is started on
/// attach and stopped on detach, and nothing is left running for a view that is gone.</para>
/// </summary>
public partial class BackgroundProcessesView : UserControl
{
    private ApiActivityViewModel? _vm;
    private DispatcherTimer?      _timer;
    private int                   _tick;

    public BackgroundProcessesView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not ApiActivityViewModel vm) return;
        _vm = vm;

        _ = vm.LoadTokenOptionsAsync();
        _ = vm.LoadMarketScheduleAsync();

        ScheduleTabControl.SelectionChanged += OnScheduleTabChanged;

        // The in-memory mirrors refresh on every tick; anything that touches the DB or ESI stays
        // on the slower ten-second cadence.
        _ = vm.RefreshHistorySweepAsync();
        _ = vm.RefreshContractsAsync();
        _ = vm.RefreshLpStoreAsync();
        _ = vm.RefreshNameCacheAsync();
        vm.SyncStatusBar();
        vm.SyncBackgroundProcesses();
        _tick  = 0;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += OnTick;
        _timer.Start();

        // The tab a status-bar label asked for, whether it asked before this view existed or
        // asks while it is showing.
        vm.PropertyChanged += OnVmPropertyChanged;
        SelectRequestedTab();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
        ScheduleTabControl.SelectionChanged -= OnScheduleTabChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnScheduleTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ScheduleTabControl.SelectedIndex == 1 && _vm is not null) _ = _vm.LoadMarketScheduleAsync();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_vm is not { } vm) return;
        try
        {
            vm.SyncStatusBar();
            vm.SyncBackgroundProcesses();
            if (++_tick % 5 == 0)
            {
                await vm.RefreshHistorySweepAsync();
                await vm.RefreshContractsAsync();
                await vm.RefreshLpStoreAsync();
                await vm.RefreshNameCacheAsync();
            }
            else vm.SyncHistorySweep();
        }
        catch { /* best-effort monitor */ }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ApiActivityViewModel.RequestedTab)) SelectRequestedTab();
    }

    /// <summary>Selects the tab the view model was asked for, by its header, and clears the ask so
    /// the tool otherwise opens where it was left.</summary>
    private void SelectRequestedTab()
    {
        if (_vm?.RequestedTab is not { Length: > 0 } wanted) return;
        var tab = ProcessTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == wanted);
        if (tab is not null) ProcessTabs.SelectedItem = tab;
        _vm.RequestedTab = null;
    }
}
