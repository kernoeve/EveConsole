using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using EveConsole.Controls;
using EveConsole.ViewModels;
using EveConsole.Localization;

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

        // By period: the headings over each group of columns, and the units table's columns,
        // which follow the periods shown.
        PeriodBand.Target = PeriodGrid;
        PeriodBand.SetGroups(
        [
            new ColumnGroup(1, 3, SalesText.GroupAllSales,    "ColumnGroupABrush"),
            new ColumnGroup(4, 5, SalesText.GroupForProfit,   "ColumnGroupBBrush"),
            new ColumnGroup(9, 2, SalesText.GroupNotForProfit,"ColumnGroupCBrush"),
        ]);
        DataContextChanged += (_, _) =>
        {
            if (_watched is not null) _watched.PropertyChanged -= OnVmPropertyChanged;
            _watched = DataContext as SalesTrackerViewModel;
            if (_watched is not null) _watched.PropertyChanged += OnVmPropertyChanged;
            RebuildUnitColumns();
        };
    }

    private SalesTrackerViewModel? _watched;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SalesTrackerViewModel.UnitPeriods)) RebuildUnitColumns();
    }

    /// <summary>
    /// The units table's columns: what is counted, a column per period, and the total. Built here
    /// because a DataGrid's columns are not bindable; each cell binds to its row's cell by index.
    /// </summary>
    private void RebuildUnitColumns()
    {
        UnitGrid.Columns.Clear();
        if (_watched is not { } vm) return;

        UnitGrid.Columns.Add(new DataGridTextColumn
        {
            Header  = vm.SelectedCountBy.Label,
            Binding = new Binding(nameof(UnitRowVm.Key)),
            Width   = new DataGridLength(200),
        });
        for (var i = 0; i < vm.UnitPeriods.Count; i++)
        {
            var index = i;
            var column = new DataGridTextColumn
            {
                Header  = vm.UnitPeriods[i],
                Binding = new Binding($"{nameof(UnitRowVm.Cells)}[{index}]"),
                Width   = DataGridLength.Auto,   // the header or the widest cell, whichever is wider
                CustomSortComparer = new UnitSort(r => r.Line.Fp[index] + r.Line.Nfp[index]),
            };
            column.CellStyleClasses.Add("num");
            UnitGrid.Columns.Add(column);
        }
        var total = new DataGridTextColumn
        {
            Header  = SalesText.Total,
            Binding = new Binding(nameof(UnitRowVm.Total)),
            Width   = new DataGridLength(110),
            CustomSortComparer = new UnitSort(r => r.Line.Total),
        };
        total.CellStyleClasses.Add("num");
        total.CellStyleClasses.Add("ga");
        UnitGrid.Columns.Add(total);
    }

    /// <summary>Sorts the units table on the number a cell stands for, not its text.</summary>
    private sealed class UnitSort(Func<UnitRowVm, long> key) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y) =>
            (x, y) is (UnitRowVm a, UnitRowVm b) ? key(a).CompareTo(key(b)) : 0;
    }

    private void OnPeriodLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        var row = e.Row.DataContext as PeriodRowVm;
        e.Row.Classes.Set("period", row?.IsPeriod == true);
        e.Row.Classes.Set("split",  row?.IsSplit  == true);
        e.Row.Classes.Set("total",  row?.IsTotal  == true);
    }

    private async void OnAddItemGroup(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not Window owner) return;
        var picked = await new MarketGroupPickerWindow(vm.NewGroupPicker(), SalesText.PickItemGroupTitle, SalesText.PickItemGroupOk)
            .ShowDialog<MarketGroupPickerResult?>(owner);
        if (picked is not null) vm.AddItemGroup(picked.MarketGroupId, picked.GroupName);
    }

    private void OnRemoveItemGroup(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && (sender as Control)?.DataContext is ItemGroupFilter g) vm.RemoveItemGroup(g);
    }

    private async void OnCopyPeriods(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        // In a code block, so the columns line up wherever it is pasted.
        await clipboard.SetTextAsync("```\n" + vm.PeriodsText() + "\n```");
        vm.NoteCopied();
    }

    private async void OnExportPeriods(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = SalesText.ExportSummaryTitle,
            SuggestedFileName = $"sales-summary-{DateTime.UtcNow:yyyy-MM-dd}.csv",
            FileTypeChoices   = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        });
        if (file is null) return;

        await using (var stream = await file.OpenWriteAsync())
        await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(true)))
            await writer.WriteAsync(vm.PeriodsCsv());
        vm.NoteExported(file.Name);
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

        AddLabelMenu.Header    = rows.Count > 1 ? string.Format(SalesText.AddLabelToSales, rows.Count) : SalesText.AddLabel;
        RemoveLabelMenu.Header = rows.Count > 1 ? string.Format(SalesText.RemoveLabelFromSales, rows.Count) : SalesText.RemoveLabel;

        foreach (var label in await vm.KnownLabelsAsync())
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await vm.AddLabelToAsync(SelectedRows(), label);
            AddLabelMenu.Items.Add(item);
        }

        // Typing a new one is how labels are created — the same list the Order Tracker offers,
        // so a tag made here is a tag orders can use and the other way round.
        if (AddLabelMenu.Items.Count > 0) AddLabelMenu.Items.Add(new Separator());
        var newItem = new MenuItem { Header = SalesText.NewLabelMenu };
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
            SalesText.NewLabelTitle, SalesText.LabelPrompt, SalesText.NewLabelHint).ShowDialog<string?>(owner);

        if (!string.IsNullOrWhiteSpace(typed)) await vm.AddLabelToAsync(rows, typed);
    }

    private static SaleRowVm? Row(object? sender) => (sender as Control)?.DataContext as SaleRowVm;

    // The rollups up top carry their destination as an Action rather than ids, because a group
    // row is a name and a total — it borrows the link from the first sale that made it.
    private void OnOpenGroup(object? sender, RoutedEventArgs e)
        => ((sender as Control)?.DataContext as GroupRowVm)?.Open?.Invoke();

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
