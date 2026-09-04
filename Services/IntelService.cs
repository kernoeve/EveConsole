using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Turns intel-channel chat into placed sightings.
///
/// Parsing itself lives in <see cref="IntelRules"/> and is pure. This is everything around it:
/// resolving the names it finds, writing the reports, and retiring the ones a newer sighting
/// has superseded.
///
/// Name resolution is local first. The entity-name cache already holds ~225,000 character
/// names from stored killmails, which answers most lines without touching the network; only
/// what it cannot answer goes to ESI's public POST /universe/ids/, batched once per pass
/// rather than once per name. Negative answers are remembered for the session too, or the junk
/// in an intel channel — ship types, "nv", gate names — would be asked about over and over.
/// </summary>
public sealed class IntelService(
    IDbContextFactory<AppDbContext> dbFactory,
    EsiClient                       esi,
    MonitoringSettings              settings,
    AppErrorLogger                  errorLogger) : ReactiveObject
{
    // ── Observable state ─────────────────────────────────────────────────────
    // This runs off the chat importer's loop with no UI of its own, so without these it is
    // invisible: there is no way to tell a long backlog apart from a feature that is not
    // working. Surfaced in Settings → Chat Logs and on the Chat Log viewer.

    private string _statusText = "Intel: idle";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    /// <summary>Messages left to consider, so a backlog reads as progress rather than a stall.</summary>
    private int _backlog;
    public int Backlog
    {
        get => _backlog;
        private set => this.RaiseAndSetIfChanged(ref _backlog, value);
    }

    /// <summary>ESI takes a list; keeping it modest avoids a long stall on one request.</summary>
    private const int LookupBatch = 100;

    /// <summary>How many messages one pass considers. Live traffic is far below this; the
    /// backfill loops until it runs out.</summary>
    private const int MessageBatch = 2_000;

    private Dictionary<string, int>? _systems;
    private Dictionary<string, int>? _ships;

    /// <summary>Name → character id, or null for "asked, and it is not a character". Session
    /// lifetime: the positives are already persisted in UniverseNames, and the negatives are
    /// only worth keeping for as long as the same chatter keeps recurring.</summary>
    private readonly Dictionary<string, long?> _nameCache = new(StringComparer.OrdinalIgnoreCase);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parses everything that has arrived since the last pass.
    ///
    /// Drains rather than doing a single batch. A batch-per-tick looks cheaper but never
    /// catches up from a cold start: with a backlog of several hundred thousand messages and
    /// 2,000 taken per tick, the watermark crawls through year-old history for days while the
    /// overlays — which only look at the last 15 minutes to 24 hours — stay empty the whole
    /// time. Idle cost is unchanged: one indexed query that returns nothing.
    /// </summary>
    public async Task<int> ProcessNewAsync(CancellationToken ct = default)
    {
        var channels = settings.ChatIntelChannels;
        if (channels.Count == 0) return 0;

        return await RunAsync(channels, once: false, null, ct);
    }

    /// <summary>
    /// Re-parses the stored history of the intel channels, oldest first. Used by the button in
    /// settings — without it the overlays stay empty until fresh intel is posted, when tens of
    /// thousands of usable messages are already on disk.
    ///
    /// <para>Existing reports for the period being re-parsed are discarded rather than resumed
    /// around: a re-parse is normally asked for because the rules changed, and leaving the old
    /// rows would keep whatever the old rules got wrong — the unique index on ChatMessageId makes
    /// the second pass skip them.</para>
    ///
    /// <para>⚠️ "For the period being re-parsed" is the whole point. It used to clear every report
    /// unconditionally, which was harmless while chat was kept forever. Now that Data Retention
    /// can purge old chat, reports derived from messages that are gone cannot be regenerated —
    /// clearing them would destroy history permanently. So the cut starts at the oldest chat
    /// message still stored in these channels: everything from there on will be rebuilt, and
    /// everything before it is left alone.</para>
    /// </summary>
    public async Task<int> BackfillAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var channels = settings.ChatIntelChannels;
        if (channels.Count == 0) return 0;

        using (var db = dbFactory.CreateDbContext())
        {
            // The horizon of what a re-parse can actually reproduce.
            var oldest = await db.ChatMessages.AsNoTracking()
                .Where(m => channels.Contains(m.ChannelName) && !m.IsSystemMessage)
                .MinAsync(m => (string?)m.OccurredAt, ct);

            if (oldest is null) return 0;   // nothing stored to parse; keep what we have

            // Channel-scoped as well as time-scoped: a report from a channel no longer configured
            // for intel would not be regenerated either, so it is not ours to delete.
            var doomed = db.IntelReports
                .Where(r => string.Compare(r.ReportedAt, oldest) >= 0 && channels.Contains(r.ChannelName));

            await db.IntelReportCharacters
                .Where(c => doomed.Any(r => r.Id == c.IntelReportId))
                .ExecuteDeleteAsync(ct);
            await doomed.ExecuteDeleteAsync(ct);
        }

        settings.IntelWatermark = 0;
        return await RunAsync(channels, once: false, progress, ct);
    }

    // ── Pass ─────────────────────────────────────────────────────────────────

    private async Task<int> RunAsync(
        IReadOnlyList<string> channels, bool once, IProgress<string>? progress, CancellationToken ct)
    {
        var written = 0;
        var seen    = 0;
        var total   = 0;
        IsRunning   = true;

        try
        {
        while (!ct.IsCancellationRequested)
        {
            using var db = dbFactory.CreateDbContext();

            var after = settings.IntelWatermark;
            var batch = await db.ChatMessages.AsNoTracking()
                .Where(m => channels.Contains(m.ChannelName) && !m.IsSystemMessage && m.Id > after)
                .OrderBy(m => m.Id)
                .Take(MessageBatch)
                .Select(m => new { m.Id, m.OccurredAt, m.ChannelName, m.SenderName, m.Message })
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                Backlog = 0;
                break;
            }

            // Counted from the database once per pass — asking every 2,000 rows would cost more
            // than it tells anyone — and then decremented as batches are consumed, so the figure
            // counts down rather than sitting at whatever it was when the pass began.
            if (seen == 0)
                total = await db.ChatMessages.AsNoTracking()
                    .CountAsync(m => channels.Contains(m.ChannelName)
                                  && !m.IsSystemMessage && m.Id > after, ct);

            var systems = await SystemsAsync(db, ct);
            var ships   = await ShipsAsync(db, ct);
            bool IsSystem(string s) => systems.ContainsKey(s);
            bool IsShip(string s)   => ships.ContainsKey(s);

            // One resolution pass for the whole batch, so a busy channel costs a handful of ESI
            // calls rather than one per line.
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in batch)
            {
                foreach (var c in IntelRules.NameCandidates(m.Message, IsSystem))
                    candidates.Add(c);
                // The reporter too: their name comes from the log header rather than the text,
                // so it is never a parse candidate, but it is a character all the same.
                if (!string.IsNullOrWhiteSpace(m.SenderName)) candidates.Add(m.SenderName);
            }

            await ResolveAsync(db, candidates, ct);

            bool IsCharacter(string s) => _nameCache.TryGetValue(s, out var id) && id is not null;

            var seenCharacters = new List<long>();

            // Already-parsed messages, asked once for the batch rather than once per report.
            var batchIds = batch.Select(m => m.Id).ToList();
            var already  = (await db.IntelReports.AsNoTracking()
                .Where(r => batchIds.Contains(r.ChatMessageId))
                .Select(r => r.ChatMessageId)
                .ToListAsync(ct)).ToHashSet();

            // The chat message id is not enough on its own. A log file that is re-read from the
            // start — which happens whenever its length appears to go backwards, routine on a
            // synced share — gives every line a brand-new id, and a new id is indistinguishable
            // from a genuinely new post. So also key on what actually identifies a report: what
            // was said, by whom, about where, and when. Doubles as the within-batch guard.
            var minAt = batch.Min(m => m.OccurredAt) ?? "";
            var maxAt = batch.Max(m => m.OccurredAt) ?? "";
            var seenContent = (await db.Database.SqlQueryRaw<string>(
                    """
                    SELECT "ReportedAt" || '|' || "ReporterName" || '|' || "SystemId" || '|' || "Message" AS "Value"
                    FROM "IntelReports" WHERE "ReportedAt" >= {0} AND "ReportedAt" <= {1}
                    """, minAt, maxAt)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            var pending = new List<(IntelReport Report, List<IntelReportCharacter> Pilots)>();

            // A clear obsoletes everything in its system older than itself, so only the newest
            // clear per system in this batch needs applying — an earlier one can only ever
            // retire a subset of what the later one does.
            var clears = new Dictionary<int, string>();

            foreach (var m in batch)
            {
                var parsed = IntelRules.Parse(m.Message, IsSystem, IsCharacter, IsShip);
                if (parsed is null) continue;

                if (parsed.Kind == IntelRules.IntelKind.Clear)
                {
                    if (systems.TryGetValue(parsed.SystemName, out var clearedId) &&
                        (!clears.TryGetValue(clearedId, out var prev) ||
                         string.CompareOrdinal(m.OccurredAt, prev) > 0))
                        clears[clearedId] = m.OccurredAt;
                    continue;
                }

                if (!systems.TryGetValue(parsed.SystemName, out var systemId)) continue;
                if (already.Contains(m.Id)) continue;

                // Add returns false when this exact report is already stored, or has already
                // been built earlier in this same batch.
                if (!seenContent.Add($"{m.OccurredAt}|{m.SenderName}|{systemId}|{m.Message}"))
                    continue;

                var built = BuildReport(m.Id, m.OccurredAt, m.ChannelName, m.SenderName,
                                        systemId, m.Message, parsed, ships);
                if (built is null) continue;

                pending.Add(built.Value);
                seenCharacters.AddRange(built.Value.Pilots.Select(p => p.CharacterId));
                if (built.Value.Report.ReporterCharacterId is { } rid) seenCharacters.Add(rid);
            }

            written += await FlushAsync(db, pending, ct);
            await ApplyClearsAsync(db, clears, ct);

            // One affiliation pass for the whole batch rather than one per report.
            await EnsureAffiliationsAsync(db, seenCharacters, ct);

            // Keeps the write-ahead log from running away over a long backfill. Left to grow it
            // reached 1.18 GB, at which point every read had to search it and the periodic
            // automatic checkpoint became a multi-second stall.
            await CheckpointAsync(db, ct);

            settings.IntelWatermark = batch[^1].Id;
            seen += batch.Count;

            // One figure, published once, so the two rows in Background Processes cannot
            // disagree — which is what they did while Backlog held the pass's opening count and
            // the status line quietly counted down from it.
            Backlog = Math.Max(0, total - seen);

            var day  = batch[^1].OccurredAt.Length >= 10 ? batch[^1].OccurredAt[..10] : "";
            var left = Backlog;
            StatusText = left > 0
                ? $"Intel: parsing {day} — {written:N0} sightings, {left:N0} messages to go"
                : $"Intel: parsing {day} — {written:N0} sightings";
            progress?.Report(StatusText);

            if (once || batch.Count < MessageBatch) break;
        }

        // Superseding is applied once, at the end, rather than per report. It is a property of
        // the whole set — a sighting is stale when a newer one names the same pilot — so it can
        // be derived in a single statement instead of maintained incrementally with an update
        // per report. Only ever sets the flag, never clears it, so running it repeatedly is
        // safe and an interrupted pass simply recomputes on the next one.
        if (written > 0)
        {
            using var db = dbFactory.CreateDbContext();
            await SupersedeAllAsync(db, ct);
            await CheckpointAsync(db, ct);
        }

        return written;
        }
        finally
        {
            IsRunning = false;
            Backlog   = 0;
            if (written > 0 || StatusText == "Intel: idle")
                StatusText = await SummaryAsync(ct);
        }
    }

    /// <summary>What the status line says when nothing is running.</summary>
    private async Task<string> SummaryAsync(CancellationToken ct)
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var total = await db.IntelReports.CountAsync(ct);
            if (total == 0) return "Intel: nothing parsed yet";

            var newest = await db.IntelReports.AsNoTracking()
                .OrderByDescending(r => r.ReportedAt).Select(r => r.ReportedAt).FirstAsync(ct);

            return $"Intel: up to date — {total:N0} sightings, newest {newest.Replace('T', ' ').TrimEnd('Z')}";
        }
        catch { return "Intel: up to date"; }
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>Builds a report and its pilots in memory. No database access — the caller
    /// writes a whole batch at once.</summary>
    private (IntelReport Report, List<IntelReportCharacter> Pilots)? BuildReport(
        int chatMessageId, string reportedAt, string channel, string reporter,
        int systemId, string message, IntelRules.ParsedIntel parsed, Dictionary<string, int> ships)
    {
        var report = new IntelReport
        {
            ReportedAt    = reportedAt,
            ChannelName   = channel,
            ReporterName  = reporter,
            SystemId      = systemId,
            SystemName    = parsed.SystemName,
            PlayerCount   = parsed.PlayerCount,
            Note          = string.IsNullOrWhiteSpace(parsed.Note) ? null : parsed.Note,
            Message       = message,
            NoVisual      = parsed.NoVisual,
            Obsolete      = false,
            ReporterCharacterId = _nameCache.TryGetValue(reporter, out var rid) ? rid : null,
            ChatMessageId = chatMessageId,
        };

        var pilots = new List<IntelReportCharacter>();
        var ids    = new HashSet<long>();
        foreach (var pilot in parsed.Pilots)
        {
            if (!_nameCache.TryGetValue(pilot.Name, out var id) || id is null) continue;
            if (!ids.Add(id.Value)) continue;                  // the same pilot named twice
            pilots.Add(new IntelReportCharacter
            {
                CharacterId   = id.Value,
                CharacterName = pilot.Name,
                ShipTypeId    = pilot.Ship is { } s && ships.TryGetValue(s, out var t) ? t : null,
                ShipName      = pilot.Ship,
            });
        }

        return (report, pilots);
    }

    /// <summary>
    /// Writes a batch: two saves rather than two per report.
    ///
    /// The previous version saved each report, saved its pilots, then issued an update to
    /// supersede — on the order of a hundred thousand write transactions across a full parse,
    /// which drove the write-ahead log to 1.18 GB and made the app stall for seconds at a time.
    ///
    /// Reports go first because the pilot rows need their generated ids.
    /// </summary>
    private static async Task<int> FlushAsync(
        AppDbContext db, List<(IntelReport Report, List<IntelReportCharacter> Pilots)> pending,
        CancellationToken ct)
    {
        if (pending.Count == 0) return 0;

        db.IntelReports.AddRange(pending.Select(p => p.Report));
        await db.SaveChangesAsync(ct);

        foreach (var (report, pilots) in pending)
            foreach (var p in pilots)
                p.IntelReportId = report.Id;

        var all = pending.SelectMany(p => p.Pilots).ToList();
        if (all.Count > 0)
        {
            db.IntelReportCharacters.AddRange(all);
            await db.SaveChangesAsync(ct);
        }

        db.ChangeTracker.Clear();
        return pending.Count;
    }

    /// <summary>Applies the newest clear per system, one statement each rather than one per
    /// clear line.</summary>
    private static async Task ApplyClearsAsync(
        AppDbContext db, Dictionary<int, string> clears, CancellationToken ct)
    {
        if (clears.Count == 0) return;

        var now = DateTimeOffset.UtcNow;
        foreach (var (systemId, at) in clears)
            await db.IntelReports
                .Where(r => r.SystemId == systemId && !r.Obsolete
                         && string.Compare(r.ReportedAt, at) < 0)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true)
                                          .SetProperty(r => r.ObsoleteSetOn, now), ct);
    }

    /// <summary>
    /// Marks every sighting that a later one has superseded, in one statement.
    ///
    /// Derived from the whole set rather than maintained report by report, which is both far
    /// cheaper and immune to the ordering problem the incremental version had: reports do not
    /// arrive chronologically, so "retire what came before me" needed a matching "and am I
    /// already out of date" check. Asking the question of the finished set answers both.
    ///
    /// Ties on ReportedAt fall to the higher Id, since chat timestamps are only accurate to the
    /// second and a pilot really does get called in two systems within one.
    /// </summary>
    private static Task SupersedeAllAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(SupersedeSql, [DateTimeOffset.UtcNow.ToString("O")], ct);

    private const string SupersedeSql = """
        UPDATE "IntelReports"
        SET "Obsolete" = TRUE, "ObsoleteSetOn" = {0}
        WHERE "Obsolete" = FALSE
          AND EXISTS (
            SELECT 1
            FROM "IntelReportCharacters" c1
            JOIN "IntelReportCharacters" c2 ON c2."CharacterId" = c1."CharacterId"
            JOIN "IntelReports" r2        ON r2."Id" = c2."IntelReportId"
            WHERE c1."IntelReportId" = "IntelReports"."Id"
              AND r2."Id" <> "IntelReports"."Id"
              AND (r2."ReportedAt" > "IntelReports"."ReportedAt"
                OR (r2."ReportedAt" = "IntelReports"."ReportedAt"
                    AND r2."Id" > "IntelReports"."Id")))
        """;

    /// <summary>
    /// Drains what it can of the write-ahead log into the database.
    ///
    /// <para><b>⚠️ PASSIVE, never TRUNCATE.</b> This ran TRUNCATE, on the reasoning that only
    /// TRUNCATE reclaims the file. It does — but it takes the writer lock and then waits for
    /// every reader to release its snapshot, and the connection carries busy_timeout = 30000, so
    /// a checkpoint that meets a reader sits on the write lock for thirty seconds before giving
    /// up. Every other writer in the app queues behind it and times out too. Measured: all 138
    /// abandoned writes in the error log had one of these in the preceding minute, arriving in
    /// bursts of seventeen at a time, each blaming another victim because the checkpoint had
    /// already finished by the time they failed.</para>
    ///
    /// <para>PASSIVE copies what it can and yields the instant it meets a reader, which is all
    /// this needs: the point is keeping the log SMALL during a long backfill, not shrinking the
    /// file. <see cref="WalCheckpointService"/> reached the same conclusion from the other
    /// direction and its notes are the longer version of this one — including the part about the
    /// intel backfill's checkpoints, which is this code.</para>
    ///
    /// <para>Failure is still ignored: a checkpoint that copies nothing is not a problem.</para>
    /// </summary>
    private static async Task CheckpointAsync(AppDbContext db, CancellationToken ct)
    {
        // A write-ahead log is SQLite's; there is nothing to checkpoint on a server.
        if (!DbEngine.IsSqlite) return;

        try { await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(PASSIVE)", ct); }
        catch { }
    }

    /// <summary>
    /// Fills the affiliation cache for pilots we have just recorded, so the intel list can show
    /// who they fly for without a lookup per row at render time.
    ///
    /// Only ever asks about characters it has never seen. Affiliations do change, but nothing
    /// re-checks them: for reading intel, the corp somebody was in is about as useful as the one
    /// they are in now, and re-fetching thousands of pilots to chase that would cost far more
    /// than it is worth.
    /// </summary>
    private async Task EnsureAffiliationsAsync(
        AppDbContext db, List<long> characterIds, CancellationToken ct)
    {
        if (characterIds.Count == 0) return;

        var wanted = characterIds.Where(i => i > 0).Distinct().ToList();
        if (wanted.Count == 0) return;

        var known = await db.CharacterAffiliations.AsNoTracking()
            .Where(a => wanted.Contains(a.CharacterId))
            .Select(a => a.CharacterId)
            .ToListAsync(ct);

        var missing = wanted.Except(known).ToList();
        if (missing.Count == 0) return;

        try
        {
            var found = await esi.GetAffiliationsAsync(missing, ct);
            if (found.Count == 0) return;

            db.CharacterAffiliations.AddRange(found.Select(f => new CharacterAffiliation
            {
                CharacterId   = f.CharacterId,
                CorporationId = f.CorporationId,
                AllianceId    = f.AllianceId ?? 0,
                PulledAt      = DateTimeOffset.UtcNow,
            }));

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(IntelService), nameof(EnsureAffiliationsAsync), ex);
        }
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Published ship hulls, name → type id. A closed set of about 423, which is what makes it
    /// safe to check before character names — see the ordering note in IntelRules.Parse.
    /// </summary>
    private async Task<Dictionary<string, int>> ShipsAsync(AppDbContext db, CancellationToken ct)
    {
        if (_ships is not null) return _ships;

        var rows = await db.SdeTypes.AsNoTracking()
            .Join(db.SdeGroups.AsNoTracking().Where(g => g.CategoryId == 6),
                  t => t.GroupId, g => g.GroupId, (t, g) => t)
            .Where(t => t.Published)
            .Select(t => new { t.Name, t.TypeId })
            .ToListAsync(ct);

        _ships = new Dictionary<string, int>(rows.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) _ships[r.Name] = r.TypeId;
        return _ships;
    }

    private async Task<Dictionary<string, int>> SystemsAsync(AppDbContext db, CancellationToken ct)
    {
        if (_systems is not null) return _systems;

        var rows = await db.SdeSolarSystems.AsNoTracking()
            .Select(s => new { s.Name, s.SolarSystemId }).ToListAsync(ct);

        _systems = new Dictionary<string, int>(rows.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) _systems[r.Name] = r.SolarSystemId;
        return _systems;
    }

    /// <summary>Fills <see cref="_nameCache"/> for every candidate not already known: the local
    /// name cache first, then ESI for whatever is left.</summary>
    private async Task ResolveAsync(
        AppDbContext db, HashSet<string> candidates, CancellationToken ct)
    {
        var unknown = candidates.Where(c => !_nameCache.ContainsKey(c)).ToList();
        if (unknown.Count == 0) return;

        // Local first — this answers most of them without a request.
        var known = await db.UniverseNames.AsNoTracking()
            .Where(n => n.Category == "character" && unknown.Contains(n.Name))
            .Select(n => new { n.Name, n.EntityId })
            .ToListAsync(ct);

        foreach (var k in known) _nameCache[k.Name] = k.EntityId;

        // Then the names already asked about and found not to be characters. Without this a
        // re-parse asks ESI again about every ship type, gate name and stray word in the
        // channel history, which is the bulk of what a re-parse costs.
        var misses = await db.NameLookupMisses.AsNoTracking()
            .Where(m => unknown.Contains(m.Name)).Select(m => m.Name).ToListAsync(ct);
        foreach (var m in misses) _nameCache[m] = null;

        var stillUnknown = unknown.Where(u => !_nameCache.ContainsKey(u)).ToList();
        if (stillUnknown.Count == 0) return;

        for (var i = 0; i < stillUnknown.Count; i += LookupBatch)
        {
            if (ct.IsCancellationRequested) return;

            var slice = stillUnknown.Skip(i).Take(LookupBatch).ToList();
            try
            {
                var found = await esi.LookupEntityIdsAsync(slice, ct);

                foreach (var (id, name, category) in found)
                    if (category == "character")
                        _nameCache[name] = id;

                // Everything in the slice that came back as nothing, or as a corporation or an
                // alliance, is recorded as "not a character" so it is never asked about again.
                var missed = new List<string>();
                foreach (var s in slice)
                    if (_nameCache.TryAdd(s, null)) missed.Add(s);

                await CacheNamesAsync(db, found, ct);
                await RecordMissesAsync(db, missed, ct);
            }
            catch (Exception ex)
            {
                errorLogger.Log(nameof(IntelService), nameof(ResolveAsync), ex);
                return;   // leave the rest unresolved rather than hammering a failing endpoint
            }
        }
    }

    /// <summary>
    /// Remembers names ESI said are not characters.
    ///
    /// These are recorded per name and never expired. A character created later under a name
    /// already recorded as a miss would stay unrecognised — accepted deliberately, since the
    /// alternative is asking ESI about "nv", "gate" and every ship type on every pass forever.
    /// Clearing the table forces a fresh look.
    /// </summary>
    private static async Task RecordMissesAsync(
        AppDbContext db, List<string> names, CancellationToken ct)
    {
        if (names.Count == 0) return;

        var existing = await db.NameLookupMisses.AsNoTracking()
            .Where(m => names.Contains(m.Name)).Select(m => m.Name).ToListAsync(ct);

        var fresh = names.Except(existing, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Select(n => new NameLookupMiss { Name = n, CheckedAt = DateTimeOffset.UtcNow })
            .ToList();

        if (fresh.Count == 0) return;

        db.NameLookupMisses.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    /// <summary>Writes newly learned names into the shared cache, so the next run — and every
    /// other feature that resolves entities — starts from a better position.</summary>
    private static async Task CacheNamesAsync(
        AppDbContext db, List<(long Id, string Name, string Category)> found, CancellationToken ct)
    {
        if (found.Count == 0) return;

        var ids      = found.Select(f => f.Id).ToList();
        var existing = await db.UniverseNames.AsNoTracking()
            .Where(n => ids.Contains(n.EntityId)).Select(n => n.EntityId).ToListAsync(ct);

        var fresh = found.Where(f => !existing.Contains(f.Id))
            .GroupBy(f => f.Id).Select(g => g.First())
            .Select(f => new UniverseName
            {
                EntityId = f.Id, Name = f.Name, Category = f.Category, PulledAt = DateTimeOffset.UtcNow,
            })
            .ToList();

        if (fresh.Count == 0) return;

        db.UniverseNames.AddRange(fresh);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }
}
