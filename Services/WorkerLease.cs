using System.Data.Common;
using EveConsole.Data;
using EveConsole.Models;
using Npgsql;

namespace EveConsole.Services;

/// <summary>
/// Decides whether THIS process is the one that does the background work.
///
/// <para>Several clients may now be pointed at one PostgreSQL database, and exactly one of them
/// must poll ESI, recalculate build costs, take the backups and update the schema. Everything
/// else runs read-only. This class is the whole of that decision; the services themselves know
/// nothing about it beyond being started and stopped.</para>
///
/// <para><b>First come, keeps it.</b> Whoever acquires the lease holds it until it exits, and
/// releases cleanly on the way out so the next tick elsewhere picks it up. There is no preemption
/// and no priority, which is what makes the behaviour predictable: a worker started at boot is
/// running before any desktop client opens, and therefore wins without any rule saying it should.
/// </para>
///
/// <para>⚠️ On SQLite this is always true and does nothing. <see cref="SingleInstance"/> still
/// holds an exclusive handle on a file beside the database there, so a second process cannot
/// exist to contend with — and a lease among one candidate is a constant, not a decision.</para>
/// </summary>
public sealed class WorkerLease(AppErrorLogger errorLogger)
{
    /// <summary>
    /// How often a holder proves it still holds, and a contender tries to take over.
    ///
    /// <para>This IS the failover latency: a worker that dies is replaced within one tick. Thirty
    /// seconds because the cost of being wrong is small in both directions — a poll delayed by
    /// half a minute is invisible, and the statement itself is a single-row UPDATE.</para>
    /// </summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The same key <see cref="SingleInstance"/> used when it locked the whole application.
    ///
    /// <para>Deliberately not a new one. Reusing it means an old build, which takes this lock to
    /// forbid a second client outright, still excludes a new build from becoming the worker — so
    /// during an upgrade window the old client keeps polling alone rather than both of them
    /// polling. A fresh key would have made the two versions invisible to each other, which is the
    /// one outcome nobody wants.</para>
    /// </summary>
    private const long AdvisoryLockKey = 0x4556_4543_4F4E_5301;   // "EVECONS" + 1

    private NpgsqlConnection?        _lock;
    private CancellationTokenSource? _cts;
    private DateTimeOffset           _takenUtc;

    /// <summary>Whether this process is currently responsible for background work.</summary>
    public bool IsHolder { get; private set; }

    /// <summary>Raised when this process becomes responsible. Start leader-only services here.</summary>
    public event Action? Gained;

    /// <summary>
    /// Raised when this process stops being responsible — including when the lock connection dies
    /// under it. ⚠️ Stop leader-only services here, and mean it: the whole point is that the
    /// database never has two writers, and a process that lost its lock without noticing is
    /// exactly how it would.
    /// </summary>
    public event Action? Lost;

    public void Start()
    {
        // One candidate, so no contest. Announce it and run nothing.
        if (!DbEngine.IsPostgres)
        {
            IsHolder = true;
            Gained?.Invoke();
            return;
        }

        // ⚠️ The lock is already held, by this process. SingleInstance takes this very key at
        // startup to keep a second client out, and an advisory lock belongs to the session that
        // took it — so opening a new connection and asking for it would be this process losing a
        // race with itself, on the silent path that means "somebody else has it". Adopt that
        // session rather than contending with it. Null on a server that could not be reached,
        // which correctly sends us round the contending path instead.
        _lock = SingleInstance.TakePostgresLock();

        _cts = new CancellationTokenSource();
        _ = RunLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Releases the lease so another client can take it without waiting for a tick.
    ///
    /// <para>The server would drop the lock anyway when this process exits. Doing it explicitly
    /// is what turns "first come, keeps it" from a rule about crashes into one about ordinary
    /// shutdowns: close the desktop client and the headless worker beside it starts polling within
    /// a tick, rather than within however long the operating system takes to notice.</para>
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (IsHolder && _lock is not null)
        {
            // Best effort by design: this runs on the way out, and a server that has already gone
            // away releases the lock for us the moment the socket closes.
            try { using var cmd = _lock.CreateCommand();
                  cmd.CommandText = "SELECT pg_advisory_unlock(@k)";
                  cmd.Parameters.AddWithValue("k", AdvisoryLockKey);
                  cmd.ExecuteNonQuery(); }
            catch { /* the disconnect below does the same job */ }
        }

        try { _lock?.Dispose(); } catch { }
        _lock = null;

        if (IsHolder) { IsHolder = false; Lost?.Invoke(); }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Contend immediately rather than after a tick: on a machine where nothing else is
        // running, waiting thirty seconds to start polling would be thirty seconds of nothing.
        while (!ct.IsCancellationRequested)
        {
            // ⚠️ Guarded whole. This loop ending is the app silently deciding never to do
            // background work again, with nothing on screen to say so.
            try
            {
                if (IsHolder) await HoldAsync(ct);
                else          await ContendAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                errorLogger.Log("WorkerLease", "lease tick", ex);
            }

            try { await Task.Delay(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Proves the lock is still ours, and stands down if it is not.
    ///
    /// <para>⚠️ The heartbeat write IS the proof. A separate "SELECT 1" health check would leave a
    /// gap where the probe succeeds and the write does not; doing the one statement that has to
    /// work anyway means a holder that cannot record itself is a holder that has already stopped
    /// being one. The advisory lock lives on this exact session, so if this statement cannot reach
    /// the server the lock is already gone — the server released it when the socket died.</para>
    /// </summary>
    private async Task HoldAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _lock!.CreateCommand();
            cmd.CommandText = """UPDATE "BackgroundWorkerStatus" SET "HeartbeatUtc" = @n WHERE "Id" = 1""";
            cmd.Parameters.AddWithValue("n", DateTimeOffset.UtcNow);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log("WorkerLease", "lost the lease", ex);

            try { _lock?.Dispose(); } catch { }
            _lock    = null;
            IsHolder = false;
            Lost?.Invoke();
        }
    }

    /// <summary>
    /// Tries to become the holder.
    ///
    /// <para>⚠️ Fails closed, and that is the whole difference between this and the single-instance
    /// check it grew out of. <c>SingleInstance</c> returned "yes" when the server could not be
    /// reached, on purpose, so that startup would fail a moment later with a better message. The
    /// same answer here would mean every client that loses the network appoints itself the worker
    /// — a partition producing as many writers as there are clients.</para>
    /// </summary>
    private async Task ContendAsync(CancellationToken ct)
    {
        var cs = AppConfig.GetPostgresConnection();
        if (string.IsNullOrWhiteSpace(cs)) return;   // App reports the misconfiguration

        // Not already ours — from SingleInstance's handover, or from a previous tick that took
        // the lock but could not finish claiming it.
        if (_lock is null)
        {
            NpgsqlConnection? conn = null;
            try
            {
                // ⚠️ Unpooled and kept open for the life of the lease. An advisory lock belongs to
                // the session that took it, so returning this connection to the pool would end that
                // session and drop the lock while this process still believed it held one.
                var b = new NpgsqlConnectionStringBuilder(AppDb.PostgresConnectionString(cs)) { Pooling = false };
                conn = new NpgsqlConnection(b.ConnectionString);
                await conn.OpenAsync(ct);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT pg_try_advisory_lock(@k)";
                    cmd.Parameters.AddWithValue("k", AdvisoryLockKey);
                    if (await cmd.ExecuteScalarAsync(ct) is not true)
                    {
                        await conn.DisposeAsync();   // somebody else has it: nothing wrong happened
                        return;
                    }
                }

                _lock = conn;
                conn  = null;                        // owned by the lease now, not by this method
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Unreachable, refused, wrong credentials — all the same answer: not the holder.
                if (conn is not null) { try { await conn.DisposeAsync(); } catch { } }
                errorLogger.Log("WorkerLease", "acquiring the lease", ex);
                return;
            }
        }

        // The lock is ours. Say who we are before claiming to be anyone.
        try
        {
            _takenUtc = DateTimeOffset.UtcNow;
            await ClaimAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // ⚠️ Keep the lock. We hold it, and releasing here would let a second client in;
            // worse, the next tick would open a fresh session and ask for a key this process
            // never let go of, which answers false forever. Retried on the next tick instead,
            // through the branch above that sees _lock is already set.
            errorLogger.Log("WorkerLease", "recording the lease holder", ex);
            return;
        }

        IsHolder = true;
        Gained?.Invoke();
    }

    /// <summary>Records who took it, so other clients can see what version is writing.</summary>
    private async Task ClaimAsync(CancellationToken ct)
    {
        // ⚠️ Every column listed. A raw INSERT that omits one works against a database whose
        // column arrived by ALTER TABLE with a default, and fails against a fresh install where it
        // did not — the difference only shows up on somebody else's first run.
        await using var cmd = _lock!.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "BackgroundWorkerStatus"
                ("Id", "Version", "HostName", "ProcessId", "Headless", "LeaseTakenUtc", "HeartbeatUtc")
            VALUES (1, @v, @h, @p, @l, @t, @t)
            ON CONFLICT ("Id") DO UPDATE SET
                "Version"       = EXCLUDED."Version",
                "HostName"      = EXCLUDED."HostName",
                "ProcessId"     = EXCLUDED."ProcessId",
                "Headless"      = EXCLUDED."Headless",
                "LeaseTakenUtc" = EXCLUDED."LeaseTakenUtc",
                "HeartbeatUtc"  = EXCLUDED."HeartbeatUtc"
            """;
        cmd.Parameters.AddWithValue("v", AppVersion.Number);
        cmd.Parameters.AddWithValue("h", Environment.MachineName);
        cmd.Parameters.AddWithValue("p", Environment.ProcessId);
        cmd.Parameters.AddWithValue("l", AppRuntime.IsHeadless);
        cmd.Parameters.AddWithValue("t", _takenUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// What the database says about the worker, for a client deciding whether to go on.
    ///
    /// <para>⚠️ Returns the row whether or not anyone is alive. Liveness is the caller's judgement
    /// from <see cref="BackgroundWorkerStatus.HeartbeatUtc"/>, because a crashed worker leaves its
    /// row exactly as it was and there is nothing to distinguish it from a running one except how
    /// old the heartbeat is.</para>
    /// </summary>
    public static async Task<BackgroundWorkerStatus?> ReadStatusAsync(CancellationToken ct = default)
    {
        if (!DbEngine.IsPostgres) return null;

        try
        {
            await using var conn = (DbConnection)AppDb.Connect();
            await conn.OpenAsync(ct);

            await using var cmd = conn.Command(
                """
                SELECT "Version", "HostName", "ProcessId", "Headless", "LeaseTakenUtc", "HeartbeatUtc"
                FROM "BackgroundWorkerStatus" WHERE "Id" = 1
                """);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;

            return new BackgroundWorkerStatus
            {
                Version       = r.GetString(0),
                HostName      = r.GetString(1),
                ProcessId     = r.GetInt32(2),
                Headless      = r.GetBoolean(3),
                LeaseTakenUtc = r.GetFieldValue<DateTimeOffset>(4),
                HeartbeatUtc  = r.GetFieldValue<DateTimeOffset>(5),
            };
        }
        catch
        {
            // Nothing useful to say: a client that cannot read this cannot warn about it either,
            // and it is about to fail on something louder.
            return null;
        }
    }

    /// <summary>
    /// Whether a heartbeat that old still means "running".
    ///
    /// <para>Three ticks. One missed heartbeat is a slow statement or a stalled laptop; three in a
    /// row is a process that is not coming back — and being slow to declare a worker dead costs
    /// nothing here, because this only decides whether to warn about its version.</para>
    /// </summary>
    public static bool IsLive(BackgroundWorkerStatus s) =>
        DateTimeOffset.UtcNow - s.HeartbeatUtc < Tick * 3;
}
