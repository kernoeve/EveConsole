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
                var dialog = new OrderEditDialog(vm.SearchTypesAsync, initial, vm.SearchBuyersAsync,
                                                 await vm.KnownLabelsAsync());
                return await dialog.ShowDialog<OrderDialogResult?>(owner);
            };
        };

        // Built when the menu opens, not once at startup: the label list changes as orders are
        // tagged, and a menu populated at construction would offer yesterday's labels.
        if (OrdersGrid.ContextMenu is { } menu)
            menu.Opening += async (_, _) => await BuildLabelMenusAsync();
    }

    /// <summary>The rows a right-click should act on — the whole selection, not just the row
    /// under the pointer.</summary>
    private List<TrackedOrderRowVm> SelectedRows() =>
        OrdersGrid.SelectedItems.OfType<TrackedOrderRowVm>().ToList();

    private async Task BuildLabelMenusAsync()
    {
        if (DataContext is not OrderTrackerViewModel vm) return;

        var rows = SelectedRows();
        AddLabelMenu.Items.Clear();
        RemoveLabelMenu.Items.Clear();

        AddLabelMenu.IsEnabled    = rows.Count > 0;
        RemoveLabelMenu.IsEnabled = rows.Count > 0;
        if (rows.Count == 0) return;

        AddLabelMenu.Header    = rows.Count > 1 ? $"Add label to {rows.Count} orders" : "Add label";
        RemoveLabelMenu.Header = rows.Count > 1 ? $"Remove label from {rows.Count} orders" : "Remove label";

        foreach (var label in await vm.KnownLabelsAsync())
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await vm.AddLabelToAsync(SelectedRows(), label);
            AddLabelMenu.Items.Add(item);
        }

        // ⚠️ Typing a new one is how labels are created; there is no manage-labels screen and
        // deliberately so. Without this entry the menu would only ever offer what already exists,
        // and the first label could never be made from here at all.
        if (AddLabelMenu.Items.Count > 0) AddLabelMenu.Items.Add(new Separator());
        var newItem = new MenuItem { Header = "New label…" };
        newItem.Click += async (_, _) => await PromptForLabelAsync(vm);
        AddLabelMenu.Items.Add(newItem);

        // Only what the selection actually carries — offering to remove a label nothing has is
        // an action with no effect dressed as a choice.
        foreach (var label in rows.SelectMany(r => r.LabelList)
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .OrderBy(l => l, StringComparer.OrdinalIgnoreCase))
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await vm.RemoveLabelFromAsync(SelectedRows(), label);
            RemoveLabelMenu.Items.Add(item);
        }

        RemoveLabelMenu.IsEnabled = RemoveLabelMenu.Items.Count > 0;
    }

    private async Task PromptForLabelAsync(OrderTrackerViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var rows  = SelectedRows();
        var typed = await new TextPromptDialog(
            "New label", "Label", "e.g. BNI First Capital Program").ShowDialog<string?>(owner);

        if (!string.IsNullOrWhiteSpace(typed)) await vm.AddLabelToAsync(rows, typed);
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

    private void OnOpenRowContractTo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ((sender as Control)?.DataContext as TrackedOrderRowVm)?.OpenContractTo();
}
