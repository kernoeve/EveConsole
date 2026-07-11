using System.Collections.ObjectModel;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveCortex.ViewModels;

// ── Tree node types ───────────────────────────────────────────────────────────

public class MarketGroupNode : ReactiveObject
{
    public int    GroupId  { get; init; }
    public string Name     { get; init; } = "";
    public ObservableCollection<object> Children { get; } = [];
    public bool   HasItems => Children.Count > 0;

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
}

public class TypeNode
{
    public int    TypeId        { get; init; }
    public string Name          { get; init; } = "";
    public int    MarketGroupId { get; init; }
}

// ── Period filter ─────────────────────────────────────────────────────────────

public record PeriodOption(string Label, int Days); // Days=-1 means All Time

// ── Search result ─────────────────────────────────────────────────────────────

public class TypeSearchResult
{
    public int    TypeId    { get; init; }
    public string Name      { get; init; } = "";
    public string GroupPath { get; init; } = "";
}

// ── Item display models ────────────────────────────────────────────────────────

public record AttrDisplayVm(string Name, string ValueText);
public record AttrGroupVm(string CategoryName, IReadOnlyList<AttrDisplayVm> Attrs);
public record BlueprintMatVm(string MaterialName, int MaterialTypeId, int Quantity);
public record BlueprintVm(string BlueprintName, int BlueprintTypeId, IReadOnlyList<BlueprintMatVm> Materials);
public record MaterialUseVm(string BlueprintName, int BlueprintTypeId, string ProductName);

// ── Blueprint detail models ────────────────────────────────────────────────────

public record BpSkillVm(string SkillName, int SkillTypeId, int Level)
{
    public string LevelRoman => Level switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => Level.ToString() };
}

public record BpProductVm(string ProductName, int ProductTypeId, int Quantity, double Probability)
{
    public string QuantityText    => Quantity.ToString("N0");
    public bool   HasProbability  => Probability > 0 && Probability < 1;
    public string ProbabilityText => $"{Probability:P0}";
}

public record BpActivityVm(string ActivityKey, string ActivityLabel,
    IReadOnlyList<BpProductVm>    Products,
    IReadOnlyList<BlueprintMatVm> Materials,
    IReadOnlyList<BpSkillVm>      Skills)
{
    public bool HasProducts  => Products.Count  > 0;
    public bool HasMaterials => Materials.Count > 0;
    public bool HasSkills    => Skills.Count    > 0;
}

public record BlueprintDetailVm(int BlueprintTypeId, IReadOnlyList<BpActivityVm> Activities, int MaxProductionLimit)
{
    public bool HasMultipleActivities => Activities.Count > 1;
}

// ── Requirements / Required For tabs ───────────────────────────────────────────

public record RequiredForItemVm(string ItemName, int TypeId, int Level)
{
    public string LevelRoman => Level switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => Level.ToString() };
}

public record RequiredForGroupVm(string CategoryName, IReadOnlyList<RequiredForItemVm> Items);

// ── Market Orders display models ──────────────────────────────────────────────

public class MarketConfigOption
{
    public int    Id           { get; init; }
    public string LocationName { get; init; } = "";
    public string Method       { get; init; } = "";
    public override string ToString() => LocationName;
}

public class OrderRowVm
{
    public double         Price        { get; init; }
    public int            VolumeRemain { get; init; }
    public int            VolumeTotal  { get; init; }
    public int            MinVolume    { get; init; }
    public string         Range        { get; init; } = "";
    public string         LocationName { get; init; } = "";
    public DateTimeOffset Expires      { get; init; }

    public string PriceText        => Price.ToString("N2");
    public string VolumeRemainText => VolumeRemain.ToString("N0");
    public string MinVolumeText    => MinVolume.ToString("N0");
    public string ExpiresText
    {
        get
        {
            var r = Expires - DateTimeOffset.UtcNow;
            if (r.TotalSeconds <= 0) return "Expired";
            if (r.TotalDays    >= 1) return $"{(int)r.TotalDays}d {r.Hours}h";
            return $"{r.Hours}h {r.Minutes}m";
        }
    }
    public string RangeDisplay => Range switch
    {
        "station"     => "Station",
        "solarsystem" => "Solar System",
        "region"      => "Region",
        var n         => int.TryParse(n, out _) ? $"{n} Jumps" : n,
    };
}

public class ItemDisplayVm : ReactiveObject
{
    public int    TypeId           { get; init; }
    public string Name             { get; init; } = "";
    public string Description      { get; init; } = "";
    public string GroupPath        { get; init; } = "";
    public string VolumeText       { get; init; } = "";
    public int    PortionSize      { get; init; }
    public string MarketValueLabel { get; init; } = "Market Value";
    public string MarketValueText  { get; init; } = "";
    public string BuildCostText          { get; init; } = "";
    public bool   HasBuildCost           { get; init; }
    public string ReprocessedValueText   { get; init; } = "";

    // Attributes tab: fixed type stats + optional dogma attrs grouped by category
    public IReadOnlyList<AttrDisplayVm> TypeStats   { get; init; } = [];
    public IReadOnlyList<AttrGroupVm>   DogmaAttrs  { get; init; } = [];
    public bool HasDogmaAttrs => DogmaAttrs.Count > 0;

    // Industry tab — when item is a regular item
    public IReadOnlyList<BlueprintVm>   ProducedBy  { get; init; } = [];
    public IReadOnlyList<MaterialUseVm> UsedIn      { get; init; } = [];
    public bool HasProducedBy => ProducedBy.Count > 0;
    public bool HasUsedIn    => UsedIn.Count > 0;
    public bool HasIndustry  => HasProducedBy || HasUsedIn;

    // Industry tab — when item is itself a blueprint / reaction formula
    public BlueprintDetailVm? BlueprintDetail { get; init; }
    public bool IsBlueprint    => BlueprintDetail is not null;
    public bool IsNotBlueprint => BlueprintDetail is null;

    // Requirements tab — skills needed to use/build this item (any item can have these)
    public IReadOnlyList<BpSkillVm> Requirements { get; init; } = [];
    public bool HasRequirements => Requirements.Count > 0;

    // Required For tab — only meaningful when this item is itself a skill
    public bool IsSkill { get; init; }
    public IReadOnlyDictionary<int, IReadOnlyList<RequiredForGroupVm>> RequiredForByLevel { get; init; }
        = new Dictionary<int, IReadOnlyList<RequiredForGroupVm>>();

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public class ItemBrowserViewModel : ReactiveObject
{
    private readonly AppDbContext _db;
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly Regex _tagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private const int SkillCategoryId = 16;

    // requiredSkillN / requiredSkillNLevel dogma attribute ID pairs (N = 1..6)
    private static readonly (int SkillAttr, int LevelAttr)[] SkillAttrPairs =
        [(182, 277), (183, 278), (184, 279), (1285, 1286), (1289, 1287), (1290, 1288)];

    // Categories worth surfacing on the "Required For" tab — mirrors what the EVE client
    // itself shows, excluding NPC wreck/entity/celestial noise that also carries these
    // dogma attributes in the SDE.
    private static readonly HashSet<int> RequiredForCategoryAllowlist =
        [6, 7, 8, 16, 18, 20, 22, 23, 32, 65, 66];

    // ── Tree ─────────────────────────────────────────────────────────────────
    public ObservableCollection<MarketGroupNode> RootGroups { get; } = [];

    // ── Search ───────────────────────────────────────────────────────────────
    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public bool IsSearching => _searchText.Length > 1;
    public bool IsBrowsing  => _searchText.Length < 2;

    public ObservableCollection<TypeSearchResult> SearchResults { get; } = [];

    private TypeSearchResult? _selectedResult;
    public TypeSearchResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedResult, value);
            if (value != null) _ = NavigateWithHistoryAsync(value.TypeId);
        }
    }

    // ── Tree selection ────────────────────────────────────────────────────────
    private object? _selectedNode;
    public object? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            if (value is TypeNode tn) _ = NavigateWithHistoryAsync(tn.TypeId);
        }
    }

    // ── Selected item ─────────────────────────────────────────────────────────
    private ItemDisplayVm? _selectedItem;
    public ItemDisplayVm? SelectedItem
    {
        get => _selectedItem;
        private set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public bool HasItem    => _selectedItem != null;
    public bool NoItem     => _selectedItem == null;

    // ── Detail tab selection ──────────────────────────────────────────────────
    // Index into the ItemDetailTabs TabControl. Conditionally-hidden tabs keep their
    // slot in the Items collection, so these indices are stable regardless of which
    // tabs are currently visible.
    private int _selectedDetailTabIndex;
    public int SelectedDetailTabIndex
    {
        get => _selectedDetailTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedDetailTabIndex, value);
    }

    // Switches the Item Browser to a named detail tab. Returns a status message.
    public string ShowDetailTab(string tabKey)
    {
        var key = tabKey.Trim().ToLowerInvariant().Replace(" ", "_");
        int? idx = key switch
        {
            "description"                    => 0,
            "attributes"                     => 1,
            "requirements"                   => 2,
            "required_for"                   => 3,
            "industry"                       => 4,
            "market_orders" or "market" or "orders" => 5,
            "price_history" or "history" or "price" => 6,
            "derived_history" or "derived" => 7,
            _ => null,
        };
        if (idx is null) return $"Unknown Item Browser tab '{tabKey}'.";
        if (idx == 3 && SelectedItem?.IsSkill != true)
            return "The Required For tab is only available when the loaded item is a skill.";
        if (idx == 6 && !HasPriceHistoryRegions)
            return "The Price History tab has no regions configured (add one in Settings > Price History).";
        SelectedDetailTabIndex = idx.Value;
        return $"Showing the {key.Replace('_', ' ')} tab.";
    }

    // Selects a market-orders source by (partial) name. Returns a status message.
    public string TrySelectMarketSource(string name)
    {
        if (MarketConfigs.Count == 0)
            return "No ESI market sources are configured (add one in Settings > Market).";
        var match = MarketConfigs.FirstOrDefault(c => c.LocationName.Contains(name, StringComparison.OrdinalIgnoreCase))
                 ?? MarketConfigs.FirstOrDefault(c => name.Contains(c.LocationName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"No market source matching '{name}'. Available: {string.Join(", ", MarketConfigs.Select(c => c.LocationName))}.";
        SelectedMarketConfig = match;
        return $"Market source set to {match.LocationName}.";
    }

    // Selects a price-history region by (partial) name. Returns a status message.
    public string TrySelectHistoryRegion(string name)
    {
        if (HistoryRegions.Count == 0)
            return "No price-history regions are configured (add one in Settings > Price History).";
        var match = HistoryRegions.FirstOrDefault(r => r.RegionName.Contains(name, StringComparison.OrdinalIgnoreCase))
                 ?? HistoryRegions.FirstOrDefault(r => name.Contains(r.RegionName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return $"No price-history region matching '{name}'. Available: {string.Join(", ", HistoryRegions.Select(r => r.RegionName))}.";
        SelectedHistoryRegion = match;
        return $"Price-history region set to {match.RegionName}.";
    }

    // ── Market Orders tab ─────────────────────────────────────────────────────
    public ObservableCollection<MarketConfigOption> MarketConfigs { get; } = [];
    public bool HasMarketConfigs => MarketConfigs.Count > 0;

    private MarketConfigOption? _selectedMarketConfig;
    public MarketConfigOption? SelectedMarketConfig
    {
        get => _selectedMarketConfig;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedMarketConfig, value);
            _ = LoadOrdersAsync();
        }
    }

    public ObservableCollection<OrderRowVm> BuyOrders  { get; } = [];
    public ObservableCollection<OrderRowVm> SellOrders { get; } = [];

    private bool _isLoadingOrders;
    public bool IsLoadingOrders
    {
        get => _isLoadingOrders;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingOrders, value);
    }

    private CancellationTokenSource _ordersCts = new();

    // ── Status ────────────────────────────────────────────────────────────────
    private string _status = "Loading items…";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _isLoading = true;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    // ── Internal search cache ─────────────────────────────────────────────────
    private record TypeSummary(int TypeId, string Name, int? GroupId);
    private List<TypeSummary>      _allTypes       = [];
    private Dictionary<int,string> _groupPathCache = [];  // groupId → "A > B > C"
    private Dictionary<int,int?>   _parentMap      = [];  // groupId → parentGroupId
    private Dictionary<int,string> _groupNameMap   = [];  // groupId → name

    // ── Navigation history ────────────────────────────────────────────────────
    private const int                        HistoryDepth  = 100;
    private readonly List<int>               _historyBack  = new(HistoryDepth + 1);
    private readonly List<int>               _historyFwd   = new(HistoryDepth + 1);
    private int?                             _currentTypeId;
    private Dictionary<int,MarketGroupNode>  _groupNodeMap = [];
    private Dictionary<int,TypeNode>         _typeNodeMap  = [];

    private bool _canGoBack;
    public bool CanGoBack
    {
        get => _canGoBack;
        private set => this.RaiseAndSetIfChanged(ref _canGoBack, value);
    }

    private bool _canGoForward;
    public bool CanGoForward
    {
        get => _canGoForward;
        private set => this.RaiseAndSetIfChanged(ref _canGoForward, value);
    }

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> GoBackCommand    { get; }
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> GoForwardCommand { get; }

    private CancellationTokenSource _itemCts    = new();
    private CancellationTokenSource _historyCts = new();

    // ── Price History ─────────────────────────────────────────────────────────

    private readonly MarketHistoryService? _historyService;

    public ObservableCollection<PriceHistoryRegion> HistoryRegions { get; } = [];

    private PriceHistoryRegion? _selectedHistoryRegion;
    public PriceHistoryRegion? SelectedHistoryRegion
    {
        get => _selectedHistoryRegion;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedHistoryRegion, value);
            if (value is not null && SelectedItem is not null)
                _ = LoadPriceHistoryAsync();
        }
    }

    public IReadOnlyList<PeriodOption> PeriodOptions { get; } =
    [
        new("All Time",       -1),
        new("Last 30 Days",   30),
        new("Last 90 Days",   90),
        new("Last 365 Days", 365),
    ];

    private PeriodOption _selectedPeriod;
    public PeriodOption SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPeriod, value);
            ApplyPeriodFilter();
        }
    }

    private List<MarketTypeHistory> _allHistoryRows = [];

    public ObservableCollection<MarketTypeHistory> HistoryRows { get; } = [];

    private bool _isLoadingHistory;
    public bool IsLoadingHistory
    {
        get => _isLoadingHistory;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingHistory, value);
    }

    private bool _historyIsEmpty = true;
    public bool HistoryIsEmpty
    {
        get => _historyIsEmpty;
        private set => this.RaiseAndSetIfChanged(ref _historyIsEmpty, value);
    }

    private bool _hasPriceHistoryRegions;
    public bool HasPriceHistoryRegions
    {
        get => _hasPriceHistoryRegions;
        private set => this.RaiseAndSetIfChanged(ref _hasPriceHistoryRegions, value);
    }

    // Chart properties
    private ISeries[]? _historySeries;
    private Axis[]?    _historyXAxes;
    private Axis[]?    _historyYAxes;

    public ISeries[] HistorySeries { get => _historySeries ?? []; private set => this.RaiseAndSetIfChanged(ref _historySeries, value); }
    public Axis[]    HistoryXAxes  { get => _historyXAxes  ?? []; private set => this.RaiseAndSetIfChanged(ref _historyXAxes,  value); }
    public Axis[]    HistoryYAxes  { get => _historyYAxes  ?? []; private set => this.RaiseAndSetIfChanged(ref _historyYAxes,  value); }

    // ── Derived History (TypePriceSnapshots) ──────────────────────────────────
    // Daily snapshot of the item's computed Market / Build / Contract value, one
    // series each. Sourced from the TypePriceSnapshots table for the selected type.

    private CancellationTokenSource _derivedCts = new();
    private record DerivedRow(string Date, double? Market, double? Build, double? Contract);
    private List<DerivedRow> _allDerivedRows = [];

    private PeriodOption _selectedDerivedPeriod;
    public PeriodOption SelectedDerivedPeriod
    {
        get => _selectedDerivedPeriod;
        set { this.RaiseAndSetIfChanged(ref _selectedDerivedPeriod, value); ApplyDerivedPeriodFilter(); }
    }

    private bool _isLoadingDerived;
    public bool IsLoadingDerived
    {
        get => _isLoadingDerived;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingDerived, value);
    }

    private bool _derivedIsEmpty = true;
    public bool DerivedIsEmpty
    {
        get => _derivedIsEmpty;
        private set => this.RaiseAndSetIfChanged(ref _derivedIsEmpty, value);
    }

    private ISeries[]? _derivedSeries;
    private Axis[]?    _derivedXAxes;
    private Axis[]?    _derivedYAxes;

    public ISeries[] DerivedSeries { get => _derivedSeries ?? []; private set => this.RaiseAndSetIfChanged(ref _derivedSeries, value); }
    public Axis[]    DerivedXAxes  { get => _derivedXAxes  ?? []; private set => this.RaiseAndSetIfChanged(ref _derivedXAxes,  value); }
    public Axis[]    DerivedYAxes  { get => _derivedYAxes  ?? []; private set => this.RaiseAndSetIfChanged(ref _derivedYAxes,  value); }

    // ── Blueprint activity selection ──────────────────────────────────────────

    private BpActivityVm? _selectedBpActivity;
    public BpActivityVm? SelectedBpActivity
    {
        get => _selectedBpActivity;
        set => this.RaiseAndSetIfChanged(ref _selectedBpActivity, value);
    }

    // ── Required For level selector (skills only) ─────────────────────────────

    private int _requiredForLevel = 1;
    public int RequiredForLevel
    {
        get => _requiredForLevel;
        private set
        {
            this.RaiseAndSetIfChanged(ref _requiredForLevel, value);
            this.RaisePropertyChanged(nameof(IsRequiredForLevel1));
            this.RaisePropertyChanged(nameof(IsRequiredForLevel2));
            this.RaisePropertyChanged(nameof(IsRequiredForLevel3));
            this.RaisePropertyChanged(nameof(IsRequiredForLevel4));
            this.RaisePropertyChanged(nameof(IsRequiredForLevel5));
            RebuildRequiredForGroups();
        }
    }

    public bool IsRequiredForLevel1 => RequiredForLevel == 1;
    public bool IsRequiredForLevel2 => RequiredForLevel == 2;
    public bool IsRequiredForLevel3 => RequiredForLevel == 3;
    public bool IsRequiredForLevel4 => RequiredForLevel == 4;
    public bool IsRequiredForLevel5 => RequiredForLevel == 5;

    public bool HasItemsAtLevel1 => SelectedItem?.RequiredForByLevel.ContainsKey(1) == true;
    public bool HasItemsAtLevel2 => SelectedItem?.RequiredForByLevel.ContainsKey(2) == true;
    public bool HasItemsAtLevel3 => SelectedItem?.RequiredForByLevel.ContainsKey(3) == true;
    public bool HasItemsAtLevel4 => SelectedItem?.RequiredForByLevel.ContainsKey(4) == true;
    public bool HasItemsAtLevel5 => SelectedItem?.RequiredForByLevel.ContainsKey(5) == true;

    public ReactiveCommand<string, System.Reactive.Unit> SetRequiredForLevelCommand { get; }

    public ObservableCollection<RequiredForGroupVm> RequiredForGroups { get; } = [];
    public bool HasRequiredForGroups => RequiredForGroups.Count > 0;

    private void RebuildRequiredForGroups()
    {
        RequiredForGroups.Clear();
        if (SelectedItem?.RequiredForByLevel.TryGetValue(RequiredForLevel, out var groups) == true)
            foreach (var g in groups) RequiredForGroups.Add(g);
        this.RaisePropertyChanged(nameof(HasRequiredForGroups));
    }

    // Navigates to any item (blueprint or product) by type ID
    public ReactiveCommand<int, System.Reactive.Unit> NavigateToItemCommand { get; }

    public ItemBrowserViewModel(AppDbContext db, MarketHistoryService? historyService = null)
    {
        _db             = db;
        _historyService = historyService;
        _selectedPeriod = PeriodOptions[0]; // All Time
        _selectedDerivedPeriod = PeriodOptions[0]; // All Time

        GoBackCommand    = ReactiveCommand.CreateFromTask(GoBackAsync,
                               this.WhenAnyValue(x => x.CanGoBack));
        GoForwardCommand = ReactiveCommand.CreateFromTask(GoForwardAsync,
                               this.WhenAnyValue(x => x.CanGoForward));

        NavigateToItemCommand = ReactiveCommand.CreateFromTask<int>(NavigateWithHistoryAsync);
        SetRequiredForLevelCommand = ReactiveCommand.Create<string>(s =>
        {
            if (int.TryParse(s, out var level)) RequiredForLevel = level;
        });

        // Debounced search
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(text =>
            {
                this.RaisePropertyChanged(nameof(IsSearching));
                this.RaisePropertyChanged(nameof(IsBrowsing));
                _ = RunSearchAsync(text);
            });

        _ = InitialLoadsAsync();
    }

    private async Task InitialLoadsAsync()
    {
        await LoadTreeAsync();
        await LoadMarketConfigsAsync();
    }

    // ── Tree loading ──────────────────────────────────────────────────────────

    private async Task LoadTreeAsync()
    {
        try
        {
            var groups = await _db.SdeMarketGroups.AsNoTracking().ToListAsync();
            var types  = await _db.SdeTypes.AsNoTracking()
                .Where(t => t.Published && t.MarketGroupId != null)
                .Select(t => new TypeSummary(t.TypeId, t.Name, t.MarketGroupId))
                .ToListAsync();

            _allTypes = types;
            _parentMap = groups.ToDictionary(g => g.MarketGroupId, g => g.ParentGroupId);
            _groupNameMap = groups.ToDictionary(g => g.MarketGroupId,
                g => g.Name.Length > 0 ? g.Name : $"#{g.MarketGroupId}");

            // Build node map
            var nodeMap = new Dictionary<int, MarketGroupNode>();
            foreach (var g in groups)
                nodeMap[g.MarketGroupId] = new MarketGroupNode
                {
                    GroupId = g.MarketGroupId,
                    Name    = g.Name.Length > 0 ? g.Name : $"Group #{g.MarketGroupId}"
                };

            // Add child groups
            foreach (var g in groups)
            {
                if (g.ParentGroupId.HasValue && nodeMap.TryGetValue(g.ParentGroupId.Value, out var parent))
                    parent.Children.Add(nodeMap[g.MarketGroupId]);
            }

            // Add type leaves to their groups; capture TypeNode refs for tree expansion.
            var byGroup      = types.GroupBy(t => t.GroupId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
            var typeNodeMap  = new Dictionary<int, TypeNode>(types.Count);

            foreach (var (gid, node) in nodeMap)
            {
                if (!byGroup.TryGetValue(gid, out var gTypes)) continue;
                foreach (var t in gTypes)
                {
                    var tn = new TypeNode { TypeId = t.TypeId, Name = t.Name, MarketGroupId = gid };
                    node.Children.Add(tn);
                    typeNodeMap[t.TypeId] = tn;
                }
            }

            // Root nodes: no parent, sorted by name
            var roots = groups
                .Where(g => !g.ParentGroupId.HasValue)
                .Select(g => nodeMap[g.MarketGroupId])
                .OrderBy(n => n.Name)
                .ToList();

            // Make both maps available before the tree renders.
            _groupNodeMap = nodeMap;
            _typeNodeMap  = typeNodeMap;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var r in roots) RootGroups.Add(r);
                IsLoading = false;
                Status = $"{types.Count:N0} items across {groups.Count:N0} categories";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                Status = $"Error loading tree: {ex.Message}";
            });
        }

        await LoadHistoryRegionsAsync();
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private Task RunSearchAsync(string text)
    {
        SearchResults.Clear();

        if (text.Length < 2) return Task.CompletedTask;

        var lower = text.ToLowerInvariant();
        var matches = _allTypes
            .Where(t => t.Name.Contains(lower, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Name)
            .Take(200)
            .Select(t => new TypeSearchResult
            {
                TypeId    = t.TypeId,
                Name      = t.Name,
                GroupPath = t.GroupId.HasValue ? BuildGroupPath(t.GroupId.Value) : ""
            });

        foreach (var m in matches)
            SearchResults.Add(m);
        return Task.CompletedTask;
    }

    private string BuildGroupPath(int groupId)
    {
        if (_groupPathCache.TryGetValue(groupId, out var cached)) return cached;

        var parts = new List<string>();
        var current = (int?)groupId;
        var visited = new HashSet<int>();

        while (current.HasValue && !visited.Contains(current.Value))
        {
            visited.Add(current.Value);
            if (_groupNameMap.TryGetValue(current.Value, out var n)) parts.Add(n);
            _parentMap.TryGetValue(current.Value, out current);
        }

        parts.Reverse();
        var path = string.Join(" › ", parts);
        _groupPathCache[groupId] = path;
        return path;
    }

    // ── Item loading ──────────────────────────────────────────────────────────

    public async Task SelectTypeAsync(int typeId) => await NavigateWithHistoryAsync(typeId);

    // External callers use this; name param kept for API compatibility.
    public async Task NavigateToTypeAsync(int typeId, string name) => await NavigateWithHistoryAsync(typeId);

    // ── History-aware navigation ──────────────────────────────────────────────

    private async Task NavigateWithHistoryAsync(int typeId)
    {
        if (_currentTypeId == typeId) return; // already showing this item

        if (_currentTypeId.HasValue)
        {
            _historyBack.Add(_currentTypeId.Value);
            if (_historyBack.Count > HistoryDepth)
                _historyBack.RemoveAt(0);
        }
        _historyFwd.Clear();
        _currentTypeId = typeId;
        UpdateHistoryState();

        await Dispatcher.UIThread.InvokeAsync(() => ExpandAndSelectTreeNode(typeId));
        await LoadItemAsync(typeId);
    }

    private async Task GoBackAsync()
    {
        if (_historyBack.Count == 0) return;

        int prevId = _historyBack[^1];
        _historyBack.RemoveAt(_historyBack.Count - 1);

        if (_currentTypeId.HasValue)
            _historyFwd.Add(_currentTypeId.Value);

        _currentTypeId = prevId;
        UpdateHistoryState();

        await Dispatcher.UIThread.InvokeAsync(() => ExpandAndSelectTreeNode(prevId));
        await LoadItemAsync(prevId);
    }

    private async Task GoForwardAsync()
    {
        if (_historyFwd.Count == 0) return;

        int nextId = _historyFwd[^1];
        _historyFwd.RemoveAt(_historyFwd.Count - 1);

        if (_currentTypeId.HasValue)
        {
            _historyBack.Add(_currentTypeId.Value);
            if (_historyBack.Count > HistoryDepth)
                _historyBack.RemoveAt(0);
        }

        _currentTypeId = nextId;
        UpdateHistoryState();

        await Dispatcher.UIThread.InvokeAsync(() => ExpandAndSelectTreeNode(nextId));
        await LoadItemAsync(nextId);
    }

    private void UpdateHistoryState()
    {
        CanGoBack    = _historyBack.Count > 0;
        CanGoForward = _historyFwd.Count  > 0;
    }

    // Clears the search field (shows the tree), expands ancestor groups for typeId,
    // and selects the matching TypeNode — all without triggering NavigateWithHistoryAsync.
    // Must be called on the UI thread.
    private void ExpandAndSelectTreeNode(int typeId)
    {
        // Show tree instead of search results.
        _searchText = "";
        this.RaisePropertyChanged(nameof(SearchText));
        this.RaisePropertyChanged(nameof(IsSearching));
        this.RaisePropertyChanged(nameof(IsBrowsing));
        SearchResults.Clear();

        if (!_typeNodeMap.TryGetValue(typeId, out var typeNode)) return;

        // Expand every ancestor group so the leaf is visible.
        var groupId = (int?)typeNode.MarketGroupId;
        while (groupId.HasValue)
        {
            if (_groupNodeMap.TryGetValue(groupId.Value, out var gn))
                gn.IsExpanded = true;
            _parentMap.TryGetValue(groupId.Value, out groupId);
        }

        // Select the node directly without going through the setter (avoids re-entry).
        _selectedNode = typeNode;
        this.RaisePropertyChanged(nameof(SelectedNode));
    }

    // ── Price History ─────────────────────────────────────────────────────────

    public async Task LoadHistoryRegionsAsync()
    {
        var regions = await _db.PriceHistoryRegions
            .OrderBy(r => r.RegionName).ToListAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HistoryRegions.Clear();
            foreach (var r in regions) HistoryRegions.Add(r);
            HasPriceHistoryRegions = HistoryRegions.Count > 0;
            if (_selectedHistoryRegion is null && HistoryRegions.Count > 0)
                SelectedHistoryRegion = HistoryRegions[0];
        });
    }

    public async Task LoadPriceHistoryAsync()
    {
        if (_historyService is null || SelectedHistoryRegion is null || SelectedItem is null)
            return;

        // Cancel any in-progress load
        var prevCts = _historyCts;
        _historyCts = new CancellationTokenSource();
        var ct = _historyCts.Token;
        try { prevCts.Cancel(); prevCts.Dispose(); } catch { }

        var regionId = SelectedHistoryRegion.RegionId;
        var typeId   = SelectedItem.TypeId;

        IsLoadingHistory = true;
        HistoryRows.Clear();
        HistoryIsEmpty = true;

        try
        {
            await _historyService.EnsureFreshAsync(regionId, typeId, ct);

            if (ct.IsCancellationRequested) return;

            var rows = await _historyService.GetHistoryAsync(regionId, typeId);

            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allHistoryRows = rows;
                ApplyPeriodFilter();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadPriceHistoryAsync error: {ex}");
            HistoryIsEmpty = true;
        }
        finally { IsLoadingHistory = false; }
    }

    private void ApplyPeriodFilter()
    {
        IEnumerable<MarketTypeHistory> source = _allHistoryRows;
        if (_selectedPeriod.Days > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_selectedPeriod.Days).ToString("yyyy-MM-dd");
            source = source.Where(r => string.Compare(r.Date, cutoff) >= 0);
        }
        var filtered = source.ToList();

        HistoryRows.Clear();
        foreach (var r in filtered) HistoryRows.Add(r);
        HistoryIsEmpty = filtered.Count == 0;

        try { BuildHistoryChart(filtered); }
        catch (Exception chartEx)
        {
            System.Diagnostics.Debug.WriteLine($"BuildHistoryChart error: {chartEx}");
            HistorySeries = [];
            HistoryXAxes  = [];
            HistoryYAxes  = [];
        }
    }

    private void BuildHistoryChart(List<MarketTypeHistory> rows)
    {
        if (rows.Count == 0)
        {
            HistorySeries = [];
            HistoryXAxes  = [];
            HistoryYAxes  = [];
            return;
        }

        // Sort oldest→newest for the chart
        var sorted = rows.OrderBy(r => r.Date).ToList();

        static DateTimePoint ToPoint(MarketTypeHistory r, double v)
        {
            if (!DateTime.TryParse(r.Date, out var dt)) dt = DateTime.MinValue;
            return new DateTimePoint(dt, v);
        }

        var avgPts  = sorted.Select(r => ToPoint(r, r.Average)).ToList();
        var highPts = sorted.Select(r => ToPoint(r, r.Highest)).ToList();
        var lowPts  = sorted.Select(r => ToPoint(r, r.Lowest)).ToList();
        var volPts  = sorted.Select(r => ToPoint(r, r.Volume)).ToList();

        static SolidColorPaint P(SKColor c) => new(c);

        HistorySeries =
        [
            new LineSeries<DateTimePoint>
            {
                Name           = "Avg",
                Values         = avgPts,
                Stroke         = new SolidColorPaint(SKColors.Gold, 2),
                Fill           = null,
                GeometryFill   = null,
                GeometryStroke = null,
                ScalesYAt      = 0,
                YToolTipLabelFormatter = p => $"Avg: {p.Coordinate.PrimaryValue:N2}",
            },
            new LineSeries<DateTimePoint>
            {
                Name           = "High",
                Values         = highPts,
                Stroke         = new SolidColorPaint(SKColors.MediumSeaGreen, 1),
                Fill           = null,
                GeometryFill   = null,
                GeometryStroke = null,
                ScalesYAt      = 0,
                YToolTipLabelFormatter = p => $"High: {p.Coordinate.PrimaryValue:N2}",
            },
            new LineSeries<DateTimePoint>
            {
                Name           = "Low",
                Values         = lowPts,
                Stroke         = new SolidColorPaint(SKColors.IndianRed, 1),
                Fill           = null,
                GeometryFill   = null,
                GeometryStroke = null,
                ScalesYAt      = 0,
                YToolTipLabelFormatter = p => $"Low: {p.Coordinate.PrimaryValue:N2}",
            },
            new ColumnSeries<DateTimePoint>
            {
                Name      = "Volume",
                Values    = volPts,
                Fill      = P(new SKColor(91, 155, 213, 100)),
                Stroke    = null,
                ScalesYAt = 1,
                YToolTipLabelFormatter = p => $"Vol: {p.Coordinate.PrimaryValue:N0}",
            },
        ];

        HistoryXAxes =
        [
            new DateTimeAxis(TimeSpan.FromDays(1), d => d.ToString("MMM d"))
            {
                LabelsPaint    = P(new SKColor(136, 136, 153)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(40, 40, 60)),
            }
        ];

        HistoryYAxes =
        [
            new Axis
            {
                Name           = "ISK",
                LabelsPaint    = P(new SKColor(200, 168, 75)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(40, 40, 60)),
                Labeler        = v => FormatIsk(v),
                Position       = LiveChartsCore.Measure.AxisPosition.Start,
            },
            new Axis
            {
                Name           = "Volume",
                LabelsPaint    = P(new SKColor(91, 155, 213)),
                SeparatorsPaint = null,
                Labeler        = v => v >= 1_000_000 ? $"{v/1_000_000:N1}M"
                                    : v >= 1_000     ? $"{v/1_000:N1}K"
                                    : $"{v:N0}",
                Position       = LiveChartsCore.Measure.AxisPosition.End,
                ShowSeparatorLines = false,
            },
        ];
    }

    // ── Derived History loading / chart ───────────────────────────────────────

    public async Task LoadDerivedHistoryAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        // Cancel any in-progress load
        var prevCts = _derivedCts;
        _derivedCts = new CancellationTokenSource();
        var ct = _derivedCts.Token;
        try { prevCts.Cancel(); prevCts.Dispose(); } catch { }

        var typeId = item.TypeId;
        IsLoadingDerived = true;
        try
        {
            var rows = await Task.Run(() => FetchDerivedRows(typeId), ct);

            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allDerivedRows = rows;
                ApplyDerivedPeriodFilter();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadDerivedHistoryAsync error: {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allDerivedRows = [];
                ApplyDerivedPeriodFilter();
            });
        }
        finally { IsLoadingDerived = false; }
    }

    // Read snapshots on a background thread via an independent connection so this
    // never races the shared _db context (orders/history can be loading at the same time).
    private List<DerivedRow> FetchDerivedRows(int typeId)
    {
        var rows = new List<DerivedRow>();
        var connStr = _db.Database.GetDbConnection().ConnectionString;
        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Date", "MarketValue", "BuildCost", "ContractPrice"
            FROM "TypePriceSnapshots"
            WHERE "TypeId" = @typeId
            ORDER BY "Date"
            """;
        cmd.Parameters.AddWithValue("@typeId", typeId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new DerivedRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetDouble(1),
                r.IsDBNull(2) ? null : r.GetDouble(2),
                r.IsDBNull(3) ? null : r.GetDouble(3)));
        }
        return rows;
    }

    private void ApplyDerivedPeriodFilter()
    {
        IEnumerable<DerivedRow> source = _allDerivedRows;
        if (_selectedDerivedPeriod.Days > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_selectedDerivedPeriod.Days).ToString("yyyy-MM-dd");
            source = source.Where(r => string.Compare(r.Date, cutoff, StringComparison.Ordinal) >= 0);
        }
        var filtered = source.ToList();
        DerivedIsEmpty = filtered.Count == 0;

        try { BuildDerivedChart(filtered); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BuildDerivedChart error: {ex}");
            DerivedSeries = [];
            DerivedXAxes  = [];
            DerivedYAxes  = [];
        }
    }

    private void BuildDerivedChart(List<DerivedRow> rows)
    {
        if (rows.Count == 0)
        {
            DerivedSeries = [];
            DerivedXAxes  = [];
            DerivedYAxes  = [];
            return;
        }

        static DateTimePoint ToPoint(DerivedRow r, double? v)
        {
            if (!DateTime.TryParse(r.Date, out var dt)) dt = DateTime.MinValue;
            return new DateTimePoint(dt, v);   // null value → gap in the line
        }

        var marketPts   = rows.Select(r => ToPoint(r, r.Market)).ToList();
        var buildPts    = rows.Select(r => ToPoint(r, r.Build)).ToList();
        var contractPts = rows.Select(r => ToPoint(r, r.Contract)).ToList();

        static LineSeries<DateTimePoint> Line(string name, List<DateTimePoint> pts, SKColor c) => new()
        {
            Name                   = name,
            Values                 = pts,
            Stroke                 = new SolidColorPaint(c) { StrokeThickness = 2 },
            Fill                   = null,
            GeometryFill           = new SolidColorPaint(c),
            GeometryStroke         = null,
            GeometrySize           = 4,
            LineSmoothness         = 0.3,
            YToolTipLabelFormatter = p => $"{name}: {p.Coordinate.PrimaryValue:N2} ISK",
        };

        DerivedSeries =
        [
            Line("Market",   marketPts,   new SKColor(0x5b, 0x9b, 0xd5)),
            Line("Build",    buildPts,    new SKColor(0xed, 0x7d, 0x31)),
            Line("Contract", contractPts, new SKColor(0xf1, 0xc4, 0x0f)),
        ];

        DerivedXAxes =
        [
            new DateTimeAxis(TimeSpan.FromDays(1), d => d.ToString("MMM d"))
            {
                LabelsPaint     = new SolidColorPaint(new SKColor(136, 136, 153)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(40, 40, 60)),
            }
        ];

        DerivedYAxes =
        [
            new Axis
            {
                Name            = "ISK",
                LabelsPaint     = new SolidColorPaint(new SKColor(200, 168, 75)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(40, 40, 60)),
                Labeler         = v => FormatIsk(v),
            }
        ];
    }

    private static string FormatIsk(double v) => v switch
    {
        >= 1_000_000_000 => $"{v / 1_000_000_000:N1}B",
        >= 1_000_000     => $"{v / 1_000_000:N1}M",
        >= 1_000         => $"{v / 1_000:N1}K",
        _                => $"{v:N0}",
    };

    private async Task LoadItemAsync(int typeId)
    {
        var prevCts = _itemCts;
        _itemCts = new CancellationTokenSource();
        var ct = _itemCts.Token;
        try { prevCts.Cancel(); } catch { /* ignore */ }

        try
        {
            var type = await _db.SdeTypes.AsNoTracking()
                .FirstOrDefaultAsync(t => t.TypeId == typeId, ct);
            if (type == null || ct.IsCancellationRequested) return;

            var groupPath = type.MarketGroupId.HasValue
                ? BuildGroupPath(type.MarketGroupId.Value) : "";

            // Type stats (always shown)
            var typeStats = BuildTypeStats(type);

            // Dogma attributes (optional)
            var dogmaAttrs = await LoadDogmaAttrsAsync(typeId, ct);

            // Industry
            var (producedBy, usedIn) = await LoadIndustryAsync(typeId, ct);
            var blueprintDetail       = await LoadBlueprintDetailAsync(typeId, ct);

            // Requirements (skills needed to use/build this item — any item can have these)
            var requirements = await LoadRequirementsAsync(typeId, ct);

            // Required For (only meaningful when this item is itself a skill)
            var isSkill = await _db.SdeGroups.AsNoTracking()
                .Where(g => g.GroupId == type.GroupId)
                .Select(g => g.CategoryId)
                .FirstOrDefaultAsync(ct) == SkillCategoryId;
            var requiredForByLevel = isSkill
                ? await LoadRequiredForAsync(typeId, ct)
                : new Dictionary<int, List<RequiredForGroupVm>>();

            // Market value (uses the configured asset-value config + price type)
            var defaults = await _db.MarketDefaultSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct);
            string marketValueText  = "";
            string marketValueLabel = "Market Value";
            if (defaults?.AssetValueConfigId is int configId)
            {
                var price = await _db.MarketItemPrices.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.ConfigId == configId && p.TypeId == typeId, ct);
                if (price != null)
                {
                    double raw = defaults.AssetValuePriceType switch
                    {
                        MarketPriceType.Buy  => price.BuyPrice,
                        MarketPriceType.Sell => price.SellPrice,
                        _                    => price.Midpoint,
                    };
                    if (raw > 0)
                    {
                        marketValueText  = FormatIsk(raw);
                        marketValueLabel = $"Market Value ({defaults.AssetValuePriceType})";
                    }
                }
            }

            // Build cost
            var buildCost = await _db.BuildCosts.AsNoTracking()
                .FirstOrDefaultAsync(b => b.TypeId == typeId, ct);
            string buildCostText = buildCost is { TotalCost: > 0 }
                ? FormatIsk((double)buildCost.TotalCost)
                : "";

            // Reprocessing value
            var reprVal = await _db.ReprocessingItemValues.AsNoTracking()
                .FirstOrDefaultAsync(v => v.TypeId == typeId, ct);
            string reprValueText = reprVal is { Value: > 0 }
                ? FormatIsk(reprVal.Value)
                : "";

            if (ct.IsCancellationRequested) return;

            var vm = new ItemDisplayVm
            {
                TypeId           = typeId,
                Name             = type.Name,
                Description      = _tagRegex.Replace(type.Description, ""),
                GroupPath        = groupPath,
                VolumeText       = type.Volume > 0 ? $"{type.Volume:N2} m³" : "",
                PortionSize      = type.PortionSize,
                MarketValueLabel = marketValueLabel,
                MarketValueText      = marketValueText,
                BuildCostText        = buildCostText,
                HasBuildCost         = buildCost != null,
                ReprocessedValueText = reprValueText,
                TypeStats        = typeStats,
                DogmaAttrs       = dogmaAttrs,
                ProducedBy       = producedBy,
                UsedIn           = usedIn,
                BlueprintDetail  = blueprintDetail,
                Requirements     = requirements,
                IsSkill          = isSkill,
                RequiredForByLevel = requiredForByLevel.ToDictionary(
                    kv => kv.Key, kv => (IReadOnlyList<RequiredForGroupVm>)kv.Value),
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                SelectedItem       = vm;
                SelectedBpActivity = blueprintDetail?.Activities.FirstOrDefault();
                RequiredForLevel   = 1;
                RebuildRequiredForGroups(); // always rebuild — RequiredForLevel may already have been 1
                this.RaisePropertyChanged(nameof(HasItemsAtLevel1));
                this.RaisePropertyChanged(nameof(HasItemsAtLevel2));
                this.RaisePropertyChanged(nameof(HasItemsAtLevel3));
                this.RaisePropertyChanged(nameof(HasItemsAtLevel4));
                this.RaisePropertyChanged(nameof(HasItemsAtLevel5));
                this.RaisePropertyChanged(nameof(HasItem));
                this.RaisePropertyChanged(nameof(NoItem));
                _ = LoadOrdersAsync();
                if (HasPriceHistoryRegions)
                    _ = LoadPriceHistoryAsync();
                _ = LoadDerivedHistoryAsync();
            });

            // Load icon asynchronously
            _ = LoadIconAsync(typeId, vm, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Error: {ex.Message}");
        }
    }

    // ── Market orders loading ─────────────────────────────────────────────────

    private async Task LoadMarketConfigsAsync()
    {
        var configs = await _db.MarketPricingConfigs.AsNoTracking()
            .Where(c => (c.Method == MarketMethod.EsiRegion || c.Method == MarketMethod.PlayerStructure)
                        && c.IsEnabled)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .Select(c => new MarketConfigOption { Id = c.Id, LocationName = c.LocationName, Method = c.Method })
            .ToListAsync();

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            MarketConfigs.Clear();
            foreach (var c in configs) MarketConfigs.Add(c);
            this.RaisePropertyChanged(nameof(HasMarketConfigs));
            if (SelectedMarketConfig is null)
                SelectedMarketConfig = MarketConfigs.FirstOrDefault();
        });
    }

    private async Task LoadOrdersAsync()
    {
        var cts  = new CancellationTokenSource();
        _ordersCts.Cancel();
        _ordersCts = cts;
        var ct = cts.Token;

        var config = SelectedMarketConfig;
        var item   = SelectedItem;

        BuyOrders.Clear();
        SellOrders.Clear();

        if (config is null || item is null) return;

        IsLoadingOrders = true;
        try
        {
            var orders = await _db.MarketRawOrders.AsNoTracking()
                .Where(o => o.ConfigId == config.Id && o.TypeId == item.TypeId)
                .ToListAsync(ct);

            if (ct.IsCancellationRequested) return;

            // Resolve NPC station names from SDE (structure IDs ≥ 1T are handled by name below)
            var stationIds = orders
                .Select(o => o.LocationId)
                .Where(id => id < 1_000_000_000_000L)
                .Select(id => (int)id)
                .Distinct().ToList();

            var stationNames = stationIds.Count > 0
                ? await _db.SdeStations.AsNoTracking()
                    .Where(s => stationIds.Contains(s.StationId))
                    .ToDictionaryAsync(s => (long)s.StationId, s => s.Name, ct)
                : new Dictionary<long, string>();

            if (ct.IsCancellationRequested) return;

            string structureName = config.Method == MarketMethod.PlayerStructure
                ? config.LocationName : "Player Structure";

            string GetLocation(long locId) =>
                stationNames.TryGetValue(locId, out var n) ? n :
                locId >= 1_000_000_000_000L ? structureName :
                $"Station {locId}";

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                BuyOrders.Clear();
                SellOrders.Clear();

                foreach (var o in orders.Where(o => o.IsBuyOrder).OrderByDescending(o => o.Price))
                    BuyOrders.Add(new OrderRowVm
                    {
                        Price        = o.Price,
                        VolumeRemain = o.VolumeRemain,
                        VolumeTotal  = o.VolumeTotal,
                        MinVolume    = o.MinVolume,
                        Range        = o.Range,
                        LocationName = GetLocation(o.LocationId),
                        Expires      = o.Issued.AddDays(o.Duration),
                    });

                foreach (var o in orders.Where(o => !o.IsBuyOrder).OrderBy(o => o.Price))
                    SellOrders.Add(new OrderRowVm
                    {
                        Price        = o.Price,
                        VolumeRemain = o.VolumeRemain,
                        VolumeTotal  = o.VolumeTotal,
                        MinVolume    = o.MinVolume,
                        Range        = o.Range,
                        LocationName = GetLocation(o.LocationId),
                        Expires      = o.Issued.AddDays(o.Duration),
                    });
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Orders error: {ex.Message}");
        }
        finally { IsLoadingOrders = false; }
    }

    private static IReadOnlyList<AttrDisplayVm> BuildTypeStats(SdeType t)
    {
        var list = new List<AttrDisplayVm>();
        if (t.Volume   > 0)         list.Add(new AttrDisplayVm("Volume",       $"{t.Volume:N4} m³"));
        if (t.Mass     > 0)         list.Add(new AttrDisplayVm("Mass",         $"{t.Mass:N0} kg"));
        if (t.Capacity > 0)         list.Add(new AttrDisplayVm("Capacity",     $"{t.Capacity:N2} m³"));
        if (t.PortionSize > 1)      list.Add(new AttrDisplayVm("Portion Size", $"{t.PortionSize:N0}"));
        if (t.BasePrice is > 0)     list.Add(new AttrDisplayVm("Base Price",   $"{t.BasePrice.Value:N2} ISK"));
        return list;
    }

    private Dictionary<int, string>? _categoryNames;

    private async Task<Dictionary<int, string>> GetCategoryNamesAsync(CancellationToken ct)
    {
        if (_categoryNames != null) return _categoryNames;
        try
        {
            _categoryNames = await _db.SdeDogmaAttributeCategories.AsNoTracking()
                .ToDictionaryAsync(c => c.CategoryId, c => c.Name, ct);
        }
        catch { _categoryNames = []; }
        return _categoryNames;
    }

    private static readonly System.Text.RegularExpressions.Regex _camelSplit =
        new(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string PrettyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var s = _camelSplit.Replace(name, " ");
        return char.ToUpper(s[0]) + s[1..];
    }

    private async Task<IReadOnlyList<AttrGroupVm>> LoadDogmaAttrsAsync(int typeId, CancellationToken ct)
    {
        var raw = await _db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.TypeId == typeId)
            .Join(_db.SdeDogmaAttributes, a => a.AttributeId, d => d.AttributeId,
                  (a, d) => new { d.Name, d.DisplayName, d.CategoryId, d.UnitId, d.Published, a.Value })
            .Where(a => a.Published)
            .ToListAsync(ct);

        var categories = await GetCategoryNamesAsync(ct);

        return raw
            .Select(a =>
            {
                var dn    = a.DisplayName.Length > 0 && a.DisplayName != a.Name
                    ? a.DisplayName : PrettyName(a.Name);
                var value = FormatAttrValue(a.Value, a.UnitId);
                return (CatId: a.CategoryId, Attr: new AttrDisplayVm(dn, value));
            })
            .GroupBy(x => x.CatId)
            .OrderBy(g => g.Key ?? int.MaxValue)
            .Select(g =>
            {
                var catName = g.Key.HasValue && categories.TryGetValue(g.Key.Value, out var n)
                    ? n : "";
                var attrs = g.OrderBy(x => x.Attr.Name).Select(x => x.Attr).ToList();
                return new AttrGroupVm(catName, attrs);
            })
            .ToList();
    }

    private async Task<(IReadOnlyList<BlueprintVm>, IReadOnlyList<MaterialUseVm>)>
        LoadIndustryAsync(int typeId, CancellationToken ct)
    {
        // Blueprints that produce this item
        var bpIds = await _db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.ProductTypeId == typeId && p.Activity == "manufacturing")
            .Select(p => p.TypeId)
            .ToListAsync(ct);

        var producedBy = new List<BlueprintVm>();
        foreach (var bpId in bpIds.Take(10))
        {
            var bpName = await _db.SdeTypes.AsNoTracking()
                .Where(t => t.TypeId == bpId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct) ?? $"Blueprint #{bpId}";

            var mats = await _db.SdeBlueprintMaterials.AsNoTracking()
                .Where(m => m.TypeId == bpId && m.Activity == "manufacturing")
                .Join(_db.SdeTypes, m => m.MaterialTypeId, t => t.TypeId,
                      (m, t) => new { t.Name, m.MaterialTypeId, m.Quantity })
                .OrderBy(x => x.Name)
                .Select(x => new BlueprintMatVm(x.Name, x.MaterialTypeId, x.Quantity))
                .ToListAsync(ct);

            producedBy.Add(new BlueprintVm(bpName, bpId, mats));
        }

        // Blueprints where this item is an input material
        var usedInBpIds = await _db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => m.MaterialTypeId == typeId && m.Activity == "manufacturing")
            .Select(m => m.TypeId)
            .Distinct()
            .Take(30)
            .ToListAsync(ct);

        var totalUsedIn = await _db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => m.MaterialTypeId == typeId && m.Activity == "manufacturing")
            .Select(m => m.TypeId)
            .Distinct()
            .CountAsync(ct);

        var usedIn = new List<MaterialUseVm>();
        foreach (var bpId in usedInBpIds)
        {
            var bpName = await _db.SdeTypes.AsNoTracking()
                .Where(t => t.TypeId == bpId).Select(t => t.Name)
                .FirstOrDefaultAsync(ct) ?? $"Blueprint #{bpId}";

            var productName = await _db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => p.TypeId == bpId && p.Activity == "manufacturing")
                .Join(_db.SdeTypes, p => p.ProductTypeId, t => t.TypeId, (p, t) => t.Name)
                .FirstOrDefaultAsync(ct) ?? "";

            usedIn.Add(new MaterialUseVm(bpName, bpId, productName));
        }

        if (totalUsedIn > 30)
            usedIn.Add(new MaterialUseVm($"… and {totalUsedIn - 30} more blueprints", 0, ""));

        return (producedBy, usedIn);
    }

    private async Task<BlueprintDetailVm?> LoadBlueprintDetailAsync(int typeId, CancellationToken ct)
    {
        var bp = await _db.SdeBlueprints.AsNoTracking()
            .FirstOrDefaultAsync(b => b.TypeId == typeId, ct);
        if (bp is null) return null;

        // Three bulk queries — one per data type — then split by activity in memory
        var allProducts = await _db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.TypeId == typeId)
            .Join(_db.SdeTypes, p => p.ProductTypeId, t => t.TypeId,
                  (p, t) => new { p.Activity, t.Name, p.ProductTypeId, p.Quantity, p.Probability })
            .ToListAsync(ct);

        var allMaterials = await _db.SdeBlueprintMaterials.AsNoTracking()
            .Where(m => m.TypeId == typeId)
            .Join(_db.SdeTypes, m => m.MaterialTypeId, t => t.TypeId,
                  (m, t) => new { m.Activity, t.Name, m.MaterialTypeId, m.Quantity })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var allSkills = await _db.SdeBlueprintSkills.AsNoTracking()
            .Where(s => s.TypeId == typeId)
            .Join(_db.SdeTypes, s => s.SkillTypeId, t => t.TypeId,
                  (s, t) => new { s.Activity, t.Name, s.SkillTypeId, s.Level })
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var activityOrder = new[] { "manufacturing", "reaction", "invention", "copying", "research_material", "research_time" };
        var activities = new List<BpActivityVm>();

        foreach (var key in activityOrder)
        {
            var prods = allProducts.Where(p => p.Activity == key)
                .OrderBy(p => p.Name)
                .Select(p => new BpProductVm(p.Name, p.ProductTypeId, p.Quantity, p.Probability))
                .ToList();
            var mats = allMaterials.Where(m => m.Activity == key)
                .Select(m => new BlueprintMatVm(m.Name, m.MaterialTypeId, m.Quantity))
                .ToList();
            var skills = allSkills.Where(s => s.Activity == key)
                .Select(s => new BpSkillVm(s.Name, s.SkillTypeId, s.Level))
                .ToList();

            if (prods.Count == 0 && mats.Count == 0 && skills.Count == 0) continue;

            var label = key switch
            {
                "manufacturing"     => "Manufacturing",
                "reaction"          => "Reaction",
                "invention"         => "Invention",
                "copying"           => "Copying",
                "research_material" => "ME Research",
                "research_time"     => "TE Research",
                _                   => key,
            };
            activities.Add(new BpActivityVm(key, label, prods, mats, skills));
        }

        return activities.Count == 0 ? null : new BlueprintDetailVm(typeId, activities, bp.MaxProductionLimit);
    }

    // Skills required to use/build this item, read from the requiredSkillN / requiredSkillNLevel
    // dogma attribute pairs (works for ships, modules, other skills — anything can have these).
    private async Task<IReadOnlyList<BpSkillVm>> LoadRequirementsAsync(int typeId, CancellationToken ct)
    {
        var attrs = await _db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.TypeId == typeId)
            .ToDictionaryAsync(a => a.AttributeId, a => a.Value, ct);

        var results = new List<BpSkillVm>();
        foreach (var (skillAttr, levelAttr) in SkillAttrPairs)
        {
            if (!attrs.TryGetValue(skillAttr, out var skillIdVal)) continue;
            var skillTypeId = (int)skillIdVal;
            var level = attrs.TryGetValue(levelAttr, out var lv) ? (int)lv : 1;

            var skillName = await _db.SdeTypes.AsNoTracking()
                .Where(t => t.TypeId == skillTypeId)
                .Select(t => t.Name)
                .FirstOrDefaultAsync(ct) ?? $"Skill #{skillTypeId}";

            results.Add(new BpSkillVm(skillName, skillTypeId, level));
        }
        return results;
    }

    // Reverse lookup: what items require this skill, grouped by category, keyed by the
    // level at which they require it (I-V) so the view can filter to one level at a time.
    private async Task<Dictionary<int, List<RequiredForGroupVm>>> LoadRequiredForAsync(int skillTypeId, CancellationToken ct)
    {
        var byLevel = new Dictionary<int, List<(string CategoryName, string ItemName, int ItemTypeId)>>();

        foreach (var (skillAttr, levelAttr) in SkillAttrPairs)
        {
            var skillRows = _db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => a.AttributeId == skillAttr && (int)a.Value == skillTypeId);
            var levelRows = _db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => a.AttributeId == levelAttr);

            var rows = await skillRows
                .Join(levelRows, s => s.TypeId, l => l.TypeId, (s, l) => new { s.TypeId, Level = (int)l.Value })
                .Join(_db.SdeTypes.AsNoTracking().Where(t => t.Published), x => x.TypeId, t => t.TypeId,
                      (x, t) => new { x.TypeId, x.Level, TypeName = t.Name, t.GroupId })
                .Join(_db.SdeGroups.AsNoTracking(), x => x.GroupId, g => g.GroupId,
                      (x, g) => new { x.TypeId, x.Level, x.TypeName, g.CategoryId })
                .Where(x => RequiredForCategoryAllowlist.Contains(x.CategoryId))
                .Join(_db.SdeCategories.AsNoTracking(), x => x.CategoryId, c => c.CategoryId,
                      (x, c) => new { x.TypeId, x.Level, x.TypeName, CategoryName = c.Name })
                .ToListAsync(ct);

            foreach (var r in rows)
            {
                if (!byLevel.TryGetValue(r.Level, out var list))
                    byLevel[r.Level] = list = [];
                list.Add((r.CategoryName, r.TypeName, r.TypeId));
            }
        }

        return byLevel.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .GroupBy(i => i.CategoryName)
                .OrderBy(g => g.Key)
                .Select(g => new RequiredForGroupVm(g.Key,
                    g.OrderBy(i => i.ItemName)
                     .Select(i => new RequiredForItemVm(i.ItemName, i.ItemTypeId, kv.Key))
                     .ToList()))
                .ToList());
    }

    private async Task LoadIconAsync(int typeId, ItemDisplayVm vm, CancellationToken ct)
    {
        try
        {
            var variant = vm.IsBlueprint ? "bp" : "icon";
            var url   = $"https://images.evetech.net/types/{typeId}/{variant}?size=64";
            var bytes = await _http.GetByteArrayAsync(url, ct);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            await Dispatcher.UIThread.InvokeAsync(() => vm.Icon = bmp);
        }
        catch { /* icon is optional */ }
    }

    // ── Unit label table ──────────────────────────────────────────────────────

    private static readonly Dictionary<int, string> _units = new()
    {
        {1,"m"}, {2,"kg"}, {3,"s"}, {4,"m/s"}, {6,"m³"}, {9,"%"},
        {101,"tf"}, {102,"km"}, {105,"MW"}, {107,"GJ/s"}, {108,"s"},
        {109,"m"}, {111,"AU"}, {113,"HP"}, {114,"GJ"}, {115,"m³/s"},
        {116,"m/s"}, {117,"m"}, {118,"Ω"}, {119,"S"}, {120,"mm"},
        {124,"pts"}, {127,"m"}, {128,"tf"}, {129,"MN"}, {131,"AU"},
        {133,"pts"}, {134,"m³"}, {135,"1/s"},
    };

    private static string FormatAttrValue(double value, int? unitId)
    {
        var unit = unitId.HasValue && _units.TryGetValue(unitId.Value, out var u) ? u : "";
        string formatted;
        if (value >= 1_000_000_000)      formatted = $"{value / 1_000_000_000:N2}B";
        else if (value >= 1_000_000)     formatted = $"{value / 1_000_000:N2}M";
        else if (value >= 1_000)         formatted = $"{value:N0}";
        else if (value == Math.Floor(value)) formatted = $"{value:N0}";
        else                             formatted = $"{value:N4}".TrimEnd('0').TrimEnd('.');
        return unit.Length > 0 ? $"{formatted} {unit}" : formatted;
    }
}
