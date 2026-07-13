using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

// ── Dialog results ─────────────────────────────────────────────────────────────

public record InvGroupDialogResult(
    string Name,
    string Scope,
    long?  LocationId,
    string LocationName,
    bool   IncludeAssets,
    bool   IncludeIndustryJobs,
    bool   IncludeMarketBuyOrders,
    bool   IncludeContractsBuying,
    int    Multiplier,
    int?   CollectionId = null);

public record CollectionOption(int? CollectionId, string Name)
{
    public override string ToString() => Name;
}

// ── Collection row ────────────────────────────────────────────────────────────

public class InvCollectionRow : ReactiveObject
{
    private static readonly SolidColorBrush RowBrush = new(Color.Parse("#0e0e1a"));
    public IBrush RowBackground => RowBrush;

    public bool IsCollection => true;
    public bool IsGroup      => false;
    public bool IsItem       => false;

    public int?   CollectionId   { get; }
    public bool   IsSynthetic    { get; }  // true for the synthetic "Default" collection

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

    public ReactiveCommand<Unit, Unit> ToggleCommand     { get; }
    public ReactiveCommand<Unit, Unit> RenameCommand     { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand     { get; }
    public ReactiveCommand<Unit, Unit> ExpandAllCommand  { get; }
    public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }

    public InvCollectionRow(int? collectionId, string name, bool isSynthetic,
        Action toggle, Func<Task> rename, Func<Task> delete,
        Action expandAll, Action collapseAll)
    {
        CollectionId     = collectionId;
        _collectionName  = name;
        IsSynthetic      = isSynthetic;
        ToggleCommand    = ReactiveCommand.Create(toggle);
        RenameCommand    = ReactiveCommand.CreateFromTask(rename);
        DeleteCommand    = ReactiveCommand.CreateFromTask(delete);
        ExpandAllCommand = ReactiveCommand.Create(expandAll);
        CollapseAllCommand = ReactiveCommand.Create(collapseAll);
    }
}

// ── Group row ─────────────────────────────────────────────────────────────────

public class InvGroupRow : ReactiveObject
{
    private static readonly SolidColorBrush RowBrush = new(Color.Parse("#141420"));
    public IBrush RowBackground => RowBrush;

    public bool IsCollection => false;
    public bool IsGroup      => true;
    public bool IsItem       => false;

    public int    GroupId      { get; }
    public int?   CollectionId { get; set; }
    public string Scope        { get; private set; } = "Everywhere";
    public long?  LocationId   { get; private set; }
    public string LocationName { get; private set; } = "";
    public bool   IncludeAssets          { get; private set; } = true;
    public bool   IncludeIndustryJobs    { get; private set; }
    public bool   IncludeMarketBuyOrders { get; private set; }
    public bool   IncludeContractsBuying { get; private set; }

    public List<InvItemRow> AllItems { get; } = [];

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

    // Displayed beneath the group name to indicate scope
    public string ScopeDisplay => Scope == "Everywhere"
        ? "Everywhere"
        : $"{LocationName} · {Scope}";

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

    // Include flag summary for display (e.g. "Assets, IJ")
    public string IncludeSummary
    {
        get
        {
            var parts = new List<string>();
            if (IncludeAssets)          parts.Add("Assets");
            if (IncludeIndustryJobs)    parts.Add("IJ");
            if (IncludeMarketBuyOrders) parts.Add("Orders");
            if (IncludeContractsBuying) parts.Add("Contracts");
            return parts.Count > 0 ? string.Join(", ", parts) : "None";
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand    { get; }
    public ReactiveCommand<Unit, Unit> EditCommand      { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddItemCommand   { get; }

    public InvGroupRow(InvLevelGroup g,
        Action toggle, Func<Task> edit, Func<Task> delete, Func<Task> addItem,
        Func<int, Task>? saveMultiplier = null)
    {
        GroupId          = g.Id;
        CollectionId     = g.CollectionId;
        _groupName       = g.Name;
        _multiplier      = Math.Max(1, g.Multiplier);
        _saveMultiplier  = saveMultiplier;
        ApplyGroupData(g);

        ToggleCommand  = ReactiveCommand.Create(toggle);
        EditCommand    = ReactiveCommand.CreateFromTask(edit);
        DeleteCommand  = ReactiveCommand.CreateFromTask(delete);
        AddItemCommand = ReactiveCommand.CreateFromTask(addItem);
    }

    public void ApplyGroupData(InvLevelGroup g)
    {
        Scope                  = g.Scope;
        LocationId             = g.LocationId;
        LocationName           = g.LocationName;
        IncludeAssets          = g.IncludeAssets;
        IncludeIndustryJobs    = g.IncludeIndustryJobs;
        IncludeMarketBuyOrders = g.IncludeMarketBuyOrders;
        IncludeContractsBuying = g.IncludeContractsBuying;
        this.RaisePropertyChanged(nameof(ScopeDisplay));
        this.RaisePropertyChanged(nameof(IncludeSummary));
    }
}

// ── Item row ──────────────────────────────────────────────────────────────────

public class InvItemRow : ReactiveObject
{
    private static readonly SolidColorBrush Green  = new(Color.Parse("#4a9a4a"));
    private static readonly SolidColorBrush Orange = new(Color.Parse("#e0902e"));
    private static readonly SolidColorBrush Red    = new(Color.Parse("#d05a5a"));
    private static readonly SolidColorBrush Gray   = new(Color.Parse("#666677"));

    // Whole-row background tint when the item is under target: orange from 0% down to -50%,
    // red once the shortfall is worse than -50%. Transparent lets the base row colour show.
    private static readonly SolidColorBrush RowClear  = new(Colors.Transparent);
    private static readonly SolidColorBrush RowOrange = new(Color.Parse("#3a2a12"));
    private static readonly SolidColorBrush RowRed    = new(Color.Parse("#3a1616"));

    private readonly InvLevelService _svc;

    public bool IsCollection => false;
    public bool IsGroup      => false;
    public bool IsItem       => true;

    public int    ItemId   { get; }
    public int    GroupId  { get; }
    public int    TypeId   { get; }
    public string TypeName { get; }

    // Static type metadata (set once at load)
    private readonly double  _volume;
    private readonly double? _marketPrice;
    private readonly double? _buildPrice;

    public double  Volume      => _volume;
    public double? MarketPrice => _marketPrice;
    public double? BuildPrice  => _buildPrice;

    public string VolumeText      => _volume > 0      ? _volume.ToString("N2")       : "";
    public string MarketPriceText => _marketPrice > 0  ? _marketPrice.Value.ToString("N2") : "";
    public string BuildPriceText  => _buildPrice > 0   ? _buildPrice.Value.ToString("N2")  : "";

    // Per-source availability (updated on each refresh)
    private long _availAssets;
    private long _availIJ;
    private long _availOrders;

    public long AssetsQty       => _availAssets;
    public long IndustryJobsQty => _availIJ;
    public long BuyOrdersQty    => _availOrders;

    public string AssetsText       => FormatQty(_availAssets);
    public string IndustryJobsText => FormatQty(_availIJ);
    public string BuyOrdersText    => FormatQty(_availOrders);

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

    private int _targetQty = 1;
    public int TargetQty
    {
        get => _targetQty;
        set
        {
            this.RaiseAndSetIfChanged(ref _targetQty, value);
            RaiseDiffDependents();
            _ = _svc.UpdateItemTargetAsync(ItemId, value);
        }
    }

    public long Available => _availAssets + _availIJ + _availOrders;

    // Derived
    public long   TargetTotal => (long)_targetQty * _groupMultiplier;
    public long   Diff        => Available - TargetTotal;
    public double DiffPct     => TargetTotal > 0 ? (double)Diff / TargetTotal * 100.0 : 0.0;

    // Display text
    public string AvailableText   => FormatQty(Available);
    public string TargetTotalText => FormatQty(TargetTotal);
    public string DiffText        => FormatQty(Diff, sign: true);
    public string DiffPctText     => TargetTotal > 0 ? $"{DiffPct:+0.0;-0.0}%" : "—";

    // Green when at/above target; orange for a 0% to -50% shortfall; red when worse than -50%.
    public IBrush DiffColor => Diff >= 0 ? Green : DiffPct >= -50 ? Orange : Red;

    // Whole-row tint mirroring the shortfall severity (transparent when at/above target).
    public IBrush RowBackground => Diff >= 0 ? RowClear : DiffPct >= -50 ? RowOrange : RowRed;

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public InvItemRow(InvLevelItem item, InvTypeMeta meta, InvLevelService svc, Func<Task> delete,
        int groupMultiplier = 1)
    {
        ItemId           = item.Id;
        GroupId          = item.GroupId;
        TypeId           = item.TypeId;
        TypeName         = meta.Name;
        _volume          = meta.Volume;
        _marketPrice     = meta.MarketPrice;
        _buildPrice      = meta.BuildPrice;
        _targetQty       = item.TargetQuantity;
        _groupMultiplier = Math.Max(1, groupMultiplier);
        _svc             = svc;
        DeleteCommand    = ReactiveCommand.CreateFromTask(delete);
    }

    public void UpdateAvailable(InvAvailability avail)
    {
        _availAssets  = avail.Assets;
        _availIJ      = avail.IndustryJobs;
        _availOrders  = avail.BuyOrders;
        RaiseDiffDependents();
    }

    private void RaiseDiffDependents()
    {
        this.RaisePropertyChanged(nameof(Available));
        this.RaisePropertyChanged(nameof(AvailableText));
        this.RaisePropertyChanged(nameof(AssetsQty));
        this.RaisePropertyChanged(nameof(IndustryJobsQty));
        this.RaisePropertyChanged(nameof(BuyOrdersQty));
        this.RaisePropertyChanged(nameof(AssetsText));
        this.RaisePropertyChanged(nameof(IndustryJobsText));
        this.RaisePropertyChanged(nameof(BuyOrdersText));
        this.RaisePropertyChanged(nameof(TargetTotal));
        this.RaisePropertyChanged(nameof(TargetTotalText));
        this.RaisePropertyChanged(nameof(Diff));
        this.RaisePropertyChanged(nameof(DiffPct));
        this.RaisePropertyChanged(nameof(DiffText));
        this.RaisePropertyChanged(nameof(DiffPctText));
        this.RaisePropertyChanged(nameof(DiffColor));
        this.RaisePropertyChanged(nameof(RowBackground));
    }

    private static string FormatQty(long v, bool sign = false)
    {
        long abs = Math.Abs(v);
        string prefix = sign ? (v >= 0 ? "+" : "") : "";
        if (abs >= 1_000_000) return $"{prefix}{v / 1_000_000.0:N2}M";
        if (abs >= 1_000)     return $"{prefix}{v / 1_000.0:N1}K";
        return $"{prefix}{v:N0}";
    }
}

// ── Main ViewModel ─────────────────────────────────────────────────────────────

public class InvLevelViewModel : ReactiveObject
{
    private readonly InvLevelService              _svc;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly BatchAddService?             _batchSvc;
    private readonly ProductionCalculatorService? _prodCalc;
    private readonly FittingsService?             _fittings;
    private readonly ObservableCollection<Character>?   _characters;
    private readonly ObservableCollection<Corporation>? _corporations;

    private readonly List<InvGroupRow>       _allGroups       = [];
    private readonly List<InvCollectionRow>  _allCollections  = [];
    private          InvCollectionRow?       _defaultCollRow;

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
    public bool IsItemRowSelected => _selectedRow is InvItemRow;

    private string _statusText = "Loading…";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> AddGroupCommand              { get; }
    public ReactiveCommand<Unit, Unit> AddCollectionCommand         { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedItemCommand    { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand               { get; }
    public ReactiveCommand<Unit, Unit> AddFromFitCommand            { get; }
    public ReactiveCommand<Unit, Unit> AddFromMarketGroupCommand    { get; }
    public ReactiveCommand<Unit, Unit> AddFromBlueprintCommand      { get; }
    public ReactiveCommand<Unit, Unit> OpenInItemBrowserCommand     { get; }

    // ── Dialog delegates ──────────────────────────────────────────────────────
    public Func<IReadOnlyList<CollectionOption>, Task<InvGroupDialogResult?>>?              ShowAddGroupDialog        { get; set; }
    public Func<InvGroupRow, IReadOnlyList<CollectionOption>, Task<InvGroupDialogResult?>>? ShowEditGroupDialog       { get; set; }
    public Func<Task<AddItemDialogResult?>>?                                                ShowAddItemDialog          { get; set; }
    public Func<Task<FitSelectorResult?>>?                                                  ShowFitSelectorDialog      { get; set; }
    public Func<Task<MarketGroupPickerResult?>>?                                            ShowMarketGroupPickerDialog { get; set; }
    public Func<Task<BlueprintPickerResult?>>?                                              ShowBlueprintPickerDialog  { get; set; }
    public Func<Task<string?>>?                                                             ShowAddCollectionDialog    { get; set; }
    public Func<string, Task<string?>>?                                                     ShowRenameCollectionDialog { get; set; }
    public Action<int, string>?                                                             OpenInItemBrowser          { get; set; }

    public InvLevelViewModel(InvLevelService svc,
        IDbContextFactory<AppDbContext>   dbFactory,
        BatchAddService?             batchSvc      = null,
        ProductionCalculatorService? prodCalc      = null,
        FittingsService?             fittings      = null,
        ObservableCollection<Character>?   characters   = null,
        ObservableCollection<Corporation>? corporations = null)
    {
        _svc          = svc;
        _dbFactory    = dbFactory;
        _batchSvc     = batchSvc;
        _prodCalc     = prodCalc;
        _fittings     = fittings;
        _characters   = characters;
        _corporations = corporations;

        var hasGroups = this.WhenAnyValue(x => x.HasAnyGroup);
        AddGroupCommand              = ReactiveCommand.CreateFromTask(AddGroupAsync);
        AddCollectionCommand         = ReactiveCommand.CreateFromTask(AddCollectionAsync);
        DeleteSelectedItemCommand    = ReactiveCommand.CreateFromTask(DeleteSelectedItemAsync,
            this.WhenAnyValue(x => x.IsItemRowSelected));
        RefreshCommand               = ReactiveCommand.CreateFromTask(RefreshAllAsync);
        AddFromFitCommand            = ReactiveCommand.CreateFromTask(AddFromFitInvokeAsync, hasGroups);
        AddFromMarketGroupCommand    = ReactiveCommand.CreateFromTask(AddFromMarketGroupAsync, hasGroups);
        AddFromBlueprintCommand      = ReactiveCommand.CreateFromTask(AddFromBlueprintAsync, hasGroups);
        OpenInItemBrowserCommand     = ReactiveCommand.Create(OpenSelectedInItemBrowser,
            this.WhenAnyValue(x => x.IsItemRowSelected));

        Observable.Interval(TimeSpan.FromMinutes(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(async _ => await RefreshAllAsync());

        _ = InitAsync();
    }

    private bool _hasAnyGroup;
    public bool HasAnyGroup
    {
        get => _hasAnyGroup;
        private set => this.RaiseAndSetIfChanged(ref _hasAnyGroup, value);
    }

    // ── Public helpers for view code-behind ───────────────────────────────────

    public BatchAddService? GetBatchAddService() => _batchSvc;

    public Task<IReadOnlyList<InvTypeResult>> SearchTypesAsync(string text) =>
        _svc.SearchTypesAsync(text);

    public Task<IReadOnlyList<LocationOption>> SearchLocationsAsync(string scope, string text) =>
        _svc.SearchLocationsAsync(scope, text);

    // ── Initialization ────────────────────────────────────────────────────────

    private async Task InitAsync()
    {
        await LoadGroupsAsync();
        await RefreshAllAsync();
    }

    private async Task LoadGroupsAsync()
    {
        var collections = await _svc.LoadCollectionsAsync();
        var groups      = await _svc.LoadGroupsAsync();

        _allCollections.Clear();
        _allGroups.Clear();
        _defaultCollRow = null;

        foreach (var c in collections)
            _allCollections.Add(MakeCollectionRow(c.Id, c.Name, isSynthetic: false));

        foreach (var g in groups)
        {
            var row = MakeGroupRow(g);
            var items = await _svc.LoadItemsAsync(g.Id);
            var typeIds = items.Select(i => i.TypeId).ToList();
            var meta    = await _svc.GetTypeMetaAsync(typeIds);
            foreach (var item in items)
            {
                var m = meta.GetValueOrDefault(item.TypeId,
                    new InvTypeMeta(item.TypeId.ToString(), 0, null, null));
                var itemRow = new InvItemRow(item, m, _svc, () => DeleteItemAsync(item.Id), g.Multiplier);
                row.AllItems.Add(itemRow);
            }
            SortItemsAlpha(row);
            _allGroups.Add(row);
        }

        // Create the synthetic Default collection if any group has null CollectionId
        if (_allGroups.Any(g => g.CollectionId == null))
            _defaultCollRow = MakeCollectionRow(null, "Default", isSynthetic: true);

        RebuildGridRows();
        HasAnyGroup = _allGroups.Count > 0;
        StatusText = $"{_allGroups.Count} group(s) loaded. Hit Refresh to load availability.";
    }

    // ── Refresh (load available quantities from DB) ───────────────────────────

    private async Task RefreshAllAsync()
    {
        StatusText = "Loading availability data…";
        int updated = 0;
        foreach (var groupRow in _allGroups)
        {
            await RefreshGroupAsync(groupRow);
            updated += groupRow.AllItems.Count;
        }
        StatusText = $"Updated {updated} item(s) at {DateTime.Now:HH:mm:ss}.";
    }

    private async Task RefreshGroupAsync(InvGroupRow groupRow)
    {
        if (groupRow.AllItems.Count == 0) return;

        var group = new InvLevelGroup
        {
            Id                     = groupRow.GroupId,
            Scope                  = groupRow.Scope,
            LocationId             = groupRow.LocationId,
            IncludeAssets          = groupRow.IncludeAssets,
            IncludeIndustryJobs    = groupRow.IncludeIndustryJobs,
            IncludeMarketBuyOrders = groupRow.IncludeMarketBuyOrders,
            IncludeContractsBuying = groupRow.IncludeContractsBuying,
        };
        var typeIds = groupRow.AllItems.Select(r => r.TypeId).ToList();
        var avail   = await _svc.LoadAvailableAsync(group, typeIds);

        foreach (var itemRow in groupRow.AllItems)
        {
            var a = avail.GetValueOrDefault(itemRow.TypeId, new InvAvailability(0, 0, 0));
            itemRow.UpdateAvailable(a);
        }
    }

    // ── Fit selector helpers ──────────────────────────────────────────────────

    public FitSelectorViewModel? CreateFitSelectorViewModel()
    {
        if (_fittings == null || _characters == null || _corporations == null) return null;
        var groupOptions = _allGroups.Select(g => new FitGroupOption(g.GroupId, g.GroupName)).ToList();
        var preselectedId = _selectedRow switch
        {
            InvGroupRow g => g.GroupId,
            InvItemRow  i => i.GroupId,
            _             => groupOptions.Count > 0 ? groupOptions[0].GroupId : 0
        };
        return new FitSelectorViewModel(_fittings!, _dbFactory, _characters!, _corporations!, groupOptions, preselectedId);
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

        var fitting  = result.Fitting;
        var items    = new Dictionary<int, int> { [fitting.ShipTypeId] = 1 };
        var skipFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Invalid", "Implant", "BoosterBay" };
        foreach (var item in fitting.Items)
        {
            if (skipFlags.Contains(item.Flag)) continue;
            items.TryGetValue(item.TypeId, out var q);
            items[item.TypeId] = q + item.Quantity;
        }

        await AddItemsToGroupAsync(groupRow, items, fitting.Name);
    }

    // ── Market group add ──────────────────────────────────────────────────────

    private async Task AddFromMarketGroupAsync()
    {
        if (ShowMarketGroupPickerDialog == null || _batchSvc == null) return;
        var pick = await ShowMarketGroupPickerDialog();
        if (pick == null) return;

        StatusText = $"Loading items in '{pick.GroupName}'…";
        var items = await _batchSvc.GetItemsInGroupTreeAsync(pick.MarketGroupId);

        if (items.Count == 0)
        {
            StatusText = $"No published items found under '{pick.GroupName}'.";
            return;
        }

        if (items.Count > 100)
        {
            var confirmed = await ShowConfirmLargeGroupAsync(pick.GroupName, items.Count);
            if (!confirmed) { StatusText = "Cancelled."; return; }
        }

        var targetGroup = GetContextGroup();
        if (targetGroup == null) { StatusText = "No group selected."; return; }

        await AddItemsToGroupAsync(targetGroup,
            items.ToDictionary(x => x.TypeId, _ => pick.TargetQty),
            pick.GroupName);
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
        await AddItemsToGroupAsync(targetGroup, itemsWithQty, pick.ProductName, nameOverrides);
    }

    // ── Shared batch-add helper ───────────────────────────────────────────────

    private async Task AddItemsToGroupAsync(
        InvGroupRow groupRow,
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

        // Fetch type metadata
        var nameOverridesMeta = nameOverrides ?? [];
        var typeIdsToFetch = candidates.Select(kv => kv.Key).Where(id => !nameOverridesMeta.ContainsKey(id)).ToList();
        var fetchedMeta = await _svc.GetTypeMetaAsync(typeIdsToFetch);

        int added = 0;
        int dupeInDb = 0;
        foreach (var (typeId, qty) in candidates)
        {
            var item = await _svc.AddItemAsync(groupRow.GroupId, typeId);
            if (item is null) { dupeInDb++; continue; }

            int target = Math.Max(1, qty);
            if (target != 1) await _svc.UpdateItemTargetAsync(item.Id, target);
            item.TargetQuantity = target;

            InvTypeMeta meta;
            if (fetchedMeta.TryGetValue(typeId, out var fm))
                meta = nameOverridesMeta.TryGetValue(typeId, out var nameOverride)
                    ? fm with { Name = nameOverride } : fm;
            else
                meta = new InvTypeMeta(nameOverridesMeta.GetValueOrDefault(typeId, $"Type {typeId}"), 0, null, null);

            var itemRow = new InvItemRow(item, meta, _svc,
                () => DeleteItemAsync(item.Id), groupRow.Multiplier);
            groupRow.AllItems.Add(itemRow);
            added++;
        }

        SortItemsAlpha(groupRow);
        if (groupRow.IsExpanded) RebuildGridRows();
        await RefreshGroupAsync(groupRow);

        int totalSkipped = alreadyIn + dupeInDb;
        StatusText = totalSkipped > 0
            ? $"Added {added} item(s) from '{label}'; {totalSkipped} already present, skipped."
            : $"Added {added} item(s) from '{label}'.";
    }

    private async Task<bool> ShowConfirmLargeGroupAsync(string groupName, int count)
    {
        // Delegate to the view — the view wires this as a Func
        if (ShowConfirmLargeGroup != null)
            return await ShowConfirmLargeGroup(groupName, count);
        return true;
    }

    public Func<string, int, Task<bool>>? ShowConfirmLargeGroup { get; set; }

    private InvGroupRow? GetContextGroup()
    {
        return _selectedRow switch
        {
            InvGroupRow      g => g,
            InvItemRow       i => _allGroups.FirstOrDefault(g => g.GroupId == i.GroupId),
            InvCollectionRow c => _allGroups.FirstOrDefault(g => g.CollectionId == c.CollectionId),
            _                  => _allGroups.Count > 0 ? _allGroups[0] : null
        };
    }

    // ── Group CRUD ────────────────────────────────────────────────────────────

    private IReadOnlyList<CollectionOption> GetCollectionOptions()
    {
        var opts = new List<CollectionOption> { new(null, "— Default —") };
        opts.AddRange(_allCollections.Select(c => new CollectionOption(c.CollectionId, c.CollectionName)));
        return opts;
    }

    private async Task AddGroupAsync()
    {
        if (ShowAddGroupDialog is null) return;
        var result = await ShowAddGroupDialog(GetCollectionOptions());
        if (result is null) return;

        var g   = await _svc.AddGroupAsync(result);
        var row = MakeGroupRow(g);
        _allGroups.Add(row);
        HasAnyGroup = true;

        // Ensure the synthetic Default row exists if the group has no collection
        if (g.CollectionId == null && _defaultCollRow == null)
            _defaultCollRow = MakeCollectionRow(null, "Default", isSynthetic: true);

        RebuildGridRows();
        StatusText = $"Group '{g.Name}' added.";
    }

    private async Task EditGroupAsync(InvGroupRow row)
    {
        if (ShowEditGroupDialog is null) return;
        var result = await ShowEditGroupDialog(row, GetCollectionOptions());
        if (result is null) return;

        await _svc.UpdateGroupAsync(row.GroupId, result);
        row.GroupName    = result.Name;
        row.CollectionId = result.CollectionId;
        // Apply scope/location/includes to the row BEFORE touching Multiplier: the
        // Multiplier setter re-saves the whole group from the row's current state, so if
        // the row still held the old scope it would clobber the just-saved new scope in
        // the DB (the bug where scope changes reverted after restart).
        row.ApplyGroupData(new InvLevelGroup
        {
            Scope                  = result.Scope,
            LocationId             = result.LocationId,
            LocationName           = result.LocationName,
            IncludeAssets          = result.IncludeAssets,
            IncludeIndustryJobs    = result.IncludeIndustryJobs,
            IncludeMarketBuyOrders = result.IncludeMarketBuyOrders,
            IncludeContractsBuying = result.IncludeContractsBuying,
        });
        row.Multiplier   = result.Multiplier;

        // Ensure/remove synthetic Default row based on whether any group is uncollected
        if (_allGroups.Any(g => g.CollectionId == null) && _defaultCollRow == null)
            _defaultCollRow = MakeCollectionRow(null, "Default", isSynthetic: true);
        else if (!_allGroups.Any(g => g.CollectionId == null))
            _defaultCollRow = null;

        RebuildGridRows();
        await RefreshGroupAsync(row);
    }

    private async Task DeleteGroupAsync(InvGroupRow row)
    {
        await _svc.DeleteGroupAsync(row.GroupId);
        _allGroups.Remove(row);
        RebuildGridRows();
        StatusText = $"Group '{row.GroupName}' deleted.";
    }

    // ── Item CRUD ─────────────────────────────────────────────────────────────

    private async Task AddItemToGroupAsync(InvGroupRow groupRow)
    {
        if (ShowAddItemDialog is null) return;
        var result = await ShowAddItemDialog();
        if (result is null) return;

        var item = await _svc.AddItemAsync(groupRow.GroupId, result.TypeId);
        if (item is null)
        {
            StatusText = $"{result.TypeName} is already in the group.";
            return;
        }

        var meta = (await _svc.GetTypeMetaAsync([result.TypeId]))
            .GetValueOrDefault(result.TypeId, new InvTypeMeta(result.TypeName, 0, null, null));
        var row = new InvItemRow(item, meta, _svc,
            () => DeleteItemAsync(item.Id), groupRow.Multiplier);
        groupRow.AllItems.Add(row);
        SortItemsAlpha(groupRow);

        if (groupRow.IsExpanded) RebuildGridRows();

        await RefreshGroupAsync(groupRow);
    }

    private async Task DeleteItemAsync(int itemId)
    {
        await _svc.DeleteItemAsync(itemId);
        foreach (var g in _allGroups)
        {
            var r = g.AllItems.FirstOrDefault(i => i.ItemId == itemId);
            if (r is null) continue;
            g.AllItems.Remove(r);
            GridRows.Remove(r);
            break;
        }
    }

    private async Task DeleteSelectedItemAsync()
    {
        if (_selectedRow is InvItemRow item)
            await DeleteItemAsync(item.ItemId);
    }

    private void OpenSelectedInItemBrowser()
    {
        if (_selectedRow is InvItemRow item)
            OpenInItemBrowser?.Invoke(item.TypeId, item.TypeName);
    }

    // ── Grid helpers ──────────────────────────────────────────────────────────

    private InvGroupRow MakeGroupRow(InvLevelGroup g)
    {
        return new InvGroupRow(g,
            toggle:          () => ToggleGroup(g.Id),
            edit:            () => EditGroupAsync(GetGroupRow(g.Id)!),
            delete:          () => DeleteGroupAsync(GetGroupRow(g.Id)!),
            addItem:         () => AddItemToGroupAsync(GetGroupRow(g.Id)!),
            saveMultiplier:  v  => _svc.UpdateGroupAsync(g.Id,
                BuildResultFromRow(GetGroupRow(g.Id)!, v)));
    }

    private InvGroupRow? GetGroupRow(int id) => _allGroups.FirstOrDefault(r => r.GroupId == id);

    private void ToggleGroup(int groupId)
    {
        var row = GetGroupRow(groupId);
        if (row is null) return;
        row.IsExpanded = !row.IsExpanded;
        RebuildGridRows();
    }

    // ── Column sort ───────────────────────────────────────────────────────────

    private string? _sortProp;
    private bool    _sortDesc;

    public void SortByProperty(string propName)
    {
        _sortDesc = _sortProp == propName && !_sortDesc;
        _sortProp = propName;

        Func<InvItemRow, IComparable?>? key = propName switch
        {
            "TypeName"       => r => r.TypeName,
            "TargetQty"      => r => (IComparable?)r.TargetQty,
            "TargetTotal"    => r => (IComparable?)r.TargetTotal,
            "Available"      => r => (IComparable?)r.Available,
            "Diff"           => r => (IComparable?)r.Diff,
            "DiffPct"        => r => (IComparable?)r.DiffPct,
            "Volume"         => r => (IComparable?)r.Volume,
            "MarketPrice"    => r => (IComparable?)r.MarketPrice,
            "BuildPrice"     => r => (IComparable?)r.BuildPrice,
            "AssetsQty"      => r => (IComparable?)r.AssetsQty,
            "IndustryJobs"   => r => (IComparable?)r.IndustryJobsQty,
            "BuyOrders"      => r => (IComparable?)r.BuyOrdersQty,
            _                => null
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

    private static void SortItemsAlpha(InvGroupRow group)
    {
        var sorted = group.AllItems.OrderBy(i => i.TypeName, StringComparer.OrdinalIgnoreCase).ToList();
        group.AllItems.Clear();
        group.AllItems.AddRange(sorted);
    }

    private void RebuildGridRows()
    {
        GridRows.Clear();

        void AddGroupWithItems(InvGroupRow g)
        {
            GridRows.Add(g);
            if (g.IsExpanded)
                foreach (var item in g.AllItems)
                    GridRows.Add(item);
        }

        // Real collections
        foreach (var col in _allCollections)
        {
            GridRows.Add(col);
            if (col.IsExpanded)
                foreach (var g in _allGroups.Where(g => g.CollectionId == col.CollectionId))
                    AddGroupWithItems(g);
        }

        // Synthetic "Default" for ungrouped groups
        if (_defaultCollRow != null)
        {
            GridRows.Add(_defaultCollRow);
            if (_defaultCollRow.IsExpanded)
                foreach (var g in _allGroups.Where(g => g.CollectionId == null))
                    AddGroupWithItems(g);
        }
    }

    private static InvGroupDialogResult BuildResultFromRow(InvGroupRow row, int? multiplierOverride = null) =>
        new(
            row.GroupName,
            row.Scope,
            row.LocationId,
            row.LocationName,
            row.IncludeAssets,
            row.IncludeIndustryJobs,
            row.IncludeMarketBuyOrders,
            row.IncludeContractsBuying,
            multiplierOverride ?? row.Multiplier,
            row.CollectionId);

    private InvCollectionRow MakeCollectionRow(int? collectionId, string name, bool isSynthetic)
    {
        IEnumerable<InvGroupRow> GetCollGroups() => collectionId.HasValue
            ? _allGroups.Where(g => g.CollectionId == collectionId)
            : _allGroups.Where(g => g.CollectionId == null);

        return new InvCollectionRow(collectionId, name, isSynthetic,
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
                    _defaultCollRow = MakeCollectionRow(null, "Default", isSynthetic: true);
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

    // ── Collection CRUD ───────────────────────────────────────────────────────

    private async Task AddCollectionAsync()
    {
        if (ShowAddCollectionDialog == null) return;
        var name = await ShowAddCollectionDialog();
        if (string.IsNullOrWhiteSpace(name)) return;

        var c    = await _svc.AddCollectionAsync(name.Trim());
        var row  = MakeCollectionRow(c.Id, c.Name, isSynthetic: false);
        _allCollections.Add(row);
        RebuildGridRows();
        StatusText = $"Collection '{c.Name}' added.";
    }
}
