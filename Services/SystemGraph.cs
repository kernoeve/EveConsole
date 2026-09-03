using EveConsole.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Stargate adjacency for New Eden, with breadth-first range queries over it.
///
/// <para>The graph is loaded once and kept — it is roughly 13,000 systems and 26,000 links, and
/// it only changes when CCP adds space, so rebuilding it per evaluation would be pure waste for
/// an alarm that runs every minute.</para>
/// </summary>
public sealed class SystemGraph
{
    /// <summary>Gates point at the gate on the far side, so joining a gate to its destination
    /// gate yields the system pair. There is no separate adjacency table.</summary>
    private const string LinkSql = """
        SELECT a."SolarSystemId", b."SolarSystemId"
        FROM "SdeStargates" a
        JOIN "SdeStargates" b ON b."StargateId" = a."DestinationStargateId"
        """;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly SemaphoreSlim                   _gate = new(1, 1);

    private Dictionary<int, int[]>? _adjacency;

    public SystemGraph(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public int SystemCount => _adjacency?.Count ?? 0;

    public async Task<IReadOnlyDictionary<int, int[]>> AdjacencyAsync(CancellationToken ct = default)
    {
        if (_adjacency is { } cached) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_adjacency is { } raced) return raced;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var connString = db.Database.GetConnectionString()!;

            var built = new Dictionary<int, List<int>>();

            await using var conn = AppDb.Connect();
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = LinkSql;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var from = r.GetInt32(0);
                var to   = r.GetInt32(1);

                if (!built.TryGetValue(from, out var list)) built[from] = list = [];
                list.Add(to);
            }

            _adjacency = built.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray());
            return _adjacency;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Every system reachable from <paramref name="origin"/> in at most <paramref name="maxJumps"/>
    /// gate jumps, including the origin itself. Wormhole space has no gates to known space, so a
    /// range query from either side simply stays where it started — which is correct.
    /// </summary>
    public async Task<HashSet<int>> WithinJumpsAsync(int origin, int maxJumps, CancellationToken ct = default)
    {
        var adjacency = await AdjacencyAsync(ct);

        var seen  = new HashSet<int> { origin };
        if (maxJumps <= 0) return seen;

        var frontier = new List<int> { origin };
        for (var depth = 0; depth < maxJumps && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var id in frontier)
            {
                if (!adjacency.TryGetValue(id, out var neighbours)) continue;
                foreach (var n in neighbours)
                    if (seen.Add(n)) next.Add(n);
            }
            frontier = next;
        }

        return seen;
    }

    /// <summary>Drops the cached graph, so a fresh SDE import is picked up without a restart.</summary>
    public void Invalidate() => _adjacency = null;
}
