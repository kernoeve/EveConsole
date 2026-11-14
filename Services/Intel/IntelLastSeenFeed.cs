using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// Everything that feeds <see cref="IntelLastSeen"/> apart from the parser: the kills each importer
/// has just stored, the one-time seed from history, and the "intel changed" signal after a write.
///
/// <para>Kills: every attacker who is a character, and the victim when a character, are seen in the
/// kill's system at the kill's time, with the corporation and alliance the kill shows. The live
/// sources (the zKillboard stream, and the details fetched from ESI for our own and the Mine + Corp
/// poll's kills) always feed it. History (the daily dumps, a page of an entity's kills) feeds only
/// its last <see cref="KillHistory"/>: an older kill can never be anyone's last sighting that a view
/// shows, and a year of dumps would be millions of writes for nothing.</para>
///
/// <para>⚠️ Never fails an import. Where a pilot was last seen is secondary to the kill being stored, so
/// a failure here is logged and the import carries on.</para>
/// </summary>
public sealed class IntelLastSeenFeed(
    IDbContextFactory<AppDbContext> dbFactory,
    AppPreferencesService           prefs,
    ClientSignals                   signals,
    AppErrorLogger                  errorLogger)
{
    /// <summary>Set to "1" once the seed has run, in the shared database: it runs once, not once per PC.</summary>
    public const string KeySeeded = "intel.lastseen.seeded";

    /// <summary>How far back a kill from history still feeds last seen.</summary>
    public static readonly TimeSpan KillHistory = TimeSpan.FromDays(7);

    /// <summary>A burst of writes is one signal, this long after its first. Short: a kill near a
    /// watched character is meant to be heard at once.</summary>
    public static readonly TimeSpan NotifyDelay = TimeSpan.FromSeconds(1);

    private const int NamesPerQuery = 1_000;
    private const int RowsPerUpsert = 2_000;
    private const int SeedReportsPerPage = 5_000;
    private const int SeedKillsPerPage = 500;

    /// <summary>What last seen needs of a kill, whichever form it was stored from.</summary>
    public sealed record KillFacts(
        int KillMailId, DateTimeOffset At, int SystemId,
        long VictimId, long? VictimCorporationId, long? VictimAllianceId, int VictimShipTypeId,
        IReadOnlyList<KillParty> Attackers);

    public readonly record struct KillParty(long? CharacterId, long? CorporationId, long? AllianceId, int? ShipTypeId);

    public static KillFacts Facts(EsiKillMailFull k) => new(
        k.KillMailId, k.KillMailTime, k.SolarSystemId,
        k.Victim?.CharacterId ?? 0, k.Victim?.CorporationId, k.Victim?.AllianceId, k.Victim?.ShipTypeId ?? 0,
        k.Attackers?.Select(a => new KillParty(a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId)).ToList() ?? []);

    /// <summary>Whether a character was among the attackers: a kill worth telling the alarms about.</summary>
    public static bool HasPlayerAttacker(KillFacts k) => k.Attackers.Any(a => a.CharacterId is > 0);

    /// <summary>The sightings a kill gives: each attacker who is a character, and the victim when one.</summary>
    public static IEnumerable<LastSeenRow> Rows(KillFacts k, IReadOnlyDictionary<long, string> names)
    {
        var at = IntelLastSeenWriter.Format(k.At);
        var seen = new HashSet<long>();

        if (k.VictimId > 0 && seen.Add(k.VictimId))
            yield return new LastSeenRow
            {
                CharacterId = k.VictimId, SeenAt = at, Source = IntelLastSeenSource.KillVictim, SystemId = k.SystemId,
                Name = names.GetValueOrDefault(k.VictimId), KillMailId = k.KillMailId,
                ShipTypeId = k.VictimShipTypeId > 0 ? k.VictimShipTypeId : null,
                CorporationId = k.VictimCorporationId, AllianceId = k.VictimAllianceId,
                AffiliationAt = at, AffiliationSource = IntelAffiliationSource.Kill,
            };

        foreach (var a in k.Attackers)
        {
            // A character listed twice on one kill (it happens with drones and fighters) is one sighting.
            if (a.CharacterId is not > 0 || !seen.Add(a.CharacterId.Value)) continue;
            yield return new LastSeenRow
            {
                CharacterId = a.CharacterId.Value, SeenAt = at, Source = IntelLastSeenSource.KillAttacker, SystemId = k.SystemId,
                Name = names.GetValueOrDefault(a.CharacterId.Value), KillMailId = k.KillMailId,
                ShipTypeId = a.ShipTypeId is > 0 ? a.ShipTypeId : null,
                CorporationId = a.CorporationId, AllianceId = a.AllianceId,
                AffiliationAt = at, AffiliationSource = IntelAffiliationSource.Kill,
            };
        }
    }

    /// <summary>
    /// The kills an importer has just stored, in its own database context, after its save. Kills from
    /// history (<paramref name="live"/> false) older than <see cref="KillHistory"/> are left out.
    /// Returns the rows written; never throws but for cancellation.
    /// </summary>
    public Task<int> KillsStoredAsync(
        AppDbContext db, IReadOnlyCollection<EsiKillMailFull> kills, bool live, CancellationToken ct = default)
        => KillsStoredAsync(db, kills.Select(Facts).ToList(), live, ct);

    public async Task<int> KillsStoredAsync(
        AppDbContext db, IReadOnlyCollection<KillFacts> kills, bool live, CancellationToken ct = default)
    {
        if (kills.Count == 0) return 0;
        try
        {
            var since = DateTimeOffset.UtcNow - KillHistory;
            var fed = live ? kills.ToList() : kills.Where(k => k.At >= since).ToList();
            if (fed.Count == 0) return 0;

            var written = await WriteKillsAsync(db, fed, ct);
            if (written > 0 && fed.Any(HasPlayerAttacker)) Changed();
            return written;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(IntelLastSeenFeed), "kills into last seen", ex);
            return 0;
        }
    }

    private static async Task<int> WriteKillsAsync(AppDbContext db, List<KillFacts> kills, CancellationToken ct)
    {
        var ids = kills.SelectMany(k => k.Attackers.Select(a => a.CharacterId ?? 0).Append(k.VictimId))
                       .Where(id => id > 0).Distinct().ToList();
        var names = await NamesAsync(db, ids, ct);
        var rows = kills.SelectMany(k => Rows(k, names)).ToList();

        var written = 0;
        for (var i = 0; i < rows.Count; i += RowsPerUpsert)
            written += await IntelLastSeenWriter.UpsertAsync(db, rows.Skip(i).Take(RowsPerUpsert), ct);
        return written;
    }

    /// <summary>Character names from the local name cache, which holds most pilots ever on a kill here.</summary>
    private static async Task<Dictionary<long, string>> NamesAsync(AppDbContext db, List<long> ids, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        for (var i = 0; i < ids.Count; i += NamesPerQuery)
        {
            var slice = ids.Skip(i).Take(NamesPerQuery).ToList();
            foreach (var n in await db.UniverseNames.AsNoTracking()
                         .Where(n => n.Category == "character" && slice.Contains(n.EntityId))
                         .Select(n => new { n.EntityId, n.Name }).ToListAsync(ct))
                names[n.EntityId] = n.Name;
        }
        return names;
    }

    // ── "Intel changed" ──────────────────────────────────────────────────────

    private int _notifyQueued;

    /// <summary>
    /// Tells every PC that intel was written (<see cref="IntelSignals.Changed"/>): what shows intel
    /// refreshes, and the worker runs its intel alarms. A burst is one signal.
    /// </summary>
    public void Changed()
    {
        if (Interlocked.Exchange(ref _notifyQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(NotifyDelay);
            Interlocked.Exchange(ref _notifyQueued, 0);
            try { await signals.PublishAsync(IntelSignals.Payload(IntelSignals.Changed)); }
            catch (Exception ex) { errorLogger.Log(nameof(IntelLastSeenFeed), "intel changed", ex); }
        });
    }

    // ── The one-time seed ────────────────────────────────────────────────────

    /// <summary>
    /// Fills last seen from what the database already holds, once: the newest report naming each pilot,
    /// over all history, then the kills of the last <see cref="KillHistory"/>. On the worker, in the
    /// background. Newer-wins makes it safe beside the live feeds, and safe to run again after a failure.
    /// Returns whether it ran.
    /// </summary>
    public async Task<bool> SeedOnceAsync(CancellationToken ct = default)
    {
        if (await prefs.ReadFreshAsync(KeySeeded, ct) == "1") return false;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var written = await SeedReportsAsync(db, ct);
        written += await SeedKillsAsync(db, ct);

        await prefs.SetAsync(KeySeeded, "1");
        if (written > 0) Changed();
        return true;
    }

    /// <summary>The newest sighting of each pilot named in a report, with the affiliation pulled for them.</summary>
    private static async Task<int> SeedReportsAsync(AppDbContext db, CancellationToken ct)
    {
        var newest = new Dictionary<long, LastSeenRow>();
        var after = 0;
        while (true)
        {
            // By report, then the pilots of those reports: a page cut by pilot rows could split a report.
            var reports = await db.IntelReports.AsNoTracking()
                .Where(r => r.Id > after && r.Kind == IntelReportKind.Sighting)
                .OrderBy(r => r.Id).Take(SeedReportsPerPage)
                .Select(r => new { r.Id, r.ReportedAt, r.SystemId, r.Source, r.NoVisual })
                .ToListAsync(ct);
            if (reports.Count == 0) break;
            after = reports[^1].Id;

            var first = reports[0].Id;
            var byId = reports.ToDictionary(r => r.Id);
            var pilots = await db.IntelReportCharacters.AsNoTracking()
                .Where(c => c.IntelReportId >= first && c.IntelReportId <= after)
                .ToListAsync(ct);

            foreach (var c in pilots)
            {
                if (!byId.TryGetValue(c.IntelReportId, out var r)) continue;
                var row = ReportRow(r.Id, r.ReportedAt, r.SystemId, r.Source, r.NoVisual, c);
                if (!newest.TryGetValue(c.CharacterId, out var held) || Later(row, held))
                    newest[c.CharacterId] = row;
            }
        }
        if (newest.Count == 0) return 0;

        var rows = await WithPulledAffiliationsAsync(db, newest.Values.ToList(), ct);
        var written = 0;
        for (var i = 0; i < rows.Count; i += RowsPerUpsert)
            written += await IntelLastSeenWriter.UpsertAsync(db, rows.Skip(i).Take(RowsPerUpsert), ct);
        return written;
    }

    private static async Task<int> SeedKillsAsync(AppDbContext db, CancellationToken ct)
    {
        // ⚠️ Raw SQL for the date: a DateTimeOffset in a LINQ Where does not translate on SQLite.
        var since = DateTimeOffset.UtcNow - KillHistory;
        var ids = await db.Database
            .SqlQueryRaw<int>("""SELECT "KillMailId" AS "Value" FROM "KillMailDetails" WHERE "KillMailTime" >= {0}""", since)
            .ToListAsync(ct);

        var written = 0;
        for (var i = 0; i < ids.Count; i += SeedKillsPerPage)
        {
            var slice = ids.Skip(i).Take(SeedKillsPerPage).ToList();
            var details = await db.KillMailDetails.AsNoTracking().Where(d => slice.Contains(d.KillMailId)).ToListAsync(ct);
            var attackers = (await db.KillMailAttackers.AsNoTracking()
                    .Where(a => slice.Contains(a.KillMailId) && a.CharacterId != null)
                    .Select(a => new { a.KillMailId, a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId })
                    .ToListAsync(ct))
                .ToLookup(a => a.KillMailId);

            var facts = details.Select(d => new KillFacts(
                d.KillMailId, d.KillMailTime, d.SolarSystemId,
                d.VictimCharId, d.VictimCorpId, d.VictimAllianceId, d.VictimShipTypeId,
                attackers[d.KillMailId].Select(a => new KillParty(a.CharacterId, a.CorporationId, a.AllianceId, a.ShipTypeId)).ToList()))
                .ToList();
            written += await WriteKillsAsync(db, facts, ct);
        }
        return written;
    }

    // ── Reports ──────────────────────────────────────────────────────────────

    /// <summary>The sighting a report gives one of its pilots.</summary>
    public static LastSeenRow ReportRow(int reportId, string reportedAt, int systemId, int reportSource, bool noVisual,
                                        IntelReportCharacter pilot) => new()
    {
        CharacterId = pilot.CharacterId,
        SeenAt      = reportedAt,
        Source      = reportSource == IntelReportSource.LocalPaste ? IntelLastSeenSource.LocalPaste : IntelLastSeenSource.Report,
        SystemId    = systemId,
        Name        = pilot.CharacterName,
        ReportId    = reportId,
        ShipTypeId  = pilot.ShipTypeId,
        ShipName    = pilot.ShipName,
        NoVisual    = noVisual,
    };

    /// <summary>
    /// The rows with the affiliation pulled from ESI for each pilot, where one is held. A pulled
    /// affiliation only fills a last-seen row that has none from a kill; the writer sees to that.
    /// </summary>
    public static async Task<List<LastSeenRow>> WithPulledAffiliationsAsync(
        AppDbContext db, List<LastSeenRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.CharacterId).Distinct().ToList();
        var pulled = new Dictionary<long, CharacterAffiliation>();
        for (var i = 0; i < ids.Count; i += NamesPerQuery)
        {
            var slice = ids.Skip(i).Take(NamesPerQuery).ToList();
            foreach (var a in await db.CharacterAffiliations.AsNoTracking().Where(a => slice.Contains(a.CharacterId)).ToListAsync(ct))
                pulled[a.CharacterId] = a;
        }

        return [.. rows.Select(r => pulled.TryGetValue(r.CharacterId, out var a)
            ? r with
            {
                CorporationId     = a.CorporationId > 0 ? a.CorporationId : null,
                AllianceId        = a.AllianceId > 0 ? a.AllianceId : null,
                AffiliationAt     = IntelLastSeenWriter.Format(a.PulledAt),
                AffiliationSource = IntelAffiliationSource.Pulled,
            }
            : r)];
    }

    /// <summary>The writer's order: time, then source rank, then the later report or kill.</summary>
    private static bool Later(LastSeenRow a, LastSeenRow b)
    {
        var byTime = string.CompareOrdinal(a.SeenAt, b.SeenAt);
        if (byTime != 0) return byTime > 0;
        if (a.Rank != b.Rank) return a.Rank > b.Rank;
        return a.EventId > b.EventId;
    }
}
