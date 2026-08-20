using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Helps drain the write-ahead log, without ever blocking anyone to do it.
///
/// <para><b>⚠️ PASSIVE, and TRUNCATE only once the log is already empty.</b> This service first
/// shipped running TRUNCATE whenever the file passed 64 MB, with SQLite's automatic checkpoint
/// turned off so that no ordinary write would pay for one. That was wrong in both halves and the
/// numbers were emphatic: TRUNCATE
/// blocks writers and cannot complete while any reader holds a snapshot, so it timed out at thirty
/// seconds — three times a minute — while the log grew from 213 MB to over a gigabyte, and each
/// checkpoint finished with MORE in the log than it started with, because writers kept appending
/// throughout. Taking the automatic checkpoint off had removed the only thing draining the log
/// incrementally.</para>
///
/// <para>PASSIVE is the opposite: it copies what it can and gives up the instant it meets a
/// reader, so it can run often and costs nothing when there is nothing to do. It cannot shrink the
/// FILE — only the automatic checkpoint's own bookkeeping reuses that space — but keeping the log
/// SMALL is what matters, because the inline checkpoint's cost is proportional to how much is in
/// it. This is help, not a replacement.</para>
///
/// <para>What actually inflates the log is bulk work: see the chunked delete in
/// <c>MarketPricingService.UpsertRawOrdersAsync</c>, and the intel backfill's own checkpoints.
/// A log that keeps growing despite this is telling you a reader is holding a snapshot open for
/// minutes, which is a bug to find rather than a checkpoint to force.</para>
/// </summary>
public class WalCheckpointService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
{
    /// <summary>Often, because a PASSIVE checkpoint that has nothing to do is nearly free.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(20);

    /// <summary>A PASSIVE checkpoint should never be slow — it yields rather than waits. If one is,
    /// the log has grown far past anything this app should be producing.</summary>
    private const int SlowCheckpointMs = 3_000;

    /// <summary>Pages still in the log after a passive pass, expressed as MB. This is a real
    /// backlog — unlike the file size, which never comes down on its own.</summary>
    private const long BacklogAlarmMb = 256;

    /// <summary>Only worth handing the disk back above this, and only when the log is empty.</summary>
    private const long ReclaimAboveMb = 256;

    private System.Timers.Timer? _timer;
    private int  _running;
    private bool _warned;
    private bool _warnedTransaction;

    /// <summary>For the background-processes view.</summary>
    public string StatusText { get; private set; } = "WAL checkpoint: waiting";
    public DateTimeOffset? LastRunAt { get; private set; }
    public long            LastWalMb { get; private set; }

    public void Start()
    {
        if (_timer is not null) return;

        _timer = new System.Timers.Timer(Every.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await RunOnceAsync();
        _timer.Start();
    }

    /// <summary>One pass. Never throws: a checkpoint that cannot run is normal — a reader holds
    /// the log open — and the next pass will get what it can.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        // A pass that overruns its interval must not have a second one land on top of it.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        try
        {
            var before  = WalSizeMb();
            var started = System.Diagnostics.Stopwatch.StartNew();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var (busy, pagesInLog, copied) = await CheckpointAsync(db, "PASSIVE", ct);

            // ⚠️ The file size is not the backlog, and building this alarm on it was wrong. A
            // passive checkpoint copies pages out and lets SQLite reuse the space from the start
            // of the file — it never shrinks the file. So after any large burst the file stays at
            // its high-water mark for ever while the log inside it is empty, and a file-size alarm
            // reports a stall that is not happening. Measured: 612.1 MB, unchanged across every
            // sample, with nothing in flight and nothing blocked — a fully drained log.
            //
            // What the pragma returns is the truth: pages still in the log, and pages it managed
            // to copy. Only the first of those is a backlog.
            //
            // With nothing left in it, TRUNCATE is near-instant and hands the disk back — the only
            // safe moment for the mode that otherwise blocks writers for as long as it takes.
            if (busy == 0 && pagesInLog == 0 && WalSizeMb() >= ReclaimAboveMb)
                await CheckpointAsync(db, "TRUNCATE", ct);

            var took = (int)started.ElapsedMilliseconds;

            LastRunAt  = DateTimeOffset.UtcNow;
            LastWalMb  = WalSizeMb();
            StatusText = $"WAL checkpoint: log {LastWalMb} MB";

            if (took >= SlowCheckpointMs)
                errorLogger.Log(nameof(WalCheckpointService), "slow checkpoint",
                    $"A passive checkpoint took {took / 1000.0:N1}s with the log at {before} MB.");

            // ⚠️ Reported here rather than at commit, because the case that matters is the one
            // that has not committed. A write transaction left open holds the lock against every
            // other writer and stops the log being reclaimed, and it does so with no statement
            // running — invisible to everything that watches statements finish. This pass runs
            // every twenty seconds and is the only thing looking while it is still happening.
            var openFor = WriteContentionInterceptor.OldestOpenTransaction();
            if (openFor >= TimeSpan.FromSeconds(30) && !_warnedTransaction)
            {
                _warnedTransaction = true;
                errorLogger.Log(nameof(WalCheckpointService), "transaction left open",
                    $"A write transaction has been open {openFor.TotalSeconds:N0}s. While it is, no " +
                    $"other write can proceed and the log cannot be reclaimed — the log is at " +
                    $"{LastWalMb} MB. In flight: " +
                    WriteContentionInterceptor.DescribeLongRunning(TimeSpan.FromSeconds(5)));
            }
            else if (openFor < TimeSpan.FromSeconds(5))
            {
                _warnedTransaction = false;
            }

            // A real backlog: pages still in the log that a passive checkpoint could not copy,
            // which means a reader is holding a snapshot it cannot advance past. Measured in
            // PAGES, not in file size — see above for why that distinction is the whole point.
            var backlogMb = pagesInLog * 4096L / 1_048_576L;

            if (backlogMb >= BacklogAlarmMb && !_warned)
            {
                _warned = true;
                errorLogger.Log(nameof(WalCheckpointService), "write-ahead log not draining",
                    $"{backlogMb} MB is still in the log after a passive checkpoint " +
                    $"({copied:N0} of {pagesInLog:N0} pages copied, busy={busy}). The log cannot be " +
                    $"reused past the oldest snapshot still in use, so something is holding a read " +
                    $"open. In flight: " +
                    WriteContentionInterceptor.DescribeLongRunning(TimeSpan.FromSeconds(30)));
            }
            else if (backlogMb < BacklogAlarmMb / 2)
            {
                _warned = false;   // rearm once it has genuinely recovered
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(WalCheckpointService), nameof(RunOnceAsync), ex);
        }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    /// <summary>
    /// Runs a checkpoint and returns what it reports: whether it was blocked, how many pages are
    /// in the log, and how many it copied out.
    ///
    /// <para>⚠️ Run through the raw connection rather than <c>ExecuteSqlRawAsync</c>, which
    /// discards the row. Those three numbers are the only honest measure of the backlog — the file
    /// size is a high-water mark and says nothing about what is still in it.</para>
    /// </summary>
    private static async Task<(long Busy, long PagesInLog, long Copied)> CheckpointAsync(
        AppDbContext db, string mode, CancellationToken ct)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA wal_checkpoint({mode})";

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (0, 0, 0);

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static long WalSizeMb()
    {
        try
        {
            var wal = AppConfig.GetDbPath() + "-wal";
            return File.Exists(wal) ? new FileInfo(wal).Length / 1_048_576 : 0;
        }
        catch { return 0; }
    }
}
