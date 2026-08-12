using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>A hull that can light a jump drive, with what the drive costs to use.</summary>
public sealed record JumpShip(
    int    TypeId,
    string Name,
    double BaseRangeLy,
    double FuelPerLy,
    int    FuelTypeId,
    string FuelTypeName)
{
    public override string ToString() => Name;
}

/// <summary>
/// Which systems a route is allowed to stop in. The endpoints are never filtered — you jump
/// from where you are — only the midpoints chosen for you.
/// </summary>
public enum JumpMidpoints
{
    /// <summary>Anywhere a jump drive can go.</summary>
    Any,

    /// <summary>Only systems with an NPC station, so the ship can dock between jumps.</summary>
    StationSystems,

    /// <summary>Systems with a known Fortizar or Keepstar — the structures big enough to dock a
    /// capital and worth staging out of. Between <see cref="StationSystems"/> and
    /// <see cref="KeepstarSystems"/> in strictness, and a subset of the former.</summary>
    CitadelSystems,

    /// <summary>Only systems with a known Keepstar. A subset of <see cref="CitadelSystems"/>,
    /// and usually collapses the route to one path.</summary>
    KeepstarSystems,
}

/// <summary>One jump in a planned route.</summary>
public sealed record JumpLeg(
    int    FromSystemId,
    string FromSystem,
    string FromRegion,
    double FromSecurity,
    int    ToSystemId,
    string ToSystem,
    string ToRegion,
    double ToSecurity,
    double DistanceLy,
    double Fuel);

/// <summary>A system's place on CCP's 2D map layout, already flipped for screen coordinates.</summary>
public sealed record MapPoint(int Id, double X, double Y, double Security);

/// <summary>A system that could replace a midpoint, with how far it is either side of it.</summary>
public sealed record JumpAlternative(
    int Id, string Name, string Region, double Security,
    double InLy, double OutLy, double MapX, double MapY)
{
    public string Detail => $"{Region} · in {InLy:N2} ly · out {OutLy:N2} ly";
    public override string ToString() => Name;
}

public sealed record JumpRoute(
    IReadOnlyList<JumpLeg> Legs,
    double TotalDistanceLy,
    double TotalFuel,
    string FuelTypeName,
    double MaxRangeLy,
    string? Problem = null)
{
    public bool Ok => Problem is null && Legs.Count > 0;
}

/// <summary>
/// Plans capital jump routes: which systems to jump through, how far each leg is, and what it
/// costs in isotopes.
/// </summary>
public sealed class JumpPlannerService
{
    /// <summary>Metres in a light year. Positions in the SDE are metres.</summary>
    private const double MetresPerLightYear = 9.4607304725808e15;

    /// <summary>Jump Drive Calibration: +20% range per level, so level 5 doubles the hull's range.</summary>
    private const double RangePerJdcLevel = 0.20;

    /// <summary>Jump Fuel Conservation: -10% isotopes per level.</summary>
    private const double FuelSavedPerJfcLevel = 0.10;

    /// <summary>
    /// A jump drive cannot enter high security space. EVE displays security rounded to one
    /// decimal, so 0.45 already reads as 0.5 and is closed to capitals — the cut is on the
    /// displayed value, not the raw one.
    /// </summary>
    private const double HighSecFloor = 0.45;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public JumpPlannerService(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    // Dogma attributes describing a jump drive.
    private const int AttrJumpRange    = 867;   // base range, light years
    private const int AttrFuelPerLy    = 868;   // isotopes consumed per light year
    private const int AttrFuelTypeId   = 866;   // which isotope

    private sealed record Node(int Id, string Name, string Region, double Security, double X, double Y, double Z);

    /// <summary>The three Jove regions — 230 systems no stargate reaches and no player has ever
    /// entered. They read as ordinary null sec in the SDE, so nothing else filters them out.</summary>
    private static readonly HashSet<int> JoveRegionIds = [10_000_004, 10_000_017, 10_000_019];

    private List<Node>? _systems;
    private Dictionary<int, MapPoint>? _mapPoints;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static double MaxRange(double baseRangeLy, int jdcLevel) =>
        baseRangeLy * (1.0 + RangePerJdcLevel * Math.Clamp(jdcLevel, 0, 5));

    public static double FuelFor(double distanceLy, double fuelPerLy, int jfcLevel) =>
        distanceLy * fuelPerLy * (1.0 - FuelSavedPerJfcLevel * Math.Clamp(jfcLevel, 0, 5));

    /// <summary>Hulls with a jump drive, cheapest range first. Structures are excluded — a
    /// jump bridge carries the same attributes but is not something you fly.</summary>
    public async Task<List<JumpShip>> GetShipsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var attrs = await (
            from a in db.SdeTypeDogmaAttributes.AsNoTracking()
            where a.AttributeId == AttrJumpRange
               || a.AttributeId == AttrFuelPerLy
               || a.AttributeId == AttrFuelTypeId
            select new { a.TypeId, a.AttributeId, a.Value }).ToListAsync(ct);

        var byType = attrs.GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.AttributeId, x => x.Value));

        var typeIds = byType.Keys.ToList();

        // Category 6 is Ships. The same attributes appear on Ansiblex and Jump Bridge
        // structures, which would otherwise show up in the picker.
        var ships = await (
            from t in db.SdeTypes.AsNoTracking()
            join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
            where typeIds.Contains(t.TypeId) && t.Published && g.CategoryId == 6
            select new { t.TypeId, t.Name }).ToListAsync(ct);

        var fuelNames = await db.SdeTypes.AsNoTracking()
            .Where(t => t.Name.EndsWith("Isotopes"))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var result = new List<JumpShip>();
        foreach (var s in ships)
        {
            var a = byType[s.TypeId];
            if (!a.TryGetValue(AttrJumpRange, out var range) || range <= 0) continue;

            var fuelPerLy = a.GetValueOrDefault(AttrFuelPerLy);
            var fuelType  = (int)a.GetValueOrDefault(AttrFuelTypeId);

            result.Add(new JumpShip(s.TypeId, s.Name, range, fuelPerLy, fuelType,
                fuelNames.GetValueOrDefault(fuelType, "Isotopes")));
        }

        return result.OrderByDescending(s => s.BaseRangeLy).ThenBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Systems a jump drive can reach. Loaded once — roughly four thousand rows that only
    /// change when CCP adds space.
    /// </summary>
    private async Task<List<Node>> SystemsAsync(CancellationToken ct)
    {
        if (_systems is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_systems is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var rows = await (
                from s in db.SdeSolarSystems.AsNoTracking()
                join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
                where !s.IsWormhole && s.Security < HighSecFloor
                select new { s.SolarSystemId, s.Name, s.RegionId, Region = r.Name,
                             s.Security, s.X, s.Y, s.Z })
                .ToListAsync(ct);

            // ⚠️ Jove space is excluded here and nowhere else would catch it. Routing is by 3D
            // distance and ignores gates entirely, so the 230 Jove systems — sitting inside the
            // cluster at null-sec security, with no gate in or out — were perfectly good
            // midpoints as far as the search was concerned, and no player can go there.
            _systems = rows
                .Where(r => !JoveRegionIds.Contains(r.RegionId))
                .Select(r => new Node(r.SolarSystemId, r.Name, r.Region, r.Security, r.X, r.Y, r.Z))
                .ToList();

            return _systems;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Every system by name, for the pickers — including high sec, so a route that
    /// starts there can say why it cannot be flown rather than not finding the system.</summary>
    public async Task<List<(int Id, string Name, string Region, double Security)>> SearchSystemsAsync(
        string term, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return [];

        // ⚠️ Lower-cased on both sides deliberately. string.Contains translates to SQLite's
        // instr(), which is case-sensitive — "UALX" found the system and "ualx" found nothing.
        // Lowering both makes it instr(lower(Name), lower(term)), which is case-insensitive
        // without exposing LIKE's % and _ wildcards to whatever the user typed.
        var needle = term.ToLowerInvariant();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hits = await (
            from s in db.SdeSolarSystems.AsNoTracking()
            join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
            where s.Name.ToLower().Contains(needle)
            select new { s.SolarSystemId, s.Name, Region = r.Name, s.Security })
            .Take(60).ToListAsync(ct);

        return hits
            .OrderBy(h => h.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => h.Name.Length)
            .ThenBy(h => h.Name)
            .Select(h => (h.SolarSystemId, h.Name, h.Region, h.Security))
            .ToList();
    }

    /// <summary>
    /// Systems allowed as midpoints under a given restriction. Keepstars come from structures
    /// we have actually seen, so this is only as complete as the structure data — a Keepstar
    /// nobody has resolved a name for is invisible here, which narrows routes rather than
    /// inventing them.
    /// </summary>
    /// <summary>
    /// Systems a route is allowed to stop at, or null for no restriction.
    ///
    /// <para>⚠️ "Station systems" means somewhere you can dock — NPC station OR player structure.
    /// It was NPC stations only, which made it a strict subset of the Keepstar option and gave the
    /// nonsense result that a route could be found through Keepstars but not through "stations":
    /// sov null has few NPC stations and a great many citadels. Both options now read from the
    /// same facility lookup, so Keepstar systems are necessarily a subset of station systems.</para>
    /// </summary>
    private async Task<HashSet<int>?> MidpointSetAsync(JumpMidpoints restriction, CancellationToken ct)
    {
        if (restriction == JumpMidpoints.Any) return null;   // null means "no restriction"

        var facilities = await FacilitiesAsync(ct);

        return restriction switch
        {
            JumpMidpoints.StationSystems =>
                facilities.Where(kv => kv.Value.NpcStation || kv.Value.PlayerStructures > 0)
                          .Select(kv => kv.Key).ToHashSet(),

            JumpMidpoints.CitadelSystems =>
                facilities.Where(kv => kv.Value.Fortizar || kv.Value.Keepstar)
                          .Select(kv => kv.Key).ToHashSet(),

            _ => facilities.Where(kv => kv.Value.Keepstar)
                           .Select(kv => kv.Key).ToHashSet(),
        };
    }

    /// <summary>
    /// What a system offers to a capital pilot picking a midpoint. NPC stations can always be
    /// docked at; a player structure may or may not let you in, but its presence still matters —
    /// and a Fortizar or Keepstar is what makes a system a real staging option.
    /// </summary>
    public sealed record SystemFacilities(
        bool NpcStation, int PlayerStructures, bool Fortizar, bool Keepstar)
    {
        /// <summary>Compact badge line for the map tooltip. Empty when the system has nothing.</summary>
        public string Badges
        {
            get
            {
                var parts = new List<string>(4);
                if (Keepstar)         parts.Add("Keepstar");
                if (Fortizar)         parts.Add("Fortizar");
                if (NpcStation)       parts.Add("NPC station");
                if (PlayerStructures > 0 && !Keepstar && !Fortizar)
                    parts.Add($"{PlayerStructures} player structure{(PlayerStructures == 1 ? "" : "s")}");
                else if (PlayerStructures > 1)
                    parts.Add($"+{PlayerStructures - 1} more");
                return string.Join(" · ", parts);
            }
        }
    }

    private Dictionary<int, SystemFacilities>? _facilities;
    private readonly SemaphoreSlim _facilityGate = new(1, 1);

    /// <summary>
    /// Docking and staging options per system. Player structures come from the ones we have
    /// resolved names for, so this under-reports rather than inventing: a structure nobody in the
    /// app has ever seen is not known to exist.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, SystemFacilities>> FacilitiesAsync(
        CancellationToken ct = default)
    {
        if (_facilities is { } cached) return cached;

        await _facilityGate.WaitAsync(ct);
        try
        {
            if (_facilities is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var npc = (await db.SdeStations.AsNoTracking()
                .Select(s => s.SolarSystemId).Distinct().ToListAsync(ct)).ToHashSet();

            // ⚠️ Scoped to the Citadel group FIRST, then matched by name. Matching on name alone
            // also catches "Fortizar Wreck", "Fortizar Blueprint", "Fortizar Upwell Quantum Core"
            // and a BPC token — 23 type ids where only 9 are structures anyone can dock at. None
            // of the junk happens to appear in EsiStructureNames today, so this changes no count;
            // it stops one appearing from silently marking a system as staging-capable.
            //
            // The group is resolved through Keepstar rather than hard-coded, and holds Astrahus,
            // Fortizar with its five faction variants, Keepstar and the Palatine. Astrahus is
            // deliberately neither: it still counts as a player structure, just not as a place to
            // stage a capital.
            var citadelGroup = await db.SdeTypes.AsNoTracking()
                .Where(t => t.Name == "Keepstar")
                .Select(t => t.GroupId)
                .FirstOrDefaultAsync(ct);

            var citadelTypes = await db.SdeTypes.AsNoTracking()
                .Where(t => t.GroupId == citadelGroup)
                .Select(t => new { t.TypeId, t.Name })
                .ToListAsync(ct);

            var fortTypes = citadelTypes.Where(t => t.Name.Contains("Fortizar"))
                                        .Select(t => t.TypeId).ToHashSet();

            // Contains, not equals: the Palatine Keepstar is one too.
            var keepTypes = citadelTypes.Where(t => t.Name.Contains("Keepstar"))
                                        .Select(t => t.TypeId).ToHashSet();

            var structures = await db.EsiStructureNames.AsNoTracking()
                .Select(s => new { s.SolarSystemId, s.TypeId })
                .ToListAsync(ct);

            var bySystem = structures.GroupBy(s => s.SolarSystemId)
                .ToDictionary(g => g.Key, g => (
                    Count: g.Count(),
                    Fort:  g.Any(s => fortTypes.Contains(s.TypeId)),
                    Keep:  g.Any(s => keepTypes.Contains(s.TypeId))));

            var all = new Dictionary<int, SystemFacilities>();
            foreach (var id in npc.Concat(bySystem.Keys).Distinct())
            {
                var s = bySystem.GetValueOrDefault(id);
                all[id] = new SystemFacilities(npc.Contains(id), s.Count, s.Fort, s.Keep);
            }

            _facilities = all;
            return all;
        }
        finally { _facilityGate.Release(); }
    }

    private Dictionary<string, double>? _regionHues;
    private readonly SemaphoreSlim _hueGate = new(1, 1);

    /// <summary>
    /// A hue per region, chosen so that regions sharing a border are far apart on the colour
    /// wheel. Used to colour system names on the map, where the point is to make a border
    /// visible without having to already know where it is.
    ///
    /// <para>⚠️ Assigned against the actual adjacency graph, NOT hashed from the name. Hashing
    /// is stable and cheap but blind: measured over the 161 adjacent region pairs it put 24 of
    /// them within 25° of each other, including Stain/Period Basis on an identical hue and
    /// Esoteria/Paragon Soul one degree apart — every one of them a border the colour exists to
    /// show. Greedy assignment in name order is deterministic, so the palette is still stable
    /// between launches.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, double>> RegionHuesAsync(CancellationToken ct = default)
    {
        if (_regionHues is { } cached) return cached;

        await _hueGate.WaitAsync(ct);
        try
        {
            if (_regionHues is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var borders = await db.Database.SqlQueryRaw<RegionBorder>("""
                SELECT DISTINCT sa."RegionId" AS "FromId", sb."RegionId" AS "ToId"
                FROM "SdeStargates" a
                JOIN "SdeStargates"   b  ON b."StargateId"    = a."DestinationStargateId"
                JOIN "SdeSolarSystems" sa ON sa."SolarSystemId" = a."SolarSystemId"
                JOIN "SdeSolarSystems" sb ON sb."SolarSystemId" = b."SolarSystemId"
                WHERE sa."RegionId" <> sb."RegionId"
                """).ToListAsync(ct);

            var names = await db.SdeRegions.AsNoTracking()
                .Select(r => new { r.RegionId, r.Name })
                .ToListAsync(ct);

            var nameById = names.ToDictionary(r => r.RegionId, r => r.Name);

            var neighbours = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var b in borders)
            {
                if (!nameById.TryGetValue(b.FromId, out var a) ||
                    !nameById.TryGetValue(b.ToId,   out var c)) continue;

                (neighbours.TryGetValue(a, out var la) ? la : neighbours[a] = []).Add(c);
                (neighbours.TryGetValue(c, out var lc) ? lc : neighbours[c] = []).Add(a);
            }

            // Name order, so the result is identical every run regardless of query ordering.
            var ordered = names.Select(r => r.Name)
                               .Distinct(StringComparer.Ordinal)
                               .OrderBy(n => n, StringComparer.Ordinal)
                               .ToList();

            var hues = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var region in ordered)
            {
                var taken = neighbours.GetValueOrDefault(region, [])
                    .Where(hues.ContainsKey)
                    .Select(n => hues[n])
                    .ToList();

                if (taken.Count == 0) { hues[region] = HueFor(region); continue; }

                // The hue furthest from every neighbour already placed. Whole-degree steps are
                // finer than the eye needs and cost nothing at 70 regions.
                double best = 0, bestGap = -1;
                for (var h = 0; h < 360; h++)
                {
                    var gap = taken.Min(t => HueGap(h, t));
                    if (gap > bestGap) { bestGap = gap; best = h; }
                }

                hues[region] = best;
            }

            _regionHues = hues;
            return hues;
        }
        finally { _hueGate.Release(); }
    }

    /// <summary>Shortest way round the colour wheel between two hues, in degrees.</summary>
    private static double HueGap(double a, double b)
    {
        var d = Math.Abs(a - b) % 360;
        return Math.Min(d, 360 - d);
    }

    /// <summary>Starting hue for a region with no already-placed neighbours. FNV-1a rather than
    /// string.GetHashCode(), which .NET randomises per process — that would repaint the map on
    /// every launch.</summary>
    private static double HueFor(string region)
    {
        uint hash = 2166136261;
        foreach (var ch in region)
        {
            hash ^= ch;
            hash *= 16777619;
        }
        return hash % 360;
    }

    private sealed class RegionBorder
    {
        public int FromId { get; set; }
        public int ToId   { get; set; }
    }

    private Dictionary<int, (string Name, string Region)>? _systemNames;
    private readonly SemaphoreSlim _nameGate = new(1, 1);

    /// <summary>
    /// Name and region for every system, including the high-sec ones a jump drive cannot enter —
    /// those are still drawn on the map as context and still worth identifying under the pointer.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, (string Name, string Region)>> SystemNamesAsync(
        CancellationToken ct = default)
    {
        if (_systemNames is { } cached) return cached;

        await _nameGate.WaitAsync(ct);
        try
        {
            if (_systemNames is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await (
                from s in db.SdeSolarSystems.AsNoTracking()
                join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
                select new { s.SolarSystemId, s.Name, Region = r.Name })
                .ToListAsync(ct);

            _systemNames = rows.ToDictionary(r => r.SolarSystemId, r => (r.Name, r.Region));
            return _systemNames;
        }
        finally { _nameGate.Release(); }
    }

    private List<(int A, int B)>? _links;
    private readonly SemaphoreSlim _linkGate = new(1, 1);

    /// <summary>
    /// Stargate connections, one entry per pair. Drawn faintly under a jump route so the gate
    /// network is visible behind it — a jump ignores gates, but seeing them is how you tell
    /// where a midpoint actually sits in the cluster.
    /// </summary>
    public async Task<IReadOnlyList<(int A, int B)>> StargateLinksAsync(CancellationToken ct = default)
    {
        if (_links is { } cached) return cached;

        await _linkGate.WaitAsync(ct);
        try
        {
            if (_links is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var pairs = await db.Database.SqlQueryRaw<StargateLink>("""
                SELECT a."SolarSystemId" AS "FromId", b."SolarSystemId" AS "ToId"
                FROM "SdeStargates" a
                JOIN "SdeStargates" b ON b."StargateId" = a."DestinationStargateId"
                """).ToListAsync(ct);

            // Each gate is published from both ends; keep one edge per pair.
            var seen = new HashSet<(int, int)>();
            var list = new List<(int A, int B)>(pairs.Count / 2);
            foreach (var p in pairs)
            {
                var key = p.FromId < p.ToId ? (p.FromId, p.ToId) : (p.ToId, p.FromId);
                if (seen.Add(key)) list.Add(key);
            }

            _links = list;
            return list;
        }
        finally { _linkGate.Release(); }
    }

    private sealed class StargateLink
    {
        public int FromId { get; set; }
        public int ToId   { get; set; }
    }

    /// <summary>
    /// Where each system sits on CCP's published 2D map layout — the arrangement the in-game map
    /// draws — for plotting a route. Y is negated because position2D grows northward while screen
    /// Y grows downward, matching what <c>UniverseMapService</c> does for the same reason.
    ///
    /// <para>Positions only. Jump distances are true 3D and are never taken from here.</para>
    /// </summary>
    public async Task<IReadOnlyDictionary<int, MapPoint>> MapPointsAsync(CancellationToken ct = default)
    {
        if (_mapPoints is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_mapPoints is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.SdeSolarSystems.AsNoTracking()
                .Where(s => s.X2D != null && s.Y2D != null)
                .Select(s => new { s.SolarSystemId, s.X2D, s.Y2D, s.Security })
                .ToListAsync(ct);

            _mapPoints = rows.ToDictionary(
                r => r.SolarSystemId,
                r => new MapPoint(r.SolarSystemId, r.X2D!.Value, -r.Y2D!.Value, r.Security));

            return _mapPoints;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Other systems that could stand in for a midpoint — reachable from the leg before it and
    /// from the leg after, so swapping one in leaves the rest of the route intact. This is what
    /// backs picking an alternative jump point by hand, and what a midpoint dragged on the map
    /// is snapped to.
    /// </summary>
    public async Task<List<JumpAlternative>> AlternativesAsync(
        int previousSystemId, int nextSystemId, double maxRangeLy,
        JumpMidpoints restriction = JumpMidpoints.Any, CancellationToken ct = default)
    {
        var nodes    = await SystemsAsync(ct);
        var allowed  = await MidpointSetAsync(restriction, ct);
        var points   = await MapPointsAsync(ct);
        var byId     = nodes.ToDictionary(n => n.Id);

        if (!byId.TryGetValue(previousSystemId, out var prev) ||
            !byId.TryGetValue(nextSystemId, out var next)) return [];

        var result = new List<JumpAlternative>();
        foreach (var n in nodes)
        {
            if (n.Id == prev.Id || n.Id == next.Id) continue;
            if (allowed is not null && !allowed.Contains(n.Id)) continue;

            var inLy = DistanceLy(prev, n);
            if (inLy > maxRangeLy) continue;

            var outLy = DistanceLy(n, next);
            if (outLy > maxRangeLy) continue;

            var p = points.GetValueOrDefault(n.Id);
            result.Add(new JumpAlternative(n.Id, n.Name, n.Region, n.Security, inLy, outLy,
                                           p?.X ?? 0, p?.Y ?? 0));
        }

        return result.OrderBy(r => r.InLy + r.OutLy).ToList();
    }

    private static double DistanceLy(Node a, Node b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / MetresPerLightYear;
    }

    /// <summary>
    /// The fewest jumps from one system to another within a given range, breaking ties on the
    /// shorter total distance.
    ///
    /// <para>Breadth-first over jump reachability, neighbours computed as each system is
    /// expanded rather than building the whole graph up front: at four thousand systems a
    /// complete adjacency matrix is sixteen million distance calculations for a route that
    /// usually touches a handful.</para>
    /// </summary>
    public async Task<JumpRoute> PlanAsync(
        int fromSystemId, int toSystemId, JumpShip ship, int jdcLevel, int jfcLevel,
        JumpMidpoints midpoints = JumpMidpoints.Any,
        CancellationToken ct = default)
    {
        var range   = MaxRange(ship.BaseRangeLy, jdcLevel);
        var nodes   = await SystemsAsync(ct);
        var allowed = await MidpointSetAsync(midpoints, ct);
        var byId    = nodes.ToDictionary(n => n.Id);

        if (!byId.TryGetValue(fromSystemId, out var start))
            return Empty(range, ship, "The starting system cannot be reached by jump drive — high security space is closed to capitals.");
        if (!byId.TryGetValue(toSystemId, out var goal))
            return Empty(range, ship, "The destination cannot be reached by jump drive — high security space is closed to capitals.");
        if (fromSystemId == toSystemId)
            return Empty(range, ship, "The start and the destination are the same system.");

        // Distance travelled so far, and where each system was reached from.
        var bestJumps   = new Dictionary<int, int> { [start.Id] = 0 };
        var bestDist    = new Dictionary<int, double> { [start.Id] = 0 };
        var cameFrom    = new Dictionary<int, int>();
        var frontier    = new List<Node> { start };
        var jumps       = 0;

        while (frontier.Count > 0 && !ct.IsCancellationRequested)
        {
            jumps++;
            var next = new List<Node>();

            foreach (var node in frontier)
            {
                var soFar = bestDist[node.Id];

                foreach (var candidate in nodes)
                {
                    if (candidate.Id == node.Id) continue;

                    // Restrictions apply to stopping places, not to the destination: a route
                    // may end anywhere even when its midpoints must have a station.
                    if (allowed is not null
                        && candidate.Id != goal.Id
                        && !allowed.Contains(candidate.Id)) continue;

                    var d = DistanceLy(node, candidate);
                    if (d > range) continue;

                    var total = soFar + d;

                    // A system already reached in fewer jumps stays as it is; reached in the
                    // same number, keep whichever got there over less distance.
                    if (bestJumps.TryGetValue(candidate.Id, out var seenJumps))
                    {
                        if (seenJumps < jumps) continue;
                        if (seenJumps == jumps && bestDist[candidate.Id] <= total) continue;
                    }
                    else
                    {
                        next.Add(candidate);
                    }

                    bestJumps[candidate.Id] = jumps;
                    bestDist[candidate.Id]  = total;
                    cameFrom[candidate.Id]  = node.Id;
                }
            }

            if (bestJumps.ContainsKey(goal.Id)) break;
            frontier = next;
        }

        if (!cameFrom.ContainsKey(goal.Id))
            return Empty(range, ship, midpoints switch
            {
                JumpMidpoints.KeepstarSystems =>
                    $"No route within {range:N2} ly per jump using only known Keepstar systems. " +
                    "Widening the midpoints, or resolving more structures, would be needed.",
                JumpMidpoints.StationSystems =>
                    $"No route within {range:N2} ly per jump using only station systems.",
                _ =>
                    $"No route within {range:N2} ly per jump. A longer-ranged hull or more " +
                    "Jump Drive Calibration would be needed.",
            });

        // Walk the chain back to the start.
        var path = new List<int> { goal.Id };
        var cur  = goal.Id;
        while (cameFrom.TryGetValue(cur, out var prev))
        {
            path.Add(prev);
            cur = prev;
        }
        path.Reverse();

        var legs = new List<JumpLeg>(path.Count - 1);
        double totalDist = 0, totalFuel = 0;
        for (var i = 0; i < path.Count - 1; i++)
        {
            var a = byId[path[i]];
            var b = byId[path[i + 1]];
            var d = DistanceLy(a, b);
            var f = FuelFor(d, ship.FuelPerLy, jfcLevel);

            totalDist += d;
            totalFuel += f;

            legs.Add(new JumpLeg(a.Id, a.Name, a.Region, a.Security,
                                 b.Id, b.Name, b.Region, b.Security, d, f));
        }

        return new JumpRoute(legs, totalDist, totalFuel, ship.FuelTypeName, range);
    }

    private static JumpRoute Empty(double range, JumpShip ship, string problem) =>
        new([], 0, 0, ship.FuelTypeName, range, problem);
}
