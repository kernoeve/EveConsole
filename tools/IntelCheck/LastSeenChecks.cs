// Where each pilot was last seen: the one writer, the kills that feed it, the one-time seed and the parser's
// part. Run on a scratch SQLite file and on the scratch PostgreSQL database, so the one upsert is proven on
// both engines. Every name and id here is invented.
using System.Collections.Concurrent;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck
{
    internal static class LastSeenChecks
    {
        private const int S1 = 30_000_001, S2 = 30_000_002, S3 = 30_000_003;

        private static readonly DateTimeOffset Now1 = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Now2 = new(2026, 11, 12, 12, 1, 0, TimeSpan.Zero);
        private static readonly DateTimeOffset Now3 = new(2026, 11, 12, 12, 2, 0, TimeSpan.Zero);

        private static string T(int h, int m, int s = 0) => $"2026-11-12T{h:00}:{m:00}:{s:00}Z";

        private static LastSeenRow Report(long id, string at, int system, int reportId, int? ship = null, string? name = null) => new()
        {
            CharacterId = id, SeenAt = at, Source = IntelLastSeenSource.Report, SystemId = system, ReportId = reportId,
            ShipTypeId = ship, Name = name,
        };

        private static LastSeenRow Paste(long id, string at, int system, int reportId) => new()
        {
            CharacterId = id, SeenAt = at, Source = IntelLastSeenSource.LocalPaste, SystemId = system, ReportId = reportId,
        };

        private static LastSeenRow Kill(long id, string at, int system, int killId, long? corp = null, long? alliance = null,
                                        int? ship = null, bool victim = false) => new()
        {
            CharacterId = id, SeenAt = at, SystemId = system, KillMailId = killId, ShipTypeId = ship,
            Source = victim ? IntelLastSeenSource.KillVictim : IntelLastSeenSource.KillAttacker,
            CorporationId = corp, AllianceId = alliance, AffiliationAt = at, AffiliationSource = IntelAffiliationSource.Kill,
        };

        private static LastSeenRow Pulled(LastSeenRow r, long corp, string at) =>
            r with { CorporationId = corp, AllianceId = null, AffiliationAt = at, AffiliationSource = IntelAffiliationSource.Pulled };

        /// <summary>The writer's cases, on one engine.</summary>
        public static async Task WriterAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options))
            {
                await db.IntelLastSeens.ExecuteDeleteAsync();
                await db.AppPreferences.Where(p => p.Key == IntelLastSeenWriter.KeyNamedLifetime).ExecuteDeleteAsync();
            }

            async Task<int> Up(DateTimeOffset now, params LastSeenRow[] rows)
            {
                await using var db = new AppDbContext(options);
                return await IntelLastSeenWriter.UpsertAsync(db, rows, now: now);
            }
            async Task<IntelLastSeen> Get(long id)
            {
                await using var db = new AppDbContext(options);
                return await db.IntelLastSeens.AsNoTracking().FirstOrDefaultAsync(r => r.CharacterId == id) ?? new IntelLastSeen();
            }
            string Show(IntelLastSeen r) =>
                $"sys {r.SystemId} at {r.SeenAt} src {r.Source} rank {r.SourceRank} rep {r.ReportId} kill {r.KillMailId} ship {r.ShipTypeId} " +
                $"corp {r.CorporationId} ally {r.AllianceId} affAt {r.AffiliationAt} affSrc {r.AffiliationSource} prev {r.PrevSystemId}@{r.PrevSeenAt} " +
                $"since {r.InSystemSince} written {r.WrittenAt} name {r.Name}";
            var w1 = IntelLastSeenWriter.Format(Now1);
            var w2 = IntelLastSeenWriter.Format(Now2);
            var w3 = IntelLastSeenWriter.Format(Now3);

            // ── An older sighting after a newer one ──────────────────────────────────────────────
            const long older = 90_100_001;
            await Up(Now1, Pulled(Report(older, T(10, 5), S1, 101), 98_000_100, T(9, 0)));
            var n = await Up(Now2, Kill(older, T(10, 0), S2, 7001, corp: 98_000_200, alliance: 99_000_300));
            var r = await Get(older);
            c.Check($"{engine} last seen: an older kill after a newer report does not move the pilot",
                r.SystemId == S1 && r.SeenAt == T(10, 5) && r.Source == IntelLastSeenSource.Report && r.ReportId == 101
                && r.KillMailId is null && r.PrevSystemId is null, Show(r));
            c.Check($"{engine} last seen: but its affiliation replaces the pulled one, and the row says it was written",
                n == 1 && r.CorporationId == 98_000_200 && r.AllianceId == 99_000_300 && r.AffiliationAt == T(10, 0)
                && r.AffiliationSource == IntelAffiliationSource.Kill && r.WrittenAt == w2, $"{n} written; {Show(r)}");
            n = await Up(Now3, Report(older, T(9, 0), S3, 99));
            r = await Get(older);
            c.Check($"{engine} last seen: an older report changes nothing at all, not even when it was written",
                n == 0 && r.SystemId == S1 && r.WrittenAt == w2, $"{n} written; {Show(r)}");

            // ── The same second ──────────────────────────────────────────────────────────────────
            const long tie = 90_100_002;
            await Up(Now1, Report(tie, T(10, 10), S1, 5));
            await Up(Now1, Kill(tie, T(10, 10), S2, 7002));
            r = await Get(tie);
            c.Check($"{engine} last seen: in the same second a kill beats a chat report",
                r.SystemId == S2 && r.Source == IntelLastSeenSource.KillAttacker && r.SourceRank == 2 && r.KillMailId == 7002
                && r.PrevSystemId == S1 && r.PrevSeenAt == T(10, 10), Show(r));
            n = await Up(Now2, Paste(tie, T(10, 10), S3, 9));
            c.Check($"{engine} last seen: and a pasted Local", n == 0 && (await Get(tie)).SystemId == S2);

            const long tieBack = 90_100_003;
            await Up(Now1, Kill(tieBack, T(10, 10), S2, 7003));
            n = await Up(Now1, Report(tieBack, T(10, 10), S1, 6));
            c.Check($"{engine} last seen: a chat report after a kill in the same second does not win either",
                n == 0 && (await Get(tieBack)).SystemId == S2);

            const long tiePaste = 90_100_004;
            await Up(Now1, Report(tiePaste, T(10, 10), S1, 7));
            await Up(Now1, Paste(tiePaste, T(10, 10), S3, 8));
            r = await Get(tiePaste);
            c.Check($"{engine} last seen: a pasted Local beats a chat report in the same second",
                r.SystemId == S3 && r.Source == IntelLastSeenSource.LocalPaste && r.SourceRank == 1, Show(r));

            const long reparse = 90_100_005;
            await Up(Now1, Report(reparse, T(10, 10), S1, 5));
            var again = await Up(Now2, Report(reparse, T(10, 10), S1, 12));
            var lower = await Up(Now3, Report(reparse, T(10, 10), S1, 3));
            r = await Get(reparse);
            c.Check($"{engine} last seen: a re-parse's new report id replaces the old one; a lower id does not",
                again == 1 && lower == 0 && r.ReportId == 12 && r.InSystemSince == T(10, 10) && r.PrevSystemId is null,
                $"{again}/{lower}; {Show(r)}");

            // ── One pilot twice in one batch ─────────────────────────────────────────────────────
            const long twice = 90_100_006;
            n = await Up(Now1, Report(twice, T(10, 25), S2, 21), Report(twice, T(10, 20), S1, 20), Report(twice, T(10, 25), S2, 21));
            r = await Get(twice);
            c.Check($"{engine} last seen: one pilot twice in a batch (and once more exactly) is written in turn, oldest first",
                r.SystemId == S2 && r.ReportId == 21 && r.PrevSystemId == S1 && r.PrevSeenAt == T(10, 20) && r.InSystemSince == T(10, 25),
                $"{n} written; {Show(r)}");

            var big = new List<LastSeenRow>();
            for (var i = 0; i < 250; i++) big.Add(Report(91_000_000 + i, T(10, 0), S1, 1000 + i));
            for (var i = 0; i < 200; i++) big.Add(Report(91_000_000 + i, T(10, 30), S2, 2000 + i));
            big.Reverse();
            n = await Up(Now1, [.. big]);
            await using (var db = new AppDbContext(options))
            {
                var moved = await db.IntelLastSeens.CountAsync(x => x.CharacterId >= 91_000_000 && x.CharacterId < 91_000_200
                                                                  && x.SystemId == S2 && x.PrevSystemId == S1);
                var stayed = await db.IntelLastSeens.CountAsync(x => x.CharacterId >= 91_000_200 && x.CharacterId < 91_000_250
                                                                   && x.SystemId == S1 && x.PrevSystemId == null);
                c.Check($"{engine} last seen: a batch over several statements with pilots named twice",
                    moved == 200 && stayed == 50 && n == 450, $"{moved} moved, {stayed} stayed, {n} written");
            }
            n = await Up(Now2, [.. big]);
            c.Check($"{engine} last seen: the same batch again writes nothing", n == 0, $"{n} written");

            if (engine.StartsWith("PG", StringComparison.Ordinal))
            {
                // ⚠️ Mutation: why the rounds exist. One statement naming a key twice is refused.
                var refused = false;
                try
                {
                    await using var db = new AppDbContext(options);
                    await db.Database.ExecuteSqlRawAsync("""
                        INSERT INTO "IntelLastSeen" ("CharacterId", "SeenAt", "SystemId", "WrittenAt")
                        VALUES (91999999, '2026-11-12T10:00:00Z', 1, ''), (91999999, '2026-11-12T10:01:00Z', 2, '')
                        ON CONFLICT ("CharacterId") DO UPDATE SET "SystemId" = excluded."SystemId"
                        """);
                }
                catch (Npgsql.PostgresException) { refused = true; }
                c.Check($"{engine} last seen: (mutation) one statement with a pilot twice is refused", refused);
            }

            // ── When the stay began ──────────────────────────────────────────────────────────────
            const long stay = 90_100_007;
            await Up(Now1, Report(stay, T(11, 0), S1, 30));
            await Up(Now1, Report(stay, T(11, 10), S1, 31));
            c.Check($"{engine} last seen: seen again in the same system, the stay goes on", (await Get(stay)).InSystemSince == T(11, 0));
            await Up(Now1, Report(stay, T(11, 40), S1, 32));
            c.Check($"{engine} last seen: seen after longer than a sighting lasts, a new stay begins",
                (await Get(stay)).InSystemSince == T(11, 40), Show(await Get(stay)));
            await Up(Now1, Report(stay, T(11, 45), S2, 33));
            r = await Get(stay);
            c.Check($"{engine} last seen: a move begins a new stay and keeps where they were",
                r.InSystemSince == T(11, 45) && r.PrevSystemId == S1 && r.PrevSeenAt == T(11, 40), Show(r));
            n = await Up(Now1, Report(stay, T(11, 30), S3, 29));
            c.Check($"{engine} last seen: a late older sighting elsewhere leaves the stay alone",
                n == 0 && (await Get(stay)).InSystemSince == T(11, 45));

            await using (var db = new AppDbContext(options))
            {
                db.AppPreferences.Add(new AppPreference { Key = IntelLastSeenWriter.KeyNamedLifetime, Value = "60" });
                await db.SaveChangesAsync();
            }
            const long longStay = 90_100_008;
            await Up(Now1, Report(longStay, T(11, 0), S1, 40));
            await Up(Now1, Report(longStay, T(11, 40), S1, 41));
            c.Check($"{engine} last seen: the gap is the shared named lifetime (60 min here)",
                (await Get(longStay)).InSystemSince == T(11, 0), Show(await Get(longStay)));
            await using (var db = new AppDbContext(options))
                await db.AppPreferences.Where(p => p.Key == IntelLastSeenWriter.KeyNamedLifetime).ExecuteDeleteAsync();

            // ── The affiliation ──────────────────────────────────────────────────────────────────
            const long aff = 90_100_009;
            await Up(Now1, Pulled(Report(aff, T(10, 0), S1, 50), 1, T(9, 0)));
            r = await Get(aff);
            c.Check($"{engine} last seen: a pulled affiliation fills a new row",
                r.CorporationId == 1 && r.AffiliationSource == IntelAffiliationSource.Pulled && r.AffiliationAt == T(9, 0), Show(r));
            await Up(Now1, Pulled(Report(aff, T(10, 0), S1, 50), 2, T(9, 30)));
            c.Check($"{engine} last seen: a newer pulled one replaces an older pulled one", (await Get(aff)).CorporationId == 2);
            await Up(Now1, Kill(aff, T(8, 0), S2, 7010, corp: 3));
            r = await Get(aff);
            c.Check($"{engine} last seen: a kill's affiliation replaces a pulled one, even an older kill",
                r.CorporationId == 3 && r.AffiliationSource == IntelAffiliationSource.Kill && r.AffiliationAt == T(8, 0) && r.SystemId == S1, Show(r));
            n = await Up(Now1, Pulled(Report(aff, T(9, 0), S1, 49), 4, T(12, 0)));
            c.Check($"{engine} last seen: a pulled one never replaces a kill's, however new", n == 0 && (await Get(aff)).CorporationId == 3);
            n = await Up(Now1, Kill(aff, T(7, 0), S2, 7011, corp: 5));
            c.Check($"{engine} last seen: an older kill's does not replace a newer kill's", n == 0 && (await Get(aff)).CorporationId == 3);
            await Up(Now1, Kill(aff, T(9, 30), S2, 7012, corp: 6, alliance: 7));
            r = await Get(aff);
            c.Check($"{engine} last seen: a newer kill's does", r.CorporationId == 6 && r.AllianceId == 7 && r.AffiliationAt == T(9, 30), Show(r));
            await Up(Now1, Kill(aff, T(9, 45), S2, 7013, corp: 6));
            c.Check($"{engine} last seen: a newer kill with no alliance leaves the pilot with none", (await Get(aff)).AllianceId is null);

            // ── The hull, the name, and rows that cannot be placed ──────────────────────────────
            const long hull = 90_100_010;
            await Up(Now1, Kill(hull, T(10, 0), S1, 7020, ship: 587));
            await Up(Now1, Report(hull, T(10, 5), S1, 60));
            c.Check($"{engine} last seen: a report naming no hull keeps the hull known in the same system",
                (await Get(hull)).ShipTypeId == 587);
            await Up(Now1, Report(hull, T(10, 10), S2, 61));
            c.Check($"{engine} last seen: but not in another", (await Get(hull)).ShipTypeId is null);

            const long named = 90_100_011;
            await Up(Now1, Kill(named, T(10, 0), S1, 7030));
            n = await Up(Now2, Report(named, T(9, 0), S2, 70, name: "Invented Pilot"));
            r = await Get(named);
            c.Check($"{engine} last seen: an older sighting fills a missing name, and only that",
                n == 1 && r.Name == "Invented Pilot" && r.SystemId == S1 && r.WrittenAt == w2, Show(r));

            n = await Up(Now1,
                Report(0, T(10, 0), S1, 1), Report(90_100_012, T(10, 0), 0, 1), Report(90_100_013, "not a time", S1, 1));
            c.Check($"{engine} last seen: rows that cannot be placed are left out", n == 0 && (await Get(90_100_012)).CharacterId == 0);
            _ = w1; _ = w3;
        }

        // ── The kills, the seed and the parser ───────────────────────────────────────────────────

        private static EsiKillMailFull Mail(int id, DateTimeOffset at, int system, long? victim, params long?[] attackers) => new(
            id, at, system, null, null,
            new EsiKillVictim(victim, 98_000_500, null, null, 670, 1000, null, null),
            [.. attackers.Select(a => new EsiKillAttacker(a, a is null ? null : 98_000_600, a is null ? null : 99_000_700,
                                                         a is null ? 500_001 : null, 100, false, 0f, 17_738, null))]);

        /// <summary>What the importers hand on, the seed, and (on SQLite) a parser pass.</summary>
        public static async Task FeedAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            var logger = new AppErrorLogger(null!);
            var signals = new ClientSignals(logger);
            var heard = new ConcurrentQueue<string>();
            var sqlite = !engine.StartsWith("PG", StringComparison.Ordinal);
            if (sqlite) { signals.Start(); signals.Received += heard.Enqueue; }

            var feed = new IntelLastSeenFeed(new Factory(options), Prefs.Over(options), signals, logger);
            var now = DateTimeOffset.UtcNow;
            var at = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, TimeSpan.Zero);

            // The rows one kill gives.
            var mail = Mail(8001, at.AddHours(-1), S1, 92_000_001, 92_000_002, null, 92_000_002, 92_000_003);
            var rows = IntelLastSeenFeed.Rows(IntelLastSeenFeed.Facts(mail), new Dictionary<long, string> { [92_000_002] = "Invented Attacker" }).ToList();
            c.Check($"{engine} kills: the victim and each character attacker once, no NPC",
                rows.Count == 3 && rows.Count(x => x.Source == IntelLastSeenSource.KillVictim && x.CharacterId == 92_000_001 && x.ShipTypeId == 670) == 1
                && rows.Count(x => x.Source == IntelLastSeenSource.KillAttacker) == 2
                && rows.All(x => x.AffiliationSource == IntelAffiliationSource.Kill && x.KillMailId == 8001 && x.SystemId == S1)
                && rows.Single(x => x.CharacterId == 92_000_002).Name == "Invented Attacker",
                string.Join(" | ", rows));

            // The importer says which kills it newly stored.
            var importer = new ZkillboardKillImportService();
            var none = new HashSet<long>();
            var recent = Mail(8002, at.AddDays(-2), S2, 92_000_011, 92_000_012);
            var old    = Mail(8003, at.AddDays(-10), S3, null, 92_000_013);
            var npcOnly = Mail(8004, at.AddMinutes(-5), S1, 92_000_014, [null]);
            IReadOnlyList<EsiKillMailFull> added;
            await using (var db = new AppDbContext(options))
            {
                added = await importer.ImportAsync(db, [(recent, "h1"), (old, "h2")], none, none);
                await db.SaveChangesAsync();
            }
            c.Check($"{engine} kills: the importer returns the kills it stored", added.Count == 2);
            await using (var db = new AppDbContext(options))
            {
                added = await importer.ImportAsync(db, [(recent, "h1"), (old, "h2"), (npcOnly, "h3")], none, none);
                await db.SaveChangesAsync();
            }
            c.Check($"{engine} kills: and only those, on a second import", added.Count == 1 && added[0].KillMailId == 8004);

            // History feeds its last week only; the live feeds feed all.
            await using (var db = new AppDbContext(options))
                await feed.KillsStoredAsync(db, [recent, old], live: false);
            async Task<IntelLastSeen?> Get(long id)
            {
                await using var db = new AppDbContext(options);
                return await db.IntelLastSeens.AsNoTracking().FirstOrDefaultAsync(r => r.CharacterId == id);
            }
            var victim = await Get(92_000_011);
            c.Check($"{engine} kills: a recent kill from history feeds last seen, victim and attacker",
                victim is { Source: IntelLastSeenSource.KillVictim, SystemId: S2, KillMailId: 8002, AffiliationSource: IntelAffiliationSource.Kill }
                && await Get(92_000_012) is { Source: IntelLastSeenSource.KillAttacker, CorporationId: 98_000_600, AllianceId: 99_000_700 });
            c.Check($"{engine} kills: a kill from history older than a week does not", await Get(92_000_013) is null);
            await using (var db = new AppDbContext(options))
                await feed.KillsStoredAsync(db, [old], live: true);
            c.Check($"{engine} kills: a live one does, whatever its age", await Get(92_000_013) is { SystemId: S3 });

            if (sqlite)
            {
                await Task.Delay(IntelLastSeenFeed.NotifyDelay + TimeSpan.FromMilliseconds(700));
                heard.Clear();
                await using (var db = new AppDbContext(options))
                    await feed.KillsStoredAsync(db, [npcOnly], live: true);
                await Task.Delay(IntelLastSeenFeed.NotifyDelay + TimeSpan.FromMilliseconds(700));
                var quiet = heard.Count(p => IntelSignals.KindOf(p) == IntelSignals.Changed);
                await using (var db = new AppDbContext(options))
                    await feed.KillsStoredAsync(db, [mail], live: true);
                await using (var db = new AppDbContext(options))
                    await feed.KillsStoredAsync(db, [Mail(8005, at, S2, null, 92_000_020)], live: true);
                await Task.Delay(IntelLastSeenFeed.NotifyDelay + TimeSpan.FromMilliseconds(700));
                var told = heard.Count(p => IntelSignals.KindOf(p) == IntelSignals.Changed);
                c.Check($"{engine} kills: a kill with a character among the attackers says intel changed, once for a burst",
                    quiet == 0 && told == 1, $"{quiet} for the NPC kill, {told} after");
            }

            // ── The seed ─────────────────────────────────────────────────────────────────────────
            await using (var db = new AppDbContext(options))
            {
                var r1 = new IntelReport { ReportedAt = T(9, 0), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = S1,
                                           SystemName = "QZ-X77", PlayerCount = 2, Message = "seed one", Source = IntelReportSource.Chat };
                var r2 = new IntelReport { ReportedAt = T(9, 30), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = S2,
                                           SystemName = "XQ1-Z2", PlayerCount = 1, Message = "seed two", Source = IntelReportSource.Chat };
                var r3 = new IntelReport { ReportedAt = T(9, 30), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = S3,
                                           SystemName = "ZZQ-71", PlayerCount = 1, Message = "seed paste", Source = IntelReportSource.LocalPaste };
                var r4 = new IntelReport { ReportedAt = T(9, 30), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = S1,
                                           SystemName = "QZ-X77", PlayerCount = 1, Message = "seed three", Source = IntelReportSource.Chat, ChatMessageId = 0 };
                db.IntelReports.AddRange(r1, r2, r3, r4);
                await db.SaveChangesAsync();
                db.IntelReportCharacters.AddRange(
                    new IntelReportCharacter { IntelReportId = r1.Id, CharacterId = 93_000_001, CharacterName = "Seed Pilot One" },
                    new IntelReportCharacter { IntelReportId = r1.Id, CharacterId = 93_000_002, CharacterName = "Seed Pilot Two" },
                    new IntelReportCharacter { IntelReportId = r2.Id, CharacterId = 93_000_001, CharacterName = "Seed Pilot One", ShipTypeId = 11, ShipName = "Invented Hull" },
                    new IntelReportCharacter { IntelReportId = r3.Id, CharacterId = 93_000_002, CharacterName = "Seed Pilot Two" },
                    new IntelReportCharacter { IntelReportId = r4.Id, CharacterId = 93_000_002, CharacterName = "Seed Pilot Two" });
                db.CharacterAffiliations.Add(new CharacterAffiliation { CharacterId = 93_000_001, CorporationId = 98_000_010, AllianceId = 0, PulledAt = Now1 });
                db.KillMailDetails.AddRange(
                    new KillMailDetail { KillMailId = 8101, KillMailHash = "s1", KillMailTime = at.AddDays(-3), SolarSystemId = S2, VictimCharId = 93_000_004, VictimCorpId = 98_000_011, VictimShipTypeId = 670 },
                    new KillMailDetail { KillMailId = 8102, KillMailHash = "s2", KillMailTime = at.AddDays(-10), SolarSystemId = S3, VictimCharId = 0, VictimCorpId = 1_000_125, VictimShipTypeId = 670 });
                db.KillMailAttackers.AddRange(
                    new KillMailAttacker { KillMailId = 8101, CharacterId = 93_000_003, CorporationId = 98_000_012, ShipTypeId = 17_738 },
                    new KillMailAttacker { KillMailId = 8101, CharacterId = null, FactionId = 500_001 },
                    new KillMailAttacker { KillMailId = 8102, CharacterId = 93_000_005, CorporationId = 98_000_013 });
                await db.SaveChangesAsync();
                c.Check($"{engine} seed: (setup) the second report has a later id than the first", r2.Id > r1.Id);
            }

            var ran = await feed.SeedOnceAsync();
            var one = await Get(93_000_001);
            var two = await Get(93_000_002);
            c.Check($"{engine} seed: it runs once", ran && await Prefs.Over(options).ReadFreshAsync(IntelLastSeenFeed.KeySeeded) == "1");
            c.Check($"{engine} seed: a pilot's newest report, with the affiliation pulled for them",
                one is { SystemId: S2, SeenAt: "2026-11-12T09:30:00Z", ShipTypeId: 11, Name: "Seed Pilot One", CorporationId: 98_000_010,
                         AllianceId: null, AffiliationSource: IntelAffiliationSource.Pulled, Source: IntelLastSeenSource.Report });
            c.Check($"{engine} seed: in the same second a pasted Local beats a chat report",
                two is { SystemId: S3, Source: IntelLastSeenSource.LocalPaste });
            c.Check($"{engine} seed: the last week's kills, attacker and victim",
                await Get(93_000_003) is { SystemId: S2, KillMailId: 8101, CorporationId: 98_000_012 }
                && await Get(93_000_004) is { Source: IntelLastSeenSource.KillVictim });
            c.Check($"{engine} seed: not older kills", await Get(93_000_005) is null);

            await using (var db = new AppDbContext(options))
                await db.IntelLastSeens.Where(x => x.CharacterId == 93_000_002).ExecuteDeleteAsync();
            c.Check($"{engine} seed: never twice", !await feed.SeedOnceAsync() && await Get(93_000_002) is null);

            if (sqlite) signals.Stop();
        }

        /// <summary>A parser pass writes where the pilots it names were seen, with the report's id.</summary>
        public static async Task PassAsync(Checks c, string profile)
        {
            const long scout = 90_000_001, hostile = 90_000_002;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "lastseen-pass.db")}").Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = S1, ConstellationId = 20_000_001, RegionId = 10_000_001, Name = "QZ-X77", Security = -0.4 });
                db.UniverseNames.AddRange(
                    new UniverseName { EntityId = scout, Name = "Scout Alpha", Category = "character" },
                    new UniverseName { EntityId = hostile, Name = "Zeltrix Quorvane", Category = "character" });
                // On a kill here, so the parser takes the name for a character without asking anyone.
                db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = 1, CharacterId = hostile });
                db.CharacterAffiliations.AddRange(
                    new CharacterAffiliation { CharacterId = scout, CorporationId = 98_000_001, PulledAt = DateTimeOffset.UtcNow },
                    new CharacterAffiliation { CharacterId = hostile, CorporationId = 98_000_002, AllianceId = 99_000_002, PulledAt = DateTimeOffset.UtcNow });
                await db.SaveChangesAsync();
            }

            var prefs = Prefs.Over(options);
            await prefs.SetAsync(MonitoringSettings.KeyChatIntelChannels, "intelcheck");
            await prefs.LoadAsync();
            var logger = new AppErrorLogger(null!);
            var signals = new ClientSignals(logger);
            signals.Start();
            var lease = new WorkerLease(logger);
            await lease.AcquireAsync();
            var worker = new IntelService(new Factory(options), null!, new MonitoringSettings(prefs), logger, signals, lease);

            var at = DateTimeOffset.UtcNow.AddMinutes(-2);
            await using (var db = new AppDbContext(options))
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    OccurredAt = ChatLogRules.FormatTimestamp(at), ChannelName = "intelcheck", SenderName = "Scout Alpha",
                    Message = "QZ-X77 Zeltrix Quorvane", SourceFile = "pass.txt", LineNumber = 1,
                });
                await ChatMessageWrites.SaveAsync(db);
            }
            var wrote = await worker.ProcessNewAsync(waitForTurn: true);

            await using (var db = new AppDbContext(options))
            {
                var report = await db.IntelReports.AsNoTracking().FirstOrDefaultAsync();
                var row = await db.IntelLastSeens.AsNoTracking().FirstOrDefaultAsync(x => x.CharacterId == hostile);
                c.Check("pass: a parser pass writes where the named pilot was seen",
                    wrote == 1 && report is not null && row is not null && row.SystemId == S1 && row.ReportId == report.Id
                    && row.SeenAt == report.ReportedAt && row.Source == IntelLastSeenSource.Report && row.Name == "Zeltrix Quorvane",
                    $"{wrote} written; row {(row is null ? "none" : $"{row.SystemId} {row.ReportId} {row.SeenAt}")}");
                c.Check("pass: with the affiliation pulled for them",
                    row is { CorporationId: 98_000_002, AllianceId: 99_000_002, AffiliationSource: IntelAffiliationSource.Pulled });
                c.Check("pass: and not the reporter", !await db.IntelLastSeens.AnyAsync(x => x.CharacterId == scout));
            }
            signals.Stop();
        }
    }
}
