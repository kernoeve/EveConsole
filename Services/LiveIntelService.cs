using System.Globalization;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>A pilot believed to be in a system right now, and what last put them there.</summary>
/// <param name="FromKillmail">True when the placement is a killmail they were on, false for an
/// intel report.</param>
/// <param name="NoVisual">The report said "nv": in local, not seen on grid.</param>
public sealed record LivePilot(
    long CharacterId, string Name, int? ShipTypeId, string? Ship,
    DateTimeOffset At, bool FromKillmail, bool NoVisual,
    long CorporationId = 0, long AllianceId = 0);

/// <summary>Everyone currently placed in one system.</summary>
/// <param name="Unidentified">Pilots the newest standing report counted but did not name —
/// "+5", "3 lokis", or names that did not resolve to a character.</param>
/// <param name="Facts">What the standing reports said besides who — "bubbles, gate camp · on the
/// QZ-X77 gate" — in the interface language; null when nothing.</param>
/// <param name="Ships">The newest report's hulls with nobody named in them, as read.</param>
/// <param name="Freshness">1 when the newest news here is new, falling to 0 at the end of its
/// lifetime: what the map fades a mark by.</param>
public sealed record SystemHostiles(int SystemId, IReadOnlyList<LivePilot> Pilots, int Unidentified,
                                    string? Facts = null, string? Ships = null, double Freshness = 1.0)
{
    public int Count => Pilots.Count + Unidentified;

    /// <summary>The worst standings tier among the pilots and the camps' attackers; an unnamed count is
    /// Neutral. What the map colours the mark by.</summary>
    public StandingTier Tier { get; init; } = StandingTier.Neutral;

    /// <summary>Seen, no visual only, or only something said (bubbles, a camp).</summary>
    public IntelSystemState State { get; init; } = IntelSystemState.Hostile;

    /// <summary>The newest news here, for "3 min ago".</summary>
    public DateTimeOffset NewestAt { get; init; }
}

/// <summary>A system called clear, and nothing newer said of it.</summary>
/// <param name="Freshness">1 when called, 0 when the clear lifetime is over: what the mark fades by.</param>
public sealed record LiveClear(int SystemId, DateTimeOffset At, double Freshness);

/// <summary>One of the user's own characters, online, and where it is.</summary>
public sealed record OwnPilot(string Name, int SystemId, string? Hull, string? ShipName, bool Docked, string? Place,
                              long CharacterId = 0, int? ShipTypeId = null, long CorporationId = 0, long AllianceId = 0);

/// <summary>An officer NPC seen on a killmail since the last downtime: its latest kill in one system.</summary>
/// <param name="Name">The officer's own name, as the screen names it ("Ramaku Basta").</param>
/// <param name="Kills">Killmails it has been on in this system since downtime.</param>
/// <param name="VictimShip">What its latest kill was flying, as the screen names it; null when unknown.</param>
public sealed record OfficerSighting(string Name, int TypeId, int SystemId, DateTimeOffset LastKill, int Kills,
                                     string? VictimShip);

/// <param name="Officers">Officer NPCs per system, seen on a killmail since the last downtime.</param>
/// <param name="Diamonds">♦ pirate NPCs killing players per system: one run per faction, the
/// latest kill within the last five days. See <see cref="LiveIntelService.DiamondGap"/>.</param>
/// <param name="Capitals">NPC titans, supercarriers and dreadnoughts per system, seen on a killmail
/// since the last downtime.</param>
public sealed record LiveMapSnapshot(
    IReadOnlyDictionary<int, SystemHostiles>                  Hostiles,
    IReadOnlyDictionary<int, IReadOnlyList<OwnPilot>>         Own,
    DateTimeOffset                                             At,
    IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>>? Officers = null,
    IReadOnlyDictionary<int, IReadOnlyList<DiamondActivity>>?    Diamonds = null,
    IReadOnlyDictionary<int, IReadOnlyList<NpcCapitalSighting>>? Capitals = null)
{
    /// <summary>Systems called clear, with nothing newer: the map's green check.</summary>
    public IReadOnlyDictionary<int, LiveClear> Clears { get; init; } = new Dictionary<int, LiveClear>();

    /// <summary>Active gate camps, each drawn on the line of its gate.</summary>
    public IReadOnlyList<IntelMapCamp> Camps { get; init; } = [];

    /// <summary>Where reported pilots came from and were heading: the map's arrows, every live one in New Eden,
    /// newest first. Each map caps them after keeping its own systems.</summary>
    public IReadOnlyList<IntelMapArrow> Arrows { get; init; } = [];

    /// <summary>The shared snapshot this was read from: the route planner's hostiles column reads it too.</summary>
    public IntelSnapshot? Intel { get; init; }
}

/// <summary>
/// Who is where right now, in the form the Universe Map, the system pages and the route planner
/// have always read: an adapter over the one shared intel read side,
/// <see cref="IntelSnapshotService"/>, through <see cref="IntelLens"/>.
///
/// <para>What the map shows is therefore what every intel view shows: each pilot where their last
/// sighting put them (a report, a pasted Local, or a kill), for as long as that kind of sighting
/// lasts (Settings → Intel), gone once the system is called clear or a pasted Local leaves them out;
/// the unnamed count and the flags of the newest reports; and how fresh the news is.</para>
///
/// <para><b>Hostile means not ours and not blue.</b> With no character chosen the map has no
/// viewer, so a pilot counts as friendly when any of the user's characters has them blue (or the
/// character forced in Settings → Intel does): nobody else is known to be friendly.</para>
/// </summary>
public sealed partial class LiveIntelService(IntelSnapshotService snapshots, IDbContextFactory<AppDbContext>? dbFactory = null,
                                             AppErrorLogger? errors = null)
{
    /// <summary>How old the shared snapshot may be before the map's own poll reads a new one: just
    /// under the map's five-second tick, so it never shows a picture a tick behind.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(4);

    /// <summary>The shared snapshot itself, which the map subscribes to.</summary>
    public IntelSnapshotService Snapshots => snapshots;

    public async Task<LiveMapSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        await ToMapAsync(await snapshots.FreshAsync(MaxAge, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>The map's form of a snapshot, with the hulls a report counted named in the interface
    /// language. Reads the type names, so off the UI thread.</summary>
    public async Task<LiveMapSnapshot> ToMapAsync(IntelSnapshot snapshot, CancellationToken ct = default)
    {
        Func<string, string> hullName = n => n;
        if (dbFactory is null) return ToMap(snapshot, DateTimeOffset.UtcNow, hullName);

        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var english = snapshot.Systems.Values.SelectMany(s => IntelDisplay.ParseShips(s.Unnamed?.Ships)).Select(s => s.Name).ToList();
        if (english.Count > 0)
            hullName = await IntelDisplay.HullNamesAsync(db, english, ct).ConfigureAwait(false);

        // ♦ rat activity and NPC capitals come from killmails, not intel: read here beside the
        // snapshot (reused for two minutes), not by the intel pass. ⚠️ A failed read costs only
        // those marks: the intel on the map must keep moving without them.
        var map = ToMap(snapshot, now, hullName);
        try
        {
            var (diamonds, capitals) = await NpcActivityAsync(db, now, ct).ConfigureAwait(false);
            return map with { Diamonds = diamonds, Capitals = capitals };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            errors?.Log(nameof(LiveIntelService), "reading NPC activity", ex);
            return map;
        }
    }

    /// <summary>The map's form of a snapshot, seen with no viewer: every system with hostiles or
    /// something said, the systems called clear, the active camps and the reports' arrows, every own
    /// character, every officer.</summary>
    public static LiveMapSnapshot ToMap(IntelSnapshot snapshot, DateTimeOffset now, Func<string, string>? hullName = null)
    {
        hullName ??= n => n;
        var viewer = IntelMapView.Viewer();
        var view   = IntelLens.Project(snapshot, viewer, now, _ => null);

        var hostiles = new Dictionary<int, SystemHostiles>();
        var clears   = new Dictionary<int, LiveClear>();
        foreach (var row in view.Systems)
        {
            if (row.State == IntelSystemState.Clear)
            {
                clears[row.SystemId] = new LiveClear(row.SystemId, row.NewestAt, row.Freshness);
                continue;
            }
            var pilots = row.Pilots
                .OrderByDescending(p => p.Pilot.At)
                .Select(p => new LivePilot(
                    p.Pilot.CharacterId,
                    p.Pilot.Name ?? string.Format(MapText.LiveCharacterId, p.Pilot.CharacterId.ToString(CultureInfo.InvariantCulture)),
                    p.Pilot.ShipTypeId, p.Pilot.Ship, p.Pilot.At, p.Pilot.FromKill, p.Pilot.NoVisual,
                    p.Pilot.CorporationId, p.Pilot.AllianceId))
                .ToList();
            var flags = row.Facts.Aggregate(0, (f, x) => f | (int)x.Flag);
            if (row.Camps.Count > 0) flags |= (int)Monitoring.IntelRules.IntelFlags.Camp;
            hostiles[row.SystemId] = new SystemHostiles(
                row.SystemId, pilots, row.Unnamed,
                IntelDisplay.Facts(flags, row.Gate),
                row.Unnamed > 0 ? IntelDisplay.Ships(row.Intel.Unnamed?.Ships, hullName) : null,
                row.Freshness)
            {
                Tier = row.Tier, State = row.State, NewestAt = row.NewestAt,
            };
        }

        var own = snapshot.Own
            .GroupBy(o => o.SystemId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<OwnPilot>)[.. g
                .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(o => new OwnPilot(o.Name, o.SystemId, o.Hull, o.ShipName, o.Docked, o.Place,
                                          o.CharacterId, o.ShipTypeId, o.CorporationId, o.AllianceId))]);

        return new LiveMapSnapshot(hostiles, own, now, snapshot.Officers)
        {
            Clears = clears,
            Camps  = IntelMapView.Camps(snapshot, view, viewer, now),
            Arrows = IntelMapView.Arrows(view),
            Intel  = snapshot,
        };
    }

    // ── Officer spawns ───────────────────────────────────────────────────────
    //
    // An officer NPC's own death makes no killmail, but every pilot it kills does, with the
    // officer among the attackers. That is the only trace one leaves, so the map shows where an
    // officer has killed since the last downtime — when officers despawn — as a pointer to where
    // one may still be. Whether somebody has killed it since cannot be known.

    /// <summary>The latest downtime: 11:00 EVE time (UTC) today, or yesterday before then.</summary>
    internal static DateTimeOffset LastDowntime(DateTimeOffset now)
    {
        var today = new DateTimeOffset(now.UtcDateTime.Date.AddHours(11), TimeSpan.Zero);
        return now >= today ? today : today.AddDays(-1);
    }

    private static readonly SemaphoreSlim s_officerGate = new(1, 1);
    private static IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>>? s_officers;
    private static DateTimeOffset s_officersAt;
    private static HashSet<int>? s_officerTypes;

    /// <summary>How long one reading of the officers is reused. The map refreshes every few
    /// seconds; a day of killmails does not need reading that often for a mark measured in hours.</summary>
    private static readonly TimeSpan OfficerReuse = TimeSpan.FromMinutes(1);

    /// <summary>The SDE's Entity category: NPCs.</summary>
    private const int EntityCategoryId = 11;

    /// <summary>Officers per system since the last downtime. Read by the intel snapshot on each
    /// pass, and reused for a minute.</summary>
    internal static async Task<IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>>> OfficersAsync(
        AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        await s_officerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var downtime = LastDowntime(now);
            // Reused only within one EVE day: the first reading after downtime starts afresh.
            if (s_officers is { } cached && now - s_officersAt < OfficerReuse && s_officersAt >= downtime)
                return cached;

            // ⚠️ By SDE group, not by name: every officer group is an Entity group named for its
            // faction's officers ("Asteroid Guristas Officer", "… Officer Frigate"), and that is
            // the only thing they share. Read once per process; it changes only with the SDE.
            s_officerTypes ??= (await (from t in db.SdeTypes.AsNoTracking()
                                       join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                                       where g.CategoryId == EntityCategoryId && g.Name.Contains("Officer")
                                       select t.TypeId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
            var officerTypes = s_officerTypes.ToList();

            // ⚠️ Raw SQL for the time: SQLite cannot translate a DateTimeOffset comparison in LINQ.
            var kills = await db.KillMailDetails
                .FromSqlRaw("""SELECT * FROM "KillMailDetails" WHERE "KillMailTime" >= {0}""", downtime)
                .AsNoTracking()
                .Select(k => new { k.KillMailId, k.KillMailTime, k.SolarSystemId, k.VictimShipTypeId })
                .ToListAsync(ct).ConfigureAwait(false);

            var result = new Dictionary<int, IReadOnlyList<OfficerSighting>>();
            if (kills.Count > 0 && officerTypes.Count > 0)
            {
                var killIds = kills.Select(k => k.KillMailId).ToList();
                var seen = await db.KillMailAttackers.AsNoTracking()
                    .Where(a => killIds.Contains(a.KillMailId) && a.ShipTypeId != null && officerTypes.Contains(a.ShipTypeId.Value))
                    .Select(a => new { a.KillMailId, ShipTypeId = a.ShipTypeId!.Value })
                    .Distinct()
                    .ToListAsync(ct).ConfigureAwait(false);

                if (seen.Count > 0)
                {
                    var killById = kills.ToDictionary(k => k.KillMailId);
                    var typeIds  = seen.Select(s => s.ShipTypeId)
                        .Concat(seen.Select(s => killById[s.KillMailId].VictimShipTypeId)).Distinct().ToList();
                    var names = await db.SdeTypes.AsNoTracking()
                        .Where(t => typeIds.Contains(t.TypeId))
                        .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct).ConfigureAwait(false);
                    string Named(int id) => SdeNames.Type(id, names.GetValueOrDefault(id, id.ToString(CultureInfo.InvariantCulture)));

                    foreach (var bySystem in seen.GroupBy(s => killById[s.KillMailId].SolarSystemId))
                        result[bySystem.Key] = bySystem
                            .GroupBy(s => s.ShipTypeId)
                            .Select(byOfficer =>
                            {
                                var latest = byOfficer.Select(s => killById[s.KillMailId])
                                                      .MaxBy(k => k.KillMailTime)!;
                                return new OfficerSighting(Named(byOfficer.Key), byOfficer.Key, bySystem.Key,
                                    latest.KillMailTime, byOfficer.Count(),
                                    latest.VictimShipTypeId > 0 ? Named(latest.VictimShipTypeId) : null);
                            })
                            .OrderByDescending(o => o.LastKill)
                            .ToList();
                }
            }

            s_officers   = result;
            s_officersAt = now;
            return result;
        }
        finally { s_officerGate.Release(); }
    }

    internal sealed record Friends(HashSet<long> Characters, HashSet<long> Corporations, HashSet<long> Alliances);

    /// <summary>The user's own characters, corporations and alliances, and whoever they have set
    /// to positive standing.</summary>
    internal static async Task<Friends> FriendlyAsync(AppDbContext db, CancellationToken ct)
    {
        var chars = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.CorporationId, c.AllianceId }).ToListAsync(ct);
        var corps = await db.Corporations.AsNoTracking().Select(c => c.Id).ToListAsync(ct);

        var friends = new Friends(
            chars.Select(c => c.Id).ToHashSet(),
            chars.Select(c => (long)c.CorporationId).Concat(corps.Select(c => (long)c)).ToHashSet(),
            chars.Where(c => c.AllianceId is > 0).Select(c => (long)c.AllianceId!.Value).ToHashSet());

        var blues = await db.EsiContacts.AsNoTracking()
            .Where(c => c.Standing > 0)
            .Select(c => new { c.ContactId, c.ContactType })
            .ToListAsync(ct);
        foreach (var b in blues)
            switch (b.ContactType)
            {
                case "character":   friends.Characters.Add(b.ContactId);   break;
                case "corporation": friends.Corporations.Add(b.ContactId); break;
                case "alliance":    friends.Alliances.Add(b.ContactId);    break;
            }

        return friends;
    }

    internal static bool TryParseReported(string text, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at);
}
