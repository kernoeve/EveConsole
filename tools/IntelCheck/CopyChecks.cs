// A relayed copy: the same intel line said again in another channel within the copy window. It is no chatter and no
// news: stored as a copy of the report it repeats (IntelReportKind.Copy, ParentReportId the original), it adds no
// hostiles, pilots or counts, moves no last seen, and only says the report was heard in one more place. A clear and a
// sighting are never copies of each other. Checked on the matching rule alone, then in a parser pass on either engine,
// live and re-parsed.
//
// Every system, pilot, reporter and channel here is invented.
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EveConsole.Tools.IntelCheck;

internal static class CopyChecks
{
    private const int S1 = 30_000_001, S2 = 30_000_002;
    private const long Zeltrix = 90_000_002, Mirrel = 90_000_004;
    private const string ChA = "intelcopy-a", ChB = "intelcopy-b", ChC = "intelcopy-c";

    // ── The matching rule ────────────────────────────────────────────────────────────────────────────────────

    public static void Rule(Checks c)
    {
        var t0 = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        IntelReport Report(int id, string channel, int at, int kind, string message, int count = 0) => new()
        {
            Id = id, ReportedAt = ChatLogRules.FormatTimestamp(t0.AddSeconds(at)), ChannelName = channel, ReporterName = "Scout Alpha",
            SystemId = S1, SystemName = "QZ-X77", Kind = kind, PlayerCount = count, Message = message,
        };

        var held = new IntelDuplicates();
        var original = Report(1, ChA, 0, IntelReportKind.Sighting, "QZ-X77 Zeltrix Quorvane", 1);
        held.Add(original, [Zeltrix]);
        var relay = Report(0, ChB, 30, IntelReportKind.Sighting, "QZ-X77 Zeltrix Quorvane", 1);
        c.Check("copy rule: a relay names the report it copies", ReferenceEquals(held.CopyOf(relay, [Zeltrix]), original));

        // The earliest original when two are held (a copy of it held too, which must never be taken for one).
        var later = Report(2, ChC, 20, IntelReportKind.Sighting, "QZ-X77 Zeltrix Quorvane", 1);
        held.Add(later, [Zeltrix]);
        held.Add(new IntelReport
        {
            Id = 3, ReportedAt = ChatLogRules.FormatTimestamp(t0.AddSeconds(-10)), ChannelName = "intelcopy-d", ReporterName = "Scout Delta",
            SystemId = S1, Kind = IntelReportKind.Copy, Message = "QZ-X77 Zeltrix Quorvane", ParentReportId = 1,
        }, [Zeltrix]);
        c.Check("copy rule: the earliest held, never a stored copy", held.CopyOf(relay, [Zeltrix])?.Id == 1, $"{held.CopyOf(relay, [Zeltrix])?.Id}");

        // A clear and a sighting are never copies of each other: not by facts, and not by words either. The sighting's
        // words are the end of the clear's here, which a relay of a sighting would match.
        var clears = new IntelDuplicates();
        var clear = Report(10, ChA, 0, IntelReportKind.Clear, "Scout Alpha QZ-X77 Zeltrix Quorvane");
        clears.Add(clear, Array.Empty<long>());
        var sighting = Report(0, ChB, 20, IntelReportKind.Sighting, "QZ-X77 Zeltrix Quorvane", 1);
        c.Check("copy rule: a sighting is never a copy of a clear, even word for word", clears.CopyOf(sighting, [Zeltrix]) is null);
        var sightings = new IntelDuplicates();
        sightings.Add(Report(11, ChA, 0, IntelReportKind.Sighting, "QZ-X77 clr", 0), Array.Empty<long>());
        c.Check("copy rule: a clear is never a copy of a sighting", sightings.CopyOf(Report(0, ChB, 10, IntelReportKind.Clear, "QZ-X77 clr"), Array.Empty<long>()) is null);
        c.Check("copy rule: (setup) the same words as a sighting are a copy of it",
            sightings.CopyOf(Report(0, ChB, 10, IntelReportKind.Sighting, "QZ-X77 clr"), Array.Empty<long>()) is not null);
        // Other facts (a count of two), so only the words can match: what made the clear look like the sighting's original.
        c.Check("copy rule: (setup) the same words at the end of a SIGHTING's line are a copy of it",
            new Func<bool>(() =>
            {
                var words = new IntelDuplicates();
                words.Add(Report(12, ChA, 0, IntelReportKind.Sighting, "Scout Alpha QZ-X77 Zeltrix Quorvane", 2), [Zeltrix]);
                return words.CopyOf(sighting, [Zeltrix]) is not null;
            })());
        c.Check("copy rule: nor a clear of a clear: a relayed clear is stored as a clear",
            clears.CopyOf(Report(0, ChB, 10, IntelReportKind.Clear, "Scout Alpha QZ-X77 Zeltrix Quorvane"), Array.Empty<long>()) is null);
        var one = new IntelDuplicates();
        one.Add(original, [Zeltrix]);
        c.Check("copy rule: within one channel nothing is a copy",
            one.CopyOf(Report(0, ChA, 30, IntelReportKind.Sighting, "QZ-X77 Zeltrix Quorvane", 1), [Zeltrix]) is null);
        c.Check("copy rule: nor after the copy window",
            one.CopyOf(Report(0, ChB, 121, IntelReportKind.Sighting, "QZ-X77 Zeltrix Quorvane", 1), [Zeltrix]) is null);
    }

    // ── A parser pass, live and re-parsed, on either engine ──────────────────────────────────────────────────

    public static async Task SqliteAsync(Checks c, string profile)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "copies.db")}").Options;
        await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();
        var lease = new WorkerLease(new AppErrorLogger(null!));
        await lease.AcquireAsync();
        await PassAsync(c, "SQLite", options, lease);
    }

    /// <summary>On a database of its own on <paramref name="server"/>, made here and dropped at the end: the pass sets the
    /// intel channels and moves the watermark, which the other PostgreSQL checks share.</summary>
    public static async Task PostgresAsync(Checks c, string server)
    {
        var name = "intelcopy_" + Guid.NewGuid().ToString("N")[..12];
        var admin = new NpgsqlConnectionStringBuilder(server) { Pooling = false };
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var scratch = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;

        // The lease taken as on SQLite (no contest, nothing asked of a server): this check is the only worker there is.
        var lease = new WorkerLease(new AppErrorLogger(null!));
        await lease.AcquireAsync();
        try
        {
            DbEngine.Pin(DbBackend.Postgres);
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(scratch).Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                PostgresSchema.Apply(db);
            }
            await PassAsync(c, "PG", options, lease);
        }
        catch (Exception ex)
        {
            c.Check("PG copies: the checks ran to the end", false, $"{ex.GetType().Name}: {ex.Message}");
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

    private static async Task PassAsync(Checks c, string engine, DbContextOptions<AppDbContext> options, WorkerLease lease)
    {
        await using (var db = new AppDbContext(options))
        {
            db.SdeSolarSystems.AddRange(
                new SdeSolarSystem { SolarSystemId = S1, ConstellationId = 20_000_001, RegionId = 10_000_001, Name = "QZ-X77", Security = -0.4 },
                new SdeSolarSystem { SolarSystemId = S2, ConstellationId = 20_000_001, RegionId = 10_000_001, Name = "XQ1-Z2", Security = -0.4 });
            db.UniverseNames.AddRange(
                new UniverseName { EntityId = Zeltrix, Name = "Zeltrix Quorvane", Category = "character" },
                new UniverseName { EntityId = Mirrel,  Name = "Mirrel Okonda",    Category = "character" });
            // On a kill here, so the parser takes the names for characters without asking anyone.
            db.KillMailAttackers.AddRange(new KillMailAttacker { KillMailId = 1, CharacterId = Zeltrix },
                                          new KillMailAttacker { KillMailId = 1, CharacterId = Mirrel });
            foreach (var id in new[] { Zeltrix, Mirrel })
                db.CharacterAffiliations.Add(new CharacterAffiliation { CharacterId = id, CorporationId = 98_000_001, PulledAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var prefs = Prefs.Over(options);
        await prefs.SetAsync(MonitoringSettings.KeyChatIntelChannels, string.Join("\n", ChA, ChB, ChC));
        await prefs.LoadAsync();
        var logger  = new AppErrorLogger(null!);
        var signals = new ClientSignals(logger);
        signals.Start();
        var worker = new IntelService(new Factory(options), null!, new MonitoringSettings(prefs), logger, signals, lease);

        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20);
        string T(int s) => ChatLogRules.FormatTimestamp(t0.AddSeconds(s));
        var ids = new Dictionary<string, int>();
        async Task Lines(params (string Key, string Channel, string Sender, int At, string Text)[] lines)
        {
            await using var db = new AppDbContext(options);
            var rows = lines.Select(l => (l.Key, Row: new ChatMessage
            {
                OccurredAt = T(l.At), ChannelName = l.Channel, SenderName = l.Sender,
                Message = l.Text, SourceFile = l.Channel + ".txt", LineNumber = l.At,
            })).ToList();
            foreach (var (_, row) in rows) { db.ChatMessages.Add(row); await ChatMessageWrites.SaveAsync(db); }
            foreach (var (key, row) in rows) ids[key] = row.Id;
            await worker.ProcessNewAsync(waitForTurn: true);
        }

        // A sighting and its relay in one batch (the original has no id yet when the copy is found); a second relay in
        // the next, with the original stored; a clear and its relay; then the sighting said again long after.
        await Lines(("c1", ChA, "Scout Alpha", 0,  "QZ-X77  Zeltrix Quorvane"),
                    ("c2", ChB, "Scout Bravo", 30, "QZ-X77  Zeltrix Quorvane"));
        await Lines(("c3", ChC, "Scout Charlie", 60,  "QZ-X77 Zeltrix Quorvane"),
                    ("c4", ChA, "Scout Alpha",   90,  "XQ1-Z2 clr"),
                    ("c5", ChB, "Scout Bravo",   100, "XQ1-Z2 clr"));

        List<IntelReport> reports;
        List<IntelReportCharacter> pilots;
        List<IntelLastSeen> seen;
        List<int> chatter;
        await using (var db = new AppDbContext(options))
        {
            reports = await db.IntelReports.AsNoTracking().ToListAsync();
            pilots  = await db.IntelReportCharacters.AsNoTracking().ToListAsync();
            seen    = await db.IntelLastSeens.AsNoTracking().ToListAsync();
            chatter = await IntelSnapshotService.ChatterQuery(db, [ChA, ChB, ChC], int.MaxValue, "0").Select(m => m.Id).ToListAsync();
        }
        IntelReport? R(string key) => reports.FirstOrDefault(r => r.ChatMessageId == ids[key]);
        string Label(string key) => R(key) is { } r ? $"kind {r.Kind} parent {r.ParentReportId} count {r.PlayerCount}" : "no report";

        c.Check($"{engine} copies: the first line is a sighting", R("c1") is { Kind: IntelReportKind.Sighting, ParentReportId: null }, Label("c1"));
        c.Check($"{engine} copies: its relay in the same batch is a copy linked to it",
            R("c2") is { Kind: IntelReportKind.Copy } c2 && c2.ParentReportId == R("c1")?.Id, Label("c2"));
        c.Check($"{engine} copies: a relay in a later batch is linked to it too",
            R("c3") is { Kind: IntelReportKind.Copy } c3 && c3.ParentReportId == R("c1")?.Id, Label("c3"));
        c.Check($"{engine} copies: a copy names nobody and counts nothing",
            R("c2") is { PlayerCount: 0, Flags: 0, Ships: null, Gate: null, NoVisual: false } && R("c3") is { PlayerCount: 0 }
            && !pilots.Any(p => p.IntelReportId == R("c2")?.Id || p.IntelReportId == R("c3")?.Id));
        c.Check($"{engine} copies: but keeps the line, its channel and who said it",
            R("c2") is { ChannelName: ChB, ReporterName: "Scout Bravo", SystemId: S1 } && R("c3") is { ChannelName: ChC, SystemId: S1 });
        c.Check($"{engine} copies: no copy is chatter", !chatter.Contains(ids["c2"]) && !chatter.Contains(ids["c3"]),
            string.Join(",", chatter));
        c.Check($"{engine} copies: a clear's relay is a clear, never a copy",
            R("c4") is { Kind: IntelReportKind.Clear } && R("c5") is { Kind: IntelReportKind.Clear }, Label("c5"));
        c.Check($"{engine} copies: the original is not superseded by its copies", R("c1") is { Obsolete: false });
        c.Check($"{engine} copies: last seen stays at the original, not moved by a later copy",
            seen.SingleOrDefault(s => s.CharacterId == Zeltrix) is { } z && z.SeenAt == T(0) && z.ReportId == R("c1")?.Id,
            seen.SingleOrDefault(s => s.CharacterId == Zeltrix) is { } y ? $"{y.SeenAt} report {y.ReportId}" : "none");

        // The snapshot: the copies say where else it was heard, and nothing more.
        var now = DateTimeOffset.UtcNow;
        IntelSnapshot Build(IEnumerable<IntelReport> from) => IntelSnapshotBuilder.Build(new IntelSnapshotInput
        {
            Now = now, Settings = new IntelSettingsValues(), Reports = [.. from], ReportPilots = pilots, LastSeen = seen,
            SystemNames = new Dictionary<int, string> { [S1] = "QZ-X77", [S2] = "XQ1-Z2" },
        }, new IntelCampRegister(), 1);
        var snap = Build(reports);
        var without = Build(reports.Where(r => r.Kind != IntelReportKind.Copy));
        var original = snap.Feed.FirstOrDefault(e => e.ReportId == R("c1")?.Id);
        c.Check($"{engine} copies: no copy is a line of the feed",
            !snap.Feed.Any(e => e.ReportId == R("c2")?.Id || e.ReportId == R("c3")?.Id));
        c.Check($"{engine} copies: the original says where else it was heard",
            original?.EchoList.Select(x => x.Channel).SequenceEqual([ChB, ChC]) == true,
            string.Join(",", original?.EchoList.Select(x => x.Channel) ?? []));
        c.Check($"{engine} copies: the original's own count and pilots are its own",
            original is { Count: 0 } && original.Pilots.Select(p => p.CharacterId).SequenceEqual([Zeltrix]));
        c.Check($"{engine} copies: the system holds one pilot, as without the copies",
            snap.Systems.TryGetValue(S1, out var sys) && sys.PilotIds.SequenceEqual([Zeltrix]) && sys.Unnamed is null
            && without.Systems.TryGetValue(S1, out var bare) && bare.PilotIds.SequenceEqual(sys.PilotIds));
        c.Check($"{engine} copies: the copies add nothing anywhere: the same pilots, systems and lines",
            snap.Pilots.Keys.Order().SequenceEqual(without.Pilots.Keys.Order())
            && snap.Systems.Keys.Order().SequenceEqual(without.Systems.Keys.Order())
            && snap.Feed.Select(e => e.Key).SequenceEqual(without.Feed.Select(e => e.Key))
            && snap.Feed.Sum(e => e.Pilots.Count + e.Count) == without.Feed.Sum(e => e.Pilots.Count + e.Count));
        var clearLine = snap.Feed.FirstOrDefault(e => e.ReportId == R("c4")?.Id);
        c.Check($"{engine} copies: a clear's relay is read as the clear, heard in one more place",
            clearLine?.EchoList.Select(x => x.Channel).SequenceEqual([ChB]) == true && !snap.Feed.Any(e => e.ReportId == R("c5")?.Id));

        // The line as every intel view shows it: "×3", the places in its tooltip; a source filter for a relay's channel keeps it.
        var view = IntelLens.Project(snap, new IntelViewer { Sources = new HashSet<string> { ChC }, IncludeExpired = true }, now, _ => null);
        var row = view.Feed.FirstOrDefault(r => r.Event.ReportId == R("c1")?.Id);
        c.Check($"{engine} copies: shown under a relay's channel too", row is not null, string.Join(",", view.Feed.Select(r => r.Event.Key)));
        if (row is not null)
        {
            var line = new IntelEventRowVm(row, snap, now, false);
            c.Check($"{engine} copies: the line says ×3, with each channel in its tooltip",
                line.HasEchoes && line.EchoText.Contains('3') && line.EchoTip is { } tip && tip.Contains(ChB) && tip.Contains(ChC)
                && tip.Contains("Scout Charlie"), $"{line.EchoText} / {line.EchoTip}");
            c.Check($"{engine} copies: and its count of hostiles is still one", line.StateText.Contains('1') && !line.StateText.Contains('3'),
                line.StateText);
        }

        // The same sighting said again after the copy window is news again.
        await Lines(("c6", ChB, "Scout Bravo", 400, "QZ-X77  Zeltrix Quorvane"));

        var live = await LinksAsync(options);
        c.Check($"{engine} copies: said again after the copy window, it is a sighting", live.GetValueOrDefault(ids["c6"]).Kind == IntelReportKind.Sighting,
            $"{live.GetValueOrDefault(ids["c6"])}");

        // Re-parsed twice: the same copies, linked to the same originals.
        await worker.BackfillAsync();
        var again = await LinksAsync(options);
        await worker.BackfillAsync();
        var twice = await LinksAsync(options);
        static string Show(Dictionary<int, (int Kind, int Parent, int Pilots)> d) =>
            string.Join(" ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value.Kind}/{kv.Value.Parent}/{kv.Value.Pilots}"));
        c.Check($"{engine} copies: a re-parse keeps every copy and its link", Show(live) == Show(again), $"{Show(live)} | {Show(again)}");
        c.Check($"{engine} copies: and a second one too", Show(again) == Show(twice), $"{Show(again)} | {Show(twice)}");
        c.Check($"{engine} copies: (setup) the links were there to keep",
            live.GetValueOrDefault(ids["c2"]).Parent == ids["c1"] && live.GetValueOrDefault(ids["c3"]).Parent == ids["c1"]);

        signals.Stop();
    }

    /// <summary>Each report by its chat message: its kind, the chat message of the report it points at, and how many pilots it names.</summary>
    private static async Task<Dictionary<int, (int Kind, int Parent, int Pilots)>> LinksAsync(DbContextOptions<AppDbContext> options)
    {
        await using var db = new AppDbContext(options);
        var reports = await db.IntelReports.AsNoTracking().ToListAsync();
        var pilots = await db.IntelReportCharacters.AsNoTracking().ToListAsync();
        var byId = reports.ToDictionary(r => r.Id);
        return reports.ToDictionary(r => r.ChatMessageId, r => (
            r.Kind,
            r.ParentReportId is int p && byId.TryGetValue(p, out var parent) ? parent.ChatMessageId : 0,
            pilots.Count(x => x.IntelReportId == r.Id)));
    }
}
