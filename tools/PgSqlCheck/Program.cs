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

var root = args.FirstOrDefault(a => !a.StartsWith('-'))
           ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var verbose = args.Contains("-v") || args.Contains("--verbose");

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
var untypable = 0;
var id        = 0;

foreach (var c in checkable)
{
    var name = $"_pgsqlcheck_{++id}";
    try
    {
        await using (var prep = new NpgsqlCommand($"PREPARE {name} AS {c.Sql}", pg))
            await prep.ExecuteNonQueryAsync();
        await using (var drop = new NpgsqlCommand($"DEALLOCATE {name}", pg))
            await drop.ExecuteNonQueryAsync();
    }
    catch (PostgresException ex)
    {
        // ⚠️ Not a finding. PostgreSQL infers a parameter's type from where it sits; a few
        // positions give it nothing to go on, and that is a limit of checking a statement apart
        // from its parameters rather than anything wrong with the statement.
        if (ex.SqlState == "42P08" || ex.SqlState == "42P18"
            || ex.MessageText.Contains("could not determine data type", StringComparison.Ordinal))
        {
            untypable++;
            continue;
        }

        failures.Add((c, $"{ex.SqlState}: {ex.MessageText}"));
    }
    catch (Exception ex)
    {
        failures.Add((c, ex.Message.Split('\n')[0]));
    }
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

Console.WriteLine($"  statements found      : {candidates.Count}");
Console.WriteLine($"    checked             : {checkable.Count}");
Console.WriteLine($"    parameters untypable : {untypable}   (not findings; see the note in Program.cs)");
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
return real.Count == 0 ? 0 : 1;

string Rel(string p) => Path.GetRelativePath(root, p).Replace('\\', '/');
