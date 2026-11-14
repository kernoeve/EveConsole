// The Intel tool's Pilots, Camps and Watch list: the watch list's matching (the most particular entry decides) and the
// lens's marks (Always bold, Ignored dimmed, never fresh, never the nearest threat); the Camps tab's rows (active, then
// ended and faded for an hour after the last kill, then gone; camps only reported; judged by the attackers); the watch
// list's table on a database (one entry per thing); and the Pilots search on a large last-seen table: prefix first,
// "contains" and corporation names from three characters, case ignored on both engines, the window, the cap, and under
// 200 ms. Every name and id here is invented.
using System.Diagnostics;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck
{
    internal static partial class ReadSideChecks
    {
        private static IntelWatchEntry Entry(string kind, long id, int mode) =>
            new() { Id = (int)(id % 100_000) + kind.Length, Kind = kind, EntityId = id, Name = $"Entry {id % 1000}", Mode = mode };

        // ── The watch list's matching ───────────────────────────────────────────────────────────────────────

        public static void WatchSetChecks(Checks c)
        {
            const int Interdictor = 541_001, OtherHull = 541_002, DictorGroup2 = 541;
            var set = new IntelWatchSet(
            [
                Entry(IntelWatchKind.Pilot, P1, IntelWatchMode.Always),
                Entry(IntelWatchKind.Corporation, FarCorp, IntelWatchMode.Ignore),
                Entry(IntelWatchKind.Alliance, FoeAlly, IntelWatchMode.Always),
                Entry(IntelWatchKind.Hull, Interdictor, IntelWatchMode.Always),
                Entry(IntelWatchKind.Group, DictorGroup2, IntelWatchMode.Ignore),
            ]);
            c.Check("watch: a pilot's own entry beats their corporation's (Always over Ignore)",
                set.Pilot(P1, FarCorp, 0) == IntelWatchMark.Always);
            c.Check("watch: the corporation's beats the alliance's (Ignore over Always)",
                set.Pilot(P2, FarCorp, FoeAlly) == IntelWatchMark.Ignored);
            c.Check("watch: the alliance's when nothing nearer says", set.Pilot(P3, CorpB, FoeAlly) == IntelWatchMark.Always);
            c.Check("watch: the hull flown, then its group", set.Pilot(P4, CorpB, 0, Interdictor, DictorGroup2) == IntelWatchMark.Always
                && set.Pilot(P4, CorpB, 0, OtherHull, DictorGroup2) == IntelWatchMark.Ignored);
            c.Check("watch: nothing on the list is no mark", set.Pilot(P5, CorpB, 0, OtherHull, 999) == IntelWatchMark.None
                && IntelWatchSet.Empty.Pilot(P1, FarCorp, FoeAlly) == IntelWatchMark.None);
            c.Check("watch: a hull said with nobody named, by type then group",
                set.Hull(Interdictor) == IntelWatchMark.Always && set.Hull(OtherHull, DictorGroup2) == IntelWatchMark.Ignored);
            c.Check("watch: an unknown kind and a zero id are not entries",
                new IntelWatchSet([Entry("ship", 5, 0), Entry(IntelWatchKind.Pilot, 0, 0)]).IsEmpty);

            c.Check("watch: a row is Always when anyone on it is",
                IntelWatchSet.Combine([IntelWatchMark.Ignored, IntelWatchMark.Always], unmarked: true) == IntelWatchMark.Always);
            c.Check("watch: Ignored only when everyone on it is ignored",
                IntelWatchSet.Combine([IntelWatchMark.Ignored, IntelWatchMark.Ignored], false) == IntelWatchMark.Ignored
                && IntelWatchSet.Combine([IntelWatchMark.Ignored, IntelWatchMark.None], false) == IntelWatchMark.None);
            c.Check("watch: a count nobody was named for, or a flag, keeps the row undimmed",
                IntelWatchSet.Combine([IntelWatchMark.Ignored], unmarked: true) == IntelWatchMark.None);
            c.Check("watch: a row with nobody on it is not dimmed", IntelWatchSet.Combine([], false) == IntelWatchMark.None);
        }

        // ── The lens's marks ────────────────────────────────────────────────────────────────────────────────

        public static void WatchLensChecks(Checks c)
        {
            var r5 = Report(S5, Ago(1), count: 1);        // P1 alone, 1 jump from B
            var r3 = Report(S3, Ago(1), count: 1);        // P7 alone
            var r4 = Report(S4, Ago(1), count: 3);        // P2 and two unnamed
            var reports = new List<IntelReport> { r5, r3, r4 };
            var named = new List<IntelReportCharacter> { Named(r5, P1), Named(r3, P7), Named(r4, P2) };
            var seen = new List<IntelLastSeen>
            {
                Seen(P1, S5, Ago(1), reportId: r5.Id, corp: FarCorp),
                Seen(P7, S3, Ago(1), reportId: r3.Id, corp: FarCorp),
                Seen(P2, S4, Ago(1), reportId: r4.Id, corp: FarCorp),
            };
            var plain = Build(reports, named, seen);
            var watch = new IntelWatchSet(
            [
                Entry(IntelWatchKind.Pilot, P1, IntelWatchMode.Ignore),
                Entry(IntelWatchKind.Pilot, P7, IntelWatchMode.Always),
                Entry(IntelWatchKind.Pilot, P2, IntelWatchMode.Ignore),
            ]);
            var snap = plain with { Watch = watch };

            var view = IntelLens.Project(snap, Everyone, Now, Hops());
            var s5 = view.Systems.First(r => r.SystemId == S5);
            var s3 = view.Systems.First(r => r.SystemId == S3);
            var s4 = view.Systems.First(r => r.SystemId == S4);
            c.Check("watch lens: a system whose only pilot is ignored is marked Ignored, and kept (dimmed, not hidden)",
                s5.Watch == IntelWatchMark.Ignored && s5.Pilots.Single().Watch == IntelWatchMark.Ignored, s5.Watch.ToString());
            c.Check("watch lens: an ignored row is never fresh", !s5.IsFresh && s3.IsFresh);
            c.Check("watch lens: nor the nearest threat (S5 is nearest, S3 is the line)", view.Nearest?.SystemId == S3,
                view.Nearest?.SystemId.ToString() ?? "none");
            c.Check("watch lens: an Always pilot marks the row Always", s3.Watch == IntelWatchMark.Always);
            c.Check("watch lens: an ignored pilot beside an unnamed count leaves the row unmarked",
                s4.Watch == IntelWatchMark.None && s4.Pilots.Single().Watch == IntelWatchMark.Ignored);
            var feed5 = view.Feed.First(r => r.Event.ReportId == r5.Id);
            c.Check("watch lens: the feed line is marked the same, and not fresh", feed5.Watch == IntelWatchMark.Ignored && !feed5.IsFresh
                && feed5.Pilots.Single().Watch == IntelWatchMark.Ignored);
            c.Check("watch lens: an Always line", view.Feed.First(r => r.Event.ReportId == r3.Id).Watch == IntelWatchMark.Always);

            var before = IntelLens.Project(plain, Everyone, Now, Hops());
            c.Check("watch lens: (mutation) with no list, nothing is marked and S5 is the nearest",
                before.Systems.All(r => r.Watch == IntelWatchMark.None) && before.Nearest?.SystemId == S5);

            // A hull said with nobody named: "3× Interdictor" on the list as Always.
            const int SaidHull = 541_001;
            var said = Report(S6, Ago(1), count: 3, ships: "3× Zqx Dictor");
            var hullSnap = Build([said], [], []) with
            {
                Watch = new IntelWatchSet([Entry(IntelWatchKind.Hull, SaidHull, IntelWatchMode.Always)]),
                HullIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Zqx Dictor"] = SaidHull },
            };
            var s6 = IntelLens.Project(hullSnap, Everyone, Now, Hops()).Systems.FirstOrDefault(r => r.SystemId == S6);
            c.Check("watch lens: a hull said in a count, on the list as Always, marks the row", s6?.Watch == IntelWatchMark.Always,
                s6?.Watch.ToString() ?? "no row");

            // A camp of ignored attackers is dimmed; a camp row is never the panel's nearest threat anyway.
            List<IntelKill> GateKills() =>
            [
                Kill(61, Ago(5), P10, 10_000, [Attacker(P5), Attacker(P6)]),
                Kill(62, Ago(3), P11, 10_000, [Attacker(P6)]),
            ];
            var campSnap = Build([], [], [], GateKills()) with
            {
                Watch = new IntelWatchSet([Entry(IntelWatchKind.Corporation, FarCorp, IntelWatchMode.Ignore)]),
            };
            var campRow = IntelLens.Project(campSnap, Everyone, Now, Hops()).Systems.FirstOrDefault(r => r.SystemId == S1);
            c.Check("watch lens: a camp whose attackers are all ignored is marked Ignored", campRow?.Watch == IntelWatchMark.Ignored,
                campRow?.Watch.ToString() ?? "no row");
        }

        // ── The pilot page's "Last seen" ────────────────────────────────────────────────────────────────────

        public static void LastSeenLineChecks(Checks c)
        {
            var ui = System.Globalization.CultureInfo.CurrentUICulture;
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.CurrentCulture =
                    new System.Globalization.CultureInfo("en-US");
                var report = Seen(P1, S2, Ago(185));
                report.ShipTypeId = 0; report.ShipName = "Sabre";
                var line = EveConsole.ViewModels.EntityTabViewModel.LastSeenLine(report, "QZ-X77", Now);
                c.Check("pilot page: last seen from a report, with the hull said", line == "Last seen in QZ-X77, 3 h ago · Sabre · Reported", line);
                var kill = Seen(P1, S2, Now.AddDays(-3).AddMinutes(-5), IntelLastSeenSource.KillVictim);
                line = EveConsole.ViewModels.EntityTabViewModel.LastSeenLine(kill, "XQ1-Z2", Now);
                c.Check("pilot page: days for an old sighting, and a loss said as one", line == "Last seen in XQ1-Z2, 3 d ago · Lost a ship", line);
                c.Check("ages: minutes within the hour", EveConsole.ViewModels.IntelWords.LongAge(Ago(42), Now) == "42 min");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentUICulture = ui;
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }
        }

        // ── The Camps tab ───────────────────────────────────────────────────────────────────────────────────

        public static void CampsTabChecks(Checks c)
        {
            List<IntelKill> GateKills(long corp) =>
            [
                Kill(71, Ago(5), P10, 10_000, [Attacker(P5, corp: corp), Attacker(P6, corp: corp)]),
                Kill(72, Ago(3), P11, 10_000, [Attacker(P6, corp: corp)]),
            ];
            var said = Report(S3, Ago(20), flags: (int)IntelRules.IntelFlags.Camp);
            var snap = Build([said], [], [], GateKills(FarCorp));
            c.Check("camps tab: the snapshot has the camp", snap.Camps.Count == 1);

            var now = IntelLens.ProjectCamps(snap, Everyone, Now, Hops());
            var kills = now.FirstOrDefault(r => r.Camp is not null);
            var reported = now.FirstOrDefault(r => r.Camp is null);
            c.Check("camps tab: a camp from kills, active, measured and coloured by its attackers",
                kills is { SystemId: S1, Active: true, Jumps: 0, Tier: StandingTier.Red, Reported: false },
                kills is null ? "none" : $"{kills.SystemId % 100} {kills.Active} {kills.Jumps} {kills.Tier} {kills.Reported}");
            c.Check("camps tab: a camp only reported is a row of its own",
                reported is { SystemId: S3, Active: true, Reported: true, Fact: not null }, reported?.SystemId.ToString() ?? "none");
            c.Check("camps tab: active first, nearest first", now.Count == 2 && now[0].SystemId == S1);

            var ended = IntelLens.ProjectCamps(snap, Everyone, Now.AddMinutes(40), Hops());
            var endedKills = ended.FirstOrDefault(r => r.Camp is not null);
            c.Check("camps tab: 40 minutes on, the camp has ended and stays, faded",
                endedKills is { Active: false } && endedKills.Opacity == IntelLens.EndedCampOpacity,
                endedKills is null ? "gone" : $"{endedKills.Active} {endedKills.Opacity}");
            c.Check("camps tab: the reported one 60 minutes after its report is gone (20 + 40)",
                ended.All(r => r.Camp is not null), string.Join(",", ended.Select(r => r.SystemId % 100)));
            c.Check("camps tab: an hour after the last kill the camp is gone too",
                IntelLens.ProjectCamps(snap, Everyone, Now.AddMinutes(58), Hops()).Count == 0);

            SlidingCampChecks(c);

            var blue = Build([], [], [], GateKills(CorpA));
            c.Check("camps tab: a camp of blues gives no row", IntelLens.ProjectCamps(blue, Everyone, Now, Hops()).Count == 0);
            c.Check("camps tab: shown when the tiers ask for blues",
                IntelLens.ProjectCamps(blue, Everyone with { Tiers = Enum.GetValues<StandingTier>().ToHashSet() }, Now, Hops()).Count == 1);
            c.Check("camps tab: the distance filter applies (S3 is 2 jumps, within 1 it goes)",
                IntelLens.ProjectCamps(snap, Everyone with { MaxJumps = 1 }, Now, Hops()).All(r => r.SystemId != S3));
            var missing = new List<IntelOrigin>();
            IntelLens.ProjectCamps(snap, Everyone, Now, Hops(bReady: false), missing);
            c.Check("camps tab: an origin not walked yet is reported", missing.SequenceEqual([new IntelOrigin(B, S9)]));
        }

        /// <summary>
        /// The Camps tab as the service runs it: the snapshot rebuilt as time passes with one register, so
        /// the window slides past the camp's first kill. Kills nearly a camp window apart leave the window
        /// soonest after the camp ends; the camp must stay, ended, until an hour after its last kill. With a
        /// long camp window and lifetime, a camp still active must not drop out either.
        /// </summary>
        private static void SlidingCampChecks(Checks c)
        {
            List<IntelKill> Kills(DateTimeOffset first, double gapMinutes) =>
            [
                Kill(81, first, P10, 10_000, [Attacker(P5), Attacker(P6)]),
                Kill(82, first.AddMinutes(gapMinutes), P11, 10_000, [Attacker(P6)]),
            ];
            IntelSnapshot Rebuild(IntelCampRegister register, List<IntelKill> kills, DateTimeOffset now, IntelSettingsValues settings) =>
                IntelSnapshotBuilder.Build(new IntelSnapshotInput
                {
                    Now = now, Settings = settings, Reports = [], ReportPilots = [], LastSeen = [],
                    Kills = [.. kills.Where(k => k.At <= now)],
                    Gates = Gates, Groups = Groups(), Standings = Standings(),
                    Own = [new OwnPosition(A, "Pilot Alpha", S1, now, OwnPositionSource.Esi, CorpA, AllyA)],
                }, register, 1);

            // Defaults: a 60-minute window, a 30-minute camp window and lifetime. The first kill leaves the
            // window 32 minutes after the last.
            var defaults = new IntelSettingsValues();
            var last = Now;
            var kills = Kills(last.AddMinutes(-28), 28);
            var register = new IntelCampRegister();
            var seen = new List<string>();
            foreach (var after in new[] { 1, 20, 35, 45, 55, 59, 60, 65 })
            {
                var at = last.AddMinutes(after);
                var snap = Rebuild(register, kills, at, defaults);
                var row = IntelLens.ProjectCamps(snap, Everyone, at, Hops()).FirstOrDefault(r => r.Camp is not null);
                seen.Add($"+{after}: {(row is null ? "gone" : row.Active ? "active" : "ended")}");
            }
            c.Check("camps tab: rebuilt as time passes, the camp is active, then ended and kept until an hour after its last kill",
                seen.SequenceEqual(["+1: active", "+20: active", "+35: ended", "+45: ended", "+55: ended", "+59: ended", "+60: gone", "+65: gone"]),
                string.Join(", ", seen));
            c.Check("camps tab: once let go, the register holds nothing", register.Count == 0, register.Count.ToString());

            // A long camp window and lifetime: kills 58 minutes apart, the first leaving the 65-minute window
            // while the camp is still active.
            var longer = new IntelSettingsValues { CampWindow = TimeSpan.FromMinutes(60), Camp = TimeSpan.FromMinutes(60) };
            var longKills = Kills(last.AddMinutes(-58), 58);
            var longRegister = new IntelCampRegister();
            seen.Clear();
            foreach (var after in new[] { 1, 10, 30, 59, 61 })
            {
                var at = last.AddMinutes(after);
                var snap = Rebuild(longRegister, longKills, at, longer);
                var row = IntelLens.ProjectCamps(snap, Everyone, at, Hops()).FirstOrDefault(r => r.Camp is not null);
                seen.Add($"+{after}: {(row is null ? "gone" : row.Active ? "active" : "ended")}");
            }
            c.Check("camps tab: a camp still active is kept when its first kill leaves the window",
                seen.SequenceEqual(["+1: active", "+10: active", "+30: active", "+59: active", "+61: gone"]), string.Join(", ", seen));
        }

        // ── The watch list on a database ────────────────────────────────────────────────────────────────────

        public static async Task WatchTableAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options)) await db.IntelWatchEntries.ExecuteDeleteAsync();
            var list = new IntelWatchList(new Factory(options));
            var changed = 0;
            list.Changed += () => changed++;

            await list.SetAsync(IntelWatchKind.Pilot, P1, "Pilot 001", IntelWatchMode.Always, "a note");
            await list.SetAsync(IntelWatchKind.Pilot, P1, "Pilot 001", IntelWatchMode.Ignore);
            var rows = await list.ReadAsync();
            c.Check($"{engine} watch: choosing again changes the mode, never adds a second entry",
                rows is [{ Mode: IntelWatchMode.Ignore, Note: "a note" }], string.Join(";", rows.Select(r => $"{r.Kind}:{r.EntityId}:{r.Mode}")));
            c.Check($"{engine} watch: the time is ISO text and the PC is named",
                rows.Count == 1 && System.Text.RegularExpressions.Regex.IsMatch(rows[0].AddedAt, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")
                && rows[0].AddedBy.Length > 0, rows.FirstOrDefault()?.AddedAt ?? "");

            await list.SetAsync(IntelWatchKind.Corporation, P1, "Same id, other kind", IntelWatchMode.Always);
            c.Check($"{engine} watch: the same id as another kind is another entry", (await list.ReadAsync()).Count == 2);

            var unique = false;
            try
            {
                await using var db = new AppDbContext(options);
                db.IntelWatchEntries.Add(new IntelWatchEntry { Kind = IntelWatchKind.Pilot, EntityId = P1, Name = "twice", AddedAt = "x" });
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException) { unique = true; }
            c.Check($"{engine} watch: the table refuses a second entry for one thing", unique);

            var id = (await list.ReadAsync()).First(r => r.Kind == IntelWatchKind.Pilot).Id;
            await list.UpdateAsync(id, IntelWatchMode.Always, "  ");
            c.Check($"{engine} watch: an update sets the mode and clears a blank note",
                (await list.ReadAsync()).First(r => r.Id == id) is { Mode: IntelWatchMode.Always, Note: null });
            await list.RemoveAsync(IntelWatchKind.Pilot, P1);
            c.Check($"{engine} watch: removed", (await list.ReadAsync()).All(r => r.Kind != IntelWatchKind.Pilot));
            c.Check($"{engine} watch: every change is told", changed == 5, changed.ToString());
            await list.SetAsync(IntelWatchKind.Pilot, 0, "nobody", IntelWatchMode.Always);
            c.Check($"{engine} watch: an id of 0 is never stored", (await list.ReadAsync()).All(r => r.EntityId != 0));
        }

        // ── The Pilots search ───────────────────────────────────────────────────────────────────────────────

        private const int SearchRows = 150_000;
        private const long ZqxCorp = 98_777_001;

        public static async Task PilotSearchAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options))
            {
                await db.IntelLastSeens.ExecuteDeleteAsync();
                await db.UniverseNames.Where(u => u.EntityId == ZqxCorp).ExecuteDeleteAsync();
            }

            // A large table: pilots seen over sixty days, a few with names the searches look for.
            var seed = Stopwatch.StartNew();
            for (var start = 0; start < SearchRows; start += 10_000)
            {
                await using var db = new AppDbContext(options);
                db.ChangeTracker.AutoDetectChangesEnabled = false;
                for (var i = start; i < Math.Min(start + 10_000, SearchRows); i++)
                {
                    var at = Now.AddMinutes(-(i % (60 * 24 * 60)));
                    db.IntelLastSeens.Add(new IntelLastSeen
                    {
                        CharacterId = 95_000_000 + i, Name = $"Pilot {i:D6} Varn", SeenAt = Iso(at), WrittenAt = Iso(at),
                        SystemId = S1 + i % 9, CorporationId = 98_500_000 + i % 400,
                    });
                }
                await db.SaveChangesAsync();
            }
            await using (var db = new AppDbContext(options))
            {
                db.IntelLastSeens.AddRange(
                    Row(96_000_001, "Zqxen Arlo", Ago(30)),
                    Row(96_000_002, "zqxa lower", Ago(10)),
                    Row(96_000_003, "Old Zqxar", Ago(5)),            // contains, not a prefix: after the prefix ones
                    Row(96_000_004, "Zqx Too Old", Now.AddDays(-2)),  // outside 24 h
                    Row(96_000_005, "Kel Mar", Ago(2), corp: ZqxCorp));
                db.UniverseNames.Add(new UniverseName { EntityId = ZqxCorp, Name = "Zqx Holdings", Category = "corporation", PulledAt = Now });
                await db.SaveChangesAsync();
            }
            Console.WriteLine($"{engine}: {SearchRows:N0} last-seen rows seeded in {seed.ElapsedMilliseconds} ms");

            async Task<(IntelPilotResults R, long Ms)> Search(string text, TimeSpan window)
            {
                await using var db = new AppDbContext(options);
                var sw = Stopwatch.StartNew();
                var r = await IntelPilotSearch.SearchAsync(db, text, Now - window);
                return (r, sw.ElapsedMilliseconds);
            }
            var day = TimeSpan.FromHours(24);
            await Search("warm", day);   // the first query pays for opening the connection

            var (zq, _) = await Search("zq", day);
            c.Check($"{engine} pilots: two letters search the prefix only, case ignored (no \"contains\", no corporation)",
                zq.Rows.Select(r => r.CharacterId).SequenceEqual([96_000_002L, 96_000_001L]),
                string.Join(",", zq.Rows.Select(r => r.Name)));
            var (zqx, _) = await Search("ZQX", day);
            var names = zqx.Rows.Select(r => r.Name).ToList();
            c.Check($"{engine} pilots: from three letters, prefix first (newest first), then contains and the corporation's pilots",
                names.Take(2).SequenceEqual(["zqxa lower", "Zqxen Arlo"]) && names.Skip(2).SequenceEqual(["Kel Mar", "Old Zqxar"]),
                string.Join(",", names));
            c.Check($"{engine} pilots: the window leaves out a pilot seen two days ago", names.All(n => n != "Zqx Too Old"));
            var (week, _) = await Search("zqx", TimeSpan.FromDays(7));
            c.Check($"{engine} pilots: and a week's window has them", week.Rows.Any(r => r.Name == "Zqx Too Old"));
            c.Check($"{engine} pilots: the corporation's name comes back for the row", zqx.Names.GetValueOrDefault(ZqxCorp) == "Zqx Holdings");

            var (all, _) = await Search("", TimeSpan.FromDays(60));
            c.Check($"{engine} pilots: at most {IntelPilotSearch.MaxRows}, newest first",
                all.Rows.Count == IntelPilotSearch.MaxRows
                && all.Rows.Zip(all.Rows.Skip(1)).All(p => string.CompareOrdinal(p.First.SeenAt, p.Second.SeenAt) >= 0));

            // Fast enough to type into: every kind of search, on the whole table. The best of three, as a box typed
            // into searches again and again: one slow run is the machine, three are the query.
            //
            // ⚠️ Measured against one pass over the same table on the same machine, not a fixed number of
            // milliseconds. A search that finds nothing, or finds nearly everyone, has to read the whole window,
            // and how long that takes is the machine's: a CI runner took about four times as long as a desktop
            // for every search alike. What this guards is the query's shape: no search may cost more than a few
            // passes (a sort of everything, a query per row, a lost cap all would).
            // One pass is the search's own unit: the sixty-day window walked for a name nobody has, newest first.
            async Task<long> OnePass()
            {
                await using var db = new AppDbContext(options);
                var since = IntelLastSeenWriter.Format(Now - TimeSpan.FromDays(60));
                var sw = Stopwatch.StartNew();
                _ = await db.IntelLastSeens.AsNoTracking()
                    .Where(r => string.Compare(r.SeenAt, since) >= 0 && r.Name != null && r.Name.ToLower().Contains("no such pilot"))
                    .OrderByDescending(r => r.SeenAt).Take(IntelPilotSearch.MaxRows).ToListAsync();
                return sw.ElapsedMilliseconds;
            }
            var pass = long.MaxValue;
            for (var run = 0; run < 3; run++) pass = Math.Min(pass, await OnePass());
            var limit = 4 * pass + 25;

            var timings = new List<(string, long)>();
            foreach (var (text, window) in new[] { ("", day), ("pi", day), ("Pilot 0123", day), ("zqx", day), ("zqx", TimeSpan.FromDays(60)),
                                                  ("varn", TimeSpan.FromDays(60)), ("nobody here", TimeSpan.FromDays(60)) })
            {
                var best = long.MaxValue;
                for (var run = 0; run < 3; run++) best = Math.Min(best, (await Search(text, window)).Ms);
                timings.Add(($"'{text}' {window.TotalDays:0}d", best));
            }
            var said = $"one pass {pass} ms, limit {limit} ms; " + string.Join(", ", timings.Select(t => $"{t.Item1} {t.Item2} ms"));
            c.Check($"{engine} pilots: every search on {SearchRows:N0} rows within four passes over the table", timings.All(t => t.Item2 <= limit), said);
            Console.WriteLine($"{engine} pilots search: " + said);

            // ── With the standings filter: the cap counts the pilots kept ──
            // The newest the filter leaves out never push out older ones it keeps: the read pages on past them.
            async Task<(IntelPilotResults R, long Ms)> Filtered(TimeSpan window, Func<IntelLastSeen, bool> keep)
            {
                await using var db = new AppDbContext(options);
                var sw = Stopwatch.StartNew();
                var r = await IntelPilotSearch.SearchAsync(db, "", Now - window, keep);
                return (r, sw.ElapsedMilliseconds);
            }
            async Task<List<IntelLastSeen>> Window(TimeSpan window)
            {
                await using var db = new AppDbContext(options);
                var since = IntelLastSeenWriter.Format(Now - window);
                return [.. (await db.IntelLastSeens.AsNoTracking().Where(r => string.Compare(r.SeenAt, since) >= 0).ToListAsync())
                    .OrderByDescending(r => r.SeenAt, StringComparer.Ordinal)];
            }
            static bool OneIn50(IntelLastSeen r) => r.CharacterId % 50 == 0;
            foreach (var (label, window) in new[] { ("24 h", day), ("60 d", TimeSpan.FromDays(60)) })
            {
                var inWindow = await Window(window);
                var expected = inWindow.Where(OneIn50).Take(IntelPilotSearch.MaxRows).ToList();
                var kept = inWindow.Count(OneIn50);
                var (got, ms) = await Filtered(window, OneIn50);
                c.Check($"{engine} pilots filtered, {label}: the newest pilots the filter keeps, the cap counted after it",
                    got.Rows.All(OneIn50) && got.Rows.Select(r => r.SeenAt).SequenceEqual(expected.Select(r => r.SeenAt))
                    && got.Rows.Select(r => r.CharacterId).Distinct().Count() == got.Rows.Count,
                    $"{got.Rows.Count} rows, {expected.Count} expected, {ms} ms");
                c.Check($"{engine} pilots filtered, {label}: capped only when more were kept than the cap, and the hidden counted",
                    kept > IntelPilotSearch.MaxRows
                        ? got.Capped && got.Rows.Count == IntelPilotSearch.MaxRows
                        : !got.Capped && got.Hidden == inWindow.Count - kept,
                    $"capped {got.Capped}, hidden {got.Hidden} of {inWindow.Count} read, {kept} kept");
                if (label == "60 d")
                    c.Check($"{engine} pilots filtered: one in fifty kept, sixty days, within four passes over the table", ms <= limit,
                        $"{ms} ms, limit {limit} ms");
            }
            // Ten old pilots behind 1,200 newer ones the filter leaves out: all ten, nothing capped.
            static bool TenOld(IntelLastSeen r) => r.CharacterId is >= 95_000_600 and < 95_000_610;
            var (ten, _) = await Filtered(day, TenOld);
            var dayAll = await Window(day);
            c.Check($"{engine} pilots filtered: ten kept behind every newer pilot left out are all listed, and the rest counted hidden",
                ten.Rows.Count == 10 && ten.Rows.All(TenOld) && !ten.Capped && ten.Hidden == dayAll.Count - 10,
                $"{ten.Rows.Count} rows, hidden {ten.Hidden} of {dayAll.Count}, capped {ten.Capped}");
            static bool Nobody(IntelLastSeen r) => false;
            var (none, noneMs) = await Filtered(TimeSpan.FromDays(60), Nobody);
            c.Check($"{engine} pilots filtered: a filter that keeps nobody stops at its scan bound and says so",
                none.Rows.Count == 0 && none.Capped && none.Hidden >= IntelPilotSearch.MaxScanned, $"hidden {none.Hidden}, {noneMs} ms");
            Console.WriteLine($"{engine} pilots filtered: one in fifty over 60 d read in the check above; nobody kept: {noneMs} ms");

            await using (var db = new AppDbContext(options))
            {
                await db.IntelLastSeens.ExecuteDeleteAsync();
                await db.UniverseNames.Where(u => u.EntityId == ZqxCorp).ExecuteDeleteAsync();
            }

            static IntelLastSeen Row(long id, string name, DateTimeOffset at, long corp = 0) => new()
            {
                CharacterId = id, Name = name, SeenAt = Iso(at), WrittenAt = Iso(at), SystemId = S2, CorporationId = corp > 0 ? corp : null,
            };
        }
    }
}
