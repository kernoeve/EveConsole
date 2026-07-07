using System.Collections.ObjectModel;
using System.Reactive;
using EveCortex.Data;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

// One region's market summary. Open-order metrics come from stored raw orders (mapped to a region
// via each order's solar system); the 30-day sales metrics come from region-level price history.
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

// Market Viewer tool — region-level market views. Sub-tab 1: Summary (one row per region).
public class MarketViewerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;

    public ObservableCollection<MarketRegionSummaryVm> SummaryRows { get; } = new();

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public MarketViewerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadSummaryAsync);
        _ = LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Open orders aggregated to region via each order's solar system.
            var orders = await db.Database.SqlQueryRaw<OrderAgg>(
                """
                SELECT ss.RegionId AS RegionId,
                       SUM(CASE WHEN o.IsBuyOrder = 0 THEN 1 ELSE 0 END)                         AS SellCount,
                       SUM(CASE WHEN o.IsBuyOrder = 0 THEN o.Price * o.VolumeRemain ELSE 0 END)  AS SellIsk,
                       COUNT(DISTINCT CASE WHEN o.IsBuyOrder = 0 THEN o.TypeId END)              AS SellTypes,
                       SUM(CASE WHEN o.IsBuyOrder = 1 THEN 1 ELSE 0 END)                         AS BuyCount,
                       SUM(CASE WHEN o.IsBuyOrder = 1 THEN o.Price * o.VolumeRemain ELSE 0 END)  AS BuyIsk,
                       COUNT(DISTINCT CASE WHEN o.IsBuyOrder = 1 THEN o.TypeId END)              AS BuyTypes
                FROM MarketRawOrders o
                JOIN SdeSolarSystems ss ON ss.SolarSystemId = o.SystemId
                GROUP BY ss.RegionId
                """).ToListAsync();

            // 30-day sales from region-level price history (Date stored as YYYY-MM-DD).
            var cutoff = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var sales = await db.Database.SqlQueryRaw<SalesAgg>(
                """
                SELECT RegionId AS RegionId,
                       SUM(Volume)                                        AS Units,
                       SUM(Volume * Average)                              AS Isk,
                       COUNT(DISTINCT CASE WHEN Volume > 0 THEN TypeId END) AS Types
                FROM MarketTypeHistories
                WHERE Date >= {0}
                GROUP BY RegionId
                """, cutoff).ToListAsync();

            var regionIds = orders.Select(o => o.RegionId).Concat(sales.Select(s => s.RegionId)).Distinct().ToList();
            var regionNames = await db.SdeRegions.AsNoTracking()
                .Where(r => regionIds.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);

            var orderByRegion = orders.ToDictionary(o => o.RegionId);
            var salesByRegion = sales.ToDictionary(s => s.RegionId);

            var rows = regionIds
                .Select(rid =>
                {
                    orderByRegion.TryGetValue(rid, out var o);
                    salesByRegion.TryGetValue(rid, out var s);
                    return new MarketRegionSummaryVm(
                        regionNames.TryGetValue(rid, out var n) ? n : $"Region {rid}",
                        o?.SellCount ?? 0, o?.SellIsk ?? 0, o?.SellTypes ?? 0,
                        o?.BuyCount ?? 0,  o?.BuyIsk ?? 0,  o?.BuyTypes ?? 0,
                        s?.Units ?? 0,     s?.Isk ?? 0,     s?.Types ?? 0);
                })
                .OrderByDescending(r => r.SalesIskRaw)
                .ToList();

            SummaryRows.Clear();
            foreach (var r in rows) SummaryRows.Add(r);
            StatusText = rows.Count == 0
                ? "No market data yet — configure a market source and let it fetch."
                : $"{rows.Count:N0} region(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadSummary", ex);
            StatusText = "Error loading market summary.";
        }
        finally { IsLoading = false; }
    }

    private sealed class OrderAgg
    {
        public int    RegionId  { get; set; }
        public long   SellCount { get; set; }
        public double SellIsk   { get; set; }
        public long   SellTypes { get; set; }
        public long   BuyCount  { get; set; }
        public double BuyIsk    { get; set; }
        public long   BuyTypes  { get; set; }
    }

    private sealed class SalesAgg
    {
        public int    RegionId { get; set; }
        public double Units    { get; set; }
        public double Isk      { get; set; }
        public long   Types    { get; set; }
    }
}
