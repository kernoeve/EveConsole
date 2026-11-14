using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>Which ways across space a distance counts.</summary>
public enum IntelGraphMode
{
    /// <summary>Stargates and every open Thera/Turnur hole, both ways: how near a threat can come.
    /// What every intel view, panel and alarm uses.</summary>
    Threat,

    /// <summary>The threat ways plus our own jump bridges, the ways the viewer's alliance may use:
    /// how far the viewer has to go. Only where the setting is on and the view is about travel.</summary>
    Travel,
}

/// <summary>How far a system is from an origin, and whether the shortest way there goes through a
/// wormhole (so "3 jumps, via Thera" can be said).</summary>
public readonly record struct IntelHop(int Jumps, bool ViaWormhole);

/// <summary>
/// Jump distances from a system, over stargates plus Thera and Turnur's open holes (EVE-Scout), and
/// in travel mode our own jump bridges too.
///
/// <para>Every walk is a full breadth-first walk with no range cap (well under a millisecond over the
/// eight thousand or so systems reachable by gate), cached per origin: a view asks for its
/// characters' systems, and only a character that has moved costs a new walk. The holes and bridges
/// are read again every minute, and at once when EVE-Scout or the bridge list says they changed; each
/// reading bumps <see cref="Version"/>, which is part of the cache key.</para>
///
/// <para>⚠️ A view on the UI thread must not await a walk. It takes <see cref="TryCached"/>, shows "…"
/// on a miss, and asks for the walk off the UI thread; the snapshot warms every own character's
/// system on each refresh, so a miss is the moment just after a jump.</para>
/// </summary>
public sealed class IntelDistanceService
{
    private const int CacheSize = 64;
    private const double MetresPerLightYear = 9.4607304725808e15;
    private static readonly TimeSpan EdgesEvery = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly SystemGraph?                     _graph;
    private readonly EveScoutService?                 _eveScout;
    private readonly JumpBridgeService?               _bridges;
    private readonly AppErrorLogger?                  _errors;

    private readonly SemaphoreSlim _edgesGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly LinkedList<(CacheKey Key, IReadOnlyDictionary<int, IntelHop> Hops)> _lru = new();
    private readonly Dictionary<CacheKey, LinkedListNode<(CacheKey Key, IReadOnlyDictionary<int, IntelHop> Hops)>> _index = [];

    private IReadOnlyDictionary<int, int[]>? _gates;
    private Edges _edges = Edges.None;
    private DateTimeOffset _edgesAt = DateTimeOffset.MinValue;
    private Dictionary<int, (double X, double Y, double Z)>? _positions;

    private readonly record struct CacheKey(int Origin, IntelGraphMode Mode, long AllianceId, long Version);

    /// <summary>The ways that are not stargates, as one reading: holes both ways, and bridges by the
    /// alliance allowed to use each way (0 for a way whose holder is not known).</summary>
    public sealed record Edges(
        IReadOnlyDictionary<int, int[]> Holes,
        IReadOnlyList<(int From, int To, long AllianceId, bool HolderKnown)> Bridges)
    {
        public static readonly Edges None = new(new Dictionary<int, int[]>(), []);
    }

    public IntelDistanceService(
        IDbContextFactory<AppDbContext> dbFactory, SystemGraph graph,
        EveScoutService? eveScout = null, JumpBridgeService? bridges = null, AppErrorLogger? errors = null)
    {
        _dbFactory = dbFactory;
        _graph     = graph;
        _eveScout  = eveScout;
        _bridges   = bridges;
        _errors    = errors;
        if (eveScout is not null) eveScout.Changed += EdgesChanged;
        if (bridges is not null)  bridges.Changed  += EdgesChanged;
    }

    /// <summary>A service over a graph given whole: for checks, with no database.</summary>
    public IntelDistanceService(IReadOnlyDictionary<int, int[]> gates, Edges edges)
    {
        _gates   = gates;
        _edges   = edges;
        _edgesAt = DateTimeOffset.MaxValue;
    }

    /// <summary>Bumped by every new reading of the holes and bridges.</summary>
    public long Version { get; private set; }

    /// <summary>Raised after <see cref="Version"/> moves: cached walks are stale, and a view
    /// projecting with them asks again.</summary>
    public event Action? Changed;

    private void EdgesChanged() => _edgesAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Every system reachable from <paramref name="origin"/>, with its jumps and whether the shortest
    /// way uses a hole. <paramref name="allianceId"/> matters in travel mode only: the bridges whose
    /// ways that alliance may jump.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, IntelHop>> JumpsFromAsync(
        int origin, IntelGraphMode mode, long? allianceId = null, CancellationToken ct = default)
    {
        await EnsureEdgesAsync(ct).ConfigureAwait(false);
        var key = Key(origin, mode, allianceId);
        if (Lookup(key) is { } cached) return cached;

        var hops = Walk(origin, _gates ?? new Dictionary<int, int[]>(), Extra(mode, allianceId));
        Store(key, hops);
        return hops;
    }

    /// <summary>The walk if it is cached for the current holes and bridges; null otherwise. Never
    /// waits, for a projection on the UI thread.</summary>
    public IReadOnlyDictionary<int, IntelHop>? TryCached(int origin, IntelGraphMode mode, long? allianceId = null) =>
        Lookup(Key(origin, mode, allianceId));

    /// <summary>Straight-line light-years between two systems; null until the positions are loaded
    /// (by the first walk) or when either is unknown.</summary>
    public double? LightYears(int a, int b)
    {
        if (_positions is not { } pos || !pos.TryGetValue(a, out var p) || !pos.TryGetValue(b, out var q)) return null;
        var dx = p.X - q.X; var dy = p.Y - q.Y; var dz = p.Z - q.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / MetresPerLightYear;
    }

    private CacheKey Key(int origin, IntelGraphMode mode, long? allianceId) =>
        new(origin, mode, mode == IntelGraphMode.Travel ? allianceId ?? 0 : 0, Version);

    // ── The walk ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A breadth-first walk from <paramref name="origin"/> over the gates and the extra ways. A system
    /// is "via a wormhole" only when every shortest way to it uses one: a level is finished before
    /// the next starts, and one shortest way through gates alone clears the flag.
    /// </summary>
    public static Dictionary<int, IntelHop> Walk(
        int origin, IReadOnlyDictionary<int, int[]> gates, IReadOnlyDictionary<int, List<(int To, bool Hole)>> extra)
    {
        var hops = new Dictionary<int, IntelHop> { [origin] = new(0, false) };
        var frontier = new List<int> { origin };
        for (var depth = 1; frontier.Count > 0; depth++)
        {
            var found = new Dictionary<int, bool>();   // system → via a hole, over this level's ways
            void Reach(int to, bool viaHole)
            {
                if (hops.ContainsKey(to)) return;
                found[to] = found.TryGetValue(to, out var was) ? was && viaHole : viaHole;
            }

            foreach (var id in frontier)
            {
                var fromHole = hops[id].ViaWormhole;
                if (gates.TryGetValue(id, out var neighbours))
                    foreach (var n in neighbours) Reach(n, fromHole);
                if (extra.TryGetValue(id, out var ways))
                    foreach (var (to, hole) in ways) Reach(to, fromHole || hole);
            }

            frontier = [.. found.Keys];
            foreach (var (id, viaHole) in found) hops[id] = new IntelHop(depth, viaHole);
        }
        return hops;
    }

    /// <summary>The non-gate ways for a mode: holes always, bridges in travel mode by the usability
    /// rule the route planner uses.</summary>
    public IReadOnlyDictionary<int, List<(int To, bool Hole)>> Extra(IntelGraphMode mode, long? allianceId) =>
        ExtraWays(_edges, mode, allianceId);

    public static Dictionary<int, List<(int To, bool Hole)>> ExtraWays(Edges edges, IntelGraphMode mode, long? allianceId)
    {
        var ways = new Dictionary<int, List<(int, bool)>>();
        void Add(int from, int to, bool hole)
        {
            if (!ways.TryGetValue(from, out var list)) ways[from] = list = [];
            list.Add((to, hole));
        }
        foreach (var (from, tos) in edges.Holes)
            foreach (var to in tos) Add(from, to, true);

        if (mode == IntelGraphMode.Travel)
            foreach (var (from, to, holder, known) in edges.Bridges)
            {
                var way = known ? new BridgeDirection(from, to, holder, "", 0, "", null, 0) : null;
                if (RoutePlannerService.BridgeUsable(way, allianceId)) Add(from, to, false);
            }
        return ways;
    }

    // ── Holes, bridges and positions ─────────────────────────────────────────

    private async Task EnsureEdgesAsync(CancellationToken ct)
    {
        if (_gates is not null && DateTimeOffset.UtcNow - _edgesAt < EdgesEvery) return;
        if (_graph is null || _dbFactory is null) return;

        await _edgesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gates is not null && DateTimeOffset.UtcNow - _edgesAt < EdgesEvery) return;
            _gates ??= await _graph.AdjacencyAsync(ct).ConfigureAwait(false);
            _positions ??= await PositionsAsync(ct).ConfigureAwait(false);

            var edges = await ReadEdgesAsync(ct).ConfigureAwait(false);
            _edgesAt = DateTimeOffset.UtcNow;
            if (!Same(edges, _edges))
            {
                _edges = edges;
                Version++;
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // The walk goes on over whatever was read last; asked again in a minute.
            _errors?.Log(nameof(IntelDistanceService), "reading holes and bridges", ex);
            _edgesAt = DateTimeOffset.UtcNow;
        }
        finally { _edgesGate.Release(); }
    }

    private async Task<Edges> ReadEdgesAsync(CancellationToken ct)
    {
        var holes = new Dictionary<int, HashSet<int>>();
        void Hole(int a, int b)
        {
            if (a <= 0 || b <= 0 || a == b) return;
            if (!holes.TryGetValue(a, out var set)) holes[a] = set = [];
            set.Add(b);
        }
        // Every size, both ways: a threat comes through whatever fits it.
        if (_eveScout is not null)
            foreach (var c in await _eveScout.GetOpenAsync(ct).ConfigureAwait(false))
            {
                Hole(c.HubSystemId, c.OtherSystemId);
                Hole(c.OtherSystemId, c.HubSystemId);
            }

        var bridges = new List<(int, int, long, bool)>();
        if (_bridges is not null)
            foreach (var b in (await _bridges.GetAsync(ct).ConfigureAwait(false)).Bridges)
                foreach (var (from, to) in new[] { (b.SystemA, b.SystemB), (b.SystemB, b.SystemA) })
                {
                    var way = b.Directions?.FirstOrDefault(d => d.FromSystemId == from);
                    bridges.Add((from, to, way?.AllianceId ?? 0, way is not null));
                }

        return new Edges(holes.ToDictionary(kv => kv.Key, kv => kv.Value.Order().ToArray()), bridges);
    }

    private static bool Same(Edges a, Edges b) =>
        a.Bridges.SequenceEqual(b.Bridges)
        && a.Holes.Count == b.Holes.Count
        && a.Holes.All(kv => b.Holes.TryGetValue(kv.Key, out var other) && other.SequenceEqual(kv.Value));

    private async Task<Dictionary<int, (double X, double Y, double Z)>> PositionsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory!.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.SdeSolarSystems.AsNoTracking()
                .Select(s => new { s.SolarSystemId, s.X, s.Y, s.Z }).ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(s => s.SolarSystemId, s => (s.X, s.Y, s.Z));
    }

    // ── The cache ────────────────────────────────────────────────────────────

    private IReadOnlyDictionary<int, IntelHop>? Lookup(CacheKey key)
    {
        lock (_cacheLock)
        {
            if (!_index.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Hops;
        }
    }

    private void Store(CacheKey key, IReadOnlyDictionary<int, IntelHop> hops)
    {
        lock (_cacheLock)
        {
            if (_index.TryGetValue(key, out var old)) _lru.Remove(old);
            _index[key] = _lru.AddFirst((key, hops));
            while (_lru.Count > CacheSize)
            {
                _index.Remove(_lru.Last!.Value.Key);
                _lru.RemoveLast();
            }
        }
    }
}
