using System.Net;
using System.Text;
using System.Text.Json;
using EveConsole.Agent;
using EveConsole.Data;
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
//  nothing at all, and does the connection behave: the EVE token sent once and never again, the
//  key on every call, and what was stored kept through the connector's outages and refusals?
//
//  WHY. Nothing here fails loudly. A store that keeps a character the answer dropped, lets an
//  older answer put back last week's links, or takes half of an answer it should have refused all
//  leave tables that look fine. A client that kept sending the EVE token, or dropped the cache the
//  moment a connector blinked, would work in every demonstration.
//
//  The connector is a fake behind the HTTP client factory: no socket, no browser, no EVE. The
//  sign-in itself is EVE's page, which nothing automated may drive.
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

// ── The answer on its own ───────────────────────────────────────────────────

engine = "parse";
var t0 = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

{
    var (snap, error) = LinkedCharacterStore.Parse(Payload1(t0));
    Check("a good answer reads", snap is not null, error ?? "");
    Check("its accounts and characters", snap is { Accounts.Count: 2, Characters.Count: 3 }, $"{snap?.Accounts.Count} / {snap?.Characters.Count}");
    Check("the main is the account's main", snap?.Accounts.Single(a => a.AccountId == 1001).MainCharacterId == 90_000_001);
    Check("an account with no main has none", snap?.Accounts.Single(a => a.AccountId == 1002).MainCharacterId is null);
    Check("alliance 0 or absent is none", snap?.Characters.Single(c => c.CharacterId == 90_000_002).AllianceId is null
                                       && snap?.Characters.Single(c => c.CharacterId == 90_000_003).AllianceId is null);
    Check("tokenValid absent is unknown, not false", snap?.Characters.Single(c => c.CharacterId == 90_000_002).TokenValid is null);
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

    var store = new LinkedCharacterStore(factory, errors);
    var changed = 0;
    store.Changed += () => changed++;

    var fake      = new FakeConnector { Payload = Payload1(t0) };
    var connector = new ConnectorService(new FakeHttp(fake), factory, store, signIn: null, errors);
    const string Url = "http://localhost:5054/";

    // Most installs: never connected.
    Check("not connected: no account", await connector.AccountAsync() is null);
    Check("not connected: a refresh does nothing", (await connector.RefreshAsync()).Outcome == ConnectorRefreshOutcome.NotConnected);
    Check("and asks nobody", fake.Requests.Count == 0);
    Check("nothing stored, nothing logged", await CountsAsync(factory) == (0, 0, 0) && await LogCountAsync(factory) == 0);
    Check("Connect is not offered by a build with no connector application", !connector.CanConnect);

    // Connecting to the wrong thing, or with the wrong token, connects nothing.
    fake.Interface = "something-else";
    var c = await connector.ConnectWithTokenAsync(Url, "token-member");
    Check("not a connector: refused", !c.Ok && fake.Requests.All(r => r.Path != "/v1/auth/eve"), c.Message);
    fake.Interface = "eveconsole.connector";

    fake.Versions = [2];
    c = await connector.ConnectWithTokenAsync(Url, "token-member");
    Check("a connector of a later version only: refused", !c.Ok && fake.Requests.All(r => r.Path != "/v1/auth/eve"), c.Message);
    fake.Versions = [1, 2];

    c = await connector.ConnectWithTokenAsync(Url, "token-forged");
    Check("a token the connector refuses: not connected", !c.Ok && await connector.AccountAsync() is null, c.Message);
    c = await connector.ConnectWithTokenAsync(Url, "token-outsider");
    Check("a character the connector refuses: not connected", !c.Ok && await connector.AccountAsync() is null, c.Message);
    c = await connector.ConnectWithTokenAsync("http://192.168.1.20:5054/", "token-member");
    Check("plain http to another device: not even asked", !c.Ok && fake.Requests.All(r => r.Host != "192.168.1.20"), c.Message);

    // Connected.
    c = await connector.ConnectWithTokenAsync(Url, "token-member");
    var account = await connector.AccountAsync();
    Check("connected", c.Ok, c.Message);
    Check("as the character the connector named", account is { CharacterId: 90_000_001, CharacterName: "Example Main", ConnectorName: "Example Connector" },
          account?.ToString() ?? "none");
    Check("the key the connector issued is kept", fake.IssuedKeys.Count == 1);

    // Refreshing stores what it says.
    var r = await connector.RefreshAsync();
    Check("a refresh stores the links", r.Outcome == ConnectorRefreshOutcome.Imported, r.ToString());
    Check("the rows it says", await CountsAsync(factory) == (1, 2, 3), (await CountsAsync(factory)).ToString());
    await using (var db = factory.CreateDbContext())
    {
        var src  = await db.LinkedSources.SingleAsync();
        var main = await db.LinkedCharacters.SingleAsync(x => x.CharacterId == 90_000_001);
        var alt  = await db.LinkedCharacters.SingleAsync(x => x.CharacterId == 90_000_002);
        Check("source: when, and how many", src is { Source: "example", Accounts: 2, Characters: 3 } && src.GeneratedAt == t0, $"{src.GeneratedAt:O}");
        Check("the main, with its corp, alliance and token", main is { AccountId: 1001, IsMain: true, CorporationId: 98_000_001, AllianceId: 99_000_001, TokenValid: true, Name: "Example Main" });
        Check("its alt, under the same account", alt is { AccountId: 1001, IsMain: false, CorporationId: 98_000_002, AllianceId: null, TokenValid: null });
        Check("the account's main", (await db.LinkedAccounts.SingleAsync(a => a.AccountId == 1001)).MainCharacterId == 90_000_001);
    }
    Check("its first arrival is noted once", await LogCountAsync(factory, LogSeverity.Note, expect: 1) == 1);
    Check("and the screen is told", changed == 1);

    // ⚠️ The EVE token went to the connector once, and never again; every later call carried the key.
    var key = fake.IssuedKeys.Single();
    // The refused attempts above stopped at /v1/info or before any call, so the one that connected is the
    // only call the token went in.
    var carried = fake.Requests.Where(x => x.Body.Contains("token-member") || (x.Authorization ?? "").Contains("token-")).ToList();
    Check("the EVE token was sent once, to the sign-in only", carried is [{ Path: "/v1/auth/eve" }],
          string.Join(", ", carried.Select(x => x.Path)));
    var afterConnect = fake.Requests.SkipWhile(x => !(x.Path == "/v1/auth/eve" && x.Body.Contains("token-member"))).Skip(1).ToList();
    Check("no call after it carried the token", afterConnect.All(x => !x.Body.Contains("token-") && !(x.Authorization ?? "").Contains("token-")));
    Check("every call after it carried the key", afterConnect.Count > 0 && afterConnect.All(x => x.Authorization == "Bearer " + key),
          string.Join(", ", afterConnect.Select(x => $"{x.Path}={x.Authorization}")));

    // The same answer again changes nothing; an older one never puts back old links.
    r = await connector.RefreshAsync();
    Check("the same answer again changes nothing", r.Outcome == ConnectorRefreshOutcome.Unchanged && changed == 1, r.ToString());
    fake.Payload = Payload2(t0.AddHours(-1));
    r = await connector.RefreshAsync();
    Check("an older answer is not stored over a newer", r.Outcome == ConnectorRefreshOutcome.Unchanged && await CountsAsync(factory) == (1, 2, 3), r.ToString());

    // The next sync: the alt moved to an account of its own, one character and one account are gone.
    fake.Payload = Payload2(t0.AddHours(1));
    r = await connector.RefreshAsync();
    Check("a newer answer replaces the source", r.Outcome == ConnectorRefreshOutcome.Imported, r.ToString());
    await using (var db = factory.CreateDbContext())
    {
        var chars = await db.LinkedCharacters.OrderBy(x => x.CharacterId).ToListAsync();
        Check("exactly the new answer's characters", chars.Select(x => (x.CharacterId, x.AccountId)).SequenceEqual([(90_000_001L, 1001L), (90_000_002L, 1003L)]),
            string.Join(", ", chars.Select(x => $"{x.CharacterId}@{x.AccountId}")));
        Check("exactly its accounts", (await db.LinkedAccounts.Select(a => a.AccountId).OrderBy(a => a).ToListAsync()).SequenceEqual([1001L, 1003L]));
        Check("the moved alt is the new account's main", (await db.LinkedAccounts.SingleAsync(a => a.AccountId == 1003)).MainCharacterId == 90_000_002);
        var src = await db.LinkedSources.SingleAsync();
        Check("source updated", src is { Accounts: 2, Characters: 2 } && src.GeneratedAt == t0.AddHours(1));
    }
    Check("noted only the first time", await LogCountAsync(factory, LogSeverity.Note) == 1);

    // The store keeps sources apart; an empty list empties only its own.
    var second = LinkedCharacterStore.Parse(Wrap("second", "2026-11-08T09:00:00Z",
        """[{ "id": 1001, "name": "Other Name", "characters": [{ "id": 90000001, "name": "Example Main", "main": true, "corporationId": 98000003 }] }]""")).Snapshot!;
    await store.ImportAsync(second);
    Check("the store keeps a second source beside the first", await CountsAsync(factory) == (2, 3, 3), (await CountsAsync(factory)).ToString());
    await store.ImportAsync(LinkedCharacterStore.Parse(Wrap("example", "2026-11-08T15:00:00Z", "[]")).Snapshot!);
    Check("an empty list empties its own source", await CountsAsync(factory) == (2, 1, 1), (await CountsAsync(factory)).ToString());
    await using (var db = factory.CreateDbContext())
        Check("and no other", await db.LinkedCharacters.AllAsync(x => x.Source == "second")
                              && (await db.LinkedSources.SingleAsync(s => s.Source == "example")).Characters == 0);

    // ⚠️ One connector is connected at a time: its answer replaces every other source.
    fake.Payload = Payload2(t0.AddHours(4));
    r = await connector.RefreshAsync();
    await using (var db = factory.CreateDbContext())
        Check("the connector's answer replaces every other source", r.Outcome == ConnectorRefreshOutcome.Imported
              && await CountsAsync(factory) == (1, 2, 2) && await db.LinkedSources.AllAsync(x => x.Source == "example"), $"{r} {await CountsAsync(factory)}");
    await store.ImportAsync(second);
    r = await connector.RefreshAsync();
    Check("even when its own answer is unchanged", r.Outcome == ConnectorRefreshOutcome.Unchanged && await CountsAsync(factory) == (1, 2, 2), $"{r} {await CountsAsync(factory)}");
    var kept = await CountsAsync(factory);

    // ⚠️ A refused answer, an outage, or a refusal of the character: what was stored stays.
    var warned = await LogCountAsync(factory, LogSeverity.Warning);
    fake.Payload = Wrap("example", t0.AddDays(1).ToString("O"),
        """[{ "id": 5, "characters": [{ "id": 90000001, "main": true }, { "id": 90000002, "main": true }] }]""");
    r = await connector.RefreshAsync();
    Check("an answer with two mains is refused", r.Outcome == ConnectorRefreshOutcome.Rejected && r.Message.Contains("two mains"), r.ToString());
    Check("and nothing it said is stored", await CountsAsync(factory) == kept);
    await connector.RefreshAsync();
    Check("said once in the log, not every refresh", await LogCountAsync(factory, LogSeverity.Warning, expect: warned + 1) == warned + 1);

    fake.Payload = Payload2(t0.AddHours(4));
    fake.Down = true;
    r = await connector.RefreshAsync();
    Check("the connector down: unreachable", r.Outcome == ConnectorRefreshOutcome.Unreachable, r.ToString());
    Check("and the links kept, and the connection", await CountsAsync(factory) == kept && await connector.AccountAsync() is not null);
    fake.Down = false;

    fake.SourceDown = true;
    r = await connector.RefreshAsync();
    Check("its own source down: said so, and kept", r.Outcome == ConnectorRefreshOutcome.Unreachable && await CountsAsync(factory) == kept, r.ToString());
    fake.SourceDown = false;

    fake.LinkedNotFound = true;
    r = await connector.RefreshAsync();
    Check("a 404 for linked characters: no longer offered, links kept",
          r.Outcome == ConnectorRefreshOutcome.NothingOffered && await CountsAsync(factory) == kept, r.ToString());
    fake.LinkedNotFound = false;

    fake.Redirect = true;
    r = await connector.RefreshAsync();
    Check("a redirect is a refusal, not followed: key and links kept",
          r.Outcome == ConnectorRefreshOutcome.Unreachable && await connector.AccountAsync() is not null && await CountsAsync(factory) == kept, r.ToString());
    fake.Redirect = false;

    fake.Capabilities = [];
    r = await connector.RefreshAsync();
    Check("linked characters no longer offered: nothing asked, links kept",
          r.Outcome == ConnectorRefreshOutcome.NothingOffered && await CountsAsync(factory) == kept, r.ToString());
    fake.Capabilities = ["linked-characters", "something-later"];
    r = await connector.RefreshAsync();
    Check("a capability this build does not know is ignored", r.Outcome is ConnectorRefreshOutcome.Unchanged or ConnectorRefreshOutcome.Imported, r.ToString());

    fake.Allowed = false;
    r = await connector.RefreshAsync();
    Check("the character no longer allowed: refused", r.Outcome == ConnectorRefreshOutcome.NotAllowed, r.ToString());
    Check("and the key and links kept", await connector.AccountAsync() is not null && await CountsAsync(factory) == kept);
    fake.Allowed = true;

    // The connector forgot the key: forgotten here, the links kept, the address kept for reconnecting.
    fake.IssuedKeys.Clear();
    r = await connector.RefreshAsync();
    Check("a key the connector no longer knows: signed out", r.Outcome == ConnectorRefreshOutcome.SignedOut && await connector.AccountAsync() is null, r.ToString());
    Check("the links kept", await CountsAsync(factory) == kept);
    Check("the address kept", await connector.UrlAsync() == Url);

    // ⚠️ With no key left, Disconnect still forgets the links: nothing else ever would.
    await connector.DisconnectAsync();
    Check("after that, Disconnect still forgets the links", await CountsAsync(factory) == (0, 0, 0));
    await connector.ConnectWithTokenAsync(Url, "token-member");
    await connector.RefreshAsync();

    // ⚠️ Another PC on the same database: what one of them does in the middle of the other's refresh.
    var other = new ConnectorService(new FakeHttp(fake), factory, store, signIn: null, errors);
    fake.Payload = Payload1(t0.AddHours(6));
    fake.AfterKeyCheck = async path => { if (path == "/v1/linked-characters") await other.DisconnectAsync(); };
    r = await connector.RefreshAsync();
    fake.AfterKeyCheck = null;
    Check("a refresh whose answer arrives after another PC disconnected stores nothing",
          r.Outcome == ConnectorRefreshOutcome.NotConnected && await CountsAsync(factory) == (0, 0, 0) && await connector.AccountAsync() is null,
          $"{r} {await CountsAsync(factory)}");

    await connector.ConnectWithTokenAsync(Url, "token-member");
    var oldKey = fake.IssuedKeys.Single();
    fake.BeforeAnswer = async path =>
    {
        if (path != "/v1/me") return;
        fake.BeforeAnswer = null;
        fake.IssuedKeys.Remove(oldKey);                                   // the old key ended...
        await other.ConnectWithTokenAsync(Url, "token-member");           // ...and another PC connected anew
    };
    r = await connector.RefreshAsync();
    var newer = await connector.AccountAsync();
    Check("a 401 for an old key leaves the newer key alone", r.Outcome == ConnectorRefreshOutcome.SignedOut && newer is not null
          && fake.IssuedKeys.Count == 1 && fake.IssuedKeys[0] != oldKey, $"{r} connected={newer is not null}");

    // Connecting to a different connector forgets the first one's links.
    await connector.RefreshAsync();
    Check("links stored from the first connector", (await CountsAsync(factory)).Item3 > 0);
    c = await connector.ConnectWithTokenAsync("http://127.0.0.1:5054/", "token-member");
    Check("connecting to another connector forgets them", c.Ok && await CountsAsync(factory) == (0, 0, 0), c.Message);
    await connector.ConnectWithTokenAsync(Url, "token-member");
    await connector.RefreshAsync();

    // ⚠️ A connector that hangs until the request times out: disconnected all the same.
    fake.Hung = true;
    await connector.DisconnectAsync();
    fake.Hung = false;
    Check("disconnecting from a hung connector still disconnects", await connector.AccountAsync() is null && await CountsAsync(factory) == (0, 0, 0));

    fake.IssuedKeys.Clear();    // the hung connector never heard about its key ending

    // Connecting again ends the key it replaces; disconnecting ends the key there and forgets what it gave here.
    c = await connector.ConnectWithTokenAsync(Url, "token-member");
    var key1 = fake.IssuedKeys.Single();
    c = await connector.ConnectWithTokenAsync(Url, "token-member");
    Check("connecting again ends the key it replaces", c.Ok && fake.IssuedKeys.Count == 1 && fake.IssuedKeys[0] != key1,
          string.Join(",", fake.IssuedKeys));
    var key2 = fake.IssuedKeys.Single();
    await connector.DisconnectAsync();
    Check("disconnecting ends the key at the connector", fake.IssuedKeys.Count == 0
          && fake.Requests.Any(x => x.Path == "/v1/auth/sign-out" && x.Authorization == "Bearer " + key2));
    Check("and forgets it here, and every link", await connector.AccountAsync() is null && await CountsAsync(factory) == (0, 0, 0));

    // ⚠️ Even with the connector down: disconnected is disconnected.
    c = await connector.ConnectWithTokenAsync(Url, "token-member");
    await connector.RefreshAsync();
    fake.Down = true;
    await connector.DisconnectAsync();
    Check("disconnecting while it is down still disconnects", await connector.AccountAsync() is null && await CountsAsync(factory) == (0, 0, 0));
    fake.Down = false;

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
          { "id": 90000001, "name": "Example Main", "main": true, "corporationId": 98000001, "allianceId": 99000001, "tokenValid": true },
          { "id": 90000002, "name": "Example Alt", "corporationId": 98000002, "allianceId": 0 } ] },
      { "id": 1002, "name": "Example Other",
        "characters": [ { "id": 90000003, "name": "Example Other", "corporationId": 98000001, "tokenValid": false } ] },
    ]
    """);

// The alt has moved to an account of its own, and account 1002 is gone with its character.
static string Payload2(DateTimeOffset at) => Wrap("example", at.ToString("O"), """
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
        """[{ "id": 5, "characters": [{ "id": 90000001 }] }, { "id": 6, "characters": [{ "id": 90000001 }] }]"""), "both account");
    yield return ("two mains",               Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 90000001, "main": true }, { "id": 90000002, "main": true }] }]"""), "two mains");
    yield return ("an id too large for a corporation", Wrap("example", when,
        """[{ "id": 5, "characters": [{ "id": 90000001, "corporationId": 99999999999 }] }]"""), "JSON");
}

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

// ── A connector, as the interface describes one ─────────────────────────────

/// <summary>
/// Answers as docs/connector describes, from memory. Two EVE tokens it accepts, by name: "token-member" for an
/// allowed character, "token-outsider" for one it refuses. Anything else is a token that failed its checks.
/// </summary>
sealed class FakeConnector
{
    public string       Interface    = "eveconsole.connector";
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
    public List<(string Host, string Path, string? Authorization, string Body)> Requests = [];

    public async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage req)
    {
        var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
        var path = req.RequestUri!.AbsolutePath;
        Requests.Add((req.RequestUri.Host, path, req.Headers.Authorization?.ToString(), body));
        if (Down) throw new HttpRequestException("connection refused");
        // What HttpClient throws when its timeout runs out: an OperationCanceledException nobody asked for.
        if (Hung) throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 60 seconds elapsing.");
        if (Redirect && path != "/v1/info" && path != "/v1/auth/eve")
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://elsewhere.example.com" + path) } };
        if (BeforeAnswer is { } before) await before(path);

        if (path == "/v1/info")
            return Json(HttpStatusCode.OK, new { @interface = Interface, versions = Versions, name = "Example Connector" });

        if (path == "/v1/auth/eve")
        {
            var token = JsonDocument.Parse(body).RootElement.GetProperty("accessToken").GetString();
            if (token == "token-outsider") return Error(HttpStatusCode.Forbidden, "not_allowed");
            if (token != "token-member")   return Error(HttpStatusCode.Unauthorized, "eve_token_invalid");
            var key = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
            IssuedKeys.Add(key);
            return Json(HttpStatusCode.OK, new { key, character = new { id = 90_000_001, name = "Example Main" } });
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
                return Json(HttpStatusCode.OK, new { character = new { id = 90_000_001, name = "Example Main" }, capabilities = Capabilities });
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

sealed class FakeHandler(FakeConnector connector) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => connector.AnswerAsync(request);
}

sealed class FakeHttp(FakeConnector connector) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new FakeHandler(connector), disposeHandler: true);
}
