// Who and where, for intel alarms: the hostility tier (neutral or worse by default, negative only, red only) judged by
// the standings of the watched character nearest; priority systems (always watched, always wake, urgent, said first);
// the watch list (Always fires out to its reach whatever the range, the tier or the minimum, a blue still never;
// Ignore never fires); approach (seen nearer than before, or heading toward a watched character: urgent, worded as
// approaching, waking beyond the wake range; heading away said and nothing else) and "through Thera or Turnur" from
// the walk's wormhole flag; a system only the light-year range catches keeps its light years inside the watch list's
// reach; a system crossing from that reach into the range is news then. Every name and id here is invented.
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
        // A second character of the user's, in a corporation of its own, and the corporations the two rate.
        private const long B = 91_100_002, CorpB = 98_200_002;
        private const long RedCorp = 98_200_030, OrangeCorp = 98_200_031, SplitCorp = 98_200_032;

        private const double MetresPerLightYear = 9.4607304725808e15;

        /// <summary>Alpha rates RedCorp red and OrangeCorp orange; Bravo rates SplitCorp red, which Alpha has no
        /// standing with (neutral).</summary>
        private static IntelStandings TierStandings() => new(
            [new IntelStandings.OwnCharacter(A, "Pilot Alpha", CorpA, 0), new IntelStandings.OwnCharacter(B, "Pilot Bravo", CorpB, 0)],
            [CorpA, CorpB],
            [(A, FriendCorp, 10), (A, RedCorp, -10), (A, OrangeCorp, -3), (B, SplitCorp, -10)]);

        /// <summary>
        /// Around Alpha, out to 6 jumps (S1 to S5); S6 eight jumps out, inside the watch list's reach of 10 and no
        /// further; S7 twelve jumps out. Alpha's walk reaches all of them. S3's nearest way goes through a hole when asked.
        /// </summary>
        private static IntelWatchedSystems FarZone(IEnumerable<int>? priority = null, bool viaHole = false, long? s4Viewer = null)
        {
            var hops = new Dictionary<int, int> { [S1] = 0, [S2] = 1, [S3] = 2, [S4] = 3, [S5] = 6, [S6] = 8, [S7] = 12 };
            var prio = new HashSet<int>(priority ?? []);
            var systems = new HashSet<int> { S1, S2, S3, S4, S5 };
            systems.UnionWith(prio);
            var jumps = hops.Where(kv => kv.Value <= 10 || prio.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            var walk = hops.ToDictionary(kv => kv.Key, kv => new IntelHop(kv.Value, viaHole && kv.Key == S3));
            return new IntelWatchedSystems(systems, jumps, jumps.ToDictionary(kv => kv.Key, _ => "Pilot Alpha"),
                new Dictionary<int, double>(), new Dictionary<int, string>())
            {
                NearestCharacter = jumps.ToDictionary(kv => kv.Key, kv => kv.Key == S4 && s4Viewer is long v ? v : A),
                ViaWormhole      = viaHole ? new HashSet<int> { S3 } : new HashSet<int>(),
                Priority         = prio,
                WatchReach       = hops.Where(kv => kv.Value <= 10).Select(kv => kv.Key).ToHashSet(),
                NearestOrigin    = jumps.ToDictionary(kv => kv.Key, _ => S1),
                Walks            = new Dictionary<int, IReadOnlyDictionary<int, IntelHop>> { [S1] = walk },
            };
        }

        private static readonly IntelCondition.IntelAlarmOptions Six = new(Jumps: 6);

        private static IntelWatchSet WatchList(params (string Kind, long Id, int Mode)[] entries) =>
            new(entries.Select((e, i) => new IntelWatchEntry { Id = i + 1, Kind = e.Kind, EntityId = e.Id, Name = "intelcheck", Mode = e.Mode }));

        private static AlarmMatch? Pilot(IEnumerable<AlarmMatch> matches, long pilot) =>
            matches.FirstOrDefault(m => m.Key.StartsWith($"intel:p:{pilot}|", StringComparison.Ordinal));

        private static bool Has(AlarmMatch? m, string key) => m?.Detail is { } d && d.TryGetValue(key, out var v) && v is true;

        public static void WhoAndWhere(Checks c)
        {
            Hostility(c);
            Priority(c);
            WatchListed(c);
            Approaching(c);
            WhoAndWhereConfig(c);
        }

        // ── The hostility tier ───────────────────────────────────────────────────────────────────────────────

        private static void Hostility(Checks c)
        {
            var snap = Snap(
                seen:
                [
                    Seen(P1, S2, Ago(1), corp: RedCorp),
                    Seen(P2, S2, Ago(1), corp: OrangeCorp),
                    Seen(P3, S2, Ago(1), corp: FarCorp),       // no standing: neutral
                    Seen(P4, S2, Ago(1), corp: FriendCorp),    // blue
                    Seen(P5, S4, Ago(1), corp: SplitCorp),     // red to Bravo, neutral to Alpha
                ],
                reports: [Report(S3, Ago(1), count: 4)],
                kills:
                [
                    Kill(K1, S3, Ago(1), P9, (P6, HullA, FarCorp)),   // by a neutral
                    Kill(K2, S3, Ago(1), P10, (P7, HullB, RedCorp)),  // by a red
                ]) with { Standings = TierStandings() };
            string Fired(IntelCondition.IntelAlarmOptions o, IntelWatchedSystems? zone = null) =>
                string.Join(" ", new[] { P1, P2, P3, P4 }.Where(p => Pilot(IntelCondition.Match(snap, zone ?? FarZone(), o, Now), p) is not null)
                    .Select(p => (p % 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)));

            c.Check("hostile: neutral or worse (the default): red, orange and neutral fire, blue does not", Fired(Six) == "1 2 3", Fired(Six));
            c.Check("hostile: negative only: red and orange", Fired(Six with { HostileFrom = StandingTier.Orange }) == "1 2",
                Fired(Six with { HostileFrom = StandingTier.Orange }));
            c.Check("hostile: red only: red alone", Fired(Six with { HostileFrom = StandingTier.Red }) == "1",
                Fired(Six with { HostileFrom = StandingTier.Red }));

            var red = IntelCondition.Match(snap, FarZone(), Six with { HostileFrom = StandingTier.Red }, Now);
            c.Check("hostile: red only still fires for an unnamed count", red.Any(m => m.Key.StartsWith($"intel:n:{S3}|", StringComparison.Ordinal)),
                Keys(red));
            c.Check("hostile: red only: a kill by a neutral does not fire, one by a red does",
                red.All(m => m.Key != $"intel:k:{K1}") && red.Any(m => m.Key == $"intel:k:{K2}"), Keys(red));

            // Judged for the nearest character: the same pilot is red where Bravo is nearest, neutral where Alpha is.
            var alphaNear = IntelCondition.Match(snap, FarZone(), Six with { HostileFrom = StandingTier.Red }, Now);
            var bravoNear = IntelCondition.Match(snap, FarZone(s4Viewer: B), Six with { HostileFrom = StandingTier.Red }, Now);
            c.Check("hostile: judged by the nearest character: red to Bravo fires where Bravo is nearest",
                Pilot(bravoNear, P5) is not null, Keys(bravoNear));
            c.Check("hostile: and not where Alpha (neutral to them) is nearest", Pilot(alphaNear, P5) is null, Keys(alphaNear));
        }

        // ── Priority systems ─────────────────────────────────────────────────────────────────────────────────

        private static void Priority(Checks c)
        {
            var snap = Snap(
                seen: [Seen(P1, S7, Ago(1)), Seen(P2, S2, Ago(0.5)), Seen(P3, S6, Ago(1))],
                reports: [Report(S7, Ago(1), flags: (int)IntelRules.IntelFlags.Bubbles)]);
            var plain = IntelCondition.Match(snap, FarZone(), Six, Now);
            c.Check("priority: without it, a pilot twelve jumps out does not fire", Pilot(plain, P1) is null, Keys(plain));

            var zone = FarZone(priority: [S7]);
            var matches = IntelCondition.Match(snap, zone, Six, Now);
            var p1 = Pilot(matches, P1);
            c.Check("priority: a pilot in a priority system twelve jumps out fires, urgent",
                p1 is { Urgent: true } && Has(p1, "priority"), Keys(matches));
            c.Check("priority: a flag there fires urgent too",
                matches.FirstOrDefault(m => m.Key.StartsWith($"intel:f:{S7}|", StringComparison.Ordinal)) is { Urgent: true }, Keys(matches));
            c.Check("priority: said first, ahead of a newer sighting a jump out", matches.Count > 0 && Has(matches[0], "priority"), Keys(matches));
            c.Check("priority: a system outside the range and not a priority still does not fire", Pilot(matches, P3) is null, Keys(matches));

            var judged = IntelCondition.Waking(matches, zone, Six with { WakeJumps = 1 }, Now, Fresh, IntelWatchMemory.Look.AllAlong);
            c.Check("priority: it wakes beyond the wake range, at full loudness", Pilot(judged, P1) is { Quiet: false, Loudness: 1 });
            var stale = IntelCondition.Waking(matches, zone, Six, Now.AddMinutes(5), Fresh, IntelWatchMemory.Look.AllAlong);
            c.Check("priority: stale, it only shows", Pilot(stale, P1) is { Quiet: true });

            var said = IntelCondition.ComposeAnnouncement(matches) ?? "";
            c.Check("priority: said first, as a priority system",
                said.StartsWith("Priority system: 1 hostile reported in Z9-QQ1, 12 jumps from Pilot Alpha.", StringComparison.Ordinal), said);
        }

        // ── The watch list ───────────────────────────────────────────────────────────────────────────────────

        private static void WatchListed(Checks c)
        {
            var count = Report(S6, Ago(1), count: 3);
            count.Ships = "3× Hull Beta";
            var snap = Snap(
                seen:
                [
                    Seen(P1, S6, Ago(1)),                       // eight jumps out, on Always
                    Seen(P2, S2, Ago(1)),                       // on Ignore
                    Seen(P3, S2, Ago(1), corp: OrangeCorp),     // corporation on Ignore
                    Seen(P4, S5, Ago(1), corp: FriendCorp),     // blue, on Always
                    Seen(P5, S7, Ago(1)),                       // on Always, twelve jumps out: past the reach
                    Seen(P6, S4, Ago(1)),                       // on Always, in range, neutral
                ],
                reports: [count],
                kills: [Kill(K1, S3, Ago(1), P9, (P2, HullA, FarCorp))]) with
            {
                Standings = TierStandings(),
                HullIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Hull Beta"] = HullB },
                Watch = WatchList(
                    (IntelWatchKind.Pilot, P1, IntelWatchMode.Always), (IntelWatchKind.Pilot, P2, IntelWatchMode.Ignore),
                    (IntelWatchKind.Corporation, OrangeCorp, IntelWatchMode.Ignore), (IntelWatchKind.Pilot, P4, IntelWatchMode.Always),
                    (IntelWatchKind.Pilot, P5, IntelWatchMode.Always), (IntelWatchKind.Pilot, P6, IntelWatchMode.Always),
                    (IntelWatchKind.Hull, HullB, IntelWatchMode.Always)),
            };
            var matches = IntelCondition.Match(snap, FarZone(), Six, Now);
            var p1 = Pilot(matches, P1);
            c.Check("watch: Always fires beyond the range, within the watch list's reach, urgent",
                p1 is { Urgent: true } && Has(p1, "watch"), Keys(matches));
            c.Check("watch: not past the reach", Pilot(matches, P5) is null, Keys(matches));
            c.Check("watch: Ignore never fires, by the pilot or the corporation",
                Pilot(matches, P2) is null && Pilot(matches, P3) is null, Keys(matches));
            c.Check("watch: a kill by ignored pilots alone does not fire", matches.All(m => m.Key != $"intel:k:{K1}"), Keys(matches));
            c.Check("watch: a blue on Always still never fires", Pilot(matches, P4) is null, Keys(matches));
            c.Check("watch: a count said in a hull on Always fires beyond the range",
                matches.FirstOrDefault(m => m.Key.StartsWith($"intel:n:{S6}|", StringComparison.Ordinal)) is { Urgent: true }, Keys(matches));

            var red = IntelCondition.Match(snap, FarZone(), Six with { HostileFrom = StandingTier.Red, MinPlayers = 5 }, Now);
            c.Check("watch: Always fires whatever the hostility choice and the minimum (a neutral, alone, under red only and 5)",
                Pilot(red, P6) is not null, Keys(red));

            var off = IntelCondition.Match(snap, FarZone(), Six with { WatchList = false }, Now);
            c.Check("watch: with the watch list off, Always beyond the range does not fire and Ignore does",
                Pilot(off, P1) is null && Pilot(off, P2) is not null && off.Any(m => m.Key == $"intel:k:{K1}"), Keys(off));

            var judged = IntelCondition.Waking(matches, FarZone(), Six with { WakeJumps = 2 }, Now, Fresh, IntelWatchMemory.Look.AllAlong);
            c.Check("watch: Always wakes beyond the wake range", Pilot(judged, P1) is { Quiet: false, Loudness: 1 });

            var said = IntelCondition.ComposeAnnouncement(matches) ?? "";
            c.Check("watch: said as the watch list's, ahead of the rest", said.StartsWith("Watch list: ", StringComparison.Ordinal), said);

            // Ranked: a priority system, then the watch list, then other urgent news, then the rest.
            var ranked = IntelCondition.Ordered(
            [
                new AlarmMatch("plain", "plain") { Detail = new Dictionary<string, object?> { ["jumps"] = 0 } },
                new AlarmMatch("urgent", "urgent") { Urgent = true, Detail = new Dictionary<string, object?> { ["jumps"] = 1 } },
                new AlarmMatch("watch", "watch") { Urgent = true, Detail = new Dictionary<string, object?> { ["jumps"] = 2, ["watch"] = true } },
                new AlarmMatch("priority", "priority") { Urgent = true, Detail = new Dictionary<string, object?> { ["jumps"] = 9, ["priority"] = true } },
            ]);
            c.Check("order: priority, watch list, urgent, the rest", Keys(ranked) == "priority watch urgent plain", Keys(ranked));
        }

        // ── Approach ─────────────────────────────────────────────────────────────────────────────────────────

        private static void Approaching(Checks c)
        {
            IntelLastSeen Moved(long pilot, int system, int? before, double beforeAgo, int? reportId = null)
            {
                var row = Seen(pilot, system, Ago(0.5));
                row.PrevSystemId = before;
                row.PrevSeenAt = Iso(Ago(beforeAgo));
                row.ReportId = reportId;
                return row;
            }
            var toward = Report(S3, Ago(0.5), count: 1);
            toward.HeadingSystemId = S1;
            var away = Report(S2, Ago(0.5), count: 1);
            away.HeadingSystemId = S4;
            var came = Report(S4, Ago(0.5), count: 5);
            came.FromSystemId = S5;
            var snap = Snap(
                reports: [toward, away, came],
                seen:
                [
                    Moved(P1, S2, before: S4, beforeAgo: 5),            // three jumps out, now one: closer
                    Moved(P2, S3, before: S1, beforeAgo: 5),            // where Alpha is, now two out: farther
                    Moved(P3, S2, before: S5, beforeAgo: 40),           // six out, but forty minutes ago: too long
                    Moved(P4, S3, before: null, beforeAgo: 0, reportId: toward.Id),   // heading for where Alpha is
                    Moved(P5, S2, before: null, beforeAgo: 0, reportId: away.Id),     // heading away
                ],
                named:
                [
                    new IntelReportCharacter { IntelReportId = toward.Id, CharacterId = P4, CharacterName = "Pilot 4" },
                    new IntelReportCharacter { IntelReportId = away.Id, CharacterId = P5, CharacterName = "Pilot 5" },
                ]);
            var zone = FarZone();
            var matches = IntelCondition.Match(snap, zone, Six, Now);
            c.Check("approach: a pilot seen nearer than before is approaching, urgent", Pilot(matches, P1) is { Urgent: true } p1 && Has(p1, "approaching"),
                Keys(matches));
            c.Check("approach: one seen farther than before is not", Pilot(matches, P2) is { Urgent: false } p2 && !Has(p2, "approaching"));
            c.Check("approach: before is only lately (twice the named lifetime)", Pilot(matches, P3) is { Urgent: false });
            c.Check("approach: heading toward the character is approaching, urgent", Pilot(matches, P4) is { Urgent: true } p4 && Has(p4, "toward"));
            c.Check("approach: heading away is not urgent", Pilot(matches, P5) is { Urgent: false } p5 && Has(p5, "away"));
            var count = matches.FirstOrDefault(m => m.Key.StartsWith($"intel:n:{S4}|", StringComparison.Ordinal));
            c.Check("approach: a count that came from farther out is approaching", count is { Urgent: true } && Has(count, "approaching"), Keys(matches));

            string SaidOf(long pilot) => IntelCondition.ComposeAnnouncement([Pilot(matches, pilot)!]) ?? "";
            c.Check("approach: said as approaching, with how far out it was",
                SaidOf(P1) == "1 hostile reported in XQ1-Z2, 1 jump from Pilot Alpha. Approaching: was 3 jumps from Pilot Alpha. Pilot 1.", SaidOf(P1));
            c.Check("approach: heading toward, said with where that is",
                SaidOf(P4) == "1 hostile reported in ZZQ-71, 2 jumps from Pilot Alpha. Approaching: heading to QZ-X77, where Pilot Alpha is. Pilot 4.",
                SaidOf(P4));
            c.Check("approach: heading away is said, and nothing else",
                SaidOf(P5) == "1 hostile reported in XQ1-Z2, 1 jump from Pilot Alpha. Heading away, to X2-QZ7. Pilot 5.", SaidOf(P5));
            c.Check("approach: a farther sighting says nothing of its way", !SaidOf(P2).Contains("Approaching", StringComparison.Ordinal), SaidOf(P2));
            c.Check("approach: in the English summary too",
                Pilot(matches, P1)!.Summary.Contains("approaching (was 3 jumps out)", StringComparison.Ordinal)
                && Pilot(matches, P5)!.Summary.Contains("heading away to X2-QZ7", StringComparison.Ordinal), Pilot(matches, P1)!.Summary);

            // Waking: approaching wakes beyond the wake range unless the alarm says not; heading away does not.
            var narrow = IntelCondition.Waking(matches, zone, Six with { WakeJumps = 0 }, Now, Fresh, IntelWatchMemory.Look.AllAlong);
            c.Check("approach: wakes beyond the wake range", Pilot(narrow, P1) is { Quiet: false } && Pilot(narrow, P4) is { Quiet: false });
            c.Check("approach: heading away keeps its normal waking (outside the wake range: shown only)", Pilot(narrow, P5) is { Quiet: true });
            var strict = IntelCondition.Waking(matches, zone, Six with { WakeJumps = 0, ApproachWakes = false }, Now, Fresh,
                IntelWatchMemory.Look.AllAlong);
            c.Check("approach: with 'approaching pilots still wake' off, the wake range holds", Pilot(strict, P1) is { Quiet: true });

            // Through Thera or Turnur: from the walk's wormhole flag.
            var via = IntelCondition.Match(Snap(seen: [Seen(P6, S3, Ago(0.5))]), FarZone(viaHole: true), Six, Now);
            var saidVia = IntelCondition.ComposeAnnouncement(via) ?? "";
            c.Check("via: a distance through a hole is said so",
                saidVia.StartsWith("1 hostile reported in ZZQ-71, 2 jumps from Pilot Alpha, through Thera or Turnur.", StringComparison.Ordinal), saidVia);
            c.Check("via: and in the summary", Pilot(via, P6)?.Summary.Contains("through Thera or Turnur", StringComparison.Ordinal) == true,
                Pilot(via, P6)?.Summary ?? "none");
            var notVia = IntelCondition.ComposeAnnouncement(IntelCondition.Match(Snap(seen: [Seen(P6, S3, Ago(0.5))]), FarZone(), Six, Now)) ?? "";
            c.Check("via: not said by gates alone", !notVia.Contains("Thera", StringComparison.Ordinal), notVia);
        }

        // ── The config ───────────────────────────────────────────────────────────────────────────────────────

        private static void WhoAndWhereConfig(Checks c)
        {
            IntelCondition.IntelAlarmOptions Read(string json)
            {
                using var doc = JsonDocument.Parse(json);
                return IntelCondition.Options(doc.RootElement);
            }
            int? Reach(string json)
            {
                using var doc = JsonDocument.Parse(json);
                return IntelCondition.ReadWatchReach(doc.RootElement);
            }
            c.Check("config: by default neutral or worse, the watch list on, approaching pilots wake",
                Read("{}") is { HostileFrom: StandingTier.Neutral, WatchList: true, ApproachWakes: true });
            c.Check("config: negative only and red only are read",
                Read("""{"hostile_from":"Negative only"}""").HostileFrom == StandingTier.Orange
                && Read("""{"hostile_from":"Red only"}""").HostileFrom == StandingTier.Red);
            c.Check("config: the watch list reach is 10 by default, at most 15, none with the list off",
                Reach("{}") == 10 && Reach("""{"watch_list_jumps":40}""") == 15 && Reach("""{"watch_list":false}""") is null
                && Reach("""{"watch_list_jumps":4}""") == 4);

            IAlarmCondition condition = new IntelCondition(null!);
            var props = JsonSerializer.SerializeToElement(condition.ParameterSchema).GetProperty("properties");
            c.Check("editor: the new fields are there, with words in the interface language",
                new[] { "hostile_from", "watch_list", "watch_list_jumps", "approach_wakes", "priority_systems" }
                    .All(p => props.TryGetProperty(p, out _) && condition.ScreenField(p)?.Label is { Length: > 0 }));
            c.Check("editor: the hostility choices have words of their own",
                new[] { IntelCondition.HostileNeutral, IntelCondition.HostileNegative, IntelCondition.HostileRed }
                    .All(v => condition.ScreenOption("hostile_from", v) is { Length: > 0 } t && t.Length > 0));
            using var cfg = JsonDocument.Parse("""{"around":"Characters","jumps":3,"hostile_from":"Red only","priority_systems":["QZ-X77"],"watch_list_jumps":12}""");
            var described = condition.Describe(cfg.RootElement);
            c.Check("describe: says red only, the watch list's reach and the priority systems",
                described.Contains("red only", StringComparison.Ordinal) && described.Contains("watch list within 12 jumps", StringComparison.Ordinal)
                && described.Contains("1 priority system", StringComparison.Ordinal), described);
        }

        /// <summary>The watched systems with priority systems and the watch list's reach, over a hand graph on a scratch database.</summary>
        public static async Task WhoAndWhereWatchedAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                // In a straight line too, a light year apart: S1 at 0, S7 six light years out.
                foreach (var (id, name) in Names)
                    db.SdeSolarSystems.Add(new SdeSolarSystem
                    {
                        SolarSystemId = id, Name = name, RegionId = 1, ConstellationId = 1, X = (id - S1) * MetresPerLightYear,
                    });
                await db.SaveChangesAsync();
            }
            var factory = new Factory(options);
            // A line of gates: S1 - S2 - S3 - S4 - S5 - S6 - S7.
            var line = new[] { S1, S2, S3, S4, S5, S6, S7 };
            var gates = line.Select((s, i) => (s, i)).ToDictionary(x => x.s,
                x => line.Where((_, j) => Math.Abs(j - x.i) == 1).ToArray());
            var distances = new IntelDistanceService(gates, IntelDistanceService.Edges.None);
            var own = new List<OwnPosition>
            {
                new(A, "Pilot Alpha", S1, Ago(5), OwnPositionSource.ChatLog, CorpA, 0),
                new(B, "Pilot Bravo", S2, Ago(5), OwnPositionSource.ChatLog, CorpB, 0, Docked: true),
            };

            async Task<IntelWatchedSystems?> Zone(string json, string check)
            {
                using var doc = JsonDocument.Parse(json);
                try { return await IntelCondition.WatchedSystemsAsync(doc.RootElement, new SystemGraph(factory), factory, distances, own); }
                catch (Exception ex) { c.Check(check, false, ex.GetType().Name + ": " + ex.Message); return null; }
            }
            string Ids(IEnumerable<int> ids) => string.Join(",", ids.Order().Select(s => s % 1000));

            var zone = await Zone("""{"around":"Characters","jumps":1,"watch_list_jumps":3,"priority_systems":["Q2X-91"]}""",
                                  "watched: the zone with a priority system and the watch list's reach");
            if (zone is null) return;
            c.Check("watched: a priority system is watched beyond the range, at its distance from the nearest character",
                zone.Systems.Contains(S6) && zone.Priority.SetEquals([S6]) && zone.Jumps.GetValueOrDefault(S6) == 4
                && zone.NearestCharacter.GetValueOrDefault(S6) == B, $"{Ids(zone.Systems)} / {zone.Jumps.GetValueOrDefault(S6)}");
            c.Check("watched: the watch list's reach is known beside, not in the outline",
                zone.WatchReach.SetEquals([S1, S2, S3, S4, S5]) && !zone.Systems.Contains(S4) && zone.Systems.SetEquals([S1, S2, S3, S6]),
                $"{Ids(zone.WatchReach)} / {Ids(zone.Systems)}");
            c.Check("watched: how far a system beyond the reach is, from the character nearest the sighting",
                zone.JumpsBetween(S3, S7) == 5 && zone.JumpsBetween(S1, S7) == 6, $"{zone.JumpsBetween(S3, S7)} {zone.JumpsBetween(S1, S7)}");

            var docked = await Zone("""{"around":"Undocked characters","jumps":2,"watch_list":false,"priority_systems":["Q2X-91"]}""",
                                    "watched: the zone with nobody's reach but a priority system");
            if (docked is null) return;
            c.Check("watched: with the watch list off its reach is the range",
                docked.WatchReach.SetEquals([S1, S2, S3]), Ids(docked.WatchReach));
            var nobody = await Zone("""{"around":"Characters","characters":["Pilot Nobody"],"jumps":2,"priority_systems":["Q2X-91"]}""",
                                    "watched: the zone with no character online but a priority system");
            if (nobody is null) return;
            c.Check("watched: a priority system is watched with no character to watch around",
                nobody.Systems.SetEquals([S6]) && !nobody.Jumps.ContainsKey(S6), Ids(nobody.Systems));

            // ── The light-year range inside the watch list's reach ──
            // Around Alpha alone (Bravo is docked): 2 jumps, 4.5 light years, the watch list out to 6 jumps. S4 and S5
            // only the light years catch, though both are inside the watch list's reach: they keep their light years.
            const string LyJson = """{"around":"Undocked characters","jumps":2,"ly":4.5,"watch_list_jumps":6}""";
            IntelCondition.ForgetPositions();
            IntelWatchedSystems? byLy;
            try { byLy = await Zone(LyJson, "watched: the zone with a light-year range inside the watch list's reach"); }
            finally { IntelCondition.ForgetPositions(); }
            if (byLy is null) return;
            c.Check("watched: what only the light years catch is watched, not placed by jumps, though inside the reach",
                byLy.Systems.SetEquals([S1, S2, S3, S4, S5]) && !byLy.Jumps.ContainsKey(S4) && !byLy.Jumps.ContainsKey(S5)
                && Math.Abs(byLy.LightYears.GetValueOrDefault(S5) - 4) < 1e-9 && byLy.LightYearsNearestCharacter.GetValueOrDefault(S5) == A
                && !byLy.NearestCharacter.ContainsKey(S5),
                $"{Ids(byLy.Systems)} / jumps {Ids(byLy.Jumps.Keys)}");
            c.Check("watched: beyond the light years, the watch list's reach is still by jumps",
                byLy.WatchReach.SetEquals([S1, S2, S3, S4, S5, S6, S7]) && byLy.Jumps.GetValueOrDefault(S6) == 5
                && byLy.Jumps.GetValueOrDefault(S7) == 6, $"{Ids(byLy.WatchReach)} / jumps {Ids(byLy.Jumps.Keys)}");

            using var lyDoc = JsonDocument.Parse(LyJson);
            var lyOptions = IntelCondition.Options(lyDoc.RootElement);
            var lySnap = Snap(seen: [Seen(P6, S5, Ago(0.5))]);
            var lyMatches = IntelCondition.Waking(IntelCondition.Match(lySnap, byLy, lyOptions, Now), byLy, lyOptions, Now, Fresh,
                                                  IntelWatchMemory.Look.AllAlong);
            var inLy = Pilot(lyMatches, P6);
            c.Check("watched: a sighting only the light years catch, inside the reach, is said in light years",
                inLy is { } m5 && m5.Summary.Contains("4.0 ly from Pilot Alpha", StringComparison.Ordinal)
                && m5.Detail?["jumps"] is null && m5.Detail?["ly"] is 4.0, inLy?.Summary ?? "none");
            c.Check("watched: and played by its light years",
                inLy is { } l5 && Math.Abs(l5.Loudness - IntelCondition.Loudness(null, 4, 2, 4.5)) < 1e-9
                && Math.Abs(l5.Loudness - IntelCondition.Loudness(4, 4, 2, 4.5)) > 1e-3, inLy?.Loudness.ToString("0.000") ?? "none");
        }

        /// <summary>
        /// A character moving toward a hostile already sitting in a system: the system crosses from the watch list's
        /// reach into the range and is news from that moment, through the look the alarm's own evaluation takes.
        /// </summary>
        public static void MoveIntoRange(Checks c)
        {
            // A line S1 … S7. Alpha in S1, then a jump on in S2; the range 4, the watch list's reach 10.
            IntelWatchedSystems Around(int at)
            {
                var line = new[] { S1, S2, S3, S4, S5, S6, S7 };
                var here = Array.IndexOf(line, at);
                var hops = line.Select((s, i) => (s, Hops: Math.Abs(i - here))).ToDictionary(x => x.s, x => x.Hops);
                var range = hops.Where(kv => kv.Value <= 4).Select(kv => kv.Key).ToHashSet();
                return new IntelWatchedSystems(range, hops, hops.ToDictionary(kv => kv.Key, _ => "Pilot Alpha"),
                    new Dictionary<int, double>(), new Dictionary<int, string>())
                {
                    NearestCharacter = hops.ToDictionary(kv => kv.Key, _ => A),
                    WatchReach       = hops.Keys.ToHashSet(),
                    NearestOrigin    = hops.ToDictionary(kv => kv.Key, _ => at),
                };
            }
            var options = new IntelCondition.IntelAlarmOptions(Jumps: 4);
            // P6 has sat in S6 for five minutes: five jumps out of S1, four out of S2.
            var snap = Snap(seen: [Seen(P6, S6, Ago(5))]);
            var memory = new IntelWatchMemory();

            var before = Around(S1);
            var first = IntelCondition.Match(snap, before, options, Now.AddSeconds(-30));
            var lookBefore = IntelCondition.Observe(memory, 1, before, options, first, Now.AddSeconds(-30));
            c.Check("move: before the jump, beyond the range, the hostile is not matched",
                Pilot(first, P6) is null && lookBefore.ReachedAt(S6) == DateTimeOffset.MinValue, Keys(first));

            var after = Around(S2);
            var matches = IntelCondition.Match(snap, after, options, Now);
            var look = IntelCondition.Observe(memory, 1, after, options, matches, Now);
            c.Check("move: the system crossing into the range is new to the range then, though the reach held it",
                look.EnteredAt(S6) == Now && look.ReachedAt(S6) == DateTimeOffset.MinValue,
                $"{look.EnteredAt(S6):O} / {look.ReachedAt(S6):O}");
            var judged = IntelCondition.Waking(matches, after, options, Now, Fresh, look);
            c.Check("move: the hostile sitting there five minutes is news, and wakes",
                Pilot(judged, P6) is { Quiet: false }, Keys(judged.Where(m => !m.Quiet)));

            // Back a jump: S6 leaves the range for the reach alone, and the next jump in is news again.
            var back = Observe(before, Now.AddSeconds(15));
            c.Check("move: leaving the range for the reach alone is not news to the reach", back.ReachedAt(S6) == DateTimeOffset.MinValue);
            var again = Observe(after, Now.AddSeconds(30));
            c.Check("move: coming back into the range is news again", again.EnteredAt(S6) == Now.AddSeconds(30));

            // An Always pilot beyond the range is news from when its system came into the reach, not the range.
            var farOnly = IntelWatchMemory.Look.AllAlong with
            {
                Reached = new Dictionary<int, DateTimeOffset> { [S7] = Now.AddSeconds(-10) },
            };
            var alwaysSnap = Snap(seen: [Seen(P7, S7, Ago(5))]) with { Watch = WatchList((IntelWatchKind.Pilot, P7, IntelWatchMode.Always)) };
            var always = IntelCondition.Waking(IntelCondition.Match(alwaysSnap, before, options, Now), before, options, Now, Fresh, farOnly);
            c.Check("move: an Always pilot beyond the range is news from when the reach took its system in",
                Pilot(always, P7) is { Quiet: false }, Keys(always));
            var stale = IntelCondition.Waking(IntelCondition.Match(alwaysSnap, before, options, Now), before, options, Now, Fresh,
                IntelWatchMemory.Look.AllAlong with { Entered = new Dictionary<int, DateTimeOffset> { [S7] = Now.AddSeconds(-10) } });
            c.Check("move: and not from the range's memory, which does not hold it", Pilot(stale, P7) is { Quiet: true }, Keys(stale));

            IntelWatchMemory.Look Observe(IntelWatchedSystems zone, DateTimeOffset at) =>
                IntelCondition.Observe(memory, 1, zone, options, IntelCondition.Match(snap, zone, options, at), at);
        }
    }
}
