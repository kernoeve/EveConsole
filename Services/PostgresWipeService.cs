using Npgsql;

namespace EveConsole.Services;

/// <summary>What a wipe removed, so the caller can say so rather than guess.</summary>
public readonly record struct WipeResult(int Tables, int Views, int Sequences)
{
    public int Total => Tables + Views + Sequences;
}

/// <summary>
/// Empties schema <c>public</c> on a server database, so a database that already holds something
/// can still be used as the destination of a copy.
///
/// <para>⚠️ This is the one irreversible thing the application does to a user's data. The copy
/// itself only ever adds, a restore is undone by the dump it came from, but this removes rows that
/// exist nowhere else. It is deliberately not called from anywhere except the copy path, and only
/// behind a typed confirmation.</para>
///
/// <para>Everything in the schema goes, not only the tables this application knows about. Dropping
/// just the current model's tables sounds gentler and is worse: a table left behind by an older
/// version survives, and the next <c>EnsureCreated</c> either trips over it or quietly builds
/// around it. "Empty" is a state somebody can reason about; "empty except for whatever a previous
/// release happened to create" is not.</para>
/// </summary>
public static class PostgresWipeService
{
    /// <summary>
    /// Drops every view, table and sequence in schema <c>public</c>.
    ///
    /// <para>⚠️ One transaction, because PostgreSQL makes DDL transactional and a half-dropped
    /// schema is a worse place to be than either a full one or an empty one. If any drop fails —
    /// most likely because the user does not own an object — nothing is dropped at all and the
    /// error names the object.</para>
    /// </summary>
    public static async Task<WipeResult> WipeAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        await using var tx = await conn.BeginTransactionAsync(ct);

        async Task<List<string>> Names(string sql)
        {
            var found = new List<string>();
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) found.Add(r.GetString(0));
            return found;
        }

        async Task Drop(string kind, IEnumerable<string> names)
        {
            foreach (var name in names)
            {
                ct.ThrowIfCancellationRequested();
                // Catalog names, but quoted and escaped all the same: a table can legally be
                // called anything at all, including something with a quote in it.
                var quoted = '"' + name.Replace("\"", "\"\"") + '"';
                await using var cmd = new NpgsqlCommand(
                    $"DROP {kind} IF EXISTS public.{quoted} CASCADE", conn, tx);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // Views first: dropping a table CASCADE would take its dependent views with it, and then
        // the view list would name things that no longer exist. IF EXISTS covers it either way,
        // but counting what was actually there is easier when nothing vanishes behind our back.
        var views = await Names(
            "SELECT table_name FROM information_schema.views WHERE table_schema = 'public'");
        await Drop("VIEW", views);

        var tables = await Names(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public'");
        await Drop("TABLE", tables);

        // ⚠️ Whatever is left after the tables have gone. An identity column's sequence is owned
        // by its table and disappears with it, so this is only the orphans — but an orphan matters:
        // it holds the name the next EnsureCreated wants for that column, and creation fails on
        // the collision rather than on anything that points at the real cause.
        var sequences = await Names(
            "SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = 'public'");
        await Drop("SEQUENCE", sequences);

        await tx.CommitAsync(ct);
        return new WipeResult(tables.Count, views.Count, sequences.Count);
    }
}
