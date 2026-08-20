using Avalonia.Controls;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class ContractDetailView : UserControl
{
    public ContractDetailView()
    {
        InitializeComponent();
    }

    // The party links read the pane's own view model; the item link reads its row.
    private ContractDetailVm? Vm => DataContext as ContractDetailVm;

    private void OnOpenIssuer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.OpenIssuer();
    private void OnOpenAssignee(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.OpenAssignee();
    private void OnOpenAcceptor(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Vm?.OpenAcceptor();

    private void OnOpenItem(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ContractItemRowVm)?.OpenItem();
}
