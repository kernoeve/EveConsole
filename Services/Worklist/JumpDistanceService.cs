using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// How far apart two systems are by gate.
///
/// <para>Used to choose between sources. When three stations hold the material a job needs, the
/// nearest one is the cheapest to move from, and "nearest" for a hauler means gates rather than
/// light years.</para>
///
/// <para>There is no adjacency table in the SDE — a stargate points at the gate on the far side,
/// so the pairs come from joining gates to their destinations, the same way the universe map
/// builds its links.</para>
/// </summary>
public class JumpDistanceService(IDbContextFactory<AppDbContext> dbFactory)
{
    private Dictionary<int, List<int>>? _links;

    /// <summary>
    /// Gate distance from one system to every system reachable within <paramref name="maxJumps"/>.
    ///
    /// <para>Breadth-first from the destination rather than a route per candidate: sources are
    /// ranked against one destination at a time, so a single sweep answers for all of them. The
    /// cap keeps a search bounded when a destination sits in a pocket of the map with no path to
    /// most of it.</para>
    /// </summary>
    public async Task<Dictionary<int, int>> JumpsFromAsync(
        int systemId, int maxJumps = 60, CancellationToken ct = default)
    {
        var links = await LinksAsync(ct);

        var seen  = new Dictionary<int, int> { [systemId] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(systemId);

        while (queue.Count > 0)
        {
            var here = queue.Dequeue();
            var d    = seen[here];
            if (d >= maxJumps) continue;

            if (!links.TryGetValue(here, out var next)) continue;
            foreach (var to in next)
            {
                if (!seen.TryAdd(to, d + 1)) continue;
                queue.Enqueue(to);
            }
        }

        return seen;
    }

    /// <summary>
    /// The gate graph, built once. Static universe data, so a rebuild per refresh would be a
    /// pointless scan of every gate in the game.
    /// </summary>
    private async Task<Dictionary<int, List<int>>> LinksAsync(CancellationToken ct)
    {
        if (_links is not null) return _links;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var gates = await db.SdeStargates.AsNoTracking()
            .Select(g => new { g.StargateId, g.SolarSystemId, g.DestinationStargateId })
            .ToListAsync(ct);

        var systemOfGate = gates.ToDictionary(g => g.StargateId, g => g.SolarSystemId);

        var map = new Dictionary<int, List<int>>();
        foreach (var g in gates)
        {
            if (!systemOfGate.TryGetValue(g.DestinationStargateId, out var to)) continue;
            if (to == g.SolarSystemId) continue;

            map.TryAdd(g.SolarSystemId, []);
            if (!map[g.SolarSystemId].Contains(to)) map[g.SolarSystemId].Add(to);
        }

        return _links = map;
    }
}
