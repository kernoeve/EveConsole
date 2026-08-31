using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class StoresView : UserControl
{
    public StoresView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not StoresViewModel vm) return;
            vm.ConfirmDelete = async message =>
                TopLevel.GetTopLevel(this) is Window owner
                && await new ConfirmDialog(message).ShowDialog<bool>(owner);
        };
    }

    // The row's own DataContext, not the grid selection: pressing Remove on one row while
    // another is selected would otherwise remove the wrong one without saying so.
    private async void OnRemoveSender(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not StoreSenderRowVm row) return;
        if (DataContext is not StoresViewModel vm) return;
        await vm.RemoveSenderAsync(row);
    }

    private void OnResetUsage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is StoresViewModel vm) vm.ResetUsage();
    }
}
