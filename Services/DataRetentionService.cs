using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// One retention rule: whether it runs, and how far back it keeps.
/// </summary>
/// <param name="MinimumDays">A floor the UI and the service both enforce, so a hand-edited
/// preference cannot set a window short enough to destroy something still being used.</param>
public sealed class RetentionRule(
    AppPreferencesService prefs, string key, int defaultDays, int minimumDays)
{
    public int MinimumDays => minimumDays;
    public int DefaultDays => defaultDays;

    public bool Enabled
    {
        get => prefs.GetBool($"{key}.enabled");
        set => _ = prefs.SetBoolAsync($"{key}.enabled", value);
    }

    public int Days
    {
        get => Math.Max(minimumDays, (int)prefs.GetLong($"{key}.days", defaultDays));
        set => _ = prefs.SetLongAsync($"{key}.days", Math.Max(minimumDays, value));
    }

    /// <summary>
    /// When this rule last actually purged, persisted so the schedule survives restarts.
    ///
    /// <para>⚠️ Stored, not inferred. Without it the sweep can only be "once per launch", which
    /// both re-runs pointlessly when the app is restarted twice in an hour and never runs at all
    /// while the app stays open for a week — the case that matters, since this app is left
    /// running.</para>
    /// </summary>
    public DateTimeOffset? LastRunUtc
    {
        get => DateTimeOffset.TryParse(prefs.Get($"{key}.lastrun"), out var t) ? t : null;
        set => _ = prefs.SetAsync($"{key}.lastrun", value?.UtcDateTime.ToString("O"));
    }

    /// <summary>
    /// Due when enabled and either never run or last run more than <paramref name="every"/> ago.
    /// A rule that has never run is due immediately, so turning one on acts at once rather than
    /// waiting out a full period first.
    /// </summary>
    public bool IsDue(TimeSpan every)
        => Enabled && (LastRunUtc is not { } last || DateTimeOffset.UtcNow - last >= every);

    public void MarkRun() => LastRunUtc = DateTimeOffset.UtcNow;
}

/// <summary>
/// How long the app keeps data it can afford to forget.
///
/// <para>⚠️ Purging rows does NOT shrink the database file. SQLite reuses the freed pages instead
/// of returning them, so the file only gets smaller after a compaction — see
/// <see cref="DatabaseShrinkService"/>. Measured on a real 4.2 GB database, the error log was 0.6%
/// of it and killmails were 61%, which is why these three rules are not equally worth enabling.</para>
///
/// <para>Everything here is off by default. Deleting history the user did not ask to lose is the
/// one failure mode that cannot be undone from inside the app.</para>
/// </summary>
public class DataRetentionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public DataRetentionService(IDbContextFactory<AppDbContext> dbFactory, AppPreferencesService prefs)
    {
        _dbFactory = dbFactory;

        // ⚠️ Minimums differ by what the data is for. The error log is read within hours, so a
        // week is generous. Killmails and price history feed month-over-month comparisons, so a
        // month is the shortest window that leaves them meaningful.
        ErrorLog     = new RetentionRule(prefs, "retention.errorlog",     defaultDays: 30,  minimumDays: 7);
        PriceHistory = new RetentionRule(prefs, "retention.pricehistory", defaultDays: 90,  minimumDays: 30);
        GameLog      = new RetentionRule(prefs, "retention.gamelog",      defaultDays: 365, minimumDays: 30);
        ChatMessages = new RetentionRule(prefs, "retention.chat",         defaultDays: 90,  minimumDays: 30);

        // Killmails in two halves, because they are not worth the same. Your own characters' and
        // corporations' kills and losses are history you look back on; everyone else's — the
        // zKillboard feed, fights you only watched — are the bulk of the space and the part worth
        // trimming. See KillmailScope.
        OurKillmails   = new RetentionRule(prefs, "retention.killmails.ours",   defaultDays: 365, minimumDays: 30);
        OtherKillmails = new RetentionRule(prefs, "retention.killmails.others", defaultDays: 90,  minimumDays: 30);
        CarryOverKillmailRule(prefs);

        // ⚠️ A week is a real floor here, not a formality. The whole point of these rows is to
        // read back WHY an answer was poor, and that is usually noticed days later.
        AgentTelemetry = new RetentionRule(prefs, "retention.agenttelemetry", defaultDays: 90, minimumDays: 7);

        // ⚠️ The deliberate exception to "everything here is off by default" — and it should read
        // as deliberate rather than as an oversight. That rule exists because deleting history the
        // capsuleer did not ask to lose cannot be undone from inside the app. But this is not
        // their history: it is the app's own diagnostic exhaust, several rows per agent turn, each
        // carrying the SQL the model wrote. Nobody will think to enable a sweep for it, and the
        // cost of never doing so is a table that grows for the life of the install.
        //
        // Enabled only when the capsuleer has never expressed a view. Untick it once and that
        // choice stands — this must not switch itself back on at every start.
        if (prefs.Get("retention.agenttelemetry.enabled") is null) AgentTelemetry.Enabled = true;
    }

    public RetentionRule ErrorLog       { get; }
    public RetentionRule OurKillmails   { get; }
    public RetentionRule OtherKillmails { get; }
    public RetentionRule PriceHistory   { get; }
    public RetentionRule GameLog        { get; }
    public RetentionRule ChatMessages   { get; }
    public RetentionRule AgentTelemetry { get; }

    // ── Agent telemetry ───────────────────────────────────────────────────────

    /// <summary>
    /// Drops agent turns older than the window, and the tool calls and usage rows hanging off
    /// them.
    ///
    /// <para>Children first, by the same reasoning as the killmail sweep: an interruption leaves
    /// orphans that the next run clears, rather than a turn whose cost rows have gone and which
    /// would quietly understate the total.</para>
    ///
    /// <para>⚠️ Deletes by joining back to the parent's timestamp rather than trusting the
    /// children's own. A tool call and its turn are written in the same operation, so the two
    /// agree today — but a future write path that batches differently would leave rows whose
    /// parent is gone, and a cost query that reads ServiceUsage alone would then be wrong in the
    /// direction that looks plausible.</para>
    /// </summary>
    public async Task<int> PurgeAgentTelemetryAsync(int days, CancellationToken ct = default)
    {
        days = Math.Max(AgentTelemetry.MinimumDays, days);
        var cutoff = TimestampCutoff(days);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM "AgentToolCalls" WHERE "InteractionId" IN (
                 SELECT "Id" FROM "AgentInteractions" WHERE "StartedAt" < {cutoff})
             """, ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM "ServiceUsage" WHERE "InteractionId" IN (
                 SELECT "Id" FROM "AgentInteractions" WHERE "StartedAt" < {cutoff})
             """, ct);

        // Usage with no turn behind it — a TTS or transcription call logged outside an exchange —
        // ages out on its own timestamp, since there is no parent to date it by.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             DELETE FROM "ServiceUsage" WHERE "InteractionId" IS NULL AND "OccurredAt" < {cutoff}
             """, ct);

        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "AgentInteractions" WHERE "StartedAt" < {cutoff}""", ct);
    }

    // ── Error log ─────────────────────────────────────────────────────────────

    public async Task<int> PurgeErrorLogAsync(int days, CancellationToken ct = default)
    {
        days = Math.Max(ErrorLog.MinimumDays, days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ⚠️ Raw SQL, not a LINQ Where. AppErrorEntry.OccurredAt is a DateTimeOffset and EF Core's
        // SQLite provider cannot translate DateTimeOffset comparisons — it throws at runtime, not
        // at compile time. The same limitation is why GameLogEvent and MarketTypeHistory store
        // their dates as strings outright. See TimestampCutoff for why the text compare is sound.
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "AppErrorLog" WHERE "OccurredAt" < {TimestampCutoff(days)}""", ct);
    }

    // ── Killmails ─────────────────────────────────────────────────────────────

    /// <summary>Whose killmails a rule covers.</summary>
    public enum KillmailScope
    {
        /// <summary>A kill or loss of one of your characters, or of a corporation you have added:
        /// victim or attacker, or a kill ESI or zKillboard handed to one of them as its own.</summary>
        Ours,

        /// <summary>Everything else.</summary>
        Others,
    }

    /// <summary>Killmails deleted per transaction. Small enough that SQLite's single writer is
    /// free again within a second or so, large enough that a run of millions is not dominated by
    /// round trips.</summary>
    private const int KillmailBatch = 2_000;

    public Task<int> PurgeOurKillmailsAsync(
        int days, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
        => PurgeKillmailsAsync(KillmailScope.Ours, Math.Max(OurKillmails.MinimumDays, days), progress, ct);

    public Task<int> PurgeOtherKillmailsAsync(
        int days, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
        => PurgeKillmailsAsync(KillmailScope.Others, Math.Max(OtherKillmails.MinimumDays, days), progress, ct);

    /// <summary>
    /// Removes one side's killmails older than the window, and everything hanging off them.
    ///
    /// <para>⚠️ This is the one place in the app that deletes killmails. Every import path is
    /// additive-only by rule — skip if already stored, never update or delete — because a killmail
    /// is immutable once written. That rule governs importing; this is the user deliberately
    /// choosing to stop keeping old ones, which is why it is off by default and gated behind a
    /// checkbox they have to tick.</para>
    ///
    /// <para>⚠️ Sorted in memory, not in SQL. Asked as one query — kills whose victim and attackers
    /// are NOT IN ours — PostgreSQL ran past five minutes on 2.45 million kills without finishing.
    /// Read this way it takes under a second on the same data: our kills come through the indexes
    /// that lead with a character or corporation id, the old kills through the one on time, and
    /// the rest is a set lookup.</para>
    ///
    /// <para>Deleted in batches, each in a transaction of its own and children first, so a kill is
    /// removed whole or not at all, SQLite's writer is never held for the length of a run that can
    /// reach millions of kills, and progress can be reported as it goes.</para>
    /// </summary>
    private async Task<int> PurgeKillmailsAsync(
        KillmailScope scope, int days, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // No timeout: KillMailItems alone runs to tens of millions of rows, and a partial delete
        // interrupted by a timeout is worse than a slow one.
        db.Database.SetCommandTimeout(0);

        var doomed = await KillmailsToPurgeAsync(db, scope, TimestampCutoff(days), ct).ConfigureAwait(false);
        progress?.Report((0, doomed.Count));

        var removed = 0;
        foreach (var batch in doomed.Chunk(KillmailBatch))
        {
            ct.ThrowIfCancellationRequested();

            // Our own ids, so embedded rather than parameterised: a list of ints carries nothing
            // but digits.
            var ids = string.Join(",", batch);

            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
#pragma warning disable EF1002 // interpolated parts are table names from this list and ints from our own table, never input
            foreach (var child in new[] { "KillMailItems", "KillMailAttackers", "EsiKillMailRefs", "ZkbKillFlags" })
                await db.Database.ExecuteSqlRawAsync(
                    $"""DELETE FROM "{child}" WHERE "KillMailId" IN ({ids})""", ct).ConfigureAwait(false);
            removed += await db.Database.ExecuteSqlRawAsync(
                $"""DELETE FROM "KillMailDetails" WHERE "KillMailId" IN ({ids})""", ct).ConfigureAwait(false);
#pragma warning restore EF1002
            await tx.CommitAsync(ct).ConfigureAwait(false);

            progress?.Report((removed, doomed.Count));
        }
        return removed;
    }

    /// <summary>The killmails on one side that are older than the cutoff.</summary>
    internal static async Task<List<int>> KillmailsToPurgeAsync(
        AppDbContext db, KillmailScope scope, DateTimeOffset cutoff, CancellationToken ct)
    {
        // ⚠️ Raw SQL for the date: a DateTimeOffset in a LINQ Where does not translate on SQLite.
        var old = await db.Database
            .SqlQueryRaw<int>("""SELECT "KillMailId" AS "Value" FROM "KillMailDetails" WHERE "KillMailTime" < {0}""", cutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        if (old.Count == 0) return old;

        var ours = await OurKillmailIdsAsync(db, ct).ConfigureAwait(false);
        return old.Where(id => ours.Contains(id) == (scope == KillmailScope.Ours)).ToList();
    }

    /// <summary>
    /// Every killmail that is one of ours: a character of yours or a corporation you have added,
    /// as victim or attacker — or a kill ESI or zKillboard handed to one of them as its own.
    ///
    /// <para>⚠️ Every corporation in the table, not only personal ones, and not only those whose
    /// token works today. A corporation is only ever added by signing in for it, so every row is
    /// one the user chose to track; a token the SSO later refuses is cleared, and keying on it
    /// would hand a corporation's whole history to the shorter rule the day its director's token
    /// lapsed. Characters the same way.</para>
    ///
    /// <para>The references are belt and braces. A kill handed to one of ours is victim- or
    /// attacker-side one of ours already; counting them as well means the corp activity pages,
    /// which read killmails through those references, can never lose one to the other rule.</para>
    /// </summary>
    internal static async Task<HashSet<int>> OurKillmailIdsAsync(AppDbContext db, CancellationToken ct)
    {
        var chars = await db.Characters.AsNoTracking().Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false);
        var corps = await db.Corporations.AsNoTracking().Select(c => (long)c.Id).ToListAsync(ct).ConfigureAwait(false);

        var ours = new HashSet<int>();
        if (chars.Count + corps.Count == 0) return ours;
        var owners = chars.Concat(corps).ToList();

        // Attackers through the two indexes that lead with the id asked for — never a scan of the
        // attackers table, which runs to millions of rows.
        if (chars.Count > 0)
            ours.UnionWith(await db.KillMailAttackers.AsNoTracking()
                .Where(a => a.CharacterId != null && chars.Contains(a.CharacterId.Value))
                .Select(a => a.KillMailId).ToListAsync(ct).ConfigureAwait(false));
        if (corps.Count > 0)
            ours.UnionWith(await db.KillMailAttackers.AsNoTracking()
                .Where(a => a.CorporationId != null && corps.Contains(a.CorporationId.Value))
                .Select(a => a.KillMailId).ToListAsync(ct).ConfigureAwait(false));

        // Victims: one row a kill, so a single pass.
        ours.UnionWith(await db.KillMailDetails.AsNoTracking()
            .Where(d => chars.Contains(d.VictimCharId) || corps.Contains(d.VictimCorpId))
            .Select(d => d.KillMailId).ToListAsync(ct).ConfigureAwait(false));

        ours.UnionWith(await db.EsiKillMailRefs.AsNoTracking()
            .Where(r => owners.Contains(r.OwnerId))
            .Select(r => r.KillMailId).ToListAsync(ct).ConfigureAwait(false));
        return ours;
    }

    /// <summary>
    /// The single killmail rule these two replaced applied to every kill alike. Whoever had it on
    /// keeps exactly that: both new rules start from its setting, window and last run, so nothing
    /// is kept longer or purged sooner than before until they choose otherwise.
    ///
    /// <para>Once either new rule has been written the old one is never read again — and it is
    /// left in place, so a build from before the split still finds the setting it knows.</para>
    /// </summary>
    private void CarryOverKillmailRule(AppPreferencesService prefs)
    {
        const string Old = "retention.killmails";
        if (prefs.Get("retention.killmails.ours.enabled") is not null
            || prefs.Get("retention.killmails.others.enabled") is not null
            || prefs.Get($"{Old}.enabled") is null) return;

        var days = (int)prefs.GetLong($"{Old}.days", 90);
        var last = prefs.Get($"{Old}.lastrun");
        foreach (var rule in new[] { OurKillmails, OtherKillmails })
        {
            rule.Days = days;
            if (DateTimeOffset.TryParse(last, out var t)) rule.LastRunUtc = t;
            rule.Enabled = prefs.GetBool($"{Old}.enabled");
        }
    }

    // ── Price history ─────────────────────────────────────────────────────────

    /// <summary>
    /// Removes market history and the derived per-type snapshots built from it.
    ///
    /// <para>Both together on purpose: the snapshots are computed from the same market data and
    /// drive the same charts, so keeping one without the other leaves a history that disagrees
    /// with itself.</para>
    ///
    /// <para>Both tables store their date as a "yyyy-MM-dd" string precisely because of the
    /// DateTimeOffset translation limitation noted above, so here the comparison is a plain and
    /// exact text compare.</para>
    /// </summary>
    public async Task<int> PurgePriceHistoryAsync(int days, CancellationToken ct = default)
    {
        days = Math.Max(PriceHistory.MinimumDays, days);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(0);

        var history = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "MarketTypeHistories" WHERE "Date" < {cutoff}""", ct);
        var snapshots = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "TypePriceSnapshots" WHERE "Date" < {cutoff}""", ct);

        return history + snapshots;
    }


    // ── Game log ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes parsed game-log events. The source .log files on disk are untouched, so anything
    /// purged here can be re-imported with Settings → Game Logs → Import Past Logs for as long as
    /// the files themselves survive.
    /// </summary>
    public async Task<int> PurgeGameLogAsync(int days, CancellationToken ct = default)
    {
        days = Math.Max(GameLog.MinimumDays, days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(0);

        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "GameLogEvents" WHERE "OccurredAt" < {IsoCutoff(days)}""", ct);
    }

    // ── Chat messages ─────────────────────────────────────────────────────────

    /// <summary>
    /// Removes stored chat messages.
    ///
    /// <para>⚠️ Intel reports parsed from those messages are KEPT, but only because two other
    /// places were taught about this purge. IntelReport carries a ChatMessageId for provenance,
    /// and startup used to delete every report whose message had vanished — on the explicit
    /// grounds that "nothing purges chat messages on age". This does. That sweep is now bounded to
    /// orphans newer than the oldest surviving message, and IntelService.BackfillAsync likewise
    /// rebuilds only the period it can actually reproduce. Change either of those back and this
    /// purge starts destroying intel history on the next launch.</para>
    ///
    /// <para>The parser's own position is unaffected: it resumes from IntelWatermark, a message id
    /// held in preferences rather than derived from the table, so deleting older rows cannot
    /// rewind it. The .log files on disk are untouched, so a re-import remains possible while they
    /// exist.</para>
    /// </summary>
    public async Task<int> PurgeChatMessagesAsync(int days, CancellationToken ct = default)
    {
        days = Math.Max(ChatMessages.MinimumDays, days);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Database.SetCommandTimeout(0);

        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "ChatMessages" WHERE "OccurredAt" < {IsoCutoff(days)}""", ct);
    }
    // ── Scheduled sweep ───────────────────────────────────────────────────────

    /// <summary>How much data is allowed to accumulate before a rule runs again.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>
    /// How often the loop asks whether anything is due. Not the retention period — a rule that
    /// came due while the app was closed must run shortly after launch rather than at the next
    /// whole-day boundary, and a rule the user has just enabled should act without a long wait.
    /// Comparing five timestamps costs nothing, so the check can be frequent even though the work
    /// is daily.
    /// </summary>
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(15);

    private Task? _loop;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Starts the background sweep. Each rule runs when its own 24 hours are up — measured from
    /// when it last purged, not from launch — so leaving the app running for a week still trims
    /// daily, and restarting it three times in an hour does not re-run anything.
    /// </summary>
    public void Start(CancellationToken outerCt = default)
    {
        if (_loop is not null) return;

        // ⚠️ Linked to a source of our own. Every caller leaves the parameter at its default,
        // so until now nothing could stop this loop once started — and the lease has to be
        // able to, the moment this client stops being the worker.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                await PurgeDueAsync(ct);
                try { await Task.Delay(CheckEvery, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    /// <summary>
    /// Stops the retention sweep, and leaves it startable again.
    ///
    /// <para>⚠️ Both fields cleared. _loop is what Start guards on, and a
    /// CancellationTokenSource stays cancelled once it has been — keeping either would make
    /// the next Start a silent no-op for the rest of the session.</para>
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }

        _cts.Dispose();
        _cts  = null;
        _loop = null;
    }

    /// <summary>Runs every enabled rule whose interval has elapsed, and stamps each one.</summary>
    public async Task PurgeDueAsync(CancellationToken ct = default)
    {
        await RunIfDue(ErrorLog,       PurgeErrorLogAsync);
        await RunIfDue(OurKillmails,   (d, c) => PurgeOurKillmailsAsync(d, null, c));
        await RunIfDue(OtherKillmails, (d, c) => PurgeOtherKillmailsAsync(d, null, c));
        await RunIfDue(PriceHistory,   PurgePriceHistoryAsync);
        await RunIfDue(GameLog,      PurgeGameLogAsync);
        await RunIfDue(ChatMessages, PurgeChatMessagesAsync);
        await RunIfDue(AgentTelemetry, PurgeAgentTelemetryAsync);

        async Task RunIfDue(RetentionRule rule, Func<int, CancellationToken, Task<int>> purge)
        {
            if (!rule.IsDue(Interval)) return;
            try
            {
                await purge(rule.Days, ct);
                rule.MarkRun();
            }
            catch
            {
                // Housekeeping must never take the app down with it. Deliberately NOT stamped on
                // failure, so a rule that could not run stays due and is retried on the next
                // check rather than being skipped for a day.
            }
        }
    }

    /// <summary>
    /// A cutoff formatted to match how EF writes a DateTimeOffset to SQLite:
    /// "yyyy-MM-dd HH:mm:ss.fffffff+00:00". That text sorts lexicographically, so a shorter
    /// "yyyy-MM-dd HH:mm:ss" cutoff compares correctly against it — the shared prefix decides the
    /// order before the differing precision matters. Verified against SQLite's own
    /// <c>datetime('now', '-N days')</c> across several windows: identical counts every time.
    ///
    /// <para>Sound only because every row is written with <c>DateTimeOffset.UtcNow</c>, so the
    /// stored offsets are uniformly +00:00. A mixed-offset column could not be compared this way.
    /// </para>
    /// </summary>
    // ⚠️ The value, not a rendering of it. These columns are real DateTimeOffsets, which are
    // TEXT on SQLite but timestamptz on a server, where text will not compare against one.
    // IsoCutoff below is deliberately still a string: the columns IT serves really are text.
    private static DateTimeOffset TimestampCutoff(int days)
        => DateTimeOffset.UtcNow.AddDays(-days);

    /// <summary>
    /// A cutoff for the app's own ISO-8601 string columns — ChatMessage and GameLogEvent store
    /// "yyyy-MM-ddTHH:mm:ssZ", which is a different shape from how EF writes a DateTimeOffset.
    /// ⚠️ Using the wrong one of these two formats does not error: "2026-08-19 04:00:00" never
    /// compares greater than "2025-08-01T04:07:02Z", so the purge would silently delete nothing.
    /// </summary>
    private static string IsoCutoff(int days)
        => DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
}
