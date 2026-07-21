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
        cmd.CommandText = """
            PRAGMA foreign_keys = OFF;
            PRAGMA journal_mode  = WAL;
            PRAGMA busy_timeout  = 30000;
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
