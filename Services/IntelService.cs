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
    /// Parses the whole stored history of the intel channels, oldest first. Used by the button
    /// in settings — without it the overlays stay empty until fresh intel is posted, when tens
    /// of thousands of usable messages are already on disk.
    /// </summary>
    public async Task<int> BackfillAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var channels = settings.ChatIntelChannels;
        if (channels.Count == 0) return 0;

        // From the beginning: the watermark is what "new" means, and a backfill is a request to
        // reconsider everything.
        settings.IntelWatermark = 0;
        return await RunAsync(channels, once: false, progress, ct);
    }

    // ── Pass ─────────────────────────────────────────────────────────────────

    private async Task<int> RunAsync(
        IReadOnlyList<string> channels, bool once, IProgress<string>? progress, CancellationToken ct)
    {
        var written = 0;
        var seen    = 0;
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

            // Counted once per pass, not per batch: it is a progress figure, not a precise one,
            // and asking for it every 2,000 rows would cost more than it tells anyone.
            if (seen == 0)
                Backlog = await db.ChatMessages.AsNoTracking()
                    .CountAsync(m => channels.Contains(m.ChannelName)
                                  && !m.IsSystemMessage && m.Id > after, ct);

            var systems = await SystemsAsync(db, ct);
            bool IsSystem(string s) => systems.ContainsKey(s);

            // One resolution pass for the whole batch, so a busy channel costs a handful of ESI
            // calls rather than one per line.
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in batch)
                foreach (var c in IntelRules.NameCandidates(m.Message, IsSystem))
                    candidates.Add(c);

            await ResolveAsync(db, candidates, ct);

            bool IsCharacter(string s) => _nameCache.TryGetValue(s, out var id) && id is not null;

            foreach (var m in batch)
            {
                var parsed = IntelRules.Parse(m.Message, IsSystem, IsCharacter);
                if (parsed is null) continue;

                if (parsed.Kind == IntelRules.IntelKind.Clear)
                {
                    if (systems.TryGetValue(parsed.SystemName, out var clearedId))
                        await MarkSystemClearAsync(db, clearedId, m.OccurredAt, ct);
                    continue;
                }

                if (!systems.TryGetValue(parsed.SystemName, out var systemId)) continue;

                written += await WriteReportAsync(db, m.Id, m.OccurredAt, m.ChannelName,
                                                  m.SenderName, systemId, parsed, ct);
            }

            settings.IntelWatermark = batch[^1].Id;
            seen += batch.Count;

            var day  = batch[^1].OccurredAt.Length >= 10 ? batch[^1].OccurredAt[..10] : "";
            var left = Math.Max(0, Backlog - seen);
            StatusText = left > 0
                ? $"Intel: parsing {day} — {written:N0} sightings, {left:N0} messages to go"
                : $"Intel: parsing {day} — {written:N0} sightings";
            progress?.Report(StatusText);

            if (once || batch.Count < MessageBatch) break;
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

    private async Task<int> WriteReportAsync(
        AppDbContext db, int chatMessageId, string reportedAt, string channel, string reporter,
        int systemId, IntelRules.ParsedIntel parsed, CancellationToken ct)
    {
        // The unique index on ChatMessageId makes re-parsing harmless, but checking first keeps
        // a re-run from throwing rather than skipping.
        if (await db.IntelReports.AnyAsync(r => r.ChatMessageId == chatMessageId, ct)) return 0;

        var report = new IntelReport
        {
            ReportedAt    = reportedAt,
            ChannelName   = channel,
            ReporterName  = reporter,
            SystemId      = systemId,
            SystemName    = parsed.SystemName,
            PlayerCount   = parsed.PlayerCount,
            Note          = string.IsNullOrWhiteSpace(parsed.Note) ? null : parsed.Note,
            Obsolete      = false,
            ChatMessageId = chatMessageId,
        };

        db.IntelReports.Add(report);
        await db.SaveChangesAsync(ct);

        var ids = new List<long>();
        foreach (var name in parsed.CharacterNames)
        {
            if (!_nameCache.TryGetValue(name, out var id) || id is null) continue;
            if (ids.Contains(id.Value)) continue;              // the same pilot named twice
            ids.Add(id.Value);
            db.IntelReportCharacters.Add(new IntelReportCharacter
            {
                IntelReportId = report.Id,
                CharacterId   = id.Value,
                CharacterName = name,
            });
        }

        if (ids.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await SupersedeAsync(db, report.Id, ids, reportedAt, ct);
        }

        db.ChangeTracker.Clear();
        return 1;
    }

    /// <summary>
    /// Retires earlier sightings of the same pilots. A gang gets called in system after system
    /// as it moves, and every one of those calls is true when made — what makes the older ones
    /// wrong is a newer one somewhere else.
    ///
    /// Compares on ReportedAt rather than on insertion order, so a backfill that walks history
    /// out of order still ends with the newest sighting standing.
    ///
    /// Chat timestamps are only accurate to the second, and a pilot really does get called in
    /// two systems within one second — by two reporters at once, or by one posting a burst.
    /// Without a tie-break neither of those supersedes the other and the pilot stands in both
    /// places at once, which is exactly what the flag exists to prevent. Ties therefore fall to
    /// the higher Id, which is the one written later.
    /// </summary>
    private static async Task SupersedeAsync(
        AppDbContext db, int newReportId, List<long> characterIds, string reportedAt,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        // Reports do not arrive in chronological order. Messages are processed by ChatMessage
        // id, which is insertion order — and a backfill reads whole files at a time, so one
        // channel's July can be stored after another's September. A sighting can therefore be
        // written when the same pilot already has a NEWER one standing, in which case the new
        // row is the stale one and is born obsolete. Without this the pilot stands in both
        // places, which is what the flag exists to prevent.
        var supersededOnArrival = await db.IntelReportCharacters.AsNoTracking()
            .Where(c => characterIds.Contains(c.CharacterId))
            .Join(db.IntelReports.Where(r => !r.Obsolete
                                          && r.Id != newReportId
                                          && (string.Compare(r.ReportedAt, reportedAt) > 0
                                           || (r.ReportedAt == reportedAt && r.Id > newReportId))),
                  c => c.IntelReportId, r => r.Id, (c, r) => r.Id)
            .AnyAsync(ct);

        if (supersededOnArrival)
        {
            await db.IntelReports.Where(r => r.Id == newReportId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true)
                                          .SetProperty(r => r.ObsoleteSetOn, now), ct);
            return;
        }

        var stale = await db.IntelReportCharacters.AsNoTracking()
            .Where(c => characterIds.Contains(c.CharacterId))
            .Join(db.IntelReports.Where(r => !r.Obsolete
                                          && r.Id != newReportId
                                          && (string.Compare(r.ReportedAt, reportedAt) < 0
                                           || (r.ReportedAt == reportedAt && r.Id < newReportId))),
                  c => c.IntelReportId, r => r.Id, (c, r) => r.Id)
            .Distinct()
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        await db.IntelReports.Where(r => stale.Contains(r.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true)
                                      .SetProperty(r => r.ObsoleteSetOn, now), ct);
    }

    /// <summary>
    /// A "clear" call retires everything standing in that system: somebody has looked and
    /// nobody is there. Only sightings older than the call, so a clear cannot retire a sighting
    /// made after it.
    /// </summary>
    private static async Task MarkSystemClearAsync(
        AppDbContext db, int systemId, string reportedAt, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.IntelReports
            .Where(r => r.SystemId == systemId && !r.Obsolete
                     && string.Compare(r.ReportedAt, reportedAt) < 0)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Obsolete, true)
                                      .SetProperty(r => r.ObsoleteSetOn, now), ct);
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

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
                foreach (var s in slice) _nameCache.TryAdd(s, null);

                await CacheNamesAsync(db, found, ct);
            }
            catch (Exception ex)
            {
                errorLogger.Log(nameof(IntelService), nameof(ResolveAsync), ex);
                return;   // leave the rest unresolved rather than hammering a failing endpoint
            }
        }
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
