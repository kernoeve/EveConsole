using System.Net;
using System.Text;
using System.Text.Json;
using EveConsole.Agent;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Connector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

// ─────────────────────────────────────────────────────────────────────────────
//  Linked characters check
//
//  Does a connector's answer store exactly what it says, does an install that never connects see
//  nothing at all, and does the connection behave: the connector's own sign-in, confirmed by its
//  code, the key on every call, and what was stored kept through the connector's outages and refusals?
//
//  WHY. Nothing here fails loudly. A store that keeps a character the answer dropped, lets an
//  older answer put back last week's links, or takes half of an answer it should have refused all
//  leave tables that look fine. A client that put a secret in an address, opened any page it was
//  given, or dropped the cache the moment a connector blinked, would work in every demonstration.
//  And with several connectors: one asked about corporations it was not chosen for, one's answer
//  stored over another's, or two connectors both holding one corporation's data, all look fine too.
//
//  The connectors are fakes behind the HTTP client factory, one per address: no socket, no browser,
//  no EVE. A sign-in page is a call standing in for the person at the browser, who compares the codes.
//
//  On SQLite always. On PostgreSQL too when EVECONSOLE_PG names a server: a database of its own
//  is created there, built the way an existing install is upgraded (the four tables from
//  PostgresSchema's hand-written statements, not from the model), and dropped afterwards. The
//  database EVECONSOLE_PG names is never written to. --require-pg makes a server that cannot be
//  used a failure rather than a skip, for CI.
//
//  Ids below are invented, nine digits so that none is a real one: alliances 99xxxxxxx, corporations
//  98xxxxxxx, pilots 90xxxxxxx.
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

// ── The answer on its own ───────────────────────────────────────────────────

engine = "parse";
var t0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

{
    var (snap, error) = LinkedCharacterStore.Parse(Payload1(t0));
    Check("a good answer reads", snap is not null, error ?? "");
    Check("its accounts and characters", snap is { Accounts.Count: 2, Characters.Count: 3 }, $"{snap?.Accounts.Count} / {snap?.Characters.Count}");
    Check("the main is the account's main", snap?.Accounts.Single(a => a.AccountId == 1001).MainCharacterId == 900_000_001);
    Check("an account with no main has none", snap?.Accounts.Single(a => a.AccountId == 1002).MainCharacterId is null);
    Check("alliance 0 or absent is none", snap?.Characters.Single(c => c.CharacterId == 900_000_002).AllianceId is null
                                       && snap?.Characters.Single(c => c.CharacterId == 900_000_003).AllianceId is null);
    Check("tokenValid absent is unknown, not false", snap?.Characters.Single(c => c.CharacterId == 900_000_002).TokenValid is null);
    Check("names and source trimmed", snap?.Source == "example" && snap.Accounts.Single(a => a.AccountId == 1001).Name == "Example Main");

    var odd = LinkedCharacterStore.Parse(Wrap("example", "2026-11-08T14:00:00.1234567+02:00", "[]")).Snapshot;
    Check("generatedAt kept in UTC, to the microsecond", odd?.GeneratedAt == new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560)
                                                        && odd.GeneratedAt.Offset == TimeSpan.Zero, odd?.GeneratedAt.ToString("O") ?? "");
}

foreach (var (what, json, says) in Refused(t0))
{
    var (snap, error) = LinkedCharacterStore.Parse(json);
    Check($"refused: {what}", snap is null && error is not null && error.Contains(says, StringComparison.OrdinalIgnoreCase), error ?? "stored");
}

// ── The address ─────────────────────────────────────────────────────────────

engine = "address";
foreach (var (typed, expect) in new (string, string?)[]
{
    ("http://localhost:5054",           "http://localhost:5054/"),
    (" http://127.0.0.1:5054/base ",    "http://127.0.0.1:5054/base/"),
    ("http://[::1]:5054/",              "http://[::1]:5054/"),
    ("https://connector.example.com/x/?a=1#f", "https://connector.example.com/x/"),
    ("http://192.168.1.20:5054/",       null),     // ⚠️ plain http on a network: the key would travel readable
    ("http://connector.example.com/",   null),
    ("localhost:5054",                  null),
    ("ftp://localhost/",                null),
    ("",                                null),
})
{
    var (url, error) = ConnectorService.ParseUrl(typed);
    Check($"\"{typed}\" {(expect is null ? "refused" : "is " + expect)}",
          expect is null ? url is null && error is { Length: > 0 } : url?.ToString() == expect, url?.ToString() ?? error ?? "");
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
        var schema = AgentSchema.Build(db);
        Check("nor the preferences that hold the keys", !names.Contains(AgentSchema.PreferencesTable)
              && EveConsole.Agent.Tools.Data.ReadOnlySql.Reject("SELECT Value FROM AppPreferences WHERE Key = 'connector.key'", schema) is not null
              && EveConsole.Agent.Tools.Data.ReadOnlySql.Reject("select * from (table \"appPreferences\")", schema) is not null
              && EveConsole.Agent.Tools.Data.ReadOnlySql.Reject("SELECT query_to_xml('select * from \"App' || 'Preferences\"', true, false, '')", schema) is not null
              && EveConsole.Agent.Tools.Data.ReadOnlySql.Reject("SELECT most_common_vals FROM pg_stats", schema) is not null);

        // ⚠️ Each connector's key lives in the Connectors table now: hidden, and refused by name, however written.
        Check("nor the connectors, with their keys", !names.Contains(AgentSchema.ConnectorsTable, StringComparer.OrdinalIgnoreCase));
        foreach (var sql in new[]
        {
            "SELECT \"Key\" FROM \"Connectors\"",
            "select * from (table connectors)",
            "SELECT query_to_xml('select * from \"Conn' || 'ectors\"', true, false, '')",
            "SELECT * FROM (TABLE U&\"\\0043onnectors\") t",      // PostgreSQL's Unicode escapes: the name without its letters
            "SELECT * FROM (table u&\"\\0041pp\\0050references\") t",
        })
            Check($"refused: {sql}", EveConsole.Agent.Tools.Data.ReadOnlySql.Reject(sql, schema) is not null);
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

            // Built as an existing install meets them: everything else from the model, these four
            // from the statements every launch applies.
            await using (var db = factory.CreateDbContext())
            {
                await db.Database.EnsureCreatedAsync();
                await db.Database.ExecuteSqlRawAsync("""DROP TABLE "LinkedCharacters", "LinkedAccounts", "LinkedSources", "Connectors" """);
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

    var store = new LinkedCharacterStore(factory, errors);
    var changed = 0;
    store.Changed += () => changed++;

    // The user's own characters, whose corporations and alliances a connector can be chosen for. 980000004 has no
    // stored name, so it shows by number; 1900001 is in the NPC corporations' range (none has it), never offered.
    await using (var db = factory.CreateDbContext())
    {
        db.Characters.AddRange(
            new Character { Id = 900_000_001, Name = "Example Main",    CorporationId = 980_000_001, AllianceId = 990_000_001 },
            new Character { Id = 900_000_004, Name = "Example Fourth",  CorporationId = 980_000_004, AllianceId = 990_000_001 },
            new Character { Id = 900_000_006, Name = "Example Starter", CorporationId = 1_900_001 });
        db.UniverseNames.AddRange(
            new UniverseName { EntityId = 980_000_001, Name = "Example Corp One", Category = "corporation" },
            new UniverseName { EntityId = 990_000_001, Name = "Example Alliance", Category = "alliance" });
        await db.SaveChangesAsync();
    }

    // Three connectors, told apart by address. A answers at two addresses, so an entry can be pointed elsewhere.
    // The person at the browser compares the code on the connector's page with the one EVE Console showed them,
    // and signs in only if they match. What the fake connector then decides is its own setting.
    var fakeA = new FakeConnector { Name = "Example Connector A", Payload = Payload1(t0) };
    var fakeB = new FakeConnector { Name = "Example Connector B", Payload = Payload1(t0) };
    var fakeC = new FakeConnector { Name = "Example Connector C", SignInId = 900_000_099, SignInName = "Example Stranger" };
    var fake  = fakeA;
    var http  = new FakeHttp(new()
    {
        ["localhost:5054"] = fakeA, ["127.0.0.1:5054"] = fakeA, ["127.0.0.1:5055"] = fakeB, ["127.0.0.1:5056"] = fakeC,
    });
    var shown  = new List<string>();
    var opened = new List<Uri>();
    void Open(Uri page) { opened.Add(page); http.For(page)?.Visit(page, shown.LastOrDefault()); }
    var connector = new ConnectorService(http, factory, store, errors, openBrowser: Open);
    var other     = new ConnectorService(http, factory, store, errors, openBrowser: Open);   // another PC on the same database
    const string Url = "http://localhost:5054/", UrlA2 = "http://127.0.0.1:5054/", UrlB = "http://127.0.0.1:5055/", UrlC = "http://127.0.0.1:5056/";
    const string LC = ConnectorFunctions.LinkedCharacters;
    Task<ConnectorConnectResult> Connect(ConnectorService who, int id, string url = Url) => who.ConnectAsync(id, url, shown.Add, default);
    async Task<ConnectorSnapshot> Get(int id) => (await connector.GetAsync(id))!;
    int Requests() => fakeA.Requests.Count + fakeB.Requests.Count + fakeC.Requests.Count;

    // ── Most installs: no connector at all ──

    Check("no connectors: none listed", (await connector.ListAsync()).Count == 0);
    await connector.RefreshAllAsync();
    Check("no connectors: refreshing all asks nobody", Requests() == 0);
    Check("not connected: a refresh does nothing", (await connector.RefreshAsync(12345)).Outcome == ConnectorRefreshOutcome.NotConnected);
    Check("and asks nobody", Requests() == 0 && http.Unrouted.Count == 0);
    Check("nothing stored, nothing logged", await CountsAsync(factory) == (0, 0, 0) && await LogCountAsync(factory) == 0);

    var idA = await connector.AddAsync();
    Check("an entry added: listed, not connected, chosen for nothing",
          (await connector.ListAsync()).SingleOrDefault() is { Connected: false, HasScope: false, Functions.Count: 0, FunctionsChosen: false } e0 && e0.Id == idA);
    await connector.RefreshAllAsync();
    Check("an entry not connected: refreshing all asks nobody", Requests() == 0);

    // ── Connecting to the wrong thing, or a sign-in that does not end well, connects nothing ──

    fake.Interface = "something-else";
    var c = await Connect(connector, idA);
    Check("not a connector: refused before any sign-in", !c.Ok && fake.Requests.All(x => x.Path != "/v1/auth/start"), c.Message);
    fake.Interface = "eveconsole.connector";

    fake.Versions = [2];
    c = await Connect(connector, idA);
    Check("a connector of a later version only: refused before any sign-in", !c.Ok && fake.Requests.All(x => x.Path != "/v1/auth/start"), c.Message);
    fake.Versions = [1, 2];

    c = await Connect(connector, idA, "http://192.168.1.20:5054/");
    Check("plain http to another device: not even asked", !c.Ok && http.Unrouted.All(x => x.Host != "192.168.1.20"), c.Message);

    foreach (var (person, what) in new[] { ("outsider", "a character the connector refuses"), ("forged", "an EVE sign-in the connector could not confirm"),
                                           ("declines", "the person declining on the page"), ("leaves", "a sign-in left until it expired") })
    {
        fake.Person = person;
        c = await Connect(connector, idA);
        Check($"{what}: not connected", !c.Ok && !(await Get(idA)).Connected && fake.IssuedKeys.Count == 0, c.Message);
    }

    // ⚠️ Somebody else's sign-in: the page shows a code that is not the one EVE Console showed, so the person
    // declines it, and nobody gets a key for them.
    fake.Person = "member";
    fake.ForeignCode = true;
    c = await Connect(connector, idA);
    fake.ForeignCode = false;
    Check("a page showing a code EVE Console did not show is declined", !c.Ok && fake.IssuedKeys.Count == 0, c.Message);

    // ⚠️ A sign-in page EVE Console must not open: plain http on a network.
    var visits = opened.Count;
    fake.LoginPage = "http://elsewhere.example.com/login";
    c = await Connect(connector, idA);
    fake.LoginPage = null;
    Check("a sign-in page on plain http elsewhere is not opened", !c.Ok && opened.Count == visits, c.Message);

    // ── Connected ──

    // It offers one function this build knows besides linked characters, and one it does not.
    fake.Capabilities = ["linked-characters", "groups", "something-later"];
    c = await Connect(connector, idA);
    var account = await Get(idA);
    Check("connected", c.Ok, c.Message);
    Check("as the character the connector named", account is { Connected: true, CharacterId: 900_000_001, CharacterName: "Example Main", ConnectorName: "Example Connector A" },
          account.ToString());
    Check("the key the connector issued is kept", fake.IssuedKeys.Count == 1);
    Check("the code shown was the sign-in's own", shown.LastOrDefault() is { Length: > 0 } code && fake.Visits[^1].Code == code);
    Check("chosen by default for the signed-in character's corporation", account.Corporations.SequenceEqual([980_000_001]) && account.Alliances.Count == 0,
          string.Join(",", account.Corporations));
    Check("used by default for what it offers that this build knows", account is { FunctionsChosen: true } && account.Functions.SequenceEqual([LC, "groups"]),
          string.Join(",", account.Functions));
    Check("and its capabilities kept as sent", account.Capabilities.Contains("something-later"));

    // ⚠️ The poll secret only ever travels in a request body: never in an address, so never in a browser.
    var secrets = fake.Secrets.ToList();
    Check("the poll secret never appears in an address", secrets.Count > 0
          && !fake.Requests.Any(x => secrets.Any(sc => x.Path.Contains(sc)))
          && !opened.Any(v => secrets.Any(sc => v.ToString().Contains(sc))));

    // Connecting stored what it says.
    var r = new ConnectorRefreshResult(ConnectorRefreshOutcome.Imported);
    Check("connecting stores the links", c.Ok && await CountsAsync(factory) == (1, 2, 3));
    Check("the rows it says", await CountsAsync(factory, LinkedCharacterStore.SourceKey(idA)) == (1, 2, 3), (await CountsAsync(factory)).ToString());
    await using (var db = factory.CreateDbContext())
    {
        var src  = await db.LinkedSources.SingleAsync();
        var main = await db.LinkedCharacters.SingleAsync(x => x.CharacterId == 900_000_001);
        var alt  = await db.LinkedCharacters.SingleAsync(x => x.CharacterId == 900_000_002);
        Check("source: the entry's own, labelled as sent, when, and how many",
              src.Source == LinkedCharacterStore.SourceKey(idA) && src is { Label: "example", Accounts: 2, Characters: 3 } && src.GeneratedAt == t0,
              $"{src.Source} {src.Label} {src.GeneratedAt:O}");
        Check("the main, with its corp, alliance and token", main is { AccountId: 1001, IsMain: true, CorporationId: 980_000_001, AllianceId: 990_000_001, TokenValid: true, Name: "Example Main" });
        Check("its alt, under the same account", alt is { AccountId: 1001, IsMain: false, CorporationId: 980_000_002, AllianceId: null, TokenValid: null });
        Check("the account's main", (await db.LinkedAccounts.SingleAsync(a => a.AccountId == 1001)).MainCharacterId == 900_000_001);
    }
    Check("its first arrival is noted once", await LogCountAsync(factory, LogSeverity.Note, expect: 1) == 1);
    Check("and the screen is told", changed == 1);

    // ⚠️ After the sign-in, every call carries the key, and no call ever carried anything EVE gave.
    var key = fake.IssuedKeys.Single();
    var lastPoll = fake.Requests.FindLastIndex(x => x.Path == "/v1/auth/poll");
    var afterConnect = fake.Requests.Skip(lastPoll + 1).ToList();
    Check("every call after the sign-in carried the key", afterConnect.Count > 0 && afterConnect.All(x => x.Authorization == "Bearer " + key),
          string.Join(", ", afterConnect.Select(x => $"{x.Path}={x.Authorization}")));
    Check("EVE Console never sent an EVE token", !fake.Requests.Any(x => x.Body.Contains("accessToken") || x.Body.Contains("eve-token")));

    // The same answer again changes nothing; an older one never puts back old links.
    r = await connector.RefreshAsync(idA);
    Check("the same answer again changes nothing", r.Outcome == ConnectorRefreshOutcome.Unchanged && changed == 1, r.ToString());
    fake.Payload = Payload2(t0.AddHours(-1));
    r = await connector.RefreshAsync(idA);
    Check("an older answer is not stored over a newer", r.Outcome == ConnectorRefreshOutcome.Unchanged && await CountsAsync(factory) == (1, 2, 3), r.ToString());

    // The next sync: the alt moved to an account of its own, one character and one account are gone.
    fake.Payload = Payload2(t0.AddHours(1));
    r = await connector.RefreshAsync(idA);
    Check("a newer answer replaces the source", r.Outcome == ConnectorRefreshOutcome.Imported, r.ToString());
    await using (var db = factory.CreateDbContext())
    {
        var chars = await db.LinkedCharacters.OrderBy(x => x.CharacterId).ToListAsync();
        Check("exactly the new answer's characters", chars.Select(x => (x.CharacterId, x.AccountId)).SequenceEqual([(900_000_001L, 1001L), (900_000_002L, 1003L)]),
            string.Join(", ", chars.Select(x => $"{x.CharacterId}@{x.AccountId}")));
        Check("exactly its accounts", (await db.LinkedAccounts.Select(a => a.AccountId).OrderBy(a => a).ToListAsync()).SequenceEqual([1001L, 1003L]));
        Check("the moved alt is the new account's main", (await db.LinkedAccounts.SingleAsync(a => a.AccountId == 1003)).MainCharacterId == 900_000_002);
        var src = await db.LinkedSources.SingleAsync();
        Check("source updated", src is { Accounts: 2, Characters: 2 } && src.GeneratedAt == t0.AddHours(1));
    }
    Check("noted only the first time", await LogCountAsync(factory, LogSeverity.Note) == 1);

    // The store keeps sources apart; an empty list empties only its own.
    var second = LinkedCharacterStore.Parse(Wrap("second", "2026-11-08T09:00:00Z",
        """[{ "id": 1001, "name": "Other Name", "characters": [{ "id": 900000001, "name": "Example Main", "main": true, "corporationId": 980000003 }] }]""")).Snapshot!;
    await store.ImportAsync(second);
    Check("the store keeps a second source beside the first", await CountsAsync(factory) == (2, 3, 3), (await CountsAsync(factory)).ToString());
    await store.ImportAsync(LinkedCharacterStore.Parse(Wrap("example", "2026-11-08T15:00:00Z", "[]")).Snapshot!, sourceKey: LinkedCharacterStore.SourceKey(idA));
    Check("an empty list empties its own source", await CountsAsync(factory) == (2, 1, 1), (await CountsAsync(factory)).ToString());
    await using (var db = factory.CreateDbContext())
        Check("and no other", await db.LinkedCharacters.AllAsync(x => x.Source == "second")
                              && (await db.LinkedSources.SingleAsync(s => s.Source == LinkedCharacterStore.SourceKey(idA))).Characters == 0);

    // ⚠️ Several connectors: an answer replaces only what the same connection gave before.
    fake.Payload = Payload2(t0.AddHours(4));
    r = await connector.RefreshAsync(idA);
    await using (var db = factory.CreateDbContext())
        Check("a connector's answer leaves other sources alone", r.Outcome == ConnectorRefreshOutcome.Imported
              && await CountsAsync(factory) == (2, 3, 3) && await db.LinkedSources.AnyAsync(x => x.Source == "second"), $"{r} {await CountsAsync(factory)}");
    await store.ClearSourceAsync("second");
    var kept = await CountsAsync(factory);
    Check("one source cleared, the other kept", kept == (1, 2, 2), kept.ToString());

    // ⚠️ A refused answer, an outage, or a refusal of the character: what was stored stays.
    var warned = await LogCountAsync(factory, LogSeverity.Warning);
    fake.Payload = Wrap("example", t0.AddDays(1).ToString("O"),
        """[{ "id": 5, "characters": [{ "id": 900000001, "main": true }, { "id": 900000002, "main": true }] }]""");
    r = await connector.RefreshAsync(idA);
    Check("an answer with two mains is refused", r.Outcome == ConnectorRefreshOutcome.Rejected && r.Message.Contains("two mains"), r.ToString());
    Check("and nothing it said is stored", await CountsAsync(factory) == kept);
    await connector.RefreshAsync(idA);
    Check("said once in the log, not every refresh", await LogCountAsync(factory, LogSeverity.Warning, expect: warned + 1) == warned + 1);

    fake.Payload = Payload2(t0.AddHours(4));
    fake.Down = true;
    r = await connector.RefreshAsync(idA);
    Check("the connector down: unreachable", r.Outcome == ConnectorRefreshOutcome.Unreachable, r.ToString());
    Check("and the links kept, and the connection", await CountsAsync(factory) == kept && (await Get(idA)).Connected);
    fake.Down = false;

    fake.SourceDown = true;
    r = await connector.RefreshAsync(idA);
    Check("its own source down: said so, and kept", r.Outcome == ConnectorRefreshOutcome.Unreachable && await CountsAsync(factory) == kept, r.ToString());
    fake.SourceDown = false;

    fake.LinkedNotFound = true;
    r = await connector.RefreshAsync(idA);
    Check("a 404 for linked characters: no longer offered, links kept",
          r.Outcome == ConnectorRefreshOutcome.NothingOffered && await CountsAsync(factory) == kept, r.ToString());
    fake.LinkedNotFound = false;

    fake.Redirect = true;
    r = await connector.RefreshAsync(idA);
    Check("a redirect is a refusal, not followed: key and links kept",
          r.Outcome == ConnectorRefreshOutcome.Unreachable && (await Get(idA)).Connected && await CountsAsync(factory) == kept, r.ToString());
    fake.Redirect = false;

    fake.Capabilities = [];
    r = await connector.RefreshAsync(idA);
    Check("linked characters no longer offered: nothing asked, links kept",
          r.Outcome == ConnectorRefreshOutcome.NothingOffered && await CountsAsync(factory) == kept, r.ToString());
    fake.Capabilities = ["linked-characters", "something-later"];
    r = await connector.RefreshAsync(idA);
    Check("a capability this build does not know is ignored", r.Outcome is ConnectorRefreshOutcome.Unchanged or ConnectorRefreshOutcome.Imported, r.ToString());

    fake.Allowed = false;
    r = await connector.RefreshAsync(idA);
    Check("the character no longer allowed: refused", r.Outcome == ConnectorRefreshOutcome.NotAllowed, r.ToString());
    Check("and the key and links kept", (await Get(idA)).Connected && await CountsAsync(factory) == kept);
    fake.Allowed = true;

    // The connector forgot the key: forgotten here, the links kept, the address kept for reconnecting.
    fake.IssuedKeys.Clear();
    r = await connector.RefreshAsync(idA);
    Check("a key the connector no longer knows: signed out", r.Outcome == ConnectorRefreshOutcome.SignedOut && !(await Get(idA)).Connected, r.ToString());
    Check("the links kept", await CountsAsync(factory) == kept);
    Check("the address kept", (await Get(idA)).Url?.ToString() == Url);

    // ⚠️ With no key left, Disconnect still forgets the links: nothing else ever would.
    await connector.DisconnectAsync(idA);
    Check("after that, Disconnect still forgets the links", await CountsAsync(factory) == (0, 0, 0));
    await Connect(connector, idA);

    // ⚠️ Another PC on the same database: what one of them does in the middle of the other's refresh.
    fake.Payload = Payload1(t0.AddHours(6));
    fake.AfterKeyCheck = async path => { if (path == "/v1/linked-characters") await other.DisconnectAsync(idA); };
    r = await connector.RefreshAsync(idA);
    fake.AfterKeyCheck = null;
    Check("a refresh whose answer arrives after another PC disconnected stores nothing",
          r.Outcome == ConnectorRefreshOutcome.NotConnected && await CountsAsync(factory) == (0, 0, 0) && !(await Get(idA)).Connected,
          $"{r} {await CountsAsync(factory)}");

    await Connect(connector, idA);
    var oldKey = fake.IssuedKeys.Single();
    fake.BeforeAnswer = async path =>
    {
        if (path != "/v1/me") return;
        fake.BeforeAnswer = null;
        fake.IssuedKeys.Remove(oldKey);                                   // the old key ended...
        await Connect(other, idA);                                        // ...and another PC connected anew
    };
    r = await connector.RefreshAsync(idA);
    var newer = await Get(idA);
    Check("a 401 for an old key leaves the newer key alone", r.Outcome == ConnectorRefreshOutcome.SignedOut && newer.Connected
          && fake.IssuedKeys.Count == 1 && fake.IssuedKeys[0] != oldKey, $"{r} connected={newer.Connected}");

    // Pointing the entry at a different address forgets the links it had, and nobody else's.
    await connector.RefreshAsync(idA);
    Check("links stored from the first address", (await CountsAsync(factory)).Item3 > 0);
    await store.ImportAsync(second);
    fake.Capabilities = [];     // so the refresh after connecting stores nothing of its own
    c = await Connect(connector, idA, UrlA2);
    fake.Capabilities = ["linked-characters"];
    Check("pointing an entry at another address forgets only that entry's links",
          c.Ok && await CountsAsync(factory, LinkedCharacterStore.SourceKey(idA)) == (0, 0, 0) && await CountsAsync(factory, "second") == (1, 1, 1), c.Message);
    await store.ClearSourceAsync("second");
    await Connect(connector, idA);

    // ⚠️ A connector that hangs until the request times out: disconnected all the same.
    fake.Hung = true;
    await connector.DisconnectAsync(idA);
    fake.Hung = false;
    Check("disconnecting from a hung connector still disconnects", !(await Get(idA)).Connected && await CountsAsync(factory) == (0, 0, 0));

    fake.IssuedKeys.Clear();    // the hung connector never heard about its key ending

    // Connecting again ends the key it replaces; disconnecting ends the key there and forgets what it gave here.
    c = await Connect(connector, idA);
    var key1 = fake.IssuedKeys.Single();
    c = await Connect(connector, idA);
    Check("connecting again ends the key it replaces", c.Ok && fake.IssuedKeys.Count == 1 && fake.IssuedKeys[0] != key1,
          string.Join(",", fake.IssuedKeys));
    var key2 = fake.IssuedKeys.Single();
    await connector.DisconnectAsync(idA);
    Check("disconnecting ends the key at the connector", fake.IssuedKeys.Count == 0
          && fake.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization == "Bearer " + key2));
    Check("and forgets it here, and every link", !(await Get(idA)).Connected && await CountsAsync(factory) == (0, 0, 0));

    // ⚠️ Another PC connects anew while this one's sign-out is still waiting: the key it stored is the one this
    // disconnect drops, so it is ended at the connector too, not left valid there.
    c = await Connect(connector, idA);
    var signedOutKey = fake.IssuedKeys.Single();
    fake.BeforeAnswer = async path =>
    {
        if (path != "/v1/auth/sign-out") return;
        fake.BeforeAnswer = null;
        await Connect(other, idA);
    };
    await connector.DisconnectAsync(idA);
    fake.BeforeAnswer = null;
    Check("a key another PC stored during the sign-out is ended too", c.Ok && fake.IssuedKeys.Count == 0 && !(await Get(idA)).Connected
          && fake.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization is { } auth && auth != "Bearer " + signedOutKey)
          && await CountsAsync(factory) == (0, 0, 0), $"keys={fake.IssuedKeys.Count} {await CountsAsync(factory)}");

    // ⚠️ Even with the connector down: disconnected is disconnected.
    c = await Connect(connector, idA);
    fake.Down = true;
    await connector.DisconnectAsync(idA);
    Check("disconnecting while it is down still disconnects", !(await Get(idA)).Connected && await CountsAsync(factory) == (0, 0, 0));
    fake.Down = false;
    fake.IssuedKeys.Clear();    // the connector, down, never heard about its key ending

    // ⚠️ Two clients storing at once, as two PCs on one PostgreSQL can: neither fails, and the newer wins.
    var raced = true;
    for (var round = 0; round < 10; round++)
    {
        var older = LinkedCharacterStore.Parse(Payload1(t0.AddDays(2).AddMinutes(round * 2))).Snapshot!;
        var newer2 = LinkedCharacterStore.Parse(Payload2(t0.AddDays(2).AddMinutes(round * 2 + 1))).Snapshot!;
        try { await Task.WhenAll(Task.Run(() => store.ImportAsync(older)), Task.Run(() => store.ImportAsync(newer2))); }
        catch (Exception ex) { raced = false; Console.WriteLine("  race: " + ex.Message.Split('\n')[0]); break; }
        await using var db = factory.CreateDbContext();
        if ((await db.LinkedSources.SingleAsync(x => x.Source == "example")).GeneratedAt != newer2.GeneratedAt
            || await db.LinkedCharacters.CountAsync() != 2) { raced = false; break; }
    }
    Check("two stores at once: neither fails, and the newer wins", raced);
    await store.ClearAsync();

    // ════ Several connectors, each for the corporations, alliances and functions the user chose ════

    fakeA.Capabilities = ["linked-characters", "groups", "something-later"];
    var tA = t0.AddDays(3);
    var tB = t0.AddDays(3).AddMinutes(30);

    // ⚠️ A connector chosen for no corporation or alliance is asked nothing but who it is.
    var idC = await connector.AddAsync();
    c = await Connect(connector, idC, UrlC);
    var entC = await Get(idC);
    var pollC = fakeC.Requests.FindLastIndex(x => x.Path == "/v1/auth/poll");
    Check("no scope: a sign-in by a character not on this install chooses nothing",
          c.Ok && entC is { Connected: true, Corporations.Count: 0, Alliances.Count: 0 }, $"{c.Message} {string.Join(",", entC.Corporations)}");
    Check("no scope: not used, and says so", c.Message == SettingsText.ConnectorNoScope
          && connector.LastResult(idC)?.Outcome == ConnectorRefreshOutcome.NotUsed, $"{c.Message} {connector.LastResult(idC)}");
    Check("no scope: only /v1/me was asked after the sign-in", fakeC.Requests.Skip(pollC + 1).Select(x => x.Path).SequenceEqual(["/v1/me"]),
          string.Join(",", fakeC.Requests.Skip(pollC + 1).Select(x => x.Path)));
    r = await connector.RefreshAsync(idC);
    await connector.RefreshAllAsync();
    Check("no scope: a refresh, and refreshing all, ask only /v1/me", r.Outcome == ConnectorRefreshOutcome.NotUsed
          && fakeC.Requests.Skip(pollC + 1).All(x => x.Path == "/v1/me") && fakeC.Requests.Skip(pollC + 1).Count() == 3,
          string.Join(",", fakeC.Requests.Skip(pollC + 1).Select(x => x.Path)));
    Check("no scope: nothing stored for it", await CountsAsync(factory, LinkedCharacterStore.SourceKey(idC)) == (0, 0, 0));
    var keyC = fakeC.IssuedKeys.Single();
    await connector.RemoveAsync(idC);
    Check("removed: its key ended at its connector", fakeC.IssuedKeys.Count == 0
          && fakeC.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization == "Bearer " + keyC));

    // Choices made before connecting are kept: no default replaces them.
    var idB = await connector.AddAsync();
    var ch = await connector.SaveChoicesAsync(idB, [980_000_004], [], []);
    Check("a scope chosen before connecting is saved", ch.Ok && (await Get(idB)) is { FunctionsChosen: false } b0 && b0.Corporations.SequenceEqual([980_000_004]), ch.Message);
    fakeB.Payload = Payload2(tB);
    c = await Connect(connector, idB, UrlB);
    var entB = await Get(idB);
    Check("connected after choosing: the scope chosen is kept", c.Ok && entB.Corporations.SequenceEqual([980_000_004]) && entB.Alliances.Count == 0,
          $"{c.Message} {string.Join(",", entB.Corporations)}");
    Check("and its functions defaulted", entB is { FunctionsChosen: true } && entB.Functions.SequenceEqual([LC]), string.Join(",", entB.Functions));

    // Two connectors, two corporations, the same label: two sources, kept apart.
    fakeA.Payload = Payload1(tA);
    c = await Connect(connector, idA);
    Check("reconnected: the choices made before are kept", c.Ok && (await Get(idA)).Corporations.SequenceEqual([980_000_001])
          && (await Get(idA)).Functions.SequenceEqual([LC, "groups"]), c.Message);
    var keyA = LinkedCharacterStore.SourceKey(idA);
    var keyB = LinkedCharacterStore.SourceKey(idB);
    await using (var db = factory.CreateDbContext())
    {
        var sources = await db.LinkedSources.OrderBy(s => s.Source).ToListAsync();
        Check("two connectors sending the same label: two sources, each the connector's own",
              sources.Count == 2 && sources.Any(s => s.Source == keyA && s.Label == "example") && sources.Any(s => s.Source == keyB && s.Label == "example"),
              string.Join(", ", sources.Select(s => $"{s.Source}={s.Label}")));
    }
    Check("each connector's rows counted apart", await CountsAsync(factory, keyA) == (1, 2, 3) && await CountsAsync(factory, keyB) == (1, 2, 2),
          $"{await CountsAsync(factory, keyA)} {await CountsAsync(factory, keyB)}");
    fakeA.Payload = Payload2(tA.AddHours(1));
    r = await connector.RefreshAsync(idA);
    Check("a newer answer from one replaces only its own rows", r.Outcome == ConnectorRefreshOutcome.Imported
          && await CountsAsync(factory, keyA) == (1, 2, 2) && await CountsAsync(factory, keyB) == (1, 2, 2), r.ToString());

    // ⚠️ Two connectors never have the same function for the same corporation, or the same alliance.
    ch = await connector.SaveChoicesAsync(idB, [980_000_001], [], [LC]);
    entB = await Get(idB);
    Check("the same function for the same corporation: refused, naming the other connector",
          !ch.Ok && ch.Message.Contains("Example Connector A") && ch.Message.Contains(ConnectorFunctions.Label(LC)) && ch.Message.Contains("Example Corp One"), ch.Message);
    Check("and nothing saved", entB.Corporations.SequenceEqual([980_000_004]) && entB.Functions.SequenceEqual([LC]));

    ch = await connector.SaveChoicesAsync(idA, [980_000_001], [990_000_001], [LC]);
    Check("a corporation and its alliance on one connector", ch.Ok, ch.Message);
    fakeB.Capabilities = ["linked-characters", "groups"];
    await connector.RefreshAsync(idB);
    ch = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC]);
    Check("the same function for the same alliance: refused", !ch.Ok && ch.Message.Contains("Example Alliance"), ch.Message);
    ch = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], ["groups"]);
    Check("the same alliance for another function: saved", ch.Ok, ch.Message);

    ch = await connector.SaveChoicesAsync(idA, [980_000_001], [], [LC]);
    var ch2 = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC, "groups"]);
    Check("a corporation on one connector and its alliance on another: allowed", ch.Ok && ch2.Ok, $"{ch.Message} {ch2.Message}");
    r = await connector.RefreshAsync(idB);
    Check("and the alliance's connector stores its links again", r.Outcome == ConnectorRefreshOutcome.Imported && await CountsAsync(factory, keyB) == (1, 2, 2), r.ToString());

    // The router: the corporation's connector first, then the alliance's; never a guess.
    var forA = await connector.ForCharacterAsync(LC, 980_000_001, 990_000_001);
    var forB = await connector.ForCharacterAsync(LC, 980_000_009, 990_000_001);
    Check("router: the corporation's connector beats the alliance's", forA?.Id == idA, forA?.Id.ToString() ?? "none");
    Check("router: otherwise the alliance's", forB?.Id == idB, forB?.Id.ToString() ?? "none");
    Check("router: nobody for a corporation and alliance nobody chose", await connector.ForCharacterAsync(LC, 980_000_009, 990_000_009) is null);
    Check("router: nobody for a function nobody chose", await connector.ForCharacterAsync(ConnectorFunctions.FleetParticipation, 980_000_001, 990_000_001) is null);
    ConnectorSnapshot Snap(int id) => new(id, null, $"x{id}", true, 0, "", [LC], "", [980_000_001], [], [LC], true);
    Check("router: a forced tie goes to the lowest id", ConnectorRouter.ForCharacter([Snap(5), Snap(3), Snap(4)], LC, 980_000_001, null)?.Id == 3);
    Check("covers: only its own corporations and alliances", forA!.Covers(980_000_001, null) && !forA.Covers(980_000_004, 990_000_001)
          && forB!.Covers(980_000_009, 990_000_001) && !forB.Covers(980_000_009, null));

    // Defaults after a new connection skip what another connector already has.
    var idD = await connector.AddAsync();
    fakeC.SignInId = 900_000_001; fakeC.SignInName = "Example Main";
    fakeC.Capabilities = ["linked-characters", "groups"];
    var pollD = fakeC.Requests.Count;
    c = await Connect(connector, idD, UrlC);
    var entD = await Get(idD);
    Check("defaults avoid overlap: not linked characters, which another connector has for the corporation",
          c.Ok && entD.Corporations.SequenceEqual([980_000_001]) && !entD.Functions.Contains(LC) && entD.Functions.Contains("groups"),
          $"{c.Message} {string.Join(",", entD.Corporations)} / {string.Join(",", entD.Functions)}");
    Check("and so its linked characters are never asked for", fakeC.Requests.Skip(pollD).All(x => x.Path != "/v1/linked-characters")
          && connector.LastResult(idD) is { Outcome: ConnectorRefreshOutcome.NotUsed } rd && rd.Message == SettingsText.ConnectorLinkedNotChosen);
    await connector.RemoveAsync(idD);

    // ⚠️ A choice saved before /v1/me has said what is offered does not stop the defaults when it does.
    var idX = await connector.AddAsync();
    fakeC.Capabilities = [];
    c = await Connect(connector, idX, UrlC);
    ch = await connector.SaveChoicesAsync(idX, [980_000_001], [], []);
    Check("saved while nothing is offered: the functions are not yet the user's choice",
          c.Ok && ch.Ok && (await Get(idX)) is { FunctionsChosen: false, Functions.Count: 0 }, $"{c.Message} {ch.Message}");
    fakeC.Capabilities = ["linked-characters", "groups"];
    await connector.RefreshAsync(idX);
    var entX = await Get(idX);
    Check("and the defaults come once it offers something, still avoiding overlap",
          entX is { FunctionsChosen: true } && entX.Functions.SequenceEqual(["groups"]), string.Join(",", entX.Functions));
    await connector.RemoveAsync(idX);

    // Only what is offered can be ticked; what was ticked can always be unticked.
    ch = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC, "groups", ConnectorFunctions.FleetParticipation, "something-later"]);
    entB = await Get(idB);
    Check("a function the connector does not offer, or this build does not know, is not saved",
          ch.Ok && entB.Functions.SequenceEqual([LC, "groups"]), string.Join(",", entB.Functions));
    fakeB.Capabilities = ["linked-characters"];
    await connector.RefreshAsync(idB);
    Check("a function saved before stays after the connector stopped offering it", (await Get(idB)).Functions.SequenceEqual([LC, "groups"]));
    ch = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC]);
    ch2 = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC, "groups"]);
    Check("and can be unticked, but not ticked again", ch.Ok && ch2.Ok && (await Get(idB)).Functions.SequenceEqual([LC]));

    // ⚠️ No longer used for linked characters: that connector's links go, nobody else's.
    var countsA = await CountsAsync(factory, keyA);
    ch = await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], []);
    Check("linked characters unticked: that connector's links cleared, the other's kept",
          ch.Ok && await CountsAsync(factory, keyB) == (0, 0, 0) && await CountsAsync(factory, keyA) == countsA,
          $"{await CountsAsync(factory, keyB)} {await CountsAsync(factory, keyA)}");
    await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC]);
    r = await connector.RefreshAsync(idB);
    Check("ticked again: stored again", r.Outcome == ConnectorRefreshOutcome.Imported && await CountsAsync(factory, keyB) == (1, 2, 2), r.ToString());
    ch = await connector.SaveChoicesAsync(idB, [], [], [LC]);
    var beforeB = fakeB.Requests.Count;
    r = await connector.RefreshAsync(idB);
    Check("every scope unticked: its links cleared, and only /v1/me asked", ch.Ok && await CountsAsync(factory, keyB) == (0, 0, 0)
          && r.Outcome == ConnectorRefreshOutcome.NotUsed && fakeB.Requests.Skip(beforeB).Select(x => x.Path).SequenceEqual(["/v1/me"]),
          $"{r} {string.Join(",", fakeB.Requests.Skip(beforeB).Select(x => x.Path))}");
    await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC]);
    await connector.RefreshAsync(idB);

    // ⚠️ Unticked on another PC while this one's answer was on its way: nothing stored.
    fakeB.Payload = Payload1(tB.AddHours(2));
    fakeB.AfterKeyCheck = async path =>
    {
        if (path != "/v1/linked-characters") return;
        fakeB.AfterKeyCheck = null;
        await other.SaveChoicesAsync(idB, [980_000_004], [990_000_001], []);
    };
    r = await connector.RefreshAsync(idB);
    fakeB.AfterKeyCheck = null;
    Check("a refresh racing an untick stores nothing", r.Outcome == ConnectorRefreshOutcome.NotConnected && await CountsAsync(factory, keyB) == (0, 0, 0),
          $"{r} {await CountsAsync(factory, keyB)}");
    await connector.SaveChoicesAsync(idB, [980_000_004], [990_000_001], [LC]);
    r = await connector.RefreshAsync(idB);
    Check("ticked again: the answer stored", r.Outcome == ConnectorRefreshOutcome.Imported, r.ToString());

    // Disconnecting one keeps the other, and keeps its own choices.
    var beforeDisconnect = await Get(idA);
    var countsB = await CountsAsync(factory, keyB);
    var keyOfA = fakeA.IssuedKeys.Single();
    var signOutsB = fakeB.Requests.Count(x => x.Path == "/v1/auth/sign-out");
    await connector.DisconnectAsync(idA);
    var afterDisconnect = await Get(idA);
    Check("disconnect one: its key and links gone", !afterDisconnect.Connected && await CountsAsync(factory, keyA) == (0, 0, 0));
    Check("the other keeps its key and links", (await Get(idB)).Connected && await CountsAsync(factory, keyB) == countsB);
    Check("the sign-out went to that connector only, with its key", fakeA.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization == "Bearer " + keyOfA)
          && fakeB.Requests.Count(x => x.Path == "/v1/auth/sign-out") == signOutsB);
    Check("its address and choices kept", afterDisconnect.Url?.ToString() == Url && afterDisconnect.Corporations.SequenceEqual(beforeDisconnect.Corporations)
          && afterDisconnect.Functions.SequenceEqual(beforeDisconnect.Functions) && afterDisconnect.FunctionsChosen);
    var owner = await connector.ForCharacterAsync(LC, 980_000_001, 990_000_001);
    Check("router: a disconnected owner still owns its corporation, and may not be asked", owner?.Id == idA && !owner.CanAsk(LC));
    c = await Connect(connector, idA);
    var reconnected = await Get(idA);
    Check("reconnecting keeps the choices", c.Ok && reconnected.Functions.SequenceEqual(beforeDisconnect.Functions)
          && reconnected.Corporations.SequenceEqual(beforeDisconnect.Corporations) && reconnected.CanAsk(LC), c.Message);

    // A key one connector forgot: only that one signed out.
    countsA = await CountsAsync(factory, keyA);
    countsB = await CountsAsync(factory, keyB);
    fakeB.IssuedKeys.Clear();
    r = await connector.RefreshAsync(idB);
    Check("a 401 on one: that one signed out, its links kept", r.Outcome == ConnectorRefreshOutcome.SignedOut
          && !(await Get(idB)).Connected && await CountsAsync(factory, keyB) == countsB, r.ToString());
    Check("and the other untouched", (await Get(idA)).Connected && await CountsAsync(factory, keyA) == countsA);

    // Removing one: its entry, links and key go; the other stays.
    c = await Connect(connector, idB, UrlB);
    var keyOfB = fakeB.IssuedKeys.Single();
    // ...and, as with Disconnect, another PC connecting it anew while the sign-out waits: that key ends too.
    fakeB.BeforeAnswer = async path =>
    {
        if (path != "/v1/auth/sign-out") return;
        fakeB.BeforeAnswer = null;
        await Connect(other, idB, UrlB);
    };
    await connector.RemoveAsync(idB);
    fakeB.BeforeAnswer = null;
    Check("removed while another PC connected it anew: that key ended too", fakeB.IssuedKeys.Count == 0
          && fakeB.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization is { } auth && auth != "Bearer " + keyOfB),
          $"keys={fakeB.IssuedKeys.Count}");
    Check("removed: the entry gone", await connector.GetAsync(idB) is null && (await connector.ListAsync()).All(x => x.Id != idB));
    Check("removed: its links gone, its key ended at its connector", await CountsAsync(factory, keyB) == (0, 0, 0) && fakeB.IssuedKeys.Count == 0
          && fakeB.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization == "Bearer " + keyOfB));
    Check("removed: the other intact", (await Get(idA)).Connected && await CountsAsync(factory, keyA) == countsA);

    // ⚠️ Problems are logged once per connector, not once for all.
    var idB2 = await connector.AddAsync();
    await connector.SaveChoicesAsync(idB2, [980_000_004], [], []);
    c = await Connect(connector, idB2, UrlB);
    Check("a second connector again", c.Ok, c.Message);
    var w0 = await LogCountAsync(factory, LogSeverity.Warning);
    fakeA.Down = true; fakeB.Down = true;
    for (var i = 0; i < 2; i++) { await connector.RefreshAsync(idA); await connector.RefreshAsync(idB2); }
    Check("two connectors down, refreshed twice each: one warning for each", await LogCountAsync(factory, LogSeverity.Warning, expect: w0 + 2) == w0 + 2);
    fakeA.Down = false; fakeA.SourceDown = true;
    await connector.RefreshAsync(idA);
    Check("a different problem on one: one more", await LogCountAsync(factory, LogSeverity.Warning, expect: w0 + 3) == w0 + 3);
    fakeA.SourceDown = false; fakeB.Down = false;
    await connector.RefreshAllAsync();

    // Connecting into an entry removed meanwhile, from another PC: the new key is ended, nothing comes back.
    var idE = await connector.AddAsync();
    var issuedC = fakeC.IssuedKeys.Count;
    fakeC.BeforeAnswer = async path =>
    {
        if (path != "/v1/auth/poll") return;
        fakeC.BeforeAnswer = null;
        await other.RemoveAsync(idE);
    };
    c = await Connect(connector, idE, UrlC);
    fakeC.BeforeAnswer = null;
    Check("connecting into an entry removed meanwhile: refused, the new key ended, no row back",
          !c.Ok && c.Message == SettingsText.ConnectorRemovedMeanwhile && fakeC.IssuedKeys.Count == issuedC && await connector.GetAsync(idE) is null,
          $"{c.Message} keys={fakeC.IssuedKeys.Count}");

    // What the connector offers, kept raw for later features.
    fakeA.Offers = new[] { new { capability = "linked-characters", subjects = "own" } };
    await connector.RefreshAsync(idA);
    var offers = (await Get(idA)).OffersJson;
    Check("offers stored raw", offers.Length > 0 && JsonDocument.Parse(offers).RootElement is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() == 1, offers);
    fakeA.Offers = null;
    await connector.RefreshAsync(idA);
    Check("no offers: nothing stored", (await Get(idA)).OffersJson == "");

    // What a connector can be chosen for: the user's own corporations and alliances, by name where known.
    var scopes = await connector.ScopeChoicesAsync();
    Check("scope choices: the user's corporations, named where known, NPC corporations left out",
          scopes.Where(x => !x.IsAlliance).Select(x => (x.Id, x.Name, x.OfOwnCharacter)).OrderBy(x => x.Id)
              .SequenceEqual([(980_000_001, "Example Corp One", true), (980_000_004, string.Format(CharactersText.SummaryCorpNumbered, 980_000_004), true)]),
          string.Join(", ", scopes.Select(x => $"{x.IsAlliance}:{x.Id}={x.Name}")));
    Check("scope choices: their alliances", scopes.Where(x => x.IsAlliance).Select(x => (x.Id, x.Name)).SequenceEqual([(990_000_001, "Example Alliance")]));
    await connector.SaveChoicesAsync(idB2, [980_000_004, 980_000_077], [], [LC]);
    scopes = await connector.ScopeChoicesAsync();
    Check("scope choices: one still chosen, with none of the user's characters, still shown",
          scopes.SingleOrDefault(x => x.Id == 980_000_077) is { IsAlliance: false, OfOwnCharacter: false });
    await connector.SaveChoicesAsync(idB2, [980_000_004], [], [LC]);

    // The one-time clean-up: the single connector's old preferences, and links kept for no entry.
    countsA = await CountsAsync(factory, keyA);
    var keyB2 = LinkedCharacterStore.SourceKey(idB2);
    var countsB2 = await CountsAsync(factory, keyB2);
    await using (var db = factory.CreateDbContext())
    {
        db.AppPreferences.AddRange(new AppPreference { Key = "connector.url", Value = Url }, new AppPreference { Key = "connector.key", Value = "old" },
                                   new AppPreference { Key = "connectorish", Value = "kept" });
        await db.SaveChangesAsync();
    }
    await store.ImportAsync(LinkedCharacterStore.Parse(Payload1(t0)).Snapshot!);
    Check("an old label-keyed source is there to clean up", await CountsAsync(factory, "example") == (1, 2, 3));
    await connector.CleanUpAsync();
    await using (var db = factory.CreateDbContext())
        Check("clean-up: the old preferences gone, nothing else", !await db.AppPreferences.AnyAsync(p => p.Key.StartsWith("connector."))
              && await db.AppPreferences.AnyAsync(p => p.Key == "connectorish"));
    Check("clean-up: the old source gone, each entry's own kept", await CountsAsync(factory, "example") == (0, 0, 0)
          && await CountsAsync(factory, keyA) == countsA && await CountsAsync(factory, keyB2) == countsB2,
          $"{await CountsAsync(factory, "example")} {await CountsAsync(factory, keyA)} {await CountsAsync(factory, keyB2)}");

    await connector.RemoveAsync(idA);
    await connector.RemoveAsync(idB2);
    Check("every connector removed: nothing left", (await connector.ListAsync()).Count == 0 && await CountsAsync(factory) == (0, 0, 0));
    await store.ClearAsync();

    var logged = await LogCountAsync(factory, LogSeverity.Error);
    await using (var db = factory.CreateDbContext())
        Check("no errors logged along the way", logged == 0,
              string.Join(" | ", await db.AppErrors.Where(e => e.Severity == (int)LogSeverity.Error).Select(e => e.Source + ": " + e.Message).ToListAsync()));
}

// ── Answers ─────────────────────────────────────────────────────────────────

static string Wrap(string source, string generatedAt, string accounts) => $$"""
    { "format": "eveconsole.linked-characters", "version": 1, "source": "{{source}}",
      "generatedAt": "{{generatedAt}}", "accounts": {{accounts}} }
    """;

static string Payload1(DateTimeOffset at) => Wrap("  example ", at.ToString("O"), """
    [
      { "id": 1001, "name": " Example Main ",
        "characters": [
          { "id": 900000001, "name": "Example Main", "main": true, "corporationId": 980000001, "allianceId": 990000001, "tokenValid": true },
          { "id": 900000002, "name": "Example Alt", "corporationId": 980000002, "allianceId": 0 } ] },
      { "id": 1002, "name": "Example Other",
        "characters": [ { "id": 900000003, "name": "Example Other", "corporationId": 980000001, "tokenValid": false } ] },
    ]
    """);

// The alt has moved to an account of its own, and account 1002 is gone with its character.
static string Payload2(DateTimeOffset at) => Wrap("example", at.ToString("O"), """
    [
      { "id": 1001, "name": "Example Main",
        "characters": [ { "id": 900000001, "name": "Example Main", "main": true, "corporationId": 980000001 } ] },
      { "id": 1003, "name": "Example Alt",
        "characters": [ { "id": 900000002, "name": "Example Alt", "main": true, "corporationId": 980000002 } ] }
    ]
    """);

static IEnumerable<(string What, string Json, string Says)> Refused(DateTimeOffset at)
{
    var when = at.ToString("O");
    yield return ("not JSON",                "{ \"format\": ", "JSON");
    yield return ("another kind of answer",  "{ \"format\": \"something-else\", \"version\": 1 }", "format");
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
        """[{ "id": 5, "characters": [{ "id": 900000001 }] }, { "id": 6, "characters": [{ "id": 900000001 }] }]"""), "both account");
    yield return ("two mains",               Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 900000001, "main": true }, { "id": 900000002, "main": true }] }]"""), "two mains");
    yield return ("an id too large for a corporation", Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 900000001, "corporationId": 99999999999 }] }]"""), "JSON");
}

// Every row, or one source's: a connector entry's (LinkedCharacterStore.SourceKey), or a label imported directly.
static async Task<(int, int, int)> CountsAsync(OneFactory factory, string? source = null)
{
    await using var db = factory.CreateDbContext();
    if (source is null)
        return (await db.LinkedSources.CountAsync(), await db.LinkedAccounts.CountAsync(), await db.LinkedCharacters.CountAsync());
    return (await db.LinkedSources.CountAsync(s => s.Source == source),
            await db.LinkedAccounts.CountAsync(a => a.Source == source),
            await db.LinkedCharacters.CountAsync(c => c.Source == source));
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

// ── A connector, as the interface describes one ─────────────────────────────

/// <summary>
/// Answers as docs/connector describes, from memory. Its sign-in stands in for EVE's: <see cref="Visit"/> is
/// the browser at its page, and <see cref="Person"/> says how that goes — "member" signs in as an allowed
/// character, "outsider" as one it refuses, "forged" fails the EVE checks, "declines" says no to the code,
/// "leaves" lets it expire. A person also declines whenever the page's code is not the one EVE Console showed.
/// </summary>
sealed class FakeConnector
{
    public string       Interface    = "eveconsole.connector";
    public string       Name         = "Example Connector";
    public long         SignInId     = 900_000_001;          // whom the sign-in turns out to be
    public string       SignInName   = "Example Main";
    public object?      Offers;                              // /v1/me's "offers", when set
    public int[]        Versions     = [1];
    public string[]     Capabilities = ["linked-characters"];
    public string       Payload      = "";
    public bool         Down;
    public bool         SourceDown;
    public bool         Allowed      = true;
    public bool         LinkedNotFound;
    public bool         Redirect;
    public bool         Hung;
    public Func<string, Task>? BeforeAnswer;     // before the key is checked
    public Func<string, Task>? AfterKeyCheck;    // the key was good when the request arrived
    public List<string> IssuedKeys   = [];
    public string       Person       = "member";
    public bool         ForeignCode;                // the page shows a code from somebody else's sign-in
    public string?      LoginPage;                  // a sign-in page other than its own
    public List<(Uri Page, string Code)> Visits = [];
    public IEnumerable<string> Secrets => _logins.Values.Select(l => l.Secret).Concat(_used);

    private sealed class LoginState { public string Secret = "", Code = "", Status = "pending", Error = ""; }
    private readonly Dictionary<string, LoginState> _logins = [];
    private readonly List<string> _used = [];
    private int _n;

    /// <summary>The browser at the sign-in page, and the person deciding, given the code EVE Console showed.</summary>
    public void Visit(Uri page, string? shownByEveConsole)
    {
        // Every page opened is seen, whatever it is: a page that should never have been opened must show.
        var id = System.Web.HttpUtility.ParseQueryString(page.Query)["login"] ?? "";
        if (!_logins.TryGetValue(id, out var login)) { Visits.Add((page, "")); return; }
        var onPage = ForeignCode ? "ZZZ-999" : login.Code;
        Visits.Add((page, onPage));

        if (onPage != shownByEveConsole) { login.Status = "declined"; return; }
        switch (Person)
        {
            case "member":   login.Status = "done"; break;
            case "outsider": login.Status = "refused"; login.Error = "not_allowed"; break;
            case "forged":   login.Status = "refused"; login.Error = "eve_token_invalid"; break;
            case "declines": login.Status = "declined"; break;
            case "leaves":   login.Status = "expired"; break;
        }
    }
    public List<(string Host, string Path, string? Authorization, string Body)> Requests = [];

    public async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage req)
    {
        var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
        var path = req.RequestUri!.AbsolutePath;
        Requests.Add((req.RequestUri.Host, path, req.Headers.Authorization?.ToString(), body));
        if (Down) throw new HttpRequestException("connection refused");
        // What HttpClient throws when its timeout runs out: an OperationCanceledException nobody asked for.
        if (Hung) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.");
        if (Redirect && !path.StartsWith("/v1/info") && !path.StartsWith("/v1/auth/start") && !path.StartsWith("/v1/auth/poll"))
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://elsewhere.example.com" + path) } };
        if (BeforeAnswer is { } before) await before(path);

        if (path == "/v1/info")
            return Json(HttpStatusCode.OK, new { @interface = Interface, versions = Versions, name = Name });

        if (path == "/v1/auth/start")
        {
            var id = $"L{++_n}";
            var login = new LoginState { Secret = Convert.ToHexString(Guid.NewGuid().ToByteArray()), Code = $"K{_n:D2}-4QX" };
            _logins[id] = login;
            var page = LoginPage ?? $"{req.RequestUri.GetLeftPart(UriPartial.Authority)}/login?login={id}";
            return Json(HttpStatusCode.OK, new { loginId = id, pollSecret = login.Secret, loginUrl = page, code = login.Code, interval = 0, expiresIn = 600 });
        }

        if (path == "/v1/auth/poll")
        {
            var poll   = JsonDocument.Parse(body).RootElement;
            var id     = poll.GetProperty("loginId").GetString() ?? "";
            var secret = poll.GetProperty("pollSecret").GetString();
            if (!_logins.TryGetValue(id, out var login) || login.Secret != secret) return Error(HttpStatusCode.NotFound, "login_unknown");
            if (login.Status == "pending") return Json(HttpStatusCode.OK, new { status = "pending" });

            // Answered once: a finished sign-in is gone after its answer is collected.
            _logins.Remove(id);
            _used.Add(login.Secret);
            if (login.Status != "done") return Json(HttpStatusCode.OK, new { status = login.Status, error = login.Error, message = "refused: " + login.Error });

            var key = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
            IssuedKeys.Add(key);
            return Json(HttpStatusCode.OK, new { status = "done", key, character = new { id = SignInId, name = SignInName } });
        }

        var bearer = req.Headers.Authorization is { Scheme: "Bearer", Parameter: { } p } ? p : null;
        if (bearer is null || !IssuedKeys.Contains(bearer)) return Error(HttpStatusCode.Unauthorized, "not_signed_in");
        if (AfterKeyCheck is { } after) await after(path);

        switch (path)
        {
            case "/v1/auth/sign-out":
                IssuedKeys.Remove(bearer);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "/v1/me":
                if (!Allowed) return Error(HttpStatusCode.Forbidden, "not_allowed");
                return Json(HttpStatusCode.OK, new { character = new { id = SignInId, name = SignInName }, capabilities = Capabilities, offers = Offers });
            case "/v1/linked-characters":
                if (!Allowed)    return Error(HttpStatusCode.Forbidden, "not_allowed");
                if (SourceDown)  return Error(HttpStatusCode.ServiceUnavailable, "source_unavailable");
                if (LinkedNotFound) return Error(HttpStatusCode.NotFound, "not_offered");
                if (!Capabilities.Contains("linked-characters")) return Error(HttpStatusCode.NotFound, "not_offered");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Payload, Encoding.UTF8, "application/json") };
            default:
                return Error(HttpStatusCode.NotFound, "not_offered");
        }
    }

    static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        Json(status, new { error = code, message = "refused: " + code });
}

sealed class FakeHandler(FakeHttp http) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => http.SendAsync(request);
}

/// <summary>Several connectors, each at its own address. A request anywhere else is recorded, and finds nobody.</summary>
sealed class FakeHttp(Dictionary<string, FakeConnector> byAuthority) : IHttpClientFactory
{
    public List<(string Host, string Path)> Unrouted = [];

    public FakeConnector? For(Uri uri) => byAuthority.GetValueOrDefault(uri.Authority);

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req)
    {
        if (For(req.RequestUri!) is { } connector) return connector.AnswerAsync(req);
        Unrouted.Add((req.RequestUri!.Host, req.RequestUri.AbsolutePath));
        throw new HttpRequestException("connection refused");
    }

    public HttpClient CreateClient(string name) => new(new FakeHandler(this), disposeHandler: true);
}
