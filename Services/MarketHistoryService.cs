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

            int intervalSeconds = _timerSettings.GetInterval("market.history", 86_400);
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

        foreach (var region in regions)
        {
            if (ct.IsCancellationRequested) break;

            // Types that actually trade in this region (i.e. have orders) — the only ones
            // worth history. Region is derived from each order's solar system.
            var typeIds = await db.MarketRawOrders.AsNoTracking()
                .Join(db.SdeSolarSystems.AsNoTracking(),
                      o => o.SystemId, s => s.SolarSystemId, (o, s) => new { o.TypeId, s.RegionId })
                .Where(x => x.RegionId == region.RegionId)
                .Select(x => x.TypeId)
                .Distinct()
                .ToListAsync(ct);

            // Skip types already fresh — one query rather than a per-type context.
            var freshTypes = await db.MarketHistoryFetches.AsNoTracking()
                .Where(f => f.RegionId == region.RegionId && f.FetchedAt >= cutoff)
                .Select(f => f.TypeId)
                .ToHashSetAsync(ct);

            var stale = typeIds.Where(t => !freshTypes.Contains(t)).ToList();
            fresh += typeIds.Count - stale.Count;

            for (int i = 0; i < stale.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                if (await EnsureFreshAsync(region.RegionId, stale[i], ct)) fetched++;

                if ((i & 63) == 0)
                    StatusText = $"Price history: {region.RegionName} {i + 1}/{stale.Count}…";
                try { await Task.Delay(SweepFetchDelayMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }

        StatusText = $"Price history: {fetched:N0} updated, {fresh:N0} already fresh — {DateTimeOffset.Now:t}";
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

        // Replace all history rows for this type+region
        await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId)
            .ExecuteDeleteAsync(ct);

        if (entries.Count > 0)
        {
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

        if (fetch is null)
        {
            fetch = new MarketHistoryFetch { RegionId = regionId, TypeId = typeId };
            db.MarketHistoryFetches.Add(fetch);
        }
        fetch.FetchedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Returns the sum of Volume * Average over the most recent 30 history rows
    /// (i.e. the last 30 traded days). Anchored to the newest available data rather
    /// than the wall clock, so it stays correct even when ESI history lags a few days.
    /// Reads whatever the background sweep has already cached.
    /// </summary>
    public async Task<double> Get30DayIskVolumeAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var recent = await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId)
            .OrderByDescending(h => h.Date)
            .Take(30)
            .Select(h => new { h.Volume, h.Average })
            .ToListAsync();
        return recent.Sum(h => (double)h.Volume * h.Average);
    }

    /// <summary>
    /// Returns the total units traded over the most recent 30 history rows.
    /// </summary>
    public async Task<double> Get30DayUnitVolumeAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var recent = await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId)
            .OrderByDescending(h => h.Date)
            .Take(30)
            .Select(h => h.Volume)
            .ToListAsync();
        return recent.Sum(v => (double)v);
    }

    /// <summary>
    /// Volume-weighted average trade price over the most recent 30 history rows, or 0 if
    /// there is no history. Used to price items that have no current sell orders.
    /// </summary>
    public async Task<double> Get30DayAveragePriceAsync(int regionId, int typeId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var recent = await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId)
            .OrderByDescending(h => h.Date)
            .Take(30)
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
