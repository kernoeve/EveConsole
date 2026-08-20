using System.Collections.Concurrent;
using System.Data.Common;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EveConsole.Data;

/// <summary>
/// Names the statement that was holding the write lock when someone else failed to get it.
///
/// <para><b>Why this exists.</b> SQLite allows one writer at a time, and
/// <see cref="DisableForeignKeysInterceptor"/> sets <c>busy_timeout = 30000</c> — so a
/// "database is locked" here is not mild contention, it means a writer held the lock for THIRTY
/// SECONDS or more. The error log recorded every victim of that and never the cause: a dozen
/// services reporting they could not write, and nothing saying who would not let go.</para>
///
/// <para>Two things are recorded, both of them genuine faults rather than diagnostics:</para>
/// <list type="bullet">
/// <item>A write that ran long enough to be the blocker, with its statement.</item>
/// <item>A failure to acquire the lock, together with whatever write was still in flight at that
/// moment and how long it had been running.</item>
/// </list>
///
/// <para>⚠️ A wait of about 30 s means a holder ran that long. A wait of nearly zero means
/// something else entirely: a transaction that read first and then tried to upgrade to a write
/// after another connection had written. SQLite fails that immediately and the busy timeout does
/// not apply, so the two look identical in the log without the duration. That is why the wait is
/// always reported.</para>
/// </summary>
public sealed class WriteContentionInterceptor(AppErrorLogger log)
    : DbCommandInterceptor, IDbTransactionInterceptor
{
    /// <summary>A write slower than this is reported on its own, without waiting for a victim.
    /// Well past anything this app does legitimately.</summary>
    private const int SlowWriteMs = 5_000;

    /// <summary>⚠️ The logger writes to the database, so logging a slow write would intercept the
    /// log's own INSERT and, if that were also slow, log again. Set while logging.</summary>
    private static readonly AsyncLocal<bool> Logging = new();

    private sealed record InFlight(string Sql, DateTimeOffset StartedAt, bool IsWrite);

    private static readonly ConcurrentDictionary<Guid, InFlight> Running = new();
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Transactions = new();

    // ── Commands ─────────────────────────────────────────────────────────────

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    { Track(command, eventData); return result; }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken ct = default)
    { Track(command, eventData); return ValueTask.FromResult(result); }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Track(command, eventData); return result; }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken ct = default)
    { Track(command, eventData); return ValueTask.FromResult(result); }

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { Done(eventData); return result; }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken ct = default)
    { Done(eventData); return ValueTask.FromResult(result); }

    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    { Done(eventData); return result; }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken ct = default)
    { Done(eventData); return ValueTask.FromResult(result); }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => Failed(command, eventData);

    public override Task CommandFailedAsync(
        DbCommand command, CommandErrorEventData eventData, CancellationToken ct = default)
    { Failed(command, eventData); return Task.CompletedTask; }

    // ── Transactions ─────────────────────────────────────────────────────────
    //
    // The lock is held from a transaction's first write until it commits, so a transaction of many
    // quick statements blocks for as long as all of them together. Timing commands alone would
    // report every one of them as fast and never explain the wait.

    // ⚠️ Started takes the CONNECTION first and the transaction last, unlike Committed and
    // RolledBack which take the transaction first. Declaring it the obvious way still COMPILES:
    // the method just stops implementing the interface member, EF's default no-op runs instead,
    // and the instrumentation reports nothing while looking entirely correct. That is how the
    // first version of this file shipped past a clean build — caught by asking the interface map
    // which method actually runs, not by reading it.
    public DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Transactions[eventData.TransactionId] = DateTimeOffset.UtcNow;
        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken ct = default)
    {
        Transactions[eventData.TransactionId] = DateTimeOffset.UtcNow;
        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => EndTransaction(eventData.TransactionId, "committed");

    public Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    { EndTransaction(eventData.TransactionId, "committed"); return Task.CompletedTask; }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
        => EndTransaction(eventData.TransactionId, "rolled back");

    public Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default)
    { EndTransaction(eventData.TransactionId, "rolled back"); return Task.CompletedTask; }

    private void EndTransaction(Guid id, string how)
    {
        if (!Transactions.TryRemove(id, out var startedAt)) return;

        var held = DateTimeOffset.UtcNow - startedAt;
        if (held.TotalMilliseconds < SlowWriteMs || Logging.Value) return;

        Report("slow transaction",
            $"A transaction {how} after {held.TotalSeconds:N1}s. Anything else trying to write " +
            $"was queued behind it for that whole time.");
    }

    // ── Bookkeeping ──────────────────────────────────────────────────────────

    private static void Track(DbCommand command, CommandEventData eventData)
    {
        if (Logging.Value) return;
        Running[eventData.CommandId] =
            new InFlight(Trim(command.CommandText), DateTimeOffset.UtcNow, IsWrite(command.CommandText));
    }

    private void Done(CommandExecutedEventData eventData)
    {
        if (!Running.TryRemove(eventData.CommandId, out var entry)) return;
        if (Logging.Value || !entry.IsWrite) return;
        if (eventData.Duration.TotalMilliseconds < SlowWriteMs) return;

        Report("slow write",
            $"Held the write lock {eventData.Duration.TotalSeconds:N1}s: {entry.Sql}");
    }

    private void Failed(DbCommand command, CommandErrorEventData eventData)
    {
        Running.TryRemove(eventData.CommandId, out _);

        // Only lock contention. Everything else already reports itself where it happened.
        if (eventData.Exception is not SqliteException { SqliteErrorCode: 5 or 6 } || Logging.Value)
            return;

        // Whatever is still running is the candidate — with one writer at a time, the oldest
        // in-flight write is the one that was in the way.
        var holder = Running.Values.Where(v => v.IsWrite)
            .OrderBy(v => v.StartedAt).FirstOrDefault();

        var waited  = eventData.Duration.TotalSeconds;
        var blocker = holder is null
            ? "Nothing was still in flight when this failed — so the holder had already finished, " +
              "or this was a transaction upgrading from a read to a write, which SQLite refuses " +
              "immediately rather than waiting."
            : $"Blocked by a write running {(DateTimeOffset.UtcNow - holder.StartedAt).TotalSeconds:N1}s " +
              $"by then: {holder.Sql}";

        Report("blocked",
            $"Waited {waited:N1}s then gave up. {blocker} Wanted: {Trim(command.CommandText)}");
    }

    private void Report(string context, string message)
    {
        Logging.Value = true;
        try { log.Log("SqliteWriteLock", context, message, null); }
        catch { /* diagnostics must never be the reason something fails */ }
        finally { Logging.Value = false; }
    }

    /// <summary>Whether a statement takes the write lock. BEGIN is included: a transaction opened
    /// IMMEDIATE takes it up front, and one that upgrades takes it at its first write.</summary>
    private static bool IsWrite(string sql)
    {
        foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "CREATE", "DROP", "ALTER",
                                     "VACUUM", "REPLACE", "PRAGMA wal_checkpoint", "BEGIN" })
            if (sql.Contains(verb, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Enough of the statement to recognise it, without pasting a bulk insert into the
    /// error log. Parameters are deliberately not included — they are values, and some are
    /// tokens.</summary>
    private static string Trim(string sql)
    {
        var flat = sql.Replace("\r", " ").Replace("\n", " ");
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        flat = flat.Trim();
        return flat.Length <= 300 ? flat : flat[..300] + " …";
    }
}
