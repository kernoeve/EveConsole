using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

public class CharacterAuthOption
{
    public long   CharId { get; init; }
    public string Name   { get; init; } = "";
    public override string ToString() => Name;
}

/// <summary>A search hit. <see cref="Name"/> is English: picking one makes it the source's name.</summary>
public record LocationResult(long Id, string Name, string Category)
{
    /// <summary>What the results list shows: an NPC station in the interface language, a structure as named.</summary>
    public string DisplayName => SdeNames.Location(Id, Name);
}

public class StationFilterOption
{
    public long?  LocationId { get; init; }
    public string Name       { get; init; } = "";

    /// <summary>An NPC station in the interface language; the filter is kept by its id.</summary>
    public string DisplayName => LocationId is { } id ? SdeNames.Location(id, Name) : Name;

    public override string ToString() => DisplayName;
}

/// <summary>
/// A market source's name as the screens show it. The name is the user's to edit and is shown as
/// stored — except while it is still exactly the English name of the NPC station or region the
/// source reads, as picking one leaves it: that is shown in the interface language.
///
/// <para>⚠️ Display only. A source is saved, remembered and matched by its stored name.</para>
/// </summary>
internal static class MarketSourceNames
{
    /// <summary>The English names of the NPC stations and regions among these location ids, by id:
    /// what a stored name is held against.</summary>
    public static async Task<Dictionary<long, string>> PlacesAsync(
        AppDbContext db, IEnumerable<long> locationIds, CancellationToken ct = default)
    {
        var ids        = locationIds.Distinct().ToList();
        var stationIds = ids.Where(id => id is >= 60_000_000 and <= 63_999_999).Select(id => (int)id).ToList();
        var regionIds  = ids.Where(id => id is >= 10_000_000 and <= 10_999_999).Select(id => (int)id).ToList();

        var places = new Dictionary<long, string>();
        if (stationIds.Count > 0)
            foreach (var s in await db.SdeStations.AsNoTracking().Where(s => stationIds.Contains(s.StationId))
                         .Select(s => new { s.StationId, s.Name }).ToListAsync(ct))
                places[s.StationId] = s.Name;
        if (regionIds.Count > 0)
            foreach (var r in await db.SdeRegions.AsNoTracking().Where(r => regionIds.Contains(r.RegionId))
                         .Select(r => new { r.RegionId, r.Name }).ToListAsync(ct))
                places[r.RegionId] = r.Name;
        return places;
    }

    /// <summary>The name shown for a source reading <paramref name="locationId"/>, stored as
    /// <paramref name="stored"/>; <paramref name="placeEnglish"/> is that place's English name, when
    /// it is a station or region.</summary>
    public static string Shown(long locationId, string stored, string? placeEnglish) =>
        placeEnglish is not null && stored == placeEnglish ? SdeNames.Location(locationId, stored) : stored;
}

public class SdeRegionOption
{
    public SdeRegionOption() { }
    public SdeRegionOption(int regionId, string name) { RegionId = regionId; Name = name; }
    public int    RegionId { get; init; }

    /// <summary>English: saved as the source's or the price-history region's name.</summary>
    public string Name     { get; init; } = "";

    /// <summary>The region in the interface language, which the pickers show and sort on.</summary>
    public string DisplayName => SdeNames.Region(RegionId, Name);

    public override string ToString() => DisplayName;
}

public class MarketPricingConfigVm : ReactiveObject
{
    public int Id { get; init; }

    /// <summary>
    /// The price-source methods: the key the database keeps (MarketMethod), and the name shown.
    /// ⚠️ Chosen and compared by key — the shown name is translated, the stored one is not.
    /// </summary>
    public static IReadOnlyList<Choice<string>> MethodChoices { get; } =
    [
        new(MarketMethod.EsiRegion,       SettingsText.MethodRegion),
        new(MarketMethod.PlayerStructure, SettingsText.MethodPlayerStructure),
        new(MarketMethod.Fuzzwork,        MarketMethod.Fuzzwork),
    ];

    private string _method = MarketMethod.EsiRegion;

    /// <summary>The method's key, as the database keeps it.</summary>
    public string MethodKey
    {
        get => _method;
        set
        {
            this.RaiseAndSetIfChanged(ref _method, value);
            this.RaisePropertyChanged(nameof(Method));
            this.RaisePropertyChanged(nameof(IsFuzzwork));
            this.RaisePropertyChanged(nameof(IsEsiRegion));
            this.RaisePropertyChanged(nameof(IsPlayerStructure));
            this.RaisePropertyChanged(nameof(MethodBadge));
        }
    }

    /// <summary>The method as the lists show it; a key none of them knows shows as itself.</summary>
    public Choice<string> Method
    {
        get => MethodChoices.FirstOrDefault(o => o.Value == _method) ?? new(_method, _method);
        set
        {
            // A detaching ComboBox sets null; that is not a choice.
            if (value is null) { this.RaisePropertyChanged(); return; }
            MethodKey = value.Value;
        }
    }

    private string _locationName = "";
    /// <summary>The name as stored, which the user edits: what the source is saved, remembered and
    /// matched by. The lists show <see cref="DisplayName"/>.</summary>
    public string LocationName
    {
        get => _locationName;
        set
        {
            this.RaiseAndSetIfChanged(ref _locationName, value);
            this.RaisePropertyChanged(nameof(DisplayName));
        }
    }

    private string? _placeName;
    /// <summary>The English name of the NPC station or region the source reads, when it reads one:
    /// what tells a name left as the place's own from one typed in.</summary>
    public string? PlaceName
    {
        get => _placeName;
        set
        {
            _placeName = value;
            this.RaisePropertyChanged(nameof(DisplayName));
        }
    }

    /// <summary>The name as the lists show it — see <see cref="MarketSourceNames"/>.</summary>
    public string DisplayName =>
        long.TryParse(LocationIdText, out var id) ? MarketSourceNames.Shown(id, LocationName, PlaceName) : LocationName;

    private string _locationIdText = "";
    public string LocationIdText
    {
        get => _locationIdText;
        set
        {
            var another = _locationIdText != value;
            this.RaiseAndSetIfChanged(ref _locationIdText, value);
            ResolvedLocationName = "";
            if (another) PlaceName = null;   // named again once the lookup has found it
        }
    }

    private string _priceType = MarketPriceType.Midpoint;
    public string PriceType
    {
        get => _priceType;
        set => this.RaiseAndSetIfChanged(ref _priceType, value);
    }

    private CharacterAuthOption? _selectedAuthChar;
    public CharacterAuthOption? SelectedAuthChar
    {
        get => _selectedAuthChar;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAuthChar, value);
            if (value is not null) AuthCharId = value.CharId;
        }
    }

    public long? AuthCharId { get; set; }

    private SdeRegionOption? _selectedRegion;
    public SdeRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedRegion, value);
            if (value is null) return;
            _locationIdText = value.RegionId.ToString();
            this.RaisePropertyChanged(nameof(LocationIdText));
            PlaceName    = value.Name;
            LocationName = value.Name;
        }
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    private string _lastRefreshedText = SettingsText.Never;
    public string LastRefreshedText
    {
        get => _lastRefreshedText;
        set => this.RaiseAndSetIfChanged(ref _lastRefreshedText, value);
    }

    private string _lastStatus = "";
    public string LastStatus
    {
        get => _lastStatus;
        set => this.RaiseAndSetIfChanged(ref _lastStatus, value);
    }

    private string _resolvedLocationName = "";
    public string ResolvedLocationName
    {
        get => _resolvedLocationName;
        set => this.RaiseAndSetIfChanged(ref _resolvedLocationName, value);
    }

    private bool _isResolvingLocation;
    public bool IsResolvingLocation
    {
        get => _isResolvingLocation;
        set => this.RaiseAndSetIfChanged(ref _isResolvingLocation, value);
    }

    private bool _usePercentileFilter = true;
    public bool UsePercentileFilter
    {
        get => _usePercentileFilter;
        set => this.RaiseAndSetIfChanged(ref _usePercentileFilter, value);
    }

    private double _percentilePercent = 5.0;
    public double PercentilePercent
    {
        get => _percentilePercent;
        set => this.RaiseAndSetIfChanged(ref _percentilePercent, value);
    }

    public long? StationFilter { get; set; }

    public bool IsFuzzwork       => _method == MarketMethod.Fuzzwork;
    public bool IsEsiRegion      => _method == MarketMethod.EsiRegion;
    public bool IsPlayerStructure => _method == MarketMethod.PlayerStructure;
    public bool HasRawOrders     => !IsFuzzwork;
    public string MethodBadge    => _method == MarketMethod.Fuzzwork ? "FW" : "ESI";
}

public class MarketSettingsViewModel : ReactiveObject
{
    private readonly AppDbContext                    _db;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly MarketPricingService            _svc;
    private readonly EsiClient                       _esiClient;
    private readonly BuildCostService?               _buildCostSvc;

    // Fuzzwork is intentionally omitted — too much of the app (per-order views, station filters,
    // structure markets) needs raw orders, which the Fuzzwork method does not provide.
    public IReadOnlyList<Choice<string>>      Methods    { get; }
        = [.. MarketPricingConfigVm.MethodChoices.Where(c => c.Value != MarketMethod.Fuzzwork)];
    public IReadOnlyList<string>              PriceTypes { get; }
        = [MarketPriceType.Midpoint, MarketPriceType.Buy, MarketPriceType.Sell];

    private IReadOnlyList<CharacterAuthOption> _characterOptions = [];
    public IReadOnlyList<CharacterAuthOption> CharacterOptions
    {
        get => _characterOptions;
        private set => this.RaiseAndSetIfChanged(ref _characterOptions, value);
    }

    private IReadOnlyList<SdeRegionOption> _regionOptions = [];
    public IReadOnlyList<SdeRegionOption> RegionOptions
    {
        get => _regionOptions;
        private set => this.RaiseAndSetIfChanged(ref _regionOptions, value);
    }

    public ObservableCollection<MarketPricingConfigVm>  Configs              { get; } = [];
    public ObservableCollection<LocationResult>         LocationResults       { get; } = [];
    public ObservableCollection<StationFilterOption>    StationFilterOptions  { get; } = [];

    private bool _loadingStationOptions;
    private StationFilterOption? _selectedStationFilter;
    public StationFilterOption? SelectedStationFilter
    {
        get => _selectedStationFilter;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedStationFilter, value);
            if (!_loadingStationOptions && Selected is not null)
                Selected.StationFilter = value?.LocationId;
        }
    }

    private MarketPricingConfigVm? _selected;
    public MarketPricingConfigVm? Selected
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            this.RaisePropertyChanged(nameof(HasSelected));
            LocationResults.Clear();
            LocationSearch = "";
            SearchStatus   = "";
            _ = LoadStationFilterOptionsAsync(value);
        }
    }
    public bool HasSelected => _selected != null;

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string _locationSearch = "";
    public string LocationSearch
    {
        get => _locationSearch;
        set => this.RaiseAndSetIfChanged(ref _locationSearch, value);
    }

    private string _searchStatus = "";
    public string SearchStatus
    {
        get => _searchStatus;
        set => this.RaiseAndSetIfChanged(ref _searchStatus, value);
    }

    private LocationResult? _selectedLocationResult;
    public LocationResult? SelectedLocationResult
    {
        get => _selectedLocationResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLocationResult, value);
            this.RaisePropertyChanged(nameof(HasLocationResult));
        }
    }
    public bool HasLocationResult => _selectedLocationResult != null;

    // ── Default pricing ──────────────────────────────────────────────────────
    private MarketPricingConfigVm? _selectedAssetConfig;
    public MarketPricingConfigVm? SelectedAssetConfig
    {
        get => _selectedAssetConfig;
        set => this.RaiseAndSetIfChanged(ref _selectedAssetConfig, value);
    }

    private string _assetValuePriceType = MarketPriceType.Midpoint;
    public string AssetValuePriceType
    {
        get => _assetValuePriceType;
        set => this.RaiseAndSetIfChanged(ref _assetValuePriceType, value);
    }

    private MarketPricingConfigVm? _selectedManufacturingConfig;
    public MarketPricingConfigVm? SelectedManufacturingConfig
    {
        get => _selectedManufacturingConfig;
        set => this.RaiseAndSetIfChanged(ref _selectedManufacturingConfig, value);
    }

    private string _manufacturingPriceType = MarketPriceType.Sell;
    public string ManufacturingPriceType
    {
        get => _manufacturingPriceType;
        set => this.RaiseAndSetIfChanged(ref _manufacturingPriceType, value);
    }

    private decimal _missingPriceMarkupPct = 15m;
    public decimal MissingPriceMarkupPct
    {
        get => _missingPriceMarkupPct;
        set => this.RaiseAndSetIfChanged(ref _missingPriceMarkupPct, value);
    }

    private bool _filterLowballBuyOrders = true;
    public bool FilterLowballBuyOrders
    {
        get => _filterLowballBuyOrders;
        set => this.RaiseAndSetIfChanged(ref _filterLowballBuyOrders, value);
    }

    private decimal _lowballBuyOrderThresholdPct = 25m;
    public decimal LowballBuyOrderThresholdPct
    {
        get => _lowballBuyOrderThresholdPct;
        set => this.RaiseAndSetIfChanged(ref _lowballBuyOrderThresholdPct, value);
    }

    private bool _purchaseWhenCheaper;
    public bool PurchaseWhenCheaper
    {
        get => _purchaseWhenCheaper;
        set => this.RaiseAndSetIfChanged(ref _purchaseWhenCheaper, value);
    }

    private decimal _purchaseThresholdPct = 100m;
    public decimal PurchaseThresholdPct
    {
        get => _purchaseThresholdPct;
        set => this.RaiseAndSetIfChanged(ref _purchaseThresholdPct, value);
    }

    private string _defaultsStatus = "";
    public string DefaultsStatus
    {
        get => _defaultsStatus;
        private set => this.RaiseAndSetIfChanged(ref _defaultsStatus, value);
    }

    private string _buildCostStatus = "";
    public string BuildCostStatus
    {
        get => _buildCostStatus;
        private set => this.RaiseAndSetIfChanged(ref _buildCostStatus, value);
    }

    public ReactiveCommand<Unit, Unit> AddCommand                       { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand                      { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand                    { get; }
    public ReactiveCommand<Unit, Unit> RefreshAllCommand                { get; }
    public ReactiveCommand<Unit, Unit> RefreshSelectedCommand           { get; }
    public ReactiveCommand<Unit, Unit> SearchLocationsCommand           { get; }
    public ReactiveCommand<Unit, Unit> UseSelectedLocationCommand       { get; }
    public ReactiveCommand<Unit, Unit> SaveDefaultsCommand              { get; }
    public ReactiveCommand<Unit, Unit> RecalculateBuildCostsCommand     { get; }

    public MarketSettingsViewModel(
        AppDbContext                    db,
        IDbContextFactory<AppDbContext> dbFactory,
        MarketPricingService            svc,
        EsiClient                       esiClient,
        ObservableCollection<Character> characters,
        BuildCostService?               buildCostSvc = null)
    {
        _db            = db;
        _dbFactory     = dbFactory;
        _svc           = svc;
        _esiClient     = esiClient;
        _buildCostSvc  = buildCostSvc;

        RebuildCharacterOptions(characters);
        characters.CollectionChanged += (_, _) => RebuildCharacterOptions(characters);

        AddCommand                    = ReactiveCommand.CreateFromTask(AddAsync);
        SaveCommand                   = ReactiveCommand.CreateFromTask(SaveAsync);
        RemoveCommand                 = ReactiveCommand.CreateFromTask(RemoveAsync);
        RefreshAllCommand             = ReactiveCommand.CreateFromTask(RefreshAllAsync);
        RefreshSelectedCommand        = ReactiveCommand.CreateFromTask(RefreshSelectedAsync);
        SearchLocationsCommand        = ReactiveCommand.CreateFromTask(SearchLocationsAsync);
        UseSelectedLocationCommand    = ReactiveCommand.Create(UseSelectedLocation);
        SaveDefaultsCommand           = ReactiveCommand.CreateFromTask(SaveDefaultsAsync);
        RecalculateBuildCostsCommand  = ReactiveCommand.CreateFromTask(RecalculateBuildCostsAsync);

        // Auto-resolve location name 600 ms after the user stops typing an ID.
        this.WhenAnyValue(x => x.Selected)
            .Select(s => s is null
                ? Observable.Return("")
                : s.WhenAnyValue(x => x.LocationIdText))
            .Switch()
            .Throttle(TimeSpan.FromMilliseconds(600))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(id => { _ = LookupLocationAsync(); });

        _ = LoadAsync();
    }

    // Re-runs the initial load. Used to recover from the first-run case where this VM
    // loaded before the SDE finished importing, leaving region dropdowns unresolved.
    public Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        // By the name shown, so the picker waits for the interface language's names first.
        await SdeNames.EnsureLoadedAsync();
        RegionOptions = (await _db.SdeRegions.AsNoTracking()
                .Where(r => !r.IsWormhole)
                .Select(r => new SdeRegionOption { RegionId = r.RegionId, Name = r.Name })
                .ToListAsync())
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        var rows = await _db.MarketPricingConfigs.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();
        var places = await MarketSourceNames.PlacesAsync(_db, rows.Select(r => r.LocationId));

        Configs.Clear();
        foreach (var row in rows)
            Configs.Add(ToVm(row, places));

        Selected = Configs.FirstOrDefault();

        await using (var fdb = _dbFactory.CreateDbContext())
        {
            var defaults = await fdb.MarketDefaultSettings.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == 1);
            if (defaults is not null)
            {
                SelectedAssetConfig         = defaults.AssetValueConfigId.HasValue
                    ? Configs.FirstOrDefault(c => c.Id == defaults.AssetValueConfigId.Value) : null;
                AssetValuePriceType         = defaults.AssetValuePriceType;
                SelectedManufacturingConfig = defaults.ManufacturingConfigId.HasValue
                    ? Configs.FirstOrDefault(c => c.Id == defaults.ManufacturingConfigId.Value) : null;
                ManufacturingPriceType      = defaults.ManufacturingPriceType;
                MissingPriceMarkupPct          = defaults.MissingPriceMarkupPct;
                FilterLowballBuyOrders         = defaults.FilterLowballBuyOrders;
                LowballBuyOrderThresholdPct    = defaults.LowballBuyOrderThresholdPct;
                PurchaseWhenCheaper            = defaults.PurchaseWhenCheaper;
                PurchaseThresholdPct           = defaults.PurchaseThresholdPct;
            }
        }
    }

    private async Task SaveDefaultsAsync()
    {
        try
        {
            int?    assetConfigId = SelectedAssetConfig?.Id;
            string  assetType     = AssetValuePriceType;
            int?    mfgConfigId   = SelectedManufacturingConfig?.Id;
            string  mfgType       = ManufacturingPriceType;
            decimal markup        = MissingPriceMarkupPct;
            int     filterLowball = FilterLowballBuyOrders ? 1 : 0;
            decimal lowballPct    = LowballBuyOrderThresholdPct;
            int     buyCheaper    = PurchaseWhenCheaper ? 1 : 0;
            decimal buyThreshold  = PurchaseThresholdPct;

            await using var fdb = _dbFactory.CreateDbContext();
            await fdb.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "MarketDefaultSettings"
                    ("Id", "AssetValueConfigId", "AssetValuePriceType",
                     "ManufacturingConfigId", "ManufacturingPriceType", "MissingPriceMarkupPct",
                     "FilterLowballBuyOrders", "LowballBuyOrderThresholdPct",
                     "PurchaseWhenCheaper", "PurchaseThresholdPct")
                VALUES
                    (1, {assetConfigId}, {assetType},
                     {mfgConfigId}, {mfgType}, {markup},
                     {filterLowball}, {lowballPct},
                     {buyCheaper}, {buyThreshold})
                ON CONFLICT("Id") DO UPDATE SET
                    "AssetValueConfigId"          = excluded."AssetValueConfigId",
                    "AssetValuePriceType"         = excluded."AssetValuePriceType",
                    "ManufacturingConfigId"        = excluded."ManufacturingConfigId",
                    "ManufacturingPriceType"       = excluded."ManufacturingPriceType",
                    "MissingPriceMarkupPct"        = excluded."MissingPriceMarkupPct",
                    "FilterLowballBuyOrders"       = excluded."FilterLowballBuyOrders",
                    "LowballBuyOrderThresholdPct"  = excluded."LowballBuyOrderThresholdPct",
                    "PurchaseWhenCheaper"          = excluded."PurchaseWhenCheaper",
                    "PurchaseThresholdPct"         = excluded."PurchaseThresholdPct"
                """);

            DefaultsStatus = SettingsText.Saved;
        }
        catch (Exception ex) { DefaultsStatus = string.Format(CommonText.ErrorWithMessage, ex.Message); }
    }

    private async Task RecalculateBuildCostsAsync()
    {
        if (_buildCostSvc == null) return;
        BuildCostStatus = SettingsText.MarketRecalculating;
        try
        {
            await _buildCostSvc.RunAfterMarketRefreshAsync();
            BuildCostStatus = _buildCostSvc.StatusText;
        }
        catch (Exception ex) { BuildCostStatus = string.Format(CommonText.ErrorWithMessage, ex.Message[..Math.Min(60, ex.Message.Length)]); }
    }

    private void RebuildCharacterOptions(IEnumerable<Character> characters)
    {
        CharacterOptions = characters
            .Select(c => new CharacterAuthOption { CharId = c.Id, Name = c.Name })
            .ToList();

        foreach (var vm in Configs)
        {
            if (vm.AuthCharId.HasValue && vm.SelectedAuthChar is null)
                vm.SelectedAuthChar = CharacterOptions.FirstOrDefault(o => o.CharId == vm.AuthCharId.Value);
        }
    }

    /// <param name="places">The English names of the stations and regions the sources read
    /// (<see cref="MarketSourceNames.PlacesAsync"/>), for the names the lists show.</param>
    private MarketPricingConfigVm ToVm(MarketPricingConfig c, IReadOnlyDictionary<long, string>? places = null)
    {
        var authChar  = c.AuthCharId.HasValue
            ? CharacterOptions.FirstOrDefault(o => o.CharId == c.AuthCharId.Value)
            : null;
        var regionOpt = c.Method == MarketMethod.EsiRegion
            ? RegionOptions.FirstOrDefault(r => r.RegionId == (int)c.LocationId)
            : null;

        var vm = new MarketPricingConfigVm
        {
            Id                   = c.Id,
            MethodKey            = c.Method,
            LocationIdText       = c.LocationId.ToString(),
            PriceType            = c.PriceType,
            AuthCharId           = c.AuthCharId,
            IsEnabled            = c.IsEnabled,
            LastRefreshedText    = c.LastRefreshed.HasValue
                ? c.LastRefreshed.Value.UtcDateTime.ToString("g") : SettingsText.Never,
            LastStatus           = c.LastStatus,
            StationFilter        = c.StationFilter,
            UsePercentileFilter  = c.UsePercentileFilter,
            PercentilePercent    = c.PercentilePercent,
        };
        // Set SelectedRegion after construction so its setter can fire, then restore the
        // saved LocationName (the setter overwrites it with the region name).
        vm.SelectedRegion    = regionOpt;
        vm.LocationName      = c.LocationName;
        vm.PlaceName         = places?.GetValueOrDefault(c.LocationId) ?? vm.PlaceName;
        vm.SelectedAuthChar  = authChar; // may be null if characters not loaded yet — AuthCharId preserved above
        return vm;
    }

    private async Task LoadStationFilterOptionsAsync(MarketPricingConfigVm? config)
    {
        _loadingStationOptions = true;
        StationFilterOptions.Clear();
        SelectedStationFilter = null;
        if (config is null || config.IsFuzzwork) { _loadingStationOptions = false; return; }

        var locationIds = await _db.MarketRawOrders.AsNoTracking()
            .Where(o => o.ConfigId == config.Id && !o.IsBuyOrder)
            .Select(o => o.LocationId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync();

        StationFilterOptions.Add(new StationFilterOption { LocationId = null, Name = SettingsText.MarketAllStations });

        var namedOptions = new List<StationFilterOption>();
        foreach (var locId in locationIds)
        {
            var station = await _db.SdeStations.AsNoTracking()
                .FirstOrDefaultAsync(s => s.StationId == (int)locId);
            namedOptions.Add(new StationFilterOption { LocationId = locId, Name = station?.Name ?? string.Format(SettingsText.MarketLocationFallback, locId) });
        }
        foreach (var opt in namedOptions.OrderBy(o => o.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            StationFilterOptions.Add(opt);

        SelectedStationFilter = StationFilterOptions.FirstOrDefault(o => o.LocationId == config.StationFilter)
                             ?? StationFilterOptions.FirstOrDefault();
        _loadingStationOptions = false;
    }

    private async Task AddAsync()
    {
        var config = new MarketPricingConfig
        {
            Method       = MarketMethod.EsiRegion,
            LocationName = SettingsText.MarketNewSourceName,
            LocationId   = 60003760,
            PriceType    = MarketPriceType.Midpoint,
            IsEnabled    = true,
            SortOrder    = Configs.Count,
            LastStatus   = "",
        };
        _db.MarketPricingConfigs.Add(config);
        await _db.SaveChangesAsync();

        var vm = ToVm(config);
        Configs.Add(vm);
        Selected = vm;
        Status = SettingsText.MarketSourceAdded;
    }

    private async Task SaveAsync()
    {
        if (Selected is null) return;

        var config = await _db.MarketPricingConfigs.FindAsync(Selected.Id);
        if (config is null) return;

        config.Method               = Selected.MethodKey;
        config.LocationName         = Selected.LocationName;
        config.LocationId           = long.TryParse(Selected.LocationIdText, out var lid) ? lid : 0;
        config.PriceType            = Selected.PriceType;
        config.AuthCharId           = Selected.AuthCharId;
        config.IsEnabled            = Selected.IsEnabled;
        config.StationFilter        = Selected.StationFilter;
        config.UsePercentileFilter  = Selected.UsePercentileFilter;
        config.PercentilePercent    = Selected.PercentilePercent;
        var shown                   = Selected.DisplayName;

        await _db.SaveChangesAsync();
        Status = string.Format(SettingsText.MarketSourceSaved, shown);
    }

    private async Task RemoveAsync()
    {
        if (Selected is null) return;

        var config = await _db.MarketPricingConfigs.FindAsync(Selected.Id);
        if (config is not null)
        {
            await _db.MarketItemPrices
                .Where(p => p.ConfigId == config.Id)
                .ExecuteDeleteAsync();
            _db.MarketPricingConfigs.Remove(config);
            await _db.SaveChangesAsync();
        }

        var removed = Selected;
        Configs.Remove(removed);
        Selected = Configs.FirstOrDefault();
        Status = SettingsText.MarketSourceRemoved;
    }

    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        Status = SettingsText.MarketRefreshingAll;
        try
        {
            await SaveAsync();
            await Task.Run(async () => await _svc.RefreshAllAsync());
            await LoadAsync();
            Status = SettingsText.MarketRefreshedAll;
        }
        catch (Exception ex) { Status = string.Format(CommonText.ErrorWithMessage, ex.Message); }
        finally { IsBusy = false; }
    }

    private async Task RefreshSelectedAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        Status = string.Format(SettingsText.MarketRefreshing, Selected.DisplayName);
        try
        {
            await SaveAsync();
            var refreshed = await Task.Run(async () => await _svc.RefreshConfigAsync(Selected.Id));

            var updated = await _db.MarketPricingConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == Selected.Id);
            if (updated is not null)
            {
                Selected.LastRefreshedText      = updated.LastRefreshed?.UtcDateTime.ToString("g") ?? SettingsText.Never;
                Selected.LastStatus             = updated.LastStatus;
                Selected.UsePercentileFilter    = updated.UsePercentileFilter;
                Selected.PercentilePercent      = updated.PercentilePercent;
            }
            await LoadStationFilterOptionsAsync(Selected);

            // ⚠️ By the outcome the service returns, not by the status starting with "OK": that
            // word is translated now, and stored in whichever language the refresh ran under.
            Status = refreshed
                ? string.Format(SettingsText.MarketRefreshComplete, Selected.LastRefreshedText, Selected.LastStatus)
                : string.Format(SettingsText.MarketRefreshFailed, Selected.LastStatus);
        }
        catch (Exception ex) { Status = string.Format(CommonText.ErrorWithMessage, ex.Message); }
        finally { IsBusy = false; }
    }

    // ── Location lookup ───────────────────────────────────────────────────────

    private async Task LookupLocationAsync()
    {
        if (Selected is null) return;
        if (Selected.IsEsiRegion) return;
        if (!long.TryParse(Selected.LocationIdText, out var id) || id <= 0)
        {
            Selected.ResolvedLocationName = SettingsText.MarketInvalidId;
            return;
        }

        Selected.IsResolvingLocation  = true;
        Selected.ResolvedLocationName = SettingsText.MarketResolving;
        try
        {
            if (id >= 1_000_000_000_000L)
            {
                // Player-owned structure — GET /universe/structures/{id}/ (auth required)
                if (!Selected.AuthCharId.HasValue)
                {
                    Selected.ResolvedLocationName = SettingsText.MarketSelectAuthChar;
                    return;
                }
                var detailResult = await _esiClient.GetStructureAsync(Selected.AuthCharId.Value, id);
                var resolvedName = detailResult.Data?.Name ?? SettingsText.MarketStructureNotFound;
                Selected.ResolvedLocationName = resolvedName;
            }
            else
            {
                // NPC station — query local SDE first (faster, no network). Shown in the interface
                // language; its English is what tells a name left as the station's own.
                var station = await _db.SdeStations.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.StationId == (int)id);
                if (station is not null)
                {
                    Selected.ResolvedLocationName = SdeNames.Station(id, station.Name);
                    Selected.PlaceName            = station.Name;
                }
                else
                {
                    // Fall back to ESI /universe/stations/{id}/ for stations not in SDE
                    var detail = await _esiClient.GetStationAsync(id);
                    Selected.ResolvedLocationName = detail?.Name ?? SettingsText.MarketStationNotFound;
                }
            }
        }
        catch (Exception ex) { Selected.ResolvedLocationName = string.Format(CommonText.ErrorWithMessage, ex.Message); }
        finally { Selected.IsResolvingLocation = false; }
    }

    private async Task SearchLocationsAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationSearch)) return;

        long? charId = Selected?.AuthCharId ?? CharacterOptions.FirstOrDefault()?.CharId;
        if (!charId.HasValue)
        {
            SearchStatus = SettingsText.MarketSearchNeedsChar;
            return;
        }

        SearchStatus = SettingsText.MarketSearching;
        LocationResults.Clear();
        try
        {
            var result = await _esiClient.SearchLocationsAsync(charId.Value, LocationSearch);
            var found  = new List<LocationResult>();

            // Resolve station IDs from local SDE — with the stations whose name in the interface
            // language holds the text, since ESI searches the English names only.
            var ids = (result?.Station ?? []).Concat(SdeNames.Find(SdeNameKind.Station, LocationSearch.Trim()))
                .Select(i => (int)i).Distinct().ToList();
            if (ids.Count > 0)
            {
                var stations = await _db.SdeStations.AsNoTracking()
                    .Where(s => ids.Contains(s.StationId))
                    .ToListAsync();
                found.AddRange(stations.Select(s => new LocationResult(s.StationId, s.Name, SettingsText.MarketResultStation)));
            }

            // Resolve structure IDs via ESI — fetch up to 100, parallelized with a cap of 10 concurrent.
            if (result?.Structure?.Count > 0)
            {
                var structIds = result.Structure.Take(100).ToList();
                var sem       = new SemaphoreSlim(10, 10);
                var tasks     = structIds.Select(async sid =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var detailResult = await _esiClient.GetStructureAsync(charId.Value, sid);
                        return detailResult.Data is not null
                            ? new LocationResult(sid, detailResult.Data.Name, SettingsText.MarketResultStructure)
                            : new LocationResult(sid, string.Format(SettingsText.MarketStructureFallback, sid), SettingsText.MarketResultStructure);
                    }
                    finally { sem.Release(); }
                });
                found.AddRange(await Task.WhenAll(tasks));
            }

            // By the name shown.
            found.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));

            foreach (var r in found)
                LocationResults.Add(r);

            SearchStatus = found.Count == 0 ? SettingsText.MarketNoResults : string.Format(SettingsText.MarketResultCount, found.Count);
        }
        catch (Exception ex) { SearchStatus = string.Format(CommonText.ErrorWithMessage, ex.Message); }
    }

    private void UseSelectedLocation()
    {
        if (Selected is null || SelectedLocationResult is null) return;
        Selected.LocationIdText       = SelectedLocationResult.Id.ToString();
        Selected.LocationName         = SelectedLocationResult.Name;          // English: stored
        Selected.ResolvedLocationName = SelectedLocationResult.DisplayName;
        SelectedLocationResult        = null;
        LocationResults.Clear();
        LocationSearch = "";
        SearchStatus   = SettingsText.MarketLocationApplied;
    }
}
