using EveConsole.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.RegularExpressions;

// ─────────────────────────────────────────────────────────────────────────────
//  Fresh-install check
//
//  Answers one question: would this build start on a machine that has never run
//  EveConsole before?
//
//  It exists because that case cannot be tested by running the app on a developer's
//  machine, where the database has accumulated years of ALTER TABLE history. The
//  specific trap is that EF's EnsureCreated() builds every entity table FIRST and
//  emits no DEFAULT clauses — a C# initialiser like `= false` is not a SQL default.
//  So on a fresh install every hand-written CREATE TABLE IF NOT EXISTS for an
//  entity is dead code, its defaults never apply, and the follow-up ALTERs throw
//  "duplicate column" into an empty catch. A seed INSERT that omits one of those
//  columns then fails with error 19 — but only ever for a new user.
//
//  v0.9.10 shipped exactly that: MarketDefaultSettings.PurchaseWhenCheaper aborted
//  startup, and AlertSettings hit the same violation behind an INSERT OR IGNORE
//  that swallowed it, leaving new users with no alert settings row at all.
//
//  SCOPE. This replays the raw SQL from App.axaml.cs in source order against a
//  database built by EnsureCreated() alone. It does not execute the C# control flow
//  around those statements, so it is a guard against raw SQL colliding with the
//  EF-created schema — not a general "does the app start" test. A green result here
//  is narrower than it may look.
// ─────────────────────────────────────────────────────────────────────────────

var root = args.Length > 0 ? args[0] : FindRepoRoot();
if (root is null)
{
    Console.Error.WriteLine("Could not locate EveConsole.csproj. Pass the repo root as the first argument.");
    return 2;
}

var appFile = Path.Combine(root, "App.axaml.cs");
if (!File.Exists(appFile))
{
    Console.Error.WriteLine($"Not found: {appFile}");
    return 2;
}

SQLitePCL.Batteries_V2.Init();

var dbPath = Path.Combine(Path.GetTempPath(), $"eveconsole-freshcheck-{Guid.NewGuid():N}.db");
try
{
    // A brand-new install: EF creates every entity table and nothing else has run.
    var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
    using (var db = new AppDbContext(opts)) db.Database.EnsureCreated();

    var src = File.ReadAllText(appFile);
    var q3  = new string('"', 3);   // built, not written, so this file's own regex is not a literal

    // Blank the raw-string literals before brace tracking, so SQL text cannot be
    // mistaken for code structure.
    var masked = new StringBuilder(src);
    foreach (Match m in Regex.Matches(src, q3 + ".*?" + q3, RegexOptions.Singleline))
        for (var i = m.Index; i < m.Index + m.Length; i++)
            if (masked[i] != '\n') masked[i] = ' ';
    var flat = masked.ToString();

    // Statements inside a try block are allowed to fail: the idempotent ALTERs are
    // written that way on purpose and throw on every fresh install by design.
    var inTry = new bool[src.Length];
    var open  = new List<bool>();
    for (var i = 0; i < flat.Length; i++)
    {
        if (flat[i] == '{')
            open.Add(Regex.IsMatch(flat[Math.Max(0, i - 60)..i], @"\btry\s*$"));
        else if (flat[i] == '}' && open.Count > 0)
            open.RemoveAt(open.Count - 1);
        inTry[i] = open.Contains(true);
    }

    var stmts = new Regex(Regex.Escape("ExecuteSqlRaw(") + @"\s*" + q3 + "(.*?)" + q3,
                          RegexOptions.Singleline);

    using var cn = new SqliteConnection($"Data Source={dbPath}");
    cn.Open();

    int ok = 0, tolerated = 0;
    var failures = new List<string>();

    foreach (Match m in stmts.Matches(src))
    {
        var sql     = m.Groups[1].Value.Trim();
        var line    = src[..m.Index].Count(c => c == '\n') + 1;
        var guarded = inTry[m.Index];

        using var cmd = cn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            var rows = cmd.ExecuteNonQuery();

            // A seed reporting success while leaving its table empty is a defect wearing a
            // success code: INSERT OR IGNORE swallows the very constraint failure being
            // hunted here, which is how the AlertSettings row went missing unnoticed.
            if (Regex.IsMatch(sql, @"^\s*INSERT", RegexOptions.IgnoreCase) && rows == 0)
            {
                var table = Regex.Match(sql, @"INTO\s+""?(\w+)""?").Groups[1].Value;
                using var chk = cn.CreateCommand();
                chk.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                if (Convert.ToInt64(chk.ExecuteScalar()) == 0)
                {
                    failures.Add($"  SILENT  App.axaml.cs:{line}  {table}: inserted 0 rows and the table is still empty");
                    continue;
                }
            }
            ok++;
        }
        catch (SqliteException ex)
        {
            if (guarded) { tolerated++; continue; }
            failures.Add($"  BREAKS  App.axaml.cs:{line}  {ex.Message.Split('\n')[0]}");
        }
    }

    Console.WriteLine($"Fresh-install check: {ok} statement(s) ok, "
                    + $"{tolerated} tolerated inside try/catch, {failures.Count} problem(s).");

    if (failures.Count == 0) return 0;

    Console.WriteLine();
    foreach (var f in failures) Console.WriteLine(f);
    Console.WriteLine();
    Console.WriteLine("A statement above would abort startup, or silently seed nothing, for a user");
    Console.WriteLine("whose database EF has just created. Name every NOT NULL column in the INSERT,");
    Console.WriteLine("or give the property a SQL default in the model rather than a C# initialiser.");
    return 1;
}
finally
{
    SqliteConnection.ClearAllPools();
    foreach (var f in Directory.GetFiles(Path.GetDirectoryName(dbPath)!,
                                         Path.GetFileName(dbPath) + "*"))
        try { File.Delete(f); } catch { /* a temp file left behind is not worth failing over */ }
}

// Walks up from the working directory so the tool runs from the repo root, from its
// own folder, or from wherever CI happens to invoke it.
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
