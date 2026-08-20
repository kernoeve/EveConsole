using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Helps drain the write-ahead log, without ever blocking anyone to do it.
///
/// <para><b>⚠️ PASSIVE only, and never TRUNCATE.</b> This service first shipped running TRUNCATE
/// once the file passed 64 MB, with SQLite's automatic checkpoint turned off so that no ordinary
/// write would pay for one. That was wrong in both halves and the numbers were emphatic: TRUNCATE
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

    /// <summary>Past this the log is not being reclaimed, which means a reader is holding a
    /// snapshot open. Reported once per crossing rather than every pass.</summary>
    private const long AlarmingWalMb = 512;

    private System.Timers.Timer? _timer;
    private int  _running;
    private bool _warned;

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
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(PASSIVE)", ct);

            var took = (int)started.ElapsedMilliseconds;

            LastRunAt  = DateTimeOffset.UtcNow;
            LastWalMb  = WalSizeMb();
            StatusText = $"WAL checkpoint: log {LastWalMb} MB";

            if (took >= SlowCheckpointMs)
                errorLogger.Log(nameof(WalCheckpointService), "slow checkpoint",
                    $"A passive checkpoint took {took / 1000.0:N1}s with the log at {before} MB.");

            // The log not coming down is the symptom worth chasing — it means something is holding
            // a read snapshot open, and no amount of checkpointing will help until it lets go.
            if (LastWalMb >= AlarmingWalMb && !_warned)
            {
                _warned = true;
                errorLogger.Log(nameof(WalCheckpointService), "write-ahead log not draining",
                    $"The log has reached {LastWalMb} MB and passive checkpoints are not reclaiming " +
                    $"it. That happens when a read transaction stays open: the log cannot be reused " +
                    $"past the oldest snapshot still in use. Look for a long-running query rather " +
                    $"than forcing a checkpoint.");
            }
            else if (LastWalMb < AlarmingWalMb / 2)
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
