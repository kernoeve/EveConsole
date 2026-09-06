using System.Text.RegularExpressions;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

// ─────────────────────────────────────────────────────────────────────────────
//  Postgres schema check
//
//  Two questions, neither needing a server.
//
//  1. Does the entity model map cleanly to PostgreSQL? EF builds the CREATE
//     script from the model alone — no connection is opened — so every entity,
//     property type, key and index goes through Npgsql's type mapper here. A CLR
//     type SQLite tolerated and Npgsql cannot map fails at this point rather than
//     on a user's first launch.
//
//  2. Has the hand-written schema drifted? The SQLite path in App.axaml.cs and
//     PostgresSchema are kept in step by hand, so an index added to one and not
//     the other is the easiest mistake to make and the hardest to notice: it
//     works for every existing user and silently does not exist for Postgres
//     ones. Missing indexes do not fail, they just make the app look broken —
//     IX_KillMailAttackers_Corp is worth ten minutes on a corp Kills tab.
//
//  SCOPE. Neither question touches the ~70 hand-written SQL statements in the
//  services, which is where the remaining dialect differences live. A green
//  result here is narrower than it may look, in the same way FreshInstallCheck's
//  is.
// ─────────────────────────────────────────────────────────────────────────────

var root = args.FirstOrDefault(a => !a.StartsWith('-')) is { } given && Directory.Exists(given)
    ? given
    : FindRepoRoot();

var failures = 0;

// ── 1. The model, through Npgsql's mapper ───────────────────────────────────
try
{
    // Not connected to, and deliberately not connectable: the script comes from the model.
    const string designTime = "Host=schema.check.invalid;Database=eveconsole;Username=none";
    var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(designTime).Options;
    using var db = new AppDbContext(opts);

    var script = db.Database.GenerateCreateScript();
    var outPath = Path.Combine(Path.GetTempPath(), "eveconsole-postgres-schema.sql");
    File.WriteAllText(outPath, script);

    Console.WriteLine("Model maps to PostgreSQL cleanly.");
    Console.WriteLine($"  tables  : {Count(script, "CREATE TABLE")}");
    Console.WriteLine($"  indexes : {Count(script, "CREATE INDEX") + Count(script, "CREATE UNIQUE INDEX")}");
    Console.WriteLine($"  script  : {outPath} ({script.Length:N0} bytes)");

    // The bootstrap creates these two by hand; they are not entities, so they must NOT appear
    // here. If one does, it has been added to the model and the hand-written copy is now a
    // second, diverging definition of the same table.
    foreach (var orphan in new[] { "IndustryOpportunitiesSettings", "TradeOpportunitiesSettings" })
        if (script.Contains($"\"{orphan}\"", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"  DRIFT: {orphan} is now an entity — remove it from PostgresSchema.Tables.");
            failures++;
        }
}
catch (Exception ex)
{
    Console.Error.WriteLine("The model does NOT map to PostgreSQL:");
    Console.Error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is { } inner) Console.Error.WriteLine($"  inner: {inner.Message}");
    return 1;
}

// ── 2. Index parity between the two bootstraps ──────────────────────────────
if (root is null)
{
    Console.Error.WriteLine("\nCould not locate the repo root, so index parity was NOT checked.");
    return failures == 0 ? 0 : 1;
}

var appSource = File.ReadAllText(Path.Combine(root, "App.axaml.cs"));

// Matches the statements wherever they sit: some are their own ExecuteSqlRaw call, others are
// elements of a `foreach (var sql in new[] { … })`.
var pattern = new Regex(
    """CREATE\s+(?:UNIQUE\s+)?INDEX\s+IF\s+NOT\s+EXISTS\s+"(?<name>[^"]+)"\s*ON\s+"[^"]+"\s*\([^)]*\)""",
    RegexOptions.Singleline);

var inSqlite   = pattern.Matches(appSource).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
var inPostgres = PostgresSchema.Indexes
    .Select(s => pattern.Match(s))
    .Where(m => m.Success)
    .Select(m => m.Groups["name"].Value)
    .ToHashSet(StringComparer.Ordinal);

Console.WriteLine($"\nIndex parity: {inSqlite.Count} in the SQLite bootstrap, {inPostgres.Count} in PostgresSchema.");

foreach (var missing in inSqlite.Except(inPostgres).Order())
{
    Console.Error.WriteLine($"  DRIFT: {missing} is created for SQLite but not for PostgreSQL.");
    failures++;
}
foreach (var extra in inPostgres.Except(inSqlite).Order())
{
    Console.Error.WriteLine($"  DRIFT: {extra} is created for PostgreSQL but not for SQLite.");
    failures++;
}

if (failures == 0) Console.WriteLine("  no drift.");

failures += EveConsole.Tools.PgSchemaCheck.CopyTypes.Check();

// ── 3. Against a real server, when one is offered ───────────────────────────
//
// ⚠️ Taken from the environment rather than an argument: a connection string carries a
// password, and an argument would land in shell history and CI logs.
if (Environment.GetEnvironmentVariable("EVECONSOLE_PG") is { Length: > 0 } live)
    failures += await EveConsole.Tools.PgSchemaCheck.Live.RunAsync(live);
else
    Console.WriteLine("\nNo EVECONSOLE_PG set, so nothing was executed against a server.");

return failures == 0 ? 0 : 1;

static int Count(string haystack, string needle)
{
    int n = 0, i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "EveConsole.csproj"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
