using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SalesTrackerView : ReactiveUserControl<SalesTrackerViewModel>
{
    public SalesTrackerView()
    {
        InitializeComponent();
    }

    private void OnMarkNotForProfit(object? sender, RoutedEventArgs e) => SetNotForProfit(true);
    private void OnRestoreToProfit(object? sender, RoutedEventArgs e)  => SetNotForProfit(false);

    private void SetNotForProfit(bool value)
    {
        if (ViewModel is not { } vm) return;

        // Copy the selection out first. Marking re-filters the grid, and with "show not for
        // profit" off the marked rows leave it — which clears SelectedItems underneath us.
        var rows = SalesGrid.SelectedItems.OfType<SaleRowVm>().ToList();
        if (rows.Count == 0) return;

        _ = vm.SetNotForProfitAsync(rows, value);
    }
}
