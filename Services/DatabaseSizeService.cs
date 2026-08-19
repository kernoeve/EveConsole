using Microsoft.Data.Sqlite;

namespace EveConsole.Services;

/// <summary>What one table costs on disk, table data and its indexes counted separately.</summary>
public sealed record TableSizeRow(
    string Name,
    long   Rows,
    long   TableBytes,
    long   IndexBytes,
    int    IndexCount,
    bool   Estimated)
{
    public long TableTotalBytes => TableBytes + IndexBytes;
}

/// <summary>
/// The whole picture: what the file actually occupies, and where it went.
/// </summary>
/// <param name="FileBytes">Pages × page size — the real file, free space included.</param>
/// <param name="FreeBytes">Pages on the freelist — exact, but ⚠️ a floor rather than "space you
/// could get back": it counts only wholly empty pages, and deleting rows mostly leaves holes
/// inside pages that stay in use. Not surfaced in the UI for that reason.</param>
public sealed record DatabaseSizeReport(
    long FileBytes,
    long UsedBytes,
    long FreeBytes,
    int  PageSize,
    IReadOnlyList<TableSizeRow> Tables);

/// <summary>
/// Measures which tables the database file is spent on.
///
/// <para>⚠️ SQLite answers this exactly through the <c>dbstat</c> virtual table, but that needs
/// <c>SQLITE_ENABLE_DBSTAT_VTAB</c> at compile time and the <c>e_sqlite3</c> build shipped with
/// Microsoft.Data.Sqlite does not have it — <c>PRAGMA compile_options</c> confirms. Swapping the
/// native provider, or shipping <c>sqlite3_analyzer.exe</c> and parsing its output, would buy
/// page-level precision that no retention decision actually needs.</para>
///
/// <para>So this measures row payload by scanning, and then <b>calibrates</b>: the per-table
/// numbers are scaled so their total equals the file's genuinely used pages
/// (<c>page_count − freelist_count</c>). That converts a ranking into absolute figures that
/// reconcile with the file on disk, without pretending to a precision it does not have — the
/// scaling absorbs per-page overhead, which is roughly proportional to content anyway.</para>
///
/// <para>Index size is estimated the same way, from the key columns plus the row reference. On a
/// database like this one it is the larger half for the killmail tables, so leaving it out would
/// point retention at the wrong place.</para>
/// </summary>
public sealed class DatabaseSizeService
{
    /// <summary>Beyond this a table is sampled rather than read in full.</summary>
    private const int FullScanMax = 100_000;

    /// <summary>Rows to sample from a large table — half from each end, because rows tend to grow
    /// over time and reading only the oldest would understate a table that is getting wider.</summary>
    private const int SampleRows = 20_000;

    /// <summary>Per-row cell overhead SQLite adds beyond the payload itself: the record header,
    /// the cell pointer and the row length varint. An approximation, and one the calibration step
    /// largely corrects for.</summary>
    private const int RowOverhead = 12;

    /// <summary>An index entry carries the key columns plus a reference back to the row.</summary>
    private const int IndexEntryOverhead = 14;

    public Task<DatabaseSizeReport> AnalyseAsync(
        string dbPath, IProgress<string>? progress = null, CancellationToken ct = default)
        => Task.Run(() => Analyse(dbPath, progress, ct), ct);

    private static DatabaseSizeReport Analyse(
        string dbPath, IProgress<string>? progress, CancellationToken ct)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        var pageSize  = (int)Scalar(conn, "PRAGMA page_size");
        var pageCount = Scalar(conn, "PRAGMA page_count");
        var freeList  = Scalar(conn, "PRAGMA freelist_count");

        var fileBytes = pageCount * pageSize;
        var freeBytes = freeList  * pageSize;
        var usedBytes = fileBytes - freeBytes;

        var tables = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        var raw   = new List<TableSizeRow>(tables.Count);
        var done  = 0;

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Measuring {table} ({++done} of {tables.Count})…");

            try { raw.Add(Measure(conn, table, ct)); }
            catch (OperationCanceledException) { throw; }
            catch { /* a table we cannot read tells us nothing; skip it rather than fail the run */ }
        }

        // Calibrate to the file. Everything measured is payload; the difference between that and
        // the used pages is overhead we cannot see, spread proportionally.
        var measured = raw.Sum(t => t.TableTotalBytes);
        var scale    = measured > 0 ? (double)usedBytes / measured : 1.0;

        var scaled = raw
            .Select(t => t with
            {
                TableBytes = (long)(t.TableBytes * scale),
                IndexBytes = (long)(t.IndexBytes * scale),
            })
            .OrderByDescending(t => t.TableTotalBytes)
            .ToList();

        return new DatabaseSizeReport(fileBytes, usedBytes, freeBytes, pageSize, scaled);
    }

    private static TableSizeRow Measure(SqliteConnection conn, string table, CancellationToken ct)
    {
        var rows = Scalar(conn, $"SELECT COUNT(*) FROM {Quote(table)}");

        var columns = Columns(conn, table);
        if (rows == 0 || columns.Count == 0)
            return new TableSizeRow(table, rows, 0, 0, Indexes(conn, table).Count, false);

        var estimated  = rows > FullScanMax;
        var tableBytes = PayloadOf(conn, table, columns, rows, estimated, ct)
                       + rows * RowOverhead;

        long indexBytes = 0;
        var  indexes    = Indexes(conn, table);
        foreach (var (indexName, keyColumns) in indexes)
        {
            ct.ThrowIfCancellationRequested();
            if (keyColumns.Count == 0) continue;

            indexBytes += PayloadOf(conn, table, keyColumns, rows, estimated, ct)
                        + rows * IndexEntryOverhead;
        }

        return new TableSizeRow(table, rows, tableBytes, indexBytes, indexes.Count, estimated);
    }

    /// <summary>
    /// Total payload of the named columns across the table, read in full when the table is small
    /// enough and extrapolated from a two-ended sample when it is not.
    /// </summary>
    private static long PayloadOf(
        SqliteConnection conn, string table, IReadOnlyList<string> columns,
        long rows, bool sampled, CancellationToken ct)
    {
        var expr = string.Join(" + ",
            columns.Select(c => $"COALESCE(LENGTH(CAST({Quote(c)} AS BLOB)),0)"));

        if (!sampled)
            return Scalar(conn, $"SELECT COALESCE(SUM({expr}),0) FROM {Quote(table)}");

        var half = SampleRows / 2;
        var sql  = $"""
            SELECT COALESCE(SUM(len),0), COUNT(*) FROM (
                SELECT ({expr}) AS len FROM {Quote(table)} LIMIT {half}
                UNION ALL
                SELECT ({expr}) AS len FROM (
                    SELECT * FROM {Quote(table)} ORDER BY _rowid_ DESC LIMIT {half}))
            """;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText    = sql;
            cmd.CommandTimeout = 600;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return 0;

            var sum   = r.IsDBNull(0) ? 0L : r.GetInt64(0);
            var taken = r.IsDBNull(1) ? 0L : r.GetInt64(1);
            return taken == 0 ? 0 : (long)(sum / (double)taken * rows);
        }
        catch (SqliteException)
        {
            // WITHOUT ROWID tables have no _rowid_; the leading sample alone is still indicative.
            var sum = Scalar(conn,
                $"SELECT COALESCE(SUM({expr}),0) FROM (SELECT * FROM {Quote(table)} LIMIT {SampleRows})");
            var taken = Math.Min(SampleRows, rows);
            return taken == 0 ? 0 : (long)(sum / (double)taken * rows);
        }
    }

    private static List<string> Columns(SqliteConnection conn, string table)
    {
        var cols = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({Quote(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    /// <summary>Every index on the table with its key columns — including the implicit ones SQLite
    /// creates for UNIQUE constraints, which occupy real pages like any other.</summary>
    private static List<(string Name, List<string> KeyColumns)> Indexes(SqliteConnection conn, string table)
    {
        var names = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({Quote(table)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(1));
        }

        var result = new List<(string, List<string>)>(names.Count);
        foreach (var name in names)
        {
            var keys = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA index_info({Quote(name)})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(2)) keys.Add(r.GetString(2));   // null = an expression, not a column
            result.Add((name, keys));
        }
        return result;
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText    = sql;
        cmd.CommandTimeout = 600;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    /// <summary>Identifiers come from the schema, not from input, but quoting them keeps a table
    /// named after a keyword from breaking the query.</summary>
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
}
