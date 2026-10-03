using EveConsole.Api;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>What a route favours: the fewest jumps, high security, or low and null security.</summary>
public enum RoutePreference { Shortest, Safer, LessSecure }

/// <summary>How a step of a route was reached.</summary>
public enum RouteHop { Start, Gate, Bridge, Wormhole }

/// <summary>
/// The jump bridge a step was reached by: the Ansiblex in the system before it (null for a bridge
/// added by hand, which has no structure to name), its zone and capacitor multiplier for the
/// jump, and whether who may use it is known at all.
/// </summary>
public sealed record RouteBridge(long? StructureId, string GateName, int Zone, int Multiplier, bool AccessKnown);

/// <summary>The Thera or Turnur wormhole a step was reached by, as EVE-Scout lists it: the
/// signature to warp to in the system before, the one it lands on, and when it closes.</summary>
public sealed record RouteWormhole(string Signature, string LandsOn, string Type, string MaxShipSize, DateTimeOffset? ExpiresAt);

/// <summary>One system on a route, and how it was reached.</summary>
public sealed record RouteStep(
    int SystemId, string Name, int RegionId, string Region, double Security, RouteHop Hop,
    RouteBridge? Bridge = null, RouteWormhole? Wormhole = null);

/// <summary>What to plan: from and to, the preference, the systems to keep out of, whether jump
/// bridges may be used and by which alliance (null: none), and whether Thera and Turnur's
/// wormholes may, for a ship of which size (EVE-Scout's words: small … capital).</summary>
public sealed record RouteRequest(
    int FromSystemId, int ToSystemId, RoutePreference Preference,
    IReadOnlyCollection<int> Avoid, bool UseBridges, long? AllianceId,
    bool UseWormholes = false, string ShipSize = "large");

/// <summary>A planned route, start first; empty with <see cref="NoRoute"/> set when there is none.</summary>
public sealed record RoutePlan(IReadOnlyList<RouteStep> Steps, bool NoRoute)
{
    public int Jumps       => Math.Max(0, Steps.Count - 1);
    public int BridgeJumps => Steps.Count(s => s.Hop == RouteHop.Bridge);
}

/// <summary>
/// Plans a route through stargates and the jump bridges the map knows, as the game's own
/// autopilot cannot: it knows no Ansiblex. The gate graph is the SDE's; the bridges are
/// <see cref="JumpBridgeService"/>'s, ESI-read and hand-added alike.
///
/// <para>⚠️ A bridge may be jumped only by the alliance holding sovereignty in the system the jump
/// starts from (rules since 2026-09-22), so each way across is judged on its own against the
/// pilot's alliance. A way whose holder is not known — sovereignty not yet read — is allowed and
/// marked, rather than silently dropped. Capitals cannot use bridges at all bar Rorquals and
/// freighters; this plans for ships that take gates.</para>
/// </summary>
public sealed class RoutePlannerService(
    IDbContextFactory<AppDbContext> dbFactory,
    SystemGraph                     graph,
    JumpBridgeService?              bridges  = null,
    EsiClient?                      esi      = null,
    EveScoutService?                eveScout = null)
{
    /// <summary>What a jump into a system the preference shuns costs, against 1 for any jump —
    /// enough that a route keeps to the preferred space whenever it can, as the game's own
    /// "safer" and "less secure" settings do.</summary>
    private const double Shunned = 1000;

    /// <summary>High security as the game shows it: 0.5 and above, after rounding.</summary>
    public static bool IsHighSec(double security) => security >= 0.45;

    private sealed record SystemInfo(string Name, int RegionId, string Region, double Security);
    private Dictionary<int, SystemInfo>? _systems;
    private readonly SemaphoreSlim _load = new(1, 1);

    private async Task<Dictionary<int, SystemInfo>> SystemsAsync(CancellationToken ct)
    {
        if (_systems is { } ready) return ready;
        await _load.WaitAsync(ct);
        try
        {
            if (_systems is { } again) return again;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var regions = await db.SdeRegions.AsNoTracking().ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);
            _systems = (await db.SdeSolarSystems.AsNoTracking()
                    .Select(s => new { s.SolarSystemId, s.Name, s.RegionId, s.Security }).ToListAsync(ct))
                .ToDictionary(s => s.SolarSystemId,
                              s => new SystemInfo(s.Name, s.RegionId, regions.GetValueOrDefault(s.RegionId, ""), s.Security));
            return _systems;
        }
        finally { _load.Release(); }
    }

    /// <summary>A way out of a system that is not a stargate: a bridge, or a wormhole.</summary>
    private sealed record Link(int To, RouteBridge? Bridge, RouteWormhole? Wormhole);

    /// <summary>The ways across every known bridge and open Thera/Turnur hole, from each system,
    /// as the request may use them.</summary>
    private async Task<Dictionary<int, List<Link>>> OtherLinksAsync(RouteRequest req, CancellationToken ct)
    {
        var links = new Dictionary<int, List<Link>>();
        void Add(int from, Link link)
        {
            if (!links.TryGetValue(from, out var list)) links[from] = list = [];
            list.Add(link);
        }

        // A wormhole goes both ways, and takes the ship sizes its mass allows.
        if (req.UseWormholes && eveScout is not null)
            foreach (var c in await eveScout.GetOpenAsync(ct))
            {
                if (!EveScoutService.Fits(c.MaxShipSize, req.ShipSize)) continue;
                Add(c.HubSystemId,   new Link(c.OtherSystemId, null, new RouteWormhole(c.HubSignature,   c.OtherSignature, c.WormholeType, c.MaxShipSize, c.ExpiresAt)));
                Add(c.OtherSystemId, new Link(c.HubSystemId,   null, new RouteWormhole(c.OtherSignature, c.HubSignature,   c.WormholeType, c.MaxShipSize, c.ExpiresAt)));
            }

        if (!req.UseBridges || bridges is null) return links;

        foreach (var b in (await bridges.GetAsync(ct)).Bridges)
            foreach (var (from, to) in new[] { (b.SystemA, b.SystemB), (b.SystemB, b.SystemA) })
            {
                var way  = b.Directions?.FirstOrDefault(d => d.FromSystemId == from);
                var gate = b.Gates.FirstOrDefault(g => g.SystemId == from);

                // Known holder: only that alliance. Unknown: allowed, and said so.
                if (way is not null && (way.AllianceId == 0 || way.AllianceId != req.AllianceId)) continue;
                if (way is null && req.AllianceId is null) continue;

                Add(from, new Link(to, new RouteBridge(gate?.StructureId, gate?.Name ?? "", way?.Zone ?? 0, way?.Multiplier ?? 0, way is not null), null));
            }
        return links;
    }

    public async Task<RoutePlan> PlanAsync(RouteRequest req, CancellationToken ct = default)
    {
        var systems   = await SystemsAsync(ct);
        var adjacency = await graph.AdjacencyAsync(ct);
        var otherOut  = await OtherLinksAsync(req, ct);
        var avoid     = req.Avoid.ToHashSet();

        if (!systems.ContainsKey(req.FromSystemId) || !systems.ContainsKey(req.ToSystemId))
            return new RoutePlan([], true);

        double Cost(int into)
        {
            if (!systems.TryGetValue(into, out var s)) return 1;
            return req.Preference switch
            {
                RoutePreference.Safer      => IsHighSec(s.Security) ? 1 : 1 + Shunned,
                RoutePreference.LessSecure => IsHighSec(s.Security) ? 1 + Shunned : 1,
                _                          => 1,
            };
        }

        // Dijkstra. Ties go to fewer bridge and wormhole jumps: a gate is the safer bet than a
        // bridge whose access list nobody has seen, or a hole that may have closed.
        var dist = new Dictionary<int, (double Cost, int Bridges)> { [req.FromSystemId] = (0, 0) };
        var prev = new Dictionary<int, (int From, Link? Via)>();
        var queue = new PriorityQueue<int, (double, int)>();
        queue.Enqueue(req.FromSystemId, (0, 0));

        while (queue.TryDequeue(out var at, out var key))
        {
            if (dist.TryGetValue(at, out var best) && (key.Item1 > best.Cost || (key.Item1 == best.Cost && key.Item2 > best.Bridges))) continue;
            if (at == req.ToSystemId) break;

            IEnumerable<(int To, Link? Via)> next =
                (adjacency.TryGetValue(at, out var gates) ? gates.Select(g => (g, (Link?)null)) : [])
                .Concat(otherOut.TryGetValue(at, out var ol) ? ol.Select(x => (x.To, (Link?)x)) : []);

            foreach (var (to, via) in next)
            {
                if (avoid.Contains(to) && to != req.ToSystemId) continue;
                var cost    = key.Item1 + Cost(to);
                var bridgeN = key.Item2 + (via is null ? 0 : 1);
                if (dist.TryGetValue(to, out var known) && (known.Cost < cost || (known.Cost == cost && known.Bridges <= bridgeN))) continue;
                dist[to] = (cost, bridgeN);
                prev[to] = (at, via);
                queue.Enqueue(to, (cost, bridgeN));
            }
        }

        if (!dist.ContainsKey(req.ToSystemId)) return new RoutePlan([], true);

        var path = new List<RouteStep>();
        for (var at = req.ToSystemId; ; )
        {
            var s = systems[at];
            if (at == req.FromSystemId)
            {
                path.Add(new RouteStep(at, s.Name, s.RegionId, s.Region, s.Security, RouteHop.Start));
                break;
            }
            var (from, via) = prev[at];
            path.Add(new RouteStep(at, s.Name, s.RegionId, s.Region, s.Security,
                                   via is null ? RouteHop.Gate : via.Bridge is not null ? RouteHop.Bridge : RouteHop.Wormhole,
                                   via?.Bridge, via?.Wormhole));
            at = from;
        }
        path.Reverse();
        return new RoutePlan(path, false);
    }

    // ── The autopilot ────────────────────────────────────────────────────────

    private const string WaypointEndpoint = "https://esi.evetech.net/ui/autopilot/waypoint/";
    public const string WaypointScope = "esi-ui.write_waypoint.v1";

    /// <summary>
    /// What the game is told to fly: each Ansiblex on the route, in order, then the destination.
    /// The game knows no bridges or wormholes, so a waypoint on each bridge is what brings the
    /// autopilot to it; the jump itself is the pilot's. Between them the game picks the gates by
    /// its own route settings. A bridge added by hand names no structure, and a wormhole is a
    /// signature: the system it is in stands in.
    /// </summary>
    public static List<long> Waypoints(RoutePlan plan)
    {
        var ids = new List<long>();
        for (var i = 1; i < plan.Steps.Count; i++)
        {
            if (plan.Steps[i].Hop is not (RouteHop.Bridge or RouteHop.Wormhole)) continue;
            var at = plan.Steps[i].Bridge?.StructureId ?? plan.Steps[i - 1].SystemId;
            if (ids.Count == 0 || ids[^1] != at) ids.Add(at);
        }
        if (plan.Steps.Count > 0) ids.Add(plan.Steps[^1].SystemId);
        return ids;
    }

    /// <summary>
    /// Sets the route in the game for one character: the first waypoint replaces whatever was
    /// there, the rest follow it. Null when it went through; otherwise why not, in ESI's words.
    /// The character must be logged in for the game to take it.
    /// </summary>
    public async Task<string?> SendToAutopilotAsync(long characterId, RoutePlan plan, CancellationToken ct = default)
    {
        if (esi is null) return "ESI is not available.";
        var first = true;
        foreach (var id in Waypoints(plan))
        {
            var url = $"{WaypointEndpoint}?destination_id={id}&add_to_beginning=false&clear_other_waypoints={(first ? "true" : "false")}";
            var r = await esi.RequestRawAsync(HttpMethod.Post, url, null, characterId, ct);
            if (!r.IsSuccess) return r.StatusCode == 0 ? r.Error ?? "" : $"{r.StatusCode} {r.Error}";
            first = false;
        }
        return null;
    }
}
