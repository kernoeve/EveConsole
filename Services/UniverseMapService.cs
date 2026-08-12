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
    int    ConstellationId = 0,
    string ConstellationName = "",
    /// <summary>Which zoom tier this node belongs to on a continuous map: 0 draws when zoomed
    /// out, 1 when zoomed in. Both tiers live in one graph so zooming reveals detail rather
    /// than navigating to a different map.</summary>
    int    Tier = 0);

/// <summary>An undirected jump between two nodes. Stored once per pair, low id first.</summary>
public sealed record MapEdge(int FromId, int ToId, int Tier = 0);

public sealed record MapGraph(IReadOnlyList<MapNode> Nodes, IReadOnlyList<MapEdge> Edges)
{
    /// <summary>True when the graph carries both zoom tiers, so the canvas should switch between
    /// them on zoom instead of drawing everything.</summary>
    public bool IsContinuous { get; init; }
}

/// <summary>A searchable place: a region or a system, with what is needed to navigate to it.
/// SystemId is 0 for a region.</summary>
public sealed record PlaceMatch(string Name, string Detail, int RegionId, int SystemId)
{
    public override string ToString() => Name;
}

public sealed record RegionSummary(
    int RegionId, string Name, bool IsWormhole, int SystemCount,
    /// <summary>False for Anoikis, abyssal and VR-* regions, which have no place on the map.</summary>
    bool IsKnownSpace);

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

    // The queries below are compile-time constants with {0}-style placeholders for their
    // values, rather than strings interpolated at the call site. Same SQL, but the values go
    // through parameters, which is what keeps EF1002 quiet and the injection surface at zero.

    private const string RegionLinksSql = $$"""
        SELECT DISTINCT
               MIN(a."RegionId", b."RegionId") AS "FromId",
               MAX(a."RegionId", b."RegionId") AS "ToId"
        FROM ({{LinkSql}}) l
        JOIN "SdeSolarSystems" a ON a."SolarSystemId" = l."FromId"
        JOIN "SdeSolarSystems" b ON b."SolarSystemId" = l."ToId"
        WHERE a."RegionId" <> b."RegionId"
        """;

    private const string RegionSystemLinksSql = $$"""
        SELECT DISTINCT
               MIN(l."FromId", l."ToId") AS "FromId",
               MAX(l."FromId", l."ToId") AS "ToId"
        FROM ({{LinkSql}}) l
        JOIN "SdeSolarSystems" a ON a."SolarSystemId" = l."FromId"
        JOIN "SdeSolarSystems" b ON b."SolarSystemId" = l."ToId"
        WHERE a."RegionId" = {0} OR b."RegionId" = {0}
        """;

    private const string RegionGatewayCountSql = $$"""
        SELECT 0 AS "Key", COUNT(*) AS "Total" FROM (
            SELECT DISTINCT l."FromId", l."ToId"
            FROM ({{LinkSql}}) l
            JOIN "SdeSolarSystems" a ON a."SolarSystemId" = l."FromId"
            JOIN "SdeSolarSystems" b ON b."SolarSystemId" = l."ToId"
            WHERE a."RegionId" = {0} AND b."RegionId" <> {0})
        """;

    /// <summary>Which victim hulls to count.</summary>
    public enum KillKind { All, Ships, Pods }

    /// <summary>
    /// Capsules. Both capsule hulls — the standard one and the Genolution 'Auroral' variant —
    /// sit in this one group, so ships and pods split cleanly on a single id. Verified against
    /// the SDE rather than assumed: no other group in it carries a podded victim.
    /// </summary>
    private const int CapsuleGroupId = 29;

    private static string KillCountSql(bool byRegion, KillKind kind)
    {
        // The hull join is only worth its cost when a kind is actually being filtered on.
        var join = kind == KillKind.All
            ? ""
            : """LEFT JOIN "SdeTypes" ty ON ty."TypeId" = k."VictimShipTypeId" """;

        // COALESCE, and a left join, so a hull the SDE has not heard of yet counts as a ship
        // rather than vanishing from both overlays.
        var filter = kind switch
        {
            KillKind.Pods  => $$"""AND COALESCE(ty."GroupId", 0) =  {{CapsuleGroupId}}""",
            KillKind.Ships => $$"""AND COALESCE(ty."GroupId", 0) <> {{CapsuleGroupId}}""",
            _              => "",
        };

        return byRegion
            ? $$"""
                SELECT s."RegionId" AS "Key", COUNT(*) AS "Total"
                FROM "KillMailDetails" k
                JOIN "SdeSolarSystems" s ON s."SolarSystemId" = k."SolarSystemId"
                {{join}}
                WHERE k."KillMailTime" >= {0} {{filter}}
                GROUP BY s."RegionId"
                """
            : $$"""
                SELECT k."SolarSystemId" AS "Key", COUNT(*) AS "Total"
                FROM "KillMailDetails" k
                {{join}}
                WHERE k."KillMailTime" >= {0} {{filter}}
                GROUP BY k."SolarSystemId"
                """;
    }

    private sealed class LinkRaw
    {
        public int FromId { get; set; }
        public int ToId   { get; set; }
    }

    /// <summary>
    /// Regions and systems in one list for the jump box. Systems carry their security and
    /// region so near-identical names can be told apart, and matches that start with the typed
    /// text rank above ones that merely contain it.
    /// </summary>
    public async Task<List<PlaceMatch>> SearchPlacesAsync(
        string text, int limit = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return [];
        var q = text.Trim();

        // ⚠️ Lower-cased on both sides: string.Contains becomes SQLite's instr(), which is
        // case-sensitive, so "jita" would find nothing while "Jita" found the system. Not a LIKE,
        // so the % and _ wildcards are never exposed to what the user typed.
        var needle = q.ToLowerInvariant();

        using var db = dbFactory.CreateDbContext();

        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => r.RegionId < MaxKnownSpaceRegionId && r.Name.ToLower().Contains(needle))
            .Select(r => new { r.Name, r.RegionId })
            .Take(limit)
            .ToListAsync(ct);

        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => s.Name.ToLower().Contains(needle))
            .Join(db.SdeRegions.AsNoTracking(), s => s.RegionId, r => r.RegionId,
                  (s, r) => new { s.Name, s.SolarSystemId, s.RegionId, Region = r.Name, s.Security })
            .Take(limit * 2)
            .ToListAsync(ct);

        return regions
            .Select(r => new PlaceMatch(r.Name, "Region", r.RegionId, 0))
            .Concat(systems.Select(s => new PlaceMatch(
                s.Name, $"{s.Security:F1}  ·  {s.Region}", s.RegionId, s.SolarSystemId)))
            .OrderByDescending(p => p.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.SystemId == 0 ? 0 : 1)   // regions before systems at equal rank
            .ThenBy(p => p.Name.Length)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    public async Task<List<RegionSummary>> GetRegionsAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // Projected to an anonymous type and ordered in memory: EF cannot translate an OrderBy
        // that reads a property off a constructed record, so sorting after the Select throws.
        var rows = await db.SdeRegions.AsNoTracking()
            .Select(r => new
            {
                r.RegionId,
                r.Name,
                r.IsWormhole,
                Systems = db.SdeSolarSystems.Count(s => s.RegionId == r.RegionId),
            })
            .ToListAsync(ct);

        return rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new RegionSummary(
                r.RegionId, r.Name, r.IsWormhole, r.Systems,
                r.RegionId < MaxKnownSpaceRegionId))
            .ToList();
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

        var links = await db.Database.SqlQueryRaw<LinkRaw>(RegionLinksSql).ToListAsync(ct);

        var edges = links
            .Where(l => known.Contains(l.FromId) && known.Contains(l.ToId))
            .Select(l => new MapEdge(l.FromId, l.ToId))
            .Distinct()
            .ToList();

        return new MapGraph(nodes, edges);
    }

    /// <summary>
    /// The whole cluster as one map: regions to read when zoomed out, every system when zoomed
    /// in, in a single coordinate space so zooming reveals detail instead of navigating.
    ///
    /// <para>⚠️ Regions are placed at the CENTROID OF THEIR SYSTEMS in CCP's published 2D layout,
    /// not by the galactic (X, -Z) projection <see cref="GetUniverseGraphAsync"/> uses. The two
    /// are different spaces, and mixing them would put a region's box somewhere its own systems
    /// are not. Measured across the 70 known-space regions, region extents in this layout overlap
    /// in only 12 of 2,415 pairs and never by more than 8% of a region's area, so regions still
    /// read as distinct territory — which is what makes the shared space usable.</para>
    ///
    /// <para>Region ids and system ids occupy disjoint ranges, so one overlay dictionary keyed by
    /// node id serves both tiers without collision.</para>
    /// </summary>
    public async Task<MapGraph> GetContinuousGraphAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var systems = await (
            from s in db.SdeSolarSystems.AsNoTracking()
            join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
            where s.RegionId < MaxKnownSpaceRegionId && s.X2D != null && s.Y2D != null
            select new
            {
                s.SolarSystemId, s.Name, s.RegionId, s.ConstellationId,
                s.Security, s.IsWormhole, s.FactionId, s.SecurityClass,
                X = s.X2D!.Value, Y = s.Y2D!.Value,
                RegionName = r.Name,
            }).ToListAsync(ct);

        var constellationNames = await db.SdeConstellations.AsNoTracking()
            .ToDictionaryAsync(c => c.ConstellationId, c => c.Name, ct);

        // Y negated for the same reason everywhere else: position2D grows northward, screen Y
        // grows downward.
        var systemNodes = systems.Select(s => new MapNode(
            s.SolarSystemId, s.Name, s.X, -s.Y, s.Security, s.IsWormhole, s.FactionId,
            s.SecurityClass, IsOutsideRegion: false, RegionName: s.RegionName,
            ConstellationId: s.ConstellationId,
            ConstellationName: constellationNames.GetValueOrDefault(s.ConstellationId, ""),
            Tier: 1)).ToList();

        var regionMeta = await db.SdeRegions.AsNoTracking()
            .Where(r => r.RegionId < MaxKnownSpaceRegionId)
            .Select(r => new { r.RegionId, r.Name, r.IsWormhole, r.FactionId })
            .ToListAsync(ct);

        var regionNodes = regionMeta
            .Select(r =>
            {
                var members = systems.Where(s => s.RegionId == r.RegionId).ToList();
                if (members.Count == 0) return null;

                return new MapNode(
                    r.RegionId, r.Name,
                    members.Average(s => s.X), -members.Average(s => s.Y),
                    members.Average(s => s.Security), r.IsWormhole, r.FactionId,
                    Tier: 0);
            })
            .OfType<MapNode>()
            .ToList();

        var systemIds = systemNodes.Select(n => n.Id).ToHashSet();
        var regionIds = regionNodes.Select(n => n.Id).ToHashSet();

        var systemLinks = await db.Database.SqlQueryRaw<LinkRaw>(LinkSql).ToListAsync(ct);
        var regionLinks = await db.Database.SqlQueryRaw<LinkRaw>(RegionLinksSql).ToListAsync(ct);

        var edges = regionLinks
            .Where(l => regionIds.Contains(l.FromId) && regionIds.Contains(l.ToId))
            .Select(l => new MapEdge(l.FromId, l.ToId, 0))
            .Concat(systemLinks
                .Where(l => systemIds.Contains(l.FromId) && systemIds.Contains(l.ToId))
                .Select(l => new MapEdge(l.FromId, l.ToId, 1)))
            .Distinct()
            .ToList();

        return new MapGraph([.. regionNodes, .. systemNodes], edges) { IsContinuous = true };
    }

    /// <summary>
    /// One region's systems and the jumps between them, plus the immediate systems just over
    /// each border so the exits are visible rather than dangling.
    /// </summary>
    public async Task<MapGraph> GetRegionGraphAsync(int regionId, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var all = await db.Database
            .SqlQueryRaw<LinkRaw>(RegionSystemLinksSql, regionId).ToListAsync(ct);

        var ids = all.SelectMany(l => new[] { l.FromId, l.ToId }).Distinct().ToList();

        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => ids.Contains(s.SolarSystemId) || s.RegionId == regionId)
            .Join(db.SdeRegions.AsNoTracking(), s => s.RegionId, r => r.RegionId,
                  (s, r) => new { S = s, RegionName = r.Name })
            .ToListAsync(ct);

        // The whole table is ~1,200 rows, so a lookup beats widening the join above.
        var constellationNames = await db.SdeConstellations.AsNoTracking()
            .ToDictionaryAsync(c => c.ConstellationId, c => c.Name, ct);

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
            ConstellationId: x.S.ConstellationId,
            ConstellationName: constellationNames.GetValueOrDefault(x.S.ConstellationId, ""))).ToList();

        var present = nodes.Select(n => n.Id).ToHashSet();
        var edges = all
            .Where(l => present.Contains(l.FromId) && present.Contains(l.ToId))
            .Select(l => new MapEdge(l.FromId, l.ToId))
            .ToList();

        return new MapGraph(PlaceGateways(nodes, edges), edges);
    }

    /// <summary>
    /// Moves each out-of-region system from its true position to just outside the border system
    /// it connects to.
    ///
    /// Their real coordinates can be most of a region away, and since the view is framed to fit
    /// every node, one distant neighbour is enough to squash the region under inspection into a
    /// corner. Keeping the true <em>direction</em> but replacing the true distance with roughly
    /// one system's spacing preserves the sense of which way the exit leads while letting the
    /// region fill the canvas — the same thing dotlan does with its grey border boxes.
    /// </summary>
    private static List<MapNode> PlaceGateways(List<MapNode> nodes, List<MapEdge> edges)
    {
        var insideIds = nodes.Where(n => !n.IsOutsideRegion).Select(n => n.Id).ToHashSet();
        var outside   = nodes.Where(n => n.IsOutsideRegion).ToList();
        if (outside.Count == 0) return nodes;

        var byId = nodes.ToDictionary(n => n.Id);

        // Typical distance between neighbouring systems in this region, used as the offset so
        // the boxes sit a natural jump away at any region's scale.
        var intra = edges
            .Where(e => insideIds.Contains(e.FromId) && insideIds.Contains(e.ToId))
            .Select(e => Distance(byId[e.FromId], byId[e.ToId]))
            .Where(d => d > 0)
            .OrderBy(d => d)
            .ToList();

        var step = intra.Count > 0
            ? intra[intra.Count / 2] * 1.3
            : Spread(nodes.Where(n => !n.IsOutsideRegion)) * 0.25;
        if (step <= 0) step = 1;

        // Anchor each gateway to the border system(s) it actually connects to.
        var anchors = outside.ToDictionary(
            n => n.Id,
            n => edges
                .Where(e => e.FromId == n.Id || e.ToId == n.Id)
                .Select(e => e.FromId == n.Id ? e.ToId : e.FromId)
                .Where(insideIds.Contains)
                .Select(id => byId[id])
                .ToList());

        var insideNodes = nodes.Where(n => !n.IsOutsideRegion).ToList();
        var cx = insideNodes.Average(n => n.X);
        var cy = insideNodes.Average(n => n.Y);

        var moved  = new Dictionary<int, MapNode>();
        var taken  = new List<(double X, double Y)>();
        var clear  = step * 0.85;

        foreach (var group in outside.GroupBy(n => string.Join(",", anchors[n.Id]
                                                   .Select(a => a.Id).OrderBy(i => i))))
        {
            var anchorList = anchors[group.First().Id];
            if (anchorList.Count == 0) continue;   // unreachable from this region; leave it be

            var ax = anchorList.Average(a => a.X);
            var ay = anchorList.Average(a => a.Y);

            // Bearing blends where the neighbour really lies with "away from the middle of the
            // region". True bearing alone regularly pointed along the border and dropped the box
            // onto another system.
            var placed = group
                .Select(n => (Node: n, Angle: Bearing(n, ax, ay, cx, cy)))
                .OrderBy(x => x.Angle)
                .ToList();

            // Several regions can share one border system, so fan out anything too close.
            const double minGap = 0.55;   // radians, ~31 degrees
            for (var i = 1; i < placed.Count; i++)
                if (placed[i].Angle - placed[i - 1].Angle < minGap)
                    placed[i] = (placed[i].Node, placed[i - 1].Angle + minGap);

            foreach (var (node, angle) in placed)
            {
                // Look for somewhere with room, preferring the truthful bearing and only
                // rotating away from it when the whole ray stays blocked — which happens in
                // dense regions like Domain, where pushing straight out just meets more systems.
                var best      = (X: ax + Math.Cos(angle) * step, Y: ay + Math.Sin(angle) * step);
                var bestClear = double.MinValue;

                foreach (var offset in AngleOffsets)
                {
                    var a = angle + offset;
                    var found = false;

                    for (var mult = 1.0; mult <= 3.0; mult += 0.2)
                    {
                        var x = ax + Math.Cos(a) * step * mult;
                        var y = ay + Math.Sin(a) * step * mult;

                        var room = Math.Min(
                            insideNodes.Min(s => Hypot(s.X - x, s.Y - y)),
                            taken.Count == 0 ? double.MaxValue
                                             : taken.Min(t => Hypot(t.X - x, t.Y - y)));

                        if (room > bestClear) { bestClear = room; best = (x, y); }
                        if (room >= clear)    { found = true; break; }
                    }

                    if (found) break;
                }

                taken.Add(best);
                moved[node.Id] = node with { X = best.X, Y = best.Y };
            }
        }

        return nodes.Select(n => moved.GetValueOrDefault(n.Id, n)).ToList();
    }

    /// <summary>Rotations tried when the preferred bearing is blocked, nearest first so the
    /// placement stays as close to the true direction as the space allows.</summary>
    private static readonly double[] AngleOffsets =
        [0, 0.35, -0.35, 0.7, -0.7, 1.05, -1.05, 1.4, -1.4];

    /// <summary>Direction to push a gateway: mostly where the neighbouring region actually is,
    /// nudged away from the region's centre so the box lands outside rather than among the
    /// systems.</summary>
    private static double Bearing(MapNode gate, double ax, double ay, double cx, double cy)
    {
        var (tx, ty) = Normalize(gate.X - ax, gate.Y - ay);
        var (ox, oy) = Normalize(ax - cx, ay - cy);
        var x = tx + ox * 0.6;
        var y = ty + oy * 0.6;
        if (Hypot(x, y) < 1e-9) { x = ox; y = oy; }
        if (Hypot(x, y) < 1e-9) { x = 0;  y = -1; }   // degenerate: send it north
        return Math.Atan2(y, x);
    }

    private static (double X, double Y) Normalize(double x, double y)
    {
        var len = Hypot(x, y);
        return len < 1e-9 ? (0, 0) : (x / len, y / len);
    }

    private static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private static double Distance(MapNode a, MapNode b) => Math.Sqrt(
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Largest axis of a node set's bounding box; 0 when there is nothing to measure.</summary>
    private static double Spread(IEnumerable<MapNode> nodes)
    {
        var list = nodes.ToList();
        if (list.Count < 2) return 0;
        return Math.Max(list.Max(n => n.X) - list.Min(n => n.X),
                        list.Max(n => n.Y) - list.Min(n => n.Y));
    }

    // ── Overlay data ─────────────────────────────────────────────────────────

    private sealed class CountRaw
    {
        public int Key   { get; set; }
        public int Total { get; set; }
    }

    /// <summary>
    /// Killmails per system (or per region) over the last <paramref name="days"/> days, from our
    /// own stored kills, optionally split into ships and pods.
    ///
    /// This is the accurate source for ship and pod kills, and CCP's system_kills counter is
    /// not. That endpoint is documented as "the last hour ending at Last-Modified", but it
    /// demonstrably re-reports the same kills across consecutive hourly snapshots — C-FD0D on
    /// 2026-08-03 had one burst of 14 ship kills inside hour 15, reported as 12 at 15:44 and a
    /// further 15 at 16:44, summing to 31 for a day zKillboard and our own killmails both put
    /// at 18. Any multi-hour total built from those snapshots is therefore inflated, roughly
    /// doubled, and no fixed divisor corrects it.
    ///
    /// The trade here is coverage rather than accuracy: a loss nobody publishes never reaches
    /// zKillboard, so this undercounts where CCP's counter would not — chiefly solo high-sec
    /// losses to NPCs. Measured against CCP's own figure once its overlap is accounted for,
    /// that gap runs around 17%.
    /// </summary>
    public async Task<Dictionary<int, int>> GetKillCountsAsync(
        int days, bool byRegion, KillKind kind = KillKind.All, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // KillMailTime is a DateTimeOffset, which SQLite cannot compare in a LINQ Where — EF
        // throws "could not be translated". The stored format is sortable, so compare as text.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss+00:00");

        // Built into a variable rather than interpolated at the call: SqlQueryRaw warns (EF1002)
        // on an interpolated argument. The only interpolated part is a compile-time constant.
        var sql = KillCountSql(byRegion, kind);

        var rows = await db.Database
            .SqlQueryRaw<CountRaw>(sql, cutoff)
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Key, r => r.Total);
    }

    /// <summary>
    /// NPC station count per system (or per region).
    ///
    /// Region comes from joining through the solar system rather than from
    /// <c>SdeStations.RegionId</c>. That column is populated now — by the importer, and by a
    /// startup repair for databases imported before the fix — but the join is kept because it
    /// is correct whatever state the column is in. Reading it directly is what made the
    /// original bug invisible: a zero there is a legitimate-looking id, so grouping on it
    /// returned an empty result instead of failing.
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

    /// <summary>
    /// Player-owned structures per system or region, as far as this app knows them: the names
    /// cache only holds structures we have actually seen through an authenticated request, so
    /// this is a floor, not a census. Unlike NPC stations, which the SDE lists in full.
    /// </summary>
    public async Task<Dictionary<int, int>> GetPlayerStructureCountsAsync(
        bool byRegion, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var q = db.EsiStructureNames.AsNoTracking().Where(s => s.SolarSystemId > 0);

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

    /// <summary>
    /// Averages a per-system value up to its region, for showing a system-level measure on the
    /// universe map. Only systems present in the input count toward the average, so a region
    /// where most systems have no reading is not dragged down by treating absent as zero.
    /// </summary>
    public async Task<Dictionary<int, double>> GetRegionAveragesAsync(
        IReadOnlyDictionary<int, double> bySystem, CancellationToken ct = default)
    {
        if (bySystem.Count == 0) return [];

        using var db = dbFactory.CreateDbContext();
        var regionOf = await db.SdeSolarSystems.AsNoTracking()
            .Select(s => new { s.SolarSystemId, s.RegionId })
            .ToDictionaryAsync(s => s.SolarSystemId, s => s.RegionId, ct);

        return bySystem
            .Where(kv => regionOf.ContainsKey(kv.Key))
            .GroupBy(kv => regionOf[kv.Key])
            .ToDictionary(g => g.Key, g => g.Average(kv => kv.Value));
    }

    /// <summary>
    /// Totals a per-system count up to its region, for showing system activity on the universe
    /// map. Summed rather than averaged — a region's activity is what happened across all of
    /// it, unlike an index, which only makes sense as an average.
    /// </summary>
    public async Task<Dictionary<int, int>> GetRegionSumsAsync(
        IReadOnlyDictionary<int, int> bySystem, CancellationToken ct = default)
    {
        if (bySystem.Count == 0) return [];

        using var db = dbFactory.CreateDbContext();
        var regionOf = await db.SdeSolarSystems.AsNoTracking()
            .Select(s => new { s.SolarSystemId, s.RegionId })
            .ToDictionaryAsync(s => s.SolarSystemId, s => s.RegionId, ct);

        return bySystem
            .Where(kv => regionOf.ContainsKey(kv.Key))
            .GroupBy(kv => regionOf[kv.Key])
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
    }

    /// <summary>
    /// Celestials of one kind per system, or summed per region. Kind: 0 planet, 1 moon,
    /// 2 stargate, 3 asteroid belt, 4 star.
    /// </summary>
    public async Task<Dictionary<int, int>> GetCelestialCountsAsync(
        int kind, bool byRegion, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();
        var q = db.SdeCelestials.AsNoTracking().Where(c => c.Kind == kind);

        if (!byRegion)
            return await q.GroupBy(c => c.SolarSystemId)
                          .Select(g => new { g.Key, Total = g.Count() })
                          .ToDictionaryAsync(g => g.Key, g => g.Total, ct);

        return await q.Join(db.SdeSolarSystems.AsNoTracking(),
                            c => c.SolarSystemId, s => s.SolarSystemId, (c, s) => s.RegionId)
                      .GroupBy(rid => rid)
                      .Select(g => new { g.Key, Total = g.Count() })
                      .ToDictionaryAsync(g => g.Key, g => g.Total, ct);
    }

    /// <summary>
    /// Players reported in each system by the intel channels.
    ///
    /// <paramref name="includeObsolete"/> is the difference between two questions. False asks
    /// "who is there now": only sightings nothing has superseded, which is what the live
    /// overlay wants. True asks "where has anyone been reported": every sighting in the window
    /// including the retired ones, which traces the path a gang took as it was called through
    /// system after system.
    ///
    /// Counts sum PlayerCount rather than counting reports, so a single "+8" call carries the
    /// weight it should.
    /// </summary>
    public async Task<Dictionary<int, int>> GetIntelCountsAsync(
        int minutes, bool includeObsolete, bool byRegion, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // ReportedAt is an ISO-8601 string, sortable, and compared as text for the same reason
        // killmail times are — SQLite cannot translate a DateTimeOffset comparison.
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-minutes)
            .UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var q = db.IntelReports.AsNoTracking()
            .Where(r => string.Compare(r.ReportedAt, cutoff) >= 0);

        if (!includeObsolete) q = q.Where(r => !r.Obsolete);

        if (!byRegion)
            return await q.GroupBy(r => r.SystemId)
                          .Select(g => new { g.Key, Total = g.Sum(x => x.PlayerCount) })
                          .ToDictionaryAsync(g => g.Key, g => g.Total, ct);

        return await q.Join(db.SdeSolarSystems.AsNoTracking(),
                            r => r.SystemId, s => s.SolarSystemId,
                            (r, s) => new { s.RegionId, r.PlayerCount })
                      .GroupBy(x => x.RegionId)
                      .Select(g => new { g.Key, Total = g.Sum(x => x.PlayerCount) })
                      .ToDictionaryAsync(g => g.Key, g => g.Total, ct);
    }

    /// <summary>
    /// Planetary power or workforce, pooled per system or summed per region. Both are produced
    /// per planet but pooled per system — the pool is what sovereignty upgrades draw on — so
    /// the system total is the meaningful figure, not the per-planet one.
    ///
    /// Reads zero everywhere until an SDE import has run since planetary resources were added.
    /// </summary>
    public async Task<Dictionary<int, int>> GetProductionTotalsAsync(
        bool workforce, bool byRegion, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        // Joined through the celestial rather than trusting the resource row to know its system:
        // SdePlanetResources is keyed by planet id alone.
        var q = db.SdePlanetResources.AsNoTracking()
            .Join(db.SdeCelestials.AsNoTracking().Where(c => c.Kind == 0),
                  r => r.PlanetId, c => c.ItemId,
                  (r, c) => new { c.SolarSystemId, Value = workforce ? r.Workforce : r.Power });

        if (!byRegion)
            return await q.GroupBy(x => x.SolarSystemId)
                          .Select(g => new { g.Key, Total = g.Sum(x => x.Value) })
                          .Where(g => g.Total > 0)
                          .ToDictionaryAsync(g => g.Key, g => g.Total, ct);

        return await q.Join(db.SdeSolarSystems.AsNoTracking(),
                            x => x.SolarSystemId, s => s.SolarSystemId,
                            (x, s) => new { s.RegionId, x.Value })
                      .GroupBy(x => x.RegionId)
                      .Select(g => new { g.Key, Total = g.Sum(x => x.Value) })
                      .Where(g => g.Total > 0)
                      .ToDictionaryAsync(g => g.Key, g => g.Total, ct);
    }

    public sealed record SystemDetail(
        int    SystemId,
        string Name,
        double Security,
        string SecurityClass,
        string Constellation,
        string Region,
        int    RegionId,
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
            constellation, region, s.RegionId, gates, stations,
            celestials.GetValueOrDefault(0), celestials.GetValueOrDefault(1));
    }

    // ── System view ──────────────────────────────────────────────────────────

    public sealed record NeighbourRow(int SystemId, string Name, double Security, string RegionName, bool OutOfRegion);
    public sealed record StationRow(long StationId, string Name, string TypeName);
    public sealed record StructureRow(long StructureId, string Name, string TypeName, string Owner, string NearestCelestial);
    public sealed record CelestialRow(string Kind, int Count);
    public sealed record KillRow(int KillMailId, DateTimeOffset When, string ShipName, string VictimName);

    public sealed record SystemView(
        SystemDetail                  Detail,
        IReadOnlyList<NeighbourRow>   Neighbours,
        IReadOnlyList<StationRow>     Stations,
        IReadOnlyList<StructureRow>   Structures,
        IReadOnlyList<CelestialRow>   Celestials,
        IReadOnlyList<KillRow>        RecentKills);

    /// <summary>
    /// Everything the system page shows that comes from the SDE and our own stored data.
    /// Statistics (sovereignty, indices, activity) are layered on by the caller, which already
    /// holds the map-stats service.
    /// </summary>
    public async Task<SystemView?> GetSystemViewAsync(int systemId, CancellationToken ct = default)
    {
        var detail = await GetSystemDetailAsync(systemId, ct);
        if (detail is null) return null;

        using var db = dbFactory.CreateDbContext();

        var homeRegion = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .Select(s => s.RegionId).FirstOrDefaultAsync(ct);

        // Neighbours come from the same gate self-join the maps use.
        var neighbourIds = await db.Database.SqlQueryRaw<LinkRaw>(NeighbourSql, systemId).ToListAsync(ct);
        var ids = neighbourIds.Select(l => l.ToId).Distinct().ToList();

        var neighbours = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => ids.Contains(s.SolarSystemId))
            .Join(db.SdeRegions.AsNoTracking(), s => s.RegionId, r => r.RegionId,
                  (s, r) => new { s.SolarSystemId, s.Name, s.Security, s.RegionId, RegionName = r.Name })
            .ToListAsync(ct);

        var stations = await db.SdeStations.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .Join(db.SdeTypes.AsNoTracking(), s => s.StationTypeId, t => t.TypeId,
                  (s, t) => new { s.StationId, s.Name, TypeName = t.Name })
            .ToListAsync(ct);

        var structures = await db.EsiStructureNames.AsNoTracking()
            .Where(s => s.SolarSystemId == systemId)
            .ToListAsync(ct);

        var typeNames = await db.SdeTypes.AsNoTracking()
            .Where(t => structures.Select(s => s.TypeId).Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var ownerIds = structures.SelectMany(s => new[] { s.OwnerId, s.AllianceId })
                                 .Where(id => id > 0).Distinct().ToList();
        var ownerNames = await db.UniverseNames.AsNoTracking()
            .Where(n => ownerIds.Contains(n.EntityId))
            .ToDictionaryAsync(n => n.EntityId, n => n.Name, ct);

        var celestials = await db.SdeCelestials.AsNoTracking()
            .Where(c => c.SolarSystemId == systemId)
            .GroupBy(c => c.Kind)
            .Select(g => new { Kind = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var kills = await db.Database.SqlQueryRaw<KillRaw>(RecentKillsSql, systemId).ToListAsync(ct);

        return new SystemView(
            detail,
            neighbours
                .Select(n => new NeighbourRow(n.SolarSystemId, n.Name, n.Security, n.RegionName,
                                              n.RegionId != homeRegion))
                .OrderBy(n => n.Name).ToList(),
            stations.Select(s => new StationRow(s.StationId, s.Name, s.TypeName))
                    .OrderBy(s => s.Name).ToList(),
            structures.Select(s => new StructureRow(
                    s.StructureId,
                    string.IsNullOrEmpty(s.Name) ? $"Structure {s.StructureId}" : s.Name,
                    typeNames.GetValueOrDefault(s.TypeId, "Unknown type"),
                    ownerNames.GetValueOrDefault(s.AllianceId > 0 ? s.AllianceId : s.OwnerId, ""),
                    s.NearestCelestial))
                .OrderBy(s => s.Name).ToList(),
            celestials.Select(c => new CelestialRow(
                    c.Kind switch { 0 => "Planets", 1 => "Moons", 2 => "Stargates", _ => "Other" },
                    c.Count))
                .OrderBy(c => c.Kind).ToList(),
            kills.Select(k => new KillRow(
                    k.KillMailId,
                    DateTimeOffset.TryParse(k.KillMailTime, out var t) ? t : default,
                    k.ShipName ?? "Unknown ship",
                    k.VictimName ?? "NPC"))
                .ToList());
    }

    private sealed class KillRaw
    {
        public int     KillMailId   { get; set; }
        public string  KillMailTime { get; set; } = "";
        public string? ShipName     { get; set; }
        public string? VictimName   { get; set; }
    }

    private const string NeighbourSql = $$"""
        SELECT DISTINCT a."SolarSystemId" AS "FromId", b."SolarSystemId" AS "ToId"
        FROM "SdeStargates" a
        JOIN "SdeStargates" b ON b."StargateId" = a."DestinationStargateId"
        WHERE a."SolarSystemId" = {0}
        """;

    private const string RecentKillsSql = """
        SELECT k."KillMailId", k."KillMailTime",
               t."Name" AS "ShipName", u."Name" AS "VictimName"
        FROM "KillMailDetails" k
        LEFT JOIN "SdeTypes"      t ON t."TypeId"   = k."VictimShipTypeId"
        LEFT JOIN "UniverseNames" u ON u."EntityId" = k."VictimCharId"
        WHERE k."SolarSystemId" = {0}
        ORDER BY k."KillMailTime" DESC
        LIMIT 25
        """;

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

        var gateways = await db.Database
            .SqlQueryRaw<CountRaw>(RegionGatewayCountSql, regionId).FirstOrDefaultAsync(ct);

        return new RegionDetail(
            r.RegionId, r.Name, systems.Count, constellations, stations,
            systems.Count > 0 ? systems.Average() : 0,
            gateways?.Total ?? 0);
    }
}
