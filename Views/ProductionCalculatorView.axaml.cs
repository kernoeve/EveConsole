using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EveConsole.Models;
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class ProductionCalculatorView : UserControl
{
    public ProductionCalculatorView()
    {
        InitializeComponent();
    }

    // ── DataGrid double-click → Item Browser ─────────────────────────────

    /// <summary>The search box: arrows move through the results, Enter picks one — or, with one
    /// picked, queues it — and Escape closes the list.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down:   vm.MoveHighlight(+1); e.Handled = true; break;
            case Key.Up:     vm.MoveHighlight(-1); e.Handled = true; break;
            case Key.Enter:  vm.AcceptSearch();    e.Handled = true; break;
        }
    }

    /// <summary>Enter in the quantity or ME box queues the item picked.</summary>
    private void OnAddFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ProductionCalculatorViewModel vm || !vm.CanAdd) return;
        vm.AcceptSearch();
        e.Handled = true;
        this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    private void OnDataGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm) return;
        if (sender is not DataGrid dg) return;

        int? typeId = dg.SelectedItem switch
        {
            PlanRawMaterial  rm => rm.TypeId,
            PlanFinalProduct fp => fp.TypeId,
            PlanIntermediate pi => pi.TypeId,
            PlanLeftoverItem li => li.TypeId,
            _ => null
        };

        if (typeId.HasValue)
            vm.OpenInItemBrowserCommand.Execute(typeId.Value).Subscribe();
    }

    // ── Raw Materials toolbar ─────────────────────────────────────────────
    //
    // The shopping lists and the exports copy TypeName, the English, not the name the grid shows:
    // they are pasted into the game's multibuy and into appraisal tools and spreadsheets.

    private async void OnShoppingListClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm || vm.Plan is null) return;
        var sb = new StringBuilder();
        foreach (var mat in vm.Plan.RawMaterials)
            sb.Append(mat.TypeName).Append('\t').AppendLine(mat.Quantity.ToString());
        await CopyToClipboardAsync(sb.ToString());
    }

    private async void OnShoppingListMissingClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm || vm.Plan is null) return;
        // Rows whose availability is unknown are skipped rather than listed at full
        // quantity — an unlinked structure is a gap in the answer, not a shortfall.
        var sb = new StringBuilder();
        foreach (var mat in vm.Plan.RawMaterials.Where(m => m.AvailabilityKnown && m.Missing > 0))
            sb.Append(mat.TypeName).Append('\t').AppendLine(mat.Missing.ToString());
        await CopyToClipboardAsync(sb.ToString());
    }

    // ── Per-job shopping lists ────────────────────────────────────────────
    // Buy items only. A material this job builds rather than buys has its own job in the
    // tree, and listing it here would double-count against that job's own inputs.

    private async void OnJobShoppingListClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not JobTreeNode node) return;
        var sb = new StringBuilder();
        foreach (var mat in node.Job.Materials.Where(m => m.IsBought))
            sb.Append(mat.TypeName).Append('\t').AppendLine(mat.TotalQty.ToString());
        await CopyToClipboardAsync(sb.ToString());
    }

    private async void OnJobShoppingListMissingClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not JobTreeNode node) return;
        var sb = new StringBuilder();
        foreach (var mat in node.Job.Materials.Where(m => m.IsBought && m.AvailabilityKnown && m.Missing > 0))
            sb.Append(mat.TypeName).Append('\t').AppendLine(mat.Missing.ToString());
        await CopyToClipboardAsync(sb.ToString());
    }

    // ── Per-facility shopping lists (Jobs tab, station view) ──────────────
    // Bought materials only, as for a job: one built here or elsewhere has its own job.

    private async void OnStationShoppingListClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not JobStationGroup group) return;
        var sb = new StringBuilder();
        foreach (var mat in group.Materials.Where(m => m.IsBought))
            sb.Append(mat.TypeName).Append('\t').AppendLine(mat.Needed.ToString());
        await CopyToClipboardAsync(sb.ToString());
    }

    private async void OnStationShoppingListMissingClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not JobStationGroup group) return;
        var sb = new StringBuilder();
        foreach (var mat in group.Materials.Where(m => m.IsBought && m.Missing > 0))
            sb.Append(mat.TypeName).Append('\t').AppendLine(mat.Missing.ToString());
        await CopyToClipboardAsync(sb.ToString());
    }

    /// <summary>The raw materials export's header row, in the grid's own column names, ending in a
    /// newline. ⚠️ In a CSV a header holding a comma is quoted, as a material name is: the names
    /// are translated, and a translation may have one.</summary>
    private static string RawMaterialsHeader(char separator)
    {
        string[] headers = [IndustryText.ColMaterial, IndustryText.ColQuantity,
                            IndustryText.ColUnitPrice, IndustryText.ColTotalCost];
        return string.Join(separator, headers.Select(h =>
                   separator == ',' && h.Contains(',') ? $"\"{h}\"" : h)) + "\n";
    }

    private async void OnExportClipboardClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm || vm.Plan is null) return;
        var sb = new StringBuilder(RawMaterialsHeader('\t'));
        foreach (var m in vm.Plan.RawMaterials)
            sb.Append(m.TypeName).Append('\t')
              .Append(m.Quantity.ToString("N0")).Append('\t')
              .Append(m.UnitPrice.ToString("N2")).Append('\t')
              .AppendLine(m.TotalCost.ToString("N0"));
        await CopyToClipboardAsync(sb.ToString());
    }

    private async void OnExportCsvClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm || vm.Plan is null) return;
        var sb = new StringBuilder(RawMaterialsHeader(','));
        foreach (var m in vm.Plan.RawMaterials)
        {
            var name = m.TypeName.Contains(',') ? $"\"{m.TypeName}\"" : m.TypeName;
            sb.Append(name).Append(',')
              .Append(m.Quantity).Append(',')
              .Append(m.UnitPrice.ToString("N2")).Append(',')
              .AppendLine(m.TotalCost.ToString("N0"));
        }
        await SaveToFileAsync(sb.ToString(), "raw_materials.csv",
            new FilePickerFileType("CSV") { Patterns = ["*.csv"] });
    }

    private async void OnExportTabClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProductionCalculatorViewModel vm || vm.Plan is null) return;
        var sb = new StringBuilder(RawMaterialsHeader('\t'));
        foreach (var m in vm.Plan.RawMaterials)
            sb.Append(m.TypeName).Append('\t')
              .Append(m.Quantity.ToString("N0")).Append('\t')
              .Append(m.UnitPrice.ToString("N2")).Append('\t')
              .AppendLine(m.TotalCost.ToString("N0"));
        await SaveToFileAsync(sb.ToString(), "raw_materials.txt",
            new FilePickerFileType(IndustryText.FileTypeText) { Patterns = ["*.txt"] });
    }

    // ── Row links ─────────────────────────────────────────────────────────
    //
    // The plan rows bind straight into the grids, so each carries its own OpenItem and this is
    // pure dispatch. One handler for all four: separate classes with the same link, and matching
    // on type here beats four identical methods.
    private void OnOpenPlanItem(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case PlanRawMaterial  r: r.OpenItem(); break;
            case PlanIntermediate r: r.OpenItem(); break;
            case PlanFinalProduct r: r.OpenItem(); break;
            case PlanLeftoverItem r: r.OpenItem(); break;
        }
    }

    private static PlanJob? Job(object? sender)
        => ((sender as Control)?.DataContext as JobTreeNode)?.Job;

    private void OnOpenJobStation(object? sender, RoutedEventArgs e) => Job(sender)?.OpenStation();
    private void OnOpenJobSystem(object? sender, RoutedEventArgs e)  => Job(sender)?.OpenSystem();

    private static JobStationGroup? Group(object? sender) => (sender as Control)?.DataContext as JobStationGroup;

    private void OnOpenGroupStation(object? sender, RoutedEventArgs e) => Group(sender)?.Lead.OpenStation();
    private void OnOpenGroupSystem(object? sender, RoutedEventArgs e)  => Group(sender)?.Lead.OpenSystem();

    private void OnOpenStationMaterial(object? sender, RoutedEventArgs e) =>
        ((sender as Control)?.DataContext as StationMaterialLine)?.OpenItem();

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task CopyToClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    private async Task SaveToFileAsync(string content, string suggestedName, FilePickerFileType fileType)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = IndustryText.TitleExportRawMaterials,
            SuggestedFileName = suggestedName,
            FileTypeChoices   = [fileType],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(content);
    }
}
