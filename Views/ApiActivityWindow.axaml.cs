using Avalonia.Controls;
using Avalonia.Threading;
using EveCortex.ViewModels;

namespace EveCortex.Views;

public partial class ApiActivityWindow : Window
{
    private DispatcherTimer? _historyTimer;
    private int _historyTick;

    public ApiActivityWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is not ApiActivityViewModel vm) return;

        vm.Entries.CollectionChanged += (_, _) => UpdateCount(vm.Entries.Count);
        UpdateCount(vm.Entries.Count);

        _ = vm.LoadTokenOptionsAsync();
        _ = vm.LoadMarketScheduleAsync();

        var schedTc = this.FindControl<TabControl>("ScheduleTabControl");
        if (schedTc is not null)
            schedTc.SelectionChanged += (_, _) =>
            {
                if (schedTc.SelectedIndex == 1)
                    _ = vm.LoadMarketScheduleAsync();
            };

        // Live price-history sweep monitor — recompute counts from the DB every ~10s and
        // copy the service's live counts every 2s. Runs on the UI thread only while open.
        _ = vm.RefreshHistorySweepAsync();
        _historyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _historyTimer.Tick += async (_, _) =>
        {
            try
            {
                if (++_historyTick % 5 == 0) await vm.RefreshHistorySweepAsync();
                else                          vm.SyncHistorySweep();
            }
            catch { /* best-effort monitor */ }
        };
        _historyTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _historyTimer?.Stop();
        _historyTimer = null;
        base.OnClosed(e);
    }

    private void UpdateCount(int count)
    {
        if (CountLabel is not null)
            CountLabel.Text = $"{count:N0} entr{(count == 1 ? "y" : "ies")} (max 10,000)";
    }
}
