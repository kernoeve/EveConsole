using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

public class MarketHistoryService(
    IDbContextFactory<AppDbContext> dbFactory,
    EsiClient esi,
    AppErrorLogger errorLogger)
{
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Ensures history for (regionId, typeId) is no older than 24 hours.
    /// Only records the fetch timestamp when ESI responds successfully.
    /// Logs an error entry if ESI returns a non-success status.
    /// </summary>
    public async Task EnsureFreshAsync(int regionId, int typeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var fetch = await db.MarketHistoryFetches
            .FindAsync([regionId, typeId], ct);

        if (fetch is not null && DateTimeOffset.UtcNow - fetch.FetchedAt < CacheAge)
            return; // already fresh

        var entries = await esi.GetMarketHistoryAsync(regionId, typeId, ct);

        if (entries is null)
        {
            // ESI returned a non-success status — log and do NOT record the attempt
            // so it will be retried on the next call.
            errorLogger.Log(
                "MarketHistoryService",
                $"region={regionId} type={typeId}",
                "ESI market history fetch failed (non-success HTTP status). Will retry next call.");
            return;
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
    }

    /// <summary>
    /// Returns the sum of Volume * Average for the last 30 calendar days.
    /// Assumes EnsureFreshAsync has already been called.
    /// </summary>
    public async Task<double> Get30DayIskVolumeAsync(int regionId, int typeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");

        return await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId
                     && string.Compare(h.Date, cutoff) >= 0)
            .SumAsync(h => (double)h.Volume * h.Average);
    }

    /// <summary>
    /// Returns all cached history rows newest-first.
    /// Assumes EnsureFreshAsync has already been called.
    /// </summary>
    public async Task<List<MarketTypeHistory>> GetHistoryAsync(int regionId, int typeId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.MarketTypeHistories
            .Where(h => h.RegionId == regionId && h.TypeId == typeId)
            .OrderByDescending(h => h.Date)
            .ToListAsync();
    }
}
