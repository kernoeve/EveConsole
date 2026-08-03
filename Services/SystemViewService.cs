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
        int    Gates,
        long?  AllianceId,
        long?  CorporationId,
        string AllianceName,
        string CorporationName,
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
        var hour = await LatestHourAsync(db, ct);
        var last = hour is null ? null : await OneBucketAsync(db, systemId, hour, ct);
        var day  = (await stats.GetActivityWindowAsync(1, ct)).GetValueOrDefault(systemId);

        return new SystemHeader(
            s.SolarSystemId, s.Name, region, s.RegionId, constellation, s.ConstellationId,
            s.Security, s.SecurityClass,
            celestials.GetValueOrDefault(0), celestials.GetValueOrDefault(1), celestials.GetValueOrDefault(2),
            sov?.AllianceId, sov?.CorporationId,
            sov?.AllianceId is { } a ? names.GetValueOrDefault(a, $"Alliance {a}") : "",
            sov?.CorporationId is { } c ? names.GetValueOrDefault(c, $"Corporation {c}") : "",
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
    /// Derives a system's history by diffing consecutive snapshots: a change of holder, and a
    /// crossing of an ADM whole number.
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

        var adm = await db.MapSovStructures.AsNoTracking()
            .Where(m => m.SystemId == systemId && m.Adm != null)
            .OrderBy(m => m.Bucket)
            .Select(m => new { m.Bucket, Adm = m.Adm!.Value })
            .ToListAsync(ct);

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

        // Only whole-number crossings, because ADM drifts continuously and every hourly tick
        // would otherwise be an "event".
        for (var i = 1; i < adm.Count; i++)
        {
            var before = Math.Floor(adm[i - 1].Adm);
            var after  = Math.Floor(adm[i].Adm);
            if (Math.Abs(before - after) < 0.5) continue;

            events.Add(new SystemEvent(
                MapStatsService.ParseBucket(adm[i].Bucket),
                after > before ? "ADM increased" : "ADM decreased",
                $"{before:F0} → {after:F0}",
                null));
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

    // ── Celestials ───────────────────────────────────────────────────────────

    public sealed record CelestialRow(long ItemId, int TypeId, string Name, string TypeName, int Kind);

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
        long StructureId, int TypeId, string Name, string TypeName, string Owner, bool IsNpc);

    public async Task<List<StructureRow>> GetStructuresAsync(
        int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var stations = await db.SdeStations.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .ToListAsync(ct);

        var player = await db.EsiStructureNames.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .ToListAsync(ct);

        var typeIds = stations.Select(s => s.StationTypeId ?? 0)
            .Concat(player.Select(s => s.TypeId)).Where(t => t > 0).Distinct().ToList();
        var types = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var ownerIds = stations.Select(s => (long)(s.CorporationId ?? 0))
            .Concat(player.Select(s => s.AllianceId > 0 ? s.AllianceId : s.OwnerId))
            .Where(i => i > 0).Distinct().ToList();

        var corpNames = await db.SdeNpcCorporations.AsNoTracking()
            .Where(c => ownerIds.Contains(c.CorporationId))
            .ToDictionaryAsync(c => (long)c.CorporationId, c => c.Name, ct);
        var universeNames = await db.UniverseNames.AsNoTracking()
            .Where(n => ownerIds.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        string OwnerOf(long id) =>
            id <= 0 ? "" : corpNames.GetValueOrDefault(id) ?? universeNames.GetValueOrDefault(id, "");

        var result = stations.Select(s => new StructureRow(
                s.StationId, s.StationTypeId ?? 0, s.Name,
                types.GetValueOrDefault(s.StationTypeId ?? 0, "Station"),
                OwnerOf(s.CorporationId ?? 0), true))
            .Concat(player.Select(s => new StructureRow(
                s.StructureId, s.TypeId,
                string.IsNullOrEmpty(s.Name) ? $"Structure {s.StructureId}" : s.Name,
                types.GetValueOrDefault(s.TypeId, "Unknown type"),
                OwnerOf(s.AllianceId > 0 ? s.AllianceId : s.OwnerId), false)))
            .OrderByDescending(r => r.IsNpc).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
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
