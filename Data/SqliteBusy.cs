using Microsoft.Data.Sqlite;

namespace EveConsole.Data;

/// <summary>
/// SQLite's "database is locked", and how a long load lives with it: the write that met it waits
/// a moment and is made again.
///
/// <para>⚠️ SQLite has one write lock for the whole file. Every connection already waits up to
/// thirty seconds for it (busy_timeout, DisableForeignKeysInterceptor), but a background poll
/// can hold it longer than that, and an SDE load on SQLite runs long enough to meet one sooner or
/// later. Before this, the one batch that met it failed the whole import: the undo put the old
/// SDE back and the user started again from the download — six times over, on one machine.</para>
///
/// <para>A write that fails this way did nothing: SQLite rolls the statement back, and EF
/// rolls back the transaction SaveChanges opened and keeps the entities it was saving as Added.
/// So the same write is simply made again, as often as it takes — never the table from the top,
/// which on the big ones is most of an hour on SQLite. Only cancelling stops it.</para>
///
/// <para>Only for writes that are whole in themselves: one statement, one SaveChanges, one
/// transaction begun and committed inside <c>write</c>. A write inside a transaction opened
/// outside it cannot be retried on its own — the failure has already undone the rest.</para>
/// </summary>
public static class SqliteBusy
{
    /// <summary>How long to wait before the write is made again, on top of the thirty seconds
    /// SQLite has already waited inside it.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(3);

    /// <summary>SQLITE_BUSY or SQLITE_LOCKED, wherever EF has wrapped it.</summary>
    public static bool IsBusy(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is SqliteException { SqliteErrorCode: 5 or 6 }) return true;
        return false;
    }

    /// <summary>Makes the write, and again after <see cref="Pause"/> each time the database is
    /// locked. <paramref name="waiting"/> hears each wait, with how many there have been.</summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> write, Action<int>? waiting, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await write(); }
            catch (Exception ex) when (IsBusy(ex) && !ct.IsCancellationRequested)
            {
                waiting?.Invoke(attempt);
                await Task.Delay(Pause, ct);
            }
        }
    }

    public static Task RetryAsync(Func<Task> write, Action<int>? waiting, CancellationToken ct) =>
        RetryAsync(async () => { await write(); return true; }, waiting, ct);

    /// <summary>The same, for the synchronous schema statements.</summary>
    public static void Retry(Action write, Action<int>? waiting, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { write(); return; }
            catch (Exception ex) when (IsBusy(ex) && !ct.IsCancellationRequested)
            {
                waiting?.Invoke(attempt);
                ct.WaitHandle.WaitOne(Pause);
            }
        }
    }
}
