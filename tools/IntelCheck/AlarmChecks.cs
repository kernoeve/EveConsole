// The intel alarm on intel as it stands: what it matches in a snapshot (alive, not cleared, not seen since elsewhere,
// hostile to the character nearest), the keys it gives each thing (a pilot's stay, a count's band, a flag's episode, a
// kill), flag-only reports, kills said as kills with their attackers not said again, the order things are said in, the
// watched systems over threat distances and the snapshot's own positions, an evaluation that reads the snapshot before
// it matches, and the one-time re-prime of every intel alarm. Every name and id here is invented.
using System.Globalization;
using System.Text.Json;
using EveConsole.Alarms;
using EveConsole.Alarms.Conditions;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck
{
    internal static partial class AlarmChecks
    {
        // Systems: S1 where Alpha is, S2 one jump, S3 two, S4 three, S5 six; S6 outside the alarm's reach; S7 far off.
        private const int S1 = 30_000_101, S2 = 30_000_102, S3 = 30_000_103, S4 = 30_000_104, S5 = 30_000_105, S6 = 30_000_106,
                          S7 = 30_000_107;

        private const long A = 91_100_001, CorpA = 98_200_001;
        private const long FriendCorp = 98_200_009, FarCorp = 98_200_020;

        private const long P1 = 92_100_001, P2 = 92_100_002, P3 = 92_100_003, P4 = 92_100_004, P5 = 92_100_005,
                           P6 = 92_100_006, P7 = 92_100_007, P8 = 92_100_008, P9 = 92_100_009, P10 = 92_100_010,
                           P11 = 92_100_011;

        private const int HullA = 71_001, HullB = 71_002, HullC = 71_003;
        private const int K1 = 81_001, K2 = 81_002, K3 = 81_003, K4 = 81_004;

        private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        private static DateTimeOffset Ago(double minutes) => Now.AddMinutes(-minutes);
        private static string Iso(DateTimeOffset at) => IntelLastSeenWriter.Format(at);

        private static readonly Dictionary<int, string> Names = new()
        {
            [S1] = "QZ-X77", [S2] = "XQ1-Z2", [S3] = "ZZQ-71", [S4] = "X2-QZ7", [S5] = "K7-QZP", [S6] = "Q2X-91", [S7] = "Z9-QQ1",
        };

        private static IntelStandings Standings() => new(
            [new IntelStandings.OwnCharacter(A, "Pilot Alpha", CorpA, 0)], [CorpA], [(A, FriendCorp, 10)]);

        /// <summary>What the alarm around Alpha watches: S1 to S5, by the jumps above.</summary>
        private static IntelWatchedSystems Zone() =>
            new(new HashSet<int> { S1, S2, S3, S4, S5 },
                new Dictionary<int, int> { [S1] = 0, [S2] = 1, [S3] = 2, [S4] = 3, [S5] = 6 },
                new Dictionary<int, string> { [S1] = "Pilot Alpha", [S2] = "Pilot Alpha", [S3] = "Pilot Alpha", [S4] = "Pilot Alpha", [S5] = "Pilot Alpha" },
                new Dictionary<int, double>(), new Dictionary<int, string>())
            {
                NearestCharacter = new Dictionary<int, long> { [S1] = A, [S2] = A, [S3] = A, [S4] = A, [S5] = A },
            };

        private static readonly IntelCondition.IntelAlarmOptions Defaults = new(Jumps: 10);

        private static int _reportId = 5_000;

        private static IntelReport Report(int system, DateTimeOffset at, int count = 0, int flags = 0,
                                          int kind = IntelReportKind.Sighting, bool noVisual = false) => new()
        {
            Id = ++_reportId, ReportedAt = Iso(at), ChannelName = "intelcheck", ReporterName = "Scout Alpha", SystemId = system,
            SystemName = Names[system], PlayerCount = count, Flags = flags, Kind = kind, NoVisual = noVisual, Message = "intelcheck",
        };

        private static IntelLastSeen Seen(long pilot, int system, DateTimeOffset at, DateTimeOffset? since = null,
                                          int source = IntelLastSeenSource.Report, int? killId = null, long corp = FarCorp,
                                          bool noVisual = false, int? ship = null) => new()
        {
            CharacterId = pilot, Name = $"Pilot {pilot % 1000}", SystemId = system, SeenAt = Iso(at), Source = source,
            SourceRank = IntelLastSeenSource.RankOf(source), KillMailId = killId, CorporationId = corp, NoVisual = noVisual,
            ShipTypeId = ship, InSystemSince = Iso(since ?? at), WrittenAt = Iso(at),
        };

        private static IntelKill Kill(int id, int system, DateTimeOffset at, long victim, params (long Pilot, int Hull, long Corp)[] gang) =>
            new(id, at, system, victim, FarCorp, 0, HullC, null, null, null, null,
                [.. gang.Select(g => new IntelKillAttacker(g.Pilot, g.Corp, 0, g.Hull, null, null, null))]);

        private static IntelSnapshot Snap(IEnumerable<IntelReport>? reports = null, IEnumerable<IntelLastSeen>? seen = null,
                                          IEnumerable<IntelKill>? kills = null, IEnumerable<IntelReportCharacter>? named = null,
                                          DateTimeOffset? at = null, IntelEpisodeRegister? episodes = null) =>
            IntelSnapshotBuilder.Build(new IntelSnapshotInput
            {
                Now = at ?? Now, Settings = new IntelSettingsValues(),
                Reports = [.. reports ?? []], ReportPilots = [.. named ?? []], LastSeen = [.. seen ?? []], Kills = [.. kills ?? []],
                Standings = Standings(), SystemNames = Names,
                TypeNames = new Dictionary<int, string> { [HullA] = "Hull Alpha", [HullB] = "Hull Beta", [HullC] = "Hull Gamma" },
                Names = Enumerable.Range(1, 11).ToDictionary(i => 92_100_000L + i, i => $"Pilot {i}"),
                Own = [new OwnPosition(A, "Pilot Alpha", S1, Ago(30), OwnPositionSource.Esi, CorpA, 0)],
            }, new IntelCampRegister(), 1, episodes);

        private static IReadOnlyList<AlarmMatch> Match(IntelSnapshot snap, IntelCondition.IntelAlarmOptions? options = null) =>
            IntelCondition.Match(snap, Zone(), options ?? Defaults, snap.At);

        private static string Keys(IEnumerable<AlarmMatch> matches) => string.Join(" ", matches.Select(m => m.Key));
        private static string Kind(AlarmMatch m) => m.Detail?["kind"] as string ?? "";

        public static void Run(Checks c)
        {
            var ui = CultureInfo.CurrentUICulture;
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("en-US");
            try
            {
                CurrentState(c);
                PilotStays(c);
                Flags(c);
                Bands(c);
                LongEpisodes(c);
                Kills(c);
                Filters(c);
                Config(c);
                Waking(c);
                Memory(c);
                MoveIntoRange(c);
                Gate(c);
                WhoAndWhere(c);
            }
            finally
            {
                CultureInfo.CurrentUICulture = ui;
                CultureInfo.CurrentCulture = culture;
            }
        }

        // ── Only what stands now ─────────────────────────────────────────────────────────────────────────────

        private static void CurrentState(Checks c)
        {
            var clear = Report(S3, Ago(3), kind: IntelReportKind.Clear);
            var snap = Snap([clear],
            [
                Seen(P1, S6, Ago(1), since: Ago(1)),   // reported in S2 before, seen since outside the reach: superseded
                Seen(P2, S2, Ago(20)),                 // past the named lifetime (15 min): expired
                Seen(P3, S3, Ago(5)),                  // called clear after
                Seen(P4, S2, Ago(1), since: Ago(4)),   // there now
            ]);
            var matches = Match(snap);
            c.Check("alarm: a pilot seen since outside the reach is not matched", matches.All(m => !m.Key.Contains($":{P1}|")), Keys(matches));
            c.Check("alarm: a pilot past the named lifetime is not matched", matches.All(m => !m.Key.Contains($":{P2}|")), Keys(matches));
            c.Check("alarm: a pilot in a system called clear since is not matched", matches.All(m => !m.Key.Contains($":{P3}|")), Keys(matches));
            c.Check("alarm: a pilot there now is matched, keyed by the stay",
                matches.Count == 1 && matches[0].Key == $"intel:p:{P4}|{S2}|20261112T115600", Keys(matches));

            // A clear the snapshot no longer counts (past its own lifetime) still took out who it cleared.
            var later = Match(Snap([Report(S3, Ago(14), kind: IntelReportKind.Clear)], [Seen(P3, S3, Ago(14.5))]));
            c.Check("alarm: a pilot seen before a clear stays gone after the clear itself ends", later.Count == 0, Keys(later));
        }

        // ── A pilot once per stay ────────────────────────────────────────────────────────────────────────────

        private static void PilotStays(Checks c)
        {
            string KeyOf(IntelLastSeen row) => Match(Snap(seen: [row])).Single(m => Kind(m) == IntelCondition.KindPilot).Key;
            var first   = KeyOf(Seen(P4, S2, Ago(6), since: Ago(6)));
            var again   = KeyOf(Seen(P4, S2, Ago(1), since: Ago(6)));
            var back    = KeyOf(Seen(P4, S2, Ago(1), since: Ago(1)));
            var elsewhere = KeyOf(Seen(P4, S3, Ago(1), since: Ago(1)));
            c.Check("alarm: the same pilot called again in the same stay is the same key", first == again, $"{first} / {again}");
            c.Check("alarm: a new stay in the same system is a new key", back != first, back);
            c.Check("alarm: the pilot in another system is a new key", elsewhere != first && elsewhere != back, elsewhere);
        }

        // ── Flag-only reports ────────────────────────────────────────────────────────────────────────────────

        private static void Flags(Checks c)
        {
            var bubbles  = Report(S4, Ago(4), flags: (int)IntelRules.IntelFlags.Bubbles);
            var again    = Report(S4, Ago(1), flags: (int)IntelRules.IntelFlags.Bubbles);
            var hole     = Report(S3, Ago(1), flags: (int)IntelRules.IntelFlags.Wormhole);
            var once     = Match(Snap([bubbles, hole]));
            var twice    = Match(Snap([bubbles, again, hole]));
            var flag     = once.FirstOrDefault(m => Kind(m) == IntelCondition.KindFlag);
            c.Check("alarm: a report of bubbles with nobody counted fires",
                once.Count == 1 && flag is not null && flag.Key == $"intel:f:{S4}|Bubbles|20261112T115600", Keys(once));
            c.Check("alarm: the same bubbles called again in their episode are the same key",
                twice.Count == 1 && twice[0].Key == flag?.Key, Keys(twice));
            c.Check("alarm: a wormhole said alone does not fire", once.All(m => !m.Key.Contains($":{S3}|")), Keys(once));
            var off = Match(Snap([bubbles]), Defaults with { FlagReports = false });
            c.Check("alarm: flag-only reports switched off: nothing", off.Count == 0, Keys(off));

            // Every flag that changes how to go in fires; said in the system's own sentence.
            var all = Report(S2, Ago(2), flags: (int)(IntelRules.IntelFlags.Camp | IntelRules.IntelFlags.Cyno | IntelRules.IntelFlags.Hotdrop
                                                      | IntelRules.IntelFlags.Spike | IntelRules.IntelFlags.Probes));
            var five = Match(Snap([all]));
            c.Check("alarm: a camp, a cyno, a hot drop, a spike and combat probes each fire once",
                five.Count == 5 && five.Select(m => m.Key).Distinct().Count() == 5, Keys(five));

            var said = IntelCondition.ComposeAnnouncement(once) ?? "";
            c.Check("alarm: a flag-only report is said as where and what, with no count",
                said.StartsWith("In X2-QZ7, 3 jumps from Pilot Alpha: ", StringComparison.Ordinal)
                && said.Contains("bubbles", StringComparison.OrdinalIgnoreCase) && !said.Contains('0'), said);
        }

        // ── An unnamed count once per band ───────────────────────────────────────────────────────────────────

        private static void Bands(Checks c)
        {
            var r3 = Report(S5, Ago(6), count: 3);
            var r6 = Report(S5, Ago(4), count: 6);
            var r7 = Report(S5, Ago(2), count: 7);
            string KeyOf(params IntelReport[] reports) => Match(Snap(reports)).Single(m => Kind(m) == IntelCondition.KindCount).Key;
            var three = KeyOf(r3);
            var six   = KeyOf(r3, r6);
            var seven = KeyOf(r3, r6, r7);
            c.Check("alarm: a count rising from 3 to 6 is news", six != three, $"{three} / {six}");
            c.Check("alarm: from 6 to 7 it is not", seven == six, $"{six} / {seven}");
            c.Check("alarm: the count keeps the episode it began in", seven == $"intel:n:{S5}|20261112T115400|5", seven);
            c.Check("alarm: the bands are 1, 2-4, 5-9, 10-19, 20+",
                new[] { 1, 2, 4, 5, 9, 10, 19, 20, 400 }.Select(IntelCondition.Band).SequenceEqual([1, 2, 2, 5, 5, 10, 10, 20, 20]));
            var after = KeyOf(Report(S5, Ago(40), count: 3), Report(S5, Ago(1), count: 3));
            c.Check("alarm: a count after the last one ended is a new episode", after == $"intel:n:{S5}|20261112T115900|2", after);

            // A count that falls is no news: the band is the episode's highest.
            var fell = KeyOf(Report(S5, Ago(4), count: 6), Report(S5, Ago(2), count: 3));
            c.Check("alarm: an episode of 6 then 3 keeps the band-5 key", fell == $"intel:n:{S5}|20261112T115600|5", fell);
        }

        // ── An episode longer than the snapshot's window ─────────────────────────────────────────────────────

        private static void LongEpisodes(Checks c)
        {
            // From 10:45 every 15 min to 12:15: a count (6 at first, then 3) in S5, bubbles in S4. Read every few
            // seconds by the snapshot; the alarm evaluated at 11:44 (59 min in) and at 12:00 (75 min in, the first
            // report gone from the hour's window).
            var t0 = Ago(75);
            var reports = Enumerable.Range(0, 7).SelectMany(i => new[]
            {
                Report(S5, t0.AddMinutes(15 * i), count: i == 0 ? 6 : 3),
                Report(S4, t0.AddMinutes(15 * i), flags: (int)IntelRules.IntelFlags.Bubbles),
            }).ToList();
            IntelSnapshot At(double minutes, IntelEpisodeRegister register, IEnumerable<IntelReport>? extra = null)
            {
                var now = t0.AddMinutes(minutes);
                return Snap([.. reports.Where(r => IntelSnapshotBuilder.TryParse(r.ReportedAt, out var a) && a <= now), .. extra ?? []],
                            at: now, episodes: register);
            }
            string KeyOf(IntelSnapshot snap, string kind) => Match(snap).FirstOrDefault(m => Kind(m) == kind)?.Key ?? "none";

            var episodes = new IntelEpisodeRegister();
            for (var m = 0; m <= 59; m += 5) At(m, episodes);   // the readings before
            var count59 = KeyOf(At(59, episodes), IntelCondition.KindCount);
            var flag59  = KeyOf(At(59, episodes), IntelCondition.KindFlag);
            for (var m = 60; m <= 75; m += 5) At(m, episodes);
            var count75 = KeyOf(At(75, episodes), IntelCondition.KindCount);
            var flag75  = KeyOf(At(75, episodes), IntelCondition.KindFlag);
            c.Check("alarm: a count going on past the window's edge keeps its first start and its highest band",
                count59 == $"intel:n:{S5}|20261112T104500|5" && count75 == count59, $"{count59} / {count75}");
            c.Check("alarm: bubbles going on past the window's edge keep their first start",
                flag59 == $"intel:f:{S4}|Bubbles|20261112T104500" && flag75 == flag59, $"{flag59} / {flag75}");

            // Without the register the start would move with the window: what the check above guards.
            var fresh = KeyOf(At(75, new IntelEpisodeRegister()), IntelCondition.KindCount);
            c.Check("alarm: read cold at 75 min the run begins at its oldest report read back", fresh == $"intel:n:{S5}|20261112T110000|2", fresh);

            // A clear ends the episode: the next count is news again.
            var clear = Report(S5, t0.AddMinutes(78), kind: IntelReportKind.Clear);
            At(78, episodes, [clear]);
            var again = Report(S5, t0.AddMinutes(80), count: 3);
            var next = KeyOf(At(80, episodes, [clear, again]), IntelCondition.KindCount);
            c.Check("alarm: after a clear the same system's count is a new episode", next == $"intel:n:{S5}|20261112T120500|2", next);
            c.Check("alarm: an episode no reading can continue is forgotten",
                episodes.Apply([], t0.AddMinutes(200)).Count == 0 && episodes.Count == 0, episodes.Count.ToString(CultureInfo.InvariantCulture));
        }

        // ── Kills ────────────────────────────────────────────────────────────────────────────────────────────

        private static void Kills(Checks c)
        {
            var named = Report(S1, Ago(0.5), count: 1);
            var snap = Snap(
                [named],
                [
                    Seen(P5, S1, Ago(1), source: IntelLastSeenSource.KillAttacker, killId: K1, ship: HullA),
                    Seen(P6, S1, Ago(0.5), since: Ago(1)),   // called in the channel after the kill: one stay
                    Seen(P9, S1, Ago(1), source: IntelLastSeenSource.KillVictim, killId: K1),
                    Seen(P10, S2, Ago(0.2), since: Ago(0.2)),   // a sighting newer than the kill, a jump out
                    Seen(P3, S1, Ago(0.2), since: Ago(0.2)),    // and one where the kill was, on no kill
                ],
                [
                    Kill(K1, S1, Ago(1), P9, (P5, HullA, FarCorp), (P6, HullB, FarCorp), (P11, HullA, FarCorp)),
                    Kill(K2, S3, Ago(2), P1, (P7, HullB, FarCorp)),
                    Kill(K3, S2, Ago(2), P2, (P8, HullA, FriendCorp)),
                    Kill(K4, S6, Ago(2), P3, (P7, HullA, FarCorp)),
                ],
                [new IntelReportCharacter { IntelReportId = named.Id, CharacterId = P6, CharacterName = "Pilot 6" }]);
            var matches = Match(snap);
            var k1 = matches.FirstOrDefault(m => m.Key == $"intel:k:{K1}");
            var k2 = matches.FirstOrDefault(m => m.Key == $"intel:k:{K2}");
            c.Check("alarm: a kill where Alpha is fires, said as a kill, urgent", k1 is { Urgent: true, Silent: false } && Kind(k1) == IntelCondition.KindKill,
                Keys(matches));
            c.Check("alarm: a kill two jumps out fires too, not urgent", k2 is { Urgent: false, Silent: false }, Keys(matches));
            c.Check("alarm: a kill by blues alone does not fire", matches.All(m => m.Key != $"intel:k:{K3}"), Keys(matches));
            c.Check("alarm: a kill outside the reach does not fire", matches.All(m => m.Key != $"intel:k:{K4}"), Keys(matches));
            c.Check("alarm: the urgent kill comes first, before a newer sighting as near", matches.Count > 0 && matches[0] == k1, Keys(matches));

            var p5 = matches.FirstOrDefault(m => m.Key.StartsWith($"intel:p:{P5}|", StringComparison.Ordinal));
            var p6 = matches.FirstOrDefault(m => m.Key.StartsWith($"intel:p:{P6}|", StringComparison.Ordinal));
            c.Check("alarm: the kill's attackers are banked, not said again as a sighting",
                p5 is { Silent: true } && p6 is { Silent: true }, $"{p5?.Silent} {p6?.Silent}");
            c.Check("alarm: the victim is no sighting", matches.All(m => !m.Key.Contains($":{P9}|")), Keys(matches));
            var p10 = matches.FirstOrDefault(m => m.Key.StartsWith($"intel:p:{P10}|", StringComparison.Ordinal));
            c.Check("alarm: a pilot who was on no kill is said as a sighting", p10 is { Silent: false }, Keys(matches));

            // A kill fires whatever the minimum, and the minimum still holds for sightings.
            var three = Match(snap, Defaults with { MinPlayers = 3 });
            c.Check("alarm: a kill fires whatever min players says",
                three.Any(m => m.Key == $"intel:k:{K1}") && three.Any(m => m.Key == $"intel:k:{K2}"), Keys(three));
            c.Check("alarm: one pilot in a system is under a minimum of three", three.All(m => !m.Key.StartsWith($"intel:p:{P10}|", StringComparison.Ordinal)),
                Keys(three));

            var said = IntelCondition.ComposeAnnouncement([.. matches.Where(m => !m.Silent)]) ?? "";
            c.Check("alarm: the kill is said first, as a kill: what was lost to what",
                said.StartsWith("Kill in QZ-X77, where Pilot Alpha is: Hull Gamma lost to Hull Alpha and Hull Beta.", StringComparison.Ordinal), said);
            c.Check("alarm: the farther kill is said as one too",
                said.Contains("Kill in ZZQ-71, 2 jumps from Pilot Alpha: Hull Gamma lost to Hull Beta.", StringComparison.Ordinal), said);
            c.Check("alarm: the attackers are not said again as hostiles reported where the kill was, the other pilot is",
                said.Contains(" 1 hostile reported in QZ-X77, where Pilot Alpha is. Pilot 3.", StringComparison.Ordinal)
                && !said.Contains("hostiles reported in QZ-X77", StringComparison.Ordinal), said);
            var title = new IntelCondition(null!).DefaultText("Intel", default, [k1!]).Title;
            c.Check("alarm: a kill alone is titled as a kill", title == "Kill reported — QZ-X77", title);
        }

        // ── Who is hostile, and seen ─────────────────────────────────────────────────────────────────────────

        private static void Filters(Checks c)
        {
            var snap = Snap(seen:
            [
                Seen(P1, S2, Ago(1), corp: FriendCorp),   // blue to Alpha
                Seen(A, S2, Ago(1), corp: CorpA),         // Alpha's own character
                Seen(P2, S2, Ago(1), noVisual: true),
                Seen(P3, S2, Ago(1)),
            ]);
            var matches = Match(snap);
            c.Check("alarm: a pilot blue to the character nearest is no hostile", matches.All(m => !m.Key.Contains($":{P1}|")), Keys(matches));
            c.Check("alarm: an own character is no hostile", matches.All(m => !m.Key.Contains($":{A}|")), Keys(matches));
            c.Check("alarm: a no-visual pilot is matched unless asked", matches.Any(m => m.Key.Contains($":{P2}|")), Keys(matches));
            var seenOnly = Match(snap, Defaults with { SkipNoVisual = true });
            c.Check("alarm: ignore no visual drops the no-visual pilot only",
                seenOnly.All(m => !m.Key.Contains($":{P2}|")) && seenOnly.Any(m => m.Key.Contains($":{P3}|")), Keys(seenOnly));
            var two = Match(snap, Defaults with { MinPlayers = 2 });
            c.Check("alarm: two hostiles meet a minimum of two (the blue and the own not counted)", two.Count == 2, Keys(two));
            var three = Match(snap, Defaults with { MinPlayers = 3 });
            c.Check("alarm: and not a minimum of three", three.Count == 0, Keys(three));
        }

        // ── The config, the poll, the watched systems ────────────────────────────────────────────────────────

        private static void Config(Checks c)
        {
            IAlarmCondition condition = new IntelCondition(null!);
            c.Check("alarm: a new intel alarm looks every 15 s", condition.DefaultPollSeconds == 15, condition.DefaultPollSeconds?.ToString() ?? "null");
            using var empty = JsonDocument.Parse("{}");
            c.Check("alarm: an alarm saved before the switch has flag-only reports on", IntelCondition.Options(empty.RootElement).FlagReports);
            using var off = JsonDocument.Parse("""{"flag_reports":false}""");
            c.Check("alarm: and the switch turns them off", !IntelCondition.Options(off.RootElement).FlagReports);
            var schema = JsonSerializer.Serialize(condition.ParameterSchema);
            var flagSpec = JsonSerializer.SerializeToElement(condition.ParameterSchema).GetProperty("properties").GetProperty("flag_reports");
            c.Check("alarm: the editor shows the switch, on by default",
                flagSpec.GetProperty("type").GetString() == "boolean" && flagSpec.GetProperty("default").ValueKind == JsonValueKind.True
                && condition.ScreenField("flag_reports")?.Label is { Length: > 0 }, schema);
            c.Check("alarm: the editor's note says what changed",
                condition.ScreenDescription.Contains(EveConsole.Localization.AlarmsText.CheckIntelChangedNote, StringComparison.Ordinal));
        }

        public static async Task WatchedAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();
            var factory = new Factory(options);
            // S1 - S2 - S3 by gates, and a hole from S2 to S5.
            var gates = new Dictionary<int, int[]> { [S1] = [S2], [S2] = [S1, S3], [S3] = [S2] };
            var distances = new IntelDistanceService(gates, new IntelDistanceService.Edges(
                new Dictionary<int, int[]> { [S2] = [S5], [S5] = [S2] }, []));
            OwnPosition Own(long id, string name, int system, bool docked) =>
                new(id, name, system, Ago(5), OwnPositionSource.ChatLog, CorpA, 0, Docked: docked);
            var own = new List<OwnPosition> { Own(A, "Pilot Alpha", S1, docked: false), Own(91_100_002, "Pilot Bravo", S3, docked: true) };

            using var characters = JsonDocument.Parse("""{"around":"Characters","jumps":2}""");
            // ⚠️ The walk must be the distance service's: the stargate graph alone reads the app's own database
            // (which this check never opens), and a failure there is this check failing, not the run stopping.
            IntelWatchedSystems zone;
            try { zone = await IntelCondition.WatchedSystemsAsync(characters.RootElement, new SystemGraph(factory), factory, distances, own); }
            catch (Exception ex)
            {
                c.Check("watched: threat distance reaches through a hole (S5, two jumps via S2's hole)", false, ex.GetType().Name + ": " + ex.Message);
                return;
            }
            c.Check("watched: threat distance reaches through a hole (S5, two jumps via S2's hole)",
                zone.Systems.SetEquals([S1, S2, S3, S5]) && zone.Jumps.GetValueOrDefault(S5) == 2 && zone.ViaWormhole.Contains(S5)
                && !zone.ViaWormhole.Contains(S3), string.Join(",", zone.Systems.Select(s => s % 1000)));
            c.Check("watched: each system's nearest character, by id, for standings",
                zone.NearestCharacter.GetValueOrDefault(S3) == 91_100_002 && zone.NearestCharacter.GetValueOrDefault(S1) == A
                && zone.Nearest.GetValueOrDefault(S3) == "Pilot Bravo");

            using var undocked = JsonDocument.Parse("""{"around":"Undocked characters","jumps":0}""");
            zone = await IntelCondition.WatchedSystemsAsync(undocked.RootElement, new SystemGraph(factory), factory, distances, own);
            c.Check("watched: around undocked characters, the docked one is not watched (from the snapshot's positions)",
                zone.Systems.SetEquals([S1]), string.Join(",", zone.Systems.Select(s => s % 1000)));
        }

        /// <summary>
        /// The whole evaluation on a scratch database: the snapshot it is handed has never been read, and what was
        /// written after one evaluation is seen by the next.
        /// </summary>
        public static async Task EvaluateAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                db.SdeSolarSystems.Add(new SdeSolarSystem { SolarSystemId = S1, Name = "QZ-X77", RegionId = 1, ConstellationId = 1 });
                var now = DateTimeOffset.UtcNow;
                db.IntelLastSeens.Add(Seen(P1, S1, now.AddMinutes(-1)));
                await db.SaveChangesAsync();
            }

            var factory = new Factory(options);
            var snapshots = new IntelSnapshotService(factory, new IntelStandingService(factory));
            var condition = new IntelCondition(new SystemGraph(factory), snapshots,
                new IntelDistanceService(new Dictionary<int, int[]>(), IntelDistanceService.Edges.None));
            using var config = JsonDocument.Parse("""{"around":"Systems","systems":["QZ-X77"],"jumps":0}""");
            AlarmEvaluationContext Ctx() => new()
            {
                DbFactory = factory, ConnectionString = "", Alarm = new Alarm { Name = "intelcheck", ConditionType = "intel" },
                Now = DateTimeOffset.Now,
            };

            // A failed evaluation is a failed check here, not the end of the run.
            async Task<IReadOnlyList<AlarmMatch>> Evaluate(string check)
            {
                try { return await condition.EvaluateAsync(config.RootElement, Ctx()); }
                catch (Exception ex) { c.Check(check, false, ex.GetType().Name + ": " + ex.Message); return []; }
            }

            var first = await Evaluate("evaluate: the first evaluation runs");
            c.Check("evaluate: a snapshot never read is read before matching", first.Any(m => m.Key.StartsWith($"intel:p:{P1}|", StringComparison.Ordinal)),
                Keys(first));

            await using (var db = new AppDbContext(options))
            {
                db.IntelLastSeens.Add(Seen(P2, S1, DateTimeOffset.UtcNow.AddSeconds(-5)));
                await db.SaveChangesAsync();
            }
            await Task.Delay(20);
            var second = await Evaluate("evaluate: the second evaluation runs");
            c.Check("evaluate: a pilot written after the last read is matched by the next evaluation",
                second.Any(m => m.Key.StartsWith($"intel:p:{P2}|", StringComparison.Ordinal)), Keys(second));
            snapshots.Dispose();
        }

        /// <summary>Every intel alarm re-primed once when the keys changed shape; nothing else touched, and never twice.</summary>
        public static async Task RePrimeAsync(Checks c, string engine, DbContextOptions<AppDbContext> options)
        {
            long intel, other, ownPoll;
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.AlarmSeenKeys.ExecuteDeleteAsync();
                await db.Alarms.ExecuteDeleteAsync();
                await db.AppPreferences.Where(p => p.Key == IntelCondition.KeysVersionKey).ExecuteDeleteAsync();
                var a = new Alarm { Name = "intel", ConditionType = "intel", Primed = true, PollSeconds = 60 };
                var b = new Alarm { Name = "timer", ConditionType = "timer", Primed = true, PollSeconds = 60 };
                var d = new Alarm { Name = "intel 30", ConditionType = "intel", Primed = true, PollSeconds = 30 };
                db.Alarms.AddRange(a, b, d);
                await db.SaveChangesAsync();
                (intel, other, ownPoll) = (a.Id, b.Id, d.Id);
                db.AlarmSeenKeys.AddRange(
                    new AlarmSeenKey { AlarmId = intel, MatchKey = $"intel:{S1}|2026-11-12T11:55|pilot 1", FirstSeenAt = Now },
                    new AlarmSeenKey { AlarmId = other, MatchKey = "timer:1", FirstSeenAt = Now });
                await db.SaveChangesAsync();
            }

            bool did;
            await using (var db = new AppDbContext(options)) did = IntelCondition.RePrimeForEpisodeKeys(db);
            await using (var db = new AppDbContext(options))
            {
                var alarms = await db.Alarms.AsNoTracking().ToDictionaryAsync(x => x.Id);
                var keys = await db.AlarmSeenKeys.AsNoTracking().Select(k => k.MatchKey).ToListAsync();
                c.Check($"re-prime ({engine}): the first start after the change re-primes every intel alarm",
                    did && !alarms[intel].Primed && alarms[other].Primed);
                c.Check($"re-prime ({engine}): and drops the intel alarms' old keys, only theirs", keys.SequenceEqual(["timer:1"]), string.Join(",", keys));
                c.Check($"re-prime ({engine}): an intel alarm on the old default now looks every 15 s; one set otherwise, and other checks, keep theirs",
                    alarms[intel].PollSeconds == 15 && alarms[ownPoll].PollSeconds == 30 && alarms[other].PollSeconds == 60,
                    $"{alarms[intel].PollSeconds} {alarms[ownPoll].PollSeconds} {alarms[other].PollSeconds}");
                c.Check($"re-prime ({engine}): and marks the database",
                    await db.AppPreferences.AnyAsync(p => p.Key == IntelCondition.KeysVersionKey));
                await db.Alarms.Where(x => x.Id == intel).ExecuteUpdateAsync(s => s.SetProperty(x => x.Primed, true));
            }

            await using (var db = new AppDbContext(options)) did = IntelCondition.RePrimeForEpisodeKeys(db);
            await using (var db = new AppDbContext(options))
            {
                var primed = await db.Alarms.Where(x => x.Id == intel).Select(x => x.Primed).SingleAsync();
                c.Check($"re-prime ({engine}): never again once done", !did && primed);
            }
        }
    }
}
