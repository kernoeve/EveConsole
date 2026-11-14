// Routes and gate camps, for intel alarms: the last route the Route Planner sent for a character (CharacterRoutes, on
// both engines), followed to where the character is so only the part ahead is watched, cleared on arrival and
// ignored after six hours; the systems ahead watched however far and said along the route; gate camps worked out from
// kills fired near a watched character or on a route ahead, keyed per camp, per character and per route, so a new
// route through a known camp is said again and wakes. Every name and id here is invented.
using System.Globalization;
using System.Text.Json;
using EveConsole.Alarms;
using EveConsole.Alarms.Conditions;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Intel;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Tools.IntelCheck
{
    internal static partial class AlarmChecks
    {
        /// <summary>The route S1 … S7, start first.</summary>
        private static readonly int[] Line = [S1, S2, S3, S4, S5, S6, S7];

        private static string RouteStamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

        /// <summary>
        /// Alpha on S3, the third step of the route S1 … S7 sent at <paramref name="setAt"/> (null: no route), watching
        /// <paramref name="range"/> jumps around: S2, S3 and S4 by default. Ahead: S4 (1) … S7 (4); behind: S1 and S2.
        /// </summary>
        private static IntelWatchedSystems RouteZone(DateTimeOffset? setAt, int range = 1, long who = A, string name = "Pilot Alpha")
        {
            const int here = 2;
            var hops = Line.Select((s, i) => (s, Hops: Math.Abs(i - here))).ToDictionary(x => x.s, x => x.Hops);
            var systems = hops.Where(kv => kv.Value <= range).Select(kv => kv.Key).ToHashSet();
            var route = new Dictionary<int, IntelRouteStep>();
            var routeHere = new Dictionary<int, IntelRouteStep>();
            if (setAt is { } at)
            {
                for (var i = here + 1; i < Line.Length; i++) route[Line[i]] = new IntelRouteStep(who, name, i - here, at);
                routeHere[Line[here]] = new IntelRouteStep(who, name, 0, at, Line[here + 1]);
                systems.UnionWith(route.Keys);
            }
            return new IntelWatchedSystems(systems, hops, hops.ToDictionary(kv => kv.Key, _ => name),
                new Dictionary<int, double>(), new Dictionary<int, string>())
            {
                NearestCharacter = hops.ToDictionary(kv => kv.Key, _ => who),
                WatchReach       = hops.Keys.ToHashSet(),
                Route            = route,
                RouteHere        = routeHere,
            };
        }

        /// <summary>
        /// Alpha somewhere else, sent the route S1 … S7 at <paramref name="setAt"/>: not yet on it, so every step is ahead
        /// (S1 the first), counted only along the route (not how far Alpha is), and no step is Alpha's. As
        /// WatchedSystemsAsync builds it when no walk reaches the steps: placed by those counts.
        /// <paramref name="oldShape"/> builds it as before the fix (begun at S1: S1 Alpha's step, S2 … S7 ahead, measured).
        /// </summary>
        private static IntelWatchedSystems ElsewhereZone(DateTimeOffset setAt, bool oldShape = false)
        {
            var route = new Dictionary<int, IntelRouteStep>();
            var routeHere = new Dictionary<int, IntelRouteStep>();
            if (oldShape)
            {
                for (var i = 1; i < Line.Length; i++) route[Line[i]] = new IntelRouteStep(A, "Pilot Alpha", i, setAt);
                routeHere[Line[0]] = new IntelRouteStep(A, "Pilot Alpha", 0, setAt, Line[1]);
            }
            else
                for (var i = 0; i < Line.Length; i++) route[Line[i]] = new IntelRouteStep(A, "Pilot Alpha", i + 1, setAt, Measured: false);
            var hops = route.ToDictionary(kv => kv.Key, kv => kv.Value.Jumps);
            return new IntelWatchedSystems(route.Keys.ToHashSet(), hops, hops.ToDictionary(kv => kv.Key, _ => "Pilot Alpha"),
                new Dictionary<int, double>(), new Dictionary<int, string>())
            {
                NearestCharacter = hops.ToDictionary(kv => kv.Key, _ => A),
                WatchReach       = hops.Keys.ToHashSet(),
                Route            = route,
                RouteHere        = routeHere,
            };
        }

        /// <summary>A camp on the gate in <paramref name="system"/> to <paramref name="gateTo"/>: two pilots lost to
        /// <paramref name="attackers"/> (of <paramref name="corp"/>), the first <paramref name="firstAgo"/> minutes ago, the last
        /// <paramref name="lastAgo"/>.</summary>
        private static GateCamp Camp(int system, int gateTo, double firstAgo, double lastAgo, long corp, params long[] attackers)
        {
            var first = Ago(firstAgo);
            var last = Ago(lastAgo);
            return new GateCamp(
                $"{50_000_000 + system % 1000}|{RouteStamp(first)}", system, 50_000_000 + system % 1000, gateTo, first,
                [K1, K2], [P9, P10], first, last, last, last.AddMinutes(30),
                [.. attackers.Select(a => new IntelCampAttacker(a, corp, 0, HullA))], [HullA], false, false, false, false);
        }

        /// <summary>A snapshot holding one camp, with the kills that made it (so the attackers' hulls are named).</summary>
        private static IntelSnapshot CampSnap(GateCamp camp, IEnumerable<IntelLastSeen>? seen = null) =>
            Snap(seen: seen, kills:
            [
                Kill(K1, camp.SystemId, camp.FirstKill, P9, (P6, HullA, FarCorp)),
                Kill(K2, camp.SystemId, camp.LastKill, P10, (P6, HullA, FarCorp)),
            ]) with { Camps = [camp] };

        private static AlarmMatch? CampMatch(IEnumerable<AlarmMatch> matches) =>
            matches.FirstOrDefault(m => Kind(m) == IntelCondition.KindCamp);

        public static void Routes(Checks c)
        {
            RouteFollow(c);
            RouteAhead(c);
            CampsAheadAndBehind(c);
            CampSaidAgain(c);
            CampFilters(c);
            RouteConfig(c);
        }

        // ── Following a route ────────────────────────────────────────────────────────────────────────────────

        private static void RouteFollow(Checks c)
        {
            CharacterRoute Row(int atIndex = 0, double setAgo = 10, string? cleared = null) => new()
            {
                CharacterId = A, DestinationSystemId = S7, SystemIds = CharacterRouteStore.Encode(Line), SetAt = Iso(Ago(setAgo)),
                AtIndex = atIndex, ClearedAt = cleared,
            };
            var row = Row();
            string Ahead(CharacterRouteAhead? r) => r is null ? "none" : string.Join(",", r.Ahead.Select(x => $"{x.SystemId % 1000}:{x.Jumps}"));

            var start = CharacterRouteStore.Follow(row, S1, Now);
            c.Check("route: at the start, every step after it is ahead, by jumps along the route",
                Ahead(start) == "102:1,103:2,104:3,105:4,106:5,107:6", Ahead(start));
            var mid = CharacterRouteStore.Follow(row, S3, Now);
            c.Check("route: on the third step, the two before it are behind",
                mid is { AtIndex: 2, OnRoute: true, Here: S3, Next: S4 } && Ahead(mid) == "104:1,105:2,106:3,107:4", Ahead(mid));
            var off = CharacterRouteStore.Follow(Row(atIndex: 2), 30_000_999, Now);
            c.Check("route: off the route, the remainder from the step last seen at is ahead", off is { AtIndex: 2 } && Ahead(off) == "104:1,105:2,106:3,107:4",
                Ahead(off));
            c.Check("route: off the route, the character is on no step and leaves by no gate of it",
                off is { OnRoute: false, Here: null, Next: null }, off is null ? "none" : $"{off.Here} {off.Next}");
            var unknown = CharacterRouteStore.Follow(Row(atIndex: 4), null, Now);
            c.Check("route: where the character is not known, the step last seen at holds, on no step",
                unknown is { AtIndex: 4, OnRoute: false, Here: null }, Ahead(unknown));

            // Sent to a character somewhere else than its first step: not yet on it, every step ahead, the first included.
            var elsewhere = CharacterRouteStore.Follow(Row(atIndex: -1), 30_000_999, Now);
            c.Check("route: sent to a character elsewhere, every step is ahead, the first included, and none is theirs",
                elsewhere is { AtIndex: -1, OnRoute: false, Here: null, Next: null, Arrived: false }
                && Ahead(elsewhere) == "101:1,102:2,103:3,104:4,105:5,106:6,107:7", Ahead(elsewhere));
            var joined = CharacterRouteStore.Follow(Row(atIndex: -1), S1, Now);
            c.Check("route: and once seen on its first step, that step is theirs",
                joined is { AtIndex: 0, OnRoute: true, Here: S1, Next: S2 } && Ahead(joined) == "102:1,103:2,104:3,105:4,106:5,107:6", Ahead(joined));
            c.Check("route: at the destination it has arrived", CharacterRouteStore.Follow(row, S7, Now) is { Arrived: true });
            c.Check("route: six hours after it was sent it is ignored",
                CharacterRouteStore.Follow(Row(setAgo: 6 * 60 + 1), S3, Now) is null
                && CharacterRouteStore.Follow(Row(setAgo: 6 * 60 - 1), S3, Now) is not null);
            c.Check("route: a cleared route is not followed", CharacterRouteStore.Follow(Row(cleared: Iso(Ago(1))), S3, Now) is null);
            c.Check("route: the comma list reads back, junk dropped",
                CharacterRouteStore.Decode(" 30000101, x,30000102,,0 ").SequenceEqual([S1, S2]) && CharacterRouteStore.Decode(null).Count == 0);
        }

        // ── What is watched along it ─────────────────────────────────────────────────────────────────────────

        private static void RouteAhead(Checks c)
        {
            var snap = Snap(seen: [Seen(P1, S6, Ago(1)), Seen(P2, S1, Ago(1)), Seen(P3, S2, Ago(1))]);
            var options = new IntelCondition.IntelAlarmOptions(Jumps: 1);
            var matches = IntelCondition.Match(snap, RouteZone(Ago(10)), options, Now);
            var ahead = Pilot(matches, P1);
            c.Check("route: a sighting three jumps ahead on the route, beyond the range, fires", ahead is not null, Keys(matches));
            c.Check("route: one behind, beyond the range, does not", Pilot(matches, P2) is null, Keys(matches));
            c.Check("route: one behind but within the range still fires as near", Pilot(matches, P3) is not null, Keys(matches));
            var said = ahead is null ? "" : IntelCondition.ComposeAnnouncement([ahead]) ?? "";
            c.Check("route: said along the route", said == "1 hostile reported in Q2X-91, 3 jumps ahead on Pilot Alpha's route. Pilot 1.", said);
            c.Check("route: and in the summary", ahead?.Summary.Contains("(3 jumps ahead on Pilot Alpha's route)", StringComparison.Ordinal) == true,
                ahead?.Summary ?? "none");
            var none = IntelCondition.Match(snap, RouteZone(null), options, Now);
            c.Check("route: without a route, the system ahead beyond the range is not watched", Pilot(none, P1) is null, Keys(none));
        }

        // ── Camps ahead and behind ───────────────────────────────────────────────────────────────────────────

        private static void CampsAheadAndBehind(Checks c)
        {
            var setAt = Ago(10);
            var options = new IntelCondition.IntelAlarmOptions(Jumps: 1);
            AlarmMatch? On(GateCamp camp, IntelWatchedSystems? zone = null, IntelCondition.IntelAlarmOptions? o = null) =>
                CampMatch(IntelCondition.Match(CampSnap(camp), zone ?? RouteZone(setAt), o ?? options, Now));

            var aheadCamp = Camp(S5, S6, 9, 6, FarCorp, P6);
            var ahead = On(aheadCamp);
            c.Check("camp: on the route ahead, beyond the range, fires, keyed by the camp, the character and the route",
                ahead?.Key == $"intel:c:{aheadCamp.Key}|{A}|{RouteStamp(setAt)}", ahead?.Key ?? "none");
            c.Check("camp: on the route ahead within five jumps it is urgent", ahead is { Urgent: true } && Has(ahead, "on_route"));
            var said = ahead is null ? "" : IntelCondition.ComposeAnnouncement([ahead]) ?? "";
            c.Check("camp: said as a gate camp, along the route, with its kills and hulls",
                said.StartsWith("Gate camp in K7-QZP on the Q2X-91 gate, 2 jumps ahead on Pilot Alpha's route. 2 kills in 3 minutes. ", StringComparison.Ordinal)
                && said.Contains("Hull Alpha", StringComparison.Ordinal), said);
            c.Check("camp: and in the summary", ahead?.Summary.StartsWith("Gate camp in K7-QZP on the Q2X-91 gate (2 jumps ahead on Pilot Alpha's route): 2 kills in 3 min",
                StringComparison.Ordinal) == true, ahead?.Summary ?? "none");

            c.Check("camp: behind on the route, beyond the range, does not fire", On(Camp(S1, S2, 9, 6, FarCorp, P6)) is null);
            var behindNear = On(Camp(S2, S1, 9, 6, FarCorp, P6));
            c.Check("camp: behind but within the range fires as near, not on the route, not urgent",
                behindNear is { Urgent: false } && !Has(behindNear, "on_route") && behindNear.Key.EndsWith($"|{A}|near", StringComparison.Ordinal),
                behindNear?.Key ?? "none");

            var far = Camp(S7, S6, 9, 6, FarCorp, P6);
            c.Check("camp: four jumps ahead is urgent (within five)", On(far) is { Urgent: true });

            // The step the character is on: a camp on the gate the route leaves by is on the route; on another gate, near.
            var outbound = On(Camp(S3, S4, 9, 6, FarCorp, P6));
            c.Check("camp: here, on the gate the route leaves by, is on the route",
                outbound is not null && Has(outbound, "on_route") && outbound.Key.EndsWith($"|{RouteStamp(setAt)}", StringComparison.Ordinal),
                outbound?.Key ?? "none");
            var otherGate = On(Camp(S3, S2, 9, 6, FarCorp, P6));
            c.Check("camp: here, on another gate, is near", otherGate is not null && !Has(otherGate, "on_route"), otherGate?.Key ?? "none");

            c.Check("camp: without the route, one ahead beyond the range does not fire", On(aheadCamp, RouteZone(null)) is null);

            // A route sent to a character somewhere else (one plan to everyone online): the plan's first step is not where
            // they are. A camp there on the gate to the second step is on the route ahead, but nothing says how near.
            var startCamp = Camp(S1, S2, 9, 6, FarCorp, P6);
            var elsewhere = On(startCamp, ElsewhereZone(setAt));
            c.Check("camp: sent from elsewhere, one at the plan's start on its way out fires on the route, not urgent",
                elsewhere is { Urgent: false } && Has(elsewhere, "on_route"), elsewhere?.Key ?? "none");
            var saidStart = elsewhere is null ? "" : IntelCondition.ComposeAnnouncement([elsewhere]) ?? "";
            c.Check("camp: and said as ahead on the route, not where the character is",
                saidStart.Contains("1 jump ahead on Pilot Alpha's route", StringComparison.Ordinal), saidStart);
            c.Check("camp: sent from elsewhere, one two steps on is not urgent either",
                On(Camp(S2, S3, 9, 6, FarCorp, P6), ElsewhereZone(setAt)) is { Urgent: false });
            // ⚠️ Mutation: the zone as it was built before (the stored step taken for where the character is) says the
            // camp is where they are, urgent.
            c.Check("camp: (mutation) taking the plan's start for where the character is would make it urgent",
                On(startCamp, ElsewhereZone(setAt, oldShape: true)) is { Urgent: true });
        }

        // ── A new route through a known camp ────────────────────────────────────────────────────────────────

        private static void CampSaidAgain(Checks c)
        {
            var camp = Camp(S5, S6, 9, 6, FarCorp, P6);
            var snap = CampSnap(camp);
            var options = new IntelCondition.IntelAlarmOptions(Jumps: 3);   // S5 is within the range: near without a route
            string? KeyOf(IntelWatchedSystems zone) => CampMatch(IntelCondition.Match(snap, zone, options, Now))?.Key;

            var near = KeyOf(RouteZone(null, range: 3));
            var first = KeyOf(RouteZone(Ago(10), range: 3));
            var again = KeyOf(RouteZone(Ago(10), range: 3));
            var second = KeyOf(RouteZone(Ago(0.25), range: 3));
            c.Check("camp: near, without a route, keyed as near", near?.EndsWith($"|{A}|near", StringComparison.Ordinal) == true, near ?? "none");
            c.Check("camp: a route sent through it is a new key", first is not null && first != near, first ?? "none");
            c.Check("camp: the same route, the same key: said once", again == first, again ?? "none");
            c.Check("camp: a new route through the same camp is a new key again", second is not null && second != first, second ?? "none");
            var bravo = KeyOf(RouteZone(null, range: 3, who: B, name: "Pilot Bravo"));
            c.Check("camp: near another character, a key of its own", bravo is not null && bravo != near, bravo ?? "none");

            // News: the camp's last kill is six minutes old, past the fresh window. Seen first near, it is banked unsaid; a
            // new route through it is news from when this alarm caught it, and fires.
            var memory = new IntelWatchMemory();
            IReadOnlyList<AlarmMatch> Look(IntelWatchedSystems zone, DateTimeOffset at)
            {
                var matches = IntelCondition.Match(snap, zone, options, at);
                var look = IntelCondition.Observe(memory, 7, zone, options, matches, at);
                return IntelCondition.News(matches, zone, options, at, Fresh, look);
            }
            var before = CampMatch(Look(RouteZone(null, range: 3), Now));
            c.Check("camp: (setup) a camp six minutes old, first seen near, is banked unsaid", before is { Silent: true }, before?.Key ?? "none");
            var routeZone = RouteZone(Now.AddSeconds(10), range: 3);
            var routed = CampMatch(Look(routeZone, Now.AddSeconds(15)));
            c.Check("camp: a new route through it fires", routed is { Silent: false }, routed?.Key ?? "none");
            var still = CampMatch(Look(routeZone, Now.AddSeconds(30)));
            c.Check("camp: and the same route a pass later keeps its key", still?.Key == routed?.Key);
            var next = CampMatch(Look(RouteZone(Now.AddMinutes(4), range: 3), Now.AddMinutes(4).AddSeconds(5)));
            c.Check("camp: another new route through it fires again", next is { Silent: false } && next.Key != routed?.Key, next?.Key ?? "none");

            // ⚠️ Mutation: judged without when the alarm caught the key (a camp treated as a sighting), the new route stays unsaid.
            var matchesNow = IntelCondition.Match(snap, routeZone, options, Now.AddSeconds(15));
            var blind = CampMatch(IntelCondition.News(matchesNow, routeZone, options, Now.AddSeconds(15), Fresh,
                IntelWatchMemory.Look.AllAlong));
            c.Check("camp: (mutation) without the catch time a new route through a camp would be unsaid", blind is { Silent: true });
        }

        // ── What does not fire ───────────────────────────────────────────────────────────────────────────────

        private static void CampFilters(Checks c)
        {
            var options = new IntelCondition.IntelAlarmOptions(Jumps: 1);
            var zone = RouteZone(Ago(10));
            AlarmMatch? On(GateCamp camp, IntelCondition.IntelAlarmOptions? o = null) =>
                CampMatch(IntelCondition.Match(CampSnap(camp), zone, o ?? options, Now));

            c.Check("camp: one of blues alone does not fire", On(Camp(S5, S6, 9, 6, FriendCorp, P6)) is null);
            c.Check("camp: with camps switched off, none fires", On(Camp(S5, S6, 9, 6, FarCorp, P6), options with { Camps = false }) is null);
            c.Check("camp: one ended (thirty minutes after its last kill) does not fire", On(Camp(S5, S6, 50, 31, FarCorp, P6)) is null);
            var ignored = IntelCondition.Match(CampSnap(Camp(S5, S6, 9, 6, FarCorp, P6)) with
            {
                Watch = WatchList((IntelWatchKind.Pilot, P6, IntelWatchMode.Ignore)),
            }, zone, options, Now);
            c.Check("camp: one of pilots the watch list ignores does not fire", CampMatch(ignored) is null, Keys(ignored));
            var priority = Camp(S5, S6, 9, 6, FarCorp, P6);
            var redZone = RouteZone(Ago(10));
            var red = IntelCondition.Match(CampSnap(priority), redZone, options with { HostileFrom = StandingTier.Red }, Now);
            c.Check("camp: judged by the hostility choice (neutral campers under red only do not fire)", CampMatch(red) is null, Keys(red));
        }

        // ── The config ───────────────────────────────────────────────────────────────────────────────────────

        private static void RouteConfig(Checks c)
        {
            using (var doc = JsonDocument.Parse("{}"))
                c.Check("config: gate camps on by default", IntelCondition.Options(doc.RootElement).Camps);
            using (var doc = JsonDocument.Parse("""{"camps":false}"""))
                c.Check("config: gate camps can be switched off", !IntelCondition.Options(doc.RootElement).Camps);

            IAlarmCondition condition = new IntelCondition(null!);
            var props = JsonSerializer.SerializeToElement(condition.ParameterSchema).GetProperty("properties");
            c.Check("editor: route and camps are there, on by default, with words in the interface language",
                new[] { "route", "camps" }.All(p => props.TryGetProperty(p, out var prop) && prop.GetProperty("default").GetBoolean()
                                                    && condition.ScreenField(p)?.Label is { Length: > 0 }));
            c.Check("editor: the note says both are on by default",
                condition.ScreenDescription.Contains(EveConsole.Localization.AlarmsText.CheckIntelRouteNote, StringComparison.Ordinal));
            using var off = JsonDocument.Parse("""{"around":"Characters","route":false,"camps":false}""");
            var described = condition.Describe(off.RootElement);
            c.Check("describe: says the route ahead and gate camps are off",
                described.Contains("not the route ahead", StringComparison.Ordinal) && described.Contains("no gate camps", StringComparison.Ordinal), described);
        }

        // ── On a database ────────────────────────────────────────────────────────────────────────────────────

        /// <summary>The routes table through the store: a route sent replaces the one before, one of a single system clears
        /// it, reading follows the character and writes it back, arrival clears, six hours on it is ignored.</summary>
        public static async Task RouteStoreAsync(Checks c, string label, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                await db.CharacterRoutes.ExecuteDeleteAsync();
            }
            var factory = new Factory(options);
            async Task<CharacterRoute?> RowAsync(long who)
            {
                await using var db = new AppDbContext(options);
                return await db.CharacterRoutes.AsNoTracking().FirstOrDefaultAsync(r => r.CharacterId == who);
            }

            await CharacterRouteStore.SaveAsync(factory, A, [S1, S2, S3], Ago(30));
            await CharacterRouteStore.SaveAsync(factory, A, Line, Ago(10));
            var row = await RowAsync(A);
            c.Check($"{label} routes: a route sent replaces the one before, the character not yet seen on it",
                row is { DestinationSystemId: S7, AtIndex: -1, ClearedAt: null } && row.SystemIds == CharacterRouteStore.Encode(Line)
                && row.SetAt == Iso(Ago(10)), row is null ? "none" : $"{row.SystemIds} {row.AtIndex} {row.SetAt}");

            await using (var db = new AppDbContext(options))
            {
                var before = await CharacterRouteStore.ReadAsync(db, new Dictionary<long, int> { [A] = 30_000_999 }, Now);
                c.Check($"{label} routes: read with the character elsewhere, every step is ahead and nothing is written",
                    before is [{ AtIndex: -1, OnRoute: false }] && before[0].Ahead.Select(x => x.SystemId).SequenceEqual(Line));
            }
            c.Check($"{label} routes: (still not seen on it)", (await RowAsync(A))?.AtIndex == -1);
            await using (var db = new AppDbContext(options))
            {
                var live = await CharacterRouteStore.ReadAsync(db, new Dictionary<long, int> { [A] = S3 }, Now);
                c.Check($"{label} routes: read with the character on the third step, the rest is ahead",
                    live is [{ AtIndex: 2 }] && live[0].Ahead.Select(x => x.SystemId).SequenceEqual([S4, S5, S6, S7]));
            }
            c.Check($"{label} routes: the step the character was seen at is written back", (await RowAsync(A))?.AtIndex == 2);

            await using (var db = new AppDbContext(options))
            {
                var away = await CharacterRouteStore.ReadAsync(db, new Dictionary<long, int> { [A] = 30_000_999 }, Now);
                c.Check($"{label} routes: off the route, the step last seen at holds", away is [{ AtIndex: 2 }]);
                var arrived = await CharacterRouteStore.ReadAsync(db, new Dictionary<long, int> { [A] = S7 }, Now);
                c.Check($"{label} routes: at the destination it is not ahead any more", arrived.Count == 0);
            }
            c.Check($"{label} routes: and it is cleared", (await RowAsync(A))?.ClearedAt == Iso(Now));

            await CharacterRouteStore.SaveAsync(factory, A, Line, Ago(5));
            c.Check($"{label} routes: a route sent after arrival is flown again", (await RowAsync(A)) is { ClearedAt: null, AtIndex: -1 });
            await CharacterRouteStore.SaveAsync(factory, A, [S3], Ago(4));
            c.Check($"{label} routes: a route of one system (already there) clears the one before", (await RowAsync(A))?.ClearedAt is not null);

            await CharacterRouteStore.SaveAsync(factory, B, Line, Ago(6 * 60 + 5));
            await using (var db = new AppDbContext(options))
            {
                var old = await CharacterRouteStore.ReadAsync(db, new Dictionary<long, int> { [B] = S2 }, Now);
                c.Check($"{label} routes: one sent more than six hours ago is ignored", old.Count == 0);
            }

            // A move seen by the location poll follows the route too, with no alarm reading it.
            var now = DateTimeOffset.UtcNow;
            await CharacterRouteStore.SaveAsync(factory, A, Line, now.AddMinutes(-2));
            await using (var db = new AppDbContext(options))
            {
                await db.CharacterStatuses.Where(s => s.CharacterId == A).ExecuteDeleteAsync();
                db.CharacterStatuses.Add(new CharacterStatus { CharacterId = A, Online = true, SolarSystemId = S5 });
                await db.SaveChangesAsync();
            }
            await CharacterRouteStore.FollowStatusAsync(factory, A);
            c.Check($"{label} routes: a move the location poll saw moves the route on", (await RowAsync(A))?.AtIndex == 4);
            await using (var db = new AppDbContext(options))
                await db.CharacterStatuses.Where(s => s.CharacterId == A).ExecuteUpdateAsync(s => s.SetProperty(x => x.SolarSystemId, S7));
            await CharacterRouteStore.FollowStatusAsync(factory, A);
            c.Check($"{label} routes: and arriving there clears it", (await RowAsync(A))?.ClearedAt is not null);
        }

        /// <summary>The watched systems with a route: over a line of gates on a scratch database, through the store.</summary>
        public static async Task RouteWatchedAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();
            var factory = new Factory(options);
            var gates = Line.Select((s, i) => (s, i)).ToDictionary(x => x.s, x => Line.Where((_, j) => Math.Abs(j - x.i) == 1).ToArray());
            var distances = new IntelDistanceService(gates, IntelDistanceService.Edges.None);
            var own = new List<OwnPosition>
            {
                new(A, "Pilot Alpha", S3, Ago(5), OwnPositionSource.ChatLog, CorpA, 0),
                new(B, "Pilot Bravo", S1, Ago(5), OwnPositionSource.ChatLog, CorpB, 0, Docked: true),
            };
            await CharacterRouteStore.SaveAsync(factory, A, Line, Ago(10));

            async Task<IntelWatchedSystems?> Zone(string json, IReadOnlyList<OwnPosition> where, string check)
            {
                using var doc = JsonDocument.Parse(json);
                try { return await IntelCondition.WatchedSystemsAsync(doc.RootElement, new SystemGraph(factory), factory, distances, where, now: Now); }
                catch (Exception ex) { c.Check(check, false, ex.GetType().Name + ": " + ex.Message); return null; }
            }
            string Ids(IEnumerable<int> ids) => string.Join(",", ids.Order().Select(s => s % 1000));

            var zone = await Zone("""{"around":"Characters","characters":["Pilot Alpha"],"jumps":1,"watch_list":false}""", own, "watched: the zone with a route");
            if (zone is null) return;
            c.Check("watched: the route ahead is watched however far, the part behind not",
                zone.Systems.SetEquals([S2, S3, S4, S5, S6, S7]) && !zone.Systems.Contains(S1), Ids(zone.Systems));
            c.Check("watched: each step ahead with its jumps along the route, and whose route it is",
                Ids(zone.Route.Keys) == "104,105,106,107" && zone.Route[S6].Jumps == 3 && zone.Route[S6].CharacterId == A
                && zone.Route[S6].Name == "Pilot Alpha", Ids(zone.Route.Keys));
            c.Check("watched: the step the character is on, with the gate it leaves by", zone.RouteHere.TryGetValue(S3, out var here) && here.NextSystemId == S4);
            c.Check("watched: a step beyond every range is placed by its jumps along the route, judged for its character",
                zone.Jumps.GetValueOrDefault(S7) == 4 && zone.NearestCharacter.GetValueOrDefault(S7) == A, $"{zone.Jumps.GetValueOrDefault(S7)}");
            await using (var db = new AppDbContext(options))
                c.Check("watched: reading it wrote back where the character is on it",
                    (await db.CharacterRoutes.AsNoTracking().FirstAsync(r => r.CharacterId == A)).AtIndex == 2);

            var off = await Zone("""{"around":"Characters","characters":["Pilot Alpha"],"jumps":1,"watch_list":false,"route":false}""", own, "watched: the zone with the route off");
            c.Check("watched: with the route off, only the range", off is not null && off.Route.Count == 0 && off.Systems.SetEquals([S2, S3, S4]),
                off is null ? "none" : Ids(off.Systems));
            var others = await Zone("""{"around":"Characters","characters":["Pilot Bravo"],"jumps":1,"watch_list":false}""", own,
                "watched: the zone around another character");
            c.Check("watched: a route is watched only for a character the alarm watches", others is not null && others.Route.Count == 0,
                others is null ? "none" : Ids(others.Route.Keys));
            var systems = await Zone("""{"around":"Systems","systems":["QZ-X77"],"jumps":0}""", own, "watched: the zone around systems");
            c.Check("watched: around systems there is no route", systems is not null && systems.Route.Count == 0);

            // Bravo, docked, sends a route: watched from the dock, even by an alarm around undocked characters.
            await CharacterRouteStore.SaveAsync(factory, B, [S1, S2, S3], Ago(1));
            var docked = await Zone("""{"around":"Undocked characters","jumps":0,"watch_list":false}""", own, "watched: a docked character's route");
            c.Check("watched: a docked character's route is watched", docked is not null && docked.Route.ContainsKey(S2) && docked.Route[S2].CharacterId == B,
                docked is null ? "none" : Ids(docked.Route.Keys));
            c.Check("watched: seen on its first step, that step and its gate out are the character's",
                docked is not null && docked.RouteHere.TryGetValue(S1, out var start) && start.CharacterId == B && start.NextSystemId == S2);

            // Off the path: Alpha on S3, sent S5 … S7 (the game picks its own gates there). Every step is ahead, the first
            // included, measured from where Alpha is; no step is Alpha's, so no gate out of one is on the route.
            await CharacterRouteStore.SaveAsync(factory, A, [S5, S6, S7], Ago(2));
            var offPath = await Zone("""{"around":"Characters","characters":["Pilot Alpha"],"jumps":1,"watch_list":false}""", own,
                "watched: a character off the path");
            c.Check("watched: off the path, the character is on no step of it", offPath is not null && offPath.RouteHere.Count == 0,
                offPath is null ? "none" : Ids(offPath.RouteHere.Keys));
            c.Check("watched: off the path, every step is ahead, the first included, measured from where the character is",
                offPath is not null && Ids(offPath.Route.Keys) == "105,106,107" && offPath.Route[S5] is { Jumps: 2, Measured: true }
                && offPath.Route[S7] is { Jumps: 4, Measured: true }, offPath is null ? "none" : Ids(offPath.Route.Keys));
            await using (var db = new AppDbContext(options))
                c.Check("watched: and nothing is written back for it", (await db.CharacterRoutes.AsNoTracking().FirstAsync(r => r.CharacterId == A)).AtIndex == -1);

            // Bravo, docked where no walk starts (around undocked characters), sent a route elsewhere: the steps are counted
            // along the route only, which is not how far Bravo is.
            await CharacterRouteStore.SaveAsync(factory, B, [S4, S5, S6], Ago(1));
            var unmeasured = await Zone("""{"around":"Undocked characters","jumps":0,"watch_list":false}""", own, "watched: a route with no walk");
            c.Check("watched: no walk from the character, the steps are counted along the route, not measured, none theirs",
                unmeasured is not null && unmeasured.Route.TryGetValue(S4, out var first) && first is { CharacterId: B, Jumps: 1, Measured: false }
                && !unmeasured.RouteHere.Values.Any(s => s.CharacterId == B),
                unmeasured is null ? "none" : string.Join(" ", unmeasured.Route.Select(kv => $"{kv.Key % 1000}:{kv.Value.CharacterId % 1000}:{kv.Value.Jumps}:{kv.Value.Measured}")));

            // Arrived: the route goes.
            var there = new List<OwnPosition> { own[0] with { SystemId = S7 } };
            var arrived = await Zone("""{"around":"Characters","characters":["Pilot Alpha"],"jumps":1,"watch_list":false}""", there,
                "watched: the zone on arrival");
            c.Check("watched: on arrival nothing is ahead", arrived is not null && arrived.Route.Count == 0, arrived is null ? "none" : Ids(arrived.Route.Keys));
            await using (var db = new AppDbContext(options))
                c.Check("watched: and the route is cleared", (await db.CharacterRoutes.AsNoTracking().FirstAsync(r => r.CharacterId == A)).ClearedAt is not null);
        }
    }
}
