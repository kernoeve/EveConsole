// The Universe Map and the route planner on the shared snapshot: marks coloured by the worst standings tier and
// faded by age, the clear and no-visual states, camps on their gates, move and heading arrows, the route planner's
// hostiles column, and the alert zone drawn through the alarm's own function. Every name and id here is invented.
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

            var busy = IntelSnapshotBuilder.Build(Input(manyMoves: 40), new IntelCampRegister(), 3);
            var many = IntelMapView.Arrows(IntelMapView.Project(busy, Now));
            c.Check($"arrows: at most {IntelMapView.MaxArrows}, newest first",
                many.Count == IntelMapView.MaxArrows && many.Zip(many.Skip(1)).All(p => p.First.At >= p.Second.At), many.Count.ToString());

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
                condition.Contains("await WatchedSystemsAsync(config, _graph, ctx.DbFactory, ct)", StringComparison.Ordinal)
                && !condition.Contains("_graph.DistancesWithinAsync", StringComparison.Ordinal));
            c.Check("zone: the map's outline is drawn through the same function",
                zone.Contains("IntelCondition.WatchedSystemsAsync(", StringComparison.Ordinal));
        }
    }
}
