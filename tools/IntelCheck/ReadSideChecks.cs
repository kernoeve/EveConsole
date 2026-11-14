// The read side of intel: standings in the game's order, distances over gates, holes and bridges, gate camps from
// kill positions with their SDE groups found by name, the snapshot's rules, the per-viewer lens (sorts, fade,
// filter-then-cap, nearest origin, the clear state), the snapshot service on a database, and the history purge.
// Every name and id here is invented.
using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck
{
    internal static partial class ReadSideChecks
    {
        // Systems.
        private const int S1 = 30_000_001, S2 = 30_000_002, S3 = 30_000_003, S4 = 30_000_004, S5 = 30_000_005,
                          S6 = 30_000_006, S7 = 30_000_007, S8 = 30_000_008, S9 = 30_000_009;

        // Own characters, their corporations and alliance.
        private const long A = 91_000_001, B = 91_000_002;
        private const long CorpA = 98_100_001, CorpB = 98_100_002, AllyA = 99_100_001;

        // Other pilots.
        private const long P1 = 92_000_001, P2 = 92_000_002, P3 = 92_000_003, P4 = 92_000_004, P5 = 92_000_005,
                           P6 = 92_000_006, P7 = 92_000_007, P8 = 92_000_008, P9 = 92_000_009, P10 = 92_000_010,
                           P11 = 92_000_011, P12 = 92_000_012, P13 = 92_000_013;
        private const long FriendCorp = 98_100_009, FarCorp = 98_100_020, FoeAlly = 99_100_009;

        private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

        private static string Iso(DateTimeOffset at) => IntelLastSeenWriter.Format(at);
        private static DateTimeOffset Ago(double minutes) => Now.AddMinutes(-minutes);

        // ── Standings ───────────────────────────────────────────────────────────────────────────────────────

        private static IntelStandings Standings(long forced = 0) => new(
            [new IntelStandings.OwnCharacter(A, "Pilot Alpha", CorpA, AllyA), new IntelStandings.OwnCharacter(B, "Pilot Bravo", CorpB, 0)],
            [CorpA, CorpB],
            [
                (A, P1, -10), (A, FriendCorp, 10), (A, P6, -10),        // A's personal contacts
                (CorpA, P1, 10), (CorpA, P2, -5),                       // A's corporation's
                (AllyA, P2, 10), (AllyA, P3, 5), (AllyA, FoeAlly, -10), // A's alliance's
            ],
            forced);

        public static void StandingChecks(Checks c)
        {
            var s = Standings();
            c.Check("tiers: personal contacts come before the corporation's (-10 personal over +10 corp)",
                s.TierOf(A, P1, FarCorp, 0) == StandingTier.Red, s.TierOf(A, P1, FarCorp, 0).ToString());
            c.Check("tiers: the corporation's come before the alliance's (-5 corp over +10 alliance)",
                s.TierOf(A, P2, FarCorp, 0) == StandingTier.Orange, s.TierOf(A, P2, FarCorp, 0).ToString());
            c.Check("tiers: the alliance's when nothing nearer says (+5)", s.TierOf(A, P3, FarCorp, 0) == StandingTier.LightBlue);
            c.Check("tiers: a contact on the pilot's corporation", s.TierOf(A, P4, FriendCorp, 0) == StandingTier.Blue);
            c.Check("tiers: a contact on the pilot's alliance", s.TierOf(A, P5, FarCorp, FoeAlly) == StandingTier.Red);
            c.Check("tiers: the viewer's own corporation is blue whatever a contact says",
                s.TierOf(A, P6, CorpA, 0) == StandingTier.Blue, s.TierOf(A, P6, CorpA, 0).ToString());
            c.Check("tiers: the viewer's own alliance is blue", s.TierOf(A, P7, FarCorp, AllyA) == StandingTier.Blue);
            c.Check("tiers: nobody said anything: neutral", s.TierOf(A, P7, FarCorp, 0) == StandingTier.Neutral);
            c.Check("tiers: own characters are their own state, for any viewer",
                s.TierOf(B, A, CorpA, AllyA) == StandingTier.Own && s.TierOf(A, A, CorpA, AllyA) == StandingTier.Own);
            c.Check("tiers: another viewer has its own standings (B knows nothing of P1)", s.TierOf(B, P1, FarCorp, 0) == StandingTier.Neutral);
            c.Check("tiers: with no viewer the friendliest any own character gives (A red, B neutral: neutral)",
                s.UnionTier(P1, FarCorp, 0) == StandingTier.Neutral && s.TierFor(null, P4, FriendCorp, 0) == StandingTier.Blue);
            c.Check("tiers: with no viewer an own corporation is blue", s.UnionTier(P6, CorpB, 0) == StandingTier.Blue);
            var forced = Standings(forced: B);
            c.Check("tiers: a forced character is used for every viewer",
                forced.TierFor(A, P1, FarCorp, 0) == StandingTier.Neutral && forced.EffectiveViewer(A) == B);
            c.Check("tiers: a forced character that is not ours is ignored", Standings(forced: P1).ForcedCharacterId == 0);
            c.Check("tiers: personal contacts known for A, not for B", s.HasPersonalContacts(A) && !s.HasPersonalContacts(B));

            var steps = new (double Standing, StandingTier Tier)[]
            {
                (-10, StandingTier.Red), (-5.01, StandingTier.Red), (-5, StandingTier.Orange), (-0.1, StandingTier.Orange),
                (0, StandingTier.Neutral), (0.1, StandingTier.LightBlue), (5, StandingTier.LightBlue), (5.1, StandingTier.Blue),
                (10, StandingTier.Blue),
            };
            foreach (var (standing, tier) in steps)
                c.Check($"tiers: {standing} is {tier}", StandingTiers.Of(standing) == tier, StandingTiers.Of(standing).ToString());
        }

        // ── SDE groups, by name ─────────────────────────────────────────────────────────────────────────────

        private const int ShipCat = 9_906, ModuleCat = 9_907;
        private const int FrigateGroup = 9_910, CapsuleGroup = 9_911, DictorGroup = 9_912, HicGroup = 9_913,
                          SmartbombGroup = 9_920, LauncherGroup = 9_921, FieldGroup = 9_922, StructureGroup = 9_930,
                          MobileGroup = 9_940, CynoGroup = 9_941;

        private static IntelSdeGroups Groups(bool withSmartbomb = true) => new(
            new (int, int, string)[]
            {
                (FrigateGroup, ShipCat, "Frigate"), (CapsuleGroup, ShipCat, IntelSdeGroups.CapsuleName),
                (DictorGroup, ShipCat, IntelSdeGroups.InterdictorName), (HicGroup, ShipCat, IntelSdeGroups.HeavyInterdictorName),
                (SmartbombGroup, ModuleCat, withSmartbomb ? IntelSdeGroups.SmartBombName : "Smart Bomb (renamed)"),
                (LauncherGroup, ModuleCat, IntelSdeGroups.SphereLauncherName), (FieldGroup, ModuleCat, IntelSdeGroups.FieldGeneratorName),
                (StructureGroup, 9_965, "Citadel"), (MobileGroup, 9_922, IntelSdeGroups.MobileDisruptorName),
                (CynoGroup, 9_922, IntelSdeGroups.CynoFieldName),
            },
            [(ShipCat, IntelSdeGroups.ShipCategoryName), (ModuleCat, "Module")]);

        public static async Task SdeGroupChecks(Checks c, string root, string profile)
        {
            var g = Groups();
            c.Check("SDE groups: every name intel reads is found", g.Missing.Count == 0, string.Join(",", g.Missing));
            c.Check("SDE groups: by the ids the SDE has, not remembered numbers",
                g.Group(IntelSdeGroups.SmartBombName) == SmartbombGroup && g.Group(IntelSdeGroups.InterdictorName) == DictorGroup
                && g.Group(IntelSdeGroups.CapsuleName) == CapsuleGroup && g.ShipCategory == ShipCat);
            c.Check("SDE groups: a ship's group is a ship, a structure's is not", g.IsShip(FrigateGroup) && !g.IsShip(StructureGroup));

            var renamed = Groups(withSmartbomb: false);
            c.Check("SDE groups: a group the SDE no longer names is reported missing",
                renamed.Missing.SequenceEqual([IntelSdeGroups.SmartBombName]), string.Join(",", renamed.Missing));
            c.Check("SDE groups: and its flag is off", !renamed.Is(SmartbombGroup, IntelSdeGroups.SmartBombName));

            // The same from a database, as the snapshot loads it.
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={Path.Combine(profile, "sdegroups.db")}").Options;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.SdeCategories.Add(new SdeCategory { CategoryId = ShipCat, Name = IntelSdeGroups.ShipCategoryName });
                db.SdeGroups.Add(new SdeGroup { GroupId = DictorGroup, CategoryId = ShipCat, Name = IntelSdeGroups.InterdictorName });
                db.SdeGroups.Add(new SdeGroup { GroupId = SmartbombGroup, CategoryId = ModuleCat, Name = IntelSdeGroups.SmartBombName });
                await db.SaveChangesAsync();
            }
            await using (var db = new AppDbContext(options))
            {
                var loaded = await IntelSdeGroups.LoadAsync(db);
                c.Check("SDE groups: loaded from the database by name",
                    loaded.Group(IntelSdeGroups.InterdictorName) == DictorGroup && loaded.ShipCategory == ShipCat
                    && loaded.Missing.Contains(IntelSdeGroups.CapsuleName));
            }

            // ⚠️ No group number written into the detector: it must read the SDE's.
            var detector = File.ReadAllText(Path.Combine(root, "Services", "Intel", "IntelCampDetector.cs"));
            var numbers = Regex.Matches(detector, @"\b(29|72|361|541|589|894|899|1405)\b").Select(m => m.Value).ToList();
            c.Check("SDE groups: the camp detector holds no group numbers of its own", numbers.Count == 0, string.Join(",", numbers));
        }

        // ── Camps ───────────────────────────────────────────────────────────────────────────────────────────

        private const long G1 = 50_000_001, G2 = 50_000_002;
        private const int Hull = 70_001, DictorHull = 70_002, Pod = 70_003, SmartbombType = 80_001;

        private static readonly IReadOnlyDictionary<int, IReadOnlyList<IntelGate>> Gates = new Dictionary<int, IReadOnlyList<IntelGate>>
        {
            [S1] = [new IntelGate(G1, S1, S2, 0, 0, 0), new IntelGate(G2, S1, S3, 1e9, 0, 0)],
        };

        private static IntelKillAttacker Attacker(long id, int? ship = Hull, int? shipGroup = FrigateGroup,
                                                  int? weapon = null, int? weaponGroup = null, long corp = FarCorp) =>
            new(id, corp, 0, ship, shipGroup, weapon, weaponGroup);

        private static IntelKill Kill(int id, DateTimeOffset at, long victim, double x, IntelKillAttacker[] attackers,
                                      int victimGroup = FrigateGroup, int system = S1) =>
            new(id, at, system, victim, FarCorp, 0, victimGroup == CapsuleGroup ? Pod : Hull, victimGroup, x, 0, 0, attackers);

        private static List<IntelKill> CampKills(DateTimeOffset t0) =>
        [
            // On G1: two pilots lost to the same gang within ten minutes, then one of them in a pod.
            Kill(1, t0, P1, 10_000, [Attacker(P5, DictorHull, DictorGroup), Attacker(P6)]),
            Kill(2, t0.AddMinutes(10), P2, 100_000, [Attacker(P6, weapon: SmartbombType, weaponGroup: SmartbombGroup), Attacker(P7)]),
            Kill(3, t0.AddMinutes(11), P2, 100_000, [Attacker(P7)], CapsuleGroup),
            // 200 km off G1: not a gate kill.
            Kill(4, t0.AddMinutes(12), P3, 200_000, [Attacker(P6)]),
            // G2, one kill: a kill, not a camp.
            Kill(5, t0.AddMinutes(5), P4, 1e9 - 5_000, [Attacker(P8)]),
            // G1 again 39 minutes after the last: a run of its own, and alone.
            Kill(6, t0.AddMinutes(50), P8, 10_000, [Attacker(P5)]),
            // A structure lost on G1 is no pilot lost.
            Kill(7, t0.AddMinutes(2), P9, 10_000, [Attacker(P6)], StructureGroup),
        ];

        public static void CampChecks(Checks c)
        {
            var g = Groups();
            var t0 = Ago(40);
            var kills = CampKills(t0);
            var camps = IntelCampDetector.Detect(kills, Gates, g, 150, TimeSpan.FromMinutes(30), 2);
            var camp = camps.FirstOrDefault();
            c.Check("camps: one camp, on the gate the kills were near", camps.Count == 1 && camp!.StargateId == G1 && camp.GateSystemId == S2,
                string.Join(" | ", camps.Select(x => $"{x.StargateId} {string.Join(",", x.Kills.Select(k => k.KillMailId))}")));
            if (camp is null) return;
            c.Check("camps: its kills are the gate kills sharing attackers, the pod among them",
                camp.Kills.Select(k => k.KillMailId).SequenceEqual([1, 2, 3]), string.Join(",", camp.Kills.Select(k => k.KillMailId)));
            c.Check("camps: a pod is a kill but not another pilot lost", camp.VictimIds.SequenceEqual([P1, P2]), string.Join(",", camp.VictimIds));
            c.Check("camps: it became a camp with the second pilot lost", camp.FormedAt == t0.AddMinutes(10));
            c.Check("camps: smartbomb and interdictor from the attackers' groups, nothing else",
                camp.Smartbomb && camp.Interdictor && !camp.Hic && !camp.Bubble);
            c.Check("camps: a renamed smartbomb group turns that flag off and nothing else",
                IntelCampDetector.Detect(kills, Gates, Groups(withSmartbomb: false), 150, TimeSpan.FromMinutes(30), 2)
                    is [{ Smartbomb: false, Interdictor: true }]);
            c.Check("camps: three pilots needed, two lost: no camp",
                IntelCampDetector.Detect(kills, Gates, g, 150, TimeSpan.FromMinutes(30), 3).Count == 0);
            c.Check("camps: 5 km from the gate, the first kill (10 km) is not a gate kill, so no camp",
                IntelCampDetector.Detect(kills, Gates, g, 5, TimeSpan.FromMinutes(30), 2).Count == 0);
            c.Check("camps: a 5-minute window splits the two kills ten minutes apart",
                IntelCampDetector.Detect(kills, Gates, g, 150, TimeSpan.FromMinutes(5), 2).Count == 0);
            var noShared = new List<IntelKill>
            {
                Kill(11, t0, P1, 10_000, [Attacker(P5)]),
                Kill(12, t0.AddMinutes(1), P2, 10_000, [Attacker(P6)]),
            };
            c.Check("camps: two kills on a gate with no attacker in common are not a camp",
                IntelCampDetector.Detect(noShared, Gates, g, 150, TimeSpan.FromMinutes(30), 2).Count == 0);
            c.Check("camps: the nearest gate is the nearest", IntelCampDetector.NearestGate(Gates, S1, 999_000_000, 0, 0)?.StargateId == G2
                && IntelCampDetector.NearestGate(Gates, S1, 10_000, 0, 0) is { StargateId: G1, Km: 10 });
            c.Check("camps: a system without gates has no nearest gate", IntelCampDetector.NearestGate(Gates, S9, 0, 0, 0) is null);

            // The register: a camp keeps its identity while the window slides past its first kill, and a new
            // one on the same gate after it ended is new.
            var register = new IntelCampRegister();
            var life = TimeSpan.FromMinutes(30);
            var kept = IntelLens.CampKept;
            var first = register.Apply(camps, Now, life, TimeSpan.FromMinutes(30), kept).Single();
            var later = new List<IntelKill>(kills.Where(k => k.KillMailId != 1))
            {
                Kill(21, t0.AddMinutes(20), P10, 10_000, [Attacker(P7)]),
            };
            var slid = IntelCampDetector.Detect(later, Gates, g, 150, TimeSpan.FromMinutes(30), 2);
            var again = register.Apply(slid, Now.AddMinutes(5), life, TimeSpan.FromMinutes(30), kept);
            c.Check("camps: the same camp keeps its key after its first kill drops out",
                again.Count == 1 && again[0].Key == first.Key && again[0].EpisodeStart == t0,
                $"{first.Key} then {string.Join(",", again.Select(x => x.Key))}");
            // Its last kill is at t0 + 20 (Now - 20): held, as last read, until an hour after that.
            var held = register.Apply([], Now.AddMinutes(39), life, TimeSpan.FromMinutes(30), kept);
            c.Check("camps: a camp no reading shows any more is held, as last read, ended",
                held.Count == 1 && held[0].Key == first.Key && held[0].LastKill == t0.AddMinutes(20) && !held[0].IsActive(Now.AddMinutes(39)),
                string.Join(",", held.Select(x => $"{x.Key} {x.LastKill:HH:mm}")));
            c.Check("camps: an hour after its last kill the held camp is let go",
                register.Apply([], Now.AddMinutes(40), life, TimeSpan.FromMinutes(30), kept).Count == 0 && register.Count == 0,
                register.Count.ToString());
            register.Apply(slid, Now.AddMinutes(5), life, TimeSpan.FromMinutes(30), kept);
            register.Apply([], Now.AddHours(2), life, TimeSpan.FromMinutes(30), kept);
            c.Check("camps: an ended camp nobody shows is forgotten", register.Count == 0, register.Count.ToString());
            var fresh = register.Apply(camps, Now.AddHours(2), life, TimeSpan.FromMinutes(30), kept).Single();
            c.Check("camps: a camp read after that is keyed by its own first kill",
                fresh.Key == IntelCampRegister.KeyOf(G1, t0) && fresh.ExpiresAt == t0.AddMinutes(11) + life);

            // Two clusters on one gate within the window are one episode: one camp, one key, everything of both.
            // Read as two camps they shared a key, and the Feed failed on the duplicate every refresh.
            var tail = camp with
            {
                Kills = [Kill(31, t0.AddMinutes(25), P10, 10_000, [Attacker(P7)])], VictimIds = [P10],
                FirstKill = t0.AddMinutes(25), LastKill = t0.AddMinutes(26), FormedAt = t0.AddMinutes(26),
                Attackers = [.. camp.Attackers.Take(1)], Smartbomb = false, Interdictor = false, Bubble = true,
            };
            var joined = new IntelCampRegister().Apply([camp, tail], Now, life, TimeSpan.FromMinutes(30), kept);
            c.Check("camps: two clusters of one episode on a gate are one camp, with the kills, victims and flags of both",
                joined.Count == 1 && joined[0].KillMailIds.SequenceEqual([1, 2, 3, 31]) && joined[0].VictimIds.SequenceEqual([P1, P2, P10])
                && joined[0].LastKill == t0.AddMinutes(26) && joined[0].ExpiresAt == t0.AddMinutes(26) + life
                && joined[0].Smartbomb && joined[0].Bubble && joined[0].Attackers.Count == camp.Attackers.Count,
                string.Join(" | ", joined.Select(x => $"{x.Key} {string.Join(",", x.KillMailIds)}")));
        }

        // ── Distances ───────────────────────────────────────────────────────────────────────────────────────

        private const int Hub = S9;

        private static IReadOnlyDictionary<int, int[]> Line() => new Dictionary<int, int[]>
        {
            [S1] = [S2], [S2] = [S1, S3], [S3] = [S2, S4], [S4] = [S3, S5], [S5] = [S4],
        };

        private static IntelDistanceService.Edges Edges() => new(
            new Dictionary<int, int[]> { [Hub] = [S1, S5], [S1] = [Hub], [S5] = [Hub] },
            [(S2, S4, 77, true), (S4, S2, 0, false)]);

        public static async Task DistanceChecks(Checks c)
        {
            var d = new IntelDistanceService(Line(), Edges());
            c.Check("distance: nothing is cached before a walk", d.TryCached(S1, IntelGraphMode.Threat) is null);
            var from1 = await d.JumpsFromAsync(S1, IntelGraphMode.Threat);
            c.Check("distance: the hole is a way, both ways (S5 is 2 through the hub, not 4 by gates)",
                from1.GetValueOrDefault(S5) == new IntelHop(2, true) && from1.GetValueOrDefault(Hub) == new IntelHop(1, true),
                $"{from1.GetValueOrDefault(S5)} {from1.GetValueOrDefault(Hub)}");
            c.Check("distance: a system as near by gates as through a hole is not \"via a wormhole\"",
                from1.GetValueOrDefault(S4) == new IntelHop(3, false), from1.GetValueOrDefault(S4).ToString());
            c.Check("distance: gates alone stay gates", from1.GetValueOrDefault(S3) == new IntelHop(2, false));
            c.Check("distance: the walk is cached for the view to take without waiting", ReferenceEquals(d.TryCached(S1, IntelGraphMode.Threat), from1));

            var threat2 = await d.JumpsFromAsync(S2, IntelGraphMode.Threat);
            c.Check("distance: threat distance never uses a bridge", threat2.GetValueOrDefault(S4).Jumps == 2);
            var travel77 = await d.JumpsFromAsync(S2, IntelGraphMode.Travel, 77);
            c.Check("distance: travel uses a bridge the holder's alliance may jump", travel77.GetValueOrDefault(S4).Jumps == 1);
            var travel88 = await d.JumpsFromAsync(S2, IntelGraphMode.Travel, 88);
            c.Check("distance: but not another alliance's", travel88.GetValueOrDefault(S4).Jumps == 2);
            var back88 = await d.JumpsFromAsync(S4, IntelGraphMode.Travel, 88);
            c.Check("distance: a way whose holder is not known is allowed for a pilot in an alliance", back88.GetValueOrDefault(S2).Jumps == 1);
            var backNone = await d.JumpsFromAsync(S4, IntelGraphMode.Travel, null);
            c.Check("distance: and not for a pilot in none", backNone.GetValueOrDefault(S2).Jumps == 2);
            c.Check("distance: the route planner's rule, as one function",
                RoutePlannerService.BridgeUsable(new BridgeDirection(1, 2, 77, "", 0, "", null, 0), 77)
                && !RoutePlannerService.BridgeUsable(new BridgeDirection(1, 2, 0, "", 0, "", null, 0), 77)
                && !RoutePlannerService.BridgeUsable(new BridgeDirection(1, 2, 77, "", 0, "", null, 0), null)
                && RoutePlannerService.BridgeUsable(null, 88) && !RoutePlannerService.BridgeUsable(null, null));

            // No range cap.
            var chain = new Dictionary<int, int[]>();
            for (var i = 0; i < 300; i++) chain[1_000 + i] = i == 0 ? [1_001] : i == 299 ? [1_298] : [1_000 + i - 1, 1_000 + i + 1];
            var far = IntelDistanceService.Walk(1_000, chain, new Dictionary<int, List<(int, bool)>>());
            c.Check("distance: no range cap (299 jumps)", far.GetValueOrDefault(1_299).Jumps == 299);
        }

        // ── The snapshot's rules ───────────────────────────────────────────────────────────────────────────

        private static int _reportId = 1_000;

        private static IntelReport Report(int system, DateTimeOffset at, int count = 0, int flags = 0, int kind = IntelReportKind.Sighting,
                                          int source = IntelReportSource.Chat, int? parent = null, int? from = null,
                                          string channel = "intelcheck", bool noVisual = false, string? ships = null, bool obsolete = false) => new()
        {
            Id = ++_reportId, ReportedAt = Iso(at), ChannelName = channel, ReporterName = "Scout Alpha", SystemId = system,
            SystemName = "QZ-X77", PlayerCount = count, Flags = flags, Kind = kind, Source = source, ParentReportId = parent,
            FromSystemId = from, NoVisual = noVisual, Ships = ships, Obsolete = obsolete, Message = "intelcheck",
        };

        private static IntelReportCharacter Named(IntelReport r, long pilot) => new()
        {
            IntelReportId = r.Id, CharacterId = pilot, CharacterName = $"Pilot {pilot % 1000}",
        };

        private static IntelLastSeen Seen(long pilot, int system, DateTimeOffset at, int source = IntelLastSeenSource.Report,
                                          int? reportId = null, long corp = 0, long alliance = 0, bool noVisual = false) => new()
        {
            CharacterId = pilot, Name = $"Pilot {pilot % 1000}", SystemId = system, SeenAt = Iso(at), Source = source,
            SourceRank = IntelLastSeenSource.RankOf(source), ReportId = reportId, CorporationId = corp > 0 ? corp : null,
            AllianceId = alliance > 0 ? alliance : null, NoVisual = noVisual, WrittenAt = Iso(at),
        };

        private static IntelSnapshot Build(IEnumerable<IntelReport> reports, IEnumerable<IntelReportCharacter> named,
                                           IEnumerable<IntelLastSeen> seen, IEnumerable<IntelKill>? kills = null,
                                           IntelSettingsValues? settings = null, IntelStandings? standings = null) =>
            IntelSnapshotBuilder.Build(new IntelSnapshotInput
            {
                Now = Now, Settings = settings ?? new IntelSettingsValues(),
                Reports = [.. reports], ReportPilots = [.. named], LastSeen = [.. seen], Kills = [.. kills ?? []],
                Gates = Gates, Groups = Groups(), Standings = standings ?? Standings(),
                Own = [new OwnPosition(A, "Pilot Alpha", S1, Ago(30), OwnPositionSource.Esi, CorpA, AllyA),
                       new OwnPosition(B, "Pilot Bravo", S9, Ago(30), OwnPositionSource.ChatLog, CorpB, 0)],
            }, new IntelCampRegister(), 1);

        public static void SnapshotChecks(Checks c)
        {
            // A clear and a pasted Local take out who they do not list; a sighting after them stands.
            var clear  = Report(S1, Ago(3), kind: IntelReportKind.Clear);
            var paste  = Report(S8, Ago(2), count: 1, source: IntelReportSource.LocalPaste);
            var dscan  = Report(S8, Ago(1), count: 5, source: IntelReportSource.DscanPaste);
            var after  = Report(S1, Ago(2), count: 1);
            var before = Report(S1, Ago(5), count: 1);
            var snap = Build([clear, paste, dscan, after, before], [Named(after, P10), Named(before, P11), Named(paste, P12)],
            [
                Seen(P10, S1, Ago(2), reportId: after.Id), Seen(P11, S1, Ago(5), reportId: before.Id),
                Seen(P12, S8, Ago(2), IntelLastSeenSource.LocalPaste, paste.Id), Seen(P13, S8, Ago(4)),
                Seen(P9, S4, Ago(6), IntelLastSeenSource.KillVictim),
            ]);
            c.Check("snapshot: a clear takes out a pilot seen before it", !snap.Pilots.ContainsKey(P11));
            c.Check("snapshot: a pilot seen after the clear is there", snap.Pilots.ContainsKey(P10));
            c.Check("snapshot: a pasted Local takes out who it does not list, and keeps who it does",
                !snap.Pilots.ContainsKey(P13) && snap.Pilots.ContainsKey(P12));
            c.Check("snapshot: a d-scan counts the ships less the pilots placed there (5 - 1)",
                snap.Systems[S8].Unnamed is { Count: 4, Source: IntelReportSource.DscanPaste }, snap.Systems[S8].Unnamed?.ToString() ?? "none");
            c.Check("snapshot: a loss lasts the victim lifetime (5 min)", snap.Pilots[P9].Lifetime == TimeSpan.FromMinutes(5));
            c.Check("snapshot: the clear is kept as the system's clear state", snap.Systems[S1].Clear?.ReportId == clear.Id);

            // A clear relayed into another channel a minute later: read once, at its first time. A pilot called between
            // the clear and its relay stands, and the Feed shows the clear once.
            var first   = Report(S2, Ago(4), kind: IntelReportKind.Clear, channel: "intelcheck-a");
            var between = Report(S2, Ago(3.5), count: 1, channel: "intelcheck-a");
            var relay   = Report(S2, Ago(3), kind: IntelReportKind.Clear, channel: "intelcheck-b");
            var apart   = Report(S2, Ago(3), kind: IntelReportKind.Clear, channel: "intelcheck-a");
            snap = Build([first, between, relay], [Named(between, P3)], [Seen(P3, S2, Ago(3.5), reportId: between.Id)]);
            c.Check("snapshot: a pilot called between a clear and its relay stands", snap.Pilots.ContainsKey(P3));
            c.Check("snapshot: the relayed clear is read once, the first",
                snap.Systems[S2].Clear?.ReportId == first.Id && snap.Feed.Count(e => e.ReportId == relay.Id) == 0,
                $"clear {snap.Systems[S2].Clear?.ReportId}, first {first.Id}");
            snap = Build([first, between, apart], [Named(between, P3)], [Seen(P3, S2, Ago(3.5), reportId: between.Id)]);
            c.Check("snapshot: a second clear in the same channel is no relay: it takes out the pilot seen before it",
                !snap.Pilots.ContainsKey(P3));

            // Unnamed counts: the newest report's, less its named pilots; gone when they are said to have moved on.
            var plus3a = Report(S4, Ago(10), count: 3);
            var plus3b = Report(S4, Ago(1), count: 4);
            var moved  = Report(S5, Ago(0.5), count: 2, from: S6);
            var stay   = Report(S6, Ago(1), count: 2);
            var cyno   = Report(S7, Ago(12), flags: (int)IntelRules.IntelFlags.Cyno);
            var campR  = Report(S7, Ago(20), flags: (int)IntelRules.IntelFlags.Camp);
            snap = Build([plus3a, plus3b, moved, stay, cyno, campR], [Named(plus3b, P2)], [Seen(P2, S4, Ago(1), reportId: plus3b.Id)]);
            c.Check("snapshot: the unnamed count is the newest report's less its named pilots (4 - 1)",
                snap.Systems[S4].Unnamed?.Count == 3, snap.Systems[S4].Unnamed?.ToString() ?? "none");
            c.Check("snapshot: a run of counts keeps the episode it started",
                snap.Systems[S4].Unnamed?.EpisodeStart == Ago(10), snap.Systems[S4].Unnamed?.EpisodeStart.ToString() ?? "none");
            c.Check("snapshot: a newer report saying they came from a system takes its count away",
                snap.Systems[S6].Unnamed is null && snap.Systems[S5].Unnamed?.Count == 2);
            c.Check("snapshot: and is a move on the map", snap.Systems[S5].Moves is [{ FromSystemId: S6, ToSystemId: S5 }]);
            var facts = snap.Systems[S7].Facts;
            c.Check("snapshot: a cyno lasts the cyno lifetime, a camp the camp lifetime",
                facts.Any(f => f.Flag == IntelRules.IntelFlags.Cyno && f.Lifetime == TimeSpan.FromMinutes(10))
                && facts.Any(f => f.Flag == IntelRules.IntelFlags.Camp && f.Lifetime == TimeSpan.FromMinutes(30)));
            var view = IntelLens.Project(snap, new IntelViewer(), Now, _ => null);
            var s7 = view.Systems.FirstOrDefault(r => r.SystemId == S7);
            c.Check("snapshot: a cyno 12 minutes old has ended, the camp 20 minutes old has not",
                s7 is { State: IntelSystemState.FlagsOnly } && s7.Facts.Select(f => f.Flag).SequenceEqual([IntelRules.IntelFlags.Camp]));

            // A kill confirms a report of the same pilot in the same system.
            var rep = Report(S3, Ago(3), count: 1);
            snap = Build([rep], [Named(rep, P5)], [Seen(P5, S3, Ago(1), IntelLastSeenSource.KillAttacker)],
                [Kill(31, Ago(1), P1, 5e9, [Attacker(P5)], system: S3)]);
            c.Check("snapshot: a kill and a report of one pilot in one system confirm each other", snap.Pilots[P5].ConfirmedByKill);
            c.Check("snapshot: a kill by players is in the feed", snap.Feed.Any(e => e.Kind == IntelEventKind.Kill && e.KillMailId == 31));

            // The feed is uncapped: a view filters, then caps.
            var many = Enumerable.Range(0, 700).Select(i => Report(S6, Ago(i % 50), count: 1)).ToList();
            snap = Build(many, [], []);
            c.Check("snapshot: the feed keeps every report in the window", snap.Feed.Count == 700, snap.Feed.Count.ToString());

            // Camps are found, keyed and flagged.
            snap = Build([], [], [], CampKills(Ago(40)));
            c.Check("snapshot: the camp is in the snapshot and its forming in the feed",
                snap.Camps.Count == 1 && snap.Feed.Any(e => e.Kind == IntelEventKind.CampFormed && e.CampKey == snap.Camps[0].Key));
            c.Check("snapshot: a gate kill says which gate", snap.Feed.First(e => e.KillMailId == 1).NearGate is { StargateId: G1, Km: 10 });
        }

        // ── The lens ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Hand-made walks: from S1 (A) and from S9 (B).</summary>
        private static Func<IntelOrigin, IReadOnlyDictionary<int, IntelHop>?> Hops(bool bReady = true) => o => o.SystemId switch
        {
            S1 => new Dictionary<int, IntelHop>
            {
                [S1] = new(0, false), [S2] = new(1, false), [S3] = new(2, false), [S4] = new(3, false),
                [S5] = new(6, false), [S6] = new(11, false), [S7] = new(4, true),
            },
            S9 when bReady => new Dictionary<int, IntelHop> { [S9] = new(0, false), [S5] = new(1, false), [S3] = new(4, false) },
            _ => null,
        };

        private static readonly IntelViewer Everyone = new() { Origins = [new(A, S1), new(B, S9)] };

        public static void LensChecks(Checks c)
        {
            var clear = Report(S1, Ago(3), kind: IntelReportKind.Clear);
            var r3    = Report(S3, Ago(1), count: 1);
            var camp3 = Report(S3, Ago(20), flags: (int)IntelRules.IntelFlags.Camp);
            var r4    = Report(S4, Ago(1), count: 3);
            var r5    = Report(S5, Ago(2), count: 1);
            var r2    = Report(S2, Ago(1), count: 1);
            var r6    = Report(S6, Ago(1), count: 1);
            var head  = Report(S3, Ago(4), count: 1);
            var follow = Report(S3, Ago(3), count: 2, parent: head.Id);
            var orphan = Report(S3, Ago(2.5), count: 1, parent: 1);   // its thread's first report is not in the window
            var reports = new List<IntelReport> { clear, r3, camp3, r4, r5, r2, r6, head, follow, orphan };
            var named = new List<IntelReportCharacter> { Named(r3, P7), Named(r5, P1), Named(r2, P4), Named(r6, P8), Named(r4, P2) };
            var seen = new List<IntelLastSeen>
            {
                Seen(P7, S3, Ago(1), reportId: r3.Id, corp: FarCorp),
                Seen(P1, S5, Ago(2), reportId: r5.Id, corp: FarCorp),
                Seen(P4, S2, Ago(1), reportId: r2.Id, corp: FriendCorp),
                Seen(P2, S4, Ago(12), reportId: r4.Id, corp: FarCorp),
                Seen(P8, S6, Ago(1), reportId: r6.Id, corp: FarCorp),
                Seen(P9, S4, Ago(20), corp: FarCorp),
            };
            // A busy kill feed eleven jumps away, newer than everything near.
            var farKills = Enumerable.Range(0, 600)
                .Select(i => Kill(10_000 + i, Ago(0.1), P13, 5e9, [Attacker(P8)], system: S6)).ToList();
            var snap = Build(reports, named, seen, farKills);

            var view = IntelLens.Project(snap, Everyone with { MaxJumps = 10 }, Now, Hops());
            var ids = view.Systems.Select(r => r.SystemId).ToList();
            c.Check("lens: systems by jumps from the nearest origin, then newest; blue-only S2 hidden, S6 beyond 10 jumps",
                ids.SequenceEqual([S1, S5, S3, S4]), string.Join(",", ids.Select(i => i % 100)));
            var s5 = view.Systems.First(r => r.SystemId == S5);
            c.Check("lens: S5 is 1 jump from B, not 6 from A", s5.Jumps == 1 && s5.NearestOrigin == B);
            c.Check("lens: and coloured by B's standings (P1 is red to A, neutral to B)",
                s5.Tier == StandingTier.Neutral && s5.StandingsFrom == B, $"{s5.Tier} from {s5.StandingsFrom}");
            var s1 = view.Systems.First(r => r.SystemId == S1);
            c.Check("lens: a system called clear, with nothing since, is in the Clear state", s1.State == IntelSystemState.Clear && !s1.IsFresh);
            var s3 = view.Systems.First(r => r.SystemId == S3);
            c.Check("lens: a camp flag rides along with the pilots", s3.State == IntelSystemState.Hostile
                && s3.Facts.Any(f => f.Flag == IntelRules.IntelFlags.Camp));
            c.Check("lens: news under two minutes old within five jumps is fresh", s3.IsFresh);
            var s4 = view.Systems.First(r => r.SystemId == S4);
            c.Check("lens: the worst tier of the named pilots (orange), an unnamed count beside it",
                s4.Tier == StandingTier.Orange && s4.Unnamed == 2 && s4.Pilots.Count == 1, $"{s4.Tier} {s4.Unnamed} {s4.Pilots.Count}");
            c.Check("lens: an expired pilot is not shown", s4.Pilots.All(p => p.Pilot.CharacterId != P9));
            c.Check("lens: the nearest live threat is the panel's line (S5, 1 jump; the clear S1 is no threat)", view.Nearest?.SystemId == S5);
            c.Check("lens: rows meanwhile fade with their freshest news", s4.Opacity == 1.0 && Math.Abs(s3.Freshness - 14.0 / 15) < 1e-9,
                $"{s4.Opacity} {s3.Freshness}");

            // Filter, then cap.
            var panel = IntelLens.Project(snap, Everyone with { MaxJumps = 10, FeedCap = 50, SystemCap = 2 }, Now, Hops());
            c.Check("lens: the system cap is applied after the filters", panel.Systems.Select(r => r.SystemId).SequenceEqual([S1, S5]));
            c.Check("lens: 600 newer kills eleven jumps away do not push out a report two jumps away",
                panel.Feed.Any(r => r.Event.ReportId == r3.Id) && panel.Feed.All(r => r.Event.SystemId != S6),
                $"{panel.Feed.Count} rows");
            var capFirst = snap.Feed.Take(50).Any(e => e.ReportId == r3.Id);
            c.Check("lens: (mutation) capping the feed first would have lost it", !capFirst);

            // The feed: follow-ups under their thread's first report; one whose first is gone stands alone.
            var feed = IntelLens.Project(snap, Everyone with { IncludeKills = false }, Now, Hops()).Feed;
            var headRow = feed.FirstOrDefault(r => r.Event.ReportId == head.Id);
            c.Check("lens: a follow-up nests under its thread's first report",
                headRow is { FollowUps: [{ Event.ReportId: var fid }] } && fid == follow.Id && feed.All(r => r.Event.ReportId != follow.Id));
            c.Check("lens: a follow-up whose first report is gone is a line of its own",
                feed.Any(r => r.Event.ReportId == orphan.Id && r.Event.Kind == IntelEventKind.FollowUp));
            c.Check("lens: the feed is newest first", feed.Zip(feed.Skip(1)).All(p => p.First.Event.At >= p.Second.Event.At));
            c.Check("lens: kills left out when asked", feed.All(r => r.Event.Kind != IntelEventKind.Kill));
            var sources = IntelLens.Project(snap, Everyone with { Sources = new HashSet<string> { IntelSnapshot.SourceKills } }, Now, Hops()).Feed;
            c.Check("lens: the sources filter keeps only what it names", sources.Count > 0 && sources.All(r => r.Event.SourceKey == IntelSnapshot.SourceKills));

            // A distance not walked yet: the rows still show, with no jumps, and the caller is told.
            var pending = IntelLens.Project(snap, Everyone with { MaxJumps = 10 }, Now, Hops(bReady: false));
            c.Check("lens: an origin not walked yet is reported, and its rows are not dropped",
                pending.MissingOrigins.SequenceEqual([new IntelOrigin(B, S9)]) && pending.Systems.Any(r => r.SystemId == S5 && r.Jumps == 6));
            var nobody = IntelLens.Project(snap, new IntelViewer { MaxJumps = 3 }, Now, _ => null);
            c.Check("lens: with nobody online there is no distance to filter by", nobody.Systems.Any(r => r.SystemId == S6 && r.Jumps is null));
            c.Check("lens: and colours come from the friendliest own character (P1 neutral, not red)",
                nobody.Systems.First(r => r.SystemId == S5).Tier == StandingTier.Neutral);
            var withBlues = IntelLens.Project(snap, Everyone with { Tiers = Enum.GetValues<StandingTier>().ToHashSet() }, Now, Hops());
            c.Check("lens: blues show when the tiers ask for them", withBlues.Systems.Any(r => r.SystemId == S2 && r.Tier == StandingTier.Blue));
            var expired = IntelLens.Project(snap, Everyone with { IncludeExpired = true, IncludeKills = false }, Now.AddMinutes(30), Hops()).Feed;
            c.Check("lens: the tool's Feed keeps expired lines at 30%",
                expired.Count > 0 && expired.All(r => r.Expired && r.Opacity == IntelLens.ExpiredOpacity));
            c.Check("lens: the panel does not", IntelLens.Project(snap, Everyone, Now.AddMinutes(30), Hops()).Feed.Count == 0);

            // The fade itself.
            var life = TimeSpan.FromMinutes(15);
            c.Check("fade: full for the first third", IntelLens.Opacity(Now, life, Now) == 1 && IntelLens.Opacity(Ago(5), life, Now) == 1);
            c.Check("fade: down to 40% at the end", Math.Abs(IntelLens.Opacity(Ago(14.999), life, Now) - 0.4) < 0.001
                && Math.Abs(IntelLens.Opacity(Ago(10), life, Now) - 0.7) < 1e-9, IntelLens.Opacity(Ago(10), life, Now).ToString());
            c.Check("fade: 30% once expired", IntelLens.Opacity(Ago(15), life, Now) == 0.3 && IntelLens.Opacity(Ago(40), life, Now) == 0.3);
            c.Check("fade: freshness falls from 1 to 0", IntelLens.Freshness(Now, life, Now) == 1
                && Math.Abs(IntelLens.Freshness(Ago(7.5), life, Now) - 0.5) < 1e-9 && IntelLens.Freshness(Ago(20), life, Now) == 0);

            // The map's adapter shows the same picture, with no viewer.
            var map = LiveIntelService.ToMap(snap, Now);
            c.Check("map: the adapter places the hostiles and counts", map.Hostiles.TryGetValue(S4, out var h4) && h4.Count == 3
                && h4.Pilots.Single().CharacterId == P2 && !map.Hostiles.ContainsKey(S2));
            c.Check("map: and the own characters", map.Own.TryGetValue(S1, out var o1) && o1.Single().CharacterId == A
                && map.Own.ContainsKey(S9));
            c.Check("map: a system only called clear is not a mark", !map.Hostiles.ContainsKey(S1));

            // A camp is judged by its attackers: our own corporation killing two hostiles on a gate is
            // no camp to warn of, on a panel, the tool or the map; a hostile gang's is.
            List<IntelKill> GateKills(long corp) =>
            [
                Kill(41, Ago(5), P1, 10_000, [Attacker(P5, corp: corp), Attacker(P6, corp: corp)]),
                Kill(42, Ago(3), P2, 10_000, [Attacker(P6, corp: corp)]),
            ];
            var blueSnap = Build([], [], [], GateKills(CorpA));
            var blueView = IntelLens.Project(blueSnap, Everyone, Now, Hops());
            c.Check("camps: a camp whose attackers are all blue is found, but gives no row",
                blueSnap.Camps.Count == 1 && blueView.Systems.All(r => r.SystemId != S1),
                $"{blueSnap.Camps.Count} camps; rows {string.Join(",", blueView.Systems.Select(r => r.SystemId % 100))}");
            c.Check("camps: nor a line in the feed", blueView.Feed.All(r => r.Event.Kind != IntelEventKind.CampFormed));
            c.Check("camps: nor a mark on the map", !LiveIntelService.ToMap(blueSnap, Now).Hostiles.ContainsKey(S1));
            var blueShown = IntelLens.Project(blueSnap, Everyone with { Tiers = Enum.GetValues<StandingTier>().ToHashSet() }, Now, Hops());
            c.Check("camps: shown, blue, when the tiers ask for blues",
                blueShown.Systems.FirstOrDefault(r => r.SystemId == S1) is { Camps.Count: 1, Tier: StandingTier.Blue });

            var redSnap = Build([], [], [], GateKills(FarCorp));
            var redRow = IntelLens.Project(redSnap, Everyone, Now, Hops()).Systems.FirstOrDefault(r => r.SystemId == S1);
            c.Check("camps: a hostile gang's camp is a row at the worst tier of its attackers (P6 red to A)",
                redRow is { Camps.Count: 1, State: IntelSystemState.FlagsOnly, Tier: StandingTier.Red },
                redRow is null ? "no row" : $"{redRow.Camps.Count} {redRow.State} {redRow.Tier}");
            c.Check("camps: and a camp mark on the map",
                LiveIntelService.ToMap(redSnap, Now).Hostiles.ContainsKey(S1));
        }

        // ── The service, on a database ──────────────────────────────────────────────────────────────────────

        public static async Task ServiceAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            var now = DateTimeOffset.UtcNow;
            await using (var db = new AppDbContext(options))
            {
                await db.IntelReportCharacters.ExecuteDeleteAsync();
                await db.IntelReports.ExecuteDeleteAsync();
                await db.IntelLastSeens.ExecuteDeleteAsync();
                await db.KillMailAttackers.ExecuteDeleteAsync();
                await db.KillMailDetails.ExecuteDeleteAsync();
                await db.CharacterStatuses.ExecuteDeleteAsync();
                await db.Characters.ExecuteDeleteAsync();
                await db.ChatMessages.ExecuteDeleteAsync();
                await db.AppPreferences.Where(p => p.Key.StartsWith("intel.") || p.Key.StartsWith("zkb.")).ExecuteDeleteAsync();
                await db.SdeCelestials.ExecuteDeleteAsync();
                await db.SdeStargates.ExecuteDeleteAsync();
                await db.SdeSolarSystems.ExecuteDeleteAsync();
                await db.SdeTypes.Where(t => t.TypeId == SaidHull || t.TypeId == UnpublishedHull).ExecuteDeleteAsync();
                await db.SdeGroups.Where(g => g.GroupId == SaidHullGroup).ExecuteDeleteAsync();

                // A published hull a report may name with no type id beside it, and one never published.
                db.SdeGroups.Add(new SdeGroup { GroupId = SaidHullGroup, CategoryId = 6, Name = "Invented Hull Class", Published = true });
                db.SdeTypes.Add(new SdeType { TypeId = SaidHull, GroupId = SaidHullGroup, Name = "Hull Zeta", Published = true });
                db.SdeTypes.Add(new SdeType { TypeId = UnpublishedHull, GroupId = SaidHullGroup, Name = "Hull Unseen", Published = false });

                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = S1, Name = "QZ-X77", RegionId = 1, ConstellationId = 1 });
                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = S2, Name = "XQ1-Z2", RegionId = 1, ConstellationId = 1 });
                db.SdeStargates.Add(new SdeStargate { StargateId = (int)G1, SolarSystemId = S1, DestinationStargateId = (int)G2 });
                db.SdeStargates.Add(new SdeStargate { StargateId = (int)G2, SolarSystemId = S2, DestinationStargateId = (int)G1 });
                db.SdeCelestials.Add(new SdeCelestial { ItemId = G1, SolarSystemId = S1, Kind = 2, Name = "Stargate (XQ1-Z2)" });
                db.SdeCelestials.Add(new SdeCelestial { ItemId = G2, SolarSystemId = S2, Kind = 2, Name = "Stargate (QZ-X77)" });
                db.Characters.Add(new Character { Id = A, Name = "Pilot Alpha", CorporationId = (int)CorpA });
                db.CharacterStatuses.Add(new CharacterStatus
                {
                    CharacterId = A, Online = true, SolarSystemId = S1,
                    LocationCheckedAt = now.AddSeconds(-60), OnlineCheckedAt = now.AddSeconds(-60),
                });
                // The chat log saw the character jump after ESI last looked.
                db.ChatMessages.Add(new ChatMessage
                {
                    OccurredAt = Iso(now.AddSeconds(-10)), ChannelName = "Local", ListenerCharacterId = A, SenderName = "EVE System",
                    Message = "Channel changed to Local : XQ1-Z2", IsSystemMessage = true, SystemName = "XQ1-Z2",
                    SourceFile = "intelcheck-local.txt", LineNumber = 1,
                });
                var rep = new IntelReport
                {
                    ReportedAt = Iso(now.AddMinutes(-1)), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = S1,
                    SystemName = "QZ-X77", PlayerCount = 2, Message = "QZ-X77 Pilot 1 +1",
                };
                db.IntelReports.Add(rep);
                db.IntelReports.Add(new IntelReport
                {
                    ReportedAt = Iso(now.AddMinutes(-2)), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = S2,
                    SystemName = "XQ1-Z2", Kind = IntelReportKind.Clear, Message = "XQ1-Z2 clr",
                });
                await db.SaveChangesAsync();
                db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = rep.Id, CharacterId = P1, CharacterName = "Pilot 1" });
                db.KillMailDetails.Add(new KillMailDetail
                {
                    KillMailId = 77_001, KillMailHash = "x", KillMailTime = now.AddMinutes(-5), SolarSystemId = S1,
                    VictimCharId = P3, VictimCorpId = FarCorp, VictimShipTypeId = Hull, VictimPosX = 20_000, VictimPosY = 0, VictimPosZ = 0,
                });
                db.KillMailAttackers.Add(new KillMailAttacker { KillMailId = 77_001, CharacterId = P4, CorporationId = FarCorp, ShipTypeId = Hull, FinalBlow = true });
                await db.SaveChangesAsync();
                await IntelLastSeenWriter.UpsertAsync(db, [new LastSeenRow
                {
                    CharacterId = P1, SeenAt = Iso(now.AddMinutes(-1)), Source = IntelLastSeenSource.Report, SystemId = S1,
                    ReportId = rep.Id, Name = "Pilot 1",
                }]);
            }

            var factory = new Factory(options);
            var prefs = Prefs.Over(options);
            var service = new IntelSnapshotService(factory, new IntelStandingService(factory), prefs: prefs);
            var snap = await service.RefreshNowAsync();
            c.Check($"{engine} service: a pilot and the unnamed beside them", snap.Pilots.ContainsKey(P1)
                && snap.Systems.TryGetValue(S1, out var s1) && s1.Unnamed?.Count == 1, string.Join(",", snap.Pilots.Keys));
            c.Check($"{engine} service: the clear is read", snap.Systems.TryGetValue(S2, out var s2) && s2.Clear is not null);
            c.Check($"{engine} service: a kill is read, and its gate", snap.Feed.FirstOrDefault(e => e.KillMailId == 77_001) is { NearGate.StargateId: G1 });
            c.Check($"{engine} service: the own character is where the chat log says, newer than ESI",
                snap.Own is [{ CharacterId: A, SystemId: S2, Source: OwnPositionSource.ChatLog }],
                string.Join(",", snap.Own.Select(o => $"{o.CharacterId}@{o.SystemId} {o.Source}")));
            c.Check($"{engine} service: a published hull is found by its English name, any case, for its icon",
                snap.HullId("Hull Zeta") == SaidHull && snap.HullId("hull zeta") == SaidHull, snap.HullId("Hull Zeta").ToString());
            c.Check($"{engine} service: an unpublished type or a class name is no hull",
                snap.HullId("Hull Unseen") == 0 && snap.HullId("Invented Hull Class") == 0);

            await ChatterAsync(c, engine, options, now);

            // Written since: a new sighting, and a late one with an old time.
            await using (var db = new AppDbContext(options))
                await IntelLastSeenWriter.UpsertAsync(db,
                [
                    new LastSeenRow { CharacterId = P2, SeenAt = Iso(now), Source = IntelLastSeenSource.Report, SystemId = S1, Name = "Pilot 2" },
                    new LastSeenRow { CharacterId = P5, SeenAt = Iso(now.AddMinutes(-40)), Source = IntelLastSeenSource.KillAttacker, SystemId = S1 },
                ]);
            snap = await service.RefreshNowAsync();
            c.Check($"{engine} service: rows written since the last read are read", snap.Pilots.ContainsKey(P2));
            c.Check($"{engine} service: a late row with an old time still is", snap.Pilots.ContainsKey(P5));

            // Settings are read fresh: a lifetime changed on another PC, and the warning's dismissal cleared once
            // the full feed is seen on.
            await prefs.SetLongAsync(IntelSettings.KeyNamed, 30);
            await prefs.SetAsync(ZkillboardSettings.KeyScope, "All");
            await prefs.SetBoolAsync(IntelSettings.KeyFeedWarning, true);
            service.SettingsChanged();
            snap = await service.RefreshNowAsync();
            c.Check($"{engine} service: a lifetime set elsewhere is used", snap.Pilots[P1].Lifetime == TimeSpan.FromMinutes(30));
            c.Check($"{engine} service: the full feed on clears the warning's dismissal",
                snap.FeedStatus is { FullFeed: true, Dismissed: false } && await prefs.ReadFreshAsync(IntelSettings.KeyFeedWarning) == "0");
            service.Dispose();
        }

        private const int SaidHull = 70_901, UnpublishedHull = 70_902, SaidHullGroup = 70_900;

        /// <summary>Unlinked chatter: the lines no report came from, a paste's 0 never matching a line, and on
        /// SQLite, the plan: each line's reports are found by the partial index, never by a scan of them all.</summary>
        private static async Task ChatterAsync(Checks c, string engine, DbContextOptions<AppDbContext> options, DateTimeOffset now)
        {
            await using var db = new AppDbContext(options);
            ChatMessage Line(string text, int n) => new()
            {
                OccurredAt = Iso(now.AddMinutes(-3)), ChannelName = "intelcheck chatter", SenderName = "Scout Alpha", Message = text,
                SourceFile = "intelcheck-chatter.txt", LineNumber = n,
            };
            var talk = Line("anyone awake?", 1);
            var intel = Line("QZ-X77 Pilot 1", 2);
            db.ChatMessages.AddRange(talk, intel);
            await db.SaveChangesAsync();
            db.IntelReports.Add(new IntelReport
            {
                ReportedAt = Iso(now.AddMinutes(-3)), ChannelName = "intelcheck chatter", ReporterName = "Scout Alpha", SystemId = S1,
                SystemName = "QZ-X77", PlayerCount = 1, Message = intel.Message, ChatMessageId = intel.Id,
            });
            await db.SaveChangesAsync();

            var query = IntelSnapshotService.ChatterQuery(db, ["intelcheck chatter"], int.MaxValue, Iso(now.AddHours(-1)));
            var ids = await query.Select(m => m.Id).ToListAsync();
            c.Check($"{engine} service: unlinked chatter is the line no report came from, and only that",
                ids.SequenceEqual([talk.Id]), string.Join(",", ids));

            if (engine != "SQLite") return;
            // PostgreSQL's planner chooses by table size, so on a scratch table its plan proves nothing; SQLite's
            // choice of index is by shape alone, the same here as on a year of intel.
            await db.Database.OpenConnectionAsync();
            await using var cmd = query.CreateDbCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + cmd.CommandText;
            var plan = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) plan.Add(r.GetString(3));
            c.Check("SQLite service: unlinked chatter finds a line's reports by the partial index, never scanning them all",
                plan.Any(p => p.Contains("IX_IntelReports_ChatMessage", StringComparison.Ordinal)), string.Join(" | ", plan));
        }

        // ── Retention ──────────────────────────────────────────────────────────────────────────────────────

        public static async Task RetentionAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            var now = DateTimeOffset.UtcNow;
            await using (var db = new AppDbContext(options))
            {
                await db.IntelReportCharacters.ExecuteDeleteAsync();
                await db.IntelReports.ExecuteDeleteAsync();
                await db.IntelLastSeens.ExecuteDeleteAsync();
                var old = new IntelReport { ReportedAt = Iso(now.AddDays(-100)), ChannelName = "intelcheck", ReporterName = "Scout", SystemId = S1, SystemName = "QZ-X77", Message = "old" };
                var recent = new IntelReport { ReportedAt = Iso(now.AddDays(-1)), ChannelName = "intelcheck", ReporterName = "Scout", SystemId = S1, SystemName = "QZ-X77", Message = "new" };
                db.IntelReports.AddRange(old, recent);
                await db.SaveChangesAsync();
                db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = old.Id, CharacterId = P1, CharacterName = "Pilot 1" });
                db.IntelReportCharacters.Add(new IntelReportCharacter { IntelReportId = recent.Id, CharacterId = P2, CharacterName = "Pilot 2" });
                await db.SaveChangesAsync();
                await IntelLastSeenWriter.UpsertAsync(db,
                [
                    new LastSeenRow { CharacterId = P1, SeenAt = Iso(now.AddDays(-100)), Source = IntelLastSeenSource.Report, SystemId = S1 },
                    new LastSeenRow { CharacterId = P2, SeenAt = Iso(now.AddDays(-1)), Source = IntelLastSeenSource.Report, SystemId = S1 },
                ]);
            }

            var retention = new DataRetentionService(new Factory(options), Prefs.Over(options));
            c.Check($"{engine} retention: the intel rule is off by default, 90 days, at least 7",
                !retention.Intel.Enabled && retention.Intel.DefaultDays == 90 && retention.Intel.MinimumDays == 7);
            var removed = await retention.PurgeIntelAsync(90);
            await using (var db = new AppDbContext(options))
            {
                c.Check($"{engine} retention: the old report, its pilot and the old last-seen row go", removed == 3, removed.ToString());
                c.Check($"{engine} retention: the newer ones stay",
                    await db.IntelReports.CountAsync() == 1 && await db.IntelReportCharacters.CountAsync() == 1
                    && await db.IntelLastSeens.CountAsync() == 1);
            }
            c.Check($"{engine} retention: never shorter than the floor", await retention.PurgeIntelAsync(1) == 0);
        }
    }
}
