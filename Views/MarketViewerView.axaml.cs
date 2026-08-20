using Avalonia.Controls;
using Avalonia.ReactiveUI;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class MarketViewerView : ReactiveUserControl<MarketViewerViewModel>
{
    public MarketViewerView()
    {
        InitializeComponent();
    }

    // ── Pie slices ────────────────────────────────────────────────────────────
    //
    // The clicked point's series carries the slice label, which is what the view model maps back
    // to an item. "Other" is not in that map and opens nothing — it stands for everything past
    // the tenth slice, not for one item.
    private void OnSellSliceClicked(IChartView chart, ChartPoint? point)
        => ViewModel?.OpenSellSlice(point?.Context.Series.Name);

    private void OnBuySliceClicked(IChartView chart, ChartPoint? point)
        => ViewModel?.OpenBuySlice(point?.Context.Series.Name);

    /// <summary>The type name on the three by-type grids. Two row classes, one handler — they
    /// carry the same link but share no base class.</summary>
    private void OnOpenRowType(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case MarketTypeSummaryVm r: r.OpenType(); break;
            case MarketOrderByTypeVm r: r.OpenType(); break;
        }
    }
}
