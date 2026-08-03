using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>Dataset names, matching EVE Ref's directory names so one constant serves both.</summary>
public static class MapDataset
{
    public const string Jumps         = "system-jumps";
    public const string Kills         = "system-kills";
    public const string Sovereignty   = "sovereignty-map";
    public const string SovStructures = "sovereignty-structures";
    public const string Industry      = "industry-systems";
    public const string FactionWar    = "faction-warfare-systems";
    public const string Incursions    = "incursions";

    public static readonly string[] All =
        [Jumps, Kills, Sovereignty, SovStructures, Industry, FactionWar, Incursions];
}

/// <summary>
/// The single write path for map statistics, shared by the live ESI poll and the EVE Ref
/// archive backfill.
///
/// Everything is keyed by the CCP hour bucket the data describes, never by when it was
/// fetched, which is what lets the two sources produce interchangeable rows. Writes are
/// insert-only and skip anything already present, so re-running a backfill over a period
/// already covered live is a no-op rather than a duplicate.
/// </summary>
public class MapStatsService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger? errors = null)
{
    /// <summary>
    /// Bucket key for an instant: "yyyy-MM-dd HH" in UTC. Sortable as text, groups to a day
    /// with SUBSTR, and identical whether it came from an ESI Last-Modified header or an EVE
    /// Ref file_time.
    /// </summary>
    public static string BucketOf(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-dd HH");

    public static DateTimeOffset ParseBucket(string bucket) =>
        DateTimeOffset.ParseExact(bucket + ":00:00 +00:00", "yyyy-MM-dd HH:mm:ss zzz", null);

    // ── Bucket bookkeeping ───────────────────────────────────────────────────

    /// <summary>Buckets already stored for a dataset within a range, so gap-fill can ask for
    /// only what is missing.</summary>
    public async Task<HashSet<string>> GetStoredBucketsAsync(
        string dataset, string fromBucket, string toBucket, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var rows = await db.MapStatBuckets.AsNoTracking()
            .Where(b => b.Dataset == dataset
                     && string.Compare(b.Bucket, fromBucket) >= 0
                     && string.Compare(b.Bucket, toBucket)   <= 0)
            .Select(b => b.Bucket)
            .ToListAsync(ct);
        return rows.ToHashSet();
    }

    public async Task<bool> HasBucketAsync(string dataset, string bucket, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.MapStatBuckets.AsNoTracking()
            .AnyAsync(b => b.Dataset == dataset && b.Bucket == bucket, ct);
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores one bucket's worth of rows for a dataset. Does nothing if that bucket is already
    /// recorded, so the caller need not check first and two sources racing on the same hour
    /// cannot double-insert.
    /// </summary>
    /// <returns>Rows written; -1 if the bucket was already present.</returns>
    public async Task<int> StoreAsync<T>(
        string dataset, string bucket, string source, IReadOnlyList<T> rows,
        CancellationToken ct = default) where T : class
    {
        using var db = dbFactory.CreateDbContext();

        if (await db.MapStatBuckets.AnyAsync(b => b.Dataset == dataset && b.Bucket == bucket, ct))
            return -1;

        try
        {
            if (rows.Count > 0)
            {
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                await db.AddRangeAsync(rows, ct);
            }

            // Written in the same transaction as the rows: a bucket marked stored but whose
            // rows failed to save would be skipped forever by gap-fill.
            db.MapStatBuckets.Add(new MapStatBucket
            {
                Dataset  = dataset,
                Bucket   = bucket,
                StoredAt = DateTimeOffset.UtcNow,
                Source   = source,
                RowCount = rows.Count,
            });

            await db.SaveChangesAsync(ct);
            return rows.Count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Most likely a primary-key collision from a concurrent write of the same bucket,
            // which is exactly the case the design intends to be harmless.
            errors?.Log("MapStats", $"store {dataset} {bucket}", ex);
            return -1;
        }
    }

    // ── Reads for the overlays ───────────────────────────────────────────────

    public sealed record SystemActivity(int SystemId, int ShipJumps, int ShipKills, int PodKills, int NpcKills);

    /// <summary>
    /// Totals per system over the last <paramref name="hours"/>, reading the hourly tables.
    /// For windows longer than the hourly retention the daily rollup is used instead — see
    /// <see cref="GetActivityByDayAsync"/>.
    /// </summary>
    public async Task<Dictionary<int, SystemActivity>> GetActivityAsync(
        int hours, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var from = BucketOf(DateTimeOffset.UtcNow.AddHours(-hours));

        var jumps = await db.MapSystemJumps.AsNoTracking()
            .Where(j => string.Compare(j.Bucket, from) >= 0)
            .GroupBy(j => j.SystemId)
            .Select(g => new { SystemId = g.Key, Total = g.Sum(x => x.ShipJumps) })
            .ToListAsync(ct);

        var kills = await db.MapSystemKills.AsNoTracking()
            .Where(k => string.Compare(k.Bucket, from) >= 0)
            .GroupBy(k => k.SystemId)
            .Select(g => new
            {
                SystemId = g.Key,
                Ship = g.Sum(x => x.ShipKills),
                Pod  = g.Sum(x => x.PodKills),
                Npc  = g.Sum(x => x.NpcKills),
            })
            .ToListAsync(ct);

        return Merge(
            jumps.Select(j => (j.SystemId, j.Total, 0, 0, 0)),
            kills.Select(k => (k.SystemId, 0, k.Ship, k.Pod, k.Npc)));
    }

    /// <summary>Totals per system over the last N days, from the daily rollup.</summary>
    public async Task<Dictionary<int, SystemActivity>> GetActivityByDayAsync(
        int days, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var from = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");

        var rows = await db.MapSystemDailies.AsNoTracking()
            .Where(d => string.Compare(d.Day, from) >= 0)
            .GroupBy(d => d.SystemId)
            .Select(g => new
            {
                SystemId = g.Key,
                Jumps = g.Sum(x => x.ShipJumps),
                Ship  = g.Sum(x => x.ShipKills),
                Pod   = g.Sum(x => x.PodKills),
                Npc   = g.Sum(x => x.NpcKills),
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.SystemId,
            r => new SystemActivity(r.SystemId, r.Jumps, r.Ship, r.Pod, r.Npc));
    }

    private static Dictionary<int, SystemActivity> Merge(
        params IEnumerable<(int SystemId, int Jumps, int Ship, int Pod, int Npc)>[] sets)
    {
        var acc = new Dictionary<int, SystemActivity>();
        foreach (var set in sets)
            foreach (var (id, jumps, ship, pod, npc) in set)
            {
                var cur = acc.GetValueOrDefault(id, new SystemActivity(id, 0, 0, 0, 0));
                acc[id] = cur with
                {
                    ShipJumps = cur.ShipJumps + jumps,
                    ShipKills = cur.ShipKills + ship,
                    PodKills  = cur.PodKills  + pod,
                    NpcKills  = cur.NpcKills  + npc,
                };
            }
        return acc;
    }

    /// <summary>Most recent sovereignty snapshot, as system → (alliance, corp, faction).</summary>
    public async Task<Dictionary<int, MapSovereignty>> GetLatestSovereigntyAsync(
        CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var latest = await db.MapSovereignties.AsNoTracking()
            .OrderByDescending(s => s.Bucket).Select(s => s.Bucket).FirstOrDefaultAsync(ct);
        if (latest is null) return [];

        return await db.MapSovereignties.AsNoTracking()
            .Where(s => s.Bucket == latest)
            .ToDictionaryAsync(s => s.SystemId, s => s, ct);
    }

    /// <summary>
    /// Highest ADM per system from the most recent structure snapshot. A system can hold more
    /// than one sovereignty structure; the defended value is the one that matters.
    /// </summary>
    public async Task<Dictionary<int, double>> GetLatestAdmAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var latest = await db.MapSovStructures.AsNoTracking()
            .OrderByDescending(s => s.Bucket).Select(s => s.Bucket).FirstOrDefaultAsync(ct);
        if (latest is null) return [];

        return await db.MapSovStructures.AsNoTracking()
            .Where(s => s.Bucket == latest && s.Adm != null)
            .GroupBy(s => s.SystemId)
            .Select(g => new { SystemId = g.Key, Adm = g.Max(x => x.Adm!.Value) })
            .ToDictionaryAsync(g => g.SystemId, g => g.Adm, ct);
    }

    // ── Retention ────────────────────────────────────────────────────────────

    /// <summary>
    /// Rolls hourly rows older than <paramref name="keepHourlyDays"/> into daily totals and
    /// deletes them. Without this the hourly tables grow without bound — system jumps alone is
    /// roughly 4,800 rows an hour, about 42 million a year.
    ///
    /// Hours counts how many buckets fed each day, so an incomplete day stays identifiable:
    /// the archive itself occasionally lacks an hour.
    /// </summary>
    private static readonly string[] ThinnedTables =
        ["MapSovereignties", "MapSovStructures", "MapIndustryIndices",
         "MapFactionWarfares", "MapIncursions"];

    public async Task<int> RollUpAsync(int keepHourlyDays, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-keepHourlyDays).ToString("yyyy-MM-dd");

        // Aggregate and delete in one transaction so a crash cannot drop hourly rows whose
        // totals were never written.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // ON CONFLICT adds rather than replaces, so an old hour recovered from the archive
        // after its day was already rolled up still lands in the daily total.
        var affected = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "MapSystemDailies" ("Day", "SystemId", "ShipJumps", "ShipKills", "PodKills", "NpcKills", "Hours")
            SELECT "Day", "SystemId", SUM("ShipJumps"), SUM("ShipKills"), SUM("PodKills"), SUM("NpcKills"), MAX("Hours")
            FROM (
                SELECT SUBSTR("Bucket", 1, 10) AS "Day", "SystemId",
                       SUM("ShipJumps") AS "ShipJumps", 0 AS "ShipKills", 0 AS "PodKills", 0 AS "NpcKills",
                       COUNT(*) AS "Hours"
                FROM "MapSystemJumps" WHERE SUBSTR("Bucket", 1, 10) < {cutoff}
                GROUP BY 1, 2
                UNION ALL
                SELECT SUBSTR("Bucket", 1, 10), "SystemId",
                       0, SUM("ShipKills"), SUM("PodKills"), SUM("NpcKills"), COUNT(*)
                FROM "MapSystemKills" WHERE SUBSTR("Bucket", 1, 10) < {cutoff}
                GROUP BY 1, 2
            )
            GROUP BY "Day", "SystemId"
            ON CONFLICT("Day", "SystemId") DO UPDATE SET
                "ShipJumps" = "MapSystemDailies"."ShipJumps" + excluded."ShipJumps",
                "ShipKills" = "MapSystemDailies"."ShipKills" + excluded."ShipKills",
                "PodKills"  = "MapSystemDailies"."PodKills"  + excluded."PodKills",
                "NpcKills"  = "MapSystemDailies"."NpcKills"  + excluded."NpcKills",
                "Hours"     = MAX("MapSystemDailies"."Hours", excluded."Hours")
            """, ct);

        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "MapSystemJumps" WHERE SUBSTR("Bucket", 1, 10) < {cutoff}""", ct);
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "MapSystemKills" WHERE SUBSTR("Bucket", 1, 10) < {cutoff}""", ct);

        // The rest are state snapshots rather than counters, so summing them would be
        // meaningless — they are thinned to the first bucket of each day instead.
        foreach (var table in ThinnedTables)
        {
            // Table name is structural, so it cannot be a parameter; the list is a private
            // constant, never anything user-supplied.
            // $$ so {0} stays literal for the parameter placeholder and {{table}} interpolates.
            var sql = $$"""
                DELETE FROM "{{table}}"
                WHERE SUBSTR("Bucket", 1, 10) < {0}
                  AND "Bucket" NOT IN (
                      SELECT MIN("Bucket") FROM "{{table}}" GROUP BY SUBSTR("Bucket", 1, 10))
                """;
            await db.Database.ExecuteSqlRawAsync(sql, [cutoff], ct);
        }

        await tx.CommitAsync(ct);
        return affected;
    }
}
