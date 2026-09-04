using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Daily-dump backfill for the zKillboard integration — two entry points sharing the
/// same underlying day-import step:
///
///   • Manual (<see cref="BackfillAsync"/>) — explicit, user-initiated, walks back N
///     days. Typically run once after first enabling the feature, to pull in history
///     from before tracking started.
///   • Automatic gap-fill (<see cref="RunGapFillAsync"/>) — runs on its own hourly
///     check (via Start/StopAsync, same lifecycle shape as GameLogImportService):
///     if the last fully-imported day is more than one day behind today, it walks
///     forward and imports each missing day, so a period the app was closed doesn't
///     leave a hole in Mine+Corp coverage once it's running again.
///
/// Both filter to the tracked character/corp set when Scope = MineAndCorp, and import
/// unfiltered when Scope = All. Additive-only via ZkillboardKillImportService.
///
/// Performance: one DbContext (and one preloaded ZkillboardKillImportService.KnownIds)
/// is reused across the WHOLE multi-day run rather than per day, so existence checks
/// are in-memory HashSet lookups instead of a DB round trip per killmail — the original
/// per-call AnyAsync version was the dominant cost for any real backfill. Mine+Corp
/// scope also filters each day's dump before deserializing (see
/// ZkillboardApiClient.GetDailyDumpAsync) rather than after, so kills that don't involve
/// a tracked owner never get their attacker/item lists materialized at all.
/// </summary>
public sealed class ZkillboardBackfillService(
    IServiceScopeFactory        scopeFactory,
    ZkillboardSettings          settings,
    ZkillboardApiClient         api,
    ZkillboardKillImportService importer,
    AppErrorLogger              errorLogger) : ReactiveObject
{
    // Kills per SaveChangesAsync flush, not rows — each kill also brings its attackers
    // and items along (~20-25 rows/kill on a typical day). Larger now that
    // AutoDetectChangesEnabled=false removes the main cost of holding more at once:
    // fewer transaction commits for the same total work.
    private const int SaveBatchSize   = 2000;
    private const int GapCheckHours   = 1;

    private CancellationTokenSource? _cts;        // service lifecycle (Start/StopAsync)
    private Task?                    _runTask;
    private CancellationTokenSource? _importCts;  // whichever import (manual or gap-fill) is active

    private readonly TaskCompletionSource _initialPass = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the startup gap-fill pass has finished (or failed, or been
    /// skipped because the feature is off). ZkillboardFirehoseService waits on this before
    /// choosing where to resume: LastFullDay is what tells it which days the dumps already
    /// cover, and seeding against a not-yet-updated value would make it replay days this
    /// pass is about to import far more cheaply.</summary>
    public Task InitialGapFillCompleted => _initialPass.Task;

    private string _statusText = "zKillboard backfill: not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        private set => this.RaiseAndSetIfChanged(ref _isImporting, value);
    }

    private int _progressCurrent;
    public int ProgressCurrent
    {
        get => _progressCurrent;
        private set => this.RaiseAndSetIfChanged(ref _progressCurrent, value);
    }

    private int _progressTotal = 1;
    public int ProgressTotal
    {
        get => _progressTotal;
        private set => this.RaiseAndSetIfChanged(ref _progressTotal, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    // ── Lifecycle — drives the automatic gap-fill only; manual backfill is called
    //    directly from the settings ViewModel regardless of Start/StopAsync state ──

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _importCts?.Cancel();
        if (_cts is null) return;

        await _cts.CancelAsync();
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }

        _cts     = null;
        _runTask = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (settings.Enabled && !IsImporting)
                {
                    try
                    {
                        await RunGapFillAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        errorLogger.Log(nameof(ZkillboardBackfillService), nameof(RunAsync), ex);
                    }
                }

                // Signalled on every path, including disabled and failed — a waiter must
                // never hang just because the pass did nothing.
                _initialPass.TrySetResult();

                await Task.Delay(TimeSpan.FromHours(GapCheckHours), ct);
            }
        }
        finally
        {
            _initialPass.TrySetResult();
        }
    }

    // ── Manual backfill ──────────────────────────────────────────────────────

    public void CancelImport() => _importCts?.Cancel();

    /// <summary>Walk back <paramref name="days"/> calendar days (oldest first) and
    /// import each day's dump. Explicit and user-initiated.</summary>
    public async Task BackfillAsync(int days)
    {
        if (IsImporting) return;

        _importCts = new CancellationTokenSource();
        var ct     = _importCts.Token;

        IsImporting     = true;
        ProgressCurrent = 0;
        ProgressTotal   = Math.Max(1, days);
        ProgressText    = "Starting…";

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PreparePragmasAsync(db, ct);
            // This context only ever Adds new entities during a backfill run — it never
            // mutates an already-tracked one — so DetectChanges (which EF runs before
            // every SaveChangesAsync by default, scanning every currently-tracked entity)
            // has nothing to find. With tens of thousands of kills a day, each pulling in
            // several attackers/items, that scan was the dominant cost: measured parse +
            // deserialize for a ~14K-kill day at ~0.5s total, so the reported 20-30s/day
            // was almost entirely EF's per-SaveChanges change detection, not JSON or I/O.
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            var (charIds, corpIds) = await ZkillboardKillImportService.GetTrackedIdsAsync(db, ct);
            var known = await ZkillboardKillImportService.KnownIds.LoadAsync(db, ct);

            var today     = DateOnly.FromDateTime(DateTime.UtcNow);
            var startDate = today.AddDays(-days);
            var imported  = 0;

            for (var i = 0; i < days; i++)
            {
                if (ct.IsCancellationRequested) break;

                var date = startDate.AddDays(i);
                ProgressCurrent = i + 1;
                ProgressText    = $"Day {i + 1:N0} of {days:N0} — {date:yyyy-MM-dd}";

                imported += await ImportDayAsync(db, date, charIds, corpIds, known, ct);
            }

            StatusText = ct.IsCancellationRequested
                ? $"zKillboard backfill: cancelled after {ProgressCurrent:N0} day(s), {imported:N0} kill(s)"
                : $"zKillboard backfill: imported {imported:N0} kill(s) across {days:N0} day(s)";
            ProgressText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText   = $"zKillboard backfill: failed — {Truncate(ex.Message)}";
            ProgressText = StatusText;
            errorLogger.Log(nameof(ZkillboardBackfillService), nameof(BackfillAsync), ex);
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    // ── Automatic gap-fill ───────────────────────────────────────────────────

    /// <summary>If the last fully-imported day is more than one day behind today
    /// (UTC), walks forward importing each missing day. No-op if already caught up,
    /// the feature is off, or a manual backfill is currently running.</summary>
    public async Task RunGapFillAsync(CancellationToken ct = default)
    {
        if (!settings.Enabled || IsImporting) return;

        var today    = DateOnly.FromDateTime(DateTime.UtcNow);
        var lastFull = settings.LastFullDay;

        // First run ever — nothing to catch up on; seed the watermark to yesterday so
        // future gaps are measured from here rather than from the dawn of the API.
        if (lastFull is null)
        {
            settings.LastFullDay = today.AddDays(-1);
            return;
        }

        var lastComplete = today.AddDays(-1);
        if (lastFull.Value >= lastComplete) return; // already current

        _importCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _importCts.Token);
        var localCt = linked.Token;

        IsImporting = true;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await PreparePragmasAsync(db, localCt);
            db.ChangeTracker.AutoDetectChangesEnabled = false; // see BackfillAsync remarks

            var (charIds, corpIds) = await ZkillboardKillImportService.GetTrackedIdsAsync(db, localCt);
            var known = await ZkillboardKillImportService.KnownIds.LoadAsync(db, localCt);

            var day       = lastFull.Value.AddDays(1);
            var totalDays = lastComplete.DayNumber - day.DayNumber + 1;

            ProgressTotal   = Math.Max(1, totalDays);
            ProgressCurrent = 0;
            var imported    = 0;

            while (day <= lastComplete && !localCt.IsCancellationRequested)
            {
                ProgressCurrent++;
                ProgressText = $"Gap-fill {ProgressCurrent:N0} of {totalDays:N0} — {day:yyyy-MM-dd}";
                imported += await ImportDayAsync(db, day, charIds, corpIds, known, localCt);
                day = day.AddDays(1);
            }

            StatusText = localCt.IsCancellationRequested
                ? $"zKillboard gap-fill: cancelled, caught up through {settings.LastFullDay:yyyy-MM-dd}"
                : $"zKillboard gap-fill: caught up through {settings.LastFullDay:yyyy-MM-dd}, {imported:N0} kill(s)";
            ProgressText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText = $"zKillboard gap-fill: failed — {Truncate(ex.Message)}";
            errorLogger.Log(nameof(ZkillboardBackfillService), nameof(RunGapFillAsync), ex);
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    // ── Shared day-import step ───────────────────────────────────────────────

    /// <summary>Same PRAGMAs HoboImportService/SdeImportService use for their own bulk
    /// imports — journal_mode=WAL/busy_timeout are already set globally by
    /// DisableForeignKeysInterceptor on every connection, but synchronous=NORMAL/
    /// cache_size/temp_store are not, and matter far more once inserts are batched
    /// instead of one row (and one AnyAsync check) at a time.</summary>
    private static async Task PreparePragmasAsync(AppDbContext db, CancellationToken ct)
    {
        await AppDb.TuneForBulkImportAsync(db.Database, ct);
    }

    private async Task<int> ImportDayAsync(
        AppDbContext db, DateOnly date,
        IReadOnlySet<long> charIds, IReadOnlySet<long> corpIds,
        ZkillboardKillImportService.KnownIds known,
        CancellationToken ct)
    {
        var unfiltered = settings.Scope == ZkbScope.All;

        var imported   = 0;
        var sinceFlush = 0;
        var status     = new ZkillboardApiClient.DumpStatus();

        var dump = unfiltered
            ? api.GetDailyDumpAsync(date, status: status, ct: ct)
            : api.GetDailyDumpAsync(date, charIds, corpIds, status, ct);

        await foreach (var kill in dump)
        {
            if (ct.IsCancellationRequested) break;

            if (await importer.ImportAsync(db, kill.Kill, kill.Hash, charIds, corpIds, ct, known))
                imported++;
            sinceFlush++;

            if (sinceFlush >= SaveBatchSize)
            {
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
                sinceFlush = 0;
            }
        }

        if (sinceFlush > 0) await db.SaveChangesAsync(ct);

        // Only a day whose dump actually existed counts as fully imported. Advancing the
        // watermark past a day zKillboard had not published yet marked it permanently
        // done, so the gap-fill never came back for it once the dump appeared — leaving a
        // silent, unrecoverable hole for the most recent day of every backfill.
        // Advance-only otherwise: a manual backfill over older history must never move
        // the watermark backward.
        if (!ct.IsCancellationRequested && status.Available
            && (settings.LastFullDay is null || date > settings.LastFullDay))
            settings.LastFullDay = date;

        return imported;
    }

    private static string Truncate(string s, int max = 80) => s.Length <= max ? s : s[..max];
}
