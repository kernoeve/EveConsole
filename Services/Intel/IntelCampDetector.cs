using System.Globalization;

namespace EveConsole.Services.Intel;

/// <summary>A stargate and where it leads, at its position in its system.</summary>
public sealed record IntelGate(long StargateId, int SystemId, int DestinationSystemId, double X, double Y, double Z);

/// <summary>One attacker on a kill, with the groups of its hull and weapon already looked up.</summary>
public readonly record struct IntelKillAttacker(
    long CharacterId, long CorporationId, long AllianceId,
    int? ShipTypeId, int? ShipGroupId, int? WeaponTypeId, int? WeaponGroupId);

/// <summary>A kill as intel reads it: when, where on grid, who was lost in what, and who did it.</summary>
public sealed record IntelKill(
    int KillMailId, DateTimeOffset At, int SystemId,
    long VictimId, long VictimCorporationId, long VictimAllianceId, int VictimShipTypeId, int? VictimGroupId,
    double? X, double? Y, double? Z,
    IReadOnlyList<IntelKillAttacker> Attackers)
{
    /// <summary>Whether any attacker is a character: a kill by players, not by NPCs alone.</summary>
    public bool HasPlayerAttacker => Attackers.Any(a => a.CharacterId > 0);
}

/// <summary>The gate nearest a point, and how far it is.</summary>
public readonly record struct IntelGateNear(long StargateId, int DestinationSystemId, double Km);

/// <summary>A pilot on a camp, as on their newest kill there: who they fly for and in what. The
/// affiliation is what a view judges the camp's standing by.</summary>
public readonly record struct IntelCampAttacker(long CharacterId, long CorporationId, long AllianceId, int? ShipTypeId);

/// <summary>Kills clustered on one gate, before the register gives the camp its identity.</summary>
/// <param name="Attackers">Every player attacker once, by character id.</param>
public sealed record IntelCampCluster(
    int SystemId, long StargateId, int GateSystemId,
    IReadOnlyList<IntelKill> Kills, IReadOnlyList<long> VictimIds,
    DateTimeOffset FirstKill, DateTimeOffset LastKill, DateTimeOffset FormedAt,
    IReadOnlyList<IntelCampAttacker> Attackers, IReadOnlyList<int> AttackerShipTypeIds,
    bool Smartbomb, bool Interdictor, bool Hic, bool Bubble)
{
    public IEnumerable<long> AttackerIds => Attackers.Select(a => a.CharacterId);
}

/// <summary>
/// A gate camp: kills on one gate by the same people, close together in time.
/// </summary>
/// <param name="Key">The camp's identity: the gate and when this episode of it began. Stable while
/// the camp goes on, even after its first kill drops out of the window.</param>
/// <param name="GateSystemId">The system the gate leads to, which is the gate's name.</param>
/// <param name="FormedAt">The kill that made it a camp: the one bringing the distinct victims to the
/// minimum.</param>
/// <param name="ExpiresAt">Thirty minutes (the camp lifetime) after the last kill.</param>
/// <param name="Attackers">Every player attacker once, with the affiliation a view judges the camp by.</param>
/// <param name="Reported">An intel report in the system also called a camp on this gate.</param>
public sealed record GateCamp(
    string Key, int SystemId, long StargateId, int GateSystemId, DateTimeOffset EpisodeStart,
    IReadOnlyList<int> KillMailIds, IReadOnlyList<long> VictimIds,
    DateTimeOffset FirstKill, DateTimeOffset LastKill, DateTimeOffset FormedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<IntelCampAttacker> Attackers, IReadOnlyList<int> AttackerShipTypeIds,
    bool Smartbomb, bool Interdictor, bool Hic, bool Bubble, bool Reported = false)
{
    public bool IsActive(DateTimeOffset now) => now < ExpiresAt;

    public IEnumerable<long> AttackerIds => Attackers.Select(a => a.CharacterId);
}

/// <summary>
/// Gate camps worked out from kill positions, on read, never stored.
///
/// <para>A kill counts towards a camp when its victim was a player in a ship (a capsule included) and
/// was lost within <c>intel.camp.gate_km</c> (150 km) of a stargate, the nearest one in the system. On
/// one gate, kills each within <c>intel.camp.window_min</c> (30 min) of the one before and sharing at
/// least one attacker with another of them are one cluster; a cluster with at least
/// <c>intel.camp.min_victims</c> (2) distinct pilots lost, capsules not counted, is a camp. A single
/// kill is a kill, not a camp.</para>
///
/// <para>The flags come from the attackers: a smartbomb fitted, an interdictor or a heavy interdictor
/// flown, a bubble launched. Their groups are found by name (<see cref="IntelSdeGroups"/>).</para>
/// </summary>
public static class IntelCampDetector
{
    /// <summary>The nearest gate in the system to a point, or null when the system has none.</summary>
    public static IntelGateNear? NearestGate(IReadOnlyDictionary<int, IReadOnlyList<IntelGate>> gates,
                                             int systemId, double x, double y, double z)
    {
        if (!gates.TryGetValue(systemId, out var list) || list.Count == 0) return null;
        IntelGate? best = null;
        var bestSq = double.MaxValue;
        foreach (var g in list)
        {
            double dx = g.X - x, dy = g.Y - y, dz = g.Z - z;
            var sq = dx * dx + dy * dy + dz * dz;
            if (sq < bestSq) { bestSq = sq; best = g; }
        }
        return best is null ? null : new IntelGateNear(best.StargateId, best.DestinationSystemId, Math.Sqrt(bestSq) / 1000.0);
    }

    /// <summary>The gate a kill happened on, when it was within <paramref name="gateKm"/> of one.</summary>
    public static IntelGateNear? GateOf(IntelKill k, IReadOnlyDictionary<int, IReadOnlyList<IntelGate>> gates, double gateKm) =>
        k is { X: double x, Y: double y, Z: double z } && NearestGate(gates, k.SystemId, x, y, z) is { } near && near.Km <= gateKm
            ? near : null;

    /// <summary>The clusters among <paramref name="kills"/> that are camps, oldest first.</summary>
    public static List<IntelCampCluster> Detect(
        IEnumerable<IntelKill> kills, IReadOnlyDictionary<int, IReadOnlyList<IntelGate>> gates,
        IntelSdeGroups groups, double gateKm, TimeSpan window, int minVictims)
    {
        var capsule = groups.Group(IntelSdeGroups.CapsuleName);

        // Gate kills of players in ships, by gate.
        var byGate = new Dictionary<long, (IntelGateNear Gate, int SystemId, List<IntelKill> Kills)>();
        foreach (var k in kills)
        {
            if (k.VictimId <= 0) continue;
            if (groups.ShipCategory is not null && (k.VictimGroupId is not int vg || !groups.IsShip(vg))) continue;
            if (GateOf(k, gates, gateKm) is not { } gate) continue;
            if (!byGate.TryGetValue(gate.StargateId, out var entry))
                byGate[gate.StargateId] = entry = (gate, k.SystemId, []);
            entry.Kills.Add(k);
        }

        var camps = new List<IntelCampCluster>();
        foreach (var (stargateId, (gate, systemId, list)) in byGate)
        {
            list.Sort((a, b) => a.At != b.At ? a.At.CompareTo(b.At) : a.KillMailId.CompareTo(b.KillMailId));

            // Runs of kills each within the window of the one before.
            var run = new List<IntelKill>();
            foreach (var k in list)
            {
                if (run.Count > 0 && k.At - run[^1].At > window)
                {
                    Cluster(run, stargateId, gate.DestinationSystemId, systemId, capsule, groups, minVictims, camps);
                    run = [];
                }
                run.Add(k);
            }
            Cluster(run, stargateId, gate.DestinationSystemId, systemId, capsule, groups, minVictims, camps);
        }
        return [.. camps.OrderBy(c => c.FirstKill).ThenBy(c => c.StargateId)];
    }

    /// <summary>Within one run: the kills linked by shared attackers, and those groups with enough
    /// victims.</summary>
    private static void Cluster(List<IntelKill> run, long stargateId, int gateSystemId, int systemId, int? capsule,
                                IntelSdeGroups groups, int minVictims, List<IntelCampCluster> camps)
    {
        if (run.Count < 2) return;

        // Union-find over the kills: two kills with an attacker in common are one group.
        var parent = Enumerable.Range(0, run.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        var firstWith = new Dictionary<long, int>();
        var linked = new bool[run.Count];
        for (var i = 0; i < run.Count; i++)
            foreach (var a in run[i].Attackers)
            {
                if (a.CharacterId <= 0) continue;
                if (firstWith.TryGetValue(a.CharacterId, out var j))
                {
                    if (j != i) { parent[Find(i)] = Find(j); linked[i] = linked[j] = true; }
                }
                else firstWith[a.CharacterId] = i;
            }

        foreach (var group in Enumerable.Range(0, run.Count).Where(i => linked[i]).GroupBy(Find))
        {
            var kills = group.Select(i => run[i]).ToList();   // in time order: run is sorted

            // Distinct pilots lost, a capsule not counted as another: the pilot was already lost
            // with the ship, or was never in one on the gate.
            var victims = new List<long>();
            DateTimeOffset? formed = null;
            foreach (var k in kills)
            {
                if (capsule is int pod && k.VictimGroupId == pod) continue;
                if (victims.Contains(k.VictimId)) continue;
                victims.Add(k.VictimId);
                if (victims.Count == minVictims) formed = k.At;
            }
            if (formed is null) continue;

            var attackers = kills.SelectMany(k => k.Attackers).Where(a => a.CharacterId > 0).ToList();
            // Each pilot as on their newest kill here (kills are in time order).
            var pilots = attackers.GroupBy(a => a.CharacterId).Select(g => g.Last())
                .Select(a => new IntelCampAttacker(a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId))
                .OrderBy(a => a.CharacterId).ToList();
            var hulls = pilots.Select(a => a.ShipTypeId)
                .Where(t => t is > 0).GroupBy(t => t!.Value)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => g.Key).ToList();

            camps.Add(new IntelCampCluster(
                systemId, stargateId, gateSystemId, kills, victims,
                kills[0].At, kills[^1].At, formed.Value,
                pilots, hulls,
                Smartbomb:   attackers.Any(a => groups.Is(a.WeaponGroupId, IntelSdeGroups.SmartBombName)),
                Interdictor: attackers.Any(a => groups.Is(a.ShipGroupId, IntelSdeGroups.InterdictorName)),
                Hic:         attackers.Any(a => groups.Is(a.ShipGroupId, IntelSdeGroups.HeavyInterdictorName)),
                Bubble:      attackers.Any(a => groups.Is(a.WeaponGroupId, IntelSdeGroups.SphereLauncherName)
                                             || groups.Is(a.WeaponGroupId, IntelSdeGroups.FieldGeneratorName))));
        }
    }
}

/// <summary>
/// What keeps a camp the same camp while it goes on: per gate, when each episode began and its last
/// kill. In memory, per process.
///
/// <para>Needed because the window slides: a camp running for an hour and a half loses its first
/// kills from every reading, and a camp named by its first kill would turn into a new one each time.
/// A cluster that overlaps an episode, or follows it within the camp window, continues it.</para>
///
/// <para>It also holds each episode's camp as last read, and gives it back while no reading shows it
/// any more, until <c>kept</c> after its last kill (or the camp lifetime, when that is longer). A
/// camp leaves the kills read back as soon as its earlier kills leave the window, which can be minutes
/// after it ended, or even while it is still active; the Camps tab keeps an ended camp, faded, for an
/// hour after its last kill. The episode is forgotten after that.</para>
///
/// <para>After a restart or a move to another PC the register starts empty, so a camp still going may
/// be taken for a new one once, and an ended camp whose kills have left the window is not shown again:
/// the same accepted risk as every other piece of worker memory.</para>
/// </summary>
public sealed class IntelCampRegister
{
    private sealed class Episode
    {
        public DateTimeOffset Start;
        public DateTimeOffset LastKill;
        public bool Seen;
        /// <summary>The camps of this episode as last read: given back while no reading shows them.</summary>
        public List<GateCamp> Last = [];
    }

    private readonly Dictionary<long, List<Episode>> _byGate = [];
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _byGate.Values.Sum(l => l.Count); } }

    /// <summary>
    /// The camps of this reading, with their keys, and every camp an earlier reading showed that this
    /// one no longer does, as last read, until <paramref name="kept"/> (or <paramref name="lifetime"/>,
    /// when longer) after its last kill.
    /// </summary>
    public List<GateCamp> Apply(IReadOnlyList<IntelCampCluster> clusters, DateTimeOffset now, TimeSpan lifetime, TimeSpan window,
                                TimeSpan kept)
    {
        var hold = kept > lifetime ? kept : lifetime;
        lock (_lock)
        {
            foreach (var list in _byGate.Values)
                foreach (var e in list) e.Seen = false;

            var camps = new List<GateCamp>();
            foreach (var c in clusters.OrderBy(c => c.FirstKill))
            {
                if (!_byGate.TryGetValue(c.StargateId, out var episodes)) _byGate[c.StargateId] = episodes = [];
                var episode = episodes.FirstOrDefault(e => c.FirstKill <= e.LastKill + window && c.LastKill >= e.Start - window);
                if (episode is null)
                {
                    episode = new Episode { Start = c.FirstKill, LastKill = c.LastKill };
                    episodes.Add(episode);
                }
                if (c.LastKill > episode.LastKill) episode.LastKill = c.LastKill;
                if (c.FirstKill < episode.Start)   episode.Start    = c.FirstKill;
                if (!episode.Seen) episode.Last = [];
                episode.Seen = true;

                var camp = new GateCamp(
                    KeyOf(c.StargateId, episode.Start), c.SystemId, c.StargateId, c.GateSystemId, episode.Start,
                    [.. c.Kills.Select(k => k.KillMailId)], c.VictimIds,
                    c.FirstKill, c.LastKill, c.FormedAt, c.LastKill + lifetime,
                    c.Attackers, c.AttackerShipTypeIds, c.Smartbomb, c.Interdictor, c.Hic, c.Bubble);
                episode.Last.Add(camp);
                camps.Add(camp);
            }

            foreach (var (gate, episodes) in _byGate.ToList())
            {
                episodes.RemoveAll(e => !e.Seen && now >= e.LastKill + hold);
                if (episodes.Count == 0) _byGate.Remove(gate);
                else
                    foreach (var e in episodes.Where(e => !e.Seen))
                        camps.AddRange(e.Last.Where(c => now < c.LastKill + hold));
            }
            return [.. camps.OrderBy(c => c.FirstKill).ThenBy(c => c.StargateId)];
        }
    }

    public static string KeyOf(long stargateId, DateTimeOffset start) =>
        stargateId.ToString(CultureInfo.InvariantCulture) + "@" + start.UtcDateTime.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
}
