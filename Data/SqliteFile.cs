namespace EveConsole.Data;

/// <summary>Housekeeping on the SQLite database file itself, before any connection is made.</summary>
public static class SqliteFile
{
    /// <summary>
    /// Removes a database file that is empty — zero bytes, no header — along with any sidecars,
    /// so the schema pass starts from "no database" rather than from one that cannot be written.
    ///
    /// <para>⚠️ A zero-byte file is worse than none. EF's EnsureCreated() opens it read-only to
    /// learn that it exists, the WAL pragma is then set on an empty file, and every write from
    /// there on answers "attempt to write a readonly database" — on that start and every start
    /// after it, since nothing ever gets written. A process that died between creating the file
    /// and writing its first page leaves exactly this; so did reading the schema fingerprint
    /// before EnsureCreated() in 0.9.14. A file with anything in it is never touched.</para>
    /// </summary>
    public static void DiscardIfEmpty(string dbPath)
    {
        try
        {
            if (!File.Exists(dbPath) || new FileInfo(dbPath).Length > 0) return;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm", dbPath + "-journal" })
                if (File.Exists(f)) File.Delete(f);
        }
        catch { /* leave it; EnsureCreated reports whatever is wrong in its own words */ }
    }
}
