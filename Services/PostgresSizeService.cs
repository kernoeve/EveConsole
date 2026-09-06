using Npgsql;

namespace EveConsole.Services;

/// <summary>
/// The same storage breakdown as <see cref="DatabaseSizeService"/>, asked of a server.
///
/// <para>Much simpler than the SQLite side, and better. That one has to estimate: <c>dbstat</c>
/// needs a compile-time flag the shipped SQLite build lacks, so it measures rows, applies
/// per-row overheads and calibrates the total against the file. PostgreSQL keeps the real figures
/// and hands them over — <c>pg_table_size</c> and <c>pg_indexes_size</c> are exact, including
/// each table's TOAST storage and free space map.</para>
///
/// <para>⚠️ Row counts are the one estimate here, and in the other direction from SQLite's. They
/// come from <c>n_live_tup</c>, which autovacuum maintains, so a table written heavily since the
/// last analyze reads low. Counting exactly would mean a sequential scan of every table, which on
/// the large ones costs far more than the answer is worth on a settings screen.</para>
/// </summary>
public sealed class PostgresSizeService
{
    public Task<DatabaseSizeReport> AnalyseAsync(
        string connectionString, IProgress<string>? progress = null, CancellationToken ct = default)
        => AnalyseCoreAsync(connectionString, progress, ct);

    private static async Task<DatabaseSizeReport> AnalyseCoreAsync(
        string connectionString, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Reading table sizes…");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        async Task<long> ScalarLong(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            var v = await cmd.ExecuteScalarAsync(ct);
            return v is null or DBNull ? 0 : Convert.ToInt64(v);
        }

        var databaseBytes = await ScalarLong("SELECT pg_database_size(current_database())");
        var pageSize      = (int)await ScalarLong("SELECT current_setting('block_size')::bigint");

        const string sql = """
            SELECT c.relname,
                   COALESCE(s.n_live_tup, 0)                                        AS row_estimate,
                   pg_table_size(c.oid)                                             AS table_bytes,
                   pg_indexes_size(c.oid)                                           AS index_bytes,
                   (SELECT count(*) FROM pg_index i WHERE i.indrelid = c.oid)       AS index_count
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
            WHERE n.nspname = 'public' AND c.relkind = 'r'
            ORDER BY pg_total_relation_size(c.oid) DESC
            """;

        var tables = new List<TableSizeRow>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                ct.ThrowIfCancellationRequested();
                tables.Add(new TableSizeRow(
                    Name:       r.GetString(0),
                    Rows:       r.GetInt64(1),
                    TableBytes: r.GetInt64(2),
                    IndexBytes: r.GetInt64(3),
                    IndexCount: (int)r.GetInt64(4),
                    Estimated:  true));   // see the remarks: n_live_tup, not count(*)
            }
        }

        var usedBytes = tables.Sum(t => t.TableTotalBytes);

        // ⚠️ The difference is not "free space you could reclaim". pg_database_size counts the
        // system catalogs and anything outside the public schema as well, so the remainder is
        // mostly other people's bookkeeping rather than slack. Reported for completeness and, as
        // on the SQLite side, not shown as reclaimable.
        var freeBytes = Math.Max(0, databaseBytes - usedBytes);

        progress?.Report($"Read {tables.Count:N0} tables.");
        return new DatabaseSizeReport(databaseBytes, usedBytes, freeBytes, pageSize, tables);
    }
}
