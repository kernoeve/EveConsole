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

    private List<Node>? _systems;
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
                select new { s.SolarSystemId, s.Name, Region = r.Name, s.Security, s.X, s.Y, s.Z })
                .ToListAsync(ct);

            _systems = rows
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hits = await (
            from s in db.SdeSolarSystems.AsNoTracking()
            join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
            where s.Name.Contains(term)
            select new { s.SolarSystemId, s.Name, Region = r.Name, s.Security })
            .Take(60).ToListAsync(ct);

        return hits
            .OrderBy(h => h.Name.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(h => h.Name.Length)
            .ThenBy(h => h.Name)
            .Select(h => (h.SolarSystemId, h.Name, h.Region, h.Security))
            .ToList();
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
        CancellationToken ct = default)
    {
        var range = MaxRange(ship.BaseRangeLy, jdcLevel);
        var nodes = await SystemsAsync(ct);
        var byId  = nodes.ToDictionary(n => n.Id);

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
            return Empty(range, ship,
                $"No route within {range:N2} ly per jump. A longer-ranged hull or more Jump Drive Calibration would be needed.");

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
