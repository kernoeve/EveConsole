// Intel's rules, proven without the app.
//
// What a review cannot see and a quick try never meets: a schema that works on a new database and not on a user's
// existing one; a paste deleted by the next start because it has no chat message behind it; a watermark that one PC
// moves backwards or another skips past; chat lines that commit out of id order and are never parsed; a PC that is not
// the worker parsing or evaluating alarms anyway; where a pilot was last seen moved back by a late report or kill,
// or refused by PostgreSQL for naming a pilot twice; a direction read the wrong way round, or a follow-up line given a
// different parent live than in a re-parse. Each is checked here, on a scratch SQLite file, and on a scratch
// PostgreSQL database this check makes and drops itself when EVECONSOLE_PG names a server. It never opens the database
// that string names, and never the app's own.
//
// Every name here is invented.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using EveConsole.Alarms;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.Tools.IntelCheck;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// A writer process of the PostgreSQL race below: it inserts through the app's own ChatMessages path and exits.
if (args.Length == 3 && args[0] == PgChecks.WriterArg)
    return await PgChecks.WriterAsync(args[1], int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture));

// The Universe Map's intel marks drawn to pictures, for a reviewer to look at: --map-render <folder> <label>.
if (args.Length >= 2 && args[0] == MapRender.Arg)
    return MapRender.Run(args[1], args.Length >= 3 ? args[2] : "map");

// The panel, its single line and the tool's Board drawn to pictures with invented hostiles: --hostile-shots <folder>.
if (args.Length >= 2 && args[0] == HostileShots.Arg)
    return HostileShots.Run(args[1]);

var root = args.FirstOrDefault(a => !a.StartsWith('-')) is { } given && Directory.Exists(given) ? given : FindRepoRoot();
if (root is null)
{
    Console.Error.WriteLine("Could not locate EveConsole.csproj.");
    return 2;
}

var c = new Checks();

// ⚠️ A profile of its own, before anything reads the config: nothing here may read or write the user's settings,
// and the engine is pinned for the same reason (left to itself it reads the machine's config).
var profile = Path.Combine(Path.GetTempPath(), "intelcheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profile);
AppConfig.UseProfile(profile);
DbEngine.Pin(DbBackend.Sqlite);
SQLitePCL.Batteries_V2.Init();

try
{
    // ── The signals between PCs ──────────────────────────────────────────────────────────────────────────────
    foreach (var kind in new[] { IntelSignals.Chat, IntelSignals.Changed, IntelSignals.Reparse })
        c.Check($"signal {kind} reads back as itself", IntelSignals.KindOf(IntelSignals.Payload(kind)) == kind, IntelSignals.Payload(kind));
    c.Check("an alarm firing is not an intel signal", IntelSignals.KindOf("""{"Kind":"alarm","AlarmId":1,"Summary":"intel.chat"}""") is null);
    c.Check("an unknown intel kind is not one either", IntelSignals.KindOf("""{"Kind":"intel.other"}""") is null);
    c.Check("junk is not a signal, and does not throw", IntelSignals.KindOf("""{"Kind":"intel.""") is null && IntelSignals.KindOf("") is null);

    // ── SQLite: the startup statements, on an existing database and a new one ────────────────────────────────
    var statements = SqliteSchema.IntelStatements(root);
    c.Check("the startup list has the intel statements this check expects",
        statements.Any(s => s.Contains("CREATE TABLE IF NOT EXISTS \"IntelLastSeen\""))
        && statements.Any(s => s.Contains("\"IX_IntelReports_ChatMessage\"") && s.Contains("WHERE \"ChatMessageId\" > 0"))
        && statements.Count(s => s.StartsWith("DELETE FROM \"IntelReport", StringComparison.Ordinal)) == 2,
        $"{statements.Count} statement(s) found");

    await SqliteSchema.UpgradeAsync(c, profile, statements);
    await SqliteSchema.FreshAsync(c, profile, statements);

    // ── The watermark, on SQLite ────────────────────────────────────────────────────────────────────────────
    var wmOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "prefs.db")}").Options;
    await using (var db = new AppDbContext(wmOptions)) await db.Database.EnsureCreatedAsync();
    await Watermark.CheckAsync(c, "SQLite", wmOptions);

    // ── A PC that is not the worker: it asks, and does nothing itself ───────────────────────────────────────
    await NotTheWorker.CheckAsync(c, wmOptions);

    // ── A channel ticked on another PC: the worker parses it from the next pass ────────────────────────────
    await ChannelsFromAnotherPc.CheckAsync(c, profile);

    // ── Where each pilot was last seen ──────────────────────────────────────────────────────────
    // On a database the model makes, and on the one the startup statements brought up from the build
    // before (the table as users have it).
    var lsOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "lastseen.db")}").Options;
    await using (var db = new AppDbContext(lsOptions)) await db.Database.EnsureCreatedAsync();
    await LastSeenChecks.WriterAsync(c, "SQLite", lsOptions);
    await LastSeenChecks.FeedAsync(c, "SQLite", lsOptions);
    await LastSeenChecks.WriterAsync(c, "SQLite upgraded",
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "upgrade.db")}").Options);
    await LastSeenChecks.PassAsync(c, profile);

    // ── Direction, stored clears and follow-up lines ─────────────────────────────────────────────────────────
    FollowUpChecks.Parser(c);
    FollowUpChecks.Threads(c);
    await FollowUpChecks.DatabaseAsync(c, "SQLite threads", lsOptions);
    await FollowUpChecks.PassAsync(c, profile);

    // ── The read side: standings, distances, camps, the snapshot and the lens ────────────────────────────────
    ReadSideChecks.StandingChecks(c);
    await ReadSideChecks.SdeGroupChecks(c, root, profile);
    ReadSideChecks.CampChecks(c);
    await ReadSideChecks.DistanceChecks(c);
    ReadSideChecks.SnapshotChecks(c);
    ReadSideChecks.LensChecks(c);

    // ── The Pilots, Camps and Watch list tabs ─────────────────────────────────────────────────────────────────
    ReadSideChecks.WatchSetChecks(c);
    ReadSideChecks.WatchLensChecks(c);
    ReadSideChecks.CampsTabChecks(c);
    ReadSideChecks.LastSeenLineChecks(c);

    // ── The panel's rows ─────────────────────────────────────────────────────────────────────────────────────
    PanelChecks.Rows(c);

    // ── Hostiles first: who and what in a system, one row per system, the Pilots tab's sort ─────────────────
    HostileChecks.Run(c);

    // ── The Universe Map and the route planner ──────────────────────────────────────────────────────────────
    MapChecks.Run(c, root);

    // ── The intel alarm on intel as it stands ───────────────────────────────────────────────────────────────
    AlarmChecks.Run(c);
    DbContextOptions<AppDbContext> Scratch(string name) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, name)}").Options;
    await AlarmChecks.WatchedAsync(c, Scratch("alarmwatch.db"));
    await AlarmChecks.EvaluateAsync(c, Scratch("alarmeval.db"));
    await AlarmChecks.WakeServiceAsync(c, Scratch("alarmwake.db"));
    await AlarmChecks.WhoAndWhereWatchedAsync(c, Scratch("alarmwho.db"));
    await AlarmChecks.RePrimeAsync(c, "SQLite", Scratch("alarmprime.db"));
    await AlarmChecks.RouteStoreAsync(c, "SQLite", Scratch("routes.db"));
    await AlarmChecks.RouteWatchedAsync(c, Scratch("routewatch.db"));

    var rsOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "readside.db")}").Options;
    await using (var db = new AppDbContext(rsOptions)) await db.Database.EnsureCreatedAsync();
    await ReadSideChecks.ServiceAsync(c, "SQLite", rsOptions);
    await ReadSideChecks.RetentionAsync(c, "SQLite", rsOptions);
    await ReadSideChecks.WatchTableAsync(c, "SQLite", rsOptions);
    await ReadSideChecks.PilotSearchAsync(c, "SQLite", rsOptions);

    // ── Pasted Local and d-scan ──────────────────────────────────────────────────────────────────────────────
    PasteChecks.Classification(c);
    PasteChecks.Dscan(c);
    PasteChecks.Preview(c);
    var pasteOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "paste.db")}").Options;
    await using (var db = new AppDbContext(pasteOptions)) await db.Database.EnsureCreatedAsync();
    await PasteChecks.DatabaseAsync(c, "SQLite", pasteOptions);

    // ── PostgreSQL ───────────────────────────────────────────────────────────────────────────────────────────
    // ⚠️ From the environment, not an argument: a connection string carries a password.
    if (Environment.GetEnvironmentVariable("EVECONSOLE_PG") is { Length: > 0 } server)
        await PgChecks.RunAsync(c, server);
    else
        Console.WriteLine("No EVECONSOLE_PG set, so the PostgreSQL checks did not run.");
}
finally
{
    DbEngine.Pin(DbBackend.Sqlite);
    SqliteConnection.ClearAllPools();
    try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
}

Console.WriteLine($"Intel check: {c.Count} check(s), {c.Failures.Count} failure(s).");
foreach (var f in c.Failures) Console.WriteLine(f);
return c.Failures.Count == 0 ? 0 : 1;

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

namespace EveConsole.Tools.IntelCheck
{
    internal sealed class Checks
    {
        public int Count;
        public List<string> Failures { get; } = [];

        public void Check(string name, bool ok, string detail = "")
        {
            Interlocked.Increment(ref Count);
            lock (Failures)
                if (!ok) Failures.Add($"  FAIL  {name}{(detail.Length > 0 ? " | " + detail : "")}");
        }
    }

    internal sealed class Factory(DbContextOptions<AppDbContext> o) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(o);
    }

    internal static class Prefs
    {
        /// <summary>A preferences service of its own over the database: what a second PC would have.</summary>
        public static AppPreferencesService Over(DbContextOptions<AppDbContext> options)
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped(services, _ => new AppDbContext(options));
            var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
            return new AppPreferencesService(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(provider));
        }
    }

    /// <summary>Reports and messages the checks write, with every column the model needs.</summary>
    internal static class Rows
    {
        public static IntelReport Report(int chatMessageId, int source = IntelReportSource.Chat, string at = "2026-11-12T10:00:00Z") => new()
        {
            ReportedAt = at, ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = 30_000_001,
            SystemName = "QZ-X77", PlayerCount = 1, Message = "QZ-X77 Hostile Pilot", ChatMessageId = chatMessageId, Source = source,
        };

        public static ChatMessage Message(string file, long line, string at = "2026-11-12T10:00:00Z") => new()
        {
            OccurredAt = at, ChannelName = "intelcheck", SenderName = "Scout Alpha", Message = $"{file} {line}",
            SourceFile = file, LineNumber = line,
        };

        /// <summary>Whether a save failed on a unique index, and on nothing else.</summary>
        public static async Task<bool> RefusedAsync(DbContextOptions<AppDbContext> options, IntelReport report)
        {
            await using var db = new AppDbContext(options);
            db.IntelReports.Add(report);
            try { await db.SaveChangesAsync(); return false; }
            catch (DbUpdateException ex) when (AppDb.IsUniqueViolation(ex)) { return true; }
        }

        public static async Task<bool> SavedAsync(DbContextOptions<AppDbContext> options, IntelReport report)
        {
            await using var db = new AppDbContext(options);
            db.IntelReports.Add(report);
            try { await db.SaveChangesAsync(); return true; }
            catch (DbUpdateException) { return false; }
        }

        /// <summary>The columns the model has for an entity, as the database must have them.</summary>
        public static List<string> ModelColumns<T>(DbContextOptions<AppDbContext> options)
        {
            using var db = new AppDbContext(options);
            var entity = db.Model.FindEntityType(typeof(T))!;
            var table = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            return [.. entity.GetProperties().Select(p => p.GetColumnName(table)!)];
        }
    }

    // ── SQLite ────────────────────────────────────────────────────────────────────────────────────────────────

    internal static class SqliteSchema
    {
        /// <summary>
        /// The intel statements of the SQLite startup list, as the source has them, in order. Read out of
        /// App.axaml.cs as text, the way the fresh-install and upgrade checks read it: the list is inline code in
        /// the app's startup and cannot be called on its own.
        /// </summary>
        public static List<string> IntelStatements(string root)
        {
            var src = File.ReadAllText(Path.Combine(root, "App.axaml.cs"));
            var q3 = new string('"', 3);
            return [.. Regex.Matches(src, q3 + "(.*?)" + q3, RegexOptions.Singleline)
                .Select(m => m.Groups[1].Value.Trim())
                // Index names too ("IX_IntelReports_…"): a DROP names only the index.
                .Where(s => s.Contains("IntelReports") || s.Contains("IntelLastSeen") || s.Contains("IntelReportCharacters") || s.Contains("IntelWatchEntries") || s.Contains("CharacterRoutes"))
                // Statements with braces are formatted or interpolated: not the startup list's.
                .Where(s => !s.Contains('{'))];
        }

        /// <summary>Runs them as the startup does: each on its own, a failure (a column already there) ignored.</summary>
        public static void Apply(SqliteConnection conn, IEnumerable<string> statements)
        {
            foreach (var sql in statements)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                try { cmd.ExecuteNonQuery(); } catch (SqliteException) { }
            }
        }

        public static void Exec(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public static long Scalar(SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(cmd.ExecuteScalar());
        }

        public static HashSet<string> Columns(SqliteConnection conn, string table)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var r = cmd.ExecuteReader();
            var set = new HashSet<string>(StringComparer.Ordinal);
            while (r.Read()) set.Add(r.GetString(1));
            return set;
        }

        /// <summary>Index name → (unique, partial, columns).</summary>
        public static Dictionary<string, (bool Unique, bool Partial, string Columns)> Indexes(SqliteConnection conn, string table)
        {
            var list = new List<(string Name, bool Unique, bool Partial)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA index_list(\"{table}\")";
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((r.GetString(1), r.GetInt64(2) == 1, r.GetInt64(4) == 1));
            }
            var result = new Dictionary<string, (bool, bool, string)>(StringComparer.Ordinal);
            foreach (var (name, unique, partial) in list)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"PRAGMA index_info(\"{name}\")";
                using var r = cmd.ExecuteReader();
                var cols = new List<string>();
                while (r.Read()) cols.Add(r.IsDBNull(2) ? "?" : r.GetString(2));
                result[name] = (unique, partial, string.Join(",", cols));
            }
            return result;
        }

        private static readonly string[] LastSeenIndexes =
        [
            "IX_IntelLastSeen_SeenAt", "IX_IntelLastSeen_System", "IX_IntelLastSeen_WrittenAt",
            "IX_IntelLastSeen_Name", "IX_IntelLastSeen_Corporation", "IX_IntelLastSeen_Alliance",
        ];

        /// <summary>The shape every check below expects after the startup has run.</summary>
        private static void Shape(Checks c, string label, SqliteConnection conn, DbContextOptions<AppDbContext> options)
        {
            var reportCols = Columns(conn, "IntelReports");
            var missing = Rows.ModelColumns<IntelReport>(options).Where(m => !reportCols.Contains(m)).ToList();
            c.Check($"{label}: IntelReports has every column the model has", missing.Count == 0, string.Join(",", missing));

            var seenCols = Columns(conn, "IntelLastSeen");
            missing = [.. Rows.ModelColumns<IntelLastSeen>(options).Where(m => !seenCols.Contains(m))];
            c.Check($"{label}: IntelLastSeen exists with every column the model has", seenCols.Count > 0 && missing.Count == 0,
                seenCols.Count == 0 ? "no table" : string.Join(",", missing));

            var idx = Indexes(conn, "IntelReports");
            c.Check($"{label}: the chat-message index is unique and partial",
                idx.TryGetValue("IX_IntelReports_ChatMessage", out var cm) && cm.Unique && cm.Partial && cm.Columns == "ChatMessageId",
                string.Join("; ", idx.Select(kv => $"{kv.Key}={kv.Value}")));
            c.Check($"{label}: no other unique index on ChatMessageId is left",
                idx.Count(kv => kv.Value.Columns == "ChatMessageId") == 1 && !idx.ContainsKey("IX_IntelReports_ChatMessageId"));
            c.Check($"{label}: the time and reporter indexes are there",
                idx.ContainsKey("IX_IntelReports_Time") && idx.TryGetValue("IX_IntelReports_Reporter", out var rep)
                && rep.Columns == "ChannelName,ReporterName,ReportedAt");

            var seenIdx = Indexes(conn, "IntelLastSeen");
            var absent = LastSeenIndexes.Where(n => !seenIdx.ContainsKey(n)).ToList();
            c.Check($"{label}: IntelLastSeen has its indexes", absent.Count == 0, string.Join(",", absent));

            var watchCols = Columns(conn, "IntelWatchEntries");
            missing = [.. Rows.ModelColumns<IntelWatchEntry>(options).Where(m => !watchCols.Contains(m))];
            c.Check($"{label}: IntelWatchEntries exists with every column the model has", watchCols.Count > 0 && missing.Count == 0,
                watchCols.Count == 0 ? "no table" : string.Join(",", missing));
            var watchIdx = Indexes(conn, "IntelWatchEntries");
            c.Check($"{label}: one watch entry per thing (unique on Kind, EntityId)",
                watchIdx.TryGetValue("IX_IntelWatchEntries_Kind_Entity", out var we) && we.Unique && we.Columns == "Kind,EntityId",
                string.Join("; ", watchIdx.Select(kv => $"{kv.Key}={kv.Value}")));
            var routeCols = Columns(conn, "CharacterRoutes");
            missing = [.. Rows.ModelColumns<CharacterRoute>(options).Where(m => !routeCols.Contains(m))];
            c.Check($"{label}: CharacterRoutes exists with every column the model has", routeCols.Count > 0 && missing.Count == 0,
                routeCols.Count == 0 ? "no table" : string.Join(",", missing));
        }

        /// <summary>A database made by the build before this one, then this build's start, twice.</summary>
        public static async Task UpgradeAsync(Checks c, string profile, List<string> statements)
        {
            var path = Path.Combine(profile, "upgrade.db");
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();

            using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
            conn.Open();

            // The intel tables as the previous build left them: no new columns, no IntelLastSeen, and the
            // chat-message index unique over every row.
            Exec(conn, "DROP TABLE \"IntelReports\"");
            Exec(conn, "DROP TABLE \"IntelLastSeen\"");
            Exec(conn, "DROP TABLE \"IntelWatchEntries\"");
            Exec(conn, "DROP TABLE \"CharacterRoutes\"");
            Exec(conn, """CREATE TABLE "IntelReports" ("Id" INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, "ReportedAt" TEXT NOT NULL DEFAULT '', "ChannelName" TEXT NOT NULL DEFAULT '', "ReporterName" TEXT NOT NULL DEFAULT '', "SystemId" INTEGER NOT NULL DEFAULT 0, "SystemName" TEXT NOT NULL DEFAULT '', "PlayerCount" INTEGER NOT NULL DEFAULT 0, "Note" TEXT NULL, "Obsolete" INTEGER NOT NULL DEFAULT 0, "ObsoleteSetOn" TEXT NULL, "ChatMessageId" INTEGER NOT NULL DEFAULT 0, "ReporterCharacterId" INTEGER NULL, "NoVisual" INTEGER NOT NULL DEFAULT 0, "Message" TEXT NOT NULL DEFAULT '', "Flags" INTEGER NOT NULL DEFAULT 0, "Gate" TEXT NULL, "Ships" TEXT NULL)""");
            Exec(conn, """CREATE UNIQUE INDEX "IX_IntelReports_ChatMessageId" ON "IntelReports" ("ChatMessageId")""");

            // One chat line and the report parsed from it, from before the upgrade.
            await using (var db = new AppDbContext(options))
            {
                db.ChatMessages.Add(Rows.Message("old.txt", 1, "2026-11-12T09:00:00Z"));
                await db.SaveChangesAsync();
            }
            var messageId = (int)Scalar(conn, "SELECT MAX(\"Id\") FROM \"ChatMessages\"");
            Exec(conn, $"""INSERT INTO "IntelReports" ("ReportedAt", "ChannelName", "SystemId", "SystemName", "PlayerCount", "ChatMessageId", "Message") VALUES ('2026-11-12T09:00:00Z', 'intelcheck', 30000001, 'QZ-X77', 1, {messageId}, 'QZ-X77 Hostile Pilot')""");

            Apply(conn, statements);   // the first start of this build
            Shape(c, "upgrade", conn, options);
            c.Check("upgrade: the old report is kept, as a chat sighting",
                Scalar(conn, $"SELECT COUNT(*) FROM \"IntelReports\" WHERE \"ChatMessageId\" = {messageId} AND \"Kind\" = 0 AND \"Source\" = 0") == 1);

            await PastesAsync(c, "upgrade", options, conn, messageId, statements);
        }

        /// <summary>A database this build makes from nothing, then its start.</summary>
        public static async Task FreshAsync(Checks c, string profile, List<string> statements)
        {
            var path = Path.Combine(profile, "fresh.db");
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
            await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();

            using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
            conn.Open();
            Apply(conn, statements);
            Shape(c, "fresh", conn, options);

            await using (var db = new AppDbContext(options))
            {
                db.ChatMessages.Add(Rows.Message("fresh.txt", 1, "2026-11-12T09:00:00Z"));
                await db.SaveChangesAsync();
            }
            var messageId = (int)Scalar(conn, "SELECT MAX(\"Id\") FROM \"ChatMessages\"");
            c.Check("fresh: a chat report saves", await Rows.SavedAsync(options, Rows.Report(messageId)));
            await PastesAsync(c, "fresh", options, conn, messageId, statements);
        }

        /// <summary>
        /// Pastes (ChatMessageId 0) next to a chat report: any number of them, a chat message still reported once,
        /// and every one of them still there after the next start, while a real orphan still goes.
        /// </summary>
        private static async Task PastesAsync(Checks c, string label, DbContextOptions<AppDbContext> options,
                                              SqliteConnection conn, int messageId, List<string> statements)
        {
            c.Check($"{label}: a pasted Local saves with ChatMessageId 0",
                await Rows.SavedAsync(options, Rows.Report(0, IntelReportSource.LocalPaste)));
            c.Check($"{label}: and a second paste beside it",
                await Rows.SavedAsync(options, Rows.Report(0, IntelReportSource.DscanPaste)));
            c.Check($"{label}: one chat message still gives one report", await Rows.RefusedAsync(options, Rows.Report(messageId)));

            // An orphan: a report whose chat line is gone (a re-read file), newer than the oldest chat kept.
            c.Check($"{label}: an orphan to sweep saves",
                await Rows.SavedAsync(options, Rows.Report(999999, IntelReportSource.Chat, "2026-11-12T11:00:00Z")));

            Apply(conn, statements);   // the next start
            c.Check($"{label}: a restart keeps both pastes",
                Scalar(conn, "SELECT COUNT(*) FROM \"IntelReports\" WHERE \"ChatMessageId\" = 0") == 2);
            c.Check($"{label}: and the chat report",
                Scalar(conn, $"SELECT COUNT(*) FROM \"IntelReports\" WHERE \"ChatMessageId\" = {messageId}") >= 1);
            c.Check($"{label}: while a real orphan is still swept",
                Scalar(conn, "SELECT COUNT(*) FROM \"IntelReports\" WHERE \"ChatMessageId\" = 999999") == 0);

            // ⚠️ Mutation: the same sweep without its guard must take the pastes, or this check proves nothing.
            var unguarded = statements
                .Select(s => s.Replace("r.\"ChatMessageId\" > 0 AND ", "", StringComparison.Ordinal)
                              .Replace("\"ChatMessageId\" > 0 AND NOT EXISTS", "NOT EXISTS", StringComparison.Ordinal))
                .ToList();
            c.Check($"{label}: (mutation) the guard is in the source", !unguarded.SequenceEqual(statements));
            Apply(conn, unguarded);
            c.Check($"{label}: (mutation) without the guard the start deletes every paste",
                Scalar(conn, "SELECT COUNT(*) FROM \"IntelReports\" WHERE \"ChatMessageId\" = 0") == 0);
        }
    }

    // ── The watermark ────────────────────────────────────────────────────────────────────────────────────────

    internal static class Watermark
    {
        public static async Task CheckAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            // Two services over one database, each with its own copy: two PCs.
            var one = Prefs.Over(options);
            var two = Prefs.Over(options);
            await one.LoadAsync();
            await two.LoadAsync();
            const string key = MonitoringSettings.KeyIntelWatermark;

            var first = await one.SetLongIfGreaterAsync(key, 999);
            c.Check($"{engine}: the watermark is written where there was none", first == 999, first.ToString());
            c.Check($"{engine}: a second PC reads it fresh, not its own copy", two.GetLong(key) == 0 && await two.ReadFreshLongAsync(key) == 999);

            var raised = await two.SetLongIfGreaterAsync(key, 1000);
            c.Check($"{engine}: it moves forward by number, not by spelling (\"999\" > \"1000\" as text)", raised == 1000, raised.ToString());

            var back = await one.SetLongIfGreaterAsync(key, 50);
            c.Check($"{engine}: it never moves back", back == 1000 && await two.ReadFreshLongAsync(key) == 1000, back.ToString());
            c.Check($"{engine}: and the PC that tried has the true value after", one.GetLong(key) == 1000);

            await one.SetLongAsync(key, 0);
            c.Check($"{engine}: a re-parse's reset is the one move back", await two.ReadFreshLongAsync(key) == 0);

            // Through the parser's own settings view.
            var settings = new MonitoringSettings(one);
            await settings.AdvanceIntelWatermarkAsync(42);
            await settings.AdvanceIntelWatermarkAsync(41);
            c.Check($"{engine}: the parser's advance is forward-only too", await new MonitoringSettings(two).ReadIntelWatermarkAsync() == 42);
        }
    }

    // ── A PC that is not the worker ──────────────────────────────────────────────────────────────────────────

    internal static class NotTheWorker
    {
        public static async Task CheckAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            // ⚠️ SQLite is pinned, so signals are delivered in this process: nothing reaches any server.
            var logger  = new AppErrorLogger(null!);
            var signals = new ClientSignals(logger);
            signals.Start();
            var heard = new ConcurrentQueue<string>();
            signals.Received += heard.Enqueue;

            // Alarms: the loop is not running here, as on any PC that is not the worker.
            var alarms = new AlarmService(new Factory(options), "", new AlarmConditionRegistry([]), null!, logger, signals);
            await alarms.TriggerAsync("intel");
            var nudge = heard.FirstOrDefault(p => p.Contains(AlarmService.TriggerSignalKind, StringComparison.Ordinal));
            c.Check("alarms: a PC that is not the worker asks the worker instead of evaluating",
                nudge is not null && nudge.Contains("\"intel\"", StringComparison.Ordinal), string.Join(" ", heard));
            c.Check("alarms: the nudge is recognised", nudge is not null && alarms.TryApplySignal(nudge));
            c.Check("alarms: an intel signal is not taken for a nudge", !alarms.TryApplySignal(IntelSignals.Payload(IntelSignals.Changed)));
            c.Check("alarms: nor an alarm firing", !alarms.TryApplySignal("""{"Kind":"alarm","AlarmId":3,"Summary":"x"}"""));

            // Intel: a lease never taken is a PC that is not the worker.
            heard.Clear();
            var prefs = Prefs.Over(options);
            var notWorker = new IntelService(new Factory(options), null!, new MonitoringSettings(prefs), logger, signals, new WorkerLease(logger));
            c.Check("intel: a PC that is not the worker is not one", !notWorker.IsWorker);
            c.Check("intel: it parses nothing itself", await notWorker.ProcessNewAsync() == 0 && await notWorker.BackfillAsync() == 0);

            notWorker.LinesStored();
            notWorker.LinesStored();   // a burst is one signal
            await Task.Delay(800);
            var chats = heard.Count(p => IntelSignals.KindOf(p) == IntelSignals.Chat);
            c.Check("intel: new lines there tell the worker, once for a burst", chats == 1, $"{chats} signal(s)");

            heard.Clear();
            var asked = await notWorker.ReparseAsync();
            c.Check("intel: a re-parse there asks the worker", asked is null && heard.Any(p => IntelSignals.KindOf(p) == IntelSignals.Reparse));

            var changed = 0;
            notWorker.IntelChanged += () => Interlocked.Increment(ref changed);
            c.Check("intel: every PC hears that intel changed",
                notWorker.TryApplySignal(IntelSignals.Payload(IntelSignals.Changed)) && changed == 1);
            c.Check("intel: other signals are not intel's", !notWorker.TryApplySignal("""{"Kind":"activity","Rows":[]}"""));

            // The worker: on SQLite the lease is always held.
            var lease = new WorkerLease(logger);
            await lease.AcquireAsync();
            var worker = new IntelService(new Factory(options), null!, new MonitoringSettings(prefs), logger, signals, lease);
            c.Check("intel: on SQLite this PC is the worker", worker.IsWorker);

            signals.Stop();
        }
    }

    // ── A channel ticked on another PC ───────────────────────────────────────────────────────────────────────

    internal static class ChannelsFromAnotherPc
    {
        private const string One = "intelcheck-one";
        private const string Two = "intelcheck-two";
        private const long Scout = 90_000_001;

        /// <summary>
        /// Two PCs over one database. The worker (A) has the channel list it loaded at its start; B, where
        /// Settings is, ticks a second channel and stores lines in it, then a later line in the first. The
        /// worker's next pass must parse B's channel, and must not move the watermark past its lines.
        /// </summary>
        public static async Task CheckAsync(Checks c, string profile)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "channels.db")}").Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                // Just enough for a pass to place a sighting with no lookup: the system, the reporter, and the
                // reporter's corporation already known.
                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = 30_000_001, ConstellationId = 20_000_001, RegionId = 10_000_001, Name = "QZ-X77", Security = -0.4 });
                db.UniverseNames.Add(new UniverseName { EntityId = Scout, Name = "Scout Alpha", Category = "character" });
                db.CharacterAffiliations.Add(new CharacterAffiliation { CharacterId = Scout, CorporationId = 98_000_001, PulledAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }

            var a = Prefs.Over(options);   // the worker: a headless one, with no Settings
            var b = Prefs.Over(options);   // the PC where Settings is
            await b.SetAsync(MonitoringSettings.KeyChatIntelChannels, One);
            await a.LoadAsync();
            await b.LoadAsync();

            // ⚠️ SQLite is pinned, so the "intel changed" a pass sends stays in this process.
            var logger  = new AppErrorLogger(null!);
            var signals = new ClientSignals(logger);
            signals.Start();
            var lease = new WorkerLease(logger);
            await lease.AcquireAsync();
            var aSettings = new MonitoringSettings(a);
            var worker = new IntelService(new Factory(options), null!, aSettings, logger, signals, lease);

            var now = DateTimeOffset.UtcNow;
            var first = await StoreAsync(options, One, "QZ-X77 +1", now.AddHours(-3));
            var wrote = await worker.ProcessNewAsync(waitForTurn: true);
            c.Check("channels: the worker parses the channel it started with", wrote == 1 && await ReportedAsync(options, first), $"{wrote} written");

            // B ticks a second channel, then lines arrive: one in it, then a later one in the first.
            await b.SetAsync(MonitoringSettings.KeyChatIntelChannels, One + "\n" + Two);
            var inTwo = await StoreAsync(options, Two, "QZ-X77 +2", now.AddHours(-2));
            var later = await StoreAsync(options, One, "QZ-X77 +3", now.AddHours(-1));

            c.Check("channels: (setup) the worker's own copy still lacks the new channel",
                !aSettings.ChatIntelChannels.Contains(Two) && inTwo < later);

            // ⚠️ Mutation: what a pass over that copy would read. It sees only the later line, and the watermark
            // would move to it, past the new channel's line, for good.
            await using (var db = new AppDbContext(options))
            {
                var stale = aSettings.ChatIntelChannels.ToList();
                var after = await aSettings.ReadIntelWatermarkAsync();
                var read  = await db.ChatMessages.AsNoTracking().Where(m => stale.Contains(m.ChannelName) && m.Id > after)
                                    .Select(m => m.Id).ToListAsync();
                c.Check("channels: (mutation) a pass over the copy would skip the new channel's line",
                    read.Count == 1 && read[0] == later && read[0] > inTwo, string.Join(",", read));
            }

            wrote = await worker.ProcessNewAsync(waitForTurn: true);
            c.Check("channels: the next pass parses the channel ticked on the other PC",
                await ReportedAsync(options, inTwo), $"{wrote} written");
            c.Check("channels: and the later line in the first", await ReportedAsync(options, later));
            var watermark = await aSettings.ReadIntelWatermarkAsync();
            c.Check("channels: the watermark stops at the last line read, with nothing below it unread",
                watermark == later && !await UnreadBelowAsync(options, watermark, [One, Two]), watermark.ToString());

            // The regions set by hand, likewise: the worker reads B's, not its own copy.
            var map = new Dictionary<string, IReadOnlyList<string>> { [Two] = ["Invented Region"] };
            await b.SetAsync(MonitoringSettings.KeyIntelRegions, System.Text.Json.JsonSerializer.Serialize(map));
            var copy  = aSettings.IntelChannelRegions;
            var fresh = await aSettings.ReadIntelChannelRegionsAsync();
            c.Check("channels: the regions set on the other PC are read fresh",
                !copy.ContainsKey(Two) && fresh.TryGetValue(Two, out var set) && set.SequenceEqual(["Invented Region"]));

            signals.Stop();
        }

        private static async Task<int> StoreAsync(DbContextOptions<AppDbContext> options, string channel, string text, DateTimeOffset at)
        {
            await using var db = new AppDbContext(options);
            var row = new ChatMessage
            {
                OccurredAt = ChatLogRules.FormatTimestamp(at), ChannelName = channel, SenderName = "Scout Alpha",
                Message = text, SourceFile = channel + ".txt", LineNumber = at.ToUnixTimeSeconds(),
            };
            db.ChatMessages.Add(row);
            await ChatMessageWrites.SaveAsync(db);
            return row.Id;
        }

        private static async Task<bool> ReportedAsync(DbContextOptions<AppDbContext> options, int messageId)
        {
            await using var db = new AppDbContext(options);
            return await db.IntelReports.AsNoTracking().AnyAsync(r => r.ChatMessageId == messageId);
        }

        /// <summary>Whether a line in these channels at or below the watermark has no report.</summary>
        private static async Task<bool> UnreadBelowAsync(DbContextOptions<AppDbContext> options, long watermark, string[] channels)
        {
            await using var db = new AppDbContext(options);
            return await db.ChatMessages.AsNoTracking()
                .Where(m => channels.Contains(m.ChannelName) && m.Id <= watermark)
                .AnyAsync(m => !db.IntelReports.Any(r => r.ChatMessageId == m.Id));
        }
    }

    // ── PostgreSQL ───────────────────────────────────────────────────────────────────────────────────────────

    internal static class PgChecks
    {
        public const string WriterArg = "--pg-writer";
        private const string ScratchVar = "INTELCHECK_PG_SCRATCH";

        public static async Task RunAsync(Checks c, string server)
        {
            // ⚠️ A database of its own on that server, made here and dropped at the end. The database the string
            // names is only connected to, to make this one; nothing is read from it or written to it.
            var name = "intelcheck_" + Guid.NewGuid().ToString("N")[..12];
            var admin = new NpgsqlConnectionStringBuilder(server) { Pooling = false };
            await using (var conn = new NpgsqlConnection(admin.ConnectionString))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            var scratch = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
            Console.WriteLine($"PostgreSQL: scratch database {name} on {admin.Host}:{admin.Port}");

            try
            {
                DbEngine.Pin(DbBackend.Postgres);
                var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scratch).Options;
                await using (var db = new AppDbContext(options))
                {
                    await db.Database.EnsureCreatedAsync();
                    PostgresSchema.Apply(db);
                }

                await SchemaAsync(c, "PG fresh", options, scratch);
                await UpgradeAsync(c, options, scratch);
                // After the upgrade: IntelLastSeen as the startup statements made it.
                await LastSeenChecks.WriterAsync(c, "PG", options);
                await LastSeenChecks.FeedAsync(c, "PG", options);
                await FollowUpChecks.DatabaseAsync(c, "PG threads", options);
                await ReadSideChecks.ServiceAsync(c, "PG", options);
                await ReadSideChecks.RetentionAsync(c, "PG", options);
                await ReadSideChecks.WatchTableAsync(c, "PG", options);
                await ReadSideChecks.PilotSearchAsync(c, "PG", options);
                await PasteChecks.DatabaseAsync(c, "PG", options);
                await AlarmChecks.RePrimeAsync(c, "PG", options);
                await AlarmChecks.RouteStoreAsync(c, "PG", options);
                await Watermark.CheckAsync(c, "PG", options);
                await TwoWritersAsync(c, options, scratch);
                await RaceAsync(c, options);
            }
            catch (Exception ex)
            {
                c.Check("PostgreSQL checks ran to the end", false, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                DbEngine.Pin(DbBackend.Sqlite);
                NpgsqlConnection.ClearAllPools();
                await using var conn = new NpgsqlConnection(admin.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static async Task<long> ScalarAsync(string cs, string sql)
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }

        private static async Task ExecAsync(string cs, string sql)
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<HashSet<string>> ColumnsAsync(string cs, string table)
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t", conn);
            cmd.Parameters.AddWithValue("t", table);
            var set = new HashSet<string>(StringComparer.Ordinal);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
            return set;
        }

        private static async Task<Dictionary<string, string>> IndexesAsync(string cs, string table)
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = @t", conn);
            cmd.Parameters.AddWithValue("t", table);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) map[r.GetString(0)] = r.GetString(1);
            return map;
        }

        private static async Task SchemaAsync(Checks c, string label, DbContextOptions<AppDbContext> options, string cs)
        {
            var reportCols = await ColumnsAsync(cs, "IntelReports");
            var missing = Rows.ModelColumns<IntelReport>(options).Where(m => !reportCols.Contains(m)).ToList();
            c.Check($"{label}: IntelReports has every column the model has", missing.Count == 0, string.Join(",", missing));
            var seenCols = await ColumnsAsync(cs, "IntelLastSeen");
            missing = [.. Rows.ModelColumns<IntelLastSeen>(options).Where(m => !seenCols.Contains(m))];
            c.Check($"{label}: IntelLastSeen has every column the model has", seenCols.Count > 0 && missing.Count == 0,
                seenCols.Count == 0 ? "no table" : string.Join(",", missing));

            var idx = await IndexesAsync(cs, "IntelReports");
            c.Check($"{label}: the chat-message index is unique and partial",
                idx.TryGetValue("IX_IntelReports_ChatMessage", out var def)
                && def.Contains("UNIQUE", StringComparison.Ordinal) && def.Contains("WHERE", StringComparison.Ordinal),
                string.Join("; ", idx.Values));
            c.Check($"{label}: the old index unique over every row is gone", !idx.ContainsKey("IX_IntelReports_ChatMessageId"));
            var seenIdx = await IndexesAsync(cs, "IntelLastSeen");
            c.Check($"{label}: IntelLastSeen has its indexes", seenIdx.Keys.Count(k => k.StartsWith("IX_IntelLastSeen_", StringComparison.Ordinal)) == 6,
                string.Join(",", seenIdx.Keys));
            var watchCols = await ColumnsAsync(cs, "IntelWatchEntries");
            missing = [.. Rows.ModelColumns<IntelWatchEntry>(options).Where(m => !watchCols.Contains(m))];
            c.Check($"{label}: IntelWatchEntries has every column the model has", watchCols.Count > 0 && missing.Count == 0,
                watchCols.Count == 0 ? "no table" : string.Join(",", missing));
            var watchIdx = await IndexesAsync(cs, "IntelWatchEntries");
            c.Check($"{label}: one watch entry per thing (unique on Kind, EntityId)",
                watchIdx.TryGetValue("IX_IntelWatchEntries_Kind_Entity", out var wdef)
                && wdef.Contains("UNIQUE", StringComparison.Ordinal) && wdef.Contains("\"Kind\", \"EntityId\"", StringComparison.Ordinal),
                string.Join("; ", watchIdx.Values));            var routeCols = await ColumnsAsync(cs, "CharacterRoutes");
            missing = [.. Rows.ModelColumns<CharacterRoute>(options).Where(m => !routeCols.Contains(m))];
            c.Check($"{label}: CharacterRoutes has every column the model has", routeCols.Count > 0 && missing.Count == 0,
                routeCols.Count == 0 ? "no table" : string.Join(",", missing));

            // Pastes, and the next start.
            await ExecAsync(cs, "DELETE FROM \"IntelReports\"");
            await using (var db = new AppDbContext(options))
            {
                db.ChatMessages.Add(Rows.Message($"{label}.txt", 1));
                await db.SaveChangesAsync();
            }
            var messageId = (int)await ScalarAsync(cs, "SELECT MAX(\"Id\") FROM \"ChatMessages\"");
            c.Check($"{label}: a chat report saves", await Rows.SavedAsync(options, Rows.Report(messageId)));
            c.Check($"{label}: one chat message still gives one report", await Rows.RefusedAsync(options, Rows.Report(messageId)));
            c.Check($"{label}: two pastes with ChatMessageId 0 save",
                await Rows.SavedAsync(options, Rows.Report(0, IntelReportSource.LocalPaste))
                && await Rows.SavedAsync(options, Rows.Report(0, IntelReportSource.DscanPaste)));
            await using (var db = new AppDbContext(options)) PostgresSchema.Apply(db);   // the next start
            c.Check($"{label}: a restart keeps the pastes", await ScalarAsync(cs, "SELECT COUNT(*) FROM \"IntelReports\" WHERE \"ChatMessageId\" = 0") == 2);
        }

        /// <summary>The database a previous build left: the old index, none of the new columns, no IntelLastSeen.</summary>
        private static async Task UpgradeAsync(Checks c, DbContextOptions<AppDbContext> options, string cs)
        {
            await ExecAsync(cs, """
                DELETE FROM "IntelReports";
                DROP INDEX IF EXISTS "IX_IntelReports_ChatMessage";
                CREATE UNIQUE INDEX "IX_IntelReports_ChatMessageId" ON "IntelReports" ("ChatMessageId");
                DROP INDEX IF EXISTS "IX_IntelReports_Time";
                DROP INDEX IF EXISTS "IX_IntelReports_Reporter";
                ALTER TABLE "IntelReports" DROP COLUMN "Kind", DROP COLUMN "Source", DROP COLUMN "ParentReportId",
                                           DROP COLUMN "FromSystemId", DROP COLUMN "HeadingSystemId";
                DROP TABLE "IntelLastSeen";
                DROP TABLE "IntelWatchEntries";
                DROP TABLE "CharacterRoutes";
                """);
            await using (var db = new AppDbContext(options)) PostgresSchema.Apply(db);
            await SchemaAsync(c, "PG upgrade", options, cs);
        }

        /// <summary>
        /// Two writer processes insert at once through the app's ChatMessages path, while this one reads by
        /// watermark as the parser does: every line read once, none skipped, none doubled.
        /// </summary>
        private static async Task TwoWritersAsync(Checks c, DbContextOptions<AppDbContext> options, string cs)
        {
            const int batches = 150;
            var prefs = Prefs.Over(options);
            const string key = "intelcheck.two_writers";
            await prefs.SetLongAsync(key, await ScalarAsync(cs, "SELECT COALESCE(MAX(\"Id\"), 0) FROM \"ChatMessages\""));

            var writers = new[] { "writer-a", "writer-b" }.Select(tag => Start(tag, batches, cs)).ToList();
            var seen = new List<int>();

            async Task<int> DrainAsync()
            {
                var after = await prefs.ReadFreshLongAsync(key);
                await using var db = new AppDbContext(options);
                var ids = await db.ChatMessages.AsNoTracking()
                    .Where(m => m.Id > after && m.SourceFile.StartsWith("writer-"))
                    .OrderBy(m => m.Id).Take(100).Select(m => m.Id).ToListAsync();
                if (ids.Count == 0) return 0;
                seen.AddRange(ids);
                await prefs.SetLongIfGreaterAsync(key, ids[^1]);
                return ids.Count;
            }

            while (!writers.All(w => w.HasExited)) { await DrainAsync(); await Task.Delay(2); }
            while (await DrainAsync() > 0) { }

            foreach (var w in writers)
            {
                var err = await w.StandardError.ReadToEndAsync();
                c.Check($"PG writers: a writer process finished cleanly", w.ExitCode == 0, err.Trim());
            }

            await using var check = new AppDbContext(options);
            var all = await check.ChatMessages.AsNoTracking().Where(m => m.SourceFile.StartsWith("writer-")).Select(m => m.Id).ToListAsync();
            c.Check("PG writers: every line stored once", all.Count == 2 * batches * 5, $"{all.Count} of {2 * batches * 5}");
            c.Check("PG writers: the reader read none twice", seen.Count == seen.Distinct().Count(), $"{seen.Count - seen.Distinct().Count()} twice");
            var skipped = all.Except(seen).Count();
            c.Check("PG writers: and skipped none", skipped == 0, $"{skipped} skipped");
        }

        private static Process Start(string tag, int batches, string cs)
        {
            var self = Environment.ProcessPath!;
            var info = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo(self) { ArgumentList = { typeof(PgChecks).Assembly.Location, WriterArg, tag, batches.ToString(System.Globalization.CultureInfo.InvariantCulture) } }
                : new ProcessStartInfo(self) { ArgumentList = { WriterArg, tag, batches.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            // ⚠️ No window of its own: a console per child would land on the desktop.
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardError = true;
            info.RedirectStandardOutput = true;
            info.Environment[ScratchVar] = cs;
            info.Environment["EVECONSOLE_PG"] = "";
            return Process.Start(info)!;
        }

        /// <summary>The writer process: batches of five lines, each through ChatMessageWrites as the importers do.</summary>
        public static async Task<int> WriterAsync(string tag, int batches)
        {
            try
            {
                var profile = Path.Combine(Path.GetTempPath(), "intelcheck-writer-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(profile);
                AppConfig.UseProfile(profile);
                DbEngine.Pin(DbBackend.Postgres);
                var cs = Environment.GetEnvironmentVariable(ScratchVar);
                if (string.IsNullOrEmpty(cs)) { Console.Error.WriteLine("no scratch database given"); return 2; }
                var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cs).Options;
                for (var i = 0; i < batches; i++)
                {
                    await using var db = new AppDbContext(options);
                    for (var k = 0; k < 5; k++) db.ChatMessages.Add(Rows.Message(tag, i * 5L + k));
                    await ChatMessageWrites.SaveAsync(db);
                }
                try { Directory.Delete(profile, true); } catch (IOException) { }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        /// <summary>
        /// The skip itself, made to happen: writer A takes its ids and is slow to commit, writer B takes later ids and
        /// commits first, and the reader passes in between. With the lock B waits for A and nothing is skipped; without
        /// it A's lines fall below the watermark. The second half is the mutation that shows this check can see a skip.
        /// </summary>
        private static async Task RaceAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            async Task<(int Skipped, int Total)> RunAsync(bool serialise, string file)
            {
                var prefs = Prefs.Over(options);
                var key = "intelcheck.race." + file;
                await using (var db = new AppDbContext(options))
                    await prefs.SetLongAsync(key, await db.ChatMessages.MaxAsync(m => (int?)m.Id) ?? 0);

                var seen = new List<int>();
                async Task<int> DrainAsync()
                {
                    var after = await prefs.ReadFreshLongAsync(key);
                    await using var db = new AppDbContext(options);
                    var ids = await db.ChatMessages.AsNoTracking().Where(m => m.Id > after && m.SourceFile.StartsWith(file))
                        .OrderBy(m => m.Id).Select(m => m.Id).ToListAsync();
                    if (ids.Count == 0) return 0;
                    seen.AddRange(ids);
                    await prefs.SetLongIfGreaterAsync(key, ids[^1]);
                    return ids.Count;
                }

                var aHasIds = new TaskCompletionSource();
                var a = Task.Run(async () =>
                {
                    await using var db = new AppDbContext(options);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    if (serialise) await ChatMessageWrites.SerialiseAsync(db);
                    for (var k = 0; k < 5; k++) db.ChatMessages.Add(Rows.Message(file + "-a", k));
                    await db.SaveChangesAsync();
                    aHasIds.SetResult();
                    await Task.Delay(1500);
                    await tx.CommitAsync();
                });
                await aHasIds.Task;
                var b = Task.Run(async () =>
                {
                    await using var db = new AppDbContext(options);
                    await using var tx = await db.Database.BeginTransactionAsync();
                    if (serialise) await ChatMessageWrites.SerialiseAsync(db);
                    for (var k = 0; k < 5; k++) db.ChatMessages.Add(Rows.Message(file + "-b", k));
                    await db.SaveChangesAsync();
                    await tx.CommitAsync();
                });

                await Task.Delay(600);
                await DrainAsync();          // the parser's pass, while A is still committing
                await Task.WhenAll(a, b);
                while (await DrainAsync() > 0) { }

                await using var check = new AppDbContext(options);
                var all = await check.ChatMessages.AsNoTracking().Where(m => m.SourceFile.StartsWith(file)).Select(m => m.Id).ToListAsync();
                return (all.Except(seen).Count(), all.Count);
            }

            var locked = await RunAsync(serialise: true, "race-locked");
            c.Check("PG race: with the lock a slow commit is never skipped", locked is { Skipped: 0, Total: 10 }, locked.ToString());
            var unlocked = await RunAsync(serialise: false, "race-unlocked");
            c.Check("PG race: (mutation) without it the slow writer's lines are skipped", unlocked is { Skipped: 5, Total: 10 }, unlocked.ToString());
        }
    }
}
