// The panel's rows: what an intel event says on a panel, its popup and the Intel tool. Each named pilot with
// their portrait and then their hull with its icon, the first few then "+N"; counts said once; a flag-only
// report's words without the system's name it starts with; a camp as new as its last kill; the jump bands, the
// "…" while a walk is not ready, and what lights a row. Hulls said by name only, shown with their icons; distance
// from a system, coloured by the rule with no viewer. Every name and id here is invented.
using System.Globalization;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services.ClientLayouts;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;

namespace EveConsole.Tools.IntelCheck
{
    internal static class PanelChecks
    {
        private const int Home = 31_000_001, Next = 31_000_002, Mid = 31_000_003, Far = 31_000_004, Gone = 31_000_005;
        private const long Me = 93_000_001, MyCorp = 98_300_001, RedCorp = 98_300_002;
        private const int HullA = 70_101, HullB = 70_102, HullC = 70_103;
        private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        private static DateTimeOffset Ago(double minutes) => Now.AddMinutes(-minutes);
        private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public static void Rows(Checks c)
        {
            var culture = CultureInfo.CurrentUICulture;
            var format = CultureInfo.CurrentCulture;
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("en-US");
            try { RowsInEnglish(c); SaidHulls(c); SystemOrigin(c); }
            finally { CultureInfo.CurrentUICulture = culture; CultureInfo.CurrentCulture = format; }
        }

        /// <summary>Hulls said with nobody named ("8× Hull C, 2× Interdictor" as a report or a d-scan's paste
        /// stores them, names only): each hull with its icon, found by name, on a feed line, a Board row and the
        /// panel's single line; a class with no type is words alone, never an empty square.</summary>
        private static void SaidHulls(Checks c)
        {
            var report = new IntelReport
            {
                Id = 900, ReportedAt = Iso(Ago(1)), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = Home,
                SystemName = "QZ-X77", PlayerCount = 10, Ships = "8× Hull C, 2× Interdictor", Kind = IntelReportKind.Sighting,
                Message = "QZ-X77 10 in local 8x hull c 2x dictor",
            };
            var input = new IntelSnapshotInput
            {
                Now = Now, Settings = new IntelSettingsValues(), Reports = [report],
                SystemNames = new Dictionary<int, string> { [Home] = "QZ-X77" },
                Standings = new IntelStandings([new IntelStandings.OwnCharacter(Me, "Pilot A", MyCorp, 0)], [MyCorp], []),
                Own = [new OwnPosition(Me, "Pilot A", Home, Ago(30), OwnPositionSource.ChatLog, MyCorp, 0)],
            };
            var hops = new Dictionary<int, IntelHop> { [Home] = new(0, false) };
            var viewer = new IntelViewer { Origins = [new IntelOrigin(Me, Home)], StandingsCharacterId = Me };

            string Says(IEnumerable<IntelPictureVm> pictures) =>
                string.Join(",", pictures.Select(p => $"{p.Text}:{p.Id}:{(p.HasPictureSlot ? "icon" : "words")}"));

            var known = IntelSnapshotBuilder.Build(input with
            {
                HullIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Hull C"] = HullC },
            }, new IntelCampRegister(), 1);
            var view = IntelLens.Project(known, viewer, Now, _ => hops);
            var line = new IntelEventRowVm(view.Feed.Single(), known, Now, false);
            c.Check("said hulls: a hull named alone shows its icon, found by its name; a class is words, with no square",
                Says(line.Pictures) == $"Hull C ×8:{HullC}:icon,Interdictor ×2:0:words", Says(line.Pictures));
            c.Check("said hulls: the icon is asked for by the type found",
                line.Pictures[0].PictureUrl?.Contains($"/types/{HullC}/icon", StringComparison.Ordinal) == true, line.Pictures[0].PictureUrl ?? "no url");
            var board = new IntelBoardRowVm(view.Systems.Single(), known, [.. view.Feed], null, Now, false, IntelRowActions.None);
            c.Check("said hulls: the Board's row too", Says(board.Hulls) == $"Hull C ×8:{HullC}:icon,Interdictor ×2:0:words", Says(board.Hulls));
            var single = new IntelNearestVm(view.Systems.Single(), known, Now, false);
            c.Check("said hulls: and the panel's single line", Says(single.Pictures) == $"Hull C ×8:{HullC}:icon,Interdictor ×2:0:words",
                Says(single.Pictures));

            var unknown = IntelSnapshotBuilder.Build(input, new IntelCampRegister(), 1);
            var blind = new IntelEventRowVm(IntelLens.Project(unknown, viewer, Now, _ => hops).Feed.Single(), unknown, Now, false);
            c.Check("said hulls: with no hull known by that name, words alone: never an empty square",
                blind.Pictures.All(p => !p.HasPictureSlot && p.PictureUrl is null), Says(blind.Pictures));
        }

        /// <summary>Distance from a system: jumps from there, nobody named as the origin, and the colours by the
        /// rule with no viewer (friendly if any character is), or the forced character when set.</summary>
        private static void SystemOrigin(Checks c)
        {
            const long Other = 93_000_002, OtherCorp = 98_300_009, Ally = 99_300_001, FriendCorp = 98_300_010;
            var report = new IntelReport
            {
                Id = 901, ReportedAt = Iso(Ago(1)), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = Mid,
                SystemName = "ZZQ-71", PlayerCount = 1, Kind = IntelReportKind.Sighting, Message = "ZZQ-71 Pilot Nine",
            };
            var named = new IntelReportCharacter { IntelReportId = 901, CharacterId = 94_000_009, CharacterName = "Pilot Nine" };
            var seen = new IntelLastSeen
            {
                CharacterId = 94_000_009, Name = "Pilot Nine", SystemId = Mid, SeenAt = Iso(Ago(1)), Source = IntelLastSeenSource.Report,
                SourceRank = IntelLastSeenSource.RankOf(IntelLastSeenSource.Report), ReportId = 901, CorporationId = FriendCorp, WrittenAt = Iso(Ago(1)),
            };
            // Pilot Nine's corporation: red to Me's corporation, blue to the other character's.
            IntelStandings Standings(long forced = 0) => new(
                [new IntelStandings.OwnCharacter(Me, "Pilot A", MyCorp, Ally), new IntelStandings.OwnCharacter(Other, "Pilot B", OtherCorp, Ally)],
                [MyCorp, OtherCorp], [(MyCorp, FriendCorp, -10.0), (OtherCorp, FriendCorp, 10.0)], forced);
            IntelSnapshot Snapshot(IntelStandings standings) => IntelSnapshotBuilder.Build(new IntelSnapshotInput
            {
                Now = Now, Settings = new IntelSettingsValues(), Reports = [report], ReportPilots = [named], LastSeen = [seen],
                SystemNames = new Dictionary<int, string> { [Mid] = "ZZQ-71", [Far] = "XQ1-Z2" }, Standings = standings,
                Own = [new OwnPosition(Me, "Pilot A", Home, Ago(30), OwnPositionSource.ChatLog, MyCorp, Ally)],
            }, new IntelCampRegister(), 1);
            var fromFar = new Dictionary<int, IntelHop> { [Far] = new(0, false), [Mid] = new(2, false) };
            var viewer = new IntelViewer { Origins = [new IntelOrigin(0, Far)], Tiers = new HashSet<StandingTier>(Enum.GetValues<StandingTier>()) };

            var row = IntelLens.Project(Snapshot(Standings()), viewer, Now, o => o.SystemId == Far ? fromFar : null).Systems.Single();
            c.Check("from a system: jumps counted from it, with no character as the origin or the viewer",
                row is { Jumps: 2, NearestOrigin: null, StandingsFrom: null }, $"{row.Jumps} {row.NearestOrigin} {row.StandingsFrom}");
            c.Check("from a system: the colour is the friendliest any character gives (blue to one of them)",
                row.Pilots.Single().Tier == StandingTier.Blue, row.Pilots.Single().Tier.ToString());
            var forced = IntelLens.Project(Snapshot(Standings(Me)), viewer, Now, o => o.SystemId == Far ? fromFar : null).Systems.Single();
            c.Check("from a system: the character the setting forces still decides",
                forced is { StandingsFrom: Me } && forced.Pilots.Single().Tier == StandingTier.Red, $"{forced.StandingsFrom} {forced.Pilots.Single().Tier}");

            c.Check("from a system: own bridges are the alliance all the characters share",
                Standings().BridgeAlliance(0) == Ally && Standings().BridgeAlliance(Me) == Ally);
            var split = new IntelStandings(
                [new IntelStandings.OwnCharacter(Me, "Pilot A", MyCorp, Ally), new IntelStandings.OwnCharacter(Other, "Pilot B", OtherCorp, 0)], [], []);
            c.Check("from a system: with the characters in different alliances, nobody's bridges",
                split.BridgeAlliance(0) is null && split.BridgeAlliance(Me) == Ally && split.BridgeAlliance(Other) is null);
        }

        private static void RowsInEnglish(Checks c)
        {
            var reports = new List<IntelReport>();
            var named = new List<IntelReportCharacter>();
            var seen = new List<IntelLastSeen>();
            var id = 500;
            IntelReport Report(int system, double ago, string message, int count = 0, int flags = 0, string? ships = null, int kind = IntelReportKind.Sighting)
            {
                var r = new IntelReport
                {
                    Id = ++id, ReportedAt = Iso(Ago(ago)), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = system,
                    SystemName = "x", PlayerCount = count, Flags = flags, Ships = ships, Kind = kind, Message = message,
                };
                reports.Add(r);
                return r;
            }
            void Name(IntelReport r, long pilot, string name, int ship, string hull, double ago)
            {
                named.Add(new IntelReportCharacter { IntelReportId = r.Id, CharacterId = pilot, CharacterName = name, ShipTypeId = ship, ShipName = hull });
                seen.Add(new IntelLastSeen
                {
                    CharacterId = pilot, Name = name, SystemId = r.SystemId, SeenAt = Iso(Ago(ago)), Source = IntelLastSeenSource.Report,
                    SourceRank = IntelLastSeenSource.RankOf(IntelLastSeenSource.Report), ReportId = r.Id, ShipTypeId = ship,
                    ShipName = hull, CorporationId = RedCorp, WrittenAt = Iso(Ago(ago)),
                });
            }

            var two = Report(Home, 0.2, "QZ-X77 two of them", 2, (int)IntelRules.IntelFlags.Bubbles);
            Name(two, 94_000_001, "Pilot One", HullA, "Hull A", 0.2);
            Name(two, 94_000_002, "Pilot Two", HullB, "Hull B", 0.2);
            var crowd = Report(Far, 3, "XQ1-Z2 crowd", 12, ships: "8× Hull C, 2× Hull B");
            Name(crowd, 94_000_003, "Pilot Three", HullA, "Hull A", 3);
            Name(crowd, 94_000_004, "Pilot Four", HullB, "Hull B", 3);
            var cyno = Report(Mid, 2, "ZZQ-71 Cyno lit at the citadel", 0, (int)IntelRules.IntelFlags.Cyno);
            var clear = Report(Next, 1, "X2-QZ7 clr", kind: IntelReportKind.Clear);

            // A camp on Next's gate to Home: three pilots lost to one gang, the last a minute ago.
            var gates = new Dictionary<int, IReadOnlyList<IntelGate>> { [Next] = [new IntelGate(51_000_001, Next, Home, 0, 0, 0)] };
            IntelKillAttacker A(long who, int ship) => new(who, RedCorp, 0, ship, null, null, null);
            var gang = new[] { A(95_000_001, HullA), A(95_000_002, HullC), A(95_000_003, HullC) };
            var kills = new List<IntelKill>
            {
                new(8_001, Ago(9), Next, 96_000_001, 0, 0, HullB, null, 10_000, 0, 0, gang),
                new(8_002, Ago(5), Next, 96_000_002, 0, 0, HullB, null, 10_000, 0, 0, gang),
                new(8_003, Ago(1), Next, 96_000_003, 0, 0, HullB, null, 10_000, 0, 0, gang),
            };

            var snapshot = IntelSnapshotBuilder.Build(new IntelSnapshotInput
            {
                Now = Now, Settings = new IntelSettingsValues(),
                Reports = reports, ReportPilots = named, LastSeen = seen, Kills = kills, Gates = gates,
                SystemNames = new Dictionary<int, string> { [Home] = "QZ-X77", [Next] = "X2-QZ7", [Mid] = "ZZQ-71", [Far] = "XQ1-Z2", [Gone] = "K7-QZP" },
                TypeNames = new Dictionary<int, string> { [HullA] = "Hull A", [HullB] = "Hull B", [HullC] = "Hull C" },
                Standings = new IntelStandings([new IntelStandings.OwnCharacter(Me, "Pilot A", MyCorp, 0)], [MyCorp], [(MyCorp, RedCorp, -10.0)]),
                Own = [new OwnPosition(Me, "Pilot A", Home, Ago(30), OwnPositionSource.ChatLog, MyCorp, 0)],
            }, new IntelCampRegister(), 1);

            var hops = new Dictionary<int, IntelHop>
            {
                [Home] = new(0, false), [Next] = new(1, false), [Mid] = new(4, false), [Far] = new(7, false), [Gone] = new(30, false),
            };
            var viewer = new IntelViewer
            {
                Origins = [new IntelOrigin(Me, Home)], MaxJumps = 10, StandingsCharacterId = Me, FeedCap = PanelIntelVm.MaxRows,
            };
            var view = IntelLens.Project(snapshot, viewer, Now, _ => hops);
            IntelEventRowVm Row(Func<IntelFeedRow, bool> which, bool pending = false) =>
                new(view.Feed.First(which), snapshot, Now, pending);

            var r2 = Row(r => r.Event.ReportId == two.Id);
            c.Check("panel: two named pilots read \"2 hostile\"", r2.StateText == "2 hostile", r2.StateText);
            c.Check("panel: each pilot's portrait, then their hull's icon",
                string.Join(",", r2.Pictures.Select(p => (p.IsPilot ? "@" : "#") + p.Text)) == "@Pilot One,#Hull A,@Pilot Two,#Hull B",
                string.Join(",", r2.Pictures.Select(p => p.Kind + ":" + p.Text)));
            c.Check("panel: a pilot's portrait and a hull's icon are asked for by their ids",
                r2.Pictures[0].PictureUrl!.Contains("/characters/94000001/portrait") && r2.Pictures[1].PictureUrl!.Contains("/types/70101/icon"));
            c.Check("panel: the flags follow", r2.TailText == "bubbles", r2.TailText);
            c.Check("panel: at 0 jumps, the red badge", r2.IsHere && r2.JumpsText == "0");
            c.Check("panel: news this new this near lights the row", r2.IsFresh);
            c.Check("panel: the stripe is the worst tier named (red)", ReferenceEquals(r2.TierBrush, IntelTierColors.Red));

            var rc = Row(r => r.Event.ReportId == crowd.Id);
            c.Check("panel: the first few pictures, then the rest counted once (\"+10 unnamed\", no second \"+10\")",
                rc.Pictures.Count == 5 && rc.Pictures[^1].Text == "+10 unnamed" && !rc.HasMore,
                string.Join(",", rc.Pictures.Select(p => p.Text)) + " more=" + rc.MoreText);
            c.Check("panel: 7 jumps is the normal band, and old news there does not light up", !rc.IsHere && !rc.IsNear && !rc.IsMid && !rc.IsFar && !rc.IsFresh);

            var ry = Row(r => r.Event.ReportId == cyno.Id);
            c.Check("panel: a report of a flag alone is that flag", ry.StateText == "cyno" && ry.IsFlagsState, ry.StateText);
            c.Check("panel: and shows what was said, less the system's name", ry.DetailText == "Cyno lit at the citadel", ry.DetailText);
            c.Check("panel: 4 jumps is the amber band", ry.IsMid);

            var rl = Row(r => r.Event.ReportId == clear.Id);
            c.Check("panel: a clear is good, with no standings colour", rl.IsClear && rl.TierBrush is null && rl.StateText == "clear");
            c.Check("panel: a clear never lights up", !rl.IsFresh);

            var camp = Row(r => r.Event.Kind == IntelEventKind.CampFormed);
            c.Check("panel: a camp counts its kills", camp.StateText == "gate camp · 3", camp.StateText);
            c.Check("panel: a camp is as new as its last kill", camp.AgeText == "1 min" && camp.NewestAt == Ago(1), camp.AgeText);
            c.Check("panel: a camp's hulls, most flown first, each with its icon",
                camp.Pictures.Count == 2 && camp.Pictures[0].Text == "Hull C ×2" && camp.Pictures[1].Text == "Hull A",
                string.Join(",", camp.Pictures.Select(p => p.Text)));
            c.Check("panel: a camp says which gate", camp.TailText == "near the QZ-X77 gate", camp.TailText);

            // The camp's line spans the camp: from its last kill to the camp's end, not from when it formed.
            var gateCamp = snapshot.Camps.Single();
            var campEvent = snapshot.Feed.Single(e => e.Kind == IntelEventKind.CampFormed);
            c.Check("panel: a camp's line is dated by its last kill and ends with the camp",
                campEvent.At == gateCamp.LastKill && campEvent.ExpiresAt == gateCamp.ExpiresAt && gateCamp.FormedAt < gateCamp.LastKill,
                $"at {campEvent.At:HH:mm} ends {campEvent.ExpiresAt:HH:mm}, camp formed {gateCamp.FormedAt:HH:mm} ends {gateCamp.ExpiresAt:HH:mm}");
            // Past the camp lifetime after it formed, but not after its last kill: the camp is still on.
            var life = gateCamp.ExpiresAt - gateCamp.LastKill;
            var stillOn = gateCamp.FormedAt + life + TimeSpan.FromMinutes(1);
            var lateView = IntelLens.Project(snapshot, viewer, stillOn, _ => hops);
            c.Check("panel: a camp still on is still one row, past the lifetime from when it formed",
                stillOn < gateCamp.ExpiresAt && lateView.Feed.Any(r => r.Event.Kind == IntelEventKind.CampFormed));
            var lateCamp = new IntelEventRowVm(lateView.Feed.First(r => r.Event.Kind == IntelEventKind.CampFormed), snapshot, stillOn, false);
            c.Check("panel: and it fades from its last kill, not from when it formed",
                lateCamp.Opacity == IntelLens.Opacity(gateCamp.LastKill, life, stillOn) && lateCamp.Opacity > IntelLens.ExpiredOpacity,
                lateCamp.Opacity.ToString(CultureInfo.InvariantCulture));
            var afterEnd = IntelLens.Project(snapshot, viewer, gateCamp.ExpiresAt, _ => hops);
            c.Check("panel: and gone when the camp ends", afterEnd.Feed.All(r => r.Event.Kind != IntelEventKind.CampFormed));

            c.Check("panel: nothing past the range (30 jumps)", view.Feed.All(r => r.Event.SystemId != Gone));
            var waiting = Row(r => r.Event.ReportId == two.Id, pending: true);
            c.Check("panel: a walk not ready yet shows its jumps as they were given", waiting.JumpsText == "0");
            var unknown = IntelLens.Project(snapshot, viewer, Now, _ => null);
            var noWalk = new IntelEventRowVm(unknown.Feed.First(r => r.Event.ReportId == two.Id), snapshot, Now, unknown.Pending);
            c.Check("panel: with the walk not ready, \"…\" rather than blank, and nothing lit",
                noWalk.JumpsText == "…" && noWalk.IsPending && !noWalk.IsFresh, noWalk.JumpsText);

            var later = new IntelEventRowVm(view.Feed.First(r => r.Event.ReportId == two.Id), snapshot, Now, false);
            later.Tick(Now.AddMinutes(3), snapshot.Settings.FreshSeconds);
            c.Check("panel: three minutes on, the row is no longer fresh and says its age", !later.IsFresh && later.AgeText == "3 min", later.AgeText);

            c.Check("panel: ages read as now, seconds, minutes, hours",
                IntelWords.Age(Now, Now) == "now" && IntelWords.Age(Ago(0.75), Now) == "45 s"
                && IntelWords.Age(Ago(65), Now) == "1 h 5 min", IntelWords.Age(Ago(65), Now));

            // Nobody to measure from: no rows from anywhere, just the note saying why.
            var focus = new MonitorFocus();
            var panel = new PanelIntelVm(null, null, [], focus, "M1", null) { Clock = () => Now };
            panel.Apply(snapshot);
            c.Check("panel: with no client on the monitor, no rows, only the note",
                panel.Rows.Count == 0 && panel.Note == IntelText.PanelNoClient && !panel.HasNearest,
                $"rows={panel.Rows.Count} note={panel.Note}");
            focus.Update([new ClientOnMonitor(1, "M1", false, "Pilot Z")]);
            panel.Start();
            c.Check("panel: with a client whose character is not in the app, no rows, only the note",
                panel.Rows.Count == 0 && panel.Note == string.Format(CultureInfo.CurrentCulture, IntelText.PanelNotInApp, "Pilot Z"),
                $"rows={panel.Rows.Count} note={panel.Note}");
            panel.Dispose();
        }
    }
}
