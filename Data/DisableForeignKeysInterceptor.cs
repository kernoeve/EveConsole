using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EveConsole.Data;

// EF Core's SQLite provider runs "PRAGMA foreign_keys = ON" after every connection open.
// Our Corporations table still has the old FK constraint in its DDL (SQLite can't drop constraints).
// This interceptor immediately overrides that pragma so the DB never enforces it.
// Referential integrity between Character and Corporation is managed at the application layer.
public class DisableForeignKeysInterceptor : DbConnectionInterceptor
{
    private static void ApplyPragmas(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        // WAL allows readers to proceed while a writer holds the lock.
        // busy_timeout makes SQLite retry for up to 30 s before throwing SQLITE_BUSY,
        // preventing "entity changes" errors when the market bulk-write overlaps polling writes.
        //
        // ⚠️ synchronous=NORMAL rather than the FULL default. Under FULL every commit fsyncs, and
        // the exclusive write lock is held across the whole commit — so each of the hundreds of
        // small writes a minute this app makes held that lock for a disk round trip while
        // everything else queued behind it. Measured before the change: 408 "database is locked"
        // failures in three days across nine services. NORMAL returns once the write reaches the
        // OS cache and syncs at checkpoints instead.
        //
        // Safe in WAL: SQLite's own documentation states WAL is corruption-proof at NORMAL and
        // recommends it for WAL applications. An application crash loses nothing, since the OS
        // still flushes; only an OS crash or power cut can lose the last seconds of commits, and
        // nearly everything written here is re-fetchable.
        // ⚠️ autocheckpoint OFF, and checkpointing handed to WalCheckpointService instead.
        //
        // At the default of 1000 pages, whichever connection happens to commit past the threshold
        // performs the checkpoint INLINE — doing the work for every other writer while they queue
        // behind it. With a write-ahead log that had grown to 213 MB, that cost the unlucky writer
        // more than twenty seconds. Measured: sixteen updates to a 639-row table, all completing
        // within the same second, every one of them reported as having "held" the lock for over
        // twenty seconds when all they did was wait. The statement in the log was never the cause,
        // because the cause is whichever statement drew the short straw.
        //
        // Off, no writer ever pays for it, and one service does it deliberately where it can be
        // measured and kept off the critical path.
        cmd.CommandText = """
            PRAGMA foreign_keys      = OFF;
            PRAGMA journal_mode      = WAL;
            PRAGMA synchronous       = NORMAL;
            PRAGMA busy_timeout      = 30000;
            PRAGMA wal_autocheckpoint = 0;
            """;
        cmd.ExecuteNonQuery();
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => ApplyPragmas(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        await Task.Run(() => ApplyPragmas(connection), ct);
    }
}
