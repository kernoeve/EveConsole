using System.Data.Common;
using System.Reflection;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace EveConsole.Services;

/// <summary>
/// How far a copy has got.
///
/// <para><paramref name="RowsExpected"/> is every row in the source, counted before the copy
/// began, so a caller can show progress by row rather than by table. Progress by table is close
/// to useless on this schema: KillMailItems alone is over half the database, so the bar would sit
/// on one number for the better part of an hour while the copy was working perfectly.</para>
/// </summary>
public readonly record struct CopyProgress(
    string Table, int TableIndex, int TableCount, long RowsInTable, long RowsTotal,
    long RowsExpected);

/// <summary>
/// Copies every table from one engine into the other, so somebody moving to a server keeps the
/// history they already have.
///
/// <para>Reads through EF and writes through PostgreSQL's binary COPY. Reading through EF is what
/// makes it correct rather than merely fast: SQLite keeps decimals and dates as TEXT, so a raw
/// read hands back strings that would each need parsing back into the type the destination column
/// actually wants. EF's providers already know that mapping in both directions, and having them
/// do it is the difference between a copy that is right and one that is nearly right.</para>
///
/// <para>⚠️ Table order does not matter here, and that is a property of this schema rather than a
/// general truth: it declares no foreign keys, because the relationship between a character and
/// its corporation is enforced in the application. Add one and this needs a topological sort
/// before it will work.</para>
///
/// <para>⚠️ Dates are forced to UTC on the way in. Binary COPY does not pass through the
/// command interceptor that normally does this — nothing here is a DbCommand — and Npgsql
/// rejects any offset but zero.</para>
/// </summary>
public sealed class DatabaseCopyService
{
    /// <summary>How many rows pass before the copy reports progress again.</summary>
    private const int ProgressEvery = 5_000;

    /// <summary>
    /// Copies source into destination. The destination is expected to be empty; this neither
    /// checks nor truncates, because deciding to overwrite somebody's data is the caller's to
    /// make and the UI asks first.
    /// </summary>
    public static async Task<long> CopyAsync(
        Func<AppDbContext> openSource,
        Func<AppDbContext> openDestination,
        string destinationConnectionString,
        IProgress<CopyProgress>? progress,
        CancellationToken ct)
    {
        using var probe = openDestination();
        var entities = probe.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null)
            .OrderBy(e => e.GetTableName(), StringComparer.Ordinal)
            .ToList();

        using var src = openSource();
        src.ChangeTracker.AutoDetectChangesEnabled = false;
        await src.Database.OpenConnectionAsync(ct);

        // ⚠️ ONE transaction, held open across every table, so the whole copy sees the
        // source as it was at a single instant. Each table used to be read on its own connection,
        // which meant the copy was a series of snapshots taken minutes or hours apart: rows the
        // poller wrote while the copy ran could land in one table and not another, and a killmail
        // could arrive with its attackers copied but not its items.
        //
        // SQLite in WAL mode gives readers exactly this for free, and does not block the writer
        // to do it, so the poller keeps working throughout. The cost is that the WAL cannot be
        // checkpointed while the read is open, so it grows by everything written during the copy.
        //
        // Measured on a 4.7 GB database: 475 MB, which WalCheckpointService notices and warns
        // about. It is reclaimed at the next checkpoint once the read closes, so it costs disk
        // for the length of the copy and nothing after. An earlier version of this comment
        // guessed "a few megabytes" and was wrong by two orders of magnitude.
        //
        // This makes the copy CONSISTENT. It does not make it COMPLETE: anything polled after the
        // snapshot is taken stays in the source and is not copied. Only stopping the poller can
        // close that gap, and this deliberately does not reach into it.
        await using var snapshot = await src.Database.BeginTransactionAsync(ct);
        var srcTx = snapshot.GetDbTransaction();

        var modelTables = entities.Select(e => e.GetTableName()!).ToHashSet(StringComparer.Ordinal);
        var extras      = await ExtraTablesAsync(src, srcTx, modelTables, ct);
        var total       = entities.Count + extras.Count;

        // Counted up front so progress can be reported by row. Measured at 0.96 s for 68 million
        // rows across 199 tables, which is nothing against a copy that runs for an hour, and
        // inside the snapshot so the total agrees with what is actually copied.
        var expected = await MeasureAsync(src, srcTx, modelTables.Concat(extras), ct);

        await using var pg = new NpgsqlConnection(destinationConnectionString);
        await pg.OpenAsync(ct);

        long grand = 0;
        var copyOne = typeof(DatabaseCopyService)
            .GetMethod(nameof(CopyTableAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        for (var i = 0; i < entities.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var entity = entities[i];
            var table  = entity.GetTableName()!;

            var task = (Task<long>)copyOne
                .MakeGenericMethod(entity.ClrType)
                .Invoke(null, [src, pg, table, progress, i, total, grand, expected, ct])!;

            var rows = await task;
            grand += rows;

            progress?.Report(new CopyProgress(table, i + 1, total, rows, grand, expected));
        }

        for (var j = 0; j < extras.Count; j++)
        {
            ct.ThrowIfCancellationRequested();

            var table = extras[j];
            var rows  = await CopyExtraTableAsync(src, srcTx, pg, table, ct);
            grand += rows;

            progress?.Report(new CopyProgress(
                table, entities.Count + j + 1, total, rows, grand, expected));
        }

        await ResetIdentitySequencesAsync(pg, ct);
        return grand;
    }

    /// <summary>
    /// The source's tables that the EF model knows nothing about.
    ///
    /// <para>⚠️ The schema is not only what the model describes. Two settings tables are
    /// created by raw SQL during startup and have no entity type, so a loop over
    /// Model.GetEntityTypes() cannot see them — and did not copy them. A migration therefore
    /// looked clean while silently leaving the user's Trade Opportunities exclusions behind, and
    /// nothing said so: the copy reported every row it wrote, and it had written them all.</para>
    ///
    /// <para>That was found by counting rows on both sides afterwards, not by reading the code,
    /// which is why this now works from the SOURCE's own table list. A table added outside the
    /// model in future is copied rather than quietly dropped.</para>
    /// </summary>
    private static async Task<List<string>> ExtraTablesAsync(
        AppDbContext src, DbTransaction tx, IReadOnlySet<string> modelTables, CancellationToken ct)
    {
        var found = new List<string>();

        await using var cmd = src.Database.GetDbConnection().CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' "
                        + "AND name NOT LIKE 'sqlite_%' ORDER BY name";

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (!modelTables.Contains(name)) found.Add(name);
        }

        return found;
    }

    /// <summary>Total rows across the named tables, for a progress bar that means something.</summary>
    private static async Task<long> MeasureAsync(
        AppDbContext src, DbTransaction tx, IEnumerable<string> tables, CancellationToken ct)
    {
        long total = 0;

        foreach (var table in tables)
        {
            ct.ThrowIfCancellationRequested();

            await using var cmd = src.Database.GetDbConnection().CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\"";

            total += Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        return total;
    }

    /// <summary>
    /// Copies one table that has no entity type, column for column, as the source describes it.
    ///
    /// <para>These are small by nature — a settings table with a single row — so this
    /// reads the lot and inserts row by row rather than reaching for COPY. Values go across as
    /// SQLite hands them over, which is safe here because a table outside the model is also
    /// outside the value-converter machinery: it holds the plain scalars it appears to hold.</para>
    /// </summary>
    private static async Task<long> CopyExtraTableAsync(
        AppDbContext src, DbTransaction tx, NpgsqlConnection pg, string table, CancellationToken ct)
    {
        var quoted  = '"' + table.Replace("\"", "\"\"") + '"';
        var columns = new List<string>();
        var rows    = new List<object?[]>();

        await using (var read = src.Database.GetDbConnection().CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = $"SELECT * FROM {quoted}";

            await using var r = await read.ExecuteReaderAsync(ct);
            for (var i = 0; i < r.FieldCount; i++) columns.Add(r.GetName(i));

            while (await r.ReadAsync(ct))
            {
                var values = new object?[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++)
                    values[i] = await r.IsDBNullAsync(i, ct) ? null : r.GetValue(i);
                rows.Add(values);
            }
        }

        if (rows.Count == 0) return 0;

        var cols = string.Join(", ", columns.Select(c => '"' + c.Replace("\"", "\"\"") + '"'));
        var holes = string.Join(", ", columns.Select((_, i) => $"@p{i}"));

        foreach (var values in rows)
        {
            ct.ThrowIfCancellationRequested();

            await using var write = new NpgsqlCommand(
                $"INSERT INTO public.{quoted} ({cols}) VALUES ({holes})", pg);

            for (var i = 0; i < values.Length; i++)
                write.Parameters.AddWithValue($"p{i}", values[i] ?? DBNull.Value);

            await write.ExecuteNonQueryAsync(ct);
        }

        return rows.Count;
    }

    /// <summary>
    /// Moves every identity sequence past the largest key that was copied.
    ///
    /// <para>⚠️ Without this the copy looks perfect and the first new row fails. An identity
    /// column declared GENERATED BY DEFAULT accepts the explicit ids a copy supplies, but nothing
    /// tells its sequence they were used — it is still at 1, so the next insert that lets the
    /// database choose picks a key that already exists. The failure lands later, on ordinary use,
    /// far from the migration that caused it.</para>
    /// </summary>
    private static async Task ResetIdentitySequencesAsync(NpgsqlConnection pg, CancellationToken ct)
    {
        const string sql = """
            SELECT quote_ident(c.table_name), quote_ident(c.column_name)
            FROM information_schema.columns c
            WHERE c.table_schema = 'public' AND c.is_identity = 'YES'
            """;

        var targets = new List<(string Table, string Column)>();
        await using (var find = new NpgsqlCommand(sql, pg))
        await using (var r = await find.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                targets.Add((r.GetString(0), r.GetString(1)));

        foreach (var (table, column) in targets)
        {
            ct.ThrowIfCancellationRequested();

            // setval(..., false) means "the next value is this one", so an empty table is left
            // starting at 1 rather than skipping it.
            var bare = column.Trim('"').Replace("'", "''");
            var bump = $"""
                SELECT setval(
                    pg_get_serial_sequence('public.{table}', '{bare}'),
                    COALESCE((SELECT MAX({column}) FROM public.{table}), 0) + 1,
                    false)
                """;

            await using var cmd = new NpgsqlCommand(bump, pg);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task<long> CopyTableAsync<T>(
        AppDbContext src,
        NpgsqlConnection pg,
        string table,
        IProgress<CopyProgress>? progress,
        int index,
        int total,
        long runningTotal,
        long expected,
        CancellationToken ct) where T : class
    {
        var entity = src.Model.FindEntityType(typeof(T))!;

        // Only real, readable columns. A shadow property has no PropertyInfo to read from, and
        // this model has none; if one appears it must be handled rather than silently skipped.
        // Paired with the converter EF would use for each one. A property's CLR type is not
        // always what the column holds: an enum is stored as an int, and any property with a
        // value converter is stored as whatever that converter produces.
        var props = entity.GetProperties()
            .Where(p => p.PropertyInfo is not null)
            .Select(p => (Property: p, Converter: p.GetTypeMapping().Converter))
            .ToList();

        var columns = string.Join(", ", props.Select(p => $"\"{p.Property.GetColumnName()}\""));
        var copySql = $"COPY \"{table}\" ({columns}) FROM STDIN (FORMAT BINARY)";

        long written = 0;

        await using var writer = await pg.BeginBinaryImportAsync(copySql, ct);

        // ⚠️ Streamed in ONE query rather than paged with Skip/Take. Offset paging makes a big
        // table quadratic: SQLite walks every skipped row before it can return a page, so the page
        // starting at seven million steps over seven million rows to collect five thousand.
        // Measured on KillMailAttackers, 9.6M rows: a page costs 0.02 s at the start and 0.41 s at
        // the end, against a flat 0.009 s streamed — about 9.2 billion row-steps to read 9.6
        // million rows. It is invisible on a small table, and on a large one it presents as that
        // one table mysteriously slowing down as it goes, which is how it was found.
        //
        // The ordering that used to be here existed only to make the pages a stable window. With
        // no pages there is no window to keep stable, and a copy does not care what order rows
        // arrive in. Nothing accumulates either: the read is no-tracking, so the change tracker
        // this used to clear between pages never fills.
        var rows = src.Set<T>().AsNoTracking().AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.StartRowAsync(ct);
            foreach (var (property, converter) in props)
            {
                var value = property.PropertyInfo!.GetValue(row);

                // ⚠️ Through EF's own converter, not straight from the property. Writing
                // the CLR value directly failed on the first enum it met — "Writing values
                // of 'AlarmActionKind' is not supported for parameters having no NpgsqlDbType"
                // — because the column holds an int and the property does not. Asking EF
                // what the provider value is covers enums and every other converted type at
                // once, rather than special-casing them one failure at a time.
                if (converter is not null) value = converter.ConvertToProvider(value);

                // See the class remarks: COPY bypasses the interceptor, so UTC is enforced here.
                value = value switch
                {
                    DateTimeOffset dto when dto.Offset != TimeSpan.Zero => dto.ToUniversalTime(),
                    DateTime dt when dt.Kind == DateTimeKind.Local      => dt.ToUniversalTime(),
                    DateTime dt when dt.Kind == DateTimeKind.Unspecified
                        => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
                    _ => value,
                };

                // ⚠️ A belt for the braces above: an enum with no explicit converter still
                // reaches here as an enum, and Npgsql cannot infer a type for it. Unboxing to
                // the underlying integral type is what the column expects anyway.
                if (value is Enum boxed)
                    value = Convert.ChangeType(boxed, Enum.GetUnderlyingType(boxed.GetType()));

                if (value is null) await writer.WriteNullAsync(ct);
                else               await writer.WriteAsync(value, ct);
            }

            if (++written % ProgressEvery == 0)
                progress?.Report(new CopyProgress(
                    table, index, total, written, runningTotal + written, expected));
        }

        await writer.CompleteAsync(ct);
        return written;
    }
}
