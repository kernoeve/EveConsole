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
    /// Totals per system over a window of days, drawing from both the hourly tables and the
    /// daily rollup as needed.
    ///
    /// Callers should prefer this over the two single-source methods. Hourly rows only survive
    /// the retention window — one day by default — so asking the hourly table for a week would
    /// quietly return a day's worth and look like a real answer. The split point is the
    /// earliest day actually present in the hourly tables, which keeps the two sources disjoint
    /// even mid-rollup, when a day could otherwise appear in both and be counted twice.
    /// </summary>
    public async Task<Dictionary<int, SystemActivity>> GetActivityWindowAsync(
        int days, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var fromDay    = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        var fromBucket = fromDay + " 00";

        var earliestJump = await db.MapSystemJumps.AsNoTracking()
            .OrderBy(j => j.Bucket).Select(j => j.Bucket).FirstOrDefaultAsync(ct);
        var earliestKill = await db.MapSystemKills.AsNoTracking()
            .OrderBy(k => k.Bucket).Select(k => k.Bucket).FirstOrDefaultAsync(ct);

        var earliestHourly = new[] { earliestJump, earliestKill }
            .Where(b => !string.IsNullOrEmpty(b))
            .OrderBy(b => b)
            .FirstOrDefault();

        var hourly = await GetActivityAsync(fromBucket, ct);

        // Days at or after the hourly floor are already covered above.
        var dailyCeiling = earliestHourly is null ? null : earliestHourly[..10];
        var daily = await GetActivityByDayAsync(fromDay, dailyCeiling, ct);

        return Merge(
            hourly.Values.Select(a => (a.SystemId, a.ShipJumps, a.ShipKills, a.PodKills, a.NpcKills)),
            daily .Values.Select(a => (a.SystemId, a.ShipJumps, a.ShipKills, a.PodKills, a.NpcKills)));
    }

    /// <summary>
    /// Totals per system from the hourly tables only, from <paramref name="fromBucket"/> onward.
    /// Bounded by the hourly retention window — use <see cref="GetActivityWindowAsync"/> unless
    /// the window is certain to fit inside it.
    /// </summary>
    public async Task<Dictionary<int, SystemActivity>> GetActivityAsync(
        string fromBucket, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var from = fromBucket;

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

    /// <summary>
    /// Totals per system from the daily rollup, over [<paramref name="fromDay"/>,
    /// <paramref name="beforeDay"/>). The exclusive upper bound is how the caller avoids
    /// double-counting days that also still have hourly rows.
    /// </summary>
    public async Task<Dictionary<int, SystemActivity>> GetActivityByDayAsync(
        string fromDay, string? beforeDay = null, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var from = fromDay;

        var rows = await db.MapSystemDailies.AsNoTracking()
            .Where(d => string.Compare(d.Day, from) >= 0
                     && (beforeDay == null || string.Compare(d.Day, beforeDay) < 0))
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
    /// <summary>Who holds a system and how well defended it is, for the sovereignty overlay.</summary>
    public sealed record SovOverlayEntry(long? AllianceId, string Holder, double? Adm);

    /// <summary>
    /// Latest sovereignty snapshot joined to the newest ADM reading, with holder names resolved
    /// from the entity-name cache where they are known.
    /// </summary>
    public async Task<Dictionary<int, SovOverlayEntry>> GetSovereigntyOverlayAsync(
        CancellationToken ct = default)
    {
        var sov = await GetLatestSovereigntyAsync(ct);
        if (sov.Count == 0) return [];

        var adm = await GetLatestAdmAsync(ct);

        using var db = dbFactory.CreateDbContext();
        var ids = sov.Values.Where(s => s.AllianceId is not null)
                            .Select(s => s.AllianceId!.Value).Distinct().ToList();

        // Names come from the shared cache, which fills in as other features resolve entities.
        // An unresolved alliance simply shows its id rather than blocking the overlay.
        var names = await db.UniverseNames.AsNoTracking()
            .Where(n => ids.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        return sov.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var s = kv.Value;
                var holder = s.AllianceId is { } a
                    ? names.GetValueOrDefault(a, $"Alliance {a}")
                    : s.FactionId is { } f ? $"Faction {f}" : "Unclaimed";

                // TryGetValue, not GetValueOrDefault: the latter yields 0.0 for a system with
                // no sovereignty structure, which is a real ADM value and would print "0.0"
                // under every high-sec system instead of leaving the caption empty.
                double? admValue = adm.TryGetValue(kv.Key, out var found) ? found : null;
                return new SovOverlayEntry(s.AllianceId, holder, admValue);
            });
    }

    /// <summary>Most recent cost index per system for one industry activity.</summary>
    public async Task<Dictionary<int, double>> GetLatestIndustryAsync(
        string activity, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var latest = await db.MapIndustryIndices.AsNoTracking()
            .OrderByDescending(i => i.Bucket).Select(i => i.Bucket).FirstOrDefaultAsync(ct);
        if (latest is null) return [];

        return await db.MapIndustryIndices.AsNoTracking()
            .Where(i => i.Bucket == latest && i.Activity == activity)
            .ToDictionaryAsync(i => i.SystemId, i => i.CostIndex, ct);
    }

    /// <summary>Latest faction-warfare state per system.</summary>
    public async Task<Dictionary<int, MapFactionWarfare>> GetLatestFactionWarfareAsync(
        CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var latest = await db.MapFactionWarfares.AsNoTracking()
            .OrderByDescending(f => f.Bucket).Select(f => f.Bucket).FirstOrDefaultAsync(ct);
        if (latest is null) return [];

        return await db.MapFactionWarfares.AsNoTracking()
            .Where(f => f.Bucket == latest)
            .ToDictionaryAsync(f => f.SystemId, f => f, ct);
    }

    /// <summary>Latest incursions, keyed by constellation — which is how CCP scopes them.</summary>
    public async Task<Dictionary<int, MapIncursion>> GetLatestIncursionsAsync(
        CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var latest = await db.MapIncursions.AsNoTracking()
            .OrderByDescending(i => i.Bucket).Select(i => i.Bucket).FirstOrDefaultAsync(ct);
        if (latest is null) return [];

        return await db.MapIncursions.AsNoTracking()
            .Where(i => i.Bucket == latest)
            .ToDictionaryAsync(i => i.ConstellationId, i => i, ct);
    }

    /// <summary>Faction id to name, from the SDE.</summary>
    public async Task<Dictionary<int, string>> GetFactionNamesAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.SdeFactions.AsNoTracking()
            .ToDictionaryAsync(f => f.FactionId, f => f.Name, ct);
    }

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
