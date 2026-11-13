// The Universe Map and the route planner on the shared snapshot: marks coloured by the worst standings tier and
// faded by age, the clear and no-visual states, camps on their gates (the stub and its tag clear of the far system),
// move and heading arrows (capped per map, after it keeps its own systems), the route planner's hostiles column, and
// the alert zone drawn through the alarm's own function. Every name and id here is invented.
using Avalonia;
using EveConsole.Controls;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;
using static EveConsole.Tools.IntelCheck.MapScene;

namespace EveConsole.Tools.IntelCheck
{
    internal static class MapChecks
    {
        public static void Run(Checks c, string root)
        {
            var snap = Snapshot();
            var live = LiveIntelService.ToMap(snap, Now);

            // ── Marks: tier, fade, state ─────────────────────────────────────────────────────────────────────
            var red = live.Hostiles.GetValueOrDefault(S2);
            c.Check("map: a red pilot's mark is red, seen, and fresh",
                red is { Tier: StandingTier.Red, State: IntelSystemState.Hostile, Count: 1 } && red.Freshness > 0.9,
                $"{red?.Tier} {red?.State} {red?.Freshness:0.00}");
            var orange = live.Hostiles.GetValueOrDefault(S3);
            c.Check("map: an orange pilot ten minutes old is orange, and faded below the new one",
                orange is { Tier: StandingTier.Orange } && orange.Freshness < red!.Freshness && orange.Freshness > 0,
                $"{orange?.Tier} {orange?.Freshness:0.00}");
            var nv = live.Hostiles.GetValueOrDefault(S4);
            c.Check("map: three unnamed with no visual are a neutral no-visual mark",
                nv is { State: IntelSystemState.NoVisual, Tier: StandingTier.Neutral, Count: 3 }, $"{nv?.State} {nv?.Tier} {nv?.Count}");
            c.Check("map: bubbles with nobody counted are flags only",
                live.Hostiles.GetValueOrDefault(S5) is { State: IntelSystemState.FlagsOnly, Count: 0 });
            c.Check("map: a system called clear is a clear, not a hostile mark",
                live.Clears.ContainsKey(S6) && !live.Hostiles.ContainsKey(S6) && live.Clears[S6].Freshness is > 0 and < 1,
                $"{live.Clears.GetValueOrDefault(S6)?.Freshness:0.00}");
            c.Check("map: a blue pilot is never drawn", !live.Hostiles.ContainsKey(S11) && !live.Clears.ContainsKey(S11));
            var nearlyGone = live.Hostiles.GetValueOrDefault(S10);
            c.Check("map: a mark near the end of its lifetime has faded almost out",
                nearlyGone is { Tier: StandingTier.Red } && nearlyGone.Freshness < 0.2, $"{nearlyGone?.Freshness:0.00}");

            var graph = Graph();
            var marks = MapToolViewModel.BuildMarkers(live, graph);
            c.Check("map marks: the red mark is drawn in the red tier's colour, filled, as fresh as its news",
                marks.GetValueOrDefault(S2) is { State: MapMarkState.Hostile } m2 && m2.Tint == IntelTierColors.Colour(StandingTier.Red)
                && Math.Abs(m2.Freshness - red!.Freshness) < 1e-9);
            c.Check("map marks: orange in orange, no visual hollow, flags only as \"!\", clear as a check",
                marks.GetValueOrDefault(S3)?.Tint == IntelTierColors.Colour(StandingTier.Orange)
                && marks.GetValueOrDefault(S4)?.State == MapMarkState.NoVisual
                && marks.GetValueOrDefault(S5) is { State: MapMarkState.FlagsOnly, Reported: true }
                && marks.GetValueOrDefault(S6) is { State: MapMarkState.Clear, Hostiles: 0, HostileTitle: not null });
            c.Check("map marks: a hover says how new and how seen",
                marks.GetValueOrDefault(S4)?.HostileDetail?.Contains(Localization.MapText.LiveNoVisualLine, StringComparison.Ordinal) == true
                && marks.GetValueOrDefault(S2)?.HostileRows?.FirstOrDefault()?.Text.StartsWith(
                       string.Format(Localization.MapText.LiveNewestLine, ""), StringComparison.Ordinal) == true);

            // ── Camps on their gates ─────────────────────────────────────────────────────────────────────────
            c.Check("map: the camp is on ZXQ-4F's gate to 9Q-ZX2, neutral, two kills",
                live.Camps is [{ SystemId: S7, GateSystemId: S8, Tier: StandingTier.Neutral } camp] && camp.Camp.KillMailIds.Count == 2,
                string.Join(",", live.Camps.Select(x => $"{x.SystemId}->{x.GateSystemId} {x.Tier}")));
            var stubs = MapToolViewModel.BuildCamps(live, graph);
            c.Check("map: the camp's stub runs towards the system its gate leads to, named in its hover",
                stubs is [{ SystemId: S7, ToSystemId: S8, Kills: 2 } stub] && stub.Title.Contains("9Q-ZX2", StringComparison.Ordinal)
                && stub.Title.Contains("ZXQ-4F", StringComparison.Ordinal) && stub.Freshness is > 0 and <= 1);
            c.Check("map: an ended camp is not drawn",
                LiveIntelService.ToMap(snap, Now.AddMinutes(31)).Camps.Count == 0);

            // ── Arrows ───────────────────────────────────────────────────────────────────────────────────────
            var move    = live.Arrows.SingleOrDefault(a => !a.Heading);
            var heading = live.Arrows.SingleOrDefault(a => a.Heading);
            c.Check("arrows: a move runs from where they came from to where they were seen (QX-7ZK → XQ1-Z2), in red",
                move is { FromSystemId: S9, ToSystemId: S2, Tier: StandingTier.Red }, $"{move?.FromSystemId}->{move?.ToSystemId} {move?.Tier}");
            c.Check("arrows: a heading runs from where they were seen to where they are going (ZZQ-71 ⇢ QZ-X77)",
                heading is { FromSystemId: S3, ToSystemId: S1, Tier: StandingTier.Orange }, $"{heading?.FromSystemId}->{heading?.ToSystemId}");
            var drawn = MapToolViewModel.BuildArrows(live, graph);
            c.Check("arrows: drawn solid for the move and dashed for the heading, the move's hover names both ends",
                drawn.Count == 2 && drawn.Single(a => a.FromId == S9).Dashed == false && drawn.Single(a => a.FromId == S3).Dashed
                && drawn.Single(a => a.FromId == S9).Detail.Contains("QX-7ZK", StringComparison.Ordinal));
            c.Check("arrows: an arrow fades with its report", move!.Freshness > heading!.Freshness);

            var later = IntelSnapshotBuilder.Build(Input(clearS2After: true), new IntelCampRegister(), 2);
            c.Check("arrows: called clear after the move, the system's arrow goes with its pilots",
                LiveIntelService.ToMap(later, Now).Arrows.All(a => a.ToSystemId != S2));
            c.Check("arrows: past the report's lifetime there is no arrow",
                LiveIntelService.ToMap(snap, Now.AddHours(1)).Arrows.Count == 0);

            // ── The cap comes after each map keeps its own systems ──────────────────────────────────────────────
            // Forty newer moves into Q7-ZZX, each from a system this map does not hold: news from elsewhere.
            var busy     = IntelSnapshotBuilder.Build(Input(manyMoves: 40), new IntelCampRegister(), 3);
            var busyLive = LiveIntelService.ToMap(busy, Now);
            var many     = busyLive.Arrows;
            c.Check("arrows: every live one in New Eden reaches the maps, newest first",
                many.Count == 42 && many.Zip(many.Skip(1)).All(p => p.First.At >= p.Second.At), many.Count.ToString());
            static bool Has(IEnumerable<MapIntelArrow> list, int from, int to) => list.Any(a => a.FromId == from && a.ToId == to);
            var ours = MapToolViewModel.BuildArrows(busyLive, graph);
            c.Check("arrows: forty newer elsewhere, and this map still draws its own move and heading",
                ours.Count == 2 && Has(ours, S9, S2) && Has(ours, S3, S1), string.Join(",", ours.Select(a => $"{a.FromId % 100}->{a.ToId % 100}")));
            c.Check("arrows: (mutation) capped before the map keeps its own, the heading is lost",
                !many.Take(IntelMapView.MaxArrows).Any(a => a.FromSystemId == S3 && a.ToSystemId == S1));

            // A region's map that holds every one of them draws the newest MaxArrows; the whole-universe map hands
            // its canvas them all, and the canvas draws the newest MaxArrows on screen.
            var sources = Enumerable.Range(0, 40).Select(i => 39_901_000 + i).ToList();
            var region  = new MapGraph(
                [.. graph.Nodes, .. sources.Select(id => new MapNode(id, "QX" + (id % 1000), 900, 600, -0.3, false, SecurityClass: "null",
                                                                      RegionId: 10_900_001, RegionName: "Q-ZX9"))],
                graph.Edges);
            var expected = many.Where(a => region.Nodes.Any(n => n.Id == a.FromSystemId) && region.Nodes.Any(n => n.Id == a.ToSystemId))
                               .Take(IntelMapView.MaxArrows).Select(a => (a.FromSystemId, a.ToSystemId)).ToList();
            var capped = MapToolViewModel.BuildArrows(busyLive, region);
            c.Check($"arrows: a region's map draws at most {IntelMapView.MaxArrows}, its newest",
                capped.Count == IntelMapView.MaxArrows && capped.Select(a => (a.FromId, a.ToId)).SequenceEqual(expected), capped.Count.ToString());
            var universe = MapToolViewModel.BuildArrows(busyLive, region with { IsContinuous = true });
            c.Check("arrows: the whole-universe map hands its canvas every one, for the canvas to cap on screen",
                universe.Count == 42 && universe.Take(IntelMapView.MaxArrows).Select(a => (a.FromId, a.ToId)).SequenceEqual(expected),
                universe.Count.ToString());
            var canvas = File.ReadAllText(Path.Combine(root, "Controls", "MapCanvas.cs"));
            c.Check("arrows: the canvas counts only arrows it drew towards the cap",
                canvas.Contains("if (drawnArrows >= EveConsole.Services.Intel.IntelMapView.MaxArrows) break;", StringComparison.Ordinal)
                && canvas.Contains("if (DrawIntelArrow(ctx, arrow)) drawnArrows++;", StringComparison.Ordinal));

            // ── A camp's stub and tag stop short of the far system ─────────────────────────────────────────────
            StubChecks(c);

            // ── The route planner's hostiles column ──────────────────────────────────────────────────────────
            var route = IntelMapView.Route(snap, Systems.Select(s => s.Id), Alpha, Now);
            c.Check("route: hostiles on the route as the route's character sees them",
                route.GetValueOrDefault(S2) is { Hostiles: 1, Tier: StandingTier.Red }
                && route.GetValueOrDefault(S4) is { Hostiles: 3, NoVisual: true }
                && route.GetValueOrDefault(S7) is { Camp: true, Hostiles: 2 },
                string.Join(", ", route.Values.Select(r => $"{r.SystemId % 100}:{r.Hostiles}{r.Tier}")));
            c.Check("route: a clear and a blue are not hostiles", !route.ContainsKey(S6) && !route.ContainsKey(S11));
            c.Check("route: only the route's own systems",
                IntelMapView.Route(snap, [S2], Alpha, Now).Keys.SequenceEqual([S2]));

            // ── The alert zone is the alarm's own set ────────────────────────────────────────────────────────
            var condition = File.ReadAllText(Path.Combine(root, "Alarms", "Conditions", "IntelCondition.cs"));
            var zone      = File.ReadAllText(Path.Combine(root, "Services", "Intel", "IntelZoneService.cs"));
            c.Check("zone: the alarm's check matches against WatchedSystemsAsync",
                condition.Contains("await WatchedSystemsAsync(config, _graph, ctx.DbFactory, _distances, OwnOf(snapshot), ct)", StringComparison.Ordinal)
                && !condition.Contains("_graph.DistancesWithinAsync", StringComparison.Ordinal));
            c.Check("zone: the map's outline is drawn through the same function",
                zone.Contains("IntelCondition.WatchedSystemsAsync(", StringComparison.Ordinal));
        }

        /// <summary>
        /// The stub's geometry, as the canvas lays it out: the camped system's box centred at the origin, 46 by 16
        /// (a short name), and the system its gate leads to due east. The tag is 13 by 13; the stub's round end and
        /// shadow reach 3 px past where it ends.
        /// </summary>
        private static void StubChecks(Checks c)
        {
            const double tagW = 13, tagH = 13, edge = 23, cap = 3;
            var east = new Vector(1, 0);
            static Rect Box(double x) => new(x - 23, -8, 46, 16);
            static Rect Tag(Point at) => new(at.X - 6.5, at.Y - 6.5, 13, 13);

            // The scene's ZXQ-4F → 9Q-ZX2 at the wide zoom: 77 px apart, boxes 31 px apart.
            var shortFar = Box(77);
            var (reach, tag) = MapCampStub.Layout(new Point(0, 0), east, edge, 77 * 0.4, [shortFar], tagW, tagH);
            c.Check("stub: on a short gate line the stub stops before the far system's box",
                reach > edge + 4 && reach + cap < shortFar.Left, $"{reach:0.0} vs {shortFar.Left:0.0}");
            c.Check("stub: and its tag leaves the line rather than cover the far system's name", tag is null);
            var old = new Point(Math.Max(77 * 0.4, edge + 46), 0);
            c.Check("stub: (mutation) laid out as before, the tag sat on the far system's box", Tag(old).Intersects(shortFar));

            // Room to clear the hostile mark but not to reach two fifths of the way: still on the line, clear of the far box.
            var midFar = Box(160);
            (reach, tag) = MapCampStub.Layout(new Point(0, 0), east, edge, 160 * 0.4, [midFar], tagW, tagH);
            c.Check("stub: with room, the tag sits past the hostile mark, clear of the far box",
                tag is { } mid && Math.Abs(mid.X - (edge + MapCampStub.MarkClearance)) < 1e-9 && !Tag(mid).Intersects(midFar),
                $"{tag}");

            // A long line: two fifths of the way, as designed.
            var longFar = Box(300);
            (reach, tag) = MapCampStub.Layout(new Point(0, 0), east, edge, 300 * 0.4, [longFar], tagW, tagH);
            c.Check("stub: on a long gate line, two fifths of the way with the tag at its end",
                tag is { } far && Math.Abs(far.X - 120) < 1e-9 && Math.Abs(reach - 120) < 1e-9 && !Tag(far).Intersects(longFar));

            // Dots, zoomed out: the far system 80 px to the west, its name written to its right, towards the camp.
            var farDot  = new Rect(-85, -5, 10, 10);
            var farName = new Rect(-72, -6, 40, 12);
            (reach, tag) = MapCampStub.Layout(new Point(0, 0), new Vector(-1, 0), 5, 80 * 0.4, [farDot, farName], tagW, tagH);
            c.Check("stub: on the dot tier it stops short of the far system's name too",
                tag is null && -(reach + cap) > farName.Right, $"{reach:0.0}");

            // No far system on the map: the short stub up and to the left, its tag at the end.
            (reach, tag) = MapCampStub.Layout(new Point(0, 0), new Vector(-0.7071, -0.7071), edge, 0, [], tagW, tagH);
            c.Check("stub: with no far system, a short stub with its tag at the end",
                tag is not null && Math.Abs(reach - (edge + MapCampStub.MarkClearance)) < 1e-9);
        }
    }
}
