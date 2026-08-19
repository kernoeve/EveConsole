using System;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using Avalonia.VisualTree;
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

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not OrderTrackerViewModel vm || vm.Selected is null) return;

        // ⚠️ The event bubbles from wherever it landed. A column header — where a double-tap
        // auto-sizes the column — and the empty space under the last row both arrive here, and
        // neither should open an editor on whichever row happens to be selected.
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(true) is null) return;

        // A double-tap on one of the in-cell links has already navigated away; opening the dialog
        // on top of that is not what the second click asked for.
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(true) is not null) return;

        vm.EditCommand.Execute().Subscribe();
    }

    // Each row carries its own navigation; these reach it through the button's DataContext.
    private void OnOpenContract(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TrackedOrderRowVm)?.OpenContract();

    private void OnOpenRowType(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TrackedOrderRowVm)?.OpenType();

    private void OnOpenRowBuyer(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TrackedOrderRowVm)?.OpenBuyer();
}
