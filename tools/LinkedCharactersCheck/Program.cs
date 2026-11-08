using EveConsole.Agent;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// ─────────────────────────────────────────────────────────────────────────────
//  Linked characters check
//
//  Does a file of linked characters import exactly what it says, and does an install with no
//  file see nothing at all?
//
//  WHY. Nothing here fails loudly. An import that keeps a character the file dropped, lets an
//  older copy of the file put back last week's links, or takes half of a file it should have
//  refused all leave tables that look fine. And most installs never get a file, so a missing
//  folder must be no error and no row.
//
//  On SQLite always. On PostgreSQL too when EVECONSOLE_PG names a server: a database of its own
//  is created there, built the way an existing install is upgraded (the three tables from
//  PostgresSchema's hand-written statements, not from the model), and dropped afterwards. The
//  database EVECONSOLE_PG names is never written to. --require-pg makes a server that cannot be
//  used a failure rather than a skip, for CI.
//
//  Ids below are invented, in the ranges EVE uses: alliances 99xxxxxxx, corporations 98xxxxxxx,
//  pilots 90xxxxxxx.
// ─────────────────────────────────────────────────────────────────────────────

// ⚠️ SQLite, pinned, before anything reads the engine: nothing here may fall through to a
// developer machine's PostgreSQL config and touch a real server.
DbEngine.Pin(DbBackend.Sqlite);

var failures = 0;
var engine   = "";
void Check(string what, bool ok, string detail = "")
{
    if (!ok) failures++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} [{engine}] {what}{(ok || detail.Length == 0 ? "" : "  -> " + detail)}");
}

// ── The file on its own ─────────────────────────────────────────────────────

engine = "parse";
var t0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

{
    var (snap, error) = LinkedCharacterImportService.Parse(File1(t0));
    Check("a good file reads", snap is not null, error ?? "");
    Check("its accounts and characters", snap is { Accounts.Count: 2, Characters.Count: 3 }, $"{snap?.Accounts.Count} / {snap?.Characters.Count}");
    Check("the main is the account's main", snap?.Accounts.Single(a => a.AccountId == 1001).MainCharacterId == 90_000_001);
    Check("an account with no main has none", snap?.Accounts.Single(a => a.AccountId == 1002).MainCharacterId is null);
    Check("alliance 0 or absent is none", snap?.Characters.Single(c => c.CharacterId == 90_000_002).AllianceId is null
                                       && snap?.Characters.Single(c => c.CharacterId == 90_000_003).AllianceId is null);
    Check("tokenValid absent is unknown, not false", snap?.Characters.Single(c => c.CharacterId == 90_000_002).TokenValid is null);
    Check("names and source trimmed", snap?.Source == "example" && snap.Accounts.Single(a => a.AccountId == 1001).Name == "Example Main");

    var odd = LinkedCharacterImportService.Parse(Wrap("example", "2026-11-08T14:00:00.1234567+02:00", "[]")).Snapshot;
    Check("generatedAt kept in UTC, to the microsecond", odd?.GeneratedAt == new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560)
                                                        && odd.GeneratedAt.Offset == TimeSpan.Zero, odd?.GeneratedAt.ToString("O") ?? "");
}

foreach (var (what, json, says) in Refused(t0))
{
    var (snap, error) = LinkedCharacterImportService.Parse(json);
    Check($"refused: {what}", snap is null && error is not null && error.Contains(says, StringComparison.OrdinalIgnoreCase), error ?? "imported");
}

// ── SQLite ──────────────────────────────────────────────────────────────────

engine = "SQLite";
var dbPath = Path.Combine(Path.GetTempPath(), $"linkedcheck-{Guid.NewGuid():N}.db");
try
{
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite($"Data Source={dbPath};Pooling=False", AppDb.KeepCollectionParameter).Options;
    var factory = new OneFactory(options);
    await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
    await RunSuiteAsync(factory);

    await using (var db = factory.CreateDbContext())
    {
        var names = AgentSchema.Build(db).TableNames;
        Check("the agent never sees them", !names.Any(n => n.StartsWith("Linked", StringComparison.OrdinalIgnoreCase)));
    }
}
finally
{
    foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        try { File.Delete(f); } catch { }
}

// ── PostgreSQL ──────────────────────────────────────────────────────────────

var requirePg = args.Contains("--require-pg");

// ⚠️ From the environment rather than an argument: a connection string carries a password, and an
// argument would land in shell history and CI logs.
if (Environment.GetEnvironmentVariable("EVECONSOLE_PG") is { Length: > 0 } live)
{
    engine = "PostgreSQL";
    var scratch = $"linkedcheck_{Guid.NewGuid():N}";
    var admin   = new NpgsqlConnectionStringBuilder(live);
    var created = false;
    try
    {
        await using (var c = new NpgsqlConnection(admin.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{scratch}\"", c);
            await cmd.ExecuteNonQueryAsync();
        }
        created = true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{(requirePg ? "FAIL" : "skip")} [PostgreSQL] could not make a database of its own: {ex.Message.Split('\n')[0]}");
        if (requirePg) failures++;
    }

    if (created)
    {
        try
        {
            var target  = new NpgsqlConnectionStringBuilder(live) { Database = scratch, Pooling = false };
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(target.ConnectionString, AppDb.KeepCollectionParameter)
                .AddInterceptors(new PostgresParameterInterceptor()).Options;
            var factory = new OneFactory(options);

            // Built as an existing install meets them: everything else from the model, these three
            // from the statements every launch applies.
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("""DROP TABLE "LinkedCharacters", "LinkedAccounts", "LinkedSources" """);
                PostgresSchema.Apply(db, includeSeeds: false);
                PostgresSchema.Apply(db, includeSeeds: false);
            }
            Check("the hand-written tables apply, twice", true);
            await RunSuiteAsync(factory);
        }
        catch (Exception ex) { Check("the suite ran", false, ex.Message.Split('\n')[0]); }
        finally
        {
            try
            {
                await using var c = new NpgsqlConnection(admin.ConnectionString);
                await c.OpenAsync();
                await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)", c);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex) { Console.WriteLine($"note: {scratch} was not dropped: {ex.Message.Split('\n')[0]}"); }
        }
    }
}
else if (requirePg)
{
    Console.WriteLine("FAIL --require-pg, and no EVECONSOLE_PG set");
    failures++;
}
else Console.WriteLine("\nNo EVECONSOLE_PG set, so PostgreSQL was not checked.");

Console.WriteLine(failures == 0 ? "\nPASS: linked characters" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;

// ── The suite, on either engine ─────────────────────────────────────────────

async Task RunSuiteAsync(OneFactory factory)
{
    var services = new ServiceCollection();
    services.AddScoped(_ => factory.CreateDbContext());
    await using var provider = services.BuildServiceProvider();
    var errors = new AppErrorLogger(provider.GetRequiredService<IServiceScopeFactory>());

    var folder = Path.Combine(Path.GetTempPath(), $"linkedcheck-{Guid.NewGuid():N}");
    try
    {
        var import = new LinkedCharacterImportService(factory, errors, folder);
        var changed = 0;
        import.Changed += () => changed++;

        // Most installs: no folder, then an empty one.
        Check("no folder: nothing read", (await import.ScanOnceAsync()).Count == 0);
        Directory.CreateDirectory(folder);
        Check("an empty folder: nothing read", (await import.ScanOnceAsync()).Count == 0);
        Check("and nothing stored, nothing logged", await CountsAsync(factory) == (0, 0, 0) && await LogCountAsync(factory) == 0);

        // A file arrives.
        var file1 = Path.Combine(folder, "example.json");
        Write(file1, File1(t0));
        var r = await import.ScanOnceAsync();
        Check("a file is imported", r is [{ Outcome: LinkedImportOutcome.Imported, Accounts: 2, Characters: 3 }], Describe(r));
        Check("and it says so", changed == 1);
        Check("the rows it says", await CountsAsync(factory) == (1, 2, 3), (await CountsAsync(factory)).ToString());
        await using (var db = factory.CreateDbContext())
        {
            var src  = await db.LinkedSources.SingleAsync();
            var main = await db.LinkedCharacters.SingleAsync(c => c.CharacterId == 90_000_001);
            var alt  = await db.LinkedCharacters.SingleAsync(c => c.CharacterId == 90_000_002);
            Check("source: when, and how many", src is { Source: "example", Accounts: 2, Characters: 3 } && src.GeneratedAt == t0, $"{src.GeneratedAt:O}");
            Check("the main, with its corp, alliance and token", main is { AccountId: 1001, IsMain: true, CorporationId: 98_000_001, AllianceId: 99_000_001, TokenValid: true, Name: "Example Main" });
            Check("its alt, under the same account", alt is { AccountId: 1001, IsMain: false, CorporationId: 98_000_002, AllianceId: null, TokenValid: null });
            Check("the account's main", (await db.LinkedAccounts.SingleAsync(a => a.AccountId == 1001)).MainCharacterId == 90_000_001);
        }
        Check("its first arrival is noted once", await LogCountAsync(factory, LogSeverity.Note, expect: 1) == 1);

        // Unchanged: not read again. Rewritten with the same time: read, and left alone.
        Check("an unchanged file is not read again", (await import.ScanOnceAsync()).Count == 0);
        Write(file1, File1(t0) + " ");
        r = await import.ScanOnceAsync();
        Check("the same sync again changes nothing", r is [{ Outcome: LinkedImportOutcome.Older }] && changed == 1, Describe(r));

        // ⚠️ An older copy, say from another machine on the same database, never puts back old links.
        Write(file1, File2(t0.AddHours(-1)));
        r = await import.ScanOnceAsync();
        Check("an older file is not imported over a newer", r is [{ Outcome: LinkedImportOutcome.Older }] && await CountsAsync(factory) == (1, 2, 3), Describe(r));

        // The next sync: the alt moved to the other account, one character and one account are gone.
        Write(file1, File2(t0.AddHours(1)));
        r = await import.ScanOnceAsync();
        Check("a newer file replaces the source", r is [{ Outcome: LinkedImportOutcome.Imported }], Describe(r));
        await using (var db = factory.CreateDbContext())
        {
            var chars = await db.LinkedCharacters.OrderBy(c => c.CharacterId).ToListAsync();
            Check("exactly the new file's characters", chars.Select(c => (c.CharacterId, c.AccountId)).SequenceEqual([(90_000_001L, 1001L), (90_000_002L, 1003L)]),
                string.Join(", ", chars.Select(c => $"{c.CharacterId}@{c.AccountId}")));
            Check("exactly its accounts", (await db.LinkedAccounts.Select(a => a.AccountId).OrderBy(a => a).ToListAsync()).SequenceEqual([1001L, 1003L]));
            Check("the moved alt is the new account's main", (await db.LinkedAccounts.SingleAsync(a => a.AccountId == 1003)).MainCharacterId == 90_000_002);
            var src = await db.LinkedSources.SingleAsync();
            Check("source updated", src is { Accounts: 2, Characters: 2 } && src.GeneratedAt == t0.AddHours(1));
        }
        Check("noted only the first time", await LogCountAsync(factory, LogSeverity.Note) == 1);

        // A second source stands apart.
        var file2 = Path.Combine(folder, "second.json");
        Write(file2, Wrap("second", "2026-11-08T09:00:00Z",
            """[{ "id": 1001, "name": "Other Name", "characters": [{ "id": 90000001, "name": "Example Main", "main": true, "corporationId": 98000003 }] }]"""));
        r = await import.ScanOnceAsync();
        Check("a second source imports beside the first", r is [{ Outcome: LinkedImportOutcome.Imported, Source: "second" }] && await CountsAsync(factory) == (2, 3, 3), Describe(r));
        Write(file1, Wrap("example", "2026-11-08T15:00:00Z", "[]"));
        r = await import.ScanOnceAsync();
        Check("an empty list empties its own source", r is [{ Outcome: LinkedImportOutcome.Imported, Accounts: 0 }] && await CountsAsync(factory) == (2, 1, 1), $"{Describe(r)} {await CountsAsync(factory)}");
        await using (var db = factory.CreateDbContext())
            Check("and no other", await db.LinkedCharacters.AllAsync(c => c.Source == "second")
                                  && (await db.LinkedSources.SingleAsync(s => s.Source == "example")).Characters == 0);

        // Refused files: said once each, nothing touched.
        var before = await CountsAsync(factory);
        var warned = await LogCountAsync(factory, LogSeverity.Warning);
        var bad    = Refused(t0.AddDays(1)).ToList();
        for (var i = 0; i < bad.Count; i++)
            Write(Path.Combine(folder, $"zz-bad-{i:D2}.json"), bad[i].Json);
        r = await import.ScanOnceAsync();
        Check("every bad file refused", r.Count == bad.Count && r.All(x => x.Outcome == LinkedImportOutcome.Rejected), Describe(r));
        Check("nothing they said was stored", await CountsAsync(factory) == before);
        Check("each said once in the log", await LogCountAsync(factory, LogSeverity.Warning, expect: warned + bad.Count) == warned + bad.Count);
        Check("and not again next time", (await import.ScanOnceAsync()).Count == 0 && await LogCountAsync(factory, LogSeverity.Warning) == warned + bad.Count);
        foreach (var f in Directory.EnumerateFiles(folder, "zz-bad-*.json")) File.Delete(f);

        // ⚠️ A file still open for writing is not refused: it is tried again when the writer is done.
        // Windows only — elsewhere a share mode is a hint other programs need not honour.
        if (OperatingSystem.IsWindows())
        {
            var file3 = Path.Combine(folder, "third.json");
            var text  = Wrap("third", "2026-11-08T09:00:00Z", "[]");
            using (var writer = new FileStream(file3, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                writer.Write(System.Text.Encoding.UTF8.GetBytes(text));
                writer.Flush();
                r = await import.ScanOnceAsync();
                Check("a file being written is left for later", r is [{ Outcome: LinkedImportOutcome.Unreadable }], Describe(r));
            }
            r = await import.ScanOnceAsync();
            Check("and read once it is finished", r is [{ Outcome: LinkedImportOutcome.Imported, Source: "third" }], Describe(r));
        }
        Check("no errors logged along the way", await LogCountAsync(factory, LogSeverity.Error) == 0);
    }
    finally
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }
}

// ── Files ───────────────────────────────────────────────────────────────────

static string Wrap(string source, string generatedAt, string accounts) => $$"""
    { "format": "eveconsole.linked-characters", "version": 1, "source": "{{source}}",
      "generatedAt": "{{generatedAt}}", "accounts": {{accounts}} }
    """;

static string File1(DateTimeOffset at) => Wrap("  example ", at.ToString("O"), """
    [
      { "id": 1001, "name": " Example Main ",
        "characters": [
          { "id": 90000001, "name": "Example Main", "main": true, "corporationId": 98000001, "allianceId": 99000001, "tokenValid": true },
          { "id": 90000002, "name": "Example Alt", "corporationId": 98000002, "allianceId": 0 } ] },
      { "id": 1002, "name": "Example Other",
        "characters": [ { "id": 90000003, "name": "Example Other", "corporationId": 98000001, "tokenValid": false } ] },
    ]
    """);

// The alt has moved to an account of its own, and account 1002 is gone with its character.
static string File2(DateTimeOffset at) => Wrap("example", at.ToString("O"), """
    [
      { "id": 1001, "name": "Example Main",
        "characters": [ { "id": 90000001, "name": "Example Main", "main": true, "corporationId": 98000001 } ] },
      { "id": 1003, "name": "Example Alt",
        "characters": [ { "id": 90000002, "name": "Example Alt", "main": true, "corporationId": 98000002 } ] }
    ]
    """);

static IEnumerable<(string What, string Json, string Says)> Refused(DateTimeOffset at)
{
    var when = at.ToString("O");
    yield return ("not JSON",                "{ \"format\": ", "JSON");
    yield return ("another kind of file",    "{ \"format\": \"something-else\", \"version\": 1 }", "format");
    yield return ("no format",               "{ \"version\": 1 }", "format");
    yield return ("a newer version",         Wrap("example", when, "[]").Replace("\"version\": 1", "\"version\": 2"), "newer");
    yield return ("no source",               Wrap(" ", when, "[]"), "source");
    yield return ("a source too long",       Wrap(new string('x', 65), when, "[]"), "longer");
    yield return ("no generatedAt",          Wrap("example", when, "[]").Replace($"\"generatedAt\": \"{when}\",", ""), "generatedAt");
    yield return ("no accounts list",        Wrap("example", when, "null"), "accounts");
    yield return ("an account with no id",   Wrap("example", when, """[{ "name": "x" }]"""), "no \"id\"");
    yield return ("an account twice",        Wrap("example", when, """[{ "id": 5 }, { "id": 5 }]"""), "twice");
    yield return ("a character with no id",  Wrap("example", when, """[{ "id": 5, "characters": [{ "name": "x" }] }]"""), "no \"id\"");
    yield return ("a character in two accounts", Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 90000001 }] }, { "id": 6, "characters": [{ "id": 90000001 }] }]"""), "both account");
    yield return ("two mains",               Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 90000001, "main": true }, { "id": 90000002, "main": true }] }]"""), "two mains");
    yield return ("an id too large for a corporation", Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 90000001, "corporationId": 99999999999 }] }]"""), "JSON");
}

static void Write(string path, string text)
{
    // A new write time each time, even within the clock's resolution: the scan goes by it.
    var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
    File.WriteAllText(path, text);
    if (File.GetLastWriteTimeUtc(path) <= before) File.SetLastWriteTimeUtc(path, before.AddSeconds(1));
}

static string Describe(IReadOnlyList<LinkedImportResult> r) =>
    r.Count == 0 ? "nothing read" : string.Join("; ", r.Select(x => $"{x.File}: {x.Outcome} {x.Detail}".TrimEnd()));

static async Task<(int, int, int)> CountsAsync(OneFactory factory)
{
    await using var db = factory.CreateDbContext();
    return (await db.LinkedSources.CountAsync(), await db.LinkedAccounts.CountAsync(), await db.LinkedCharacters.CountAsync());
}

// The logger writes in the background, so a count is waited for, briefly, before it is believed.
static async Task<int> LogCountAsync(OneFactory factory, LogSeverity? severity = null, int? expect = null)
{
    var n = 0;
    for (var i = 0; i < 20; i++)
    {
        await using (var db = factory.CreateDbContext())
            n = severity is { } s ? await db.AppErrors.CountAsync(e => e.Severity == (int)s) : await db.AppErrors.CountAsync();
        if (expect is null ? i >= 2 : n >= expect) break;
        await Task.Delay(50);
    }
    return n;
}

sealed class OneFactory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(o);
}
