using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Exercises the SQLite half of SdeUndo against a real database: does the backup get taken, can
// other writers still work while it is open, does a rollback put the data back, does dispose
// alone restore on the exception path, and is the backup file cleaned up every time.
//
// ⚠️ The third check is the one worth having. SdeUndo takes a file copy on SQLite instead of
// holding a transaction precisely because a transaction held for the length of an import blocks
// every other writer in the app — measured, before this existed: an insert into an unrelated
// table failed with "database is locked", and wal_checkpoint reclaimed nothing. That property is
// invisible in normal use and would be silently lost by anyone "simplifying" SdeUndo back into a
// single transaction, so it is asserted here rather than described in a comment.
//
//     dotnet run --project tools/SdeUndoCheck
//
// With the app running, its bin\ is locked, so build somewhere it does not hold:
//
//     dotnet run --project tools/SdeUndoCheck -c Release -p:UseAppHost=false

SQLitePCL.Batteries_V2.Init();
DbEngine.Pin(DbBackend.Sqlite);

var dir = Path.Combine(Path.GetTempPath(), "sdeundotest");
if (Directory.Exists(dir)) Directory.Delete(dir, true);
Directory.CreateDirectory(dir);
var dbPath = Path.Combine(dir, "t.db");
var bakPath = dbPath + ".sdebak";

var opts = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;

var progress = new Progress<SdeImportProgress>(_ => { });
var pass = 0; var fail = 0;
void Check(string what, bool ok) { if (ok) { pass++; Console.WriteLine($"  PASS  {what}"); } else { fail++; Console.WriteLine($"  FAIL  {what}"); } }

using (var db = new AppDbContext(opts)) db.Database.EnsureCreated();

static List<string> Tables(AppDbContext db) => db.Model.GetEntityTypes()
    .Select(t => t.GetTableName())
    .Where(n => n is not null && n.StartsWith("Sde", StringComparison.Ordinal) && n != "SdeBuildInfos")
    .Select(n => n!).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

static async Task SeedAsync(AppDbContext db, string tag)
{
    db.SdeRaces.RemoveRange(db.SdeRaces);
    db.SdeCategories.RemoveRange(db.SdeCategories);
    await db.SaveChangesAsync();
    for (int i = 1; i <= 5; i++)
        db.SdeRaces.Add(new SdeRace { RaceId = i, Name = $"{tag} race {i}", Description = "" });
    for (int i = 1; i <= 3; i++)
        db.SdeCategories.Add(new SdeCategory { CategoryId = i, Name = $"{tag} category {i}" });
    await db.SaveChangesAsync();
}

// ── 1. Rollback puts the previous SDE back ───────────────────────────────────────────────
Console.WriteLine("Rollback:");
{
    using var db = new AppDbContext(opts);
    await SeedAsync(db, "OLD");

    var undo = await SdeUndo.CreateAsync(db, Tables(db), progress, default);
    Check("backup file exists once the copy is taken", File.Exists(bakPath));

    // The import's wipe, committed immediately on SQLite.
    foreach (var t in Tables(db))
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{t}\"");
    Check("tables are empty mid-import", !db.SdeRaces.AsNoTracking().Any());

    // ⚠️ The point of the whole exercise: another connection can still WRITE while this is open.
    // With a transaction held instead, this fails with "database is locked".
    try
    {
        using var other = new SqliteConnection($"Data Source={dbPath}");
        other.Open();
        using var cmd = other.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=3000";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO \"AppErrorLog\" (\"OccurredAt\",\"Source\",\"Context\",\"Message\",\"HostName\",\"Headless\") " +
                          "VALUES ('2026-01-01','t','t','a poller writing during the import','h',0)";
        cmd.ExecuteNonQuery();
        Check("another connection can WRITE an unrelated table mid-import", true);
    }
    catch (Exception ex) { Check($"another connection can WRITE an unrelated table mid-import ({ex.Message.Split('\n')[0]})", false); }

    await undo.RollbackAsync(default);
    await undo.DisposeAsync();

    using var after = new AppDbContext(opts);
    var races = after.SdeRaces.AsNoTracking().OrderBy(r => r.RaceId).ToList();
    Check("5 races restored", races.Count == 5);
    Check("restored rows are the OLD ones", races.FirstOrDefault()?.Name == "OLD race 1");
    Check("3 categories restored", after.SdeCategories.AsNoTracking().Count() == 3);
    Check("backup file cleaned up", !File.Exists(bakPath));
}

// ── 2. Commit keeps the new data ─────────────────────────────────────────────────────────
Console.WriteLine("Commit:");
{
    using var db = new AppDbContext(opts);
    var undo = await SdeUndo.CreateAsync(db, Tables(db), progress, default);
    foreach (var t in Tables(db))
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{t}\"");
    await SeedAsync(db, "NEW");
    await undo.CommitAsync(default);
    await undo.DisposeAsync();

    using var after = new AppDbContext(opts);
    var races = after.SdeRaces.AsNoTracking().OrderBy(r => r.RaceId).ToList();
    Check("new data kept", races.Count == 5 && races[0].Name == "NEW race 1");
    Check("backup file cleaned up", !File.Exists(bakPath));
}

// ── 3. Dispose without settling restores, which is the exception path ────────────────────
Console.WriteLine("Exception path (dispose without commit or rollback):");
{
    using var db = new AppDbContext(opts);
    await SeedAsync(db, "KEEP");

    var undo = await SdeUndo.CreateAsync(db, Tables(db), progress, default);
    foreach (var t in Tables(db))
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{t}\"");
    await undo.DisposeAsync();          // as `await using` would on the way out of a throw

    using var after = new AppDbContext(opts);
    var races = after.SdeRaces.AsNoTracking().OrderBy(r => r.RaceId).ToList();
    Check("data restored by dispose alone", races.Count == 5 && races[0].Name == "KEEP race 1");
    Check("backup file cleaned up", !File.Exists(bakPath));
}

Console.WriteLine();
Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
