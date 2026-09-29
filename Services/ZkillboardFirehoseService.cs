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
/// interval for: the stream paces itself — one request at a time at the live edge, 7s
/// after a 404 as zKillboard asks, batches only while catching up, and nothing at all
/// while R2Z2 is refusing this machine (see the R2Z2 gate in ZkillboardApiClient).
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
    // ⚠️ R2Z2's limit is 15 requests a second per IP, shared by every copy of the app on the
    // machine. This once sent ten at a time on every pass, even at the live edge where nine
    // of them 404; a release and a dev build together went over and were refused for hours.
    //
    // At the live edge: one request at a time, as zKillboard's own client does. Catching up:
    // a batch of R2Z2PerSecond — the most the API client's gate lets start in a second, the
    // gate spacing the batches. About 14,000 kills an hour, 24 times the stream's pace.
    private const int BatchSize        = ZkillboardApiClient.R2Z2PerSecond;
    private const int NoNewBackoffSecs = 7;    // documented minimum is 6s after a 404
    private const int IdleTickSecs     = 15;   // how often we re-check Enabled/Scope while idle
    private const int LongestRetrySecs = 120;  // failures back off 7s, 14s, 28s … up to this

    // A sequence that stays empty while the head moves well past it is a hole in the
    // stream, not the live edge. Without this the cursor would sit on it forever.
    private const int StallsBeforeSkip = 5;
    private const int HoleMargin       = 10;

    private int  _consecutiveStalls;
    private int  _failuresInARow;
    private bool _batching;      // behind the live edge: a batch at a time
    private int  _foundInARow;   // one at a time, and each found something

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
        // R2Z2 is refusing this machine: ask it nothing until the pause is over, a slice at
        // a time so a change of setting still gets noticed, then start again with one request.
        if (api.R2Z2PausedUntil is { } until)
        {
            StatusText   = $"zKillboard firehose: zKillboard is limiting this machine's requests — next try at {until.ToLocalTime():t}";
            _batching    = false;
            _foundInARow = 0;

            var wait = until - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait < TimeSpan.FromSeconds(IdleTickSecs) ? wait : TimeSpan.FromSeconds(IdleTickSecs), ct);
            return;
        }

        if (_cursor is null)
        {
            _cursor = await SeedCursorAsync(ct);
            if (_cursor is null)
            {
                // A refusal is reported, and waited out, at the top of the next pass.
                if (api.R2Z2PausedUntil is null) await RetryAfterFailureAsync(ct);
                return;
            }
            _batching = true; // most likely behind; the first short batch will say otherwise
        }

        var start = _cursor.Value;
        var size  = _batching ? BatchSize : 1;

        // A batch goes out together; only the contiguous run found from the start is taken,
        // so a hole never lets us silently skip past unread entries — the next pass
        // re-requests from the hole.
        var entries = await Task.WhenAll(
            Enumerable.Range(0, size).Select(i => api.GetEphemeralAsync(start + i, ct)));

        var take = 0;
        while (take < entries.Length && entries[take].Answer == ZkillboardApiClient.R2Z2Answer.Found) take++;

        if (take == 0)
        {
            _batching    = false; // at the edge, or starting again after a refusal or failure
            _foundInARow = 0;
            switch (entries[0].Answer)
            {
                case ZkillboardApiClient.R2Z2Answer.Refused: return; // waited out at the top of the next pass
                case ZkillboardApiClient.R2Z2Answer.Failed:  await RetryAfterFailureAsync(ct); return;
                default:                                     await HandleNothingAtCursorAsync(start, ct); return;
            }
        }

        _consecutiveStalls = 0;
        _failuresInARow    = 0;

        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            // Once per batch rather than once per killmail — during a long catch-up the
            // per-kill version was two extra queries for every single kill imported.
            var (charIds, corpIds) = await ZkillboardKillImportService.GetTrackedIdsAsync(db, ct);

            for (var i = 0; i < take; i++)
                await importer.ImportAsync(db, entries[i].Kill!.Kill, entries[i].Kill!.Hash, charIds, corpIds, ct);

            await db.SaveChangesAsync(ct);
        }

        _cursor = start + take;
        settings.SaveR2Z2Position(start + take - 1);
        ImportedThisSession += take;
        StatusText = $"zKillboard firehose: sequence {_cursor:N0} — {ImportedThisSession:N0} imported this session";

        // A full batch has more behind it, and a short one reached the edge. One at a time,
        // a batch's worth found back to back means a backlog has built up again.
        if (_batching)
            _batching = take == size;
        else if (++_foundInARow >= BatchSize)
            (_batching, _foundInARow) = (true, 0);
    }

    /// <summary>R2Z2 could not be reached, or answered with an error: try again later, and
    /// later each time it happens again.</summary>
    private async Task RetryAfterFailureAsync(CancellationToken ct)
    {
        _failuresInARow++;
        var secs = Math.Min(LongestRetrySecs, NoNewBackoffSecs << Math.Min(_failuresInARow - 1, 5));
        StatusText = $"zKillboard firehose: could not reach zKillboard — trying again in {secs}s";
        await Task.Delay(TimeSpan.FromSeconds(secs), ct);
    }

    /// <summary>Nothing at the cursor: normally just the live edge, so back off and retry.
    /// But if the stream head has moved well past this position and it is still empty
    /// after several attempts, it is a hole rather than the edge — step over it, otherwise
    /// the cursor parks there permanently and live capture silently stops.</summary>
    private async Task HandleNothingAtCursorAsync(long cursor, CancellationToken ct)
    {
        _failuresInARow = 0;

        // Every StallsBeforeSkip-th empty pass asks where the head is — not every pass after it.
        if (++_consecutiveStalls >= StallsBeforeSkip)
        {
            _consecutiveStalls = 0;
            var head = await api.GetSequenceAsync(ct);
            if (api.R2Z2PausedUntil is not null) return; // refused; waited out at the top of the next pass

            if (head is not null && head.Value > cursor + HoleMargin)
            {
                _cursor = cursor + 1;
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
