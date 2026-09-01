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
    public ReactiveCommand<Unit, Unit> ExportCommand     { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand     { get; }
    public ReactiveCommand<Unit, Unit> ExpandAllCommand  { get; }
    public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }

    public InvCollectionRow(int? collectionId, string name, bool isSynthetic,
        Action toggle, Func<Task> rename, Func<Task> export, Func<Task> delete,
        Action expandAll, Action collapseAll)
    {
        CollectionId     = collectionId;
        _collectionName  = name;
        IsSynthetic      = isSynthetic;
        ToggleCommand    = ReactiveCommand.Create(toggle);
        RenameCommand    = ReactiveCommand.CreateFromTask(rename);
        ExportCommand    = ReactiveCommand.CreateFromTask(export);
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

    // ── The scope's location, as a link ───────────────────────────────────────
    //
    // ScopeDisplay reads "Jita IV - Moon 4 · Station". The name half points somewhere, the scope
    // word does not, so the view renders the two separately and only the name links. An
    // "Everywhere" group has no location at all and shows neither.
    public string ScopeSuffix   => Scope == "Everywhere" ? "" : $" · {Scope}";
    public bool   HasLocationLink => LocationId is > 0 && LocationName.Length > 0
                                  && Scope != "Everywhere";

    /// <summary>
    /// Where each scope's id lives.
    ///
    /// <para>⚠️ A "Station" scope holds either an NPC station or a player structure — one column
    /// for both — so it splits on int range. SdeStations keys on an int, which a structure id
    /// cannot fit, so anything above that range is definitively a structure.</para>
    /// </summary>
    public void OpenLocation()
    {
        var id = LocationId ?? 0;
        if (id <= 0) return;

        switch (Scope)
        {
            case "Region":  EntityNavigator.Instance.Region((int)id); break;
            case "System":  EntityNavigator.Instance.System((int)id); break;
            case "Station" when id <= int.MaxValue:
                EntityNavigator.Instance.Entity(EntityKind.Station, id); break;
            case "Station":
                EntityNavigator.Instance.Structure(id); break;
        }
    }

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
        this.RaisePropertyChanged(nameof(ScopeSuffix));
        this.RaisePropertyChanged(nameof(LocationName));
        this.RaisePropertyChanged(nameof(HasLocationLink));
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

    /// <summary>Alternating shade for a row carrying no warning. A small step from the grid's own
    /// #0d0d12, matching the shared banding elsewhere in the app.</summary>
    private static readonly SolidColorBrush RowBand = new(Color.Parse("#111118"));

    private readonly InvLevelService _svc;

    public bool IsCollection => false;
    public bool IsGroup      => false;
    public bool IsItem       => true;

    public int    ItemId   { get; }
    public int    GroupId  { get; }
    public int    TypeId   { get; }
    public string TypeName { get; }

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

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

    /// <summary>
    /// Set by the view as rows are laid out, so a healthy row can be banded.
    ///
    /// <para>⚠️ Position, so it has to be reassigned after a sort — the row that was third is not
    /// third any more. <see cref="InvLevelViewModel.ApplyRowBanding"/> owns that.</para>
    /// </summary>
    private bool _isAltRow;
    public bool IsAltRow
    {
        get => _isAltRow;
        set { this.RaiseAndSetIfChanged(ref _isAltRow, value); this.RaisePropertyChanged(nameof(RowBackground)); }
    }

    /// <summary>
    /// Whole-row tint mirroring the shortfall severity, falling back to alternating shading.
    ///
    /// <para>⚠️ Both live here because both want the same pixel and only this object knows which
    /// should win. The shared alternating-row style paints the row template, which sits on top of
    /// this — so it was silently erasing the amber and red on every other row, and the grid opts
    /// out of it with Classes="tinted". Meaning beats position: a row that is short of target
    /// shows that, and only a healthy row is banded.</para>
    /// </summary>
    public IBrush RowBackground =>
        Diff <  0 && DiffPct <  -50 ? RowRed
      : Diff <  0                   ? RowOrange
      : IsAltRow                    ? RowBand
                                    : RowClear;

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

public class InvLevelViewModel : ReactiveObject, IPeriodicRefresh
{
    /// <summary>Set the first time this tool is opened; until then its refresh timer is a
    /// no-op. See IPeriodicRefresh.</summary>
    public bool AutoRefreshEnabled { get; set; }

    private readonly InvLevelService              _svc;
    private readonly InvLevelCollectionTransfer   _transfer;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly BatchAddService?             _batchSvc;
    private readonly ProductionCalculatorService? _prodCalc;
    private readonly FittingsService?             _fittings;
    private readonly ObservableCollection<Character>?   _characters;
    private readonly ObservableCollection<Corporation>? _corporations;
    private readonly AppPreferencesService              _prefs;

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

    // File pickers belong to the view; the view model says which collection and what to call it.
    public Func<int, string, Task>? ExportCollection { get; set; }
    public Func<Task>?              ImportCollection { get; set; }

    public ReactiveCommand<Unit, Unit> ImportCollectionCommand { get; private set; } = null!;

    /// <summary>
    /// Writes a collection out. Called by the view once it has a stream from the save dialog.
    /// </summary>
    public Task ExportCollectionAsync(int collectionId, Stream output) =>
        _transfer.ExportAsync(collectionId, output);

    /// <summary>
    /// Reads a collection in, always as a new one, and reloads so it appears without a refresh.
    /// </summary>
    /// <remarks>
    /// Unlike every other mutation here, import writes its rows straight to the database rather
    /// than through the in-memory lists, so it is the one path that has to re-read them.
    /// <see cref="RefreshAllAsync"/> only updates availability on groups already loaded — it was
    /// what this called, and the imported collection stayed invisible until the next restart.
    /// </remarks>
    public async Task ImportCollectionAsync(Stream input)
    {
        try
        {
            var r = await _transfer.ImportAsync(input);
            await LoadGroupsAsync();
            await RefreshAllAsync();
            StatusText = $"Imported '{r.CollectionName}' — {r.Groups} group(s), {r.Items} item(s)"
                       + (r.UnknownTypes > 0
                            ? $". {r.UnknownTypes} item(s) skipped: this install's SDE does not have them."
                            : ".");
            await NotifyGroupsChangedAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
        }
    }
    public Func<Task<FitSelectorResult?>>?                                                  ShowFitSelectorDialog      { get; set; }
    public Func<Task<MarketGroupPickerResult?>>?                                            ShowMarketGroupPickerDialog { get; set; }
    public Func<Task<BlueprintPickerResult?>>?                                              ShowBlueprintPickerDialog  { get; set; }
    public Func<Task<string?>>?                                                             ShowAddCollectionDialog    { get; set; }
    public Func<string, Task<string?>>?                                                     ShowRenameCollectionDialog { get; set; }
    public Action<int, string>?                                                             OpenInItemBrowser          { get; set; }

    /// <param name="prefs">Required, and deliberately not optional like the rest. It was added as
    /// a trailing optional and wired to a property that is assigned later in the caller's
    /// constructor, so it silently arrived null and the saved expansion state never loaded or
    /// saved. Sitting among the required parameters, that cannot happen again.</param>
    public InvLevelViewModel(InvLevelService svc,
        IDbContextFactory<AppDbContext>   dbFactory,
        AppPreferencesService              prefs,
        BatchAddService?             batchSvc      = null,
        ProductionCalculatorService? prodCalc      = null,
        FittingsService?             fittings      = null,
        ObservableCollection<Character>?   characters   = null,
        ObservableCollection<Corporation>? corporations = null)
    {
        _svc          = svc;
        _dbFactory    = dbFactory;
        _prefs        = prefs;
        _transfer     = new InvLevelCollectionTransfer(dbFactory);
        _batchSvc     = batchSvc;
        _prodCalc     = prodCalc;
        _fittings     = fittings;
        _characters   = characters;
        _corporations = corporations;

        var hasGroups = this.WhenAnyValue(x => x.HasAnyGroup);
        AddGroupCommand              = ReactiveCommand.CreateFromTask(AddGroupAsync);
        AddCollectionCommand         = ReactiveCommand.CreateFromTask(AddCollectionAsync);
        ImportCollectionCommand      = ReactiveCommand.CreateFromTask(
            async () => { if (ImportCollection is not null) await ImportCollection(); });
        DeleteSelectedItemCommand    = ReactiveCommand.CreateFromTask(DeleteSelectedItemAsync,
            this.WhenAnyValue(x => x.IsItemRowSelected));
        RefreshCommand               = ReactiveCommand.CreateFromTask(RefreshAllAsync);
        AddFromFitCommand            = ReactiveCommand.CreateFromTask(AddFromFitInvokeAsync, hasGroups);
        AddFromMarketGroupCommand    = ReactiveCommand.CreateFromTask(AddFromMarketGroupAsync, hasGroups);
        AddFromBlueprintCommand      = ReactiveCommand.CreateFromTask(AddFromBlueprintAsync, hasGroups);
        OpenInItemBrowserCommand     = ReactiveCommand.Create(OpenSelectedInItemBrowser,
            this.WhenAnyValue(x => x.IsItemRowSelected));

        // ⚠️ Gated and labelled. Off until the tool is opened, because every view model is
        // built at launch; labelled because the error log otherwise cannot tell one periodic
        // refresh from another once it is on the UI thread.
        Observable.Interval(TimeSpan.FromMinutes(1))
            .Where(_ => AutoRefreshEnabled)
            .ObserveOnUi("InvLevel.AutoRefresh")
            .SubscribeAsyncSafe(_ => RefreshAllAsync(), null, "InvLevel.AutoRefresh");

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

        // Before the first rebuild, so the list is drawn folded as it was left rather than
        // opening everything and snapping shut a frame later.
        ApplyStoredExpansion();

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
        // ⚠️ Task.Run, not a bare await: SQLite has no real async I/O, so awaiting the service
        // directly runs the whole query on whatever thread called — and this is called from a
        // main-thread timer, which is how a background refresh froze the window for seconds.
        var avail   = await Task.Run(() => _svc.LoadAvailableAsync(group, typeIds));

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
        Dictionary<int, (long Qty, string Name)> mats;
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

        // ⚠️ Chain quantities are long; an inventory level is an int column in the rules
        // table. Clamped rather than cast, so an absurd plan produces a capped level instead
        // of a negative one.
        var itemsWithQty  = mats.ToDictionary(kv => kv.Key,
                                              kv => (int)Math.Clamp(kv.Value.Qty, 0, int.MaxValue));
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
        await NotifyGroupsChangedAsync();
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
        await NotifyGroupsChangedAsync();
    }

    private async Task DeleteGroupAsync(InvGroupRow row)
    {
        await _svc.DeleteGroupAsync(row.GroupId);
        _allGroups.Remove(row);
        RebuildGridRows();
        StatusText = $"Group '{row.GroupName}' deleted.";
        await NotifyGroupsChangedAsync();
    }

    /// <summary>
    /// Tells anything that offers these groups for selection that the list has moved on.
    ///
    /// <para>The Worklist's rule and station-level tabs load their group dropdown once. Without
    /// this, a group added here is missing from them until the app restarts, and one renamed here
    /// still shows its old name — so a rule appears to point at a group that no longer exists.</para>
    /// </summary>
    public Func<Task>? GroupsChanged { get; set; }

    private Task NotifyGroupsChangedAsync() =>
        GroupsChanged is null ? Task.CompletedTask : GroupsChanged();

    // ── Item CRUD ─────────────────────────────────────────────────────────────

    private async Task AddItemToGroupAsync(InvGroupRow groupRow)
    {
        if (ShowAddItemDialog is null) return;
        var result = await ShowAddItemDialog();
        if (result is null) return;

        var item = await _svc.AddItemAsync(groupRow.GroupId, result.TypeId, result.TargetQty);
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

    /// <summary>
    /// Re-stripes the item rows in whatever order they are currently in.
    ///
    /// <para>Counted over item rows only. Collection and group headers carry their own colours and
    /// are what a reader uses to keep their place, so including them in the count would put two
    /// banded item rows side by side across a header and undo the point of the stripe.</para>
    ///
    /// <para>⚠️ Must be called again after sorting. Banding is positional, and the row that was
    /// second is not second once the grid is re-ordered — the view's sort handler calls this.</para>
    /// </summary>
    public void ApplyRowBanding()
    {
        var n = 0;
        foreach (var row in GridRows)
            if (row is InvItemRow item)
                item.IsAltRow = n++ % 2 == 1;
    }

    private void RebuildGridRows()
    {
        var desired = new List<object>();

        void AddGroupWithItems(InvGroupRow g)
        {
            desired.Add(g);
            if (g.IsExpanded)
                foreach (var item in g.AllItems)
                    desired.Add(item);
        }

        // Real collections
        foreach (var col in _allCollections)
        {
            desired.Add(col);
            if (col.IsExpanded)
                foreach (var g in _allGroups.Where(g => g.CollectionId == col.CollectionId))
                    AddGroupWithItems(g);
        }

        // Synthetic "Default" for ungrouped groups
        if (_defaultCollRow != null)
        {
            desired.Add(_defaultCollRow);
            if (_defaultCollRow.IsExpanded)
                foreach (var g in _allGroups.Where(g => g.CollectionId == null))
                    AddGroupWithItems(g);
        }

        SyncGridRows(desired);
        ApplyRowBanding();
        PersistExpansion();
    }

    /// <summary>
    /// Brings <see cref="GridRows"/> to <paramref name="desired"/> with the fewest possible
    /// changes.
    ///
    /// <para>Clearing and refilling would be simpler, but a collection reset sends the DataGrid's
    /// scroll position back to the top — so adding one item to a group near the bottom of a long
    /// list threw the reader back to the first row every time. Individual inserts and removes
    /// leave the viewport where it was.</para>
    /// </summary>
    private void SyncGridRows(List<object> desired)
    {
        var wanted = new HashSet<object>(desired, ReferenceEqualityComparer.Instance);
        for (var i = GridRows.Count - 1; i >= 0; i--)
            if (!wanted.Contains(GridRows[i]))
                GridRows.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < GridRows.Count && ReferenceEquals(GridRows[i], desired[i])) continue;

            var at = IndexOfRow(desired[i], i);
            if (at >= 0) GridRows.Move(at, i);
            else         GridRows.Insert(i, desired[i]);
        }

        while (GridRows.Count > desired.Count)
            GridRows.RemoveAt(GridRows.Count - 1);
    }

    /// <summary>
    /// Reference-identity search, because rows are view models without value equality and
    /// <see cref="ObservableCollection{T}.IndexOf"/> would fall back to <c>Equals</c>.
    /// </summary>
    private int IndexOfRow(object row, int from)
    {
        for (var i = from; i < GridRows.Count; i++)
            if (ReferenceEquals(GridRows[i], row)) return i;
        return -1;
    }

    // ── Expansion state ───────────────────────────────────────────────────────
    //
    // Which groups and collections are folded shut is a view preference, not data, so it lives in
    // AppPreferences rather than the group tables. Collapsed ids are stored rather than expanded
    // ones so that anything newly created — by this app or an import — starts open.

    private const string CollapsedGroupsKey      = "invlevels.collapsed_groups";
    private const string CollapsedCollectionsKey = "invlevels.collapsed_collections";
    private const string DefaultCollectionToken  = "default";

    private bool _expansionRestored;

    private void ApplyStoredExpansion()
    {
        var groups = Ids(_prefs.Get(CollapsedGroupsKey) ?? "");
        foreach (var g in _allGroups)
            g.IsExpanded = !groups.Contains(g.GroupId.ToString());

        var colls = Ids(_prefs.Get(CollapsedCollectionsKey) ?? "");
        foreach (var c in _allCollections)
            c.IsExpanded = !colls.Contains(c.CollectionId!.Value.ToString());
        if (_defaultCollRow is not null)
            _defaultCollRow.IsExpanded = !colls.Contains(DefaultCollectionToken);

        _expansionRestored = true;

        static HashSet<string> Ids(string csv) =>
            new(csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void PersistExpansion()
    {
        // Not before the stored state has been applied, or the first rebuild of a fresh load —
        // where everything still defaults to expanded — would overwrite what was saved.
        if (!_expansionRestored) return;

        var groups = string.Join(',', _allGroups.Where(g => !g.IsExpanded).Select(g => g.GroupId));

        var colls = _allCollections.Where(c => !c.IsExpanded)
                                   .Select(c => c.CollectionId!.Value.ToString())
                                   .ToList();
        if (_defaultCollRow is { IsExpanded: false }) colls.Add(DefaultCollectionToken);

        _ = _prefs.SetAsync(CollapsedGroupsKey, groups);
        _ = _prefs.SetAsync(CollapsedCollectionsKey, string.Join(',', colls));
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
            export: async () =>
            {
                if (isSynthetic || !collectionId.HasValue || ExportCollection is null) return;
                var collRow = _allCollections.FirstOrDefault(c => c.CollectionId == collectionId);
                if (collRow == null) return;
                await ExportCollection(collectionId.Value, collRow.CollectionName);
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
