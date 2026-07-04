using System.Collections.ObjectModel;
using System.Reactive;
using EveCortex.Services;
using Microsoft.Data.Sqlite;
using ReactiveUI;

namespace EveCortex.ViewModels;

public record StationOption(long LocationId, string Name);

public enum TradeMode { SellToBuyOrder, UndercutSellOrder }
public record TradeModeOption(string Label, TradeMode Kind);

public record ExcludedMarketGroupVm(int MarketGroupId, string Name);

public class TradeRow
{
    public int    TypeId        { get; init; }
    public string TypeName      { get; init; } = "";
    public double BestSell      { get; init; }
    public double DestPrice     { get; init; }   // buy order price OR cheapest dest sell
    public double ProfitPerUnit { get; init; }
    public double M3PerUnit     { get; init; }
    public double ProfitPerM3   { get; init; }
    public long   Quantity      { get; init; }
    public double TotalVolume   { get; init; }
    public double TotalCost     { get; init; }
    public double TotalProfit   { get; init; }

    public string BestSellDisplay    => FormatIsk(BestSell);
    public string DestPriceDisplay   => FormatIsk(DestPrice);
    public string ProfitUnitDisplay  => FormatIsk(ProfitPerUnit);
    public string ProfitM3Display    => $"{ProfitPerM3:N2}";
    public string QuantityDisplay    => $"{Quantity:N0}";
    public string TotalVolumeDisplay => $"{TotalVolume:N1}";
    public string TotalCostDisplay   => FormatIsk(TotalCost);
    public string TotalProfitDisplay => FormatIsk(TotalProfit);

    private static string FormatIsk(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:N2}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:N2}B",
        >= 1_000_000         => $"{v / 1_000_000:N2}M",
        _                    => $"{v:N2}",
    };
}

public class TradeOpportunitiesViewModel : ReactiveObject
{
    private readonly string               _connString;
    private readonly MarketHistoryService _historyService;
    private readonly BatchAddService      _batchSvc;

    // ── Mode ──────────────────────────────────────────────────────────────────

    public List<TradeModeOption> ModeOptions { get; } = [
        new("Buy Sell → Sell to Buy Order",   TradeMode.SellToBuyOrder),
        new("Buy Sell → Undercut Sell Order",  TradeMode.UndercutSellOrder),
    ];

    private TradeModeOption _selectedMode;
    public TradeModeOption SelectedMode
    {
        get => _selectedMode;
        set => this.RaiseAndSetIfChanged(ref _selectedMode, value);
    }

    // ── Station dropdowns ─────────────────────────────────────────────────────

    public ObservableCollection<StationOption> Stations { get; } = [];

    private StationOption? _sourceStation;
    public StationOption? SourceStation
    {
        get => _sourceStation;
        set => this.RaiseAndSetIfChanged(ref _sourceStation, value);
    }

    private StationOption? _destinationStation;
    public StationOption? DestinationStation
    {
        get => _destinationStation;
        set => this.RaiseAndSetIfChanged(ref _destinationStation, value);
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    private string _cargoM3 = "60000";
    public string CargoM3
    {
        get => _cargoM3;
        set => this.RaiseAndSetIfChanged(ref _cargoM3, value);
    }

    private string _iskCap = "";
    public string IskCap
    {
        get => _iskCap;
        set => this.RaiseAndSetIfChanged(ref _iskCap, value);
    }

    private string _minIskVolume = "";
    public string MinIskVolume
    {
        get => _minIskVolume;
        set => this.RaiseAndSetIfChanged(ref _minIskVolume, value);
    }

    // ── Excluded market groups (and everything nested under them) ────────────

    public ObservableCollection<ExcludedMarketGroupVm> ExcludedMarketGroups { get; } = [];

    public Func<Task<MarketGroupPickerResult?>>? ShowMarketGroupPickerDialog { get; set; }

    public BatchAddService GetBatchAddService() => _batchSvc;

    public ReactiveCommand<Unit, Unit>                     AddExcludedGroupCommand    { get; }
    public ReactiveCommand<ExcludedMarketGroupVm, Unit>    RemoveExcludedGroupCommand { get; }

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
        cmd.CommandText = """SELECT "ExcludedMarketGroupIds" FROM "TradeOpportunitiesSettings" WHERE "Id" = 1""";
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
        cmd.CommandText = """UPDATE "TradeOpportunitiesSettings" SET "ExcludedMarketGroupIds" = @ids WHERE "Id" = 1""";
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

    public ObservableCollection<TradeRow> Results { get; } = [];

    private string _statusText = "Select source and destination stations, then click Calculate.";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private string _summaryVolume  = "";
    private string _summaryCost    = "";
    private string _summaryProfit  = "";

    public string SummaryVolume { get => _summaryVolume;  private set => this.RaiseAndSetIfChanged(ref _summaryVolume, value); }
    public string SummaryCost   { get => _summaryCost;    private set => this.RaiseAndSetIfChanged(ref _summaryCost,   value); }
    public string SummaryProfit { get => _summaryProfit;  private set => this.RaiseAndSetIfChanged(ref _summaryProfit, value); }

    private bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set => this.RaiseAndSetIfChanged(ref _hasSummary, value); }

    private bool _isCalculating;
    public bool IsCalculating { get => _isCalculating; private set => this.RaiseAndSetIfChanged(ref _isCalculating, value); }

    // ── Command ───────────────────────────────────────────────────────────────

    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CalculateCommand { get; }

    // ── Construction ──────────────────────────────────────────────────────────

    public TradeOpportunitiesViewModel(string connString, MarketHistoryService historyService, BatchAddService batchSvc)
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
        await LoadStationsAsync();
        await LoadExcludedGroupsAsync();
    }

    // ── Station loading ───────────────────────────────────────────────────────

    private async Task LoadStationsAsync()
    {
        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = StationsSql;

        Stations.Clear();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Stations.Add(new StationOption(
                reader.GetInt64(0),
                reader.GetString(1)));
        }
    }

    // ── Calculate ─────────────────────────────────────────────────────────────

    private async Task CalculateAsync()
    {
        if (SourceStation is null || DestinationStation is null)
        {
            StatusText = "Please select both a source and destination station.";
            return;
        }
        if (SourceStation.LocationId == DestinationStation.LocationId)
        {
            StatusText = "Source and destination must be different stations.";
            return;
        }
        if (!double.TryParse(CargoM3, out var cargoM3) || cargoM3 <= 0)
        {
            StatusText = "Please enter a valid cargo size in m³.";
            return;
        }

        double? iskCap = null;
        if (!string.IsNullOrWhiteSpace(IskCap))
        {
            if (!double.TryParse(IskCap, out var cap) || cap <= 0)
            {
                StatusText = "Please enter a valid ISK cap (or leave blank for no limit).";
                return;
            }
            iskCap = cap;
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

        Results.Clear();
        HasSummary = false;
        SummaryVolume = SummaryCost = SummaryProfit = "";
        StatusText = "Calculating…";
        IsCalculating = true;

        try
        {
            var candidates = await FetchCandidatesAsync(
                SourceStation.LocationId, DestinationStation.LocationId);

            int? destRegionId = minIskVol.HasValue
                ? await GetRegionIdAsync(DestinationStation.LocationId)
                : null;

            if (minIskVol.HasValue && !destRegionId.HasValue)
            {
                StatusText = "Could not resolve the destination station's region — " +
                             "ensure market data has been loaded for that location so the volume filter can work.";
                return;
            }

            var list = await BuildShoppingListAsync(candidates, cargoM3, iskCap, destRegionId, minIskVol);
            foreach (var r in list) Results.Add(r);

            var totalVol    = list.Sum(r => r.TotalVolume);
            var totalCost   = list.Sum(r => r.TotalCost);
            var totalProfit = list.Sum(r => r.TotalProfit);

            if (list.Count > 0)
            {
                SummaryVolume  = $"{totalVol:N1} / {cargoM3:N0} m³";
                SummaryCost    = FormatIsk(totalCost);
                SummaryProfit  = FormatIsk(totalProfit);
                HasSummary     = true;
                StatusText     = $"{list.Count} item type{(list.Count == 1 ? "" : "s")}  ·  {totalVol:N1} m³ loaded";
            }
            else
            {
                StatusText = "No profitable opportunities found for this route.";
            }
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
        double BestSell, double DestPrice,
        double ProfitPerUnit, double M3PerUnit, double ProfitPerM3,
        long MaxQty);

    private async Task<List<Candidate>> FetchCandidatesAsync(long sourceId, long destId)
    {
        var excludedGroupIds = await GetExcludedGroupIdsRecursiveAsync();
        var exclusionClause  = excludedGroupIds.Count > 0
            ? $"""AND (t."MarketGroupId" IS NULL OR t."MarketGroupId" NOT IN ({string.Join(",", excludedGroupIds)})) """
            : "";

        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = (SelectedMode.Kind == TradeMode.UndercutSellOrder
            ? UndercutSql : CandidateSql).Replace("/*EXCLUSION*/", exclusionClause);
        cmd.Parameters.AddWithValue("@sourceId", sourceId);
        cmd.Parameters.AddWithValue("@destId",   destId);

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
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetInt64(7)));
        }
        return list;
    }

    private async Task<List<TradeRow>> BuildShoppingListAsync(
        List<Candidate> candidates, double cargoM3, double? iskCap,
        int? destRegionId, double? minIskVol30d)
    {
        var result    = new List<TradeRow>();
        var remainM3  = cargoM3;
        var remainIsk = iskCap ?? double.MaxValue;
        int fetched   = 0;

        foreach (var c in candidates)
        {
            if (remainM3 < c.M3PerUnit) continue;
            if (remainIsk < c.BestSell) break; // can't afford even 1 unit — done

            // On-demand 30-day ISK volume filter — only fetch what we actually need.
            if (minIskVol30d.HasValue && destRegionId.HasValue)
            {
                StatusText = $"Checking volume data… ({++fetched} fetched)";
                await _historyService.EnsureFreshAsync(destRegionId.Value, c.TypeId);
                var iskVol = await _historyService.Get30DayIskVolumeAsync(destRegionId.Value, c.TypeId);
                if (iskVol < minIskVol30d.Value) continue;
            }

            var maxByM3  = (long)Math.Floor(remainM3  / c.M3PerUnit);
            var maxByIsk = (long)Math.Floor(remainIsk / c.BestSell);
            var qty      = Math.Min(c.MaxQty, Math.Min(maxByM3, maxByIsk));
            if (qty <= 0) continue;

            var vol    = qty * c.M3PerUnit;
            var cost   = qty * c.BestSell;
            var profit = qty * c.ProfitPerUnit;

            result.Add(new TradeRow
            {
                TypeId        = c.TypeId,
                TypeName      = c.TypeName,
                BestSell      = c.BestSell,
                DestPrice     = c.DestPrice,
                ProfitPerUnit = c.ProfitPerUnit,
                M3PerUnit     = c.M3PerUnit,
                ProfitPerM3   = c.ProfitPerM3,
                Quantity      = qty,
                TotalVolume   = vol,
                TotalCost     = cost,
                TotalProfit   = profit,
            });

            remainM3  -= vol;
            remainIsk -= cost;

            if (remainM3 < 1) break; // cargo full
        }

        return result;
    }

    private async Task<int?> GetRegionIdAsync(long locationId)
    {
        using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();

        // Try NPC station first
        cmd.CommandText = """SELECT "RegionId" FROM "SdeStations" WHERE "StationId" = @id""";
        cmd.Parameters.AddWithValue("@id", (int)Math.Min(locationId, int.MaxValue));
        var result = await cmd.ExecuteScalarAsync();
        if (result is not DBNull and not null)
            return Convert.ToInt32(result);

        // Player structure path 1: resolved name record already has SolarSystemId
        cmd.CommandText = """
            SELECT ss."RegionId"
            FROM "EsiStructureNames" sn
            JOIN "SdeSolarSystems"   ss ON ss."SolarSystemId" = sn."SolarSystemId"
            WHERE sn."StructureId" = @sid AND sn."SolarSystemId" != 0
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@sid", locationId);
        result = await cmd.ExecuteScalarAsync();
        if (result is not DBNull and not null)
            return Convert.ToInt32(result);

        // Player structure path 2: derive from any cached order at that location
        cmd.CommandText = """
            SELECT ss."RegionId"
            FROM "MarketRawOrders" o
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = o."SystemId"
            WHERE o."LocationId" = @lid AND o."SystemId" != 0
            LIMIT 1
            """;
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@lid", locationId);
        result = await cmd.ExecuteScalarAsync();
        if (result is not DBNull and not null)
            return Convert.ToInt32(result);

        return null;
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static string FormatIsk(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:N2}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:N2}B",
        >= 1_000_000         => $"{v / 1_000_000:N2}M",
        _                    => $"{v:N2}",
    };

    // ── SQL ───────────────────────────────────────────────────────────────────

    private const string StationsSql = """
        SELECT o.LocationId,
               COALESCE(s."Name", sn."Name", 'Unknown (' || o.LocationId || ')') AS StationName
        FROM (
            SELECT DISTINCT "LocationId" FROM "MarketRawOrders"
        ) o
        LEFT JOIN "SdeStations"       s  ON s."StationId"   = CAST(o.LocationId AS INTEGER)
        LEFT JOIN "EsiStructureNames" sn ON sn."StructureId" = o.LocationId
        ORDER BY StationName
        """;

    private const string CandidateSql = """
        WITH src AS (
            SELECT "TypeId",
                   MIN("Price")        AS BestSell,
                   SUM("VolumeRemain") AS AvailSell
            FROM "MarketRawOrders"
            WHERE "LocationId" = @sourceId AND "IsBuyOrder" = 0
            GROUP BY "TypeId"
        ),
        dst AS (
            -- Only count buy-order volume where that individual order's price
            -- exceeds the source sell price — avoids mixing in junk 1-ISK orders.
            SELECT d."TypeId",
                   MAX(d."Price")        AS BestBuy,
                   SUM(d."VolumeRemain") AS AvailBuy
            FROM "MarketRawOrders" d
            JOIN src s ON s.TypeId = d."TypeId"
                       AND d."Price" > s.BestSell
            WHERE d."LocationId" = @destId AND d."IsBuyOrder" = 1
            GROUP BY d."TypeId"
        )
        SELECT
            s.TypeId,
            t."Name",
            CAST(s.BestSell AS REAL)                                 AS BestSell,
            CAST(d.BestBuy  AS REAL)                                 AS BestBuy,
            CAST(d.BestBuy - s.BestSell AS REAL)                     AS ProfitPerUnit,
            CAST(t."Volume" AS REAL)                                 AS M3PerUnit,
            CAST((d.BestBuy - s.BestSell) / t."Volume" AS REAL)     AS ProfitPerM3,
            MIN(s.AvailSell, d.AvailBuy)                             AS MaxQty
        FROM src s
        JOIN dst d ON d.TypeId = s.TypeId
        JOIN "SdeTypes" t ON t."TypeId" = s.TypeId
        WHERE t."Volume" > 0
        /*EXCLUSION*/
        ORDER BY ProfitPerM3 DESC
        """;

    private const string UndercutSql = """
        WITH src AS (
            SELECT "TypeId",
                   MIN("Price")        AS BestSell,
                   SUM("VolumeRemain") AS AvailSell
            FROM "MarketRawOrders"
            WHERE "LocationId" = @sourceId AND "IsBuyOrder" = 0
            GROUP BY "TypeId"
        ),
        dst AS (
            SELECT "TypeId",
                   MIN("Price") AS DestSell
            FROM "MarketRawOrders"
            WHERE "LocationId" = @destId AND "IsBuyOrder" = 0
            GROUP BY "TypeId"
        )
        SELECT
            s.TypeId,
            t."Name",
            CAST(s.BestSell  AS REAL)                                AS BestSell,
            CAST(d.DestSell  AS REAL)                                AS DestSell,
            CAST(d.DestSell - s.BestSell AS REAL)                    AS ProfitPerUnit,
            CAST(t."Volume"  AS REAL)                                AS M3PerUnit,
            CAST((d.DestSell - s.BestSell) / t."Volume" AS REAL)    AS ProfitPerM3,
            s.AvailSell                                              AS MaxQty
        FROM src s
        JOIN dst d ON d.TypeId = s.TypeId
        JOIN "SdeTypes" t ON t."TypeId" = s.TypeId
        WHERE d.DestSell > s.BestSell
          AND t."Volume" > 0
        /*EXCLUSION*/
        ORDER BY ProfitPerM3 DESC
        """;
}
