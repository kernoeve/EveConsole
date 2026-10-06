using System.Globalization;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.ViewModels;
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
public sealed record SystemHostiles(int SystemId, IReadOnlyList<LivePilot> Pilots, int Unidentified,
                                    string? Facts = null, string? Ships = null)
{
    public int Count => Pilots.Count + Unidentified;
}

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
public sealed record LiveMapSnapshot(
    IReadOnlyDictionary<int, SystemHostiles>                  Hostiles,
    IReadOnlyDictionary<int, IReadOnlyList<OwnPilot>>         Own,
    DateTimeOffset                                             At,
    IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>>? Officers = null);

/// <summary>
/// Who is where right now: the last place each pilot was put by an intel report or a killmail,
/// and where the user's own characters are.
///
/// <para><b>One placement per pilot, the newest.</b> A report places the pilots it names in its
/// system; a killmail places every player attacker in the kill's system at the kill's time, with
/// the ship on the mail. Whichever is newest is where the pilot is, so a gang reported in one
/// system and then seen on a kill two jumps on moves with the kill. A killmail is the better
/// sighting of the two — the exact pilot and hull, where a report may name neither.</para>
///
/// <para><b>A loss takes the pilot off the map.</b> The victim of a newer killmail is down, and
/// stays off until something places them again.</para>
///
/// <para><b>Only recent placements count</b> (<see cref="Window"/>). Anyone not reported or seen
/// on a kill within it has gone stale and is not shown anywhere.</para>
///
/// <para><b>Hostile means not ours and not blue:</b> the user's own characters, their
/// corporations and alliances, and anyone they or their corporations have set to positive
/// standing are left out. Everyone else is shown — nobody else is known to be friendly.</para>
///
/// <para>⚠️ Computed on read, not stored. Intel is written by every client and killmails only by
/// the one doing the background work, so a table of placements would need both writers to keep
/// it right; reading the two sources together gives every client the same answer from the shared
/// database.</para>
/// </summary>
public sealed class LiveIntelService(
    IDbContextFactory<AppDbContext> dbFactory,
    CorpActivityService             names)
{
    /// <summary>How recent a placement must be to count as "there now".</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private sealed record Placement(
        long CharacterId, DateTimeOffset At, int SystemId, bool Down, bool FromKillmail,
        int? ShipTypeId, string? ShipName, string? Name, int ReportId, bool NoVisual,
        long CorporationId, long AllianceId);

    public async Task<LiveMapSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var now    = DateTimeOffset.UtcNow;
        var cutoff = now - Window;

        using var db = dbFactory.CreateDbContext();

        var placements = new List<Placement>();

        // ── Intel reports ────────────────────────────────────────────────────
        var cutoffText = cutoff.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var reports = await db.IntelReports.AsNoTracking()
            .Where(r => string.Compare(r.ReportedAt, cutoffText) >= 0)
            .Select(r => new { r.Id, r.ReportedAt, r.SystemId, r.PlayerCount, r.NoVisual, r.Obsolete, r.Flags, r.Gate, r.Ships })
            .ToListAsync(ct);

        var reportIds = reports.Select(r => r.Id).ToList();
        var pilots = reportIds.Count == 0 ? [] : await db.IntelReportCharacters.AsNoTracking()
            .Where(c => reportIds.Contains(c.IntelReportId))
            .Select(c => new { c.IntelReportId, c.CharacterId, c.CharacterName, c.ShipTypeId, c.ShipName })
            .ToListAsync(ct);

        var pilotIds = pilots.Select(p => p.CharacterId).Distinct().ToList();
        var affiliations = pilotIds.Count == 0 ? [] : await db.CharacterAffiliations.AsNoTracking()
            .Where(a => pilotIds.Contains(a.CharacterId))
            .ToDictionaryAsync(a => a.CharacterId, a => (Corp: a.CorporationId, Alliance: a.AllianceId), ct);

        var reportById = reports.ToDictionary(r => r.Id);
        foreach (var p in pilots)
        {
            var r = reportById[p.IntelReportId];
            if (!TryParseReported(r.ReportedAt, out var at)) continue;
            var aff = affiliations.GetValueOrDefault(p.CharacterId);
            placements.Add(new Placement(p.CharacterId, at, r.SystemId, Down: false, FromKillmail: false,
                p.ShipTypeId, p.ShipName, p.CharacterName, r.Id, r.NoVisual, aff.Corp, aff.Alliance));
        }

        // ── Killmails ────────────────────────────────────────────────────────
        // ⚠️ Raw SQL for the time: SQLite cannot translate a DateTimeOffset comparison in LINQ.
        var kills = await db.KillMailDetails
            .FromSqlRaw("""SELECT * FROM "KillMailDetails" WHERE "KillMailTime" >= {0}""", cutoff)
            .AsNoTracking()
            .Select(k => new { k.KillMailId, k.KillMailTime, k.SolarSystemId, k.VictimCharId,
                               k.VictimShipTypeId, k.VictimCorpId, k.VictimAllianceId })
            .ToListAsync(ct);

        var killIds = kills.Select(k => k.KillMailId).ToList();
        var attackers = killIds.Count == 0 ? [] : await db.KillMailAttackers.AsNoTracking()
            .Where(a => killIds.Contains(a.KillMailId) && a.CharacterId != null)
            .Select(a => new { a.KillMailId, a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId })
            .ToListAsync(ct);

        var killById = kills.ToDictionary(k => k.KillMailId);
        foreach (var a in attackers)
        {
            var k = killById[a.KillMailId];
            placements.Add(new Placement(a.CharacterId!.Value, k.KillMailTime, k.SolarSystemId, Down: false,
                FromKillmail: true, a.ShipTypeId, null, null, 0, false,
                a.CorporationId ?? 0, a.AllianceId ?? 0));
        }
        foreach (var k in kills.Where(k => k.VictimCharId > 0))
            placements.Add(new Placement(k.VictimCharId, k.KillMailTime, k.SolarSystemId, Down: true,
                FromKillmail: true, k.VictimShipTypeId, null, null, 0, false,
                k.VictimCorpId, k.VictimAllianceId ?? 0));

        // ── Newest placement per pilot ───────────────────────────────────────
        // At the same second a killmail beats a report: its time is the event itself, a report's
        // is when somebody typed it.
        var latest = placements
            .GroupBy(p => p.CharacterId)
            .Select(g => g.OrderByDescending(p => p.At).ThenByDescending(p => p.FromKillmail).First())
            .ToList();

        // A report marked obsolete with none of its pilots seen anywhere since was cleared — a
        // "clr" in that system — so the pilots it still holds are gone, not there.
        var newestReportOfPilot = latest.Where(p => !p.FromKillmail).ToDictionary(p => p.CharacterId, p => p.ReportId);
        bool Cleared(Placement p)
        {
            if (p.FromKillmail) return false;
            var r = reportById[p.ReportId];
            if (!r.Obsolete) return false;
            // Obsolete, but a pilot of the same report has moved on — superseded, not cleared.
            return pilots.Where(x => x.IntelReportId == p.ReportId)
                         .All(x => newestReportOfPilot.TryGetValue(x.CharacterId, out var rid) && rid == p.ReportId);
        }

        var friendly = await FriendlyAsync(db, ct);
        bool IsFriendly(Placement p) =>
            friendly.Characters.Contains(p.CharacterId)
            || (p.CorporationId > 0 && friendly.Corporations.Contains(p.CorporationId))
            || (p.AllianceId    > 0 && friendly.Alliances.Contains(p.AllianceId));

        var standing = latest.Where(p => !p.Down && !Cleared(p) && !IsFriendly(p)).ToList();

        // ── Names ────────────────────────────────────────────────────────────
        // ⚠️ From the local caches only. Asking ESI here put every refresh at the mercy of its
        // latency — with ESI slow or down, the marks stopped moving for minutes. Names the
        // caches lack are fetched in the background and are there on a later refresh; until
        // then the pilot is shown by id.
        var needNames = standing.Where(p => p.Name is null).Select(p => p.CharacterId).Distinct().ToList();
        var resolved = new Dictionary<long, string>();
        if (needNames.Count > 0)
        {
            foreach (var n in await db.UniverseNames.AsNoTracking()
                         .Where(u => needNames.Contains(u.EntityId))
                         .Select(u => new { u.EntityId, u.Name }).ToListAsync(ct))
                resolved[n.EntityId] = n.Name;
            foreach (var c in await db.Characters.AsNoTracking()
                         .Where(c => needNames.Contains(c.Id))
                         .Select(c => new { c.Id, c.Name }).ToListAsync(ct))
                resolved[c.Id] = c.Name;
            ResolveInBackground(needNames.Where(id => !resolved.ContainsKey(id)).ToList());
        }

        var shipIds = standing.Where(p => p.ShipTypeId is not null).Select(p => p.ShipTypeId!.Value).Distinct().ToList();
        var shipNames = shipIds.Count == 0 ? [] : await db.SdeTypes.AsNoTracking()
            .Where(t => shipIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        string? Ship(Placement p) => p.ShipTypeId is int t && shipNames.TryGetValue(t, out var en)
            ? SdeNames.Type(t, en)
            : p.ShipName;

        // ── Per system ───────────────────────────────────────────────────────
        var hostiles = new Dictionary<int, SystemHostiles>();
        foreach (var bySystem in standing.GroupBy(p => p.SystemId))
        {
            var list = bySystem
                .OrderByDescending(p => p.At)
                .Select(p => new LivePilot(p.CharacterId,
                    p.Name ?? resolved.GetValueOrDefault(p.CharacterId)
                           ?? string.Format(MapText.LiveCharacterId, p.CharacterId.ToString(CultureInfo.InvariantCulture)),
                    p.ShipTypeId, Ship(p), p.At, p.FromKillmail, p.NoVisual, p.CorporationId, p.AllianceId))
                .ToList();
            hostiles[bySystem.Key] = new SystemHostiles(bySystem.Key, list, 0);
        }

        // Pilots a report counted without naming. Only the newest report still standing in each
        // system: two reports of the same "+5" a minute apart are the same five, not ten. What
        // the standing reports said besides — a spike, bubbles, the gate — is kept even where
        // nobody was counted: "QZ-X77 bubbles" is worth a mark on its own.
        var hullName = await IntelDisplay.HullNamesAsync(db,
            reports.SelectMany(r => IntelDisplay.ParseShips(r.Ships)).Select(s => s.Name), ct);
        foreach (var bySystem in reports.Where(r => !r.Obsolete).GroupBy(r => r.SystemId))
        {
            var ordered = bySystem.OrderByDescending(r => r.ReportedAt, StringComparer.Ordinal).ThenByDescending(r => r.Id).ToList();
            var newest  = ordered[0];
            var named   = pilots.Count(p => p.IntelReportId == newest.Id);
            var extra   = Math.Max(0, newest.PlayerCount - named);
            var facts   = IntelDisplay.Facts(ordered.Aggregate(0, (f, r) => f | r.Flags),
                                             ordered.Select(r => r.Gate).FirstOrDefault(g => !string.IsNullOrEmpty(g)));
            var ships   = IntelDisplay.Ships(newest.Ships, hullName);
            if (extra == 0 && facts is null) continue;

            hostiles[bySystem.Key] = hostiles.TryGetValue(bySystem.Key, out var h)
                ? h with { Unidentified = extra, Facts = facts, Ships = ships }
                : new SystemHostiles(bySystem.Key, [], extra, facts, ships);
        }

        // ── Own characters ───────────────────────────────────────────────────
        var own = new Dictionary<int, IReadOnlyList<OwnPilot>>();
        foreach (var bySystem in (await MainWindowViewModel.ReadOnlineCharactersAsync(db, ct))
                     .Where(c => c.Online && c.SolarSystemId is not null)
                     .GroupBy(c => c.SolarSystemId!.Value))
            own[bySystem.Key] = bySystem
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(c => new OwnPilot(c.Name, bySystem.Key,
                    c.ShipTypeId is int t && c.Hull is { } hull ? SdeNames.Type(t, hull) : c.Hull,
                    c.ShipName, c.Docked, c.Place, c.CharacterId, c.ShipTypeId, c.CorporationId, c.AllianceId))
                .ToList();

        return new LiveMapSnapshot(hostiles, own, now, await OfficersAsync(db, now, ct));
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

    private static async Task<IReadOnlyDictionary<int, IReadOnlyList<OfficerSighting>>> OfficersAsync(
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

            // ⚠️ Raw SQL for the time, as for the live killmails above: SQLite cannot translate a
            // DateTimeOffset comparison in LINQ.
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

    private Task _resolving = Task.CompletedTask;

    /// <summary>
    /// Looks names up over ESI without holding up the snapshot. One lookup at a time: while one
    /// is running, later refreshes do not start another — they will ask for whatever is still
    /// missing once it is done. The resolver writes what it finds to the name cache, which the
    /// next snapshot reads.
    /// </summary>
    private void ResolveInBackground(List<long> ids)
    {
        if (ids.Count == 0 || !_resolving.IsCompleted) return;
        _resolving = Task.Run(async () =>
        {
            try { await names.ResolveNamesAsync(ids); }
            catch { /* Names are a nicety here; the next refresh asks again. */ }
        });
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
