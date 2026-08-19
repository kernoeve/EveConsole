using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ContractsView : ReactiveUserControl<ContractsViewModel>
{
    public ContractsView()
    {
        InitializeComponent();
    }

    // Both grids render the same row type, so one set of handlers serves the public and
    // personal tabs alike — the button's DataContext is the row it sits in.
    private static ContractRowVm? Row(object? sender)
        => (sender as Control)?.DataContext as ContractRowVm;

    private void OnOpenContents(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenContents();
    private void OnOpenIssuer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenIssuer();
    private void OnOpenAssignee(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenAssignee();
}
