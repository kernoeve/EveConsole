using System.Collections.ObjectModel;
using System.Reactive;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.Data.Sqlite;
using ReactiveUI;

namespace EveCortex.ViewModels;

// Which market number to compare the build cost against.
public enum IndustryMode { BuildAndSellOrder, BuildAndSellToBuyOrder }
public record IndustryModeOption(string Label, IndustryMode Kind);

// A market pricing config the user can price against (reuses the Market Sources configs).
public record IndustryMarketConfig(int ConfigId, string Name, string Method, long LocationId);

public class IndustryRow
{
    public int    TypeId           { get; init; }
    public string TypeName         { get; init; } = "";
    public double BuildCost        { get; init; }
    public double SellPrice        { get; init; }   // market number we sell into (sell or buy order)
    public bool   HasSellOrders    { get; init; } = true; // false → priced from 30-day history avg
    public double ProfitPerUnit    { get; init; }
    public double Margin           { get; init; }   // profit / build cost
    public double BuildSeconds     { get; init; }   // to build ONE unit (ties up the slot this long)
    public double SlotDays         { get; init; }   // BuildSeconds / 86400
    public double ProfitPerSlotDay { get; init; }

    public string BuildCostDisplay     => FormatIsk(BuildCost);
    // "*" marks a sell price derived from 30-day history because there are no sell orders.
    public string SellPriceDisplay     => HasSellOrders ? FormatIsk(SellPrice) : FormatIsk(SellPrice) + " *";
    public string ProfitUnitDisplay    => FormatIsk(ProfitPerUnit);
    public string MarginDisplay        => $"{Margin * 100:N1}%";
    public string BuildTimeDisplay      => FormatDuration(BuildSeconds);
    public string SlotDaysDisplay       => $"{SlotDays:N2}";
    public string ProfitPerSlotDayDisplay => FormatIsk(ProfitPerSlotDay);

    private static string FormatDuration(double seconds)
    {
        if (seconds <= 0) return "–";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalDays  >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m";
        return $"{(int)ts.TotalSeconds}s";
    }

    private static string FormatIsk(double v)
    {
        var abs = Math.Abs(v);
        var sign = v < 0 ? "-" : "";
        return abs switch
        {
            >= 1_000_000_000_000 => $"{sign}{abs / 1_000_000_000_000:N2}T",
            >= 1_000_000_000     => $"{sign}{abs / 1_000_000_000:N2}B",
            >= 1_000_000         => $"{sign}{abs / 1_000_000:N2}M",
            _                    => $"{sign}{abs:N2}",
        };
    }
}

public class IndustryOpportunitiesViewModel : ReactiveObject
{
    private readonly string               _connString;
    private readonly MarketHistoryService _historyService;
    private readonly BatchAddService      _batchSvc;

    // Per-unit build time is computed and cached in BuildCosts.BuildSeconds by
    // BuildCostService (using the default park's blueprint TE, skills, and structure
    // role/rig time bonuses) — we just read it here and convert to slot-days.
    private const double SecondsPerDay = 86_400.0;

    // ── Mode ──────────────────────────────────────────────────────────────────

    public List<IndustryModeOption> ModeOptions { get; } =
    [
        new("Build & Sell Order",         IndustryMode.BuildAndSellOrder),
        new("Build & Sell to Buy Order",  IndustryMode.BuildAndSellToBuyOrder),
    ];

    private IndustryModeOption _selectedMode;
    public IndustryModeOption SelectedMode
    {
        get => _selectedMode;
        set => this.RaiseAndSetIfChanged(ref _selectedMode, value);
    }

    // ── Market config (pricing source) ────────────────────────────────────────

    public ObservableCollection<IndustryMarketConfig> MarketConfigs { get; } = [];

    private IndustryMarketConfig? _selectedConfig;
    public IndustryMarketConfig? SelectedConfig
    {
        get => _selectedConfig;
        set => this.RaiseAndSetIfChanged(ref _selectedConfig, value);
    }

    // ── Filters ────────────────────────────────────────────────────────────────

    private string _minIskVolume = "";
    public string MinIskVolume
    {
        get => _minIskVolume;
        set => this.RaiseAndSetIfChanged(ref _minIskVolume, value);
    }

    private string _minUnitVolume = "";
    public string MinUnitVolume
    {
        get => _minUnitVolume;
        set => this.RaiseAndSetIfChanged(ref _minUnitVolume, value);
    }

    // Faction items (MetaGroupId = 4) are ME0 BPCs that are often not worth building.
    private bool _skipFactionItems;
    public bool SkipFactionItems
    {
        get => _skipFactionItems;
        set => this.RaiseAndSetIfChanged(ref _skipFactionItems, value);
    }

    // ── Excluded market groups (and everything nested under them) ────────────

    public ObservableCollection<ExcludedMarketGroupVm> ExcludedMarketGroups { get; } = [];

    public Func<Task<MarketGroupPickerResult?>>? ShowMarketGroupPickerDialog { get; set; }

    public BatchAddService GetBatchAddService() => _batchSvc;

    public ReactiveCommand<Unit, Unit>                  AddExcludedGroupCommand    { get; }
    public ReactiveCommand<ExcludedMarketGroupVm, Unit> RemoveExcludedGroupCommand { get; }

    private async Task AddExcludedGroupAsync()
    {
        if (ShowMarketGroupPickerDialog is null) return;
        var pick = await ShowMarketGroupPickerDialog();
        if (pick is null) return;
        if (ExcludedMarketGroups.Any(g => g.MarketGroupId == pick.MarketGroupId)) return;

        ExcludedMarketGroups.Add(new ExcludedMarketGroupVm(pick.MarketGroupId, pick.GroupName));
        await SaveExcludedGroupsAsync();
    }

    private async Task RemoveExcludedGroupAsync(ExcludedMarketGroupVm group)
    {
        ExcludedMarketGroups.Remove(group);
        await SaveExcludedGroupsAsync();
    }

    private async Task LoadExcludedGroupsAsync()
    {
        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "ExcludedMarketGroupIds" FROM "IndustryOpportunitiesSettings" WHERE "Id" = 1""";
        var raw = (await cmd.ExecuteScalarAsync()) as string ?? "";

        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        if (ids.Count == 0) return;

        using var nameCmd = conn.CreateCommand();
        nameCmd.CommandText = $"""SELECT "MarketGroupId", "Name" FROM "SdeMarketGroups" WHERE "MarketGroupId" IN ({string.Join(",", ids)})""";
        var names = new Dictionary<int, string>();
        using (var reader = await nameCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                names[reader.GetInt32(0)] = reader.GetString(1);

        ExcludedMarketGroups.Clear();
        foreach (var id in ids)
            if (names.TryGetValue(id, out var name))
                ExcludedMarketGroups.Add(new ExcludedMarketGroupVm(id, name));
    }

    private async Task SaveExcludedGroupsAsync()
    {
        var csv = string.Join(",", ExcludedMarketGroups.Select(g => g.MarketGroupId));
        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """UPDATE "IndustryOpportunitiesSettings" SET "ExcludedMarketGroupIds" = @ids WHERE "Id" = 1""";
        cmd.Parameters.AddWithValue("@ids", csv);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<HashSet<int>> GetExcludedGroupIdsRecursiveAsync()
    {
        var result = new HashSet<int>();
        foreach (var g in ExcludedMarketGroups)
            result.UnionWith(await _batchSvc.GetDescendantGroupIdsAsync(g.MarketGroupId));
        return result;
    }

    // ── Item navigation callback (set by MainWindow) ──────────────────────────

    public Action<int, string>? ItemNavigationRequested { get; set; }

    public void RequestItemNavigation(int typeId, string typeName)
        => ItemNavigationRequested?.Invoke(typeId, typeName);

    // ── Results ───────────────────────────────────────────────────────────────

    public ObservableCollection<IndustryRow> Results { get; } = [];

    private string _statusText = "Select a market config, then click Calculate.";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isCalculating;
    public bool IsCalculating { get => _isCalculating; private set => this.RaiseAndSetIfChanged(ref _isCalculating, value); }

    // ── Command ───────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> CalculateCommand { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public IndustryOpportunitiesViewModel(string connString, MarketHistoryService historyService, BatchAddService batchSvc)
    {
        _connString     = connString;
        _historyService = historyService;
        _batchSvc       = batchSvc;
        _selectedMode   = ModeOptions[0];
        CalculateCommand           = ReactiveCommand.CreateFromTask(CalculateAsync);
        AddExcludedGroupCommand    = ReactiveCommand.CreateFromTask(AddExcludedGroupAsync);
        RemoveExcludedGroupCommand = ReactiveCommand.CreateFromTask<ExcludedMarketGroupVm>(RemoveExcludedGroupAsync);
    }

    public async Task InitializeAsync()
    {
        await LoadMarketConfigsAsync();
        await LoadExcludedGroupsAsync();
    }

    // ── Market config loading ─────────────────────────────────────────────────

    private async Task LoadMarketConfigsAsync()
    {
        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT "Id", "LocationName", "Method", "LocationId"
            FROM "MarketPricingConfigs"
            WHERE "IsEnabled" = 1
            ORDER BY "SortOrder"
            """;

        MarketConfigs.Clear();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            MarketConfigs.Add(new IndustryMarketConfig(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }
        _selectedConfig ??= MarketConfigs.FirstOrDefault();
        this.RaisePropertyChanged(nameof(SelectedConfig));
    }

    // ── Calculate ─────────────────────────────────────────────────────────────

    private async Task CalculateAsync()
    {
        if (SelectedConfig is null)
        {
            StatusText = "Please select a market config for pricing.";
            return;
        }

        double? minIskVol = null;
        if (!string.IsNullOrWhiteSpace(MinIskVolume))
        {
            if (!double.TryParse(MinIskVolume, out var mv) || mv < 0)
            {
                StatusText = "Please enter a valid minimum ISK volume (or leave blank for no filter).";
                return;
            }
            minIskVol = mv;
        }

        double? minUnitVol = null;
        if (!string.IsNullOrWhiteSpace(MinUnitVolume))
        {
            if (!double.TryParse(MinUnitVolume, out var uv) || uv < 0)
            {
                StatusText = "Please enter a valid minimum unit volume (or leave blank for no filter).";
                return;
            }
            minUnitVol = uv;
        }

        Results.Clear();
        StatusText = "Calculating…";
        IsCalculating = true;

        try
        {
            var candidates = await FetchCandidatesAsync(SelectedConfig.ConfigId);

            // Region is needed for the volume filters AND to price items that have no
            // sell orders off their 30-day history average. Resolve it best-effort.
            int?   regionId   = await ResolveRegionAsync(SelectedConfig);
            string regionName = regionId.HasValue ? await GetRegionNameAsync(regionId.Value) : "unresolved";
            bool needsVolume = minIskVol.HasValue || minUnitVol.HasValue;
            if (needsVolume && !regionId.HasValue)
            {
                StatusText = "Could not resolve this market config's region — " +
                             "the 30-day volume filters need a region to look up market history.";
                return;
            }

            var (typesWithSell, typesWithBuy, configHasRawOrders) =
                await LoadOrderPresenceAsync(SelectedConfig.ConfigId);

            var rows = await BuildRowsAsync(candidates, regionId, minIskVol, minUnitVol,
                                            typesWithSell, typesWithBuy, configHasRawOrders);
            // Default display order — best profit-per-slot-day first. Headers allow re-sorting.
            foreach (var r in rows.OrderByDescending(r => r.ProfitPerSlotDay)) Results.Add(r);

            int noSell = rows.Count(r => !r.HasSellOrders);
            var note   = noSell > 0 ? $"  ·  * {noSell} priced from 30-day avg (no sell orders)" : "";
            // Show the volume region so it's clear the 30-day filters use the Price At region.
            var volNote = needsVolume ? $" · 30d volume region: {regionName}" : "";
            StatusText = rows.Count > 0
                ? $"{rows.Count} profitable item{(rows.Count == 1 ? "" : "s")} · priced at {SelectedConfig.Name}{volNote}{note}"
                : "No profitable build opportunities found for this market config.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsCalculating = false;
        }
    }

    // ── Core algorithm ────────────────────────────────────────────────────────

    private record Candidate(
        int TypeId, string TypeName,
        double BuildCost, double SellOrderPrice, double BuyOrderPrice,
        double BuildSeconds);

    private async Task<List<Candidate>> FetchCandidatesAsync(int configId)
    {
        var excludedGroupIds = await GetExcludedGroupIdsRecursiveAsync();
        var exclusionClause  = excludedGroupIds.Count > 0
            ? $"""AND (t."MarketGroupId" IS NULL OR t."MarketGroupId" NOT IN ({string.Join(",", excludedGroupIds)})) """
            : "";

        // Faction items are MetaGroupId 4.
        var factionClause = SkipFactionItems
            ? """AND (t."MetaGroupId" IS NULL OR t."MetaGroupId" != 4) """
            : "";

        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = CandidateSql
            .Replace("/*EXCLUSION*/", exclusionClause)
            .Replace("/*FACTION*/", factionClause);
        cmd.Parameters.AddWithValue("@configId", configId);

        var list = new List<Candidate>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new Candidate(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5)));
        }
        return list;
    }

    // Which types have live buy/sell orders for this config (from the raw order snapshot),
    // and whether the config has any raw orders at all (Fuzzwork configs have none — for
    // those we trust the stored prices and treat every item as having sell orders).
    private async Task<(HashSet<int> WithSell, HashSet<int> WithBuy, bool HasRawOrders)>
        LoadOrderPresenceAsync(int configId)
    {
        var withSell = new HashSet<int>();
        var withBuy  = new HashSet<int>();

        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT "TypeId", "IsBuyOrder"
            FROM "MarketRawOrders"
            WHERE "ConfigId" = @configId
            """;
        cmd.Parameters.AddWithValue("@configId", configId);
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            int typeId = reader.GetInt32(0);
            if (reader.GetBoolean(1)) withBuy.Add(typeId);
            else                      withSell.Add(typeId);
        }
        return (withSell, withBuy, withSell.Count > 0 || withBuy.Count > 0);
    }

    private async Task<List<IndustryRow>> BuildRowsAsync(
        List<Candidate> candidates, int? regionId, double? minIskVol30d, double? minUnitVol30d,
        HashSet<int> typesWithSell, HashSet<int> typesWithBuy, bool configHasRawOrders)
    {
        var result = new List<IndustryRow>();

        foreach (var c in candidates)
        {
            if (c.BuildCost <= 0) continue;

            double sellInto;
            bool   hasSellOrders = true;

            if (SelectedMode.Kind == IndustryMode.BuildAndSellToBuyOrder)
            {
                // No buy orders for this item → nobody to sell to → skip.
                if (configHasRawOrders && !typesWithBuy.Contains(c.TypeId)) continue;
                sellInto = c.BuyOrderPrice;
            }
            else
            {
                bool sellOrders = !configHasRawOrders || typesWithSell.Contains(c.TypeId);
                if (sellOrders)
                {
                    sellInto = c.SellOrderPrice; // real lowest sell
                }
                else
                {
                    // No sell orders — these are often the most lucrative if in demand.
                    // Price them off the 30-day history average (what they actually trade
                    // at) rather than the build-cost gap-fill, and flag them with a "*".
                    sellInto = regionId.HasValue
                        ? await _historyService.Get30DayAveragePriceAsync(regionId.Value, c.TypeId)
                        : 0;
                    hasSellOrders = false;
                }
            }

            if (sellInto <= 0) continue;

            double profit = sellInto - c.BuildCost;
            if (profit <= 0) continue; // only surface opportunities

            // 30-day volume filters read history cached by the background sweep — no ESI here.
            if ((minIskVol30d.HasValue || minUnitVol30d.HasValue) && regionId.HasValue)
            {
                if (minIskVol30d.HasValue)
                {
                    var iskVol = await _historyService.Get30DayIskVolumeAsync(regionId.Value, c.TypeId);
                    if (iskVol < minIskVol30d.Value) continue;
                }
                if (minUnitVol30d.HasValue)
                {
                    var unitVol = await _historyService.Get30DayUnitVolumeAsync(regionId.Value, c.TypeId);
                    if (unitVol < minUnitVol30d.Value) continue;
                }
            }

            double slotDays = c.BuildSeconds / SecondsPerDay;

            result.Add(new IndustryRow
            {
                TypeId           = c.TypeId,
                TypeName         = c.TypeName,
                BuildCost        = c.BuildCost,
                SellPrice        = sellInto,
                HasSellOrders    = hasSellOrders,
                ProfitPerUnit    = profit,
                Margin           = profit / c.BuildCost,
                BuildSeconds     = c.BuildSeconds,
                SlotDays         = slotDays,
                ProfitPerSlotDay = slotDays > 0 ? profit / slotDays : 0,
            });
        }

        return result;
    }

    private async Task<string> GetRegionNameAsync(int regionId)
    {
        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Name" FROM "SdeRegions" WHERE "RegionId" = @id""";
        cmd.Parameters.AddWithValue("@id", regionId);
        return (await cmd.ExecuteScalarAsync()) as string ?? $"Region {regionId}";
    }

    // Resolves the region id used for the 30-day volume lookups from a market config.
    private async Task<int?> ResolveRegionAsync(IndustryMarketConfig cfg)
    {
        // ESI Region configs store the region id directly in LocationId.
        if (cfg.Method == MarketMethod.EsiRegion) return (int)cfg.LocationId;

        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();

        // Maybe LocationId is already a region id (Fuzzwork region configs).
        cmd.CommandText = """SELECT "RegionId" FROM "SdeRegions" WHERE "RegionId" = @loc""";
        cmd.Parameters.AddWithValue("@loc", cfg.LocationId);
        var region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // NPC station: resolve via its solar system. (SdeStations.RegionId is not
        // populated by the SDE import — join through SolarSystemId instead.)
        cmd.CommandText = """
            SELECT ss."RegionId"
            FROM "SdeStations"     s
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = s."SolarSystemId"
            WHERE s."StationId" = @sid AND s."SolarSystemId" != 0
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@sid", (int)Math.Min(cfg.LocationId, int.MaxValue));
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Player structure: resolved name record already has SolarSystemId.
        cmd.CommandText = """
            SELECT ss."RegionId"
            FROM "EsiStructureNames" sn
            JOIN "SdeSolarSystems"   ss ON ss."SolarSystemId" = sn."SolarSystemId"
            WHERE sn."StructureId" = @lid AND sn."SolarSystemId" != 0
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@lid", cfg.LocationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Fallback: derive from any cached order at that location.
        cmd.CommandText = """
            SELECT ss."RegionId"
            FROM "MarketRawOrders" o
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = o."SystemId"
            WHERE o."LocationId" = @lid AND o."SystemId" != 0
            LIMIT 1
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@lid", cfg.LocationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        return region;
    }

    // Treats NULL/DBNull and a 0 region id as "unresolved" so callers fall through.
    private static int? ToRegionId(object? scalar)
    {
        if (scalar is null or DBNull) return null;
        var id = Convert.ToInt32(scalar);
        return id != 0 ? id : null;
    }

    // ── SQL ───────────────────────────────────────────────────────────────────

    // Joins cached build cost + build time (from BuildCostService) with market prices
    // for the chosen config. One BuildCosts row per item (TypeId is PK), so no dedupe.
    private const string CandidateSql = """
        SELECT
            bc."TypeId",
            bc."TypeName",
            CAST(bc."TotalCost"    AS REAL)   AS BuildCost,
            CAST(mip."SellPrice"   AS REAL)   AS SellPrice,
            CAST(mip."BuyPrice"    AS REAL)   AS BuyPrice,
            CAST(bc."BuildSeconds" AS REAL)   AS BuildSeconds
        FROM "BuildCosts" bc
        JOIN "MarketItemPrices" mip
              ON mip."TypeId" = bc."TypeId" AND mip."ConfigId" = @configId
        JOIN "SdeTypes" t ON t."TypeId" = bc."TypeId"
        WHERE bc."TotalCost" > 0 AND bc."BuildSeconds" > 0
        /*EXCLUSION*/
        /*FACTION*/
        """;
}
