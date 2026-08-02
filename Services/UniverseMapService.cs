using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>A drawable point on a map — a region on the universe map, a system on a region map.</summary>
public sealed record MapNode(
    int    Id,
    string Name,
    double X,
    double Y,
    double Security,
    bool   IsWormhole,
    int?   FactionId = null,
    string SecurityClass = "",
    /// <summary>Set on a region map for systems that belong to a neighbouring region and are
    /// only drawn because a gate reaches them.</summary>
    bool   IsOutsideRegion = false,
    string RegionName = "",
    /// <summary>Zero on the universe map, where the nodes are regions.</summary>
    int    ConstellationId = 0);

/// <summary>An undirected jump between two nodes. Stored once per pair, low id first.</summary>
public sealed record MapEdge(int FromId, int ToId);

public sealed record MapGraph(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges);

public sealed record RegionSummary(int RegionId, string Name, bool IsWormhole, int SystemCount);

/// <summary>
/// Map geometry for the Universe tool.
///
/// Two different coordinate sources are used on purpose. Regions are placed by projecting
/// their galactic position (X, -Z), because CCP publishes no 2D layout above system level.
/// Systems are placed by CCP's own published 2D layout (X2D/Y2D) — the arrangement the
/// in-game map draws — which is dramatically more readable than projecting them: measured
/// across eight dense regions, a raw projection leaves 4-12 pairs of systems overlapping,
/// while CCP's layout leaves none. X2D/Y2D covers New Eden only, so wormhole and abyssal
/// systems fall back to the projection.
/// </summary>
public class UniverseMapService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// Region ids below this are New Eden proper (70 regions, including Pochven). Above it are
    /// Anoikis (11*), abyssal (12*) and the VR-* pockets (14*), which all carry real coordinates
    /// hundreds of light years from the cluster — plotting them stretches the universe map from
    /// 78 ly wide to 1,227 ly and squashes known space into a dot.
    ///
    /// The <c>IsWormhole</c> flag is NOT a substitute: only the 11* regions set it, so filtering
    /// on it still lets the abyssal and VR-* regions through.
    /// </summary>
    private const int MaxKnownSpaceRegionId = 11_000_000;

    /// <summary>Gates point at the gate on the far side, so joining a gate to its destination
    /// gate yields the system pair. There is no separate adjacency table.</summary>
    private const string LinkSql = """
        SELECT a."SolarSystemId" AS "FromId", b."SolarSystemId" AS "ToId"
        FROM "SdeStargates" a
        JOIN "SdeStargates" b ON b."StargateId" = a."DestinationStargateId"
        """;

    private sealed class LinkRaw
    {
        public int FromId { get; set; }
        public int ToId   { get; set; }
    }

    public async Task<List<RegionSummary>> GetRegionsAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.SdeRegions.AsNoTracking()
            .Select(r => new RegionSummary(
                r.RegionId, r.Name, r.IsWormhole,
                db.SdeSolarSystems.Count(s => s.RegionId == r.RegionId)))
            .OrderBy(r => r.Name)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Region-level map of the cluster. Nodes are regions projected top-down from their galactic
    /// position; edges are regions joined by at least one gate. Known space only — see
    /// <see cref="MaxKnownSpaceRegionId"/>.
    /// </summary>
    public async Task<MapGraph> GetUniverseGraphAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => r.RegionId < MaxKnownSpaceRegionId)
            .Select(r => new { r.RegionId, r.Name, r.IsWormhole, r.FactionId, r.X, r.Z })
            .ToListAsync(ct);

        var systemSecurity = await db.SdeSolarSystems.AsNoTracking()
            .GroupBy(s => s.RegionId)
            .Select(g => new { RegionId = g.Key, Avg = g.Average(s => s.Security) })
            .ToDictionaryAsync(g => g.RegionId, g => g.Avg, ct);

        // (X, -Z) is CCP's documented top-down projection; Y is the out-of-plane axis. Negating
        // Z is what puts galactic north at the top of the screen, since screen Y grows downward.
        var nodes = regions.Select(r => new MapNode(
            r.RegionId, r.Name, r.X, -r.Z,
            systemSecurity.GetValueOrDefault(r.RegionId), r.IsWormhole, r.FactionId)).ToList();

        var known = nodes.Select(n => n.Id).ToHashSet();

        var links = await db.Database.SqlQueryRaw<LinkRaw>($"""
            SELECT DISTINCT
                   MIN(a."RegionId", b."RegionId") AS "FromId",
                   MAX(a."RegionId", b."RegionId") AS "ToId"
            FROM ({LinkSql}) l
            JOIN "SdeSolarSystems" a ON a."SolarSystemId" = l."FromId"
            JOIN "SdeSolarSystems" b ON b."SolarSystemId" = l."ToId"
            WHERE a."RegionId" <> b."RegionId"
            """).ToListAsync(ct);

        var edges = links
            .Where(l => known.Contains(l.FromId) && known.Contains(l.ToId))
            .Select(l => new MapEdge(l.FromId, l.ToId))
            .Distinct()
            .ToList();

        return new MapGraph(nodes, edges);
    }

    /// <summary>
    /// One region's systems and the jumps between them, plus the immediate systems just over
    /// each border so the exits are visible rather than dangling.
    /// </summary>
    public async Task<MapGraph> GetRegionGraphAsync(int regionId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var all = await db.Database.SqlQueryRaw<LinkRaw>($"""
            SELECT DISTINCT
                   MIN(l."FromId", l."ToId") AS "FromId",
                   MAX(l."FromId", l."ToId") AS "ToId"
            FROM ({LinkSql}) l
            JOIN "SdeSolarSystems" a ON a."SolarSystemId" = l."FromId"
            JOIN "SdeSolarSystems" b ON b."SolarSystemId" = l."ToId"
            WHERE a."RegionId" = {regionId} OR b."RegionId" = {regionId}
            """).ToListAsync(ct);

        var ids = all.SelectMany(l => new[] { l.FromId, l.ToId }).Distinct().ToList();

        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => ids.Contains(s.SolarSystemId) || s.RegionId == regionId)
            .Join(db.SdeRegions.AsNoTracking(), s => s.RegionId, r => r.RegionId,
                  (s, r) => new { S = s, RegionName = r.Name })
            .ToListAsync(ct);

        var nodes = systems.Select(x => new MapNode(
            x.S.SolarSystemId, x.S.Name,
            // CCP's layout where it exists, otherwise the top-down projection. Mixing the two in
            // one view would be meaningless; verified that no region contains both mapped and
            // unmapped systems, so a given map only ever uses one source.
            //
            // ⚠️ The two sources disagree on the sign of the vertical axis. position2D grows
            // NORTHWARD (Branch and Venal hold the largest Y2D), while the projection's -Z grows
            // SOUTHWARD. Screen Y grows downward, so position2D must be negated to match.
            x.S.X2D ?? x.S.X,
            x.S.Y2D is double y2d ? -y2d : -x.S.Z,
            x.S.Security, x.S.IsWormhole, x.S.FactionId, x.S.SecurityClass,
            IsOutsideRegion: x.S.RegionId != regionId,
            RegionName: x.RegionName,
            ConstellationId: x.S.ConstellationId)).ToList();

        var present = nodes.Select(n => n.Id).ToHashSet();
        var edges = all
            .Where(l => present.Contains(l.FromId) && present.Contains(l.ToId))
            .Select(l => new MapEdge(l.FromId, l.ToId))
            .ToList();

        return new MapGraph(nodes, edges);
    }

    // ── Overlay data ─────────────────────────────────────────────────────────

    private sealed class CountRaw
    {
        public int Key   { get; set; }
        public int Total { get; set; }
    }

    /// <summary>
    /// Killmails per system (or per region) over the last <paramref name="days"/> days, from our
    /// own stored kills — so it reflects whatever the zKillboard/ESI pipeline has captured, not
    /// a universe-wide truth.
    /// </summary>
    public async Task<Dictionary<int, int>> GetKillCountsAsync(
        int days, bool byRegion, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // KillMailTime is a DateTimeOffset, which SQLite cannot compare in a LINQ Where — EF
        // throws "could not be translated". The stored format is sortable, so compare as text.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss+00:00");
        var keyCol = byRegion ? "s.\"RegionId\"" : "k.\"SolarSystemId\"";

        // $$ so that {0} stays literal for the parameter placeholder and {{...}} interpolates.
        var rows = await db.Database.SqlQueryRaw<CountRaw>($$"""
            SELECT {{keyCol}} AS "Key", COUNT(*) AS "Total"
            FROM "KillMailDetails" k
            JOIN "SdeSolarSystems" s ON s."SolarSystemId" = k."SolarSystemId"
            WHERE k."KillMailTime" >= {0}
            GROUP BY {{keyCol}}
            """, cutoff).ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Total);
    }

    /// <summary>
    /// NPC station count per system (or per region).
    ///
    /// ⚠️ Region is resolved by joining through the solar system rather than reading
    /// <c>SdeStations.RegionId</c>: the SDE importer leaves that column (and ConstellationId)
    /// at 0 on every row, so grouping by it silently yields an empty result rather than an
    /// error. Only SolarSystemId is populated.
    /// </summary>
    public async Task<Dictionary<int, int>> GetStationCountsAsync(
        bool byRegion, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var q = db.SdeStations.AsNoTracking();

        if (!byRegion)
            return await q.GroupBy(s => s.SolarSystemId)
                          .Select(g => new { g.Key, Total = g.Count() })
                          .ToDictionaryAsync(g => g.Key, g => g.Total, ct);

        return await q.Join(db.SdeSolarSystems.AsNoTracking(),
                            st => st.SolarSystemId, s => s.SolarSystemId, (st, s) => s.RegionId)
                      .GroupBy(rid => rid)
                      .Select(g => new { g.Key, Total = g.Count() })
                      .ToDictionaryAsync(g => g.Key, g => g.Total, ct);
    }

    public sealed record SystemDetail(
        int    SystemId,
        string Name,
        double Security,
        string SecurityClass,
        string Constellation,
        string Region,
        int    Gates,
        int    Stations,
        int    Planets,
        int    Moons);

    public async Task<SystemDetail?> GetSystemDetailAsync(int systemId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var s = await db.SdeSolarSystems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.SolarSystemId == systemId, ct);
        if (s is null) return null;

        var constellation = await db.SdeConstellations.AsNoTracking()
            .Where(c => c.ConstellationId == s.ConstellationId)
            .Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
        var region = await db.SdeRegions.AsNoTracking()
            .Where(r => r.RegionId == s.RegionId)
            .Select(r => r.Name).FirstOrDefaultAsync(ct) ?? "";

        var gates    = await db.SdeStargates.AsNoTracking().CountAsync(g => g.SolarSystemId == systemId, ct);
        var stations = await db.SdeStations.AsNoTracking().CountAsync(x => x.SolarSystemId == systemId, ct);

        // Kind: 0 planet, 1 moon, 2 stargate.
        var celestials = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId && c.Kind < 2)
            .GroupBy(c => c.Kind)
            .Select(g => new { g.Key, Total = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Total, ct);

        return new SystemDetail(
            s.SolarSystemId, s.Name, s.Security, s.SecurityClass,
            constellation, region, gates, stations,
            celestials.GetValueOrDefault(0), celestials.GetValueOrDefault(1));
    }

    public sealed record RegionDetail(
        int    RegionId,
        string Name,
        int    Systems,
        int    Constellations,
        int    Stations,
        double AvgSecurity,
        int    Gateways);

    public async Task<RegionDetail?> GetRegionDetailAsync(int regionId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var r = await db.SdeRegions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.RegionId == regionId, ct);
        if (r is null) return null;

        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => s.RegionId == regionId)
            .Select(s => s.Security).ToListAsync(ct);

        var constellations = await db.SdeConstellations.AsNoTracking()
            .CountAsync(c => c.RegionId == regionId, ct);

        // Via the system, not SdeStations.RegionId — see GetStationCountsAsync.
        var stations = await db.SdeStations.AsNoTracking()
            .Join(db.SdeSolarSystems.AsNoTracking(),
                  st => st.SolarSystemId, s => s.SolarSystemId, (st, s) => s.RegionId)
            .CountAsync(rid => rid == regionId, ct);

        var gateways = await db.Database.SqlQueryRaw<CountRaw>($"""
            SELECT 0 AS "Key", COUNT(*) AS "Total" FROM (
                SELECT DISTINCT l."FromId", l."ToId"
                FROM ({LinkSql}) l
                JOIN "SdeSolarSystems" a ON a."SolarSystemId" = l."FromId"
                JOIN "SdeSolarSystems" b ON b."SolarSystemId" = l."ToId"
                WHERE a."RegionId" = {regionId} AND b."RegionId" <> {regionId})
            """).FirstOrDefaultAsync(ct);

        return new RegionDetail(
            r.RegionId, r.Name, systems.Count, constellations, stations,
            systems.Count > 0 ? systems.Average() : 0,
            gateways?.Total ?? 0);
    }
}
