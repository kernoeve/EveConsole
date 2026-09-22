using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ContractsView : ReactiveUserControl<ContractsViewModel>
{
    public ContractsView()
    {
        InitializeComponent();

        // An alert that opens the tool asks for the personal grid sorted by a column; the
        // columns are the view's, so the sort is done here on the view model's behalf.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ContractsViewModel vm)
                vm.Owned.SortBy = path =>
                {
                    var column = OwnedGrid.Columns.FirstOrDefault(c => c.SortMemberPath == path);
                    column?.Sort(System.ComponentModel.ListSortDirection.Ascending);
                };
        };
    }

    // Both grids render the same row type, so one set of handlers serves the public and
    // personal tabs alike — the button's DataContext is the row it sits in.
    private static ContractRowVm? Row(object? sender)
        => (sender as Control)?.DataContext as ContractRowVm;

    private void OnOpenContents(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenContents();
    private void OnOpenIssuer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenIssuer();
    private void OnOpenFrom(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenFrom();
    private void OnOpenAssignee(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Row(sender)?.OpenAssignee();
}
