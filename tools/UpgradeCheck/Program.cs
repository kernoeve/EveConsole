using EveConsole.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.RegularExpressions;

// ─────────────────────────────────────────────────────────────────────────────
//  Upgrade check
//
//  Answers the question FreshInstallCheck cannot: would this build work on a database
//  that ALREADY EXISTS?
//
//  The two are mirror images. On a new file EF's EnsureCreated() builds the entire model,
//  so nothing is ever missing and every upgrade fault is invisible. On an existing file
//  EnsureCreated() does nothing at all, and the schema is only ever what the hand-written
//  CREATE and ALTER statements have remembered to add. A column added to the model and to
//  no list is therefore perfect on every developer's fresh test install and absent for
//  every real user.
//
//  v0.9.13 shipped exactly that: 81 columns and two tables reached the model, PostgresSchema
//  was updated and neither SQLite list was, and an existing database lost Wallet, Sales
//  Tracker, Order Tracker and the Worklist to "no such column" — while Update SDE, the one
//  action that repairs the schema, died on the missing table before it got there.
//
//  HOW. A database is built from a PREVIOUS RELEASE's statements, read out of that tag with
//  `git show` as text — the old revision is never compiled. This build's statements are then
//  applied to it, exactly as they would be on a user's machine, and the result is compared
//  against a database EnsureCreated() builds from today's model. Anything the model has and
//  the upgraded database lacks is a column some real user is missing.
//
//  ⚠️ Building the baseline from the tag's own CREATE statements is the whole point, and a
//  simpler design gets this wrong. The hand-written CREATEs are kept CURRENT — SdeGroups
//  lists Anchorable in its CREATE and also has an ALTER for it — so a database built from
//  today's CREATEs already has every column and the check passes vacuously. It would then
//  be blind to the one mistake worth catching: a column added to the model and the CREATE
//  with no ALTER written, which is invisible to new installs and fatal to old ones.
//
//  SCOPE. Names only. A column whose TYPE or nullability drifted between the hand list and
//  the model passes here, so generating the ALTER statements from the model rather than
//  transcribing them is still what keeps those honest. Statements are matched out of source
//  text, so a statement written in a shape the pattern does not match reads as absent and
//  fails the check — noisy rather than silent, which is the right way round to be wrong.
// ─────────────────────────────────────────────────────────────────────────────

var repo = args.FirstOrDefault(a => !a.StartsWith('-')) ?? FindRepoRoot();
if (repo is null)
{
    Console.Error.WriteLine("Could not locate EveConsole.csproj. Pass the repo root as the first argument.");
    return 2;
}

// How many releases back to simulate. One catches everything introduced this cycle, which is
// the common case; more catches debt that was already there when that tag was cut, because a
// gap present at v0.9.12 is present in v0.9.12's own CREATE statements too.
var depth = int.TryParse(Environment.GetEnvironmentVariable("UPGRADE_CHECK_DEPTH"), out var d) ? d : 3;

// ── The tags to measure from ────────────────────────────────────────────────
var tags = Git(repo, "tag --list v* --sort=-v:refname")
    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Take(depth)
    .ToList();

if (tags.Count == 0)
{
    Console.Error.WriteLine("No v* tags are present, so no previous release could be simulated.");
    Console.Error.WriteLine("In CI this is almost always a shallow checkout: actions/checkout needs");
    Console.Error.WriteLine("  with:");
    Console.Error.WriteLine("    fetch-depth: 0");
    return 2;
}

// ── What the model wants ────────────────────────────────────────────────────
SQLitePCL.Batteries_V2.Init();

var modelDb = Path.Combine(Path.GetTempPath(), $"upgrade-model-{Guid.NewGuid():N}.db");
using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={modelDb}").Options))
    db.Database.EnsureCreated();
var model = Schema(modelDb);

var allowed = File.ReadAllLines(Path.Combine(repo, "tools", "UpgradeCheck", "model-only-tables.txt"))
    .Select(l => l.Trim())
    .Where(l => l.Length > 0 && !l.StartsWith('#'))
    .ToHashSet(StringComparer.OrdinalIgnoreCase);

// Source files carrying schema statements. Both are read from the tag AND from the working
// tree: the tag's copy builds the old database, the working tree's upgrades it.
//
// ⚠️ EVERY file holding CREATE or ALTER statements has to be listed here. One that is not is
// invisible to this check, so the tables it builds are reported as having no CREATE at all —
// which reads exactly like the bug this tool exists to catch, coming from the file that fixes it.
//
// A file added this cycle does not exist at the older tags, and that is fine: Git() returns ""
// for a path a tag never had, so the old database simply comes up without those tables — which
// is the situation being tested.
string[] files =
[
    "App.axaml.cs",
    "Services/SdeImportService.cs",
    "Data/AgentTelemetrySchema.cs",
];
var current = files.Select(f => File.ReadAllText(Path.Combine(repo, f))).ToList();

var failed = 0;
foreach (var tag in tags)
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"upgrade-{Guid.NewGuid():N}.db");
    try
    {
        // The database as that release left it…
        foreach (var f in files)
            Apply(dbPath, Extract(Git(repo, $"show {tag}:{f}")));

        var before = Schema(dbPath);

        // …opened by this build.
        foreach (var text in current)
            Apply(dbPath, Extract(text));

        var after = Schema(dbPath);

        var missingCols = new List<string>();
        foreach (var (table, cols) in model)
            if (after.TryGetValue(table, out var have))
                missingCols.AddRange(cols.Where(c => !have.Contains(c)).Select(c => $"{table}.{c}"));

        var missingTables = model.Keys
            .Where(t => !after.ContainsKey(t) && !allowed.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var gaps = missingCols.Count + missingTables.Count;
        Console.WriteLine($"{tag,-10} {before.Count,3} tables ->{after.Count,4} after upgrade   "
                        + (gaps == 0 ? "ok" : $"{gaps} GAP(S)"));

        if (gaps == 0) continue;
        failed += gaps;

        foreach (var t in missingTables)
            Console.WriteLine($"    TABLE   {t}  is in the model and no CREATE builds it");
        foreach (var g in missingCols.GroupBy(c => c.Split('.')[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"    COLUMN  {g.Key}: {string.Join(", ", g.Select(c => c.Split('.')[1]))}");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { /* a temp file left behind is not worth failing over */ }
    }
}

SqliteConnection.ClearAllPools();
try { File.Delete(modelDb); } catch { /* ditto */ }

if (failed == 0)
{
    Console.WriteLine($"\nUpgrade check: {tags.Count} release(s) simulated, no gaps.");
    return 0;
}

Console.WriteLine($"""

    A user upgrading from the release(s) above would not receive the schema named here, so
    every query touching one of those entities throws — EF fails the whole entity, not just
    the missing value.

    A COLUMN needs an ALTER TABLE ADD COLUMN in the startup path. Generate it from the model
    rather than typing it: a type that differs from the one EnsureCreated emits gives upgraded
    installs a different column from fresh ones, and nothing throws to say so.

    A TABLE needs a CREATE TABLE IF NOT EXISTS there. Add it to model-only-tables.txt ONLY if
    every database still in use already has it, which is not true of a table added this cycle.
    """);
return 1;

// ─── helpers ────────────────────────────────────────────────────────────────

// Statements are recognised in both shapes the codebase uses: a lone ExecuteSqlRaw call, and
// an element of a `foreach (var sql in new[] { … })` array.
static List<string> Extract(string source)
{
    var q3 = new string('"', 3);   // built, not written, so this file's own regex is not a literal
    var found = new List<string>();

    foreach (Match m in Regex.Matches(source,
             Regex.Escape("ExecuteSqlRaw(") + @"\s*" + q3 + "(.*?)" + q3, RegexOptions.Singleline))
        found.Add(m.Groups[1].Value.Trim());

    // ⚠️ \s* after the opening quotes, or this misses every multi-line raw string literal —
    // which is how SQL is normally written here, the keyword starting on the line BELOW the
    // quotes. Without it only the single-line index statements match, and a file whose CREATEs
    // are all multi-line looks like a file with no CREATEs at all.
    foreach (Match m in Regex.Matches(source,
             q3 + @"\s*((?:ALTER TABLE|CREATE TABLE|CREATE INDEX|CREATE UNIQUE INDEX)\s.*?)" + q3,
             RegexOptions.Singleline))
        found.Add(m.Groups[1].Value.Trim());

    return found.Distinct(StringComparer.Ordinal).ToList();
}

// Every statement is idempotent by design — CREATE ... IF NOT EXISTS, or an ALTER whose
// duplicate-column error the app itself swallows — so failures are expected and ignored here
// for the same reason they are ignored there.
static void Apply(string dbPath, IEnumerable<string> statements)
{
    using var cn = new SqliteConnection($"Data Source={dbPath}");
    cn.Open();
    foreach (var sql in statements)
    {
        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        try { cmd.ExecuteNonQuery(); } catch (SqliteException) { /* already present, or not ours */ }
    }
}

static Dictionary<string, HashSet<string>> Schema(string dbPath)
{
    var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    using var cn = new SqliteConnection($"Data Source={dbPath}");
    cn.Open();

    var tables = new List<string>();
    using (var cmd = cn.CreateCommand())
    {
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var r = cmd.ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
    }

    foreach (var t in tables)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = cn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{t}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        map[t] = cols;
    }
    return map;
}

static string Git(string repo, string arguments)
{
    using var p = Process.Start(new ProcessStartInfo("git", arguments)
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    })!;
    var stdout = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return p.ExitCode == 0 ? stdout : "";
}

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "EveConsole.csproj"))) return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
