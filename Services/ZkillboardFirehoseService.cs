using EveConsole.Data;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Live "All kills" capture via zKillboard's R2Z2 ephemeral stream — active only while
/// <see cref="ZkillboardSettings.Scope"/> is <see cref="ZkbScope.All"/>
/// (ZkillboardPollingService covers "Mine + Corp" scope instead).
///
/// R2Z2 has no filter — every killmail in New Eden passes through it — so this is the
/// only mechanism that can satisfy "All kills" live, at the cost of pulling far more
/// volume than the targeted per-character/corp poll. There is nothing to configure an
/// interval for: the stream paces itself (~10 req/s while catching up, 6s+ backoff once
/// caught up, per zKillboard's documented etiquette).
///
/// Additive-only via ZkillboardKillImportService — every import checks for an existing
/// row first.
/// </summary>
public sealed class ZkillboardFirehoseService(
    IServiceScopeFactory        scopeFactory,
    ZkillboardSettings          settings,
    ZkillboardApiClient         api,
    ZkillboardKillImportService importer,
    ZkillboardBackfillService   backfill,
    AppErrorLogger              errorLogger) : ReactiveObject
{
    // Catch-up fetches run concurrently: the limit is round-trip latency (~310ms measured),
    // not zKillboard's rate cap, so one-at-a-time throughput was ~2.4/s — roughly 12x
    // realtime, or 5 minutes of replay per hour of downtime. A batch of 10 in flight lands
    // near 12/s, comfortably under R2Z2's documented 15 req/s ceiling, and BatchPaceMs
    // holds the batch cycle long enough that a fast run cannot overshoot it.
    private const int BatchSize        = 10;
    private const int BatchPaceMs      = 850;  // >= BatchSize/15s, so <= ~12 req/s sustained
    private const int NoNewBackoffSecs = 7;    // documented minimum is 6s after a 404
    private const int IdleTickSecs     = 15;   // how often we re-check Enabled/Scope while idle

    // A sequence that stays empty while the head moves well past it is a hole in the
    // stream, not the live edge. Without this the cursor would sit on it forever.
    private const int StallsBeforeSkip = 5;

    private int _consecutiveStalls;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;

    // In-memory active cursor. Seeded from ZkillboardSettings on first use each run;
    // advanced in memory thereafter and only persisted after each successful import so a
    // crash mid-stream re-processes at most one killmail (harmless — additive/skip-if-exists).
    private long? _cursor;

    private string _statusText = "zKillboard firehose: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private long _importedThisSession;
    public long ImportedThisSession
    {
        get => _importedThisSession;
        private set => this.RaiseAndSetIfChanged(ref _importedThisSession, value);
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }

        _cts     = null;
        _runTask = null;
        StatusText = "zKillboard firehose: stopped";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (settings.Enabled && settings.Scope == ZkbScope.All)
            {
                try
                {
                    await ConsumeOnceAsync(ct);
                    continue; // pacing delay is chosen inside ConsumeOnceAsync per outcome
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    StatusText = $"zKillboard firehose: error — {Truncate(ex.Message)}";
                    errorLogger.Log(nameof(ZkillboardFirehoseService), nameof(RunAsync), ex);
                }
            }
            else
            {
                _cursor = null; // re-seed from "now" next time All scope is activated
                StatusText = !settings.Enabled
                    ? "zKillboard firehose: disabled"
                    : "zKillboard firehose: idle (Mine+Corp scope uses the interval poll instead)";
            }

            await Task.Delay(TimeSpan.FromSeconds(IdleTickSecs), ct);
        }
    }

    private async Task ConsumeOnceAsync(CancellationToken ct)
    {
        if (_cursor is null)
        {
            _cursor = await SeedCursorAsync(ct);
            if (_cursor is null)
            {
                StatusText = "zKillboard firehose: could not reach zKillboard, retrying";
                await Task.Delay(TimeSpan.FromSeconds(NoNewBackoffSecs), ct);
                return;
            }
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        var start   = _cursor.Value;

        // Whole batch in flight at once; only the contiguous run of successes from the
        // start is consumed, so a hole never lets us silently skip past unread entries —
        // the next cycle re-requests from the hole.
        var results = await Task.WhenAll(
            Enumerable.Range(0, BatchSize).Select(i => api.GetEphemeralAsync(start + i, ct)));

        var take = 0;
        while (take < results.Length && results[take] is not null) take++;

        if (take == 0)
        {
            await HandleNothingAtCursorAsync(start, ct);
            return;
        }

        _consecutiveStalls = 0;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            // Once per batch rather than once per killmail — during a long catch-up the
            // per-kill version was two extra queries for every single kill imported.
            var (charIds, corpIds) = await ZkillboardKillImportService.GetTrackedIdsAsync(db, ct);

            for (var i = 0; i < take; i++)
                await importer.ImportAsync(db, results[i]!.Kill, results[i]!.Hash, charIds, corpIds, ct);

            await db.SaveChangesAsync(ct);
        }

        _cursor = start + take;
        settings.SaveR2Z2Position(start + take - 1);
        ImportedThisSession += take;
        StatusText = $"zKillboard firehose: sequence {_cursor:N0} — {ImportedThisSession:N0} imported this session";

        // Only pace when the batch came back full — a short batch means we reached the
        // live edge, and HandleNothingAtCursorAsync's backoff covers that on the next pass.
        if (take == BatchSize)
        {
            var remaining = BatchPaceMs - (int)started.ElapsedMilliseconds;
            if (remaining > 0) await Task.Delay(remaining, ct);
        }
    }

    /// <summary>Nothing at the cursor: normally just the live edge, so back off and retry.
    /// But if the stream head has moved well past this position and it is still empty
    /// after several attempts, it is a hole rather than the edge — step over it, otherwise
    /// the cursor parks there permanently and live capture silently stops.</summary>
    private async Task HandleNothingAtCursorAsync(long cursor, CancellationToken ct)
    {
        _consecutiveStalls++;

        if (_consecutiveStalls >= StallsBeforeSkip)
        {
            var head = await api.GetSequenceAsync(ct);
            if (head is not null && head.Value > cursor + BatchSize)
            {
                _cursor = cursor + 1;
                _consecutiveStalls = 0;
                StatusText = $"zKillboard firehose: skipped empty sequence {cursor:N0} (head {head:N0})";
                errorLogger.Log(nameof(ZkillboardFirehoseService), nameof(HandleNothingAtCursorAsync),
                    new InvalidOperationException($"sequence {cursor} stayed empty while head reached {head}; skipping"));
                return;
            }
        }

        StatusText = $"zKillboard firehose: caught up (sequence {cursor:N0})";
        await Task.Delay(TimeSpan.FromSeconds(NoNewBackoffSecs), ct);
    }

    /// <summary>
    /// Where to (re)start the stream. The daily dumps import a whole day in about two
    /// seconds, versus hours of per-killmail replay for the same span, so the firehose
    /// should never re-read a day a dump already covers: start at the first day the dumps
    /// have NOT covered, and let ZkillboardBackfillService own everything before that.
    ///
    /// The saved cursor still wins when it is further ahead — that is the ordinary
    /// short-downtime case, where no dump exists for the missed window at all and replay
    /// is the only way to get those kills.
    /// </summary>
    private async Task<long?> SeedCursorAsync(CancellationToken ct)
    {
        // LastFullDay is only trustworthy once the startup gap-fill has run; seeding
        // against a stale value would replay days it is about to import in seconds.
        StatusText = "zKillboard firehose: waiting for daily backfill to settle";
        await backfill.InitialGapFillCompleted.WaitAsync(ct);

        var resume = settings.R2Z2LastSequence > 0 ? settings.R2Z2LastSequence + 1 : (long?)null;

        // First day the dumps have not accounted for. Null when no day has ever been
        // imported, in which case there is nothing to skip past.
        if (settings.LastFullDay is { } lastFull)
        {
            var firstUncovered = lastFull.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var seek = await api.FindSequenceAtAsync(new DateTimeOffset(firstUncovered), ct);

            if (seek is not null && (resume is null || seek.Value > resume.Value))
            {
                StatusText = $"zKillboard firehose: resuming at {firstUncovered:yyyy-MM-dd} (sequence {seek:N0}); earlier days come from daily dumps";
                return seek;
            }
        }

        if (resume is not null)
        {
            // A saved cursor from before the retention window points at sequences that no
            // longer exist. Left alone it would 404 forever and the stall-skip would
            // advance it one at a time through however many expired entries there are, so
            // pull it up to the oldest sequence R2Z2 still serves.
            var oldestRetained = await api.FindSequenceAtAsync(DateTimeOffset.UnixEpoch, ct);
            return oldestRetained is not null && resume.Value < oldestRetained.Value
                ? oldestRetained
                : resume;
        }

        // Nothing to resume from and no dump history — start live.
        var current = await api.GetSequenceAsync(ct);
        if (current is null) return null;

        settings.SaveR2Z2Position(current.Value);
        return current.Value;
    }

    private static string Truncate(string s, int max = 80) => s.Length <= max ? s : s[..max];
}
