using System.Globalization;
using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// A run of ♦ pirate NPCs killing players in one system: one faction's, its latest kill within the
/// last <see cref="LiveIntelService.DiamondGap"/>, reaching back while no two kills are further
/// apart than that.
/// </summary>
/// <param name="Faction">The pirate faction, as the screen names it ("Blood Raider Covenant").</param>
/// <param name="Kills">Player ships killed in the run (capsules not counted).</param>
/// <param name="First">The run's first kill.</param>
/// <param name="Last">The run's latest kill.</param>
/// <param name="Earlier">The run reaches back as far as was read: it began before <paramref name="First"/>.</param>
/// <param name="Hulls">The ♦ hulls on the run's kills, most kills first: type id and screen name.</param>
public sealed record DiamondActivity(int SystemId, string Faction, int Kills, DateTimeOffset First,
                                     DateTimeOffset Last, bool Earlier,
                                     IReadOnlyList<(int TypeId, string Name)> Hulls);

public enum NpcCapitalKind { Dreadnought, Supercarrier, Titan }

/// <summary>An NPC capital seen on a killmail since the last downtime: its latest kill in one system.</summary>
/// <param name="Name">The hull, as the screen names it ("Dark Blood Titan").</param>
/// <param name="Faction">Its faction, as the screen names it; empty when the killmail does not say.</param>
/// <param name="Kills">Player ships it has killed here since downtime (capsules not counted).</param>
public sealed record NpcCapitalSighting(string Name, int TypeId, NpcCapitalKind Kind, string Faction,
                                        int SystemId, DateTimeOffset LastKill, int Kills);

// ── ♦ rat activity and NPC capitals ──────────────────────────────────────────
//
// Like the officers: an NPC ship's own death makes no killmail, so the only trace one leaves is the
// players it kills. Measured against a year of NPC Sotiyo losses (2026-11): ♦ pirates killed
// players in the system before 31 of 40 Sotiyos died, with a median of three and a half days of
// warning; 95% of the quiet spells between two such kills were under 1.2 days and the longest
// 7.9. Blood Raider Strongholds bring the same hulls, so the mark says what was seen — ♦ rat
// activity — and never what is behind it.
public sealed partial class LiveIntelService
{
    /// <summary>
    /// The quiet spell that ends a run of ♦ rat kills, and how long a mark stays after the run's
    /// last kill. Five days: of the gaps measured before Sotiyo losses, five of 562 were longer.
    /// </summary>
    public static readonly TimeSpan DiamondGap = TimeSpan.FromDays(5);

    /// <summary>How far back a run is followed. A run still going at the edge is shown as "since at
    /// least" that day.</summary>
    private static readonly TimeSpan DiamondLookback = TimeSpan.FromDays(60);

    /// <summary>How long one reading is reused: the map refreshes every few seconds, and five days
    /// of killmails do not need reading that often.</summary>
    private static readonly TimeSpan NpcReuse = TimeSpan.FromMinutes(2);

    /// <summary>
    /// ⚠️ The pirate factions, by id. ♦ groups also hold EDENCOM's and CONCORD's police and the
    /// empire mining corporations' guards, which kill players too and are not rats.
    /// Guristas, Angel Cartel, Blood Raiders, Sansha's Nation, Serpentis.
    /// </summary>
    private static readonly HashSet<int> PirateFactions = [500010, 500011, 500012, 500019, 500020];

    /// <summary>Capsule and Genolution capsule: a pod is the same player, not another kill.</summary>
    private static readonly HashSet<int> Capsules = [670, 33328];

    /// <summary>A watched NPC hull. The name is kept in English and put in the interface language
    /// where it is shown, so a language picked later still applies.</summary>
    private sealed record NpcType(string English, bool Diamond, NpcCapitalKind? Kind);

    private static string TypeName(int typeId) =>
        s_npcTypes!.TryGetValue(typeId, out var t) ? SdeNames.Type(typeId, t.English) : typeId.ToString(CultureInfo.InvariantCulture);

    private sealed record NpcKill(int KillMailId, DateTimeOffset At, int SystemId, long VictimCharId,
                                  int VictimShipTypeId, int ShipTypeId, long? CorporationId, int? FactionId);

    private static readonly SemaphoreSlim s_npcGate = new(1, 1);
    private static (IReadOnlyDictionary<int, IReadOnlyList<DiamondActivity>> Diamonds,
                    IReadOnlyDictionary<int, IReadOnlyList<NpcCapitalSighting>> Capitals)? s_npc;
    private static DateTimeOffset s_npcAt;
    private static Dictionary<int, NpcType>? s_npcTypes;
    private static Dictionary<long, int>? s_corpFaction;
    private static Dictionary<int, string>? s_factionNames;

    private static async Task<(IReadOnlyDictionary<int, IReadOnlyList<DiamondActivity>>,
                               IReadOnlyDictionary<int, IReadOnlyList<NpcCapitalSighting>>)> NpcActivityAsync(
        AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        await s_npcGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var downtime = LastDowntime(now);
            // Reused only within one EVE day: the capitals start afresh at downtime.
            if (s_npc is { } cached && now - s_npcAt < NpcReuse && s_npcAt >= downtime)
                return cached;

            await LoadNpcLookupsAsync(db, ct).ConfigureAwait(false);
            var types = s_npcTypes!;
            var typeIds = types.Keys.ToList();
            if (typeIds.Count == 0) return Remember(new Dictionary<int, IReadOnlyList<DiamondActivity>>(),
                                                    new Dictionary<int, IReadOnlyList<NpcCapitalSighting>>());

            // ── The recent window: every kill with one of these NPCs on it ──────────
            var since  = now - DiamondGap < downtime ? now - DiamondGap : downtime;
            var recent = await ReadKillsAsync(db, since, typeIds, null, ct).ConfigureAwait(false);

            // ── NPC capitals since downtime ─────────────────────────────────────────
            var capitals = new Dictionary<int, IReadOnlyList<NpcCapitalSighting>>();
            foreach (var bySystem in recent.Where(k => k.At >= downtime && types[k.ShipTypeId].Kind is not null)
                                           .GroupBy(k => k.SystemId))
                capitals[bySystem.Key] = bySystem
                    .GroupBy(k => k.ShipTypeId)
                    .Select(byHull =>
                    {
                        var t = types[byHull.Key];
                        return new NpcCapitalSighting(TypeName(byHull.Key), byHull.Key, t.Kind!.Value, FactionOf(byHull.First()),
                            bySystem.Key, byHull.Max(k => k.At), byHull.Select(k => k.KillMailId).Distinct().Count());
                    })
                    .OrderByDescending(c => c.Kind).ThenByDescending(c => c.LastKill)
                    .ToList();

            // ── ♦ rat runs ──────────────────────────────────────────────────────────
            // The systems with a ♦ pirate kill in the last five days, then each of them read back
            // far enough to find where its run began.
            var live = recent.Where(k => types[k.ShipTypeId].Diamond && k.At >= now - DiamondGap && IsPirate(k))
                             .Select(k => k.SystemId).Distinct().ToList();
            var diamonds = new Dictionary<int, IReadOnlyList<DiamondActivity>>();
            if (live.Count > 0)
            {
                var lookback = now - DiamondLookback;
                var diamondIds = types.Where(kv => kv.Value.Diamond).Select(kv => kv.Key).ToList();
                var history = await ReadKillsAsync(db, lookback, diamondIds, live, ct).ConfigureAwait(false);

                foreach (var bySystem in history.Where(IsPirate).GroupBy(k => k.SystemId))
                {
                    var runs = new List<DiamondActivity>();
                    foreach (var byFaction in bySystem.GroupBy(FactionOf))
                    {
                        var kills = byFaction.GroupBy(k => k.KillMailId)
                                             .Select(g => (At: g.First().At, Hulls: g.Select(k => k.ShipTypeId).Distinct().ToList()))
                                             .OrderByDescending(k => k.At).ToList();
                        if (now - kills[0].At > DiamondGap) continue;

                        var run = 1;
                        while (run < kills.Count && kills[run - 1].At - kills[run].At <= DiamondGap) run++;
                        var inRun   = kills.Take(run).ToList();
                        var earlier = run == kills.Count && inRun[^1].At - lookback <= DiamondGap;
                        var hulls   = inRun.SelectMany(k => k.Hulls).GroupBy(t => t)
                                           .OrderByDescending(g => g.Count())
                                           .Select(g => (g.Key, TypeName(g.Key))).ToList();
                        runs.Add(new DiamondActivity(bySystem.Key, byFaction.Key, run, inRun[^1].At, inRun[0].At, earlier, hulls));
                    }
                    if (runs.Count > 0)
                        diamonds[bySystem.Key] = runs.OrderByDescending(r => r.Last).ToList();
                }
            }

            return Remember(diamonds, capitals);

            (IReadOnlyDictionary<int, IReadOnlyList<DiamondActivity>>, IReadOnlyDictionary<int, IReadOnlyList<NpcCapitalSighting>>) Remember(
                Dictionary<int, IReadOnlyList<DiamondActivity>> d, Dictionary<int, IReadOnlyList<NpcCapitalSighting>> c)
            {
                s_npc   = (d, c);
                s_npcAt = now;
                return (d, c);
            }
        }
        finally { s_npcGate.Release(); }
    }

    /// <summary>The faction an NPC attacker flies for: the killmail's own faction when it has one,
    /// else its corporation's.</summary>
    private static int? FactionIdOf(NpcKill k) =>
        k.FactionId ?? (k.CorporationId is long corp && s_corpFaction!.TryGetValue(corp, out var f) ? f : null);

    private static bool IsPirate(NpcKill k) => FactionIdOf(k) is int f && PirateFactions.Contains(f);

    private static string FactionOf(NpcKill k) =>
        FactionIdOf(k) is int f && s_factionNames!.TryGetValue(f, out var name) ? SdeNames.Faction(f, name) : "";

    /// <summary>
    /// The kills since <paramref name="since"/> with one of <paramref name="typeIds"/> among the NPC
    /// attackers, a player's ship the victim — one row per kill and NPC hull. Only in
    /// <paramref name="systems"/> when given.
    /// </summary>
    private static async Task<List<NpcKill>> ReadKillsAsync(AppDbContext db, DateTimeOffset since,
        List<int> typeIds, List<int>? systems, CancellationToken ct)
    {
        // ⚠️ Raw SQL for the time, as for the live killmails: SQLite cannot translate a
        // DateTimeOffset comparison in LINQ.
        var kills = db.KillMailDetails
            .FromSqlRaw("""SELECT * FROM "KillMailDetails" WHERE "KillMailTime" >= {0}""", since)
            .AsNoTracking();
        if (systems is not null) kills = kills.Where(k => systems.Contains(k.SolarSystemId));

        var rows = await (from k in kills
                          join a in db.KillMailAttackers.AsNoTracking() on k.KillMailId equals a.KillMailId
                          where a.CharacterId == null && a.ShipTypeId != null && typeIds.Contains(a.ShipTypeId.Value)
                                && k.VictimCharId > 0
                          select new { k.KillMailId, k.KillMailTime, k.SolarSystemId, k.VictimCharId, k.VictimShipTypeId,
                                       ShipTypeId = a.ShipTypeId!.Value, a.CorporationId, a.FactionId })
                         .Distinct()
                         .ToListAsync(ct).ConfigureAwait(false);

        return rows.Where(r => !Capsules.Contains(r.VictimShipTypeId))
                   .Select(r => new NpcKill(r.KillMailId, r.KillMailTime, r.SolarSystemId, r.VictimCharId,
                                            r.VictimShipTypeId, r.ShipTypeId, r.CorporationId, r.FactionId))
                   .ToList();
    }

    /// <summary>
    /// The NPC hulls watched for, which corporation flies for which faction, and the factions'
    /// names. Read once per process; they change only with the SDE.
    ///
    /// <para>⚠️ By SDE group, as the officers are. Every ♦ NPC sits in a group named for its hull
    /// class with ♦ in front ("♦ Cruiser"); the capitals' groups name the class
    /// ("Asteroid Blood Raider Titan", "♦ Dreadnought"). Faction warfare, mission and Homefront
    /// capitals are left out: they are not spawns anyone can go and find.</para>
    /// </summary>
    private static async Task LoadNpcLookupsAsync(AppDbContext db, CancellationToken ct)
    {
        if (s_npcTypes is null)
        {
            var rows = await (from t in db.SdeTypes.AsNoTracking()
                              join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                              where g.CategoryId == EntityCategoryId
                              select new { t.TypeId, t.Name, Group = g.Name }).ToListAsync(ct).ConfigureAwait(false);
            var map = new Dictionary<int, NpcType>();
            foreach (var r in rows)
            {
                var diamond = r.Group.StartsWith('♦');
                var kind    = CapitalKind(r.Group);
                if (!diamond && kind is null) continue;
                map[r.TypeId] = new NpcType(r.Name, diamond, kind);
            }
            s_npcTypes = map;
        }

        s_corpFaction ??= (await db.SdeNpcCorporations.AsNoTracking()
                .Where(c => c.FactionId != null)
                .Select(c => new { c.CorporationId, c.FactionId })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(c => (long)c.CorporationId, c => c.FactionId!.Value);

        s_factionNames ??= (await db.SdeFactions.AsNoTracking()
                .Select(f => new { f.FactionId, f.Name })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(f => f.FactionId, f => f.Name);
    }

    /// <summary>The capital class an NPC group holds, from its name; null for anything else.</summary>
    internal static NpcCapitalKind? CapitalKind(string group)
    {
        if (group.StartsWith("FW ", StringComparison.Ordinal)
            || group.StartsWith("Mission ", StringComparison.Ordinal)
            || group.StartsWith("Homefront ", StringComparison.Ordinal)) return null;
        if (group.Contains("Titan", StringComparison.Ordinal)) return NpcCapitalKind.Titan;
        if (group.Contains("Supercarrier", StringComparison.Ordinal)
            || group.Contains("Super Carrier", StringComparison.Ordinal)) return NpcCapitalKind.Supercarrier;
        if (group.Contains("Dreadnought", StringComparison.Ordinal)) return NpcCapitalKind.Dreadnought;
        return null;
    }
}
