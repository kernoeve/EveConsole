using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class CharacterAuthOption
{
    public long   CharId { get; init; }
    public string Name   { get; init; } = "";
    public override string ToString() => Name;
}

public record LocationResult(long Id, string Name, string Category);

public class StationFilterOption
{
    public long?  LocationId { get; init; }
    public string Name       { get; init; } = "";
    public override string ToString() => Name;
}

public class SdeRegionOption
{
    public SdeRegionOption() { }
    public SdeRegionOption(int regionId, string name) { RegionId = regionId; Name = name; }
    public int    RegionId { get; init; }
    public string Name     { get; init; } = "";
    public override string ToString() => Name;
}

public class MarketPricingConfigVm : ReactiveObject
{
    public int Id { get; init; }

    private string _method = MarketMethod.EsiRegion;
    public string Method
    {
        get => _method;
        set
        {
            this.RaiseAndSetIfChanged(ref _method, value);
            this.RaisePropertyChanged(nameof(IsFuzzwork));
            this.RaisePropertyChanged(nameof(IsEsiRegion));
            this.RaisePropertyChanged(nameof(IsPlayerStructure));
            this.RaisePropertyChanged(nameof(MethodBadge));
        }
    }

    private string _locationName = "";
    public string LocationName
    {
        get => _locationName;
        set => this.RaiseAndSetIfChanged(ref _locationName, value);
    }

    private string _locationIdText = "";
    public string LocationIdText
    {
        get => _locationIdText;
        set
        {
            this.RaiseAndSetIfChanged(ref _locationIdText, value);
            ResolvedLocationName = "";
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
            LocationName = value.Name;
        }
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    private string _lastRefreshedText = "Never";
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
    public IReadOnlyList<string>              Methods    { get; }
        = [MarketMethod.EsiRegion, MarketMethod.PlayerStructure];
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
        RegionOptions = await _db.SdeRegions.AsNoTracking()
            .Where(r => !r.IsWormhole)
            .OrderBy(r => r.Name)
            .Select(r => new SdeRegionOption { RegionId = r.RegionId, Name = r.Name })
            .ToListAsync();

        var rows = await _db.MarketPricingConfigs.AsNoTracking()
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync();

        Configs.Clear();
        foreach (var row in rows)
            Configs.Add(ToVm(row));

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

            await using var fdb = _dbFactory.CreateDbContext();
            await fdb.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO MarketDefaultSettings
                    (Id, AssetValueConfigId, AssetValuePriceType,
                     ManufacturingConfigId, ManufacturingPriceType, MissingPriceMarkupPct,
                     FilterLowballBuyOrders, LowballBuyOrderThresholdPct)
                VALUES
                    (1, {assetConfigId}, {assetType},
                     {mfgConfigId}, {mfgType}, {markup},
                     {filterLowball}, {lowballPct})
                ON CONFLICT(Id) DO UPDATE SET
                    AssetValueConfigId          = excluded.AssetValueConfigId,
                    AssetValuePriceType         = excluded.AssetValuePriceType,
                    ManufacturingConfigId        = excluded.ManufacturingConfigId,
                    ManufacturingPriceType       = excluded.ManufacturingPriceType,
                    MissingPriceMarkupPct        = excluded.MissingPriceMarkupPct,
                    FilterLowballBuyOrders       = excluded.FilterLowballBuyOrders,
                    LowballBuyOrderThresholdPct  = excluded.LowballBuyOrderThresholdPct
                """);

            DefaultsStatus = "Saved.";
        }
        catch (Exception ex) { DefaultsStatus = $"Error: {ex.Message}"; }
    }

    private async Task RecalculateBuildCostsAsync()
    {
        if (_buildCostSvc == null) return;
        BuildCostStatus = "Recalculating…";
        try
        {
            await _buildCostSvc.RunAfterMarketRefreshAsync();
            BuildCostStatus = _buildCostSvc.StatusText;
        }
        catch (Exception ex) { BuildCostStatus = $"Error: {ex.Message[..Math.Min(60, ex.Message.Length)]}"; }
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

    private MarketPricingConfigVm ToVm(MarketPricingConfig c)
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
            Method               = c.Method,
            LocationIdText       = c.LocationId.ToString(),
            PriceType            = c.PriceType,
            AuthCharId           = c.AuthCharId,
            IsEnabled            = c.IsEnabled,
            LastRefreshedText    = c.LastRefreshed.HasValue
                ? c.LastRefreshed.Value.UtcDateTime.ToString("g") : "Never",
            LastStatus           = c.LastStatus,
            StationFilter        = c.StationFilter,
            UsePercentileFilter  = c.UsePercentileFilter,
            PercentilePercent    = c.PercentilePercent,
        };
        // Set SelectedRegion after construction so its setter can fire, then restore the
        // saved LocationName (the setter overwrites it with the region name).
        vm.SelectedRegion    = regionOpt;
        vm.LocationName      = c.LocationName;
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

        StationFilterOptions.Add(new StationFilterOption { LocationId = null, Name = "(All stations)" });

        var namedOptions = new List<StationFilterOption>();
        foreach (var locId in locationIds)
        {
            var station = await _db.SdeStations.AsNoTracking()
                .FirstOrDefaultAsync(s => s.StationId == (int)locId);
            namedOptions.Add(new StationFilterOption { LocationId = locId, Name = station?.Name ?? $"Location {locId}" });
        }
        foreach (var opt in namedOptions.OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
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
            LocationName = "New Market",
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
        Status = "New source added — fill in details and click Save.";
    }

    private async Task SaveAsync()
    {
        if (Selected is null) return;

        var config = await _db.MarketPricingConfigs.FindAsync(Selected.Id);
        if (config is null) return;

        config.Method               = Selected.Method;
        config.LocationName         = Selected.LocationName;
        config.LocationId           = long.TryParse(Selected.LocationIdText, out var lid) ? lid : 0;
        config.PriceType            = Selected.PriceType;
        config.AuthCharId           = Selected.AuthCharId;
        config.IsEnabled            = Selected.IsEnabled;
        config.StationFilter        = Selected.StationFilter;
        config.UsePercentileFilter  = Selected.UsePercentileFilter;
        config.PercentilePercent    = Selected.PercentilePercent;

        await _db.SaveChangesAsync();
        Status = $"Saved \"{config.LocationName}\".";
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
        Status = "Source removed.";
    }

    private async Task RefreshAllAsync()
    {
        IsBusy = true;
        Status = "Refreshing all sources…";
        try
        {
            await SaveAsync();
            await Task.Run(async () => await _svc.RefreshAllAsync());
            await LoadAsync();
            Status = "All sources refreshed.";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task RefreshSelectedAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        Status = $"Refreshing {Selected.LocationName}…";
        try
        {
            await SaveAsync();
            await Task.Run(async () => await _svc.RefreshConfigAsync(Selected.Id));

            var updated = await _db.MarketPricingConfigs.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == Selected.Id);
            if (updated is not null)
            {
                Selected.LastRefreshedText      = updated.LastRefreshed?.UtcDateTime.ToString("g") ?? "Never";
                Selected.LastStatus             = updated.LastStatus;
                Selected.UsePercentileFilter    = updated.UsePercentileFilter;
                Selected.PercentilePercent      = updated.PercentilePercent;
            }
            await LoadStationFilterOptionsAsync(Selected);

            Status = Selected.LastStatus.StartsWith("OK")
                ? $"Refresh complete — {Selected.LastRefreshedText} — {Selected.LastStatus}"
                : $"Refresh failed: {Selected.LastStatus}";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── Location lookup ───────────────────────────────────────────────────────

    private async Task LookupLocationAsync()
    {
        if (Selected is null) return;
        if (Selected.IsEsiRegion) return;
        if (!long.TryParse(Selected.LocationIdText, out var id) || id <= 0)
        {
            Selected.ResolvedLocationName = "Invalid ID";
            return;
        }

        Selected.IsResolvingLocation  = true;
        Selected.ResolvedLocationName = "Resolving…";
        try
        {
            if (id >= 1_000_000_000_000L)
            {
                // Player-owned structure — GET /universe/structures/{id}/ (auth required)
                if (!Selected.AuthCharId.HasValue)
                {
                    Selected.ResolvedLocationName = "Select an Auth Character to look up structures";
                    return;
                }
                var detailResult = await _esiClient.GetStructureAsync(Selected.AuthCharId.Value, id);
                var resolvedName = detailResult.Data?.Name ?? "Structure not found (check auth)";
                Selected.ResolvedLocationName = resolvedName;
            }
            else
            {
                // NPC station — query local SDE first (faster, no network)
                var station = await _db.SdeStations.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.StationId == (int)id);
                if (station is not null)
                {
                    Selected.ResolvedLocationName = station.Name;
                }
                else
                {
                    // Fall back to ESI /universe/stations/{id}/ for stations not in SDE
                    var detail = await _esiClient.GetStationAsync(id);
                    Selected.ResolvedLocationName = detail?.Name ?? "Station not found";
                }
            }
        }
        catch (Exception ex) { Selected.ResolvedLocationName = $"Error: {ex.Message}"; }
        finally { Selected.IsResolvingLocation = false; }
    }

    private async Task SearchLocationsAsync()
    {
        if (string.IsNullOrWhiteSpace(LocationSearch)) return;

        long? charId = Selected?.AuthCharId ?? CharacterOptions.FirstOrDefault()?.CharId;
        if (!charId.HasValue)
        {
            SearchStatus = "Add an auth character to search";
            return;
        }

        SearchStatus = "Searching…";
        LocationResults.Clear();
        try
        {
            var result = await _esiClient.SearchLocationsAsync(charId.Value, LocationSearch);
            var found  = new List<LocationResult>();

            // Resolve station IDs from local SDE
            if (result?.Station?.Count > 0)
            {
                var ids      = result.Station.Select(i => (int)i).ToList();
                var stations = await _db.SdeStations.AsNoTracking()
                    .Where(s => ids.Contains(s.StationId))
                    .OrderBy(s => s.Name)
                    .ToListAsync();
                found.AddRange(stations.Select(s => new LocationResult(s.StationId, s.Name, "Station")));
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
                            ? new LocationResult(sid, detailResult.Data.Name, "Structure")
                            : new LocationResult(sid, $"Structure {sid}", "Structure");
                    }
                    finally { sem.Release(); }
                });
                found.AddRange(await Task.WhenAll(tasks));
                found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var r in found)
                LocationResults.Add(r);

            SearchStatus = found.Count == 0 ? "No results found" : $"{found.Count} result(s)";
        }
        catch (Exception ex) { SearchStatus = $"Error: {ex.Message}"; }
    }

    private void UseSelectedLocation()
    {
        if (Selected is null || SelectedLocationResult is null) return;
        Selected.LocationIdText       = SelectedLocationResult.Id.ToString();
        Selected.LocationName         = SelectedLocationResult.Name;
        Selected.ResolvedLocationName = SelectedLocationResult.Name;
        SelectedLocationResult        = null;
        LocationResults.Clear();
        LocationSearch = "";
        SearchStatus   = "Location applied — click Save to persist.";
    }
}
