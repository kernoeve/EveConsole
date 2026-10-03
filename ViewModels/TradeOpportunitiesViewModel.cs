using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Collections;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using ReactiveUI;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;

namespace EveConsole.ViewModels;

/// <summary>A place with market orders. <see cref="Name"/> is English: the sale-posting dialogs
/// store it.</summary>
public record StationOption(long LocationId, string Name)
{
    /// <summary>What a picker shows: an NPC station in the interface language, a structure as named.</summary>
    public string DisplayName => SdeNames.Location(LocationId, Name);
}

public enum TradeMode { SellToBuyOrder, UndercutSellOrder }
public record TradeModeOption(string Label, TradeMode Kind);

/// <summary>A market group left out of the search. <paramref name="Name"/> is the name shown, in the
/// interface language; the list is saved and matched by id.</summary>
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
    // Units and ISK traded in the destination's region over the last 30 days — how much the
    // market there takes, for judging how far a sell order will have to undercut. From the
    // cached history (MarketHistoryService); 0 where none has been read for the type.
    public double DestUnitVol30d { get; init; }
    public double DestIskVol30d  { get; init; }

    public string BestSellDisplay    => FormatIsk(BestSell);
    public string DestPriceDisplay   => FormatIsk(DestPrice);
    public string ProfitUnitDisplay  => FormatIsk(ProfitPerUnit);
    public string ProfitM3Display    => FormatIsk(ProfitPerM3);
    public string QuantityDisplay    => $"{Quantity:N0}";
    public string TotalVolumeDisplay => $"{TotalVolume:N1}";
    public string TotalCostDisplay   => FormatIsk(TotalCost);
    public string TotalProfitDisplay => FormatIsk(TotalProfit);
    public string DestUnitVol30dDisplay => $"{DestUnitVol30d:N0}";
    public string DestIskVol30dDisplay  => FormatIsk(DestIskVol30d);

    /// <summary>Single-click opens the Item Browser. Double-clicking the row still routes
    /// through the tool's own RequestItemNavigation, which keeps the trade context.</summary>
    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

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
        new(MarketText.ModeSellToBuyOrder,    TradeMode.SellToBuyOrder),
        new(MarketText.ModeUndercutSellOrder, TradeMode.UndercutSellOrder),
    ];

    private TradeModeOption _selectedMode;
    public TradeModeOption SelectedMode
    {
        get => _selectedMode;
        set => this.RaiseAndSetIfChanged(ref _selectedMode, value);
    }

    // ── Station dropdowns ─────────────────────────────────────────────────────

    public ObservableCollection<StationOption> Stations { get; } = [];

    /// <summary>What the two station boxes find as they are typed in: a station by the name they
    /// show, or by its English — which is what gets pasted from other sites.</summary>
    public Avalonia.Controls.AutoCompleteFilterPredicate<object?> StationFilter { get; } = (text, item) =>
        item is StationOption s && SdeNames.Matches(SdeNameKind.Station, s.LocationId, s.Name, text ?? "");

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

    private string _minUnitVolume = "";
    public string MinUnitVolume
    {
        get => _minUnitVolume;
        set => this.RaiseAndSetIfChanged(ref _minUnitVolume, value);
    }

    // Each buy is capped at what the destination's region sold in this many days; empty for no
    // cap. Thirty to start: a month's sales is about what a sell order there can expect to move.
    private string _capDays = "30";
    public string CapDays
    {
        get => _capDays;
        set => this.RaiseAndSetIfChanged(ref _capDays, value);
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

        ExcludedMarketGroups.Add(new ExcludedMarketGroupVm(pick.MarketGroupId,
            SdeNames.MarketGroup(pick.MarketGroupId, pick.GroupName)));
        await SaveExcludedGroupsAsync();
    }

    private async Task RemoveExcludedGroupAsync(ExcludedMarketGroupVm group)
    {
        ExcludedMarketGroups.Remove(group);
        await SaveExcludedGroupsAsync();
    }

    private async Task LoadExcludedGroupsAsync()
    {
        // The names are shown once and kept, so they wait for the interface language's first.
        await SdeNames.EnsureLoadedAsync();

        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""SELECT "ExcludedMarketGroupIds" FROM "TradeOpportunitiesSettings" WHERE "Id" = 1""");
        var raw = (await cmd.ExecuteScalarAsync()) as string ?? "";

        var ids = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToList();
        if (ids.Count == 0) return;

        using var nameCmd = conn.Command($"""SELECT "MarketGroupId", "Name" FROM "SdeMarketGroups" WHERE "MarketGroupId" IN ({string.Join(",", ids)})""");
        var names = new Dictionary<int, string>();
        using (var reader = await nameCmd.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                names[reader.GetInt32(0)] = reader.GetString(1);

        ExcludedMarketGroups.Clear();
        foreach (var id in ids)
            if (names.TryGetValue(id, out var name))
                ExcludedMarketGroups.Add(new ExcludedMarketGroupVm(id, SdeNames.MarketGroup(id, name)));
    }

    private async Task SaveExcludedGroupsAsync()
    {
        var csv = string.Join(",", ExcludedMarketGroups.Select(g => g.MarketGroupId));
        using var conn = AppDb.Connect();
        await conn.OpenAsync();
        using var cmd = conn.Command("""UPDATE "TradeOpportunitiesSettings" SET "ExcludedMarketGroupIds" = @ids WHERE "Id" = 1""");
        cmd.AddWithValue("@ids", csv);
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

    /// <summary>The results as the grid shows them: filtered by name, sorted by its headers. The
    /// summary below stays the whole list's — the filter only narrows what is looked at.</summary>
    public DataGridCollectionView ResultsView { get; }

    /// <summary>Narrows the results to the items whose name contains what was typed, as it is
    /// typed. See <see cref="ItemNameFilter{T}"/>.</summary>
    public ItemNameFilter<TradeRow> NameFilter { get; }

    // The button is named through its own entry, so the hint cannot drift from its label.
    private string _statusText = string.Format(MarketText.StatusSelectStations, MarketText.Calculate);
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
        ResultsView     = new DataGridCollectionView(Results);
        NameFilter      = new ItemNameFilter<TradeRow>(ResultsView, Results, r => r.TypeName);
        Results.CollectionChanged += (_, _) => NameFilter.Update();
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
        // Sorted by the names shown, so they are waited for first (at once in English).
        await SdeNames.EnsureLoadedAsync();

        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.Command(StationsSql);

        // A location with no name is named here, not in the SQL, so the words can be translated;
        // and sorted here, so it sorts by the words shown.
        var found = new List<StationOption>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt64(0);
            found.Add(new StationOption(id,
                reader.IsDBNull(1) ? string.Format(MarketText.StationUnknown, id) : reader.GetString(1)));
        }
        Stations.Clear();
        foreach (var s in found.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)) Stations.Add(s);
    }

    // ── Calculate ─────────────────────────────────────────────────────────────

    private async Task CalculateAsync()
    {
        if (SourceStation is null || DestinationStation is null)
        {
            StatusText = MarketText.ErrSelectBothStations;
            return;
        }
        if (SourceStation.LocationId == DestinationStation.LocationId)
        {
            StatusText = MarketText.ErrSameStation;
            return;
        }
        // Optional, like the ISK cap: left empty, every profitable item is listed at what is on
        // offer, as Industry Opportunities lists everything worth building.
        double? cargoM3 = null;
        if (!string.IsNullOrWhiteSpace(CargoM3))
        {
            if (!double.TryParse(CargoM3, out var cargo) || cargo <= 0)
            {
                StatusText = MarketText.ErrInvalidCargo;
                return;
            }
            cargoM3 = cargo;
        }

        double? iskCap = null;
        if (!string.IsNullOrWhiteSpace(IskCap))
        {
            if (!double.TryParse(IskCap, out var cap) || cap <= 0)
            {
                StatusText = MarketText.ErrInvalidIskCap;
                return;
            }
            iskCap = cap;
        }

        double? minIskVol = null;
        if (!string.IsNullOrWhiteSpace(MinIskVolume))
        {
            if (!double.TryParse(MinIskVolume, out var mv) || mv < 0)
            {
                StatusText = MarketText.ErrInvalidMinIskVolume;
                return;
            }
            minIskVol = mv;
        }

        double? minUnitVol = null;
        if (!string.IsNullOrWhiteSpace(MinUnitVolume))
        {
            if (!double.TryParse(MinUnitVolume, out var uv) || uv < 0)
            {
                StatusText = MarketText.ErrInvalidMinUnitVolume;
                return;
            }
            minUnitVol = uv;
        }

        int? capDays = null;
        if (!string.IsNullOrWhiteSpace(CapDays))
        {
            if (!int.TryParse(CapDays, out var days) || days <= 0)
            {
                StatusText = MarketText.ErrInvalidCapDays;
                return;
            }
            capDays = days;
        }

        Results.Clear();
        HasSummary = false;
        SummaryVolume = SummaryCost = SummaryProfit = "";
        StatusText = MarketText.StatusCalculating;
        IsCalculating = true;

        try
        {
            var candidates = await FetchCandidatesAsync(
                SourceStation.LocationId, DestinationStation.LocationId);

            bool needsVolume  = minIskVol.HasValue || minUnitVol.HasValue;
            // Always looked up: the 30-day destination columns show it whether or not a volume
            // filter is set. Without it the columns read 0, and only a filter makes that an error.
            int? destRegionId = await GetRegionIdAsync(DestinationStation.LocationId);

            if (needsVolume && !destRegionId.HasValue)
            {
                StatusText = MarketText.ErrNoDestinationRegion;
                return;
            }

            await SdeNames.EnsureLoadedAsync();   // the rows carry the names shown
            var list = await BuildShoppingListAsync(candidates, cargoM3, iskCap, destRegionId, minIskVol, minUnitVol, capDays);
            // Default display order — highest total profit first. Column headers allow re-sorting.
            foreach (var r in list.OrderByDescending(r => r.TotalProfit)) Results.Add(r);

            var totalVol    = list.Sum(r => r.TotalVolume);
            var totalCost   = list.Sum(r => r.TotalCost);
            var totalProfit = list.Sum(r => r.TotalProfit);

            if (list.Count > 0)
            {
                SummaryVolume  = cargoM3 is { } hold ? $"{totalVol:N1} / {hold:N0} m³" : $"{totalVol:N1} m³";
                SummaryCost    = FormatIsk(totalCost);
                SummaryProfit  = FormatIsk(totalProfit);
                HasSummary     = true;
                StatusText     = Plurals.Format(MarketText.ResourceManager,
                                     nameof(MarketText.StatusItemTypesLoadedOther), list.Count, totalVol);
            }
            else
            {
                StatusText = MarketText.StatusNoOpportunities;
            }
        }
        catch (Exception ex)
        {
            StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message);
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

        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.Command((SelectedMode.Kind == TradeMode.UndercutSellOrder
            ? UndercutSql : CandidateSql).Replace("/*EXCLUSION*/", exclusionClause));
        cmd.AddWithValue("@sourceId", sourceId);
        cmd.AddWithValue("@destId",   destId);

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
        List<Candidate> candidates, double? cargoM3, double? iskCap,
        int? destRegionId, double? minIskVol30d, double? minUnitVol30d, int? capDays = 30)
    {
        var result    = new List<TradeRow>();
        var remainM3  = cargoM3 ?? double.MaxValue;
        var remainIsk = iskCap ?? double.MaxValue;

        // Units that fit in what is left; no limit at all when there is none. ⚠️ Not a bare cast:
        // double.MaxValue over a price is far past long's range.
        static long Fits(double? limit, double left, double each) =>
            limit is null || each <= 0 ? long.MaxValue : (long)Math.Floor(left / each);

        foreach (var c in candidates)
        {
            if (remainM3 < c.M3PerUnit) continue;
            if (remainIsk < c.BestSell) break; // can't afford even 1 unit — done

            // The destination's 30-day volumes, for the columns and the filters alike — read from
            // history cached by the background sweep (MarketHistoryService), no ESI calls here.
            double iskVol = 0, unitVol = 0, capUnits = 0;
            var known = false;
            if (destRegionId.HasValue)
            {
                (unitVol, iskVol, known, capUnits) = await _historyService.Get30DayVolumesAsync(destRegionId.Value, c.TypeId, capDays ?? 30);
                if (minIskVol30d.HasValue  && iskVol  < minIskVol30d.Value)  continue;
                if (minUnitVol30d.HasValue && unitVol < minUnitVol30d.Value) continue;
            }

            var maxByM3  = Fits(cargoM3, remainM3,  c.M3PerUnit);
            var maxByIsk = Fits(iskCap,  remainIsk, c.BestSell);
            var qty      = Math.Min(c.MaxQty, Math.Min(maxByM3, maxByIsk));
            // No more than the destination's region took in the days set (30 to start): buying
            // 1,000 of something that moved 3 a month is stock for years. Only where the history
            // has been read — an item it has not reached yet is not capped to nothing; one read
            // with no trades in the window is, and drops out. No days set: no cap.
            if (known && capDays is not null) qty = Math.Min(qty, (long)capUnits);
            if (qty <= 0) continue;

            var vol    = qty * c.M3PerUnit;
            var cost   = qty * c.BestSell;
            var profit = qty * c.ProfitPerUnit;

            result.Add(new TradeRow
            {
                TypeId        = c.TypeId,
                TypeName      = SdeNames.Type(c.TypeId, c.TypeName),   // shown only; the row goes by TypeId
                BestSell      = c.BestSell,
                DestPrice     = c.DestPrice,
                ProfitPerUnit = c.ProfitPerUnit,
                M3PerUnit     = c.M3PerUnit,
                ProfitPerM3   = c.ProfitPerM3,
                Quantity      = qty,
                TotalVolume   = vol,
                TotalCost     = cost,
                TotalProfit   = profit,
                DestUnitVol30d = unitVol,
                DestIskVol30d  = iskVol,
            });

            remainM3  -= vol;
            remainIsk -= cost;

            if (cargoM3 is not null && remainM3 < 1) break; // cargo full
        }

        return result;
    }

    private async Task<int?> GetRegionIdAsync(long locationId)
    {
        using var conn = AppDb.Connect();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();

        // NPC station: resolve via the station's solar system. SdeStations.RegionId is
        // populated now (by the importer, and by a startup repair for databases imported
        // before that fix), but the join is kept deliberately — it is correct whatever
        // state the column is in.
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "SdeStations"     s
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = s."SolarSystemId"
            WHERE s."StationId" = @id AND s."SolarSystemId" != 0
            """);
        cmd.AddWithValue("@id", (int)Math.Min(locationId, int.MaxValue));
        var region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Player structure path 1: resolved name record already has SolarSystemId
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "EsiStructureNames" sn
            JOIN "SdeSolarSystems"   ss ON ss."SolarSystemId" = sn."SolarSystemId"
            WHERE sn."StructureId" = @sid AND sn."SolarSystemId" != 0
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@sid", locationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        // Player structure path 2: derive from any cached order at that location
        cmd.CommandText = AppDb.CaseInsensitiveLike("""
            SELECT ss."RegionId"
            FROM "MarketRawOrders" o
            JOIN "SdeSolarSystems" ss ON ss."SolarSystemId" = o."SystemId"
            WHERE o."LocationId" = @lid AND o."SystemId" != 0
            LIMIT 1
            """);
        cmd.Parameters.Clear();
        cmd.AddWithValue("@lid", locationId);
        region = ToRegionId(await cmd.ExecuteScalarAsync());
        if (region.HasValue) return region;

        return null;
    }

    // Treats NULL/DBNull and a 0 region id as "unresolved" so callers fall through.
    private static int? ToRegionId(object? scalar)
    {
        if (scalar is null or DBNull) return null;
        var id = Convert.ToInt32(scalar);
        return id != 0 ? id : null;
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
        SELECT o."LocationId",
               COALESCE(s."Name", sn."Name") AS "StationName"
        FROM (
            SELECT DISTINCT "LocationId" FROM "MarketRawOrders"
        ) o
        LEFT JOIN "SdeStations"       s  ON s."StationId"   = CAST(o."LocationId" AS BIGINT)
        LEFT JOIN "EsiStructureNames" sn ON sn."StructureId" = o."LocationId"
        """;

    // ⚠️ A property, not a const: it interpolates the engine-correct scalar-min function.
    private static string CandidateSql => $$"""
        WITH src AS (
            SELECT "TypeId",
                   MIN("Price")        AS BestSell,
                   SUM("VolumeRemain") AS AvailSell
            FROM "MarketRawOrders"
            WHERE "LocationId" = @sourceId AND "IsBuyOrder" = FALSE
            GROUP BY "TypeId"
        ),
        dst AS (
            -- Only count buy-order volume where that individual order's price
            -- exceeds the source sell price — avoids mixing in junk 1-ISK orders.
            SELECT d."TypeId",
                   MAX(d."Price")        AS BestBuy,
                   SUM(d."VolumeRemain") AS AvailBuy
            FROM "MarketRawOrders" d
            JOIN src s ON s."TypeId" = d."TypeId"
                       AND d."Price" > s.BestSell
            WHERE d."LocationId" = @destId AND d."IsBuyOrder" = TRUE
            GROUP BY d."TypeId"
        )
        SELECT
            s."TypeId",
            t."Name",
            CAST(s.BestSell AS DOUBLE PRECISION)                                 AS BestSell,
            CAST(d.BestBuy  AS DOUBLE PRECISION)                                 AS BestBuy,
            CAST(d.BestBuy - s.BestSell AS DOUBLE PRECISION)                     AS ProfitPerUnit,
            CAST(t."Volume" AS DOUBLE PRECISION)                                 AS M3PerUnit,
            CAST((d.BestBuy - s.BestSell) / t."Volume" AS DOUBLE PRECISION)     AS ProfitPerM3,
            {{AppDb.LeastFn}}(s.AvailSell, d.AvailBuy)                             AS MaxQty
        FROM src s
        JOIN dst d ON d."TypeId" = s."TypeId"
        JOIN "SdeTypes" t ON t."TypeId" = s."TypeId"
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
            WHERE "LocationId" = @sourceId AND "IsBuyOrder" = FALSE
            GROUP BY "TypeId"
        ),
        dst AS (
            SELECT "TypeId",
                   MIN("Price") AS DestSell
            FROM "MarketRawOrders"
            WHERE "LocationId" = @destId AND "IsBuyOrder" = FALSE
            GROUP BY "TypeId"
        )
        SELECT
            s."TypeId",
            t."Name",
            CAST(s.BestSell  AS DOUBLE PRECISION)                                AS BestSell,
            CAST(d.DestSell  AS DOUBLE PRECISION)                                AS DestSell,
            CAST(d.DestSell - s.BestSell AS DOUBLE PRECISION)                    AS ProfitPerUnit,
            CAST(t."Volume"  AS DOUBLE PRECISION)                                AS M3PerUnit,
            CAST((d.DestSell - s.BestSell) / t."Volume" AS DOUBLE PRECISION)    AS ProfitPerM3,
            s.AvailSell                                              AS MaxQty
        FROM src s
        JOIN dst d ON d."TypeId" = s."TypeId"
        JOIN "SdeTypes" t ON t."TypeId" = s."TypeId"
        WHERE d.DestSell > s.BestSell
          AND t."Volume" > 0
        /*EXCLUSION*/
        ORDER BY ProfitPerM3 DESC
        """;
}
