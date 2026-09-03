using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

// ─────────────────────────────────────────────────────────────────────────────
//  Postgres schema check
//
//  Answers one question without a server: does the entity model map cleanly to
//  PostgreSQL?
//
//  EF builds the CREATE script from the model alone — no connection is opened —
//  so every entity, property type, key, index and relationship is put through the
//  Npgsql provider's type mapper here. A CLR type SQLite tolerated and Npgsql has
//  no mapping for fails at this point rather than on a user's first launch.
//
//  SCOPE. This proves the model is expressible in PostgreSQL. It says nothing
//  about the hand-written SQL in the app, which is where the dialect differences
//  actually live — see the ExecuteSqlRaw sites. A green result here is narrower
//  than it may look, in the same way FreshInstallCheck's is.
// ─────────────────────────────────────────────────────────────────────────────

// Not connected to, and deliberately not connectable: the script comes from the model.
const string designTime = "Host=schema.check.invalid;Database=eveconsole;Username=none";

try
{
    var opts = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(designTime).Options;
    using var db = new AppDbContext(opts);

    var script = db.Database.GenerateCreateScript();

    var tables  = CountMatches(script, "CREATE TABLE");
    var indexes = CountMatches(script, "CREATE INDEX") + CountMatches(script, "CREATE UNIQUE INDEX");

    var outPath = args.Length > 0
        ? args[0]
        : Path.Combine(Path.GetTempPath(), "eveconsole-postgres-schema.sql");
    File.WriteAllText(outPath, script);

    Console.WriteLine($"Model maps to PostgreSQL cleanly.");
    Console.WriteLine($"  tables  : {tables}");
    Console.WriteLine($"  indexes : {indexes}");
    Console.WriteLine($"  script  : {outPath} ({script.Length:N0} bytes)");

    // The app's own DDL creates these two; they are not entities, so they must NOT appear
    // here. If one ever does, it has been added to the model and the hand-written copy in
    // the Postgres bootstrap is now a second, diverging definition.
    foreach (var orphan in new[] { "IndustryOpportunitiesSettings", "TradeOpportunitiesSettings" })
        if (script.Contains($"\"{orphan}\"", StringComparison.Ordinal))
            Console.WriteLine($"  note    : {orphan} is now an entity — drop it from the bootstrap.");

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("The model does NOT map to PostgreSQL:");
    Console.Error.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is { } inner)
        Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
    return 1;
}

static int CountMatches(string haystack, string needle)
{
    int n = 0, i = 0;
    while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
    return n;
}
