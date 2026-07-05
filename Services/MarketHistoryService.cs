using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.Services;

public class MarketHistoryService : ReactiveObject
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(24);

    // Gap between successive ESI history fetches during a background sweep. 50 ms keeps
    // us to ~20 calls/sec — comfortably inside ESI limits even on a cold full sweep.
    private const int SweepFetchDelayMs = 50;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly EsiClient                       _esi;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly TimerSettingsService            _timerSettings;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;

    private string _statusText = "Price history: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isSweeping;
    public bool IsSweeping
    {
        get => _isSweeping;
        private set => this.RaiseAndSetIfChanged(ref _isSweeping, value);
    }

    // ── Per-region sweep progress (for the Price History monitor) ───────────────

    public record RegionSweepStatus(int RegionId, string RegionName, int Refreshed, int Queue)
    {
        public int Total => Refreshed + Queue; // types that trade in the region
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, RegionSweepStatus> _statusMap = new();

    private volatile IReadOnlyList<RegionSweepStatus> _sweepStatuses = [];
    /// <summary>Live per-region counts: how many types are refreshed (&lt;24h) vs queued.</summary>
    public IReadOnlyList<RegionSweepStatus> SweepStatuses => _sweepStatuses;

    private void SetStatus(int regionId, string name, int refreshed, int queue)
    {
        _statusMap[regionId] = new RegionSweepStatus(regionId, name, refreshed, Math.Max(0, queue));
        _sweepStatuses = _statusMap.Values.OrderBy(s => s.RegionName).ToList();
    }

    /// <summary>
    /// Recomputes the per-region refreshed/queue counts from the DB without any ESI calls.
    /// Cheap — used to populate the monitor when the settings panel opens or on a timer.
    /// </summary>
    public async Task RefreshStatusesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var regions = await db.PriceHistoryRegions.AsNoTracking()
            .Select(r => new { r.RegionId, r.RegionName }).ToListAsync(ct);
        var configRegion = await BuildConfigRegionMapAsync(db, ct);
        var cutoff = DateTimeOffset.UtcNow - CacheAge;

        // Drop regions no longer configured.
        foreach (var id in _statusMap.Keys.Where(k => regions.All(r => r.RegionId != k)).ToList())
            _statusMap.TryRemove(id, out _);

        foreach (var region in regions)
        {
            var cfgIds = configRegion.Where(kv => kv.Value == region.RegionId).Select(kv => kv.Key).ToList();
            var typeIds = cfgIds.Count == 0
                ? new List<int>()
                : await db.MarketRawOrders.AsNoTracking()
                    .Where(o => cfgIds.Contains(o.ConfigId)).Select(o => o.TypeId).Distinct().ToListAsync(ct);
            var freshSet = await db.MarketHistoryFetches.AsNoTracking()
                .Where(f => f.RegionId == region.RegionId && f.FetchedAt >= cutoff)
                .Select(f => f.TypeId).ToHashSetAsync(ct);
            int refreshed = typeIds.Count(t => freshSet.Contains(t));
            SetStatus(region.RegionId, region.RegionName, refreshed, typeIds.Count - refreshed);
        }
    }

    public MarketHistoryService(
        IDbContextFactory<AppDbContext> dbFactory,
        EsiClient                       esi,
        AppErrorLogger                  errorLogger,
        TimerSettingsService            timerSettings)
    {
        _dbFactory     = dbFactory;
        _esi           = esi;
        _errorLogger   = errorLogger;
        _timerSettings = timerSettings;
    }

    // ── Background sweep loop ───────────────────────────────────────────────────

    public void Start() { _loopTask = Task.Run(() => RunLoopAsync(_cts.Token)); }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        if (_loopTask is not null)
            try { await _loopTask; } catch (OperationCanceledException) { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Let the first market refresh populate raw orders (so we know which types trade)
        // before the initial sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(90), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                StatusText = $"Price history: error — {ex.Message[..Math.Min(60, ex.Message.Length)]}";
                _errorLogger.Log("MarketHistoryService", "SweepAsync", ex);
            }

            // How often we CHECK for lapsed items — distinct from the 24h per-item cache.
            // A scan that finds nothing stale is cheap, so we check frequently (default 10m):
            // items lapse their 24h freshness at staggered times, so frequent checks keep them
            // current and naturally spread the polls across the day instead of bursting daily.
            int intervalSeconds = _timerSettings.GetInterval("market.history", 600);
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
                await timer.WaitForNextTickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Refreshes 30-day market history for every type that currently has orders in each
    /// tracked Price-History region, skipping anything fetched within the last 24 hours.
    /// This is what lets the opportunity tools read history straight from the DB.
    /// </summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        IsSweeping = true;
        try { await SweepCoreAsync(ct); }
        finally { IsSweeping = false; }
    }

    private async Task SweepCoreAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var regions = await db.PriceHistoryRegions.AsNoTracking()
            .Select(r => new { r.RegionId, r.RegionName })
            .ToListAsync(ct);
        if (regions.Count == 0)
        {
            StatusText = "Price history: no regions tracked";
            return;
        }

        int fetched = 0, fresh = 0;
        var cutoff = DateTimeOffset.UtcNow - CacheAge;

        // Map each enabled pricing config to its region. Region configs store the region in
        // LocationId; player-structure configs resolve through the structure's solar system.
        // We must NOT derive region from each raw order's SystemId: structure-config orders
        // have SystemId = 0, so that join finds no types for player-structure regions.
        var configRegion = await BuildConfigRegionMapAsync(db, ct);

        foreach (var region in regions)
        {
            if (ct.IsCancellationRequested) break;

            // Types that actually trade in this region = all types with orders under any
            // config mapped to this region.
            var cfgIds = configRegion.Where(kv => kv.Value == region.RegionId)
                                     .Select(kv => kv.Key).ToList();
            var typeIds = cfgIds.Count == 0
                ? new List<int>()
                : await db.MarketRawOrders.AsNoTracking()
                    .Where(o => cfgIds.Contains(o.ConfigId))
                    .Select(o => o.TypeId)
                    .Distinct()
                    .ToListAsync(ct);

            // Skip types already fresh — one query rather than a per-type context.
            var freshTypes = await db.MarketHistoryFetches.AsNoTracking()
                .Where(f => f.RegionId == region.RegionId && f.FetchedAt >= cutoff)
                .Select(f => f.TypeId)
                .ToHashSetAsync(ct);

            var stale = typeIds.Where(t => !freshTypes.Contains(t)).ToList();
            int alreadyFresh = typeIds.Count - stale.Count;
            fresh += alreadyFresh;
            SetStatus(region.RegionId, region.RegionName, alreadyFresh, stale.Count);

            for (int i = 0; i < stale.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (await EnsureFreshAsync(region.RegionId, stale[i], ct)) fetched++;

                if ((i & 63) == 0)
                {
                    StatusText = $"Price history: {region.RegionName} {i + 1}/{stale.Count}…";
                    SetStatus(region.RegionId, region.RegionName, alreadyFresh + i + 1, stale.Count - (i + 1));
                }
                try { await Task.Delay(SweepFetchDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }

            // Accurate end-of-region counts — failed fetches stay queued for the next run.
            var freshNow = await db.MarketHistoryFetches.AsNoTracking()
                .Where(f => f.RegionId == region.RegionId && f.FetchedAt >= cutoff)
                .Select(f => f.TypeId).ToHashSetAsync(ct);
            int refreshedNow = typeIds.Count(t => freshNow.Contains(t));
            SetStatus(region.RegionId, region.RegionName, refreshedNow, typeIds.Count - refreshedNow);
        }

        StatusText = $"Price history: {fetched:N0} updated, {fresh:N0} already fresh — {DateTimeOffset.Now:t}";
    }

    // Resolves each enabled market pricing config to a region id.
    private static async Task<Dictionary<int, int>> BuildConfigRegionMapAsync(AppDbContext db, CancellationToken ct)
    {
        var configs = await db.MarketPricingConfigs.AsNoTracking()
            .Where(c => c.IsEnabled)
            .Select(c => new { c.Id, c.Method, c.LocationId })
            .ToListAsync(ct);

        var map = new Dictionary<int, int>();
        foreach (var c in configs)
        {
            int? region = c.Method == MarketMethod.EsiRegion ? (int)c.LocationId : null;

            if (region is null)
            {
                // Player structure: resolve through its solar system.
                region = await db.EsiStructureNames.AsNoTracking()
                    .Where(sn => sn.StructureId == c.LocationId && sn.SolarSystemId != 0)
                    .Join(db.SdeSolarSystems.AsNoTracking(),
                          sn => sn.SolarSystemId, ss => ss.SolarSystemId, (sn, ss) => (int?)ss.RegionId)
                    .FirstOrDefaultAsync(ct);

                // Fallback: LocationId is itself a region id (e.g. Fuzzwork region configs).
                if ((region is null or 0) &&
                    await db.SdeRegions.AsNoTracking().AnyAsync(r => r.RegionId == c.LocationId, ct))
                    region = (int)c.LocationId;
            }

            if (region is > 0) map[c.Id] = region.Value;
        }
        return map;
    }

    /// <summary>
    /// Ensures history for (regionId, typeId) is no older than 24 hours.
    /// Returns true if an ESI call was made (fetched), false if it was already fresh.
    /// Only records the fetch timestamp when ESI responds successfully.
    /// </summary>
    public async Task<bool> EnsureFreshAsync(int regionId, int typeId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var fetch = await db.MarketHistoryFetches.FindAsync([regionId, typeId], ct);

        if (fetch is not null && DateTimeOffset.UtcNow - fetch.FetchedAt < CacheAge)
            return false; // already fresh

        var entries = await _esi.GetMarketHistoryAsync(regionId, typeId, ct);

        if (entries is null)
        {
            // ESI returned a non-success status — log and do NOT record the attempt
            // so it will be retried on the next sweep.
            _errorLogger.Log(
                "MarketHistoryService",
                $"region={regionId} type={typeId}",
                "ESI market history fetch failed (non-success HTTP status). Will retry next sweep.");
            return true;
        }

        // Accumulate history rather than replace it. ESI only returns a rolling window
        // (~13 months); older rows we've previously stored would be lost on a full replace.
        // Instead, refresh only the window ESI actually returned (delete + reinsert from its
        // oldest returned date forward — settled past days are immutable, only the newest
        // day changes) and KEEP everything older that we've accumulated over time.
        if (entries.Count > 0)
        {
            var minDate = entries.Min(e => e.Date); // oldest date in ESI's current window

            await db.MarketTypeHistories
                .Where(h => h.RegionId == regionId && h.TypeId == typeId
                         && string.Compare(h.Date, minDate) >= 0)
                .ExecuteDeleteAsync(ct);

            db.MarketTypeHistories.AddRange(entries.Select(e => new MarketTypeHistory
            {
                RegionId   = regionId,
                TypeId     = typeId,
                Date       = e.Date,
                Average    = e.Average,
                Highest    = e.Highest,
                Lowest     = e.Lowest,
                Volume     = e.Volume,
                OrderCount = e.OrderCount,
            }));
        }
        // entries.Count == 0 → ESI has no history right now; leave any accumulated rows intact.

        if (fetch is null)
        {
            fetch = new MarketHistoryFetch { RegionId = regionId, TypeId = typeId };
            db.MarketHistoryFetches.Add(fetch);
        }
        fetch.FetchedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return true;
    }

    // Cutoff (ISO yyyy-MM-dd) for the trailing 30-CALENDAR-day window. Dates are stored as
    // ISO strings, so lexicographic comparison is a valid date comparison. Using a calendar
    // window — not the 30 most recent rows — matters for illiquid items: an item that trades
    // a couple of times a month would otherwise sum ~30 *trading days* spanning a year and
    // hugely overcount its "30-day" volume.
    private static string ThirtyDayCutoff() => DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

    /// <summary>
    /// ISK value traded over the last 30 calendar days. Reads the background-swept cache.
    /// </summary>
    public async Task<double> Get30DayIskVolumeAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = ThirtyDayCutoff();
        var recent = await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId
                     && string.Compare(h.Date, cutoff) >= 0)
            .Select(h => new { h.Volume, h.Average })
            .ToListAsync();
        return recent.Sum(h => (double)h.Volume * h.Average);
    }

    /// <summary>
    /// Total units traded over the last 30 calendar days.
    /// </summary>
    public async Task<double> Get30DayUnitVolumeAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = ThirtyDayCutoff();
        var recent = await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId
                     && string.Compare(h.Date, cutoff) >= 0)
            .Select(h => h.Volume)
            .ToListAsync();
        return recent.Sum(v => (double)v);
    }

    /// <summary>
    /// Volume-weighted average trade price over the last 30 calendar days, or 0 if there is
    /// no recent history. Used to price items that have no current sell orders.
    /// </summary>
    public async Task<double> Get30DayAveragePriceAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var cutoff = ThirtyDayCutoff();
        var recent = await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId
                     && string.Compare(h.Date, cutoff) >= 0)
            .Select(h => new { h.Volume, h.Average })
            .ToListAsync();
        long totalVol = recent.Sum(h => (long)h.Volume);
        if (totalVol > 0) return recent.Sum(h => h.Average * h.Volume) / totalVol;
        return recent.Count > 0 ? recent.Average(h => h.Average) : 0.0;
    }

    /// <summary>
    /// Returns all cached history rows newest-first.
    /// </summary>
    public async Task<List<MarketTypeHistory>> GetHistoryAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId)
            .OrderByDescending(h => h.Date)
            .ToListAsync();
    }
}
