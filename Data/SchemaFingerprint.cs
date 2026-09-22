using EveConsole.Services;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Data;

/// <summary>
/// How many (table, column) pairs a family of tables has — taken before and after the startup
/// schema pass to learn whether that pass added anything.
/// </summary>
/// <remarks>
/// <para>A column the schema pass adds is EMPTY. The pass makes the query stop throwing; it
/// does not put the SDE's value for that column into any row. So an upgrade that grows the
/// SDE or Hobo tables needs the matching import run again, and the app cannot know that
/// from the version number — a build can add a column and a build can add none. It can know
/// it from the schema: count what the SDE and Hobo tables hold before the pass and after,
/// and if the count grew, the data behind the growth is missing.</para>
///
/// <para>Measured rather than reported. Every hand-written list — the SDE's, Hobo's, the
/// PostgreSQL bootstrap — would otherwise have to say whether it changed anything, and a
/// list that forgot to would be one more thing to keep in step. This asks the database.</para>
/// </remarks>
public static class SchemaFingerprint
{
    /// <summary>Count of (table, column) pairs across every table whose name starts with <paramref name="prefix"/>.</summary>
    public static int ColumnsUnder(DbContext db, string prefix)
    {
        var cn = db.Database.GetDbConnection();

        // ⚠️ A file that is not there holds no columns, and it must not be opened to say so.
        // Opening a SQLite connection CREATES the file, empty, and EF's EnsureCreated() then
        // finds a zero-byte database rather than none: it opens it read-only to ask whether it
        // exists, sets journal_mode=WAL on it, and every write after that fails with "attempt
        // to write a readonly database". That was a fresh install of 0.9.14 failing to start.
        if (!DbEngine.IsPostgres && !SqliteFileExists(cn.ConnectionString)) return 0;

        var wasClosed = cn.State != System.Data.ConnectionState.Open;
        if (wasClosed) cn.Open();
        try
        {
            using var cmd = cn.CreateCommand();
            if (DbEngine.IsPostgres)
            {
                cmd.CommandText = """
                    SELECT COUNT(*) FROM information_schema.columns
                    WHERE table_schema = current_schema() AND table_name LIKE @p
                    """;
                var p = cmd.CreateParameter(); p.ParameterName = "p"; p.Value = prefix + "%";
                cmd.Parameters.Add(p);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }

            // SQLite has no columns view; walk the tables and ask each one.
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE @p";
            var q = cmd.CreateParameter(); q.ParameterName = "p"; q.Value = prefix + "%";
            cmd.Parameters.Add(q);
            var tables = new List<string>();
            using (var r = cmd.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));

            var total = 0;
            foreach (var t in tables)
            {
                using var info = cn.CreateCommand();
                info.CommandText = $"PRAGMA table_info(\"{t.Replace("\"", "\"\"")}\")";
                using var r = info.ExecuteReader();
                while (r.Read()) total++;
            }
            return total;
        }
        finally { if (wasClosed) cn.Close(); }
    }

    /// <summary>Whether the SQLite file a connection string names is on disk. In-memory
    /// databases count as present.</summary>
    private static bool SqliteFileExists(string connectionString)
    {
        var source = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        if (source.Length == 0 || source.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return true;
        return File.Exists(source);
    }
}
