using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// ── Dialog results ─────────────────────────────────────────────────────────────

public record GroupDialogResult(
    string  Name,
    long    StationId,
    string  StationName,
    int?    MarketSourceId,
    double? MaxPriceOverPct,
    int     Multiplier = 1,
    int?    CollectionId = null
);

// ── Collection row ────────────────────────────────────────────────────────────

public class MarketCollectionRow : ReactiveObject
{
    public bool IsCollection => true;
    public bool IsGroup      => false;
    public bool IsItem       => false;

    public int?   CollectionId  { get; }
    public bool   IsSynthetic   { get; }

    private string _collectionName;
    public string CollectionName
    {
        get => _collectionName;
        set => this.RaiseAndSetIfChanged(ref _collectionName, value);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(ExpanderIcon));
        }
    }
    public string ExpanderIcon => IsExpanded ? "▼" : "▶";

    public ReactiveCommand<Unit, Unit> ToggleCommand      { get; }
    public ReactiveCommand<Unit, Unit> RenameCommand      { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand      { get; }
    public ReactiveCommand<Unit, Unit> ExpandAllCommand   { get; }
    public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }

    public MarketCollectionRow(int? collectionId, string name, bool isSynthetic,
        Action toggle, Func<Task> rename, Func<Task> delete,
        Action expandAll, Action collapseAll)
    {
        CollectionId      = collectionId;
        _collectionName   = name;
        IsSynthetic       = isSynthetic;
        ToggleCommand     = ReactiveCommand.Create(toggle);
        RenameCommand     = ReactiveCommand.CreateFromTask(rename);
        DeleteCommand     = ReactiveCommand.CreateFromTask(delete);
        ExpandAllCommand  = ReactiveCommand.Create(expandAll);
        CollapseAllCommand = ReactiveCommand.Create(collapseAll);
    }
}

public record AddItemDialogResult(int TypeId, string TypeName, int TargetQty);

// ── Supporting VMs ────────────────────────────────────────────────────────────

public class MarketSourceOptionVm(int? id, string label)
{
    public int?   Id    { get; } = id;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

public class TypeResultVm(int typeId, string name)
{
    public int    TypeId { get; } = typeId;
    public string Name   { get; } = name;
}

// ── Grid row hierarchy ────────────────────────────────────────────────────────

public class MarketGroupRow : ReactiveObject
{
    public bool IsCollection => false;
    public bool IsGroup      => true;
    public bool IsItem       => false;

    public int    GroupId      { get; }
    public int?   CollectionId { get; set; }
    public long   StationId    { get; set; }
    public string StationName  { get; set; } = "";
    public int?   SourceId     { get; set; }
    public double? MaxPctOver  { get; set; }

    private string _groupName = "";
    public string GroupName
    {
        get => _groupName;
        set => this.RaiseAndSetIfChanged(ref _groupName, value);
    }

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            this.RaisePropertyChanged(nameof(ExpanderIcon));
        }
    }
    public string ExpanderIcon => IsExpanded ? "▼" : "▶";

    private int _multiplier = 1;
    private Func<int, Task>? _saveMultiplier;

    public int Multiplier
    {
        get => _multiplier;
        set
        {
            var v = Math.Max(1, value);
            this.RaiseAndSetIfChanged(ref _multiplier, v);
            foreach (var item in AllItems)
                item.GroupMultiplier = v;
            if (_saveMultiplier != null)
                _ = _saveMultiplier(v);
        }
    }

    public List<MarketItemRow> AllItems { get; } = [];

    public ReactiveCommand<Unit, Unit> ToggleCommand    { get; }
    public ReactiveCommand<Unit, Unit> EditCommand      { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddItemCommand   { get; }

    public MarketGroupRow(MarketLevelGroup g,
        Action toggle, Func<Task> edit, Func<Task> delete, Func<Task> addItem,
        Func<int, Task>? saveMultiplier = null)
    {
        GroupId          = g.Id;
        CollectionId     = g.CollectionId;
        _groupName       = g.Name;
        StationId        = g.StationId;
        StationName      = g.StationName;
        SourceId         = g.MarketSourceId;
        MaxPctOver       = g.MaxPriceOverPct;
        _multiplier      = Math.Max(1, g.Multiplier);
        _saveMultiplier  = saveMultiplier;

        ToggleCommand  = ReactiveCommand.Create(toggle);
        EditCommand    = ReactiveCommand.CreateFromTask(edit);
        DeleteCommand  = ReactiveCommand.CreateFromTask(delete);
        AddItemCommand = ReactiveCommand.CreateFromTask(addItem);
    }
}

public class MarketItemRow : ReactiveObject
{
    private static readonly SolidColorBrush Green   = new(Color.Parse("#4a9a4a"));
    private static readonly SolidColorBrush Red     = new(Color.Parse("#9a4a4a"));

    private readonly MarketLevelService _svc;

    public bool IsCollection => false;
    public bool IsGroup      => false;
    public bool IsItem       => true;

    public int    ItemId  { get; }
    public int    GroupId { get; }
    public int    TypeId  { get; }
    public string TypeName { get; }

    private int _groupMultiplier = 1;
    public int GroupMultiplier
    {
        get => _groupMultiplier;
        set
        {
            _groupMultiplier = Math.Max(1, value);
            RaiseDiffDependents();
        }
    }

    private int _targetQty;
    public int TargetQty
    {
        get => _targetQty;
        set
        {
            this.RaiseAndSetIfChanged(ref _targetQty, value);
            RaiseDiffDependents();
            _ = _svc.SaveItemAsync(new MarketLevelItem
                { Id = ItemId, GroupId = GroupId, TypeId = TypeId, TargetQuantity = value });
        }
    }

    // Data (updatable on each refresh)
    private int     _available;
    private double  _volume;
    private double? _marketPrice;
    private double? _stationMin;
    private double? _stationAvg;
    private double? _stationMax;
    private double? _buildPrice;

    public int     Available   => _available;
    public double  Volume      => _volume;
    public double? MarketPrice => _marketPrice;
    public double? StationMin  => _stationMin;
    public double? StationAvg  => _stationAvg;
    public double? StationMax  => _stationMax;
    public double? BuildPrice  => _buildPrice;
    public string  VolumeText     => _volume > 0 ? _volume.ToString("N2")         : "—";
    public string  BuildPriceText => _buildPrice.HasValue ? Isk(_buildPrice.Value) : "—";

    // Derived
    public int    TargetTotal => _targetQty * _groupMultiplier;
    public int    Diff        => _available - TargetTotal;
    public double DiffPct     => TargetTotal > 0 ? (double)Diff / TargetTotal * 100.0 : 0.0;

    public double? MinDelta    => _stationMin.HasValue && _marketPrice.HasValue ? _stationMin - _marketPrice : null;
    public double? MinDeltaPct => MinDelta.HasValue && _marketPrice > 0 ? MinDelta / _marketPrice * 100.0 : null;
    public double? AvgDelta    => _stationAvg.HasValue && _marketPrice.HasValue ? _stationAvg - _marketPrice : null;
    public double? AvgDeltaPct => AvgDelta.HasValue && _marketPrice > 0 ? AvgDelta / _marketPrice * 100.0 : null;
    public double? MaxDelta    => _stationMax.HasValue && _marketPrice.HasValue ? _stationMax - _marketPrice : null;
    public double? MaxDeltaPct => MaxDelta.HasValue && _marketPrice > 0 ? MaxDelta / _marketPrice * 100.0 : null;

    // Formatted text
    public string AvailableText  => Available.ToString("N0");
    public string TargetTotalText => TargetTotal.ToString("N0");
    public string DiffText        => FormatQty(Diff);
    public string DiffPctText     => TargetTotal > 0 ? $"{DiffPct:+0.0;-0.0}%" : "—";
    public string MktPriceText   => _marketPrice.HasValue ? Isk(_marketPrice.Value) : "—";
    public string StMinText      => _stationMin.HasValue  ? Isk(_stationMin.Value)  : "—";
    public string StAvgText      => _stationAvg.HasValue  ? Isk(_stationAvg.Value)  : "—";
    public string StMaxText      => _stationMax.HasValue  ? Isk(_stationMax.Value)  : "—";
    public string MinDeltaText   => MinDelta.HasValue    ? IskDiff(MinDelta.Value)            : "—";
    public string MinDeltaPctText=> MinDeltaPct.HasValue ? $"{MinDeltaPct.Value:+0.0;-0.0}%" : "—";
    public string AvgDeltaText   => AvgDelta.HasValue    ? IskDiff(AvgDelta.Value)            : "—";
    public string AvgDeltaPctText=> AvgDeltaPct.HasValue ? $"{AvgDeltaPct.Value:+0.0;-0.0}%" : "—";
    public string MaxDeltaText   => MaxDelta.HasValue    ? IskDiff(MaxDelta.Value)            : "—";
    public string MaxDeltaPctText=> MaxDeltaPct.HasValue ? $"{MaxDeltaPct.Value:+0.0;-0.0}%" : "—";

    // Colors
    public IBrush DiffColor     => Diff >= 0             ? Green : Red;
    public IBrush MinDeltaColor => (MinDelta ?? 0) >= 0  ? Green : Red;
    public IBrush AvgDeltaColor => (AvgDelta ?? 0) >= 0  ? Green : Red;
    public IBrush MaxDeltaColor => (MaxDelta ?? 0) >= 0  ? Green : Red;

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public MarketItemRow(MarketLevelItem item, string name, MarketLevelService svc, Func<Task> delete,
        int groupMultiplier = 1)
    {
        ItemId   = item.Id;
        GroupId  = item.GroupId;
        TypeId   = item.TypeId;
        TypeName = name;
        _targetQty       = item.TargetQuantity;
        _groupMultiplier = Math.Max(1, groupMultiplier);
        _svc             = svc;
        DeleteCommand    = ReactiveCommand.CreateFromTask(delete);
    }

    public void UpdateData(MarketLevelRowData d)
    {
        _available   = d.AvailableUnits;
        _volume      = d.Volume;
        _marketPrice = d.MarketPrice;
        _stationMin  = d.StationMin;
        _stationAvg  = d.StationAvg;
        _stationMax  = d.StationMax;
        _buildPrice  = d.BuildPrice;
        RaiseDiffDependents();
        this.RaisePropertyChanged(nameof(Volume));
        this.RaisePropertyChanged(nameof(VolumeText));
        this.RaisePropertyChanged(nameof(MktPriceText));
        this.RaisePropertyChanged(nameof(BuildPriceText));
        this.RaisePropertyChanged(nameof(StMinText));
        this.RaisePropertyChanged(nameof(StAvgText));
        this.RaisePropertyChanged(nameof(StMaxText));
        this.RaisePropertyChanged(nameof(MinDelta));
        this.RaisePropertyChanged(nameof(MinDeltaPct));
        this.RaisePropertyChanged(nameof(MinDeltaText));
        this.RaisePropertyChanged(nameof(MinDeltaPctText));
        this.RaisePropertyChanged(nameof(MinDeltaColor));
        this.RaisePropertyChanged(nameof(AvgDelta));
        this.RaisePropertyChanged(nameof(AvgDeltaPct));
        this.RaisePropertyChanged(nameof(AvgDeltaText));
        this.RaisePropertyChanged(nameof(AvgDeltaPctText));
        this.RaisePropertyChanged(nameof(AvgDeltaColor));
        this.RaisePropertyChanged(nameof(MaxDelta));
        this.RaisePropertyChanged(nameof(MaxDeltaPct));
        this.RaisePropertyChanged(nameof(MaxDeltaText));
        this.RaisePropertyChanged(nameof(MaxDeltaPctText));
        this.RaisePropertyChanged(nameof(MaxDeltaColor));
    }

    private void RaiseDiffDependents()
    {
        this.RaisePropertyChanged(nameof(Available));
        this.RaisePropertyChanged(nameof(AvailableText));
        this.RaisePropertyChanged(nameof(TargetTotal));
        this.RaisePropertyChanged(nameof(TargetTotalText));
        this.RaisePropertyChanged(nameof(Diff));
        this.RaisePropertyChanged(nameof(DiffPct));
        this.RaisePropertyChanged(nameof(DiffText));
        this.RaisePropertyChanged(nameof(DiffPctText));
        this.RaisePropertyChanged(nameof(DiffColor));
    }

    private static string Isk(double v)
    {
        double abs = Math.Abs(v);
        if (abs >= 1e9) return $"{v / 1e9:N2}B";
        if (abs >= 1e6) return $"{v / 1e6:N2}M";
        if (abs >= 1e3) return $"{v / 1e3:N2}K";
        return $"{v:N2}";
    }

    private static string IskDiff(double v)
    {
        string sign = v >= 0 ? "+" : "";
        double abs  = Math.Abs(v);
        if (abs >= 1e9) return $"{sign}{v / 1e9:N2}B";
        if (abs >= 1e6) return $"{sign}{v / 1e6:N2}M";
        if (abs >= 1e3) return $"{sign}{v / 1e3:N2}K";
        return $"{sign}{v:N2}";
    }

    private static string FormatQty(int v)
    {
        if (Math.Abs(v) >= 1_000_000) return $"{v / 1_000_000.0:N2}M";
        if (Math.Abs(v) >= 1_000)     return $"{v / 1_000.0:N1}K";
        return v.ToString("N0");
    }
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public class MarketLevelViewModel : ReactiveObject
{
    private readonly MarketLevelService              _svc;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly List<MarketGroupRow>        _allGroups      = [];
    private readonly List<MarketCollectionRow>   _allCollections = [];
    private          MarketCollectionRow?        _defaultCollRow;
    private readonly FittingsService?                 _fittings;
    private readonly ObservableCollection<Character>?   _characters;
    private readonly ObservableCollection<Corporation>? _corporations;
    private readonly BatchAddService?                 _batchSvc;
    private readonly ProductionCalculatorService?     _prodCalc;

    // ── Grid rows (flat list of groups + visible items) ───────────────────────
    public ObservableCollection<object> GridRows { get; } = [];

    private object? _selectedRow;
    public object? SelectedRow
    {
        get => _selectedRow;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRow, value);
            this.RaisePropertyChanged(nameof(IsItemRowSelected));
        }
    }
    public bool IsItemRowSelected => _selectedRow is MarketItemRow;

    private bool _hasAnyGroup;
    public bool HasAnyGroup
    {
        get => _hasAnyGroup;
        private set => this.RaiseAndSetIfChanged(ref _hasAnyGroup, value);
    }

    // ── Station picker / market sources (for dialogs) ─────────────────────────
    public ObservableCollection<MarketLevelStation>    AvailableStations { get; } = [];
    public ObservableCollection<MarketSourceOptionVm>  MarketSources     { get; } = [];

    // ── Status ────────────────────────────────────────────────────────────────
    private string _statusText = "Loading…";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _lastRefreshed = "";
    public string LastRefreshed
    {
        get => _lastRefreshed;
        private set => this.RaiseAndSetIfChanged(ref _lastRefreshed, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> AddGroupCommand              { get; }
    public ReactiveCommand<Unit, Unit> AddCollectionCommand         { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedItemCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddFromFitCommand            { get; }
    public ReactiveCommand<Unit, Unit> AddFromMarketGroupCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddFromBlueprintCommand      { get; }
    public ReactiveCommand<Unit, Unit> OpenInItemBrowserCommand     { get; }

    // ── Dialog delegates (wired up by View code-behind) ───────────────────────
    public Func<IReadOnlyList<CollectionOption>, Task<GroupDialogResult?>>?              ShowAddGroupDialog          { get; set; }
    public Func<MarketGroupRow, IReadOnlyList<CollectionOption>, Task<GroupDialogResult?>>? ShowEditGroupDialog      { get; set; }
    public Func<MarketGroupRow, Task<AddItemDialogResult?>>?                             ShowAddItemDialog           { get; set; }
    public Func<Task<FitSelectorResult?>>?                                               ShowFitSelectorDialog       { get; set; }
    public Func<Task<MarketGroupPickerResult?>>?                                         ShowMarketGroupPickerDialog { get; set; }
    public Func<Task<BlueprintPickerResult?>>?                                           ShowBlueprintPickerDialog   { get; set; }
    public Func<string, int, Task<bool>>?                                                ShowConfirmLargeGroup       { get; set; }
    public Func<Task<string?>>?                                                          ShowAddCollectionDialog     { get; set; }
    public Func<string, Task<string?>>?                                                  ShowRenameCollectionDialog  { get; set; }
    public Action<int, string>?                                                          OpenInItemBrowser           { get; set; }

    public MarketLevelViewModel(
        MarketLevelService              svc,
        IDbContextFactory<AppDbContext> dbFactory,
        FittingsService?                fittings      = null,
        ObservableCollection<Character>?   characters   = null,
        ObservableCollection<Corporation>? corporations = null,
        BatchAddService?                batchSvc      = null,
        ProductionCalculatorService?    prodCalc      = null)
    {
        _svc          = svc;
        _dbFactory    = dbFactory;
        _fittings     = fittings;
        _characters   = characters;
        _corporations = corporations;
        _batchSvc     = batchSvc;
        _prodCalc     = prodCalc;

        var hasItem  = this.WhenAnyValue(x => x.IsItemRowSelected);
        var hasGroup = this.WhenAnyValue(x => x.HasAnyGroup);

        AddGroupCommand              = ReactiveCommand.CreateFromTask(AddGroupAsync);
        AddCollectionCommand         = ReactiveCommand.CreateFromTask(AddMarketCollectionAsync);
        DeleteSelectedItemCommand    = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasItem);
        AddFromFitCommand            = ReactiveCommand.CreateFromTask(AddFromFitInvokeAsync, hasGroup);
        AddFromMarketGroupCommand    = ReactiveCommand.CreateFromTask(AddFromMarketGroupAsync, hasGroup);
        AddFromBlueprintCommand      = ReactiveCommand.CreateFromTask(AddFromBlueprintAsync, hasGroup);
        OpenInItemBrowserCommand     = ReactiveCommand.Create(OpenSelectedInItemBrowser, hasItem);

        Observable.Interval(TimeSpan.FromMinutes(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await AutoRefreshAsync());

        _ = InitializeAsync();
    }

    // ── Fit selector helpers ──────────────────────────────────────────────────

    public FitSelectorViewModel CreateFitSelectorViewModel()
    {
        var groupOptions = _allGroups
            .Select(g => new FitGroupOption(g.GroupId, g.GroupName))
            .ToList();

        var preselectedId = _selectedRow switch
        {
            MarketGroupRow g => g.GroupId,
            MarketItemRow  i => i.GroupId,
            _                => groupOptions.Count > 0 ? groupOptions[0].GroupId : 0
        };

        return new FitSelectorViewModel(
            _fittings!,
            _dbFactory,
            _characters!,
            _corporations!,
            groupOptions,
            preselectedId);
    }

    private async Task AddFromFitInvokeAsync()
    {
        if (ShowFitSelectorDialog == null || _fittings == null) return;
        var result = await ShowFitSelectorDialog();
        if (result == null) return;
        await AddFromFitAsync(result);
    }

    private async Task AddFromFitAsync(FitSelectorResult result)
    {
        var groupRow = _allGroups.FirstOrDefault(g => g.GroupId == result.TargetGroupId);
        if (groupRow == null) return;

        var fitting = result.Fitting;

        // Aggregate items by TypeId
        var items = new Dictionary<int, int> { [fitting.ShipTypeId] = 1 };

        var skipFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Invalid", "Implant", "BoosterBay" };

        foreach (var item in fitting.Items)
        {
            if (skipFlags.Contains(item.Flag)) continue;
            items.TryGetValue(item.TypeId, out var q);
            items[item.TypeId] = q + item.Quantity;
        }

        // Skip items already in the group
        var existingIds = groupRow.AllItems.Select(i => i.TypeId).ToHashSet();
        var newItems    = items.Where(kv => !existingIds.Contains(kv.Key)).ToList();

        if (newItems.Count == 0)
        {
            StatusText = $"All items from '{fitting.Name}' are already in the group.";
            return;
        }

        // Fetch type names in one query
        var newTypeIds = newItems.Select(kv => kv.Key).ToList();
        await using var db = _dbFactory.CreateDbContext();
        var typeNames = await db.SdeTypes
            .Where(t => newTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name);

        foreach (var (typeId, qty) in newItems)
        {
            var saved = await _svc.SaveItemAsync(new MarketLevelItem
            {
                GroupId        = groupRow.GroupId,
                TypeId         = typeId,
                TargetQuantity = qty,
            });

            var typeName = typeNames.GetValueOrDefault(typeId, $"TypeId {typeId}");
            MarketItemRow? itemRow = null;
            itemRow = new MarketItemRow(saved, typeName, _svc,
                delete: async () => await DeleteItemRowAsync(itemRow!),
                groupMultiplier: groupRow.Multiplier);

            groupRow.AllItems.Add(itemRow);
        }
        SortItemsAlpha(groupRow);

        // Refresh market data for the whole group
        var g = new MarketLevelGroup
        {
            Id              = groupRow.GroupId,
            StationId       = groupRow.StationId,
            MarketSourceId  = groupRow.SourceId,
            MaxPriceOverPct = groupRow.MaxPctOver,
        };
        var dataResult = await _svc.LoadGroupDataAsync(g);
        var dataMap    = dataResult.Rows.ToDictionary(r => r.TypeId);
        foreach (var ir in groupRow.AllItems)
            if (dataMap.TryGetValue(ir.TypeId, out var d)) ir.UpdateData(d);

        if (groupRow.IsExpanded) RebuildGridRows();

        var skipped = items.Count - newItems.Count;
        StatusText = $"Added {newItems.Count} item(s) from '{fitting.Name}'"
                   + (skipped > 0 ? $"; {skipped} already present, skipped" : "");
    }

    // ── Market group add ──────────────────────────────────────────────────────

    private async Task AddFromMarketGroupAsync()
    {
        if (ShowMarketGroupPickerDialog == null || _batchSvc == null) return;
        var pick = await ShowMarketGroupPickerDialog();
        if (pick == null) return;

        StatusText = $"Loading items in '{pick.GroupName}'…";
        var typeList = await _batchSvc.GetItemsInGroupTreeAsync(pick.MarketGroupId);

        if (typeList.Count == 0)
        {
            StatusText = $"No published items found under '{pick.GroupName}'.";
            return;
        }

        if (typeList.Count > 100 && ShowConfirmLargeGroup != null)
        {
            var confirmed = await ShowConfirmLargeGroup(pick.GroupName, typeList.Count);
            if (!confirmed) { StatusText = "Cancelled."; return; }
        }

        var targetGroup = GetContextGroup();
        if (targetGroup == null) { StatusText = "No group selected."; return; }

        var itemsWithQty = typeList.ToDictionary(x => x.TypeId, _ => pick.TargetQty);
        var nameOverrides = typeList.ToDictionary(x => x.TypeId, x => x.Name);
        await AddItemsBatchAsync(targetGroup, itemsWithQty, pick.GroupName, nameOverrides);
    }

    // ── Blueprint add ─────────────────────────────────────────────────────────

    private async Task AddFromBlueprintAsync()
    {
        if (ShowBlueprintPickerDialog == null || _prodCalc == null) return;
        var pick = await ShowBlueprintPickerDialog();
        if (pick == null) return;

        StatusText = "Calculating materials…";
        Dictionary<int, (int Qty, string Name)> mats;
        try
        {
            if (pick.WholeChain)
                mats = await _prodCalc.GetChainMaterialsAsync(
                    pick.ProductTypeId, pick.Runs, pick.ME, pick.ParkId);
            else
                mats = await _prodCalc.GetDirectMaterialsAsync(
                    pick.BlueprintTypeId, pick.Runs, pick.ME);
        }
        catch (Exception ex)
        {
            StatusText = $"Calculation error: {ex.Message}";
            return;
        }

        if (mats.Count == 0)
        {
            StatusText = "No materials found for that blueprint.";
            return;
        }

        var targetGroup = GetContextGroup();
        if (targetGroup == null) { StatusText = "No group selected."; return; }

        var itemsWithQty  = mats.ToDictionary(kv => kv.Key, kv => kv.Value.Qty);
        var nameOverrides = mats.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        await AddItemsBatchAsync(targetGroup, itemsWithQty, pick.ProductName, nameOverrides);
    }

    // ── Shared batch-add helper ───────────────────────────────────────────────

    private async Task AddItemsBatchAsync(
        MarketGroupRow groupRow,
        Dictionary<int, int> itemsWithQty,
        string label,
        Dictionary<int, string>? nameOverrides = null)
    {
        var existingIds = groupRow.AllItems.Select(i => i.TypeId).ToHashSet();
        var candidates  = itemsWithQty.Where(kv => !existingIds.Contains(kv.Key)).ToList();
        int alreadyIn   = itemsWithQty.Count - candidates.Count;

        if (candidates.Count == 0)
        {
            StatusText = $"All items from '{label}' are already in the group.";
            return;
        }

        // Fetch missing names
        var names   = nameOverrides != null
            ? new Dictionary<int, string>(nameOverrides)
            : new Dictionary<int, string>();
        var missing = candidates.Select(kv => kv.Key).Where(id => !names.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var fetched = await db.SdeTypes
                .Where(t => missing.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            foreach (var (id, n) in fetched) names[id] = n;
        }

        int added = 0;
        foreach (var (typeId, qty) in candidates)
        {
            var saved = await _svc.SaveItemAsync(new MarketLevelItem
            {
                GroupId        = groupRow.GroupId,
                TypeId         = typeId,
                TargetQuantity = Math.Max(1, qty),
            });

            var typeName = names.GetValueOrDefault(typeId, $"Type {typeId}");
            MarketItemRow? itemRow = null;
            itemRow = new MarketItemRow(saved, typeName, _svc,
                delete: async () => await DeleteItemRowAsync(itemRow!),
                groupMultiplier: groupRow.Multiplier);
            groupRow.AllItems.Add(itemRow);
            added++;
        }
        SortItemsAlpha(groupRow);

        // Refresh market data for the group
        var g = new MarketLevelGroup
        {
            Id              = groupRow.GroupId,
            StationId       = groupRow.StationId,
            MarketSourceId  = groupRow.SourceId,
            MaxPriceOverPct = groupRow.MaxPctOver,
        };
        var dataResult = await _svc.LoadGroupDataAsync(g);
        var dataMap    = dataResult.Rows.ToDictionary(r => r.TypeId);
        foreach (var ir in groupRow.AllItems)
            if (dataMap.TryGetValue(ir.TypeId, out var d)) ir.UpdateData(d);

        if (groupRow.IsExpanded) RebuildGridRows();

        StatusText = alreadyIn > 0
            ? $"Added {added} item(s) from '{label}'; {alreadyIn} already present, skipped."
            : $"Added {added} item(s) from '{label}'.";
    }

    private MarketGroupRow? GetContextGroup() => _selectedRow switch
    {
        MarketGroupRow      g => g,
        MarketItemRow       i => _allGroups.FirstOrDefault(g => g.GroupId == i.GroupId),
        MarketCollectionRow c => _allGroups.FirstOrDefault(g => g.CollectionId == c.CollectionId),
        _                     => _allGroups.Count > 0 ? _allGroups[0] : null
    };

    // ── Init ──────────────────────────────────────────────────────────────────

    private async Task InitializeAsync()
    {
        await LoadMarketSourcesAsync();
        await LoadAvailableStationsAsync();
        await LoadGroupsAsync();
        await LoadAllDataAsync();
    }

    private async Task LoadMarketSourcesAsync()
    {
        using var db   = _dbFactory.CreateDbContext();
        var configs    = await db.MarketPricingConfigs.OrderBy(c => c.SortOrder).ToListAsync();
        var defaults   = await db.MarketDefaultSettings.FindAsync(1);
        int? defaultId = defaults?.AssetValueConfigId;

        MarketSources.Clear();
        MarketSources.Add(new MarketSourceOptionVm(null, "— Asset Default —"));
        foreach (var c in configs)
            MarketSources.Add(new MarketSourceOptionVm(c.Id, c.LocationName));
    }

    private async Task LoadAvailableStationsAsync()
    {
        var stations = await _svc.GetAvailableStationsAsync();
        AvailableStations.Clear();
        foreach (var s in stations) AvailableStations.Add(s);
    }

    private IReadOnlyList<CollectionOption> GetCollectionOptions()
    {
        var opts = new List<CollectionOption> { new(null, "— Default —") };
        opts.AddRange(_allCollections.Select(c => new CollectionOption(c.CollectionId, c.CollectionName)));
        return opts;
    }

    private async Task LoadGroupsAsync()
    {
        var collections = await _svc.GetCollectionsAsync();
        var groups      = await _svc.GetGroupsAsync();

        _allCollections.Clear();
        _allGroups.Clear();
        _defaultCollRow = null;

        foreach (var c in collections)
            _allCollections.Add(MakeMarketCollectionRow(c.Id, c.Name, isSynthetic: false));

        foreach (var g in groups)
        {
            var capturedG = g;
            MarketGroupRow? groupRow = null;

            groupRow = new MarketGroupRow(capturedG,
                toggle:  () =>
                {
                    groupRow!.IsExpanded = !groupRow.IsExpanded;
                    RebuildGridRows();
                },
                edit:    async () => await EditGroupAsync(groupRow!),
                delete:  async () => await DeleteGroupAsync(groupRow!),
                addItem: async () => await AddItemToGroupAsync(groupRow!),
                saveMultiplier: async m => await _svc.SaveGroupAsync(new MarketLevelGroup
                {
                    Id              = groupRow!.GroupId,
                    CollectionId    = groupRow.CollectionId,
                    Name            = groupRow.GroupName,
                    StationId       = groupRow.StationId,
                    StationName     = groupRow.StationName,
                    MarketSourceId  = groupRow.SourceId,
                    MaxPriceOverPct = groupRow.MaxPctOver,
                    Multiplier      = m,
                })
            );

            // Load items for this group
            var items = await _svc.GetItemsAsync(g.Id);
            var typeIds = items.Select(i => i.TypeId).ToHashSet();
            using var db = _dbFactory.CreateDbContext();
            var typeNames = await db.SdeTypes
                .Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            foreach (var item in items)
            {
                var capturedItem = item;
                MarketItemRow? itemRow = null;
                itemRow = new MarketItemRow(capturedItem,
                    typeNames.GetValueOrDefault(item.TypeId, $"TypeId {item.TypeId}"),
                    _svc,
                    delete: async () => await DeleteItemRowAsync(itemRow!),
                    groupMultiplier: groupRow.Multiplier);
                groupRow.AllItems.Add(itemRow);
            }
            SortItemsAlpha(groupRow);

            _allGroups.Add(groupRow);
        }

        if (_allGroups.Any(g => g.CollectionId == null))
            _defaultCollRow = MakeMarketCollectionRow(null, "Default", isSynthetic: true);

        RebuildGridRows();
        HasAnyGroup = _allGroups.Count > 0;
    }

    private static void SortItemsAlpha(MarketGroupRow group)
    {
        var sorted = group.AllItems.OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        group.AllItems.Clear();
        group.AllItems.AddRange(sorted);
    }

    private void RebuildGridRows()
    {
        GridRows.Clear();

        void AddGroupWithItems(MarketGroupRow g)
        {
            GridRows.Add(g);
            if (g.IsExpanded)
                foreach (var item in g.AllItems)
                    GridRows.Add(item);
        }

        foreach (var col in _allCollections)
        {
            GridRows.Add(col);
            if (col.IsExpanded)
                foreach (var g in _allGroups.Where(g => g.CollectionId == col.CollectionId))
                    AddGroupWithItems(g);
        }

        if (_defaultCollRow != null)
        {
            GridRows.Add(_defaultCollRow);
            if (_defaultCollRow.IsExpanded)
                foreach (var g in _allGroups.Where(g => g.CollectionId == null))
                    AddGroupWithItems(g);
        }
    }

    // ── Data loading from cache ───────────────────────────────────────────────

    private async Task LoadAllDataAsync()
    {
        DateTimeOffset? latestFetch = null;
        int totalItems = 0;

        foreach (var group in _allGroups)
        {
            var g = new MarketLevelGroup
            {
                Id              = group.GroupId,
                Name            = group.GroupName,
                StationId       = group.StationId,
                StationName     = group.StationName,
                MarketSourceId  = group.SourceId,
                MaxPriceOverPct = group.MaxPctOver,
            };

            var result = await _svc.LoadGroupDataAsync(g);

            // Update item rows with fresh data
            var dataMap = result.Rows.ToDictionary(r => r.TypeId);
            foreach (var itemRow in group.AllItems)
            {
                if (dataMap.TryGetValue(itemRow.TypeId, out var d))
                    itemRow.UpdateData(d);
            }

            totalItems += group.AllItems.Count;
            if (result.DataFetchedAt.HasValue && (latestFetch == null || result.DataFetchedAt > latestFetch))
                latestFetch = result.DataFetchedAt;
        }

        int groupCount = _allGroups.Count;
        StatusText = groupCount == 0
            ? "No groups configured. Click '+ Add Group' to create one."
            : $"{groupCount} group(s), {totalItems} item(s)";

        if (latestFetch.HasValue)
        {
            var age    = DateTimeOffset.Now - latestFetch.Value;
            string ago = age.TotalHours >= 1
                ? $"{(int)age.TotalHours}h {age.Minutes}m ago"
                : age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes}m ago" : "just now";
            LastRefreshed = $"Data from {ago}";
        }
        else
        {
            LastRefreshed = groupCount > 0 ? "No cached orders — run market pricing to populate." : "";
        }
    }

    private async Task AutoRefreshAsync()
    {
        await LoadAvailableStationsAsync();
        await LoadAllDataAsync();
    }

    // ── Group CRUD ────────────────────────────────────────────────────────────

    private async Task AddGroupAsync()
    {
        if (ShowAddGroupDialog == null) return;

        var result = await ShowAddGroupDialog(GetCollectionOptions());
        if (result == null) return;

        var saved = await _svc.SaveGroupAsync(new MarketLevelGroup
        {
            Name            = result.Name,
            CollectionId    = result.CollectionId,
            StationId       = result.StationId,
            StationName     = result.StationName,
            MarketSourceId  = result.MarketSourceId,
            MaxPriceOverPct = result.MaxPriceOverPct,
            Multiplier      = result.Multiplier,
        });

        MarketGroupRow? groupRow = null;
        groupRow = new MarketGroupRow(saved,
            toggle:  () => { groupRow!.IsExpanded = !groupRow.IsExpanded; RebuildGridRows(); },
            edit:    async () => await EditGroupAsync(groupRow!),
            delete:  async () => await DeleteGroupAsync(groupRow!),
            addItem: async () => await AddItemToGroupAsync(groupRow!),
            saveMultiplier: async m => await _svc.SaveGroupAsync(new MarketLevelGroup
            {
                Id              = saved.Id,
                Name            = groupRow!.GroupName,
                StationId       = groupRow.StationId,
                StationName     = groupRow.StationName,
                MarketSourceId  = groupRow.SourceId,
                MaxPriceOverPct = groupRow.MaxPctOver,
                Multiplier      = m,
            })
        );

        _allGroups.Add(groupRow);

        if (result.CollectionId == null && _defaultCollRow == null)
            _defaultCollRow = MakeMarketCollectionRow(null, "Default", isSynthetic: true);

        RebuildGridRows();
        HasAnyGroup = _allGroups.Count > 0;
        StatusText = $"{_allGroups.Count} group(s)";
    }

    private async Task EditGroupAsync(MarketGroupRow groupRow)
    {
        if (ShowEditGroupDialog == null) return;

        var result = await ShowEditGroupDialog(groupRow, GetCollectionOptions());
        if (result == null) return;

        groupRow.GroupName    = result.Name;
        groupRow.CollectionId = result.CollectionId;
        groupRow.StationId    = result.StationId;
        groupRow.StationName  = result.StationName;
        groupRow.SourceId     = result.MarketSourceId;
        groupRow.MaxPctOver   = result.MaxPriceOverPct;
        groupRow.Multiplier   = result.Multiplier;

        await _svc.SaveGroupAsync(new MarketLevelGroup
        {
            Id              = groupRow.GroupId,
            CollectionId    = result.CollectionId,
            Name            = result.Name,
            StationId       = result.StationId,
            StationName     = result.StationName,
            MarketSourceId  = result.MarketSourceId,
            MaxPriceOverPct = result.MaxPriceOverPct,
            Multiplier      = result.Multiplier,
        });

        if (_allGroups.Any(g => g.CollectionId == null) && _defaultCollRow == null)
            _defaultCollRow = MakeMarketCollectionRow(null, "Default", isSynthetic: true);
        else if (!_allGroups.Any(g => g.CollectionId == null))
            _defaultCollRow = null;

        // Reload data for this group with potentially new station/source
        var g = new MarketLevelGroup
        {
            Id              = groupRow.GroupId,
            Name            = groupRow.GroupName,
            StationId       = groupRow.StationId,
            StationName     = groupRow.StationName,
            MarketSourceId  = groupRow.SourceId,
            MaxPriceOverPct = groupRow.MaxPctOver,
        };
        var dataResult = await _svc.LoadGroupDataAsync(g);
        var dataMap    = dataResult.Rows.ToDictionary(r => r.TypeId);
        foreach (var itemRow in groupRow.AllItems)
        {
            if (dataMap.TryGetValue(itemRow.TypeId, out var d))
                itemRow.UpdateData(d);
        }
    }

    private async Task DeleteGroupAsync(MarketGroupRow groupRow)
    {
        await _svc.DeleteGroupAsync(groupRow.GroupId);
        _allGroups.Remove(groupRow);
        RebuildGridRows();
        HasAnyGroup = _allGroups.Count > 0;
        StatusText = $"{_allGroups.Count} group(s)";
    }

    // ── Item CRUD ─────────────────────────────────────────────────────────────

    private async Task AddItemToGroupAsync(MarketGroupRow groupRow)
    {
        if (ShowAddItemDialog == null) return;

        var result = await ShowAddItemDialog(groupRow);
        if (result == null) return;

        // Skip if already in group
        if (groupRow.AllItems.Any(i => i.TypeId == result.TypeId)) return;

        var item = await _svc.SaveItemAsync(new MarketLevelItem
        {
            GroupId        = groupRow.GroupId,
            TypeId         = result.TypeId,
            TargetQuantity = result.TargetQty,
        });

        MarketItemRow? itemRow = null;
        itemRow = new MarketItemRow(item, result.TypeName, _svc,
            delete: async () => await DeleteItemRowAsync(itemRow!),
            groupMultiplier: groupRow.Multiplier);

        groupRow.AllItems.Add(itemRow);
        SortItemsAlpha(groupRow);

        // Load initial data for this item
        var g = new MarketLevelGroup
        {
            Id              = groupRow.GroupId,
            StationId       = groupRow.StationId,
            MarketSourceId  = groupRow.SourceId,
            MaxPriceOverPct = groupRow.MaxPctOver,
        };
        var dataResult = await _svc.LoadGroupDataAsync(g);
        var dataMap    = dataResult.Rows.ToDictionary(r => r.TypeId);
        foreach (var ir in groupRow.AllItems)
        {
            if (dataMap.TryGetValue(ir.TypeId, out var d)) ir.UpdateData(d);
        }

        if (groupRow.IsExpanded) RebuildGridRows();
    }

    private async Task DeleteItemRowAsync(MarketItemRow itemRow)
    {
        await _svc.DeleteItemAsync(itemRow.ItemId);
        var group = _allGroups.FirstOrDefault(g => g.GroupId == itemRow.GroupId);
        group?.AllItems.Remove(itemRow);
        RebuildGridRows();
    }

    private async Task DeleteSelectedAsync()
    {
        if (_selectedRow is MarketItemRow itemRow)
            await DeleteItemRowAsync(itemRow);
    }

    private void OpenSelectedInItemBrowser()
    {
        if (_selectedRow is MarketItemRow item)
            OpenInItemBrowser?.Invoke(item.TypeId, item.TypeName);
    }

    // ── Collection CRUD ───────────────────────────────────────────────────────

    private MarketCollectionRow MakeMarketCollectionRow(int? collectionId, string name, bool isSynthetic)
    {
        IEnumerable<MarketGroupRow> GetCollGroups() => collectionId.HasValue
            ? _allGroups.Where(g => g.CollectionId == collectionId)
            : _allGroups.Where(g => g.CollectionId == null);

        return new MarketCollectionRow(collectionId, name, isSynthetic,
            toggle: () =>
            {
                var row = collectionId.HasValue
                    ? _allCollections.FirstOrDefault(c => c.CollectionId == collectionId)
                    : _defaultCollRow;
                if (row != null) { row.IsExpanded = !row.IsExpanded; RebuildGridRows(); }
            },
            rename: async () =>
            {
                if (isSynthetic || !collectionId.HasValue || ShowRenameCollectionDialog == null) return;
                var collRow = _allCollections.FirstOrDefault(c => c.CollectionId == collectionId);
                if (collRow == null) return;
                var newName = await ShowRenameCollectionDialog(collRow.CollectionName);
                if (newName == null) return;
                await _svc.RenameCollectionAsync(collectionId.Value, newName);
                collRow.CollectionName = newName;
            },
            delete: async () =>
            {
                if (isSynthetic || !collectionId.HasValue) return;
                var collRow = _allCollections.FirstOrDefault(c => c.CollectionId == collectionId);
                if (collRow == null) return;
                await _svc.DeleteCollectionAsync(collectionId.Value);
                foreach (var g in _allGroups.Where(g => g.CollectionId == collectionId.Value))
                    g.CollectionId = null;
                _allCollections.Remove(collRow);
                if (_allGroups.Any(g => g.CollectionId == null) && _defaultCollRow == null)
                    _defaultCollRow = MakeMarketCollectionRow(null, "Default", isSynthetic: true);
                RebuildGridRows();
                StatusText = $"Collection '{collRow.CollectionName}' deleted.";
            },
            expandAll: () =>
            {
                foreach (var g in GetCollGroups()) g.IsExpanded = true;
                RebuildGridRows();
            },
            collapseAll: () =>
            {
                foreach (var g in GetCollGroups()) g.IsExpanded = false;
                RebuildGridRows();
            });
    }

    private async Task AddMarketCollectionAsync()
    {
        if (ShowAddCollectionDialog == null) return;
        var name = await ShowAddCollectionDialog();
        if (string.IsNullOrWhiteSpace(name)) return;

        var c   = await _svc.AddCollectionAsync(name.Trim());
        var row = MakeMarketCollectionRow(c.Id, c.Name, isSynthetic: false);
        _allCollections.Add(row);
        RebuildGridRows();
        StatusText = $"Collection '{c.Name}' added.";
    }

    // ── Column sort (items within each group; groups keep their order) ────────

    private string? _sortProp;
    private bool    _sortDesc;

    public void SortByProperty(string propName)
    {
        _sortDesc = _sortProp == propName && !_sortDesc;
        _sortProp = propName;

        Func<MarketItemRow, IComparable?>? key = propName switch
        {
            "TypeName"     => r => r.TypeName,
            "TargetQty"    => r => (IComparable?)r.TargetQty,
            "TargetTotal"  => r => (IComparable?)r.TargetTotal,
            "Available"    => r => (IComparable?)r.Available,
            "Diff"         => r => (IComparable?)r.Diff,
            "DiffPct"      => r => (IComparable?)r.DiffPct,
            "MarketPrice"  => r => (IComparable?)r.MarketPrice,
            "StationMin"   => r => (IComparable?)r.StationMin,
            "MinDelta"     => r => (IComparable?)r.MinDelta,
            "MinDeltaPct"  => r => (IComparable?)r.MinDeltaPct,
            "StationAvg"   => r => (IComparable?)r.StationAvg,
            "AvgDelta"     => r => (IComparable?)r.AvgDelta,
            "AvgDeltaPct"  => r => (IComparable?)r.AvgDeltaPct,
            "StationMax"   => r => (IComparable?)r.StationMax,
            "MaxDelta"     => r => (IComparable?)r.MaxDelta,
            "MaxDeltaPct"  => r => (IComparable?)r.MaxDeltaPct,
            "BuildPrice"   => r => (IComparable?)r.BuildPrice,
            "Volume"       => r => (IComparable?)r.Volume,
            _              => null
        };
        if (key == null) return;

        foreach (var group in _allGroups)
        {
            var sorted = (_sortDesc
                ? group.AllItems.OrderByDescending(key)
                : group.AllItems.OrderBy(key)).ToList();
            group.AllItems.Clear();
            foreach (var item in sorted) group.AllItems.Add(item);
        }
        RebuildGridRows();
    }

    // ── Public helpers for view code-behind ───────────────────────────────────

    public BatchAddService? GetBatchAddService() => _batchSvc;

    // ── Type search (used by AddItemDialog) ───────────────────────────────────

    public async Task<List<TypeResultVm>> SearchTypesAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return [];
        using var db = _dbFactory.CreateDbContext();
        var pattern = $"%{text}%";
        var results = await db.SdeTypes
            .Where(t => EF.Functions.Like(t.Name, pattern) && t.MarketGroupId != null && t.Published)
            .OrderBy(t => t.Name)
            .Take(50)
            .Select(t => new { t.TypeId, t.Name })
            .ToListAsync();
        return results.Select(r => new TypeResultVm(r.TypeId, r.Name)).ToList();
    }
}
