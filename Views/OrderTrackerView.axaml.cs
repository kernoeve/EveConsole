using Avalonia.Controls;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class OrderTrackerView : ReactiveUserControl<OrderTrackerViewModel>
{
    public OrderTrackerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not OrderTrackerViewModel vm) return;
            vm.ShowOrderDialog = async initial =>
            {
                if (TopLevel.GetTopLevel(this) is not Window owner) return null;
                var dialog = new OrderEditDialog(vm.SearchTypesAsync, initial, vm.SearchBuyersAsync);
                return await dialog.ShowDialog<OrderDialogResult?>(owner);
            };
        };
    }

    // Each row carries its own navigation; these reach it through the button's DataContext.
    private void OnOpenRowType(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TrackedOrderRowVm)?.OpenType();

    private void OnOpenRowBuyer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TrackedOrderRowVm)?.OpenBuyer();
}
