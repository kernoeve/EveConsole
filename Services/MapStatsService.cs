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

    /// <summary>
    /// Datasets stored once per day of history rather than once per hour.
    ///
    /// These are slow-moving state, and storing every hour of it is almost pure duplication.
    /// Measured on real data: sovereignty holdings were byte-identical across 8 consecutive
    /// hours, and industry indices drifted 0.05% over the same span — far below what a daily
    /// trend reads at. Industry alone at hourly cadence is 32,910 rows an hour, which is two
    /// thirds of all map data and projected to 23.7M rows for a 30-day backfill.
    ///
    /// This is about history only. The live poller still refreshes them every hour, so the
    /// current value stays current; it is the older hours that get thinned to one a day.
    /// </summary>
    public static readonly string[] DailyCadence = [Sovereignty, SovStructures, Industry];

    public static bool IsDailyCadence(string dataset) => DailyCadence.Contains(dataset);
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
    /// <summary>State tables kept at one snapshot per day beyond the current day. These back
    /// the daily-cadence datasets, which dominate storage — see MapDataset.DailyCadence.</summary>
    public sealed record DatasetCoverage(
        string Dataset, int Buckets, int Days, string Earliest, string Latest);

    /// <summary>
    /// How much history is held per dataset. Read from the bucket markers rather than the data
    /// tables, so a dataset whose hours were legitimately empty still reports its coverage.
    /// </summary>
    public async Task<List<DatasetCoverage>> GetCoverageAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var rows = await db.MapStatBuckets.AsNoTracking()
            .GroupBy(b => b.Dataset)
            .Select(g => new
            {
                Dataset  = g.Key,
                Buckets  = g.Count(),
                Earliest = g.Min(x => x.Bucket),
                Latest   = g.Max(x => x.Bucket),
            })
            .ToListAsync(ct);

        // Distinct days needs the bucket string trimmed, which SQLite will not group on
        // through EF, so it is counted here instead.
        var days = await db.MapStatBuckets.AsNoTracking()
            .Select(b => new { b.Dataset, b.Bucket })
            .ToListAsync(ct);

        var dayCount = days
            .GroupBy(x => x.Dataset)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Bucket[..10]).Distinct().Count());

        return rows
            // Min/Max come back nullable from the grouping even though a group always has rows.
            .Select(r => new DatasetCoverage(
                r.Dataset, r.Buckets, dayCount.GetValueOrDefault(r.Dataset),
                r.Earliest ?? "", r.Latest ?? ""))
            .OrderBy(r => r.Dataset)
            .ToList();
    }

    /// <summary>
    /// Rebuilds the database file so deleted pages are actually released. SQLite keeps freed
    /// pages for reuse otherwise, so a large delete does not shrink the file on disk. Cannot
    /// run inside a transaction, and rewrites the whole file, so this is reserved for one-off
    /// compactions rather than the daily rollup.
    /// </summary>
    public async Task VacuumAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(20));
        await db.Database.ExecuteSqlRawAsync("VACUUM", ct);
    }

    private static readonly string[] DailyThinnedTables =
        ["MapSovereignties", "MapSovStructures", "MapIndustryIndices"];

    /// <summary>Small state tables, thinned on the same schedule as the hourly counters.
    /// Faction warfare is 160 rows an hour and incursions 3, so there is nothing to gain by
    /// thinning them sooner.</summary>
    private static readonly string[] SlowThinnedTables =
        ["MapFactionWarfares", "MapIncursions"];

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

        // State snapshots rather than counters, so summing them would be meaningless — they are
        // thinned to the first bucket of each day instead.
        //
        // The daily-cadence tables are thinned from yesterday rather than from the retention
        // window, because they are the ones that actually cost anything. Today keeps all its
        // hours so the current value stays fresh.
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd");

        foreach (var (table, from) in
                 DailyThinnedTables.Select(t => (t, yesterday))
                     .Concat(SlowThinnedTables.Select(t => (t, cutoff))))
        {
            // Table name is structural, so it cannot be a parameter; both lists are private
            // constants, never anything user-supplied.
            // $$ so {0} stays literal for the parameter placeholder and {{table}} interpolates.
            var sql = $$"""
                DELETE FROM "{{table}}"
                WHERE SUBSTR("Bucket", 1, 10) < {0}
                  AND "Bucket" NOT IN (
                      SELECT MIN("Bucket") FROM "{{table}}" GROUP BY SUBSTR("Bucket", 1, 10))
                """;
            await db.Database.ExecuteSqlRawAsync(sql, [from], ct);
        }

        // Bucket markers for hours whose rows have gone must go too, or gap-fill would see the
        // hour as held and never notice it is empty. The surviving marker per day is kept so
        // the day itself is not re-downloaded.
        foreach (var dataset in MapDataset.DailyCadence)
            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM "MapStatBuckets"
                WHERE "Dataset" = {0}
                  AND SUBSTR("Bucket", 1, 10) < {1}
                  AND "Bucket" NOT IN (
                      SELECT MIN("Bucket") FROM "MapStatBuckets"
                      WHERE "Dataset" = {0} GROUP BY SUBSTR("Bucket", 1, 10))
                """, [dataset, yesterday], ct);

        await tx.CommitAsync(ct);
        return affected;
    }
}
