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
        Killmails    = new RetentionRule(prefs, "retention.killmails",    defaultDays: 90,  minimumDays: 30);
        PriceHistory = new RetentionRule(prefs, "retention.pricehistory", defaultDays: 90,  minimumDays: 30);
        GameLog      = new RetentionRule(prefs, "retention.gamelog",      defaultDays: 365, minimumDays: 30);
        ChatMessages = new RetentionRule(prefs, "retention.chat",         defaultDays: 90,  minimumDays: 30);
    }

    public RetentionRule ErrorLog     { get; }
    public RetentionRule Killmails    { get; }
    public RetentionRule PriceHistory { get; }
    public RetentionRule GameLog      { get; }
    public RetentionRule ChatMessages { get; }

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

    /// <summary>
    /// Removes killmails older than the window, and everything hanging off them.
    ///
    /// <para>⚠️ This is the one place in the app that deletes killmails. Every import path is
    /// additive-only by rule — skip if already stored, never update or delete — because a killmail
    /// is immutable once written. That rule governs importing; this is the user deliberately
    /// choosing to stop keeping old ones, which is why it is off by default and gated behind a
    /// checkbox they have to tick.</para>
    ///
    /// <para>Children go first, so an interruption leaves orphaned children (harmless, and cleaned
    /// up by the next run) rather than details with no attackers — which would render as corrupt
    /// kills in the browser.</para>
    /// </summary>
    public async Task<int> PurgeKillmailsAsync(int days, CancellationToken ct = default)
    {
        days = Math.Max(Killmails.MinimumDays, days);
        var cutoff = TimestampCutoff(days);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // No timeout: KillMailItems alone runs to tens of millions of rows, and a partial delete
        // interrupted by a timeout is worse than a slow one.
        db.Database.SetCommandTimeout(0);

        const string Doomed = """
            SELECT "KillMailId" FROM "KillMailDetails" WHERE "KillMailTime" < {0}
            """;

        await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "KillMailItems"     WHERE "KillMailId" IN ({Doomed})""", [cutoff], ct);
        await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "KillMailAttackers" WHERE "KillMailId" IN ({Doomed})""", [cutoff], ct);
        await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "EsiKillMailRefs"   WHERE "KillMailId" IN ({Doomed})""", [cutoff], ct);
        await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "ZkbKillFlags"      WHERE "KillMailId" IN ({Doomed})""", [cutoff], ct);

        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"""DELETE FROM "KillMailDetails" WHERE "KillMailTime" < {cutoff}""", ct);
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

    /// <summary>
    /// Starts the background sweep. Each rule runs when its own 24 hours are up — measured from
    /// when it last purged, not from launch — so leaving the app running for a week still trims
    /// daily, and restarting it three times in an hour does not re-run anything.
    /// </summary>
    public void Start(CancellationToken ct = default)
    {
        if (_loop is not null) return;

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

    /// <summary>Runs every enabled rule whose interval has elapsed, and stamps each one.</summary>
    public async Task PurgeDueAsync(CancellationToken ct = default)
    {
        await RunIfDue(ErrorLog,     PurgeErrorLogAsync);
        await RunIfDue(Killmails,    PurgeKillmailsAsync);
        await RunIfDue(PriceHistory, PurgePriceHistoryAsync);
        await RunIfDue(GameLog,      PurgeGameLogAsync);
        await RunIfDue(ChatMessages, PurgeChatMessagesAsync);

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
    private static string TimestampCutoff(int days)
        => DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// A cutoff for the app's own ISO-8601 string columns — ChatMessage and GameLogEvent store
    /// "yyyy-MM-ddTHH:mm:ssZ", which is a different shape from how EF writes a DateTimeOffset.
    /// ⚠️ Using the wrong one of these two formats does not error: "2026-08-19 04:00:00" never
    /// compares greater than "2025-08-01T04:07:02Z", so the purge would silently delete nothing.
    /// </summary>
    private static string IsoCutoff(int days)
        => DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
}
