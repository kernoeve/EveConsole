using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// The cargo a hauling-service trip is ticked off with, corrected before it is recorded. Closes
/// with the cargo to record, or null when cancelled. See <see cref="ContractedCargoVm"/>.
/// </summary>
public partial class ContractedDialog : Window
{
    public ContractedDialog() => InitializeComponent();

    private ContractedCargoVm? Vm => DataContext as ContractedCargoVm;

    private void OnOpenItem(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ContractedLineVm)?.OpenItem();

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ContractedLineVm row) Vm?.Remove(row);
    }

    private void OnRecord(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Cargo is { Count: > 0 } cargo) Close(cargo);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
