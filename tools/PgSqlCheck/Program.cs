using System.Text.RegularExpressions;
using EveConsole.Tools.PgSqlCheck;
using Npgsql;

// Asks PostgreSQL to parse and typecheck every hand-written statement in the application, without
// running any of them. PREPARE resolves the tables, the columns, the operators, the function
// overloads and the GROUP BY rules against the real schema, then the plan is discarded.
//
// ⚠️ It finds only what PostgreSQL rejects outright. A statement that is valid SQL and still
// wrong — LIKE that should be ILIKE, CAST(x AS REAL) quietly narrowing a double, COUNT(*) read
// into a C# int — parses cleanly and is invisible here. Those are the classes this cannot cover,
// and pretending otherwise would be worse than not having it.
//
// --collisions then asks what the SQL would mean on a LATER schema. The SDE import adds a column
// to an Sde table for every field CCP adds to a file (SdeCompleteness), on the user's machine,
// with no release of ours in between — so a statement that names a column bare beside an Sde
// table is one CCP field away from breaking, and no schema CI can build shows it. The pass gives
// every Sde table a column for every name the code quotes, then PREPAREs everything again:
//
//   - two tables in scope with the name → "ambiguous", which is what the user would get;
//   - a subquery whose bare name meant the OUTER table now quietly reads the Sde table's new
//     column. That is no error at all on the real schema, so the probe columns are of a type
//     nothing compares with, adds to or reads as text: the rebinding becomes a type error here,
//     or a result column of the probe type when it is only selected.
//
// ⚠️ It ALTERs tables. All of it is inside one transaction that is rolled back, but the tables
// are locked while it runs and a read-only session cannot do it: point it at a scratch database
// (CI uses the one the headless worker has just built from empty), never at one in use.

var root = args.FirstOrDefault(a => !a.StartsWith('-'))
           ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var verbose = args.Contains("-v") || args.Contains("--verbose");
var collisions = args.Contains("--collisions");

var cs = Environment.GetEnvironmentVariable("EVECONSOLE_PG");
if (string.IsNullOrWhiteSpace(cs))
{
    Console.Error.WriteLine("""
        Set EVECONSOLE_PG to a PostgreSQL connection string carrying the application's schema.

        Nothing is written and nothing is executed — every statement is PREPAREd and thrown away
        — but it must be a database whose tables and columns match the code being checked, so a
        scratch database restored from a dump is ideal.
        """);
    return 2;
}

var dirs = new[] { "Services", "ViewModels", "Agent", "Alarms", "Data" }
    .Select(d => Path.Combine(root, d))
    .Where(Directory.Exists)
    .ToList();

if (dirs.Count == 0)
{
    Console.Error.WriteLine($"No source directories found under {root}.");
    return 2;
}

// ⚠️ These run against the SQLite file and only ever will — maintenance, shrink, relocation,
// the copy's own reader. Their SQL is correctly SQLite's, sqlite_master and all, so checking it
// against PostgreSQL reports five failures that are the code working as intended.
var sqliteOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "DatabaseIntegrityService.cs", "DatabaseRelocationService.cs", "DatabaseShrinkService.cs",
    "DatabaseSizeService.cs", "SqliteMaintenance.cs", "WalCheckpointService.cs",
    "DisableForeignKeysInterceptor.cs", "DatabaseCopyService.cs",
};

var candidates = dirs
    .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
             && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
             && !sqliteOnly.Contains(Path.GetFileName(f)))
    .SelectMany(SqlExtractor.FromFile)
    .ToList();

await using var pg = new NpgsqlConnection(cs);
await pg.OpenAsync();

var checkable = candidates.Where(c => c.Unusable is null).ToList();
var skipped   = candidates.Where(c => c.Unusable is not null).ToList();

var failures  = new List<(Candidate C, string Error)>();
var id        = 0;
// What each sound statement returns, so the collision pass can tell a result column that now
// reads a probe column from one that was always there.
var shapes    = new Dictionary<Candidate, (string Types, int Count)>();

foreach (var c in checkable)
{
    var name = $"_pgsqlcheck_{++id}";
    try
    {
        await using (var prep = new NpgsqlCommand($"PREPARE {name} AS {c.Sql}", pg))
            await prep.ExecuteNonQueryAsync();
        shapes[c] = await ShapeOfAsync(name);
        await using (var drop = new NpgsqlCommand($"DEALLOCATE {name}", pg))
            await drop.ExecuteNonQueryAsync();
    }
    catch (PostgresException ex)
    {
        // ⚠️ "Could not determine data type" is a FINDING, not an artefact — the first version of
        // this tool skipped it on the reasoning that checking a statement apart from its
        // parameters is bound to leave some positions uninferable. That reasoning was wrong, and
        // the code proved it: Corp Activity's income and expense tabs failed at runtime with
        // exactly this, "42P18: could not determine data type of parameter $3", from
        //
        //     AND ({type} IS NULL OR "RefType" = {type})
        //
        // PostgreSQL has nothing to infer from in "$3 IS NULL", and the application sends that
        // parameter with no declared type either, so what PREPARE cannot resolve here the server
        // cannot resolve there. Skipping it hid two live bugs and six more in the agent tools.
        failures.Add((c, $"{ex.SqlState}: {ex.MessageText}"));
    }
    catch (Exception ex)
    {
        failures.Add((c, ex.Message.Split('\n')[0]));
    }
}

var collided = new List<(Candidate C, string Error)>();
if (collisions)
{
    var code = await CollisionPassAsync();
    if (code != 0) return code;
}

// ⚠️ Syntax errors are separated out, and it is not squeamishness. A statement with a real
// syntax error fails every single time it runs — it could never have worked on SQLite either, so
// it is not the kind of thing that lurks in a screen nobody has opened. What can lurk is a
// statement that parses everywhere and means something different here: a bare column beside a
// GROUP BY, a function with no PostgreSQL overload, a UNION whose branches disagree. So a 42601
// almost always means this tool reassembled the SQL imperfectly, while a semantic error almost
// always means the code is wrong. Mixing them buries three real findings under ten artefacts.
static bool IsSyntax(string e) =>
    e.StartsWith("42601", StringComparison.Ordinal) ||
    e.StartsWith("42P01", StringComparison.Ordinal);      // an unresolved CTE from another literal

var real     = failures.Where(f => !IsSyntax(f.Error)).ToList();
var artefact = failures.Where(f =>  IsSyntax(f.Error)).ToList();

void Show(IEnumerable<(Candidate C, string Error)> list)
{
    foreach (var (c, error) in list.OrderBy(f => f.C.File).ThenBy(f => f.C.Line))
    {
        Console.WriteLine($"  {Rel(c.File)}:{c.Line}");
        Console.WriteLine($"      {error}");
        if (verbose)
            Console.WriteLine("      | " + string.Join("\n      | ",
                c.Sql.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(12)));
        Console.WriteLine();
    }
}

Console.WriteLine();
if (real.Count > 0)
{
    Console.WriteLine($"FINDINGS — PostgreSQL parses these and rejects what they mean ({real.Count})\n");
    Show(real);
}

if (artefact.Count > 0)
{
    Console.WriteLine($"probably this tool's fault — syntax errors, which real SQL would hit on "
                    + $"every run ({artefact.Count})\n");
    Show(artefact);
}

if (collisions)
{
    if (collided.Count > 0)
    {
        Console.WriteLine($"COLLISIONS — these break once the SDE import adds a column of the same "
                        + $"name to an Sde table ({collided.Count})\n");
        Show(collided);
    }
    else
        Console.WriteLine("  no collisions: no checked statement changes meaning when an Sde table gains a column.\n");
}

Console.WriteLine($"  statements found      : {candidates.Count}");
Console.WriteLine($"    checked             : {checkable.Count}");
Console.WriteLine($"    not checkable        : {skipped.Count}   (assembled from SQL fragments)");
Console.WriteLine();
Console.WriteLine(real.Count == 0
    ? "  no semantic findings: every checked statement means on PostgreSQL what it says."
    : $"  {real.Count} finding(s) to fix.");

if (verbose && skipped.Count > 0)
{
    Console.WriteLine("\n  not checkable:");
    foreach (var s in skipped.OrderBy(s => s.File).ThenBy(s => s.Line))
        Console.WriteLine($"    {Rel(s.File)}:{s.Line}  {s.Unusable}");
}

// Only the semantic findings fail the run. The artefacts are this tool's reconstruction, and a
// checker that reports its own limitations as build failures is one nobody keeps in CI.
return real.Count == 0 && collided.Count == 0 ? 0 : 1;

string Rel(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');

async Task<(string Types, int Count)> ShapeOfAsync(string prepared)
{
    await using var q = new NpgsqlCommand(
        "SELECT coalesce(result_types::text, ''), coalesce(cardinality(result_types), 0) " +
        "FROM pg_prepared_statements WHERE name = @n", pg);
    q.Parameters.AddWithValue("n", prepared);
    await using var r = await q.ExecuteReaderAsync();
    return await r.ReadAsync() ? (r.GetString(0), r.GetInt32(1)) : ("", 0);
}

async Task Exec(string sql)
{
    await using var cmd = new NpgsqlCommand(sql, pg);
    await cmd.ExecuteNonQueryAsync();
}

async Task<int> CollisionPassAsync()
{
    // The name the errors print: "operator does not exist: "SdeColumnAddedLater" = integer" says
    // what happened without this tool's help.
    const string Probe = "SdeColumnAddedLater";

    // Every name the code quotes, whatever it names here — a column of another table, a CTE's
    // output, an alias that GROUP BY would prefer an input column over. A field CCP adds can be
    // spelled like any of them, and only those can change what this code means.
    var quoted = candidates
        .SelectMany(c => Regex.Matches(c.Sql, "\"([^\"]{1,63})\"").Select(m => m.Groups[1].Value))
        .ToHashSet(StringComparer.Ordinal);

    var columns = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    await using (var q = new NpgsqlCommand("""
        SELECT c.table_name, c.column_name
        FROM information_schema.columns c
        JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE c.table_schema = current_schema() AND t.table_type = 'BASE TABLE'
        """, pg))
    await using (var r = await q.ExecuteReaderAsync())
        while (await r.ReadAsync())
        {
            if (!columns.TryGetValue(r.GetString(0), out var set))
                columns[r.GetString(0)] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(r.GetString(1));
        }

    var sde = columns.Keys.Where(t => t.StartsWith("Sde", StringComparison.Ordinal)).OrderBy(t => t).ToList();
    if (sde.Count == 0)
    {
        Console.Error.WriteLine("--collisions: this database has no Sde tables, so there is nothing to probe.");
        return 2;
    }

    await using var tx = await pg.BeginTransactionAsync();
    try
    {
        // ⚠️ Never wait behind anybody: a database something else is using is the wrong one.
        await Exec("SET LOCAL lock_timeout = '5s'");
        await Exec($"CREATE TYPE \"{Probe}\" AS (never_read integer)");
    }
    catch (PostgresException ex)
    {
        Console.Error.WriteLine($"--collisions needs a scratch database it may alter inside a transaction "
                              + $"it rolls back; this one refused: {ex.SqlState}: {ex.MessageText}");
        return 2;
    }

    var added = 0;
    foreach (var table in sde)
    {
        var add = quoted.Where(n => !columns[table].Contains(n) && !columns.ContainsKey(n)).OrderBy(n => n).ToList();
        // PostgreSQL's 1,600 columns a table, with room for what the import itself still adds.
        if (columns[table].Count + add.Count > 1500)
        {
            Console.Error.WriteLine($"--collisions: {table} would need {add.Count} probe columns beside its "
                                  + $"{columns[table].Count}, past PostgreSQL's limit.");
            return 2;
        }
        if (add.Count == 0) continue;
        await Exec($"ALTER TABLE \"{table}\" " +
                   string.Join(", ", add.Select(n => $"ADD COLUMN \"{n}\" \"{Probe}\"")));
        added += add.Count;
    }

    var n2 = 0;
    foreach (var c in checkable.Where(shapes.ContainsKey))
    {
        var name = $"_pgsqlcollide_{++n2}";
        await Exec("SAVEPOINT probe");
        try
        {
            await using (var prep = new NpgsqlCommand($"PREPARE {name} AS {c.Sql}", pg))
                await prep.ExecuteNonQueryAsync();
            var now    = await ShapeOfAsync(name);
            var before = shapes[c];
            await Exec($"DEALLOCATE {name}");
            await Exec("RELEASE SAVEPOINT probe");
            // ⚠️ Same number of result columns, one of them now the probe's: a bare name in the
            // select list that reads the new column. More columns is a SELECT * growing, which
            // reads by name and is not this.
            if (now.Count == before.Count && now.Types != before.Types && now.Types.Contains(Probe))
                collided.Add((c, $"a result column now reads the new Sde column: {before.Types} -> {now.Types}"));
        }
        catch (PostgresException ex)
        {
            await Exec("ROLLBACK TO SAVEPOINT probe");
            collided.Add((c, ex.SqlState == "42702"
                ? $"42702 ambiguous: {ex.MessageText}"
                : $"{ex.SqlState}: {ex.MessageText} (a bare name now binds to the new Sde column)"));
        }
    }

    await tx.RollbackAsync();
    Console.WriteLine($"  collision pass        : {sde.Count} Sde tables, {added:N0} probe columns, "
                    + $"{n2} statements re-checked, all rolled back");
    return 0;
}
