using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Assembles the system page. Kept apart from <see cref="UniverseMapService"/>, which is about
/// map geometry — this is about one system in depth, and pulls from the SDE, the map statistics
/// and our own killmail store alike.
/// </summary>
public class SystemViewService(
    IDbContextFactory<AppDbContext> dbFactory,
    MapStatsService                 stats)
{
    // ── Header ───────────────────────────────────────────────────────────────

    /// <summary>One industry cost index, in the order the industry window lists them.</summary>
    public sealed record IndexReading(string Activity, string ShortName, double Index);

    /// <summary>
    /// The six activities, in a fixed order so the header reads the same on every system, with
    /// the abbreviations the client itself uses.
    /// </summary>
    private static readonly (string Activity, string Short)[] IndexOrder =
    [
        ("manufacturing",                   "Mfg"),
        ("researching_time_efficiency",     "TE"),
        ("researching_material_efficiency", "ME"),
        ("copying",                         "Copy"),
        ("invention",                       "Inv"),
        ("reaction",                        "Rxn"),
    ];

    public sealed record SystemHeader(
        int    SystemId,
        string Name,
        string Region,
        int    RegionId,
        string Constellation,
        int    ConstellationId,
        double Security,
        string SecurityClass,
        int    Planets,
        int    Moons,
        int    Belts,
        int    Gates,
        long?  AllianceId,
        long?  CorporationId,
        string AllianceName,
        string CorporationName,
        string LocalPirates,
        int    Power,
        int    Workforce,
        double? Adm,
        IReadOnlyList<IndexReading> Industry,
        int    MagmaticGasPerHour,
        int    SublimatedIcePerHour,
        int    Jumps1h,
        int    Jumps24h,
        int    ShipKills1h,
        int    ShipKills24h,
        int    NpcKills1h,
        int    NpcKills24h,
        int    PodKills1h,
        int    PodKills24h);

    public async Task<SystemHeader?> GetHeaderAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var s = await db.SdeSolarSystems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SolarSystemId == systemId, ct);
        if (s is null) return null;

        var region = await db.SdeRegions.AsNoTracking()
            .Where(r => r.RegionId == s.RegionId).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? "";
        var constellation = await db.SdeConstellations.AsNoTracking()
            .Where(c => c.ConstellationId == s.ConstellationId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";

        var celestials = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId)
            .GroupBy(c => c.Kind)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

        // No ice-belt count: modern ice belts are dynamic anomalies, not mapped celestials.
        // The SDE does define an "Ice Field" and an "Ice Belt" type, but zero celestials use
        // either — all 40,928 belts are the plain "Asteroid Belt" type — so a separate count
        // could only ever read zero and would imply the system has none when in fact we
        // cannot see them at all.

        var sov = (await stats.GetLatestSovereigntyAsync(ct)).GetValueOrDefault(systemId);

        var names = new Dictionary<long, string>();
        if (sov is not null)
        {
            var ids = new[] { sov.AllianceId ?? 0, sov.CorporationId ?? 0 }.Where(i => i > 0).ToList();
            if (ids.Count > 0)
                names = await db.UniverseNames.AsNoTracking()
                    .Where(n => ids.Contains(n.EntityId))
                    .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);
        }

        // "1h" is the newest hourly bucket — the same granularity CCP publishes — and "24h" is
        // the rolling day, which has to come through the windowed accessor because hourly rows
        // are retained for only a day.
        // Power and workforce are produced per planet but pooled per system — that pool is what
        // sovereignty upgrades draw on, so the total belongs in the header while the per-planet
        // breakdown stays on the celestials tree.
        var planets = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId && c.Kind == 0)
            .Select(c => new { c.ItemId, c.TypeId })
            .ToListAsync(ct);
        var planetIds = planets.Select(x => x.ItemId).ToList();
        var res = await db.SdePlanetResources.AsNoTracking()
            .Where(r => planetIds.Contains(r.PlanetId))
            .ToListAsync(ct);
        var byPlanet = res.ToDictionary(r => r.PlanetId, r => r);

        int PerHour(int typeId) => planets
            .Where(pl => pl.TypeId == typeId && byPlanet.ContainsKey(pl.ItemId))
            .Sum(pl => byPlanet[pl.ItemId] is var r && r.ReagentCycleTime > 0
                ? (int)Math.Round(r.ReagentPerCycle * 3600.0 / r.ReagentCycleTime) : 0);

        var adm = (await stats.GetLatestAdmAsync(ct)).TryGetValue(systemId, out var admV)
            ? admV : (double?)null;
        // All six activities from the newest bucket, in the fixed display order. Read in one
        // query for this system rather than six universe-wide dictionaries.
        var newestIndexBucket = await db.MapIndustryIndices.AsNoTracking()
            .OrderByDescending(i => i.Bucket).Select(i => i.Bucket).FirstOrDefaultAsync(ct);
        var indices = newestIndexBucket is null
            ? []
            : await db.MapIndustryIndices.AsNoTracking()
                .Where(i => i.Bucket == newestIndexBucket && i.SystemId == systemId)
                .ToDictionaryAsync(i => i.Activity, i => i.CostIndex, ct);

        var industry = IndexOrder
            .Where(o => indices.ContainsKey(o.Activity))
            .Select(o => new IndexReading(o.Activity, o.Short, indices[o.Activity]))
            .ToList();

        var pirates = await GetLocalPiratesAsync(ct);

        var hour = await LatestHourAsync(db, ct);
        var last = hour is null ? null : await OneBucketAsync(db, systemId, hour, ct);
        var day  = (await stats.GetActivityWindowAsync(1, ct)).GetValueOrDefault(systemId);

        return new SystemHeader(
            s.SolarSystemId, s.Name, region, s.RegionId, constellation, s.ConstellationId,
            s.Security, s.SecurityClass,
            celestials.GetValueOrDefault(0), celestials.GetValueOrDefault(1),
            celestials.GetValueOrDefault(3), celestials.GetValueOrDefault(2),
            sov?.AllianceId, sov?.CorporationId,
            sov?.AllianceId is { } a ? names.GetValueOrDefault(a, $"Alliance {a}") : "",
            sov?.CorporationId is { } c ? names.GetValueOrDefault(c, $"Corporation {c}") : "",
            pirates.GetValueOrDefault(s.RegionId, ""),
            res.Sum(r => r.Power), res.Sum(r => r.Workforce),
            adm, industry,
            PerHour(2015), PerHour(12),
            last?.ShipJumps ?? 0, day?.ShipJumps ?? 0,
            last?.ShipKills ?? 0, day?.ShipKills ?? 0,
            last?.NpcKills  ?? 0, day?.NpcKills  ?? 0,
            last?.PodKills  ?? 0, day?.PodKills  ?? 0);
    }

    private static async Task<string?> LatestHourAsync(AppDbContext db, CancellationToken ct) =>
        await db.MapSystemJumps.AsNoTracking()
            .OrderByDescending(j => j.Bucket).Select(j => j.Bucket).FirstOrDefaultAsync(ct);

    private sealed record HourStats(int ShipJumps, int ShipKills, int PodKills, int NpcKills);

    private static async Task<HourStats?> OneBucketAsync(
        AppDbContext db, int systemId, string bucket, CancellationToken ct)
    {
        var j = await db.MapSystemJumps.AsNoTracking()
            .Where(x => x.Bucket == bucket && x.SystemId == systemId)
            .Select(x => x.ShipJumps).FirstOrDefaultAsync(ct);
        var k = await db.MapSystemKills.AsNoTracking()
            .Where(x => x.Bucket == bucket && x.SystemId == systemId)
            .Select(x => new { x.ShipKills, x.PodKills, x.NpcKills }).FirstOrDefaultAsync(ct);

        return new HourStats(j, k?.ShipKills ?? 0, k?.PodKills ?? 0, k?.NpcKills ?? 0);
    }

    // ── Local pirates ────────────────────────────────────────────────────────

    /// <summary>
    /// The five pirate factions that rat known space. Empire navies and faction police also
    /// appear as NPC attackers and outnumber pirates in and near high sec, so restricting to
    /// these is what makes the verdict right: without it Genesis reads "Amarr Empire" rather
    /// than Blood Raiders.
    /// </summary>
    private static readonly int[] PirateFactions =
    [
        500010, // Guristas Pirates
        500011, // Angel Cartel
        500012, // Blood Raider Covenant
        500019, // Sansha's Nation
        500020, // Serpentis
    ];

    /// <summary>Below this many NPC kills a region's verdict is guesswork, so none is given.</summary>
    private const int MinPirateSample = 20;

    private sealed class PirateRaw
    {
        public int    RegionId  { get; set; }
        public int    FactionId { get; set; }
        public int    Kills     { get; set; }
        public int    Total     { get; set; }
    }

    private static Dictionary<int, string>? _pirateCache;
    private static readonly SemaphoreSlim PirateLock = new(1, 1);

    /// <summary>
    /// Which pirate faction rats each region, derived from our own killmails: NPC attackers
    /// carry a faction id, so the dominant pirate faction among a region's NPC kills is its
    /// local pirates. Verified against 17 regions with known lore — Genesis and Domain give
    /// Blood Raiders, Tenerifis and Curse the Angel Cartel, Catch and Stain Sansha, and so on.
    ///
    /// Cached for the session: it scans several million attacker rows, and the answer does not
    /// change on any timescale that matters.
    /// </summary>
    public async Task<Dictionary<int, string>> GetLocalPiratesAsync(CancellationToken ct = default)
    {
        if (_pirateCache is not null) return _pirateCache;

        await PirateLock.WaitAsync(ct);
        try
        {
            if (_pirateCache is not null) return _pirateCache;

            using var db = dbFactory.CreateDbContext();

            // Built from a private int array, never from input, so there is nothing to inject —
            // but assembling it outside the call keeps the analyzer's rule meaningful where it
            // matters instead of suppressed everywhere.
            var ids = string.Join(",", PirateFactions);
            var sql = $"""
                WITH att AS (
                    SELECT s."RegionId" AS "RegionId", a."FactionId" AS "FactionId",
                           COUNT(*) AS "Kills"
                    FROM "KillMailAttackers" a
                    JOIN "KillMailDetails"  k ON k."KillMailId"    = a."KillMailId"
                    JOIN "SdeSolarSystems"  s ON s."SolarSystemId" = k."SolarSystemId"
                    WHERE a."CharacterId" IS NULL AND a."FactionId" IN ({ids})
                    GROUP BY s."RegionId", a."FactionId")
                SELECT "RegionId", "FactionId", "Kills",
                       (SELECT SUM(a2."Kills") FROM att a2 WHERE a2."RegionId" = att."RegionId") AS "Total"
                FROM att
                WHERE "Kills" = (SELECT MAX(a3."Kills") FROM att a3 WHERE a3."RegionId" = att."RegionId")
                """;

            var rows = await db.Database.SqlQueryRaw<PirateRaw>(sql).ToListAsync(ct);

            var names = await db.SdeFactions.AsNoTracking()
                .Where(f => PirateFactions.Contains(f.FactionId))
                .ToDictionaryAsync(f => f.FactionId, f => f.Name, ct);

            _pirateCache = rows
                .Where(r => r.Kills >= MinPirateSample && names.ContainsKey(r.FactionId))
                .GroupBy(r => r.RegionId)
                .ToDictionary(g => g.Key, g => names[g.OrderByDescending(x => x.Kills).First().FactionId]);

            return _pirateCache;
        }
        finally { PirateLock.Release(); }
    }

    // ── Overview: sovereignty structures ─────────────────────────────────────

    public sealed record SovStructureRow(
        long            StructureId,
        int             TypeId,
        string          TypeName,
        long?           AllianceId,
        string          Owner,
        double?         Adm,
        DateTimeOffset? VulnerableStart,
        DateTimeOffset? VulnerableEnd)
    {
        /// <summary>A structure is invulnerable outside its window; inside it, it can be taken.</summary>
        public string State =>
            VulnerableStart is null || VulnerableEnd is null ? "Unknown"
            : DateTimeOffset.UtcNow >= VulnerableStart && DateTimeOffset.UtcNow < VulnerableEnd
                ? "Vulnerable"
                : "Invulnerable";

        public string Window =>
            VulnerableStart is null || VulnerableEnd is null
                ? ""
                : $"{VulnerableStart:yyyy-MM-dd HH:mm} → {VulnerableEnd:HH:mm} " +
                  $"({(VulnerableEnd - VulnerableStart).Value.TotalHours:F0}h)";
    }

    public async Task<List<SovStructureRow>> GetSovStructuresAsync(
        int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var latest = await db.MapSovStructures.AsNoTracking()
            .Where(m => m.SystemId == systemId)
            .OrderByDescending(m => m.Bucket).Select(m => m.Bucket).FirstOrDefaultAsync(ct);
        if (latest is null) return [];

        var rows = await db.MapSovStructures.AsNoTracking()
            .Where(m => m.SystemId == systemId && m.Bucket == latest)
            .ToListAsync(ct);

        var typeIds = rows.Select(r => r.StructureTypeId).Distinct().ToList();
        var types = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var allianceIds = rows.Where(r => r.AllianceId > 0).Select(r => r.AllianceId!.Value).Distinct().ToList();
        var names = await db.UniverseNames.AsNoTracking()
            .Where(n => allianceIds.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        return rows.Select(r => new SovStructureRow(
            r.StructureId, r.StructureTypeId,
            types.GetValueOrDefault(r.StructureTypeId, "Sovereignty structure"),
            r.AllianceId,
            r.AllianceId is { } a ? names.GetValueOrDefault(a, $"Alliance {a}") : "",
            r.Adm, r.VulnerableStart, r.VulnerableEnd)).ToList();
    }

    // ── Events (also feeds the Overview's sovereignty changes) ───────────────

    public sealed record SystemEvent(
        DateTimeOffset When, string Kind, string Summary, long? AllianceId);

    /// <summary>
    /// Derives a system's history by diffing consecutive snapshots for a change of holder.
    ///
    /// ADM movement is deliberately not an event. It drifts continuously and crosses the same
    /// whole number back and forth as activity rises and falls — one quiet system produced 14
    /// "events" in a month purely from oscillating between 4 and 5, which buries the changes
    /// that actually matter. Current ADM is on the Overview and its trend is on the graphs.
    ///
    /// ⚠️ This only reaches as far back as the stored snapshots — the backfill window, not the
    /// system's real history. dotlan shows years because it has been recording since long
    /// before this app existed; there is no source to recover what happened before our first
    /// snapshot. The caller states the window so the list is not mistaken for the whole story.
    /// </summary>
    public async Task<List<SystemEvent>> GetEventsAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var events = new List<SystemEvent>();

        var sov = await db.MapSovereignties.AsNoTracking()
            .Where(s => s.SystemId == systemId)
            .OrderBy(s => s.Bucket)
            .Select(s => new { s.Bucket, s.AllianceId, s.CorporationId })
            .ToListAsync(ct);

        var allianceIds = sov.Where(s => s.AllianceId > 0).Select(s => s.AllianceId!.Value).Distinct().ToList();

        var names = await db.UniverseNames.AsNoTracking()
            .Where(n => allianceIds.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        string Named(long? id) =>
            id is { } v && v > 0 ? names.GetValueOrDefault(v, $"Alliance {v}") : "no one";

        for (var i = 1; i < sov.Count; i++)
        {
            var prev = sov[i - 1];
            var cur  = sov[i];
            if (prev.AllianceId == cur.AllianceId) continue;

            events.Add(new SystemEvent(
                MapStatsService.ParseBucket(cur.Bucket),
                cur.AllianceId is null ? "Sovereignty lost" : "Sovereignty gained",
                $"{Named(prev.AllianceId)} → {Named(cur.AllianceId)}",
                cur.AllianceId));
        }

        return events.OrderByDescending(e => e.When).ToList();
    }

    /// <summary>The oldest snapshot held, so the events list can say how far back it reaches.</summary>
    public async Task<DateTimeOffset?> GetHistoryStartAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var first = await db.MapSovereignties.AsNoTracking()
            .OrderBy(s => s.Bucket).Select(s => s.Bucket).FirstOrDefaultAsync(ct);
        return first is null ? null : MapStatsService.ParseBucket(first);
    }

    // ── Activity history ─────────────────────────────────────────────────────

    public sealed record HistoryPoint(DateOnly Day, int Jumps, int ShipKills, int PodKills, int NpcKills);

    /// <summary>
    /// Daily activity for one system across the stored window.
    ///
    /// Comes from the daily rollup plus whatever hourly rows still exist for today, since the
    /// hourly tables are trimmed to a day — today has not been rolled up yet, so reading only
    /// the rollup would leave the newest point missing.
    /// </summary>
    public async Task<List<HistoryPoint>> GetHistoryAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var daily = await db.MapSystemDailies.AsNoTracking()
            .Where(d => d.SystemId == systemId)
            .Select(d => new { d.Day, d.ShipJumps, d.ShipKills, d.PodKills, d.NpcKills })
            .ToListAsync(ct);

        // Ship and pod kills are deliberately NOT taken from the rollup. It sums CCP's hourly
        // snapshots, which re-report the same kills across consecutive files, so those two
        // columns come out roughly doubled — see UniverseMapService.GetKillCountsAsync for the
        // evidence. They are counted from stored killmails below instead, which is what the map
        // overlays and the header already use, so all three now agree.
        //
        // Jumps and NPC kills stay on the rollup because nothing else records them.
        var points = daily.ToDictionary(
            d => d.Day,
            d => new HistoryPoint(DateOnly.Parse(d.Day), d.ShipJumps, 0, 0, d.NpcKills));

        var jumps = await db.MapSystemJumps.AsNoTracking()
            .Where(j => j.SystemId == systemId)
            .Select(j => new { j.Bucket, j.ShipJumps }).ToListAsync(ct);
        var kills = await db.MapSystemKills.AsNoTracking()
            .Where(k => k.SystemId == systemId)
            .Select(k => new { k.Bucket, k.ShipKills, k.PodKills, k.NpcKills }).ToListAsync(ct);

        foreach (var g in jumps.GroupBy(j => j.Bucket[..10]))
        {
            var cur = points.GetValueOrDefault(g.Key, new HistoryPoint(DateOnly.Parse(g.Key), 0, 0, 0, 0));
            points[g.Key] = cur with { Jumps = cur.Jumps + g.Sum(x => x.ShipJumps) };
        }
        foreach (var g in kills.GroupBy(k => k.Bucket[..10]))
        {
            var cur = points.GetValueOrDefault(g.Key, new HistoryPoint(DateOnly.Parse(g.Key), 0, 0, 0, 0));
            // NPC kills only, for the reason above.
            points[g.Key] = cur with { NpcKills = cur.NpcKills + g.Sum(x => x.NpcKills) };
        }

        // Ship and pod kills, counted from killmails. Each carries an exact timestamp, so a day
        // is simply a day — there is no snapshot window to double count. Capsules are group 29
        // alone; group 833 is Force Recon, which an earlier version wrongly counted as pods.
        var killmails = await db.Database.SqlQueryRaw<DailyKillRaw>(DailyKillSql, systemId)
            .ToListAsync(ct);

        foreach (var k in killmails)
        {
            if (k.Day.Length < 10) continue;
            var cur = points.GetValueOrDefault(k.Day, new HistoryPoint(DateOnly.Parse(k.Day), 0, 0, 0, 0));
            points[k.Day] = cur with { ShipKills = k.Ships, PodKills = k.Pods };
        }

        return points.Values.OrderBy(p => p.Day).ToList();
    }

    /// <summary>A pilot named on a report, with the ids needed to show their portrait, corp and
    /// alliance. Zero where we do not know.</summary>
    public sealed record IntelPilot(
        long CharacterId, string Name, string? Ship, int ShipTypeId,
        long CorporationId, long AllianceId,
        string CorporationName = "", string AllianceName = "");

    public sealed record IntelRow(
        DateTimeOffset            When,
        int                       PlayerCount,
        IReadOnlyList<IntelPilot> Pilots,
        string                    Note,
        string                    Reporter,
        long                      ReporterId,
        long                      ReporterCorpId,
        long                      ReporterAllianceId,
        string                    Channel,
        string                    Message,
        bool                      NoVisual,
        bool                      Obsolete,
        string                    ReporterCorpName     = "",
        string                    ReporterAllianceName = "");

    /// <summary>
    /// Sightings reported in this system, newest first.
    ///
    /// Superseded reports are kept and marked rather than hidden: a sighting being out of date
    /// is not the same as it never having happened, and the history of who came through a
    /// system is the point of reading this tab at all.
    /// </summary>
    public async Task<List<IntelRow>> GetIntelAsync(
        int systemId, int limit = 200, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var reports = await db.IntelReports.AsNoTracking()
            .Where(r => r.SystemId == systemId)
            .OrderByDescending(r => r.ReportedAt)
            .Take(limit)
            .Select(r => new { r.Id, r.ReportedAt, r.PlayerCount, r.Note,
                               r.ReporterName, r.ReporterCharacterId, r.ChannelName, r.Message, r.NoVisual, r.Obsolete })
            .ToListAsync(ct);

        if (reports.Count == 0) return [];

        var ids    = reports.Select(r => r.Id).ToList();
        var pilots = await db.IntelReportCharacters.AsNoTracking()
            .Where(c => ids.Contains(c.IntelReportId))
            .Select(c => new { c.IntelReportId, c.CharacterId, c.CharacterName, c.ShipName, c.ShipTypeId })
            .ToListAsync(ct);

        // One lookup for every character on the page — the pilots named and the reporters both,
        // since a reporter is a pilot like any other and is often named on other reports anyway.
        var everyone = pilots.Select(p => p.CharacterId)
            .Concat(reports.Select(r => r.ReporterCharacterId ?? 0))
            .Where(i => i > 0).Distinct().ToList();

        var affiliation = await db.CharacterAffiliations.AsNoTracking()
            .Where(a => everyone.Contains(a.CharacterId))
            .ToDictionaryAsync(a => a.CharacterId, a => a, ct);

        // Names for the logos. A corp or alliance badge with no name is a picture of a shape —
        // recognisable only to someone who already knew, which is not who needs the intel tab.
        var orgIds = affiliation.Values.Select(a => a.CorporationId)
            .Concat(affiliation.Values.Select(a => a.AllianceId))
            .Where(i => i > 0).Distinct().ToList();

        var orgNames = await db.UniverseNames.AsNoTracking()
            .Where(n => orgIds.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        string OrgName(long id) => id > 0 ? orgNames.GetValueOrDefault(id, "") : "";

        var byReport = pilots.GroupBy(p => p.IntelReportId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<IntelPilot>)g.Select(x =>
            {
                var a = affiliation.GetValueOrDefault(x.CharacterId);
                return new IntelPilot(x.CharacterId, x.CharacterName, x.ShipName, x.ShipTypeId ?? 0,
                                      a?.CorporationId ?? 0, a?.AllianceId ?? 0,
                                      OrgName(a?.CorporationId ?? 0), OrgName(a?.AllianceId ?? 0));
            }).ToList());

        return reports.Select(r =>
        {
            var rid = r.ReporterCharacterId ?? 0;
            var ra  = rid > 0 ? affiliation.GetValueOrDefault(rid) : null;

            return new IntelRow(
                DateTimeOffset.TryParse(r.ReportedAt, out var t) ? t : default,
                r.PlayerCount,
                byReport.GetValueOrDefault(r.Id, []),
                r.Note ?? "",
                r.ReporterName,
                rid,
                ra?.CorporationId ?? 0,
                ra?.AllianceId ?? 0,
                r.ChannelName,
                r.Message ?? "",
                r.NoVisual,
                r.Obsolete,
                OrgName(ra?.CorporationId ?? 0),
                OrgName(ra?.AllianceId ?? 0));
        }).ToList();
    }

    private sealed class DailyKillRaw
    {
        public string Day   { get; set; } = "";
        public int    Ships { get; set; }
        public int    Pods  { get; set; }
    }

    private const string DailyKillSql = """
        SELECT substr(k."KillMailTime", 1, 10) AS "Day",
               SUM(CASE WHEN COALESCE(ty."GroupId", 0) = 29 THEN 0 ELSE 1 END) AS "Ships",
               SUM(CASE WHEN COALESCE(ty."GroupId", 0) = 29 THEN 1 ELSE 0 END) AS "Pods"
        FROM "KillMailDetails" k
        LEFT JOIN "SdeTypes" ty ON ty."TypeId" = k."VictimShipTypeId"
        WHERE k."SolarSystemId" = {0}
        GROUP BY substr(k."KillMailTime", 1, 10)
        """;

    public sealed record AdmPoint(DateOnly Day, double Adm);

    /// <summary>
    /// Daily ADM for a system. The highest reading of each day, since a system can hold more
    /// than one sovereignty structure and the defended value is the one that matters.
    /// </summary>
    public async Task<List<AdmPoint>> GetAdmHistoryAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var rows = await db.MapSovStructures.AsNoTracking()
            .Where(m => m.SystemId == systemId && m.Adm != null)
            .Select(m => new { m.Bucket, Adm = m.Adm!.Value })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Bucket[..10])
            .Select(g => new AdmPoint(DateOnly.Parse(g.Key), g.Max(x => x.Adm)))
            .OrderBy(p => p.Day)
            .ToList();
    }

    public sealed record IndexSeries(string Activity, List<(DateOnly Day, double Index)> Points);

    /// <summary>Daily cost index per activity, one line per activity for the graph.</summary>
    public async Task<List<IndexSeries>> GetIndustryHistoryAsync(
        int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var rows = await db.MapIndustryIndices.AsNoTracking()
            .Where(i => i.SystemId == systemId)
            .Select(i => new { i.Bucket, i.Activity, i.CostIndex })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.Activity)
            .Select(g => new IndexSeries(
                g.Key,
                g.GroupBy(r => r.Bucket[..10])
                 .Select(d => (Day: DateOnly.Parse(d.Key), Index: d.Average(x => x.CostIndex)))
                 .OrderBy(p => p.Day)
                 .ToList()))
            .OrderBy(s => s.Activity, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public sealed record HourPoint(DateTimeOffset Hour, int Jumps, int ShipKills, int PodKills, int NpcKills);

    /// <summary>
    /// Hour-by-hour activity for the recent past, for the small charts on the Overview.
    ///
    /// Bounded by the hourly retention window rather than by the hours asked for: those rows are
    /// rolled into daily totals and deleted once they age out, so a 48-hour chart only fills if
    /// hourly detail is kept for at least two days.
    /// </summary>
    public async Task<List<HourPoint>> GetHourlyHistoryAsync(
        int systemId, int hours = 48, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var from = MapStatsService.BucketOf(DateTimeOffset.UtcNow.AddHours(-hours));

        var jumps = await db.MapSystemJumps.AsNoTracking()
            .Where(j => j.SystemId == systemId && string.Compare(j.Bucket, from) >= 0)
            .Select(j => new { j.Bucket, j.ShipJumps }).ToListAsync(ct);
        var kills = await db.MapSystemKills.AsNoTracking()
            .Where(k => k.SystemId == systemId && string.Compare(k.Bucket, from) >= 0)
            .Select(k => new { k.Bucket, k.ShipKills, k.PodKills, k.NpcKills }).ToListAsync(ct);

        var buckets = jumps.Select(j => j.Bucket).Concat(kills.Select(k => k.Bucket))
                           .Distinct().OrderBy(b => b).ToList();

        var jumpBy = jumps.ToDictionary(j => j.Bucket, j => j.ShipJumps);
        var killBy = kills.ToDictionary(k => k.Bucket, k => k);

        return buckets.Select(b => new HourPoint(
            MapStatsService.ParseBucket(b),
            jumpBy.GetValueOrDefault(b),
            killBy.TryGetValue(b, out var k) ? k.ShipKills : 0,
            k2(b, killBy, x => x.PodKills),
            k2(b, killBy, x => x.NpcKills))).ToList();

        static int k2<T>(string b, Dictionary<string, T> d, Func<T, int> pick) =>
            d.TryGetValue(b, out var v) ? pick(v) : 0;
    }

    // ── Celestials ───────────────────────────────────────────────────────────

    public sealed record CelestialRow(long ItemId, int TypeId, string Name, string TypeName, int Kind);

    /// <summary>
    /// One line of the celestial tree. Depth drives the indent: 0 for things orbiting the star,
    /// 1 for moons and belts of a planet, and one deeper again for anything docked at them.
    /// </summary>
    public sealed record CelestialNode(
        int Depth, string Kind, string Name, string TypeName, int TypeId, string Owner,
        int Power = 0, int Workforce = 0, int ReagentPerHour = 0, string Reagent = "",
        // Set only on the docked rows — a planet or a stargate has no owner to link to.
        long LocationId = 0, bool IsNpc = false,
        string Corporation = "", string Alliance = "",
        long CorporationId = 0, long AllianceId = 0);

    /// <summary>
    /// The system laid out as it is arranged in space rather than as separate lists: the star,
    /// then the gates, then each planet in orbital order with its belts, moons and anything
    /// docked at them nested beneath.
    ///
    /// Structures are placed two different ways because the two sources record location
    /// differently. Player structures carry a nearest-celestial id, which is exact. NPC
    /// stations carry none at all, but their names begin with the celestial they orbit
    /// ("Jita IV - Moon 6 - Ytiri Storage"), so the longest celestial name that prefixes the
    /// station name identifies it — matching longest-first, since "Jita IV" also prefixes
    /// "Jita IV - Moon 6".
    /// </summary>
    public async Task<List<CelestialNode>> GetCelestialTreeAsync(
        int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var celestials = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId)
            .Join(db.SdeTypes.AsNoTracking(), c => c.TypeId, t => t.TypeId,
                  (c, t) => new { c.ItemId, c.TypeId, c.Name, TypeName = t.Name, c.Kind, c.X, c.Y, c.Z })
            .ToListAsync(ct);
        if (celestials.Count == 0) return [];

        // Distance from the star, which is the origin of a system's own coordinates. This is
        // what puts the planets in orbital order without needing the celestial index.
        double Radius(double x, double y, double z) => Math.Sqrt(x * x + y * y + z * z);

        var structures = await GetStructuresAsync(systemId, ct);

        // The app's own table, matching GetStructuresAsync above — reading the polled one here
        // would place structures the two disagree about at different celestials.
        var players = await db.Structures.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId && s.NearestCelestialId > 0)
            .Select(s => new { s.StructureId, s.NearestCelestialId })
            .ToListAsync(ct);
        var celestialOfStructure = players.ToDictionary(p => p.StructureId, p => p.NearestCelestialId);

        // Longest name first so a moon wins over its planet.
        var byLongestName = celestials
            .Where(c => c.Kind is 0 or 1 or 3)
            .OrderByDescending(c => c.Name.Length)
            .ToList();

        var docked = new Dictionary<long, List<StructureRow>>();
        foreach (var s in structures)
        {
            long host = 0;

            if (!s.IsNpc && celestialOfStructure.TryGetValue(s.StructureId, out var cid))
                host = cid;
            else if (s.IsNpc)
                host = byLongestName.FirstOrDefault(c =>
                    s.Name.StartsWith(c.Name, StringComparison.OrdinalIgnoreCase))?.ItemId ?? 0;

            if (host == 0) continue;
            (docked.TryGetValue(host, out var list) ? list : docked[host] = []).Add(s);
        }

        var nodes = new List<CelestialNode>();

        void AddStructures(long celestialId, int depth)
        {
            if (!docked.TryGetValue(celestialId, out var list)) return;
            foreach (var s in list.OrderByDescending(s => s.IsNpc).ThenBy(s => s.Name))
                nodes.Add(new CelestialNode(
                    depth, s.IsNpc ? "Station" : "Structure", s.Name, s.TypeName, s.TypeId, s.Owner,
                    LocationId: s.StructureId, IsNpc: s.IsNpc,
                    Corporation: s.Corporation, Alliance: s.Alliance,
                    CorporationId: s.CorporationId, AllianceId: s.AllianceId));
        }

        foreach (var star in celestials.Where(c => c.Kind == 4))
            nodes.Add(new CelestialNode(0, "Star", star.Name, star.TypeName, star.TypeId, ""));

        foreach (var gate in celestials.Where(c => c.Kind == 2)
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new CelestialNode(0, "Stargate", gate.Name, gate.TypeName, gate.TypeId, ""));
            AddStructures(gate.ItemId, 1);
        }

        // Equinox production, per planet. Only Lava and Ice planets carry a reagent, and which
        // one is decided entirely by that type — the file itself never names it.
        var planetIds = celestials.Where(c => c.Kind == 0).Select(c => c.ItemId).ToList();
        var resources = await db.SdePlanetResources.AsNoTracking()
            .Where(r => planetIds.Contains(r.PlanetId))
            .ToDictionaryAsync(r => r.PlanetId, r => r, ct);

        foreach (var planet in celestials.Where(c => c.Kind == 0)
                     .OrderBy(c => Radius(c.X, c.Y, c.Z)))
        {
            resources.TryGetValue(planet.ItemId, out var res);
            var reagent = res is null || res.ReagentPerCycle == 0 ? ""
                : planet.TypeId == 2015 ? "Magmatic Gas"
                : planet.TypeId == 12   ? "Sublimated Ice"
                : "";
            var perHour = res is null || res.ReagentCycleTime <= 0 ? 0
                : (int)Math.Round(res.ReagentPerCycle * 3600.0 / res.ReagentCycleTime);

            nodes.Add(new CelestialNode(
                0, "Planet", planet.Name, planet.TypeName, planet.TypeId, "",
                res?.Power ?? 0, res?.Workforce ?? 0,
                string.IsNullOrEmpty(reagent) ? 0 : perHour, reagent));

            AddStructures(planet.ItemId, 1);

            var children = celestials
                .Where(c => c.Kind is 1 or 3
                         && c.Name.StartsWith(planet.Name + " -", StringComparison.OrdinalIgnoreCase))
                // Belts before moons, then by the trailing number so Moon 2 precedes Moon 10.
                .OrderBy(c => c.Kind == 3 ? 0 : 1)
                .ThenBy(c => TrailingNumber(c.Name))
                .ToList();

            foreach (var child in children)
            {
                nodes.Add(new CelestialNode(
                    1, child.Kind == 3 ? "Belt" : "Moon", child.Name, child.TypeName, child.TypeId, ""));
                AddStructures(child.ItemId, 2);
            }
        }

        // Anything whose host could not be identified still belongs on the page — dropping it
        // would quietly hide structures from a list that claims to show them all.
        var placed = nodes.Count(n => n.Kind is "Station" or "Structure");
        if (placed < structures.Count)
        {
            var placedNames = nodes.Where(n => n.Kind is "Station" or "Structure")
                                   .Select(n => n.Name).ToHashSet();
            foreach (var s in structures.Where(s => !placedNames.Contains(s.Name)))
                nodes.Add(new CelestialNode(
                    0, s.IsNpc ? "Station" : "Structure", s.Name, s.TypeName, s.TypeId, s.Owner));
        }

        return nodes;
    }

    /// <summary>Number at the end of a name, so "Moon 2" sorts before "Moon 10" rather than
    /// after it as text ordering would have it.</summary>
    private static int TrailingNumber(string name)
    {
        var i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name[i..], out var n) ? n : 0;
    }

    public async Task<List<CelestialRow>> GetCelestialsAsync(
        int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var rows = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId && c.Kind < 2)
            .Join(db.SdeTypes.AsNoTracking(), c => c.TypeId, t => t.TypeId,
                  (c, t) => new { c.ItemId, c.TypeId, c.Name, TypeName = t.Name, c.Kind })
            .ToListAsync(ct);

        return rows
            .Select(r => new CelestialRow(r.ItemId, r.TypeId, r.Name, r.TypeName, r.Kind))
            .OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Structures (NPC stations and player-owned) ───────────────────────────

    public sealed record StructureRow(
        long StructureId, int TypeId, string Name, string TypeName,
        string Corporation, string Alliance, string Location, bool IsNpc,
        long CorporationId = 0, long AllianceId = 0)
    {
        /// <summary>Corporation with its alliance after it — the alliance alone hides who
        /// actually owns the structure, and many corporations are in no alliance at all.</summary>
        public string Owner =>
            string.IsNullOrEmpty(Alliance) ? Corporation
            : string.IsNullOrEmpty(Corporation) ? Alliance
            : $"{Corporation}  ·  {Alliance}";
    }

    public async Task<List<StructureRow>> GetStructuresAsync(
        int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var stations = await db.SdeStations.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .ToListAsync(ct);

        // ⚠️ Structures, not EsiStructureNames. That table belongs to the polling service; this
        // one is the app's own and carries hand-entered names, systems and types for the
        // structures ESI refuses to describe. Reading the polled table here showed a system's
        // Astrahus as "Structure 1035…" while the Structure Browser showed the name someone had
        // typed for it.
        var player = await db.Structures.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .ToListAsync(ct);

        var typeIds = stations.Select(s => s.StationTypeId ?? 0)
            .Concat(player.Select(s => s.TypeId)).Where(t => t > 0).Distinct().ToList();
        var types = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // Both ids per structure: the corporation owns it and the alliance is shown beside it,
        // so fetching only one leaves the other column permanently blank.
        var ownerIds = stations.Select(s => (long)(s.CorporationId ?? 0))
            .Concat(player.Select(s => s.OwnerId))
            .Concat(player.Select(s => s.AllianceId))
            .Where(i => i > 0).Distinct().ToList();

        var corpNames = await db.SdeNpcCorporations.AsNoTracking()
            .Where(c => ownerIds.Contains(c.CorporationId))
            .ToDictionaryAsync(c => (long)c.CorporationId, c => c.Name, ct);
        var universeNames = await db.UniverseNames.AsNoTracking()
            .Where(n => ownerIds.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        string OwnerOf(long id) =>
            id <= 0 ? "" : corpNames.GetValueOrDefault(id) ?? universeNames.GetValueOrDefault(id, "");

        // Player structures record the celestial they sit at; NPC stations do not, but their
        // name begins with it, so the part before the final " - " is the location.
        static string StationLocation(string name)
        {
            var cut = name.LastIndexOf(" - ", StringComparison.Ordinal);
            return cut > 0 ? name[..cut] : "";
        }

        // The stored NearestCelestial is sometimes the generic word "Stargate"; the id resolves
        // to the actual celestial, so it is preferred where it matches something we hold.
        var celestialNames = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId)
            .ToDictionaryAsync(c => c.ItemId, c => c.Name, ct);

        return stations.Select(s => new StructureRow(
                s.StationId, s.StationTypeId ?? 0, s.Name,
                types.GetValueOrDefault(s.StationTypeId ?? 0, "Station"),
                OwnerOf(s.CorporationId ?? 0), "", StationLocation(s.Name), true,
                CorporationId: s.CorporationId ?? 0))
            .Concat(player.Select(s => new StructureRow(
                s.StructureId, s.TypeId,
                string.IsNullOrEmpty(s.Name) ? $"Structure {s.StructureId}" : s.Name,
                types.GetValueOrDefault(s.TypeId, "Unknown type"),
                OwnerOf(s.OwnerId), OwnerOf(s.AllianceId),
                celestialNames.GetValueOrDefault(s.NearestCelestialId, s.NearestCelestial),
                false,
                CorporationId: s.OwnerId, AllianceId: s.AllianceId)))
            .OrderByDescending(r => r.IsNpc).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Agents ───────────────────────────────────────────────────────────────

    public sealed record AgentRow(
        string Location, string Name, string Corporation, string Division,
        string AgentType, int Level, bool IsLocator,
        long AgentId = 0, long CorporationId = 0, long StationId = 0);

    /// <summary>
    /// Agents stationed in a system, grouped by where they sit.
    ///
    /// Agent quality is not returned because CCP removed it years ago; dotlan still shows the
    /// column but every value in it is a dash.
    /// </summary>
    public async Task<List<AgentRow>> GetAgentsAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // Agents sit at stations, and a few at other locations; matching on the station ids in
        // this system covers everything the SDE places here.
        var stations = await db.SdeStations.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .Select(s => new { s.StationId, s.Name })
            .ToListAsync(ct);
        if (stations.Count == 0) return [];

        var ids = stations.Select(s => (long)s.StationId).ToList();

        var agents = await db.SdeAgents.AsNoTracking()
            .Where(a => ids.Contains(a.LocationId))
            .ToListAsync(ct);
        if (agents.Count == 0) return [];

        var corpIds = agents.Select(a => a.CorporationId).Distinct().ToList();
        var corps = await db.SdeNpcCorporations.AsNoTracking()
            .Where(c => corpIds.Contains(c.CorporationId))
            .ToDictionaryAsync(c => c.CorporationId, c => c.Name, ct);

        var divisions = await db.SdeCorpDivisions.AsNoTracking()
            .ToDictionaryAsync(d => d.DivisionId, d => d.Name, ct);
        var types = await db.SdeAgentTypes.AsNoTracking()
            .ToDictionaryAsync(t => t.AgentTypeId, t => t.Name, ct);
        var stationNames = stations.ToDictionary(s => (long)s.StationId, s => s.Name);

        return agents
            .Select(a => new AgentRow(
                stationNames.GetValueOrDefault(a.LocationId, ""),
                a.Name,
                corps.GetValueOrDefault(a.CorporationId, ""),
                divisions.GetValueOrDefault(a.DivisionId, ""),
                // "BasicAgent" is the ordinary case and adds nothing next to the division, so
                // only the notable types are worth naming.
                types.GetValueOrDefault(a.AgentTypeId, "") is var t && t is "BasicAgent" or "" ? "" : t,
                a.Level,
                a.IsLocator,
                AgentId: a.AgentId, CorporationId: a.CorporationId, StationId: a.LocationId))
            .OrderBy(a => a.Location, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Level)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Gates ────────────────────────────────────────────────────────────────

    public sealed record GateRow(int SystemId, string Name, double Security, string RegionName, bool OutOfRegion);

    public async Task<List<GateRow>> GetGatesAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var home = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId).Select(s => s.RegionId).FirstOrDefaultAsync(ct);

        var ids = await db.Database.SqlQueryRaw<int>("""
            SELECT b."SolarSystemId" AS "Value"
            FROM "SdeStargates" a
            JOIN "SdeStargates" b ON b."StargateId" = a."DestinationStargateId"
            WHERE a."SolarSystemId" = {0}
            """, systemId).ToListAsync(ct);

        // Materialised before the record is built: EF cannot translate a projection that
        // constructs a record containing a computed expression, and fails the whole query —
        // the same trap GetRegionsAsync hit.
        var rows = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => ids.Contains(s.SolarSystemId))
            .Join(db.SdeRegions.AsNoTracking(), s => s.RegionId, r => r.RegionId,
                  (s, r) => new { s.SolarSystemId, s.Name, s.Security, s.RegionId, RegionName = r.Name })
            .ToListAsync(ct);

        return rows
            .Select(s => new GateRow(s.SolarSystemId, s.Name, s.Security, s.RegionName, s.RegionId != home))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
