using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EveConsole.Services;

// ⚠️ EF1002 is suppressed for this file, for two interpolations with different reasons. The table
// names come from BulkImport.TablesFor — read out of the compiled entity model and filtered to
// letters and digits — and a table name cannot be a bound parameter anyway. The ATTACH statement
// interpolates a filesystem path instead, taken from the open connection's own DataSource with
// its quotes doubled, so no caller supplies any part of that either. Anything added here that
// interpolates something else inherits neither argument and needs its own.
#pragma warning disable EF1002

/// <summary>
/// A way to put the previous data back if a wipe-and-refill import does not finish.
/// </summary>
/// <remarks>
/// ⚠️ The obvious implementation — one transaction around the wipe and the refill — is right on
/// PostgreSQL and wrong on SQLite, so there are two.
///
/// <para>SQLite allows a single writer, and a transaction held open for the minutes an import
/// takes holds that lock the whole time. Measured on a 600k-row stand-in: another connection could
/// still READ the pre-import data, but an insert into a completely unrelated table failed with
/// "database is locked", and <c>wal_checkpoint(TRUNCATE)</c> returned busy and reclaimed nothing,
/// pinning the log open at the size of the data. Every background poller in this app would stall
/// for its thirty-second busy_timeout and then throw, for the whole import.
/// <c>WriteContentionInterceptor</c> exists because that shape of fault has already cost this app
/// a 787 MB log and thirty-second stalls, and SQLite is the default backend.</para>
///
/// <para>So PostgreSQL gets the transaction, which costs it nothing and additionally keeps every
/// other client reading the previous data until the commit. SQLite copies the tables into an
/// attached file instead: one statement per table, so the write lock is taken and released once
/// per table rather than held once for all of them. The trade is real and worth naming — on SQLite
/// the wipe commits immediately, so anything reading those tables during an import sees them
/// empty, exactly as it did before any of this existed. What it no longer does is stay that
/// way.</para>
///
/// <para>Used by both the SDE and the Hoboleaks imports; <c>tools/SdeUndoCheck</c> covers the
/// SQLite half.</para>
/// </remarks>
public interface IBulkImportUndo : IAsyncDisposable
{
    /// <summary>Keep the new data and discard the means of undoing it.</summary>
    Task CommitAsync(CancellationToken ct);

    /// <summary>Put the previous data back.</summary>
    Task RollbackAsync(CancellationToken ct);
}

public static class BulkImportUndo
{
    /// <summary>What an undo tells the caller about its own slow parts: stage, detail, fraction.</summary>
    /// <remarks>A fraction below zero means "leave the bar where it is".</remarks>
    public delegate void Report(string stage, string detail, double fraction);

    /// <summary>Opens whichever undo the configured engine can afford. Call before the wipe.</summary>
    /// <param name="name">
    /// Distinguishes one import's backup file from another's, so the SDE and Hoboleaks cannot
    /// collide over it.
    /// </param>
    public static async Task<IBulkImportUndo> CreateAsync(AppDbContext db, string name,
        IReadOnlyList<string> tables, Report report, CancellationToken ct)
        => DbEngine.IsPostgres
            ? new TransactionUndo(await db.Database.BeginTransactionAsync(ct))
            : await FileCopyUndo.CreateAsync(db, name, tables, report, ct);

    /// <summary>PostgreSQL: the whole import is one transaction.</summary>
    private sealed class TransactionUndo(IDbContextTransaction tx) : IBulkImportUndo
    {
        public Task CommitAsync(CancellationToken ct)   => tx.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct) => tx.RollbackAsync(ct);

        // Disposing an uncommitted transaction rolls it back, which is exactly what an exception
        // on the way out should do. Nothing to add.
        public ValueTask DisposeAsync() => tx.DisposeAsync();
    }

    /// <summary>SQLite: a copy of the tables in a file beside the database.</summary>
    private sealed class FileCopyUndo(AppDbContext db, IReadOnlyList<string> tables, string path,
        Report report) : IBulkImportUndo
    {
        private bool _settled;
        private bool _closed;

        public static async Task<FileCopyUndo> CreateAsync(AppDbContext db, string name,
            IReadOnlyList<string> tables, Report report, CancellationToken ct)
        {
            // ⚠️ The connection is opened explicitly and held for the life of this object. ATTACH
            // is a property of a CONNECTION rather than of the database, and EF closes and reopens
            // the connection between operations unless it has been opened by hand — which would
            // drop the attachment somewhere in the middle of the import, and the first statement
            // to notice would be the restore that needed it.
            await db.Database.OpenConnectionAsync(ct);

            // Beside whichever file is actually open, asked of the connection rather than of
            // config: config says which database the app was told to use, the connection knows
            // which one it got.
            var path = $"{db.Database.GetDbConnection().DataSource}.{name}bak";
            Discard(path);   // whatever an import that died left behind

            var undo = new FileCopyUndo(db, tables, path, report);
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"ATTACH DATABASE '{path.Replace("'", "''")}' AS undobak", ct);

                var n = 0;
                foreach (var t in tables)
                {
                    // One statement, one implicit transaction. CREATE TABLE AS copies the columns
                    // in order and none of the indexes, which is all a restore needs: it is read
                    // back with INSERT INTO ... SELECT *, never queried on its own.
                    await db.Database.ExecuteSqlRawAsync(
                        $"CREATE TABLE undobak.\"{t}\" AS SELECT * FROM main.\"{t}\"", ct);

                    report("Preparing", $"Copying the current data aside… {++n}/{tables.Count}", -1);
                }
            }
            catch
            {
                // Nothing has been wiped yet, so failing here costs the user only the download.
                // Leaving a half-written copy behind would cost them the disk as well.
                undo._settled = true;
                await undo.CloseAsync();
                throw;
            }

            return undo;
        }

        public async Task CommitAsync(CancellationToken ct)
        {
            if (_settled) return;
            _settled = true;
            await CloseAsync();
        }

        public async Task RollbackAsync(CancellationToken ct)
        {
            if (_settled) return;
            _settled = true;
            await RestoreAsync(ct);
            await CloseAsync();
        }

        public async ValueTask DisposeAsync()
        {
            // Unsettled means the import threw on its way past, which is the whole reason the copy
            // was taken. CancellationToken.None: a cancelled import is the case that most needs
            // its data back, and passing the cancelled token would abandon the restore halfway.
            if (!_settled)
            {
                _settled = true;
                try { await RestoreAsync(CancellationToken.None); }
                catch { /* the caller is already reporting a failure this cannot improve on */ }
            }

            await CloseAsync();
        }

        private async Task RestoreAsync(CancellationToken ct)
        {
            var n = 0;
            foreach (var t in tables)
            {
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM main.\"{t}\"", ct);
                await db.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO main.\"{t}\" SELECT * FROM undobak.\"{t}\"", ct);

                report("Restoring", $"Putting the previous data back… {++n}/{tables.Count}", -1);
            }
        }

        private async Task CloseAsync()
        {
            if (_closed) return;
            _closed = true;

            try { await db.Database.ExecuteSqlRawAsync("DETACH DATABASE undobak"); } catch { /* never attached */ }
            try { await db.Database.CloseConnectionAsync(); }  catch { /* already closed */ }
            Discard(path);
        }

        /// <summary>The backup, and the two files SQLite keeps beside it.</summary>
        private static void Discard(string path)
        {
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* in use; the next run retries */ }
        }
    }
}
