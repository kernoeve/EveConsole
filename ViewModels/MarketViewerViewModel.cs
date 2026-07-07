using System.Collections.ObjectModel;
using System.Reactive.Linq;
using EveCortex.Data;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

internal static class MarketFmt
{
    public static string Isk(double v) => Num(v);
    public static string Num(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1e12) return $"{v / 1e12:N2}T";
        if (a >= 1e9)  return $"{v / 1e9:N2}B";
        if (a >= 1e6)  return $"{v / 1e6:N2}M";
        if (a >= 1e3)  return $"{v / 1e3:N1}K";
        return v.ToString("N0");
    }
}

public class MarketPeriodOption
{
    public string Label { get; }
    public int?   Days  { get; }   // null = all time
    public MarketPeriodOption(string label, int? days) { Label = label; Days = days; }
    public override string ToString() => Label;
}

public class MarketRegionOption
{
    public string Label    { get; }
    public int?   RegionId { get; }   // null = all regions
    public MarketRegionOption(string label, int? regionId) { Label = label; RegionId = regionId; }
    public override string ToString() => Label;
}

// One region row on the Summary tab.
public class MarketRegionSummaryVm
{
    public string Region { get; }
    public string SellCount { get; }  public long   SellCountRaw { get; }
    public string SellIsk   { get; }  public double SellIskRaw   { get; }
    public string SellTypes { get; }  public long   SellTypesRaw { get; }
    public string BuyCount  { get; }  public long   BuyCountRaw  { get; }
    public string BuyIsk    { get; }  public double BuyIskRaw    { get; }
    public string BuyTypes  { get; }  public long   BuyTypesRaw  { get; }
    public string SalesUnits { get; } public double SalesUnitsRaw { get; }
    public string SalesIsk   { get; } public double SalesIskRaw   { get; }
    public string SalesTypes { get; } public long   SalesTypesRaw { get; }

    public MarketRegionSummaryVm(string region,
        long sellCount, double sellIsk, long sellTypes,
        long buyCount, double buyIsk, long buyTypes,
        double salesUnits, double salesIsk, long salesTypes)
    {
        Region        = region;
        SellCountRaw  = sellCount;  SellCount  = sellCount.ToString("N0");
        SellIskRaw    = sellIsk;    SellIsk    = MarketFmt.Isk(sellIsk);
        SellTypesRaw  = sellTypes;  SellTypes  = sellTypes.ToString("N0");
        BuyCountRaw   = buyCount;   BuyCount   = buyCount.ToString("N0");
        BuyIskRaw     = buyIsk;     BuyIsk     = MarketFmt.Isk(buyIsk);
        BuyTypesRaw   = buyTypes;   BuyTypes   = buyTypes.ToString("N0");
        SalesUnitsRaw = salesUnits; SalesUnits = MarketFmt.Num(salesUnits);
        SalesIskRaw   = salesIsk;   SalesIsk   = MarketFmt.Isk(salesIsk);
        SalesTypesRaw = salesTypes; SalesTypes = salesTypes.ToString("N0");
    }
}

// One type row on the By Type tab.
public class MarketTypeSummaryVm
{
    public string Type { get; }
    public string SellUnits { get; } public double SellUnitsRaw { get; }
    public string SellIsk   { get; } public double SellIskRaw   { get; }
    public string BuyUnits  { get; } public double BuyUnitsRaw  { get; }
    public string BuyIsk    { get; } public double BuyIskRaw    { get; }
    public string SalesUnits { get; } public double SalesUnitsRaw { get; }
    public string SalesIsk   { get; } public double SalesIskRaw   { get; }

    public MarketTypeSummaryVm(string type,
        double sellUnits, double sellIsk, double buyUnits, double buyIsk,
        double salesUnits, double salesIsk)
    {
        Type          = type;
        SellUnitsRaw  = sellUnits;  SellUnits  = MarketFmt.Num(sellUnits);
        SellIskRaw    = sellIsk;    SellIsk    = MarketFmt.Isk(sellIsk);
        BuyUnitsRaw   = buyUnits;   BuyUnits   = MarketFmt.Num(buyUnits);
        BuyIskRaw     = buyIsk;     BuyIsk     = MarketFmt.Isk(buyIsk);
        SalesUnitsRaw = salesUnits; SalesUnits = MarketFmt.Num(salesUnits);
        SalesIskRaw   = salesIsk;   SalesIsk   = MarketFmt.Isk(salesIsk);
    }
}

// Region-level market views. Orders map to a region via the order's solar system, or — for player
// structures (null-sec), where the order has no system id — via the structure's system.
public class MarketViewerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private bool _initialized;

    // Region derivation: order → system → region, else order → structure → system → region.
    private const string OrdersFrom =
        "FROM MarketRawOrders o " +
        "LEFT JOIN SdeSolarSystems ssSys ON ssSys.SolarSystemId = o.SystemId " +
        "LEFT JOIN EsiStructureNames sn   ON sn.StructureId     = o.LocationId " +
        "LEFT JOIN SdeSolarSystems ssStr  ON ssStr.SolarSystemId = sn.SolarSystemId";
    private const string RegionExpr = "COALESCE(ssSys.RegionId, ssStr.RegionId)";

    public ObservableCollection<MarketRegionSummaryVm> SummaryRows { get; } = new();
    public ObservableCollection<MarketTypeSummaryVm>   TypeRows    { get; } = new();

    public IReadOnlyList<MarketPeriodOption> Periods { get; } =
    [
        new("All Time",      null),
        new("Last 365 Days", 365),
        new("Last 90 Days",  90),
        new("Last 30 Days",  30),
        new("Last 7 Days",   7),
    ];
    private MarketPeriodOption _selectedPeriod;
    public MarketPeriodOption SelectedPeriod
    {
        get => _selectedPeriod;
        set { this.RaiseAndSetIfChanged(ref _selectedPeriod, value ?? Periods[3]); _ = LoadActiveAsync(); }
    }

    public ObservableCollection<MarketRegionOption> Regions { get; } = new();
    private MarketRegionOption? _selectedRegion;
    public MarketRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set { this.RaiseAndSetIfChanged(ref _selectedRegion, value); if (SelectedTab == 1) _ = LoadByTypeAsync(); }
    }

    // 0 = Summary, 1 = By Type
    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set { this.RaiseAndSetIfChanged(ref _selectedTab, value); _ = LoadActiveAsync(); }
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public MarketViewerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory      = dbFactory;
        _errorLogger    = errorLogger;
        _selectedPeriod = Periods[3];   // Last 30 Days

        // Auto-refresh the active tab every 5 minutes.
        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tick => { _ = LoadActiveAsync(); });

        _ = InitAsync();
    }

    private string? Cutoff() =>
        _selectedPeriod.Days is int d ? DateTime.UtcNow.AddDays(-d).ToString("yyyy-MM-dd") : null;

    private Task LoadActiveAsync() => SelectedTab == 1 ? LoadByTypeAsync() : LoadSummaryAsync();

    private async Task InitAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Regions we have data for (orders and/or any history).
            var ids = await db.Database.SqlQueryRaw<int>(
                $"SELECT DISTINCT {RegionExpr} AS Value {OrdersFrom} WHERE {RegionExpr} IS NOT NULL " +
                "UNION SELECT DISTINCT RegionId AS Value FROM MarketTypeHistories").ToListAsync();
            var names = await db.SdeRegions.AsNoTracking()
                .Where(r => ids.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);

            Regions.Clear();
            Regions.Add(new MarketRegionOption("All regions", null));
            foreach (var id in ids.OrderBy(i => names.TryGetValue(i, out var n) ? n : $"{i}"))
                Regions.Add(new MarketRegionOption(names.TryGetValue(id, out var n) ? n : $"Region {id}", id));
            _selectedRegion = Regions.FirstOrDefault();
            this.RaisePropertyChanged(nameof(SelectedRegion));

            _initialized = true;
            await LoadSummaryAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "InitAsync", ex);
            StatusText = "Error initialising market viewer.";
        }
    }

    // ── Summary (one row per region; always all regions) ──────────────────────────
    private async Task LoadSummaryAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var orders = await db.Database.SqlQueryRaw<RegionOrderAgg>(
                $"""
                 SELECT {RegionExpr} AS RegionId,
                        SUM(CASE WHEN o.IsBuyOrder = 0 THEN 1 ELSE 0 END)                        AS SellCount,
                        SUM(CASE WHEN o.IsBuyOrder = 0 THEN o.Price * o.VolumeRemain ELSE 0 END) AS SellIsk,
                        COUNT(DISTINCT CASE WHEN o.IsBuyOrder = 0 THEN o.TypeId END)             AS SellTypes,
                        SUM(CASE WHEN o.IsBuyOrder = 1 THEN 1 ELSE 0 END)                        AS BuyCount,
                        SUM(CASE WHEN o.IsBuyOrder = 1 THEN o.Price * o.VolumeRemain ELSE 0 END) AS BuyIsk,
                        COUNT(DISTINCT CASE WHEN o.IsBuyOrder = 1 THEN o.TypeId END)             AS BuyTypes
                 {OrdersFrom}
                 WHERE {RegionExpr} IS NOT NULL
                 GROUP BY {RegionExpr}
                 """).ToListAsync();

            var cutoff = Cutoff();
            var sales = await db.Database.SqlQueryRaw<RegionSalesAgg>(
                "SELECT RegionId AS RegionId, SUM(Volume) AS Units, SUM(Volume * Average) AS Isk, " +
                "COUNT(DISTINCT CASE WHEN Volume > 0 THEN TypeId END) AS Types FROM MarketTypeHistories " +
                (cutoff is null ? "" : $"WHERE Date >= '{cutoff}' ") + "GROUP BY RegionId").ToListAsync();

            var regionIds = orders.Select(o => o.RegionId).Concat(sales.Select(s => s.RegionId)).Distinct().ToList();
            var regionNames = await db.SdeRegions.AsNoTracking().Where(r => regionIds.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);
            var oByR = orders.ToDictionary(o => o.RegionId);
            var sByR = sales.ToDictionary(s => s.RegionId);

            var rows = regionIds.Select(rid =>
            {
                oByR.TryGetValue(rid, out var o); sByR.TryGetValue(rid, out var s);
                return new MarketRegionSummaryVm(
                    regionNames.TryGetValue(rid, out var n) ? n : $"Region {rid}",
                    o?.SellCount ?? 0, o?.SellIsk ?? 0, o?.SellTypes ?? 0,
                    o?.BuyCount ?? 0, o?.BuyIsk ?? 0, o?.BuyTypes ?? 0,
                    s?.Units ?? 0, s?.Isk ?? 0, s?.Types ?? 0);
            }).OrderByDescending(r => r.SalesIskRaw).ToList();

            SummaryRows.Clear();
            foreach (var r in rows) SummaryRows.Add(r);
            StatusText = rows.Count == 0 ? "No market data yet." : $"{rows.Count:N0} region(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadSummary", ex);
            StatusText = "Error loading summary.";
        }
        finally { IsLoading = false; }
    }

    // ── By Type (one row per type; selected region or all) ────────────────────────
    private async Task LoadByTypeAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            int? region = _selectedRegion?.RegionId;

            var orderWhere = region is int r1 ? $"WHERE {RegionExpr} = {r1}" : $"WHERE {RegionExpr} IS NOT NULL";
#pragma warning disable EF1002 // region is an inlined int from a fixed, DB-derived set — no injection risk
            var orders = await db.Database.SqlQueryRaw<TypeOrderAgg>(
                $"""
                 SELECT o.TypeId AS TypeId,
                        SUM(CASE WHEN o.IsBuyOrder = 0 THEN o.VolumeRemain ELSE 0 END)           AS SellUnits,
                        SUM(CASE WHEN o.IsBuyOrder = 0 THEN o.Price * o.VolumeRemain ELSE 0 END) AS SellIsk,
                        SUM(CASE WHEN o.IsBuyOrder = 1 THEN o.VolumeRemain ELSE 0 END)           AS BuyUnits,
                        SUM(CASE WHEN o.IsBuyOrder = 1 THEN o.Price * o.VolumeRemain ELSE 0 END) AS BuyIsk
                 {OrdersFrom}
                 {orderWhere}
                 GROUP BY o.TypeId
                 """).ToListAsync();
#pragma warning restore EF1002

            var cutoff = Cutoff();
            var conds = new List<string>();
            if (region is int r2) conds.Add($"RegionId = {r2}");
            if (cutoff is not null) conds.Add($"Date >= '{cutoff}'");
            var salesWhere = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";
            var sales = await db.Database.SqlQueryRaw<TypeSalesAgg>(
                $"SELECT TypeId AS TypeId, SUM(Volume) AS Units, SUM(Volume * Average) AS Isk " +
                $"FROM MarketTypeHistories {salesWhere} GROUP BY TypeId").ToListAsync();

            var typeIds = orders.Select(o => o.TypeId).Concat(sales.Select(s => s.TypeId)).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            var oByT = orders.ToDictionary(o => o.TypeId);
            var sByT = sales.ToDictionary(s => s.TypeId);

            var rows = typeIds.Select(tid =>
            {
                oByT.TryGetValue(tid, out var o); sByT.TryGetValue(tid, out var s);
                return new MarketTypeSummaryVm(
                    typeNames.TryGetValue(tid, out var n) ? n : $"Type {tid}",
                    o?.SellUnits ?? 0, o?.SellIsk ?? 0, o?.BuyUnits ?? 0, o?.BuyIsk ?? 0,
                    s?.Units ?? 0, s?.Isk ?? 0);
            }).OrderByDescending(t => t.SalesIskRaw).ToList();

            TypeRows.Clear();
            foreach (var t in rows) TypeRows.Add(t);
            StatusText = rows.Count == 0 ? "No market data for this selection." : $"{rows.Count:N0} type(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadByType", ex);
            StatusText = "Error loading by-type data.";
        }
        finally { IsLoading = false; }
    }

    private sealed class RegionOrderAgg
    {
        public int RegionId { get; set; }
        public long SellCount { get; set; } public double SellIsk { get; set; } public long SellTypes { get; set; }
        public long BuyCount  { get; set; } public double BuyIsk  { get; set; } public long BuyTypes  { get; set; }
    }
    private sealed class RegionSalesAgg
    {
        public int RegionId { get; set; } public double Units { get; set; } public double Isk { get; set; } public long Types { get; set; }
    }
    private sealed class TypeOrderAgg
    {
        public int TypeId { get; set; }
        public double SellUnits { get; set; } public double SellIsk { get; set; }
        public double BuyUnits  { get; set; } public double BuyIsk  { get; set; }
    }
    private sealed class TypeSalesAgg
    {
        public int TypeId { get; set; } public double Units { get; set; } public double Isk { get; set; }
    }
}
