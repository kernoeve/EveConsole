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
        int    Belts,
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

        var points = daily.ToDictionary(
            d => d.Day,
            d => new HistoryPoint(DateOnly.Parse(d.Day), d.ShipJumps, d.ShipKills, d.PodKills, d.NpcKills));

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
            points[g.Key] = cur with
            {
                ShipKills = cur.ShipKills + g.Sum(x => x.ShipKills),
                PodKills  = cur.PodKills  + g.Sum(x => x.PodKills),
                NpcKills  = cur.NpcKills  + g.Sum(x => x.NpcKills),
            };
        }

        return points.Values.OrderBy(p => p.Day).ToList();
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
        int Depth, string Kind, string Name, string TypeName, int TypeId, string Owner);

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

        var players = await db.EsiStructureNames.AsNoTracking()
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
                    depth, s.IsNpc ? "Station" : "Structure", s.Name, s.TypeName, s.TypeId, s.Owner));
        }

        foreach (var star in celestials.Where(c => c.Kind == 4))
            nodes.Add(new CelestialNode(0, "Star", star.Name, star.TypeName, star.TypeId, ""));

        foreach (var gate in celestials.Where(c => c.Kind == 2)
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            nodes.Add(new CelestialNode(0, "Stargate", gate.Name, gate.TypeName, gate.TypeId, ""));
            AddStructures(gate.ItemId, 1);
        }

        foreach (var planet in celestials.Where(c => c.Kind == 0)
                     .OrderBy(c => Radius(c.X, c.Y, c.Z)))
        {
            nodes.Add(new CelestialNode(0, "Planet", planet.Name, planet.TypeName, planet.TypeId, ""));
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
        string Corporation, string Alliance, string Location, bool IsNpc)
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

        var player = await db.EsiStructureNames.AsNoTracking()
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
                OwnerOf(s.CorporationId ?? 0), "", StationLocation(s.Name), true))
            .Concat(player.Select(s => new StructureRow(
                s.StructureId, s.TypeId,
                string.IsNullOrEmpty(s.Name) ? $"Structure {s.StructureId}" : s.Name,
                types.GetValueOrDefault(s.TypeId, "Unknown type"),
                OwnerOf(s.OwnerId), OwnerOf(s.AllianceId),
                celestialNames.GetValueOrDefault(s.NearestCelestialId, s.NearestCelestial),
                false)))
            .OrderByDescending(r => r.IsNpc).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
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
