using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class MarketPricingService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory   _httpFactory;
    private readonly EsiClient            _esiClient;
    private readonly ApiActivityLog       _log;
    private readonly AppErrorLogger       _errorLogger;
    private readonly TimerSettingsService _timerSettings;

    private CancellationTokenSource _cts      = new();
    private Task?                   _loopTask;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public string StatusText { get; private set; } = "Market prices: not yet fetched";

    // Fired after every full refresh cycle; BuildCostService subscribes to trigger recalculation.
    public Func<CancellationToken, Task>? AfterRefresh { get; set; }

    public MarketPricingService(
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory   httpFactory,
        EsiClient            esiClient,
        ApiActivityLog       log,
        AppErrorLogger       errorLogger,
        TimerSettingsService timerSettings)
    {
        _scopeFactory  = scopeFactory;
        _httpFactory   = httpFactory;
        _esiClient     = esiClient;
        _log           = log;
        _errorLogger   = errorLogger;
        _timerSettings = timerSettings;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start() { _loopTask = Task.Run(() => RunLoopAsync(_cts.Token)); }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loopTask != null)
            try { await _loopTask; } catch (OperationCanceledException) { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), ct);
        // On startup, skip configs whose LastRefreshed is still within the interval so
        // restarting the app doesn't fire a full market refresh unnecessarily.
        await RefreshAllAsync(ct, onlyDue: true);

        while (!ct.IsCancellationRequested)
        {
            int intervalSeconds = _timerSettings.GetInterval("market.refresh", 3600);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            await timer.WaitForNextTickAsync(ct);
            if (!ct.IsCancellationRequested)
                await RefreshAllAsync(ct);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task RefreshAllAsync(CancellationToken ct = default, bool onlyDue = false)
    {
        using var scope = _scopeFactory.CreateScope();
        var db      = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configs = await db.MarketPricingConfigs
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Id)
            .ToListAsync(ct);

        int intervalSeconds = _timerSettings.GetInterval("market.refresh", 3600);
        bool anyRefreshed   = false;

        foreach (var config in configs)
        {
            if (ct.IsCancellationRequested) break;

            if (onlyDue && config.LastRefreshed.HasValue &&
                (DateTimeOffset.UtcNow - config.LastRefreshed.Value).TotalSeconds < intervalSeconds)
                continue;

            StatusText = $"Market: refreshing {config.LocationName}…";
            await RefreshOneAsync(config, db, ct);
            anyRefreshed = true;
        }

        if (configs.Count == 0)
            StatusText = "Market: no sources configured";
        else if (anyRefreshed)
            StatusText = $"Market: last refresh {DateTimeOffset.Now:t}";
        // else: leave StatusText from the previous refresh intact

        if (AfterRefresh is not null && !ct.IsCancellationRequested && anyRefreshed)
        {
            try { await AfterRefresh(ct); }
            catch (Exception ex) { _errorLogger.Log("MarketPricingService", "AfterRefresh", ex); }
        }
    }

    public async Task RefreshConfigAsync(int configId, CancellationToken ct = default)
    {
        using var scope  = _scopeFactory.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var config = await db.MarketPricingConfigs.FindAsync([configId], ct);
        if (config is null) return;
        await RefreshOneAsync(config, db, ct);

        if (AfterRefresh is not null)
        {
            try { await AfterRefresh(ct); }
            catch (Exception ex) { _errorLogger.Log("MarketPricingService", "AfterRefresh", ex); }
        }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    private async Task RefreshOneAsync(MarketPricingConfig config, AppDbContext db, CancellationToken ct)
    {
        using var handle = _log.StartCall(config.LocationName, "market.refresh");
        try
        {
            string status;
            switch (config.Method)
            {
                case MarketMethod.Fuzzwork:
                    await RefreshFuzzworkAsync(config, db, ct);
                    status = "OK";
                    break;
                case MarketMethod.EsiRegion:
                    int regionCount = await RefreshEsiRegionAsync(config, db, ct);
                    status = $"OK ({regionCount:N0} orders fetched)";
                    break;
                case MarketMethod.PlayerStructure:
                    int structCount = await RefreshEsiStructureAsync(config, db, ct);
                    status = $"OK ({structCount:N0} orders fetched)";
                    break;
                default:
                    throw new InvalidOperationException($"Unknown method: {config.Method}");
            }

            await FillPriceGapsAsync(config.Id, db, ct);

            config.LastRefreshed = DateTimeOffset.UtcNow;
            config.LastStatus    = status;
            handle.Complete(true, 200);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var msg = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            config.LastStatus = msg;
            handle.Complete(false, 0, msg);
            _errorLogger.Log("MarketPricingService", config.LocationName, ex);
        }

        db.MarketPricingConfigs.Update(config);
        await db.SaveChangesAsync(ct);
    }

    // ── Fuzzwork ──────────────────────────────────────────────────────────────
    // Returns pre-computed percentile prices; no raw order storage needed.

    private async Task RefreshFuzzworkAsync(
        MarketPricingConfig config, AppDbContext db, CancellationToken ct)
    {
        var typeIds = await db.SdeTypes.AsNoTracking()
            .Where(t => t.Published && t.MarketGroupId != null)
            .Select(t => t.TypeId)
            .ToListAsync(ct);

        if (typeIds.Count == 0)
            throw new InvalidOperationException("No SDE types loaded — run an SDE import first.");

        var http      = _httpFactory.CreateClient("fuzzwork");
        var fetched   = DateTimeOffset.UtcNow;
        var collected = new List<MarketItemPrice>(typeIds.Count);

        const int batchSize = 120;
        for (int i = 0; i < typeIds.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch  = typeIds.Skip(i).Take(batchSize);
            var url    = $"?station={config.LocationId}&types={string.Join(",", batch)}";
            var json   = await http.GetStringAsync(url, ct);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, FwEntry>>(json, _jsonOpts);
            if (parsed == null) continue;

            foreach (var (key, entry) in parsed)
            {
                if (!int.TryParse(key, out var typeId)) continue;
                double buy  = entry.buy?.percentile  ?? 0;
                double sell = entry.sell?.percentile ?? 0;
                if (buy <= 0 && sell <= 0) continue;
                // If no buy orders, use sell as the buy proxy.
                if (buy <= 0) buy = sell;
                collected.Add(new MarketItemPrice
                {
                    ConfigId       = config.Id,
                    TypeId         = typeId,
                    BuyPrice       = buy,
                    SellPrice      = sell,
                    // Midpoint requires both sides; zero-sell items fall back to 0 so SQL uses build-cost.
                    Midpoint       = sell > 0 ? (buy + sell) / 2.0 : 0.0,
                    FetchedAt      = fetched,
                    FromMarketData = true,
                });
            }

            if (i + batchSize < typeIds.Count)
                await Task.Delay(60, ct);
        }

        await UpsertPricesAsync(config.Id, collected, db, ct);
    }

    // ── ESI Region (public, no auth) ──────────────────────────────────────────
    // Returns all public orders for the region (NPC stations only — player
    // structure orders are excluded by ESI; use Player Structure for those).

    private async Task<int> RefreshEsiRegionAsync(
        MarketPricingConfig config, AppDbContext db, CancellationToken ct)
    {
        var result = await _esiClient.ExecutePublicAllPagesAsync<EsiMarketOrder>(
            $"markets/{config.LocationId}/orders/", ct);

        if (!result.IsSuccess)
            throw new InvalidOperationException($"ESI error {result.StatusCode}: {result.Error}");

        var fetched = DateTimeOffset.UtcNow;
        var orders  = result.Data ?? [];

        await UpsertRawOrdersAsync(config.Id,
            orders.Select(o => new MarketRawOrder
            {
                ConfigId     = config.Id,
                OrderId      = o.OrderId,
                TypeId       = o.TypeId,
                IsBuyOrder   = o.IsBuyOrder,
                Price        = o.Price,
                VolumeRemain = o.VolumeRemain,
                VolumeTotal  = o.VolumeTotal,
                MinVolume    = o.MinVolume,
                LocationId   = o.LocationId,
                SystemId     = o.SystemId,
                Range        = o.Range,
                Issued       = o.Issued,
                Duration     = o.Duration,
                FetchedAt    = fetched,
            }).ToList(), db, ct);

        var filtered = config.StationFilter.HasValue
            ? orders.Where(o => o.LocationId == config.StationFilter.Value)
            : orders.AsEnumerable();

        var (filterLowball1, lowballPct1, buildCosts1) = await LoadLowballFilterAsync(db, ct);
        var prices = CalculatePercentilePrices(config.Id,
            filtered.Select(o => (o.TypeId, o.IsBuyOrder, o.Price, o.VolumeRemain)),
            fetched, config.UsePercentileFilter, config.PercentilePercent,
            filterLowball1, lowballPct1, buildCosts1);
        await UpsertPricesAsync(config.Id, prices, db, ct);
        return orders.Count;
    }

    // ── Player Structure (auth required) ──────────────────────────────────────

    private async Task<int> RefreshEsiStructureAsync(
        MarketPricingConfig config, AppDbContext db, CancellationToken ct)
    {
        if (!config.AuthCharId.HasValue)
            throw new InvalidOperationException("No auth character set for Player Structure source.");

        var result = await _esiClient.ExecuteAllPagesAsync<EsiMarketOrder>(
            config.AuthCharId.Value,
            $"markets/structures/{config.LocationId}/",
            ct);

        if (!result.IsSuccess)
            throw new InvalidOperationException($"ESI error {result.StatusCode}: {result.Error}");

        var fetched = DateTimeOffset.UtcNow;
        var orders  = result.Data ?? [];

        await UpsertRawOrdersAsync(config.Id,
            orders.Select(o => new MarketRawOrder
            {
                ConfigId     = config.Id,
                OrderId      = o.OrderId,
                TypeId       = o.TypeId,
                IsBuyOrder   = o.IsBuyOrder,
                Price        = o.Price,
                VolumeRemain = o.VolumeRemain,
                VolumeTotal  = o.VolumeTotal,
                MinVolume    = o.MinVolume,
                LocationId   = o.LocationId,
                SystemId     = o.SystemId,
                Range        = o.Range,
                Issued       = o.Issued,
                Duration     = o.Duration,
                FetchedAt    = fetched,
            }).ToList(), db, ct);

        var (filterLowball2, lowballPct2, buildCosts2) = await LoadLowballFilterAsync(db, ct);
        var prices = CalculatePercentilePrices(config.Id,
            orders.Select(o => (o.TypeId, o.IsBuyOrder, o.Price, o.VolumeRemain)),
            fetched, config.UsePercentileFilter, config.PercentilePercent,
            filterLowball2, lowballPct2, buildCosts2);
        await UpsertPricesAsync(config.Id, prices, db, ct);
        return orders.Count;
    }

    // ── Percentile calculation ────────────────────────────────────────────────

    private static List<MarketItemPrice> CalculatePercentilePrices(
        int configId,
        IEnumerable<(int TypeId, bool IsBuyOrder, double Price, int VolumeRemain)> orders,
        DateTimeOffset fetched,
        bool usePercentile            = true,
        double percentPct             = 5.0,
        bool filterLowball            = false,
        double lowballThresholdPct    = 25.0,
        IReadOnlyDictionary<int, double>? buildCosts = null)
    {
        return orders
            .GroupBy(o => o.TypeId)
            .Select(g =>
            {
                var rawBuys = g.Where(o =>  o.IsBuyOrder).Select(o => (o.Price, o.VolumeRemain)).ToList();
                var sells   = g.Where(o => !o.IsBuyOrder).Select(o => (o.Price, o.VolumeRemain)).ToList();

                // Exclude buy orders below the lowball threshold (% of build cost).
                // rawBuys tracks whether any buy orders existed before filtering so we always
                // store a row for types that appeared in the order book — even if all bids were
                // filtered — giving the SQL build-cost fallback something to key on.
                var buys = rawBuys;
                if (filterLowball && buildCosts is not null &&
                    buildCosts.TryGetValue(g.Key, out var buildCost) && buildCost > 0)
                {
                    double threshold = buildCost * lowballThresholdPct / 100.0;
                    buys = rawBuys.Where(b => b.Price >= threshold).ToList();
                }

                double buy  = buys.Count  > 0
                    ? (usePercentile ? VolumeWeightedPercentile(buys,  100.0 - percentPct) : buys.Max(b => b.Price))
                    : 0;
                double sell = sells.Count > 0
                    ? (usePercentile ? VolumeWeightedPercentile(sells, percentPct) : sells.Min(s => s.Price))
                    : 0;

                // If no buy orders remain after filtering, use sell as the buy proxy.
                if (buy == 0) buy = sell;

                return (
                    Price: new MarketItemPrice
                    {
                        ConfigId       = configId,
                        TypeId         = g.Key,
                        BuyPrice       = buy,
                        SellPrice      = sell,
                        // Midpoint requires both sides; zero-sell items fall back to 0 so SQL uses build-cost.
                        Midpoint       = sell > 0 ? (buy + sell) / 2.0 : 0.0,
                        FetchedAt      = fetched,
                        FromMarketData = true,
                    },
                    // Was there anything in the order book at all (before lowball filter)?
                    HadOrders: rawBuys.Count > 0 || sells.Count > 0
                );
            })
            // Always store a row for types with any order-book activity so the SQL
            // build-cost fallback can trigger even when all bids were filtered out.
            .Where(x => x.Price.BuyPrice > 0 || x.Price.SellPrice > 0 || x.HadOrders)
            .Select(x => x.Price)
            .ToList();
    }

    /// <summary>
    /// Volume-weighted percentile. Sort ascending; walk until cumulative volume
    /// reaches the target percentage of total volume.
    /// - Buy 95th: filters the top 5% of outlier high buy orders.
    /// - Sell  5th: filters the bottom 5% of outlier cheap sell orders.
    /// </summary>
    private static double VolumeWeightedPercentile(
        List<(double Price, int Volume)> orders, double percentile)
    {
        var sorted = orders.OrderBy(o => o.Price).ToList();
        long total = sorted.Sum(o => (long)o.Volume);
        if (total == 0) return 0;

        long target     = Math.Max(1L, (long)Math.Ceiling(total * percentile / 100.0));
        long cumulative = 0;
        foreach (var (price, volume) in sorted)
        {
            cumulative += volume;
            if (cumulative >= target)
                return price;
        }
        return sorted[^1].Price;
    }

    // ── Lowball filter helper ─────────────────────────────────────────────────

    private static async Task<(bool filter, double thresholdPct, IReadOnlyDictionary<int, double> buildCosts)>
        LoadLowballFilterAsync(AppDbContext db, CancellationToken ct)
    {
        var defaults = await db.MarketDefaultSettings.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == 1, ct);
        bool   filter       = defaults?.FilterLowballBuyOrders ?? true;
        double thresholdPct = (double)(defaults?.LowballBuyOrderThresholdPct ?? 25m);

        IReadOnlyDictionary<int, double> costs = filter
            ? await db.BuildCosts.AsNoTracking()
                .ToDictionaryAsync(bc => bc.TypeId, bc => (double)bc.TotalCost, ct)
            : (IReadOnlyDictionary<int, double>)new Dictionary<int, double>();

        return (filter, thresholdPct, costs);
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private static async Task UpsertRawOrdersAsync(
        int configId, List<MarketRawOrder> orders, AppDbContext db, CancellationToken ct)
    {
        // ESI paginates market orders and can return the same OrderId on multiple pages.
        orders = orders.DistinctBy(o => o.OrderId).ToList();

        await db.MarketRawOrders
            .Where(o => o.ConfigId == configId)
            .ExecuteDeleteAsync(ct);

        if (orders.Count == 0) return;

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        // Each transaction covers ~5 000 rows so the write lock is released
        // periodically, letting polling writes through without a 30-second wait.
        const int rowsPerTx = 5_000;
        const int chunk     = 500;
        for (int txStart = 0; txStart < orders.Count; txStart += rowsPerTx)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            int txEnd = Math.Min(txStart + rowsPerTx, orders.Count);
            for (int i = txStart; i < txEnd; i += chunk)
            {
                db.MarketRawOrders.AddRange(orders.Skip(i).Take(chunk));
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
            await tx.CommitAsync(ct);
            // Yield briefly so other async work (polling writes, UI events) can run.
            await Task.Yield();
        }
        db.ChangeTracker.AutoDetectChangesEnabled = true;

        await BackfillStructureSystemsAsync(db, ct);
    }

    /// <summary>
    /// Records which system a structure sits in, from the orders listed there.
    ///
    /// <para>A structure's own details come from /universe/structures/, which 403s unless one of
    /// our characters can dock — so for a private structure the system would otherwise stay 0
    /// forever. A market order carries its system_id regardless of that, and an order listed at a
    /// structure is proof of where the structure is.</para>
    ///
    /// <para>⚠️ Writes to <c>Structures</c>, the app's own table, and never to
    /// <c>EsiStructureNames</c>. Only ESI's own responses go into the polled tables; this is a
    /// conclusion drawn from one endpoint's data about another endpoint's subject, and putting it
    /// there would make a derived value indistinguishable from something ESI actually said about
    /// that structure. It would also be pointless: the sync only copies resolved rows onward, and
    /// a structure that needs this is by definition one that never resolves.</para>
    ///
    /// <para>Only fills zeroes, so a system from the structure endpoint — or one typed by hand —
    /// is left alone. UpdatedBy is deliberately not stamped either: filling an empty field is not
    /// the same as rewriting the row, and claiming it would tell someone their hand-written
    /// description had been overwritten when it had not.</para>
    /// </summary>
    private static async Task BackfillStructureSystemsAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            // Read first, write second, and only what changes.
            //
            // ⚠️ This was one UPDATE with two correlated subqueries over MarketRawOrders, which
            // has no index on LocationId and holds ~665,000 rows. SQLite planned it as a full
            // scan per candidate structure, twice — around a billion row reads, inside the write
            // transaction, at the end of every market refresh. It made the Jita pull hold the
            // database long enough for unrelated saves to time out.
            //
            // One grouped read costs a single scan, and the write touches only the two or three
            // rows that actually gain a system.
            var unknown = await db.Structures
                .Where(s => s.SolarSystemId == 0)
                .Select(s => s.StructureId)
                .ToListAsync(ct);
            if (unknown.Count == 0) return;

            var found = await db.MarketRawOrders.AsNoTracking()
                .Where(o => o.SystemId > 0 && unknown.Contains(o.LocationId))
                .GroupBy(o => o.LocationId)
                .Select(g => new { LocationId = g.Key, SystemId = g.Min(o => o.SystemId) })
                .ToListAsync(ct);
            if (found.Count == 0) return;

            var ids = found.Select(f => f.LocationId).ToList();
            var rows = await db.Structures.Where(s => ids.Contains(s.StructureId)).ToListAsync(ct);

            foreach (var row in rows)
            {
                var hit = found.First(f => f.LocationId == row.StructureId);
                if (row.SolarSystemId == 0) row.SolarSystemId = hit.SystemId;
            }

            await db.SaveChangesAsync(ct);
        }
        catch { /* an unfilled system id is cosmetic — never fail a market pull over it */ }
    }

    private static async Task UpsertPricesAsync(
        int configId, List<MarketItemPrice> prices, AppDbContext db, CancellationToken ct)
    {
        await db.MarketItemPrices
            .Where(p => p.ConfigId == configId)
            .ExecuteDeleteAsync(ct);

        if (prices.Count == 0) return;

        db.ChangeTracker.AutoDetectChangesEnabled = false;
        const int rowsPerTx = 5_000;
        const int chunk     = 500;
        for (int txStart = 0; txStart < prices.Count; txStart += rowsPerTx)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            int txEnd = Math.Min(txStart + rowsPerTx, prices.Count);
            for (int i = txStart; i < txEnd; i += chunk)
            {
                db.MarketItemPrices.AddRange(prices.Skip(i).Take(chunk));
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
            await tx.CommitAsync(ct);
            await Task.Yield();
        }
        db.ChangeTracker.AutoDetectChangesEnabled = true;
    }

    // ── Price gap fill ────────────────────────────────────────────────────────
    // After market data is stored, every published SDE type gets a row:
    //   - types with no market row at all → inserted at build-cost × markup (or 0)
    //   - types with a row but SellPrice = 0 → updated to build-cost × markup (or 0)
    // This makes MarketItemPrices a complete price table so callers never need
    // a separate build-cost fallback query.

    public async Task FillAllGapsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var configIds = await db.MarketPricingConfigs.AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => c.Id)
            .ToListAsync(ct);

        foreach (var id in configIds)
            await FillPriceGapsAsync(id, db, ct);
    }

    private static async Task FillPriceGapsAsync(int configId, AppDbContext db, CancellationToken ct)
    {
        var defaults = await db.MarketDefaultSettings.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == 1, ct);
        double markup  = 1.0 + (double)(defaults?.MissingPriceMarkupPct ?? 15m) / 100.0;
        var    fetched = DateTimeOffset.UtcNow;

        // ⚠️ All three steps below ignore BuildCosts rows with Bought = 1, on purpose.
        //
        // A "bought" row is one BuildCostService could not cost as a build — BPC-only with the
        // BPC never seen on contracts, or cheaper-to-buy when that option is enabled — so it
        // sets TotalCost to the item's own MARKET PRICE. Feeding that back through
        // "price = TotalCost × markup" is circular: every refresh multiplies the price by the
        // markup again. Seen live at ~15 refreshes/day against a 15% markup: 1.15^15 = 8.137x
        // per day, taking a Prototype Cerebral Accelerator from 2.9M to 98.8B in four days and
        // a 'Roaring' Small Graviton Smartbomb past a trillion ISK.
        //
        // Rows that genuinely cost out a build (Bought = 0) are unaffected.

        // Step 1 — insert a row for every published SDE type that has no row yet for this config.
        // Build cost × markup is the initial price; types with no build cost get 0.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO MarketItemPrices (ConfigId, TypeId, BuyPrice, SellPrice, Midpoint, FetchedAt, FromMarketData)
            SELECT {configId}, t.TypeId,
                COALESCE(CAST(bc.TotalCost AS REAL) * {markup}, 0.0),
                COALESCE(CAST(bc.TotalCost AS REAL) * {markup}, 0.0),
                COALESCE(CAST(bc.TotalCost AS REAL) * {markup}, 0.0),
                {fetched},
                0
            FROM SdeTypes t
            LEFT JOIN BuildCosts bc ON bc.TypeId = t.TypeId AND bc.Bought = 0
            WHERE t.Published = 1
              AND NOT EXISTS (
                  SELECT 1 FROM MarketItemPrices p
                  WHERE p.ConfigId = {configId} AND p.TypeId = t.TypeId
              )
            """, ct);

        // Step 2 — for rows that market data left with SellPrice = 0 (e.g. all buy orders were
        // lowball-filtered and there were no sell orders), apply the build-cost price as sell.
        // Existing buy price is preserved when > 0 (real orders at that price); midpoint is
        // recomputed from the final buy and sell values.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH costs AS (
                SELECT TypeId, CAST(TotalCost AS REAL) * {markup} AS EffSell
                FROM BuildCosts
                WHERE Bought = 0
            )
            UPDATE MarketItemPrices
            SET SellPrice = COALESCE((SELECT c.EffSell FROM costs c WHERE c.TypeId = MarketItemPrices.TypeId), 0.0),
                BuyPrice  = CASE
                    WHEN MarketItemPrices.BuyPrice > 0 THEN MarketItemPrices.BuyPrice
                    ELSE COALESCE((SELECT c.EffSell FROM costs c WHERE c.TypeId = MarketItemPrices.TypeId), 0.0)
                    END,
                Midpoint  = (
                    CASE
                        WHEN MarketItemPrices.BuyPrice > 0 THEN MarketItemPrices.BuyPrice
                        ELSE COALESCE((SELECT c.EffSell FROM costs c WHERE c.TypeId = MarketItemPrices.TypeId), 0.0)
                    END
                    + COALESCE((SELECT c.EffSell FROM costs c WHERE c.TypeId = MarketItemPrices.TypeId), 0.0)
                ) / 2.0,
                FetchedAt = {fetched}
            WHERE ConfigId = {configId}
              AND SellPrice = 0
            """, ct);

        // Step 3 — refresh stale build-cost-derived rows. Only rows with FromMarketData = 0
        // (inserted by Step 1) are updated; real market-order rows are never overwritten here.
        // Previously this used a price-equality heuristic (Buy=Sell=Midpoint) that incorrectly
        // matched sell-only market items (no buy orders → buy was set to sell → all three equal).
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH costs AS (
                SELECT TypeId, CAST(TotalCost AS REAL) * {markup} AS EffSell
                FROM BuildCosts
                WHERE Bought = 0
            )
            UPDATE MarketItemPrices
            SET SellPrice = c.EffSell,
                BuyPrice  = c.EffSell,
                Midpoint  = c.EffSell,
                FetchedAt = {fetched}
            FROM costs c
            WHERE MarketItemPrices.ConfigId      = {configId}
              AND MarketItemPrices.TypeId         = c.TypeId
              AND MarketItemPrices.FromMarketData = 0
              AND MarketItemPrices.SellPrice     != c.EffSell
            """, ct);

        // Step 4 — price anything that sells on contracts rather than the market from contract
        // data. Runs last so it wins over the build-cost estimate above, which is the weaker
        // signal for these items. Real market rows (FromMarketData = 1) are never touched.
        await FillContractPricesAsync(configId, db, fetched, ct);
    }

    /// <summary>
    /// Sets the market value of contract-traded types to their contract-derived price, so they
    /// can be treated as normal purchased inputs by the build-cost / production-calc code.
    ///
    /// Applies to ANY type with contract observations, not just blueprints. It was originally
    /// blueprint-only (SDE category 9) because BPCs are the obvious case — they never appear on
    /// the regular market. But the same is true of plenty of non-blueprints: measured on a live
    /// database, 510 non-blueprint types had contract prices and no market orders at all. Those
    /// were left to the build-cost estimate, which for an uncostable item is its own market
    /// price — the circular path described in FillPriceGapsAsync.
    ///
    /// The price comes from ContractPricing.EffectivePrice, so the existing rule applies: the
    /// lowest active contract price, unless that is more than 50% above the 30-day average of
    /// the daily best, in which case the steadier average is used.
    /// </summary>
    private static async Task FillContractPricesAsync(
        int configId, AppDbContext db, DateTimeOffset fetched, CancellationToken ct)
    {
        var eff = new Dictionary<int, double>();
        foreach (var cp in await db.ContractPrices.AsNoTracking().ToListAsync(ct))
        {
            var e = ContractPricing.EffectivePrice(cp);
            if (e is > 0) eff[cp.TypeId] = (double)e.Value;
        }
        if (eff.Count == 0) return;

        var ids = eff.Keys.ToList();

        var existing = await db.MarketItemPrices
            .Where(p => p.ConfigId == configId && ids.Contains(p.TypeId))
            .ToListAsync(ct);
        var existingIds = existing.Select(p => p.TypeId).ToHashSet();

        // Refresh non-market rows (gap-filled / prior contract value) to the current contract
        // price; never overwrite a row backed by real market orders.
        foreach (var p in existing)
        {
            if (p.FromMarketData) continue;
            var v = eff[p.TypeId];
            p.BuyPrice = v; p.SellPrice = v; p.Midpoint = v; p.FetchedAt = fetched;
        }

        // Insert rows for contract-priced types the published-types gap fill skipped
        // (e.g. unpublished faction blueprints).
        foreach (var tid in ids.Where(t => !existingIds.Contains(t)))
        {
            var v = eff[tid];
            db.MarketItemPrices.Add(new MarketItemPrice
            {
                ConfigId = configId, TypeId = tid,
                BuyPrice = v, SellPrice = v, Midpoint = v,
                FetchedAt = fetched, FromMarketData = false,
            });
        }

        await db.SaveChangesAsync(ct);
    }

    // ── Fuzzwork JSON DTOs ────────────────────────────────────────────────────

    private sealed class FwEntry
    {
        [JsonPropertyName("buy")]  public FwSide? buy  { get; init; }
        [JsonPropertyName("sell")] public FwSide? sell { get; init; }
    }

    private sealed class FwSide
    {
        [JsonPropertyName("percentile")] public double percentile { get; init; }
        [JsonPropertyName("max")]        public double max        { get; init; }
        [JsonPropertyName("min")]        public double min        { get; init; }
        [JsonPropertyName("orderCount")] public int    orderCount { get; init; }
    }

    // ── ESI order DTO ─────────────────────────────────────────────────────────
    // Used for both /markets/{region_id}/orders/ and /markets/structures/{id}/

    private sealed record EsiMarketOrder(
        [property: JsonPropertyName("order_id")]      long           OrderId,
        [property: JsonPropertyName("type_id")]       int            TypeId,
        [property: JsonPropertyName("location_id")]   long           LocationId,
        [property: JsonPropertyName("system_id")]     int            SystemId,
        [property: JsonPropertyName("price")]         double         Price,
        [property: JsonPropertyName("is_buy_order")]  bool           IsBuyOrder,
        [property: JsonPropertyName("volume_remain")] int            VolumeRemain,
        [property: JsonPropertyName("volume_total")]  int            VolumeTotal,
        [property: JsonPropertyName("min_volume")]    int            MinVolume,
        [property: JsonPropertyName("range")]         string         Range,
        [property: JsonPropertyName("issued")]        DateTimeOffset Issued,
        [property: JsonPropertyName("duration")]      int            Duration
    );
}
