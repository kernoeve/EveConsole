using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SalesTrackerView : ReactiveUserControl<SalesTrackerViewModel>
{
    public SalesTrackerView()
    {
        InitializeComponent();

        // Built when the menu opens, not once at startup: the label list changes as things are
        // tagged, and a menu populated at construction would offer yesterday's labels.
        if (SalesGrid.ContextMenu is { } menu)
            menu.Opening += async (_, _) => await BuildLabelMenusAsync();
    }

    // Click rather than Command. A sale row is a plain object built inside SalesQuery, not a
    // ReactiveObject, and giving several thousand of them four ICommands each would cost more
    // than reaching the row back through the button's own DataContext here.
    private void OnOpenOwner(object? sender, RoutedEventArgs e)    => Row(sender)?.OpenOwner();
    private void OnOpenLocation(object? sender, RoutedEventArgs e) => Row(sender)?.OpenLocation();
    private void OnOpenBuyer(object? sender, RoutedEventArgs e)    => Row(sender)?.OpenBuyer();
    private void OnOpenItem(object? sender, RoutedEventArgs e)     => Row(sender)?.OpenItem();

    /// <summary>
    /// Opens the contract behind a sale, the way the Order Tracker does.
    ///
    /// <para>Double-click rather than a link in a cell: the whole row is the sale, and there is
    /// no one column that means "the contract". Market rows do nothing — a wallet transaction has
    /// no contract to open.</para>
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (SalesGrid.SelectedItem is SaleRowVm row) row.OpenContract();
    }

    private void OnOpenContract(object? sender, RoutedEventArgs e) => Row(sender)?.OpenContract();

    /// <summary>The rows a right-click should act on — the whole selection.</summary>
    private List<SaleRowVm> SelectedRows() => SalesGrid.SelectedItems.OfType<SaleRowVm>().ToList();

    private async Task BuildLabelMenusAsync()
    {
        if (DataContext is not SalesTrackerViewModel vm) return;

        var rows = SelectedRows();
        AddLabelMenu.Items.Clear();
        RemoveLabelMenu.Items.Clear();

        AddLabelMenu.IsEnabled    = rows.Count > 0;
        RemoveLabelMenu.IsEnabled = rows.Count > 0;
        if (rows.Count == 0) return;

        AddLabelMenu.Header    = rows.Count > 1 ? $"Add label to {rows.Count} sales" : "Add label";
        RemoveLabelMenu.Header = rows.Count > 1 ? $"Remove label from {rows.Count} sales" : "Remove label";

        foreach (var label in await vm.KnownLabelsAsync())
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await vm.AddLabelToAsync(SelectedRows(), label);
            AddLabelMenu.Items.Add(item);
        }

        // Typing a new one is how labels are created — the same list the Order Tracker offers,
        // so a tag made here is a tag orders can use and the other way round.
        if (AddLabelMenu.Items.Count > 0) AddLabelMenu.Items.Add(new Separator());
        var newItem = new MenuItem { Header = "New label…" };
        newItem.Click += async (_, _) => await PromptForLabelAsync(vm);
        AddLabelMenu.Items.Add(newItem);

        // Only what the selection actually carries.
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

    private async Task PromptForLabelAsync(SalesTrackerViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var rows  = SelectedRows();
        var typed = await new TextPromptDialog(
            "New label", "Label", "e.g. BNI First Capital Program").ShowDialog<string?>(owner);

        if (!string.IsNullOrWhiteSpace(typed)) await vm.AddLabelToAsync(rows, typed);
    }

    private static SaleRowVm? Row(object? sender) => (sender as Control)?.DataContext as SaleRowVm;

    // The rollups up top carry their destination as an Action rather than ids, because a group
    // row is a name and a total — it borrows the link from the first sale that made it.
    private void OnOpenGroup(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as GroupRowVm)?.Open?.Invoke();

    private void OnOpenProfitGroup(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as ProfitGroupRowVm)?.Open?.Invoke();

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
