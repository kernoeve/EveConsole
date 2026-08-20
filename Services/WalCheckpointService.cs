using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Drains the write-ahead log on a schedule, so no ordinary write ever has to.
///
/// <para><b>Why this exists.</b> SQLite's automatic checkpoint runs INLINE on whichever connection
/// commits past the page threshold — that writer does the work for everyone else while the rest
/// queue behind it. It is invisible in any log, because the statement that pays is simply the one
/// that happened to commit at the wrong moment. Measured here at a 213 MB log: sixteen updates to
/// a 639-row table all finished in the same second, each reporting more than twenty seconds, none
/// of them the cause.</para>
///
/// <para><see cref="DisableForeignKeysInterceptor"/> turns the automatic checkpoint off, so this
/// is the only thing that drains the log. If it stops, the log grows without bound — every read
/// has to search it, and the file keeps the space.</para>
///
/// <para><b>PASSIVE first.</b> It copies what it can and gives up the moment it meets a reader
/// rather than blocking one, which is what makes it safe to run this often. It cannot reclaim the
/// FILE, only the space inside it, so a TRUNCATE follows when the file has grown past
/// <see cref="TruncateAboveMb"/> — that one does block writers briefly, which is the whole reason
/// it is rationed rather than run every pass.</para>
/// </summary>
public class WalCheckpointService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
{
    /// <summary>Often enough that the log stays small, rare enough to cost nothing when idle.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(20);

    /// <summary>Past this the file is worth reclaiming, not just emptying.</summary>
    private const long TruncateAboveMb = 64;

    /// <summary>A checkpoint slower than this is worth knowing about: it is the exact cost that
    /// used to land on a random ESI write.</summary>
    private const int SlowCheckpointMs = 3_000;

    private System.Timers.Timer? _timer;
    private int _running;

    /// <summary>For the background-processes view.</summary>
    public string StatusText { get; private set; } = "WAL checkpoint: waiting";
    public DateTimeOffset? LastRunAt  { get; private set; }
    public long            LastWalMb  { get; private set; }

    public void Start()
    {
        if (_timer is not null) return;

        _timer = new System.Timers.Timer(Every.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += async (_, _) => await RunOnceAsync();
        _timer.Start();
    }

    /// <summary>One pass. Never throws: a checkpoint that cannot run is normal — a reader holds
    /// the log open — and the next pass will get it.</summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        // A pass that overruns its interval must not have a second one land on top of it.
        if (Interlocked.Exchange(ref _running, 1) == 1) return;

        try
        {
            var walMb = WalSizeMb();
            var mode  = walMb >= TruncateAboveMb ? "TRUNCATE" : "PASSIVE";

            var started = System.Diagnostics.Stopwatch.StartNew();

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await db.Database.ExecuteSqlRawAsync($"PRAGMA wal_checkpoint({mode})", ct);

            var took = (int)started.ElapsedMilliseconds;

            LastRunAt  = DateTimeOffset.UtcNow;
            LastWalMb  = WalSizeMb();
            StatusText = $"WAL checkpoint: {mode.ToLowerInvariant()}, log {LastWalMb} MB";

            // Only when it was expensive. A checkpoint doing its job is not news, and this runs
            // three times a minute.
            if (took >= SlowCheckpointMs)
                errorLogger.Log(nameof(WalCheckpointService), "slow checkpoint",
                    $"A {mode} checkpoint took {took / 1000.0:N1}s with the log at {walMb} MB " +
                    $"({LastWalMb} MB after). Writers were held off for that long — had this run " +
                    $"inline on an ordinary write, that write would have worn the whole cost.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // ⚠️ Not silent. With the automatic checkpoint off, this failing repeatedly is the one
            // way the log grows without bound, and nothing else would report it.
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
