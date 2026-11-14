// A corner of space with hostiles in it, made up for the who-and-what checks and the headless pictures of the
// panel and the Board. Every name and id here is invented: the systems are names known absent from the SDE, the
// pilots, corporations and alliances carry "Qzx" so they can be nobody's, and the hulls are ids no SDE holds
// (but the capsule, whose id is the game's own type).
using System.Globalization;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services.Intel;

namespace EveConsole.Tools.IntelCheck
{
    internal static class HostileScene
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 13, 18, 0, 0, TimeSpan.Zero);
        private static DateTimeOffset Ago(double minutes) => Now.AddMinutes(-minutes);
        private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public const int Home = 39_910_001, Gang = 39_910_002, Lone = 39_910_003, Camp = 39_910_004, Counted = 39_910_005,
                         Flags = 39_910_006, Cleared = 39_910_007, GateTo = 39_910_008, Brawl = 39_910_009;
        public const int Region = 10_910_001;

        public static readonly (int Id, string Name)[] Systems =
        [
            (Home, "QZ-X77"), (Gang, "XQ1-Z2"), (Lone, "ZZQ-71"), (Camp, "X2-QZ7"), (Counted, "K7-QZP"), (Flags, "Q2X-91"),
            (Cleared, "ZXQ-4F"), (GateTo, "9Q-ZX2"), (Brawl, "Q7-ZZX"),
        ];

        /// <summary>Gates: Home–Gang–Lone–Camp–Counted–Flags in a line, Cleared and Brawl beside Home, GateTo beside Camp.</summary>
        public static readonly Dictionary<int, int[]> Gates = new()
        {
            [Home] = [Gang, Cleared, Brawl], [Gang] = [Home, Lone], [Lone] = [Gang, Camp], [Camp] = [Lone, Counted, GateTo],
            [Counted] = [Camp, Flags], [Flags] = [Counted], [Cleared] = [Home], [GateTo] = [Camp], [Brawl] = [Home],
        };

        public static Dictionary<int, IntelHop> Hops()
        {
            var hops = new Dictionary<int, IntelHop> { [Home] = new(0, false) };
            var frontier = new List<int> { Home };
            for (var d = 1; frontier.Count > 0; d++)
            {
                var next = new List<int>();
                foreach (var s in frontier)
                    foreach (var n in Gates[s])
                        if (hops.TryAdd(n, new IntelHop(d, false))) next.Add(n);
                frontier = next;
            }
            return hops;
        }

        // Who.
        public const long Me = 93_910_001, MyCorp = 98_910_001, MyAlliance = 99_910_001;
        public const long AllyA = 99_910_010, AllyB = 99_910_011;              // two hostile alliances
        public const long CorpA1 = 98_910_010, CorpA2 = 98_910_011, CorpB = 98_910_012, CorpC = 98_910_013, CorpN = 98_910_014;
        public const long A1 = 94_910_001, A2 = 94_910_002, A3 = 94_910_003, A4 = 94_910_004, B1 = 94_910_005, C1 = 94_910_006,
                          A5 = 94_910_007, Loner = 94_910_008, Camper1 = 94_910_009, Camper2 = 94_910_010, Victim = 94_910_011,
                          CampVictim1 = 94_910_012, CampVictim2 = 94_910_013, Unknown1 = 94_910_014;

        // What they fly: invented hulls, and the capsule.
        public const int Raider = 70_910, Lancer = 70_911, Bastion = 70_912, Hauler = 70_913, Capsule = 670;

        public static readonly Dictionary<long, string> Names = new()
        {
            [AllyA] = "Qzx Lantern Alliance", [AllyB] = "Qzx Thistle Pact", [CorpA1] = "Qzx Lantern Works", [CorpA2] = "Qzx Lantern Yards",
            [CorpB] = "Qzx Thistle Guard", [CorpC] = "Qzx Heron Holdings", [CorpN] = "Qzx Quiet Traders", [MyCorp] = "Qzx Home Corp",
            [MyAlliance] = "Qzx Home Alliance",
        };

        public static readonly Dictionary<int, string> TypeNames = new()
        {
            [Raider] = "Raider QX", [Lancer] = "Lancer QX", [Bastion] = "Bastion QX", [Hauler] = "Hauler QX", [Capsule] = "Capsule",
        };

        public static IntelStandings Standings() => new(
            [new IntelStandings.OwnCharacter(Me, "Pilot Qzx Home", MyCorp, MyAlliance)],
            [MyCorp],
            [(MyAlliance, AllyA, -10.0), (MyAlliance, AllyB, -5.0)]);

        private static int _id = 7_000;

        private static IntelReport Report(int system, double ago, int count = 0, int flags = 0, string? ships = null,
                                          int kind = IntelReportKind.Sighting, bool noVisual = false, string? gate = null) => new()
        {
            Id = ++_id, ReportedAt = Iso(Ago(ago)), ChannelName = "qzx-intel", ReporterName = "Scout Qzx", SystemId = system,
            SystemName = Systems.First(s => s.Id == system).Name, PlayerCount = count, Flags = flags, Ships = ships, Kind = kind,
            Source = IntelReportSource.Chat, NoVisual = noVisual, Gate = gate,
            Message = Systems.First(s => s.Id == system).Name + " qzx",
        };

        private static IntelLastSeen Seen(long pilot, int system, double ago, int ship, long corp, long alliance,
                                          int source = IntelLastSeenSource.Report, int? report = null, int? kill = null) => new()
        {
            CharacterId = pilot, Name = PilotName(pilot), SystemId = system, SeenAt = Iso(Ago(ago)), Source = source,
            SourceRank = IntelLastSeenSource.RankOf(source), ReportId = report, KillMailId = kill, ShipTypeId = ship,
            ShipName = TypeNames[ship], CorporationId = corp, AllianceId = alliance == 0 ? null : alliance, WrittenAt = Iso(Ago(ago)),
            InSystemSince = Iso(Ago(ago)),
        };

        public static string PilotName(long id) => "Pilot Qzx " + (id % 100).ToString("00", CultureInfo.InvariantCulture);

        private static IntelKillAttacker Attacker(long who, long corp, long alliance, int ship) => new(who, corp, alliance, ship, null, null, null);

        /// <summary>
        /// <list type="bullet">
        /// <item>XQ1-Z2, 1 jump: a gang of seven named: four of Qzx Lantern Alliance (two corporations), one of Qzx Thistle
        /// Pact, one of Qzx Heron Holdings (a corporation in no alliance), one whose corporation is not known yet; four in
        /// Raiders, two in Lancers, one in a capsule. Two of them killed a neutral hauler a minute ago: the victim is no
        /// hostile.</item>
        /// <item>ZZQ-71, 2 jumps: one pilot of Qzx Thistle Pact in a Lancer.</item>
        /// <item>X2-QZ7, 3 jumps: a camp on the 9Q-ZX2 gate: two pilots of Qzx Heron Holdings killed two more.</item>
        /// <item>K7-QZP, 4 jumps: five counted, no visual, said to be in Bastions.</item>
        /// <item>Q2X-91, 5 jumps: a cyno and bubbles, nobody counted.</item>
        /// <item>ZXQ-4F, 1 jump: called clear.</item>
        /// </list>
        /// </summary>
        public static IntelSnapshotInput Input()
        {
            var gang1 = Report(Gang, 3, count: 3);
            var gang2 = Report(Gang, 1.5, count: 4);
            var lone = Report(Lone, 2, count: 1);
            var counted = Report(Counted, 4, count: 5, ships: "5× Bastion QX", noVisual: true);
            var flags = Report(Flags, 6, flags: (int)(IntelRules.IntelFlags.Cyno | IntelRules.IntelFlags.Bubbles));
            var clear = Report(Cleared, 2, kind: IntelReportKind.Clear);
            var reports = new List<IntelReport> { gang1, gang2, lone, counted, flags, clear };

            IntelReportCharacter Named(IntelReport r, long pilot, int ship) =>
                new() { IntelReportId = r.Id, CharacterId = pilot, CharacterName = PilotName(pilot), ShipTypeId = ship, ShipName = TypeNames[ship] };
            var named = new List<IntelReportCharacter>
            {
                Named(gang1, A1, Raider), Named(gang1, A2, Raider), Named(gang1, A3, Raider), Named(gang2, A4, Raider),
                Named(gang2, B1, Lancer), Named(gang2, C1, Lancer), Named(gang2, Unknown1, Capsule), Named(lone, Loner, Lancer),
            };

            const int GangKill = 8_910_001, CampKill1 = 8_910_002, CampKill2 = 8_910_003;
            var seen = new List<IntelLastSeen>
            {
                Seen(A1, Gang, 1, Raider, CorpA1, AllyA, IntelLastSeenSource.KillAttacker, kill: GangKill),
                Seen(A2, Gang, 1, Raider, CorpA1, AllyA, IntelLastSeenSource.KillAttacker, kill: GangKill),
                Seen(A3, Gang, 3, Raider, CorpA2, AllyA, report: gang1.Id),
                Seen(A4, Gang, 1.5, Raider, CorpA2, AllyA, report: gang2.Id),
                Seen(B1, Gang, 1.5, Lancer, CorpB, AllyB, report: gang2.Id),
                Seen(C1, Gang, 1.5, Lancer, CorpC, 0, report: gang2.Id),
                Seen(Unknown1, Gang, 1.5, Capsule, 0, 0, report: gang2.Id),
                Seen(Victim, Gang, 1, Hauler, CorpN, 0, IntelLastSeenSource.KillVictim, kill: GangKill),
                Seen(Loner, Lone, 2, Lancer, CorpB, AllyB, report: lone.Id),
                Seen(Camper1, Camp, 2, Bastion, CorpC, 0, IntelLastSeenSource.KillAttacker, kill: CampKill2),
                Seen(Camper2, Camp, 2, Raider, CorpC, 0, IntelLastSeenSource.KillAttacker, kill: CampKill2),
            };
            var gangOnKill = new[] { Attacker(A1, CorpA1, AllyA, Raider), Attacker(A2, CorpA1, AllyA, Raider) };
            var campers = new[] { Attacker(Camper1, CorpC, 0, Bastion), Attacker(Camper2, CorpC, 0, Raider) };
            var kills = new List<IntelKill>
            {
                new(GangKill, Ago(1), Gang, Victim, CorpN, 0, Hauler, null, 0, 0, 0, gangOnKill),
                new(CampKill1, Ago(8), Camp, CampVictim1, CorpN, 0, Hauler, null, 10_000, 0, 0, campers),
                new(CampKill2, Ago(2), Camp, CampVictim2, CorpN, 0, Hauler, null, 10_000, 0, 0, campers),
            };
            var gates = new Dictionary<int, IReadOnlyList<IntelGate>> { [Camp] = [new IntelGate(51_910_001, Camp, GateTo, 0, 0, 0)] };

            return new IntelSnapshotInput
            {
                Now = Now, Settings = new IntelSettingsValues { CampMinVictims = 2 },
                Reports = reports, ReportPilots = named, LastSeen = seen, Kills = kills, Gates = gates,
                SystemNames = Systems.ToDictionary(s => s.Id, s => s.Name),
                TypeNames = TypeNames,
                HullIds = TypeNames.ToDictionary(t => t.Value, t => t.Key, StringComparer.OrdinalIgnoreCase),
                Names = Names,
                Regions = Systems.ToDictionary(s => s.Id, _ => (Region, "Qzx Reach")),
                Standings = Standings(),
                Own = [new OwnPosition(Me, "Pilot Qzx Home", Home, Ago(30), OwnPositionSource.ChatLog, MyCorp, MyAlliance)],
            };
        }

        public static IntelSnapshot Snapshot() => IntelSnapshotBuilder.Build(Input(), new IntelCampRegister(), 1);

        // A brawl, for the Feed's who: a pilot of Qzx Thistle Pact lost a Lancer in Q7-ZZX to eight: five of Qzx Lantern
        // Alliance, two of Qzx Ember Union, one of Qzx Quiet Traders (a corporation in no alliance).
        public const long AllyC = 99_910_012, CorpE = 98_910_015, BrawlVictim = 94_910_030;
        public const int BrawlKill = 8_910_009;
        public static readonly long[] BrawlLantern = [94_910_031, 94_910_032, 94_910_033, 94_910_034, 94_910_035];
        public static readonly long[] BrawlEmber = [94_910_036, 94_910_037];
        public const long BrawlTrader = 94_910_038;

        /// <summary>The scene with the brawl added: kept apart, so the rows the other checks count stay as they were.</summary>
        public static IntelSnapshotInput FeedInput()
        {
            var input = Input();
            var attackers = BrawlLantern.Select((id, i) => Attacker(id, CorpA1, AllyA, i < 3 ? Raider : Lancer))
                .Concat(BrawlEmber.Select(id => Attacker(id, CorpE, AllyC, Raider)))
                .Append(Attacker(BrawlTrader, CorpN, 0, Bastion))
                .ToArray();
            var seen = attackers.Select(a => Seen(a.CharacterId, Brawl, 0.5, a.ShipTypeId ?? Raider, a.CorporationId, a.AllianceId,
                                                  IntelLastSeenSource.KillAttacker, kill: BrawlKill))
                .Append(Seen(BrawlVictim, Brawl, 0.5, Lancer, CorpB, AllyB, IntelLastSeenSource.KillVictim, kill: BrawlKill));
            var names = new Dictionary<long, string>(Names) { [AllyC] = "Qzx Ember Union", [CorpE] = "Qzx Ember Forge" };
            return input with
            {
                LastSeen = [.. input.LastSeen, .. seen],
                Kills = [.. input.Kills, new IntelKill(BrawlKill, Ago(0.5), Brawl, BrawlVictim, CorpB, AllyB, Lancer, null, 0, 0, 0, attackers)],
                Names = names,
            };
        }

        public static IntelSnapshot FeedSnapshot() => IntelSnapshotBuilder.Build(FeedInput(), new IntelCampRegister(), 2);
    }
}
