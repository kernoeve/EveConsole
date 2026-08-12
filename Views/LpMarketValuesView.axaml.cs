using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class LpMarketValuesView : ReactiveUserControl<LpMarketValuesViewModel>
{
    public LpMarketValuesView()
    {
        InitializeComponent();
    }

    private void OnRecalculateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is LpMarketValuesViewModel vm) _ = vm.RecalculateAsync();
    }

    /// <summary>Double-clicking a corporation opens its history rather than making the user
    /// find it again in the dropdown.</summary>
    private void OnCorpDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not LpMarketValuesViewModel vm) return;
        if (sender is not DataGrid { SelectedItem: LpCorpValueVm row }) return;
        vm.ShowHistoryFor(row.CorporationId);
    }
}
