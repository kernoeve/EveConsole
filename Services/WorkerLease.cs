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
    /// The key <see cref="SingleInstance"/> used when it locked the whole application, kept rather
    /// than replaced.
    ///
    /// <para>An older build takes this lock to forbid a second client outright. Keeping the key
    /// means such a build still excludes a newer one from becoming the worker, so during a mixed
    /// window the old client goes on polling alone instead of both of them polling. A fresh key
    /// would have made the two versions invisible to each other, which is the one outcome nobody
    /// wants — and the version check at startup now refuses that pairing anyway.</para>
    /// </summary>
    private const long AdvisoryLockKey = 0x4556_4543_4F4E_5301;   // "EVECONS" + 1

    private NpgsqlConnection?        _lock;
    private CancellationTokenSource? _cts;
    private DateTimeOffset           _takenUtc;

    /// <summary>
    /// Why the last attempt to take the lease failed, or null if it did not.
    ///
    /// <para>⚠️ Reported, not merely logged. A worker that cannot reach the server contends every
    /// tick for as long as it runs, and each refusal went to the application's error log — a table
    /// in the database that is doing the refusing. From outside, a worker that could not connect
    /// was indistinguishable from one waiting politely for a lease somebody else held, which is
    /// exactly the wrong two things to make look the same.</para>
    /// </summary>
    public string? LastAcquireError { get; private set; }

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

    /// <summary>
    /// One attempt at the lease, awaited.
    ///
    /// <para>For startup, which has to know the answer before it can decide anything else. Owning
    /// the background work is what carries the right to change the schema — bringing the schema up
    /// IS the start of background processing — so this question comes before the database is
    /// touched, not after.</para>
    /// </summary>
    public async Task<bool> AcquireAsync(CancellationToken ct = default)
    {
        // One candidate, so no contest, and nothing to ask.
        if (!DbEngine.IsPostgres)
        {
            IsHolder = true;
            return true;
        }

        await ContendAsync(ct);
        return IsHolder;
    }

    /// <summary>
    /// Begins holding and defending the lease, and announces where things already stand.
    ///
    /// <para>Any client that reaches this point is version-matched to the database — startup
    /// refuses to go on otherwise — so a client that takes over later is safe to do so: the schema
    /// it inherits is one its own build would have written.</para>
    /// </summary>
    public void Start()
    {
        if (!DbEngine.IsPostgres)
        {
            IsHolder = true;
            Gained?.Invoke();
            return;
        }

        // ⚠️ Re-announced, because AcquireAsync almost certainly settled this already and it did so
        // before anything had subscribed. Without this the client that IS the worker would sit
        // there having quietly won, running none of the work it won the right to do.
        if (IsHolder) Gained?.Invoke();

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
            var wasHolder = IsHolder;

            // ⚠️ Guarded whole. This loop ending is the app silently deciding never to do
            // background work again, with nothing on screen to say so.
            try
            {
                // ⚠️ A contender WAITS at the server rather than asking again in thirty seconds.
                // That is what makes the queue decide who gets it, and the queue is in join order —
                // so a worker that has been waiting since boot is served before a desktop client
                // that opened a moment ago. See ContendAsync.
                if (IsHolder) await HoldAsync(ct);
                else          await ContendAsync(ct, wait: true);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                errorLogger.Log("WorkerLease", "lease tick", ex);
            }

            // ⚠️ Skipped on the tick that won it. The wait can have been hours, and following it
            // with half a minute of nothing would put the delay back exactly where it was removed
            // from — between the lease becoming free and the work starting.
            if (!wasHolder && IsHolder) continue;

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
    ///
    /// <para><paramref name="wait"/> decides how it asks, and the difference decides who wins.
    /// ⚠️ Polling with <c>pg_try_advisory_lock</c> made the newcomer beat the incumbent every time:
    /// a starting client asks the instant it launches, while a client already contending is asleep
    /// for all but a moment of each tick. Open a desktop client beside a headless worker that has
    /// been waiting for hours and the desktop client takes the work, which is the exact opposite of
    /// "first come, keeps it" — and made the headless worker look broken when it was merely
    /// waiting its turn.</para>
    ///
    /// <para>So a contender BLOCKS on <c>pg_advisory_lock</c> instead. PostgreSQL queues waiters
    /// and grants in order, which does three things at once: the queue makes the incumbent win
    /// because it queued first, the grant is immediate rather than up to a tick later, and there is
    /// no polling at all. Startup still uses the non-waiting form — it has to answer "am I the
    /// worker?" before the schema step, and cannot sit and wait to find out.</para>
    /// </summary>
    private async Task ContendAsync(CancellationToken ct, bool wait = false)
    {
        var cs = AppConfig.GetPostgresConnection();
        if (string.IsNullOrWhiteSpace(cs))
        {
            // ⚠️ Said, not merely returned from. This is a real state — no connection configured, or
            // a password the keyring would not give up — and it lasts for as long as it lasts. A
            // silent return here is a worker that contends forever, reports nothing, and looks
            // exactly like one politely waiting for a lease somebody else holds.
            ReportAcquireFailure("no PostgreSQL connection is configured for this process");
            return;
        }

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
                //
                // ⚠️ KeepAlive, because a contender now sits in a blocking wait that can last days.
                // Without it a server that went away unnoticed would leave this process waiting on a
                // socket nobody is going to answer, having quietly stopped contending at all.
                var b = new NpgsqlConnectionStringBuilder(AppDb.PostgresConnectionString(cs))
                {
                    Pooling   = false,
                    KeepAlive = 30,
                };
                conn = new NpgsqlConnection(b.ConnectionString);
                await conn.OpenAsync(ct);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Parameters.AddWithValue("k", AdvisoryLockKey);

                    if (wait)
                    {
                        // ⚠️ No command timeout. Waiting IS the operation, and Npgsql's default
                        // thirty seconds would abort it — turning the queued wait straight back
                        // into the polling this replaced, with more moving parts.
                        cmd.CommandText    = "SELECT pg_advisory_lock(@k)";
                        cmd.CommandTimeout = 0;
                        await cmd.ExecuteNonQueryAsync(ct);   // returns when granted
                    }
                    else
                    {
                        cmd.CommandText = "SELECT pg_try_advisory_lock(@k)";
                        if (await cmd.ExecuteScalarAsync(ct) is not true)
                        {
                            await conn.DisposeAsync();   // somebody else has it: nothing wrong happened
                            return;
                        }
                    }
                }

                _lock = conn;
                conn  = null;                        // owned by the lease now, not by this method
            }
            catch (OperationCanceledException)
            {
                // Shutting down mid-wait. Dispose here rather than leaving it to a finalizer: the
                // server is holding a backend open for this wait and should be told now.
                if (conn is not null) { try { await conn.DisposeAsync(); } catch { } }
                throw;
            }
            catch (Exception ex)
            {
                // Unreachable, refused, wrong credentials — all the same answer: not the holder.
                if (conn is not null) { try { await conn.DisposeAsync(); } catch { } }
                errorLogger.Log("WorkerLease", "acquiring the lease", ex);
                ReportAcquireFailure(ex.Message.Split('\n')[0].Trim());
                return;
            }
        }

        ClearAcquireFailure();

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

    /// <summary>
    /// Says, somewhere reachable, that the lease could not be taken.
    ///
    /// <para>⚠️ On change only. This runs every thirty seconds for as long as the condition lasts,
    /// so reporting each attempt would fill a journal with one repeated line and bury the start-up
    /// banner that says which database it is even trying to reach. The first refusal is the news;
    /// the two hundredth is not.</para>
    /// </summary>
    private void ReportAcquireFailure(string reason)
    {
        if (reason == LastAcquireError) return;

        LastAcquireError = reason;

        var line = $"cannot take the background lease — {reason}";
        if (AppRuntime.IsHeadless) Console.Error.WriteLine($"EVE Console: {line}");
        ServiceLog.Write(line);
    }

    /// <summary>The other half: a recovery nobody is told about reads as a fault that never ended.</summary>
    private void ClearAcquireFailure()
    {
        if (LastAcquireError is null) return;

        LastAcquireError = null;

        const string line = "reached the database again — contending for the background lease";
        if (AppRuntime.IsHeadless) Console.Error.WriteLine($"EVE Console: {line}");
        ServiceLog.Write(line);
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
