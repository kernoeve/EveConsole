using Microsoft.Data.Sqlite;

namespace EveConsole.Services;

/// <summary>
/// The two details every file-level operation on the database has to get right.
///
/// <para>Shared rather than repeated because both were learned the hard way, and a second copy
/// would be the one that drifts.</para>
/// </summary>
public static class SqliteMaintenance
{
    /// <summary>
    /// ⚠️ Unpooled, and never read-only. Microsoft.Data.Sqlite pools by connection string and keeps
    /// the native handle alive past <c>Dispose()</c>, so a connection opened by a maintenance path
    /// can be handed back to the app afterwards — which produced "attempt to write a readonly
    /// database" on the first write after a shrink. Unpooled makes "this leaves no handle behind"
    /// a property of the code rather than something to remember.
    /// </summary>
    public static string ConnectionString(string path)
        => $"Data Source={path};Pooling=False";

    /// <summary>
    /// Folds the write-ahead log into the main database file and truncates it.
    ///
    /// <para>⚠️ Required before copying or moving the file. A restart is an
    /// <c>Environment.Exit</c>, which kills the process without closing SQLite's connections, so a
    /// <c>-wal</c> holding committed transactions is normally still sitting there. Copy the main
    /// file alone and those transactions are missing from the copy; move it alone and they are
    /// stranded beside the old path. Neither fails loudly — the data is simply gone.</para>
    ///
    /// <para>Best effort: no WAL, or nothing to fold, is success rather than a problem.</para>
    /// </summary>
    public static void Checkpoint(string dbPath)
    {
        try
        {
            using var conn = new SqliteConnection(ConnectionString(dbPath));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            cmd.ExecuteNonQuery();
        }
        catch { /* nothing to fold, or no database yet */ }
        finally { SqliteConnection.ClearAllPools(); }
    }

    /// <summary>Removes the sidecars belonging to a database file. They describe the database they
    /// sat next to; left beside a replaced or vacated path, SQLite would try to recover a log
    /// against a file it does not describe.</summary>
    public static void DeleteSidecars(string dbPath)
    {
        foreach (var side in new[] { dbPath + "-wal", dbPath + "-shm" })
            try { if (File.Exists(side)) File.Delete(side); } catch { }
    }
}
