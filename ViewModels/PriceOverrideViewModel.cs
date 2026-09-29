using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using EveConsole.Models;
using EveConsole.Services;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

// One editable row in the Price Override grid. The three value cells are exposed as text so blank
// means "no override" (null) and typos don't throw binding exceptions; parsing is culture-invariant
// and tolerant of thousands separators.
public class PriceOverrideRow : ReactiveObject
{
    public int    TypeId   { get; }
    public string TypeName { get; }

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public PriceOverrideRow(int typeId, string typeName, decimal? build, decimal? market, decimal? contract)
    {
        TypeId   = typeId;
        TypeName = typeName;
        _buildCostText     = Fmt(build);
        _marketValueText   = Fmt(market);
        _contractValueText = Fmt(contract);
    }

    private static string Fmt(decimal? v) => v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";

    private static decimal? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = s.Replace(",", "").Replace("_", "").Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d >= 0
            ? d : (decimal?)null;
    }

    private string _buildCostText;
    public string BuildCostText
    {
        get => _buildCostText;
        set => this.RaiseAndSetIfChanged(ref _buildCostText, value);
    }

    private string _marketValueText;
    public string MarketValueText
    {
        get => _marketValueText;
        set => this.RaiseAndSetIfChanged(ref _marketValueText, value);
    }

    private string _contractValueText;
    public string ContractValueText
    {
        get => _contractValueText;
        set => this.RaiseAndSetIfChanged(ref _contractValueText, value);
    }

    public decimal? BuildCost     => Parse(_buildCostText);
    public decimal? MarketValue   => Parse(_marketValueText);
    public decimal? ContractValue => Parse(_contractValueText);
}

public class PriceOverrideViewModel : ReactiveObject
{
    private readonly PriceOverrideService _svc;
    private readonly BuildCostService     _buildCosts;

    public ObservableCollection<PriceOverrideRow> Rows { get; } = [];

    private PriceOverrideRow? _selectedRow;
    public PriceOverrideRow? SelectedRow
    {
        get => _selectedRow;
        set => this.RaiseAndSetIfChanged(ref _selectedRow, value);
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        set => this.RaiseAndSetIfChanged(ref _busy, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand           { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand          { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand       { get; }

    // Set by the view — opens the type-search dialog and returns the chosen type.
    public Func<Task<AddItemDialogResult?>>? ShowAddItemDialog { get; set; }

    public PriceOverrideViewModel(PriceOverrideService svc, BuildCostService buildCosts)
    {
        _svc        = svc;
        _buildCosts = buildCosts;

        AddCommand            = ReactiveCommand.CreateFromTask(AddAsync);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync);
        SaveCommand           = ReactiveCommand.CreateFromTask(SaveAndRecalcAsync);
        RefreshCommand        = ReactiveCommand.CreateFromTask(LoadAsync);

        _ = LoadAsync();
    }

    public Task<IReadOnlyList<InvTypeResult>> SearchTypesAsync(string text) => _svc.SearchTypesAsync(text);

    private async Task LoadAsync()
    {
        var all = await _svc.GetAllAsync();
        Rows.Clear();
        foreach (var o in all)
            Rows.Add(new PriceOverrideRow(o.TypeId, o.TypeName, o.BuildCost, o.MarketValue, o.ContractValue));
        Status = Rows.Count == 0 ? IndustryText.OverrideStatusNone
                                 : string.Format(IndustryText.OverrideStatusCount, Rows.Count);
    }

    private async Task AddAsync()
    {
        if (ShowAddItemDialog is null) return;
        var result = await ShowAddItemDialog();
        if (result is null) return;

        var existing = Rows.FirstOrDefault(r => r.TypeId == result.TypeId);
        if (existing is not null) { SelectedRow = existing; return; }

        var row = new PriceOverrideRow(result.TypeId, result.TypeName, null, null, null);
        Rows.Add(row);
        SelectedRow = row;
        Status = string.Format(IndustryText.OverrideStatusAdded, result.TypeName, IndustryText.SaveRecalculate);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedRow is null) return;
        var row = SelectedRow;
        await _svc.DeleteAsync(row.TypeId);
        Rows.Remove(row);
        Status = string.Format(IndustryText.OverrideStatusRemoved, row.TypeName);
    }

    private async Task SaveAndRecalcAsync()
    {
        Busy = true;
        try
        {
            // Persist every row; a row with all three cells blank is dropped (no-op override).
            foreach (var r in Rows.ToList())
            {
                if (r.BuildCost is null && r.MarketValue is null && r.ContractValue is null)
                {
                    await _svc.DeleteAsync(r.TypeId);
                    Rows.Remove(r);
                    continue;
                }
                await _svc.UpsertAsync(new PriceOverride
                {
                    TypeId        = r.TypeId,
                    TypeName      = r.TypeName,
                    BuildCost     = r.BuildCost,
                    MarketValue   = r.MarketValue,
                    ContractValue = r.ContractValue,
                });
            }

            Status = IndustryText.OverrideStatusSaving;
            await _buildCosts.RecalculateAllAsync();
            Status = string.Format(IndustryText.OverrideStatusSaved, Rows.Count, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            Status = string.Format(IndustryText.OverrideStatusSaveFailed, ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }
}
