// Hostiles first: who is in a system and what they fly, as every intel list leads with it. The who summary (one pilot;
// alliances largest first; a corporation in no alliance by itself; a pilot not yet known in no group), the hulls (most
// flown first, capsules last), kill attackers counted and a kill's victim never; the panel's one row per system (a
// system's several reports and its kill are one row, a camp is one row saying its gate); the Pilots tab's sort. Every
// name and id here is invented (see HostileScene).
using System.Globalization;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;
using static EveConsole.Tools.IntelCheck.HostileScene;

namespace EveConsole.Tools.IntelCheck
{
    internal static class HostileChecks
    {
        public static void Run(Checks c)
        {
            var culture = CultureInfo.CurrentUICulture;
            var format = CultureInfo.CurrentCulture;
            CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo("en-US");
            try
            {
                Who(c);
                Flying(c);
                PanelRows(c);
                PilotSort(c);
            }
            finally { CultureInfo.CurrentUICulture = culture; CultureInfo.CurrentCulture = format; }
        }

        private static IntelView View(IntelSnapshot snapshot) =>
            IntelLens.Project(snapshot, new IntelViewer
            {
                Origins = [new IntelOrigin(Me, Home)], MaxJumps = 10, StandingsCharacterId = Me,
                SystemCap = PanelIntelVm.MaxRows, FeedCap = PanelIntelVm.MaxRows,
            }, Now, _ => Hops());

        private static string Groups(IntelSystemHostiles h) =>
            string.Join(",", h.Groups.Select(g => $"{g.Kind}:{g.Name}:{g.Count}"));

        private static void Who(Checks c)
        {
            var snapshot = Snapshot();
            var view = View(snapshot);
            IntelSystemHostiles Of(int system) => IntelHostiles.Of(view.Systems.Single(s => s.SystemId == system), snapshot, Now);

            // One pilot: that pilot, with their alliance.
            var lone = Of(Lone);
            c.Check("who: one pilot is that pilot", lone.Single?.CharacterId == Loner && lone.Count == 1, lone.Single?.Name ?? "none");
            c.Check("who: with their alliance as the one group", Groups(lone) == "Alliance:Qzx Thistle Pact:1", Groups(lone));
            var loneWho = new IntelWhoVm(lone, 2);
            c.Check("who: a single pilot's token is their portrait, with their alliance's logo small on it",
                loneWho.Tokens is [{ IsPilot: true, HasBadge: true, Name: "Pilot Qzx 08" } t] && t.Badge!.Kind == IntelPictureVm.Kinds.Alliance
                && t.Badge.Id == AllyB && t.Picture.PictureUrl!.Contains($"/characters/{Loner}/portrait"),
                string.Join(",", loneWho.Tokens.Select(x => x.Name + ":" + x.Badge?.Id)));

            // Several: by alliance, largest first; a corporation in no alliance is its own group; a pilot not known is in none.
            var gang = Of(Gang);
            c.Check("who: the predominant alliance first, with its count (four of seven)",
                gang.Groups.Count == 3 && gang.Groups[0] is { Kind: IntelWhoKind.Alliance, Id: AllyA, Name: "Qzx Lantern Alliance", Count: 4 },
                Groups(gang));
            c.Check("who: then the others, one each: an alliance, and a corporation in no alliance by itself",
                gang.Groups.Skip(1).Select(g => (g.Kind, g.Id)).ToHashSet().SetEquals([(IntelWhoKind.Alliance, AllyB), (IntelWhoKind.Corporation, CorpC)]),
                Groups(gang));
            c.Check("who: a pilot whose corporation is not known yet is counted, in no group",
                gang.Count == 7 && gang.Groups.Sum(g => g.Count) == 6, $"{gang.Count} {Groups(gang)}");
            var gangWho = new IntelWhoVm(gang, 2);
            c.Check("who: the line shows the two largest groups by logo and count, then \"+1\"",
                gangWho.Tokens.Count == 2 && gangWho.First is { IsPilot: false, CountText: "4", Name: "Qzx Lantern Alliance" }
                && gangWho.First.Picture.PictureUrl!.Contains($"/alliances/{AllyA}/logo") && gangWho.MoreText == "+1",
                string.Join(",", gangWho.Tokens.Select(x => x.Name + "×" + x.CountText)) + " " + gangWho.MoreText);
            c.Check("who: the tooltip has the whole breakout, and the pilot not known",
                gangWho.Tip is { } tip && tip.Contains("Qzx Lantern Alliance · 4") && tip.Contains("Qzx Thistle Pact · 1")
                && tip.Contains("Qzx Heron Holdings · 1") && tip.Contains(string.Format(CultureInfo.CurrentCulture, IntelText.WhoTipUnknown, 1)),
                gangWho.Tip ?? "none");

            // Corporations with no alliance: by corporation.
            var camp = Of(Camp);
            c.Check("who: pilots of a corporation in no alliance are grouped by it",
                Groups(camp) == "Corporation:Qzx Heron Holdings:2", Groups(camp));
            var campWho = new IntelWhoVm(camp, 2);
            c.Check("who: its logo is the corporation's", campWho.First?.Picture.PictureUrl?.Contains($"/corporations/{CorpC}/logo") == true,
                campWho.First?.Picture.PictureUrl ?? "none");

            // Kill attackers are hostiles who were there; a kill's victim is no hostile.
            var gangRow = view.Systems.Single(s => s.SystemId == Gang);
            c.Check("who: a kill's victim is not on the system's row", gangRow.Pilots.All(p => p.Pilot.CharacterId != Victim),
                string.Join(",", gangRow.Pilots.Select(p => p.Pilot.Name)));
            c.Check("who: nor counted among its hostiles", gang.Pilots.All(p => p.CharacterId != Victim) && gang.Count == 7,
                gang.Count.ToString(CultureInfo.InvariantCulture));
            c.Check("who: the kill's attackers are, marked as on the kill",
                gang.Pilots.Where(p => p.OnKill).Select(p => p.CharacterId).Order().SequenceEqual([A1, A2]),
                string.Join(",", gang.Pilots.Where(p => p.OnKill).Select(p => p.Name)));
            c.Check("who: the system shows a kill, with when", gang.Kills == 1 && gang.NewestKill == Now.AddMinutes(-1),
                $"{gang.Kills} {gang.NewestKill}");
            var victimOnly = IntelSnapshotBuilder.Build(Input() with
            {
                LastSeen = [.. Input().LastSeen.Where(r => r.SystemId != Gang || r.CharacterId == Victim)],
                Reports = [.. Input().Reports.Where(r => r.SystemId != Gang)],
                ReportPilots = [],
            }, new IntelCampRegister(), 1);
            c.Check("who: a system where only the victim is left is no system of hostiles",
                View(victimOnly).Systems.All(s => s.SystemId != Gang), string.Join(",", View(victimOnly).Systems.Select(s => s.SystemId)));
            var lonely = Of(Lone);
            c.Check("who: a system with nobody on a kill shows none", lonely.Kills == 0 && lonely.NewestKill is null);

            // The kill marker follows the view's filter: a view whose sources leave the kills out shows none.
            var noKills = IntelHostiles.Of(gangRow, snapshot, Now, killsShown: false);
            c.Check("who: a view that hides kills shows no kill marker", noKills.Kills == 0 && noKills.NewestKill is null && noKills.Count == 7,
                $"{noKills.Kills} {noKills.Count}");
            var channelsOnly = IntelLens.Project(snapshot, new IntelViewer
            {
                Origins = [new IntelOrigin(Me, Home)], MaxJumps = 10, StandingsCharacterId = Me,
                Sources = new HashSet<string>(snapshot.Feed.Select(e => e.SourceKey).Where(k => k != IntelSnapshot.SourceKills),
                                              StringComparer.OrdinalIgnoreCase),
            }, Now, _ => Hops());
            c.Check("who: a view whose sources leave out the kills says so; one with every source shows them",
                !channelsOnly.KillsShown && view.KillsShown);

            // The first who token is never dropped from a narrow line, so its tooltip is the whole breakout.
            c.Check("who: the first token's tooltip is the whole breakout", gangWho.First?.Tip == gangWho.Tip, gangWho.First?.Tip ?? "none");

            // One named among others only counted: no lone portrait beside a count of 16.
            var named = gang.Pilots.First(p => p.AllianceId == AllyA);
            var oneOfMany = new IntelSystemHostiles([named], 15, IntelHostiles.Groups([named], snapshot), [], 0, null);
            var oneOfManyWho = new IntelWhoVm(oneOfMany, 2);
            c.Check("who: one named among fifteen counted is not shown as one pilot, but by their alliance with its count",
                oneOfMany.Single is null && oneOfMany.Count == 16
                && oneOfManyWho.First is { IsPilot: false, CountText: "1" } && oneOfManyWho.First.Picture.Id == AllyA
                && oneOfManyWho.Tip!.Contains(string.Format(CultureInfo.CurrentCulture, IntelText.PlusUnnamed, 15)),
                oneOfManyWho.First?.Name + "×" + oneOfManyWho.First?.CountText);
            var unaffiliated = named with { CorporationId = 0, AllianceId = 0 };
            var unknownWho = new IntelWhoVm(new IntelSystemHostiles([unaffiliated], 15, [], [], 0, null), 2);
            c.Check("who: one named, not yet known, among others counted: their portrait carries a count",
                unknownWho.First is { IsPilot: true, CountText: "1" }, unknownWho.First?.Name + "×" + unknownWho.First?.CountText);

            // Only counted: the count, no who.
            var counted = Of(Counted);
            c.Check("who: a count with nobody named has no who", counted is { Count: 5, Unnamed: 5, Pilots.Count: 0, Groups.Count: 0 }
                && !new IntelWhoVm(counted, 2).HasTokens, $"{counted.Count} {counted.Unnamed}");
        }

        private static void Flying(Checks c)
        {
            var snapshot = Snapshot();
            var view = View(snapshot);
            IntelSystemHostiles Of(int system) => IntelHostiles.Of(view.Systems.Single(s => s.SystemId == system), snapshot, Now);
            string Hulls(IntelSystemHostiles h) => string.Join(",", h.Hulls.Select(x => $"{x.Name}×{x.Count}{(x.IsCapsule ? "(pod)" : "")}"));

            var gang = Of(Gang);
            c.Check("flying: the most flown hull first, then the next, the capsule last",
                Hulls(gang) == "Raider QX×4,Lancer QX×2,Capsule×1(pod)", Hulls(gang));
            c.Check("flying: the primary hull is the most flown", gang.PrimaryHull is { TypeId: Raider, Count: 4 });

            // Capsules count, but never lead, however many.
            IntelHostile P(long id, int ship) => new(id, PilotName(id), ship, TypeNames[ship], CorpA1, AllyA, Now, StandingTier.Red,
                                                     IntelWatchMark.None, false, false);
            var pods = IntelHostiles.Hulls([P(1, Capsule), P(2, Capsule), P(3, Capsule), P(4, Lancer)], null, snapshot);
            c.Check("flying: three capsules and a Lancer: the Lancer leads, the capsules still counted",
                pods is [{ TypeId: Lancer, Count: 1 }, { TypeId: Capsule, Count: 3, IsCapsule: true }],
                string.Join(",", pods.Select(h => $"{h.Name}×{h.Count}")));

            // Hulls said with a count, by name.
            var counted = Of(Counted);
            c.Check("flying: hulls said with a count are found by name", Hulls(counted) == "Bastion QX×5" && counted.PrimaryHull?.TypeId == Bastion,
                Hulls(counted));

            // The panel's line: icon and name of the most flown, ×n; the next by icon; the who; the kill first among the flags.
            var events = PanelIntelVm.SystemEvents(view);
            var line = new IntelSystemLineVm(view.Systems.Single(s => s.SystemId == Gang), snapshot, events[Gang], null, Now, false, IntelRowActions.None);
            c.Check("line: the count, then the primary hull by name and ×n",
                line is { CountText: "7", PrimaryHullName: "Raider QX", PrimaryHullCount: "×4" } && line.PrimaryHull!.PictureUrl!.Contains($"/types/{Raider}/icon"),
                $"{line.CountText} {line.PrimaryHullName} {line.PrimaryHullCount}");
            c.Check("line: the further hulls by icon, named in their tooltips",
                string.Join(",", line.OtherHulls.Select(h => h.Text)) == "Lancer QX ×2,Capsule", string.Join(",", line.OtherHulls.Select(h => h.Text)));
            c.Check("line: the kill is the first flag, its time in the tooltip",
                line.Flags.FirstOrDefault() is { Kind: IntelFlagKind.Kill } k && k.Tip == string.Format(CultureInfo.CurrentCulture, IntelText.FlagKill, "17:59"),
                string.Join(",", line.Flags.Select(f => f.Kind + ":" + f.Tip)));
            c.Check("line: the region under the system", line.RegionText == "Qzx Reach", line.RegionText);
            c.Check("line: opened, the pilots there, newest first, the victim not among them",
                line.Pilots.Count == 7 && line.Pilots.All(p => p.CharacterId != Victim) && line.Pilots[0].OnKill
                && line.Pilots.First(p => p.CharacterId == C1).Group?.Kind == IntelPictureVm.Kinds.Corporation,
                string.Join(",", line.Pilots.Select(p => p.Name)));

            c.Check("age: a compact badge, m:ss then minutes then hours",
                IntelWords.CompactAge(Now.AddSeconds(-53), Now) == "0:53" && IntelWords.CompactAge(Now.AddSeconds(-200), Now) == "3:20"
                && IntelWords.CompactAge(Now.AddMinutes(-12), Now) == "12m" && IntelWords.CompactAge(Now.AddMinutes(-125), Now) == "2h",
                IntelWords.CompactAge(Now.AddSeconds(-200), Now));
        }

        private static void PanelRows(Checks c)
        {
            var snapshot = Snapshot();
            var view = View(snapshot);
            var rows = PanelIntelVm.Lines(snapshot, view, Now, IntelRowActions.None);

            c.Check("panel: one row per system", rows.Select(r => r.SystemId).Distinct().Count() == rows.Count
                && rows.Select(r => r.SystemId).ToHashSet().SetEquals([Gang, Lone, Camp, Counted, Flags, Cleared]),
                string.Join(",", rows.Select(r => r.SystemName)));
            var gangLines = view.Feed.Count(r => r.Event.SystemId == Gang);
            var gang = rows.Single(r => r.SystemId == Gang);
            c.Check("panel: a system's two reports and its kill are one row, with all three as its details",
                gangLines == 3 && gang.Events.Count == 3 && gang.Events.Any(e => e.Kind == IntelEventKind.Kill),
                $"{gangLines} lines, {gang.Events.Count} details");
            c.Check("panel: nearest first", rows.Select(r => r.JumpsText).SequenceEqual(rows.OrderBy(r => int.Parse(r.JumpsText, CultureInfo.InvariantCulture)).Select(r => r.JumpsText)),
                string.Join(",", rows.Select(r => r.SystemName + ":" + r.JumpsText)));

            var camp = rows.Single(r => r.SystemId == Camp);
            c.Check("panel: a camp is one row that says camp and its gate",
                camp.GateText == "9Q-ZX2 gate" && camp.Flags.Any(f => f.Kind == IntelFlagKind.Camp && f.Tip.StartsWith("Gate camp on the 9Q-ZX2 gate", StringComparison.Ordinal)),
                camp.GateText + " | " + string.Join(",", camp.Flags.Select(f => f.Tip)));
            c.Check("panel: the camp's kills are not lines of their own beside the camp's",
                camp.Events.Count == 1 && camp.Events[0].Kind == IntelEventKind.CampFormed, string.Join(",", camp.Events.Select(e => e.Kind)));
            c.Check("panel: the camp's attackers are its hostiles, by their corporation", camp.CountText == "2" && camp.Who.First?.Name == "Qzx Heron Holdings",
                $"{camp.CountText} {camp.Who.First?.Name}");

            var counted = rows.Single(r => r.SystemId == Counted);
            c.Check("panel: a no-visual count reads as reported, with its flag", counted is { CountText: "5", IsNoVisual: true }
                && counted.Flags.Any(f => f.Kind == IntelFlagKind.NoVisual));
            var flags = rows.Single(r => r.SystemId == Flags);
            c.Check("panel: flags with nobody counted are icons, their words in the tooltips",
                !flags.HasCount && flags.Flags.Select(f => f.Kind).SequenceEqual([IntelFlagKind.Cyno, IntelFlagKind.Bubbles]),
                string.Join(",", flags.Flags.Select(f => f.Kind)));
            var cleared = rows.Single(r => r.SystemId == Cleared);
            c.Check("panel: a clear is its icon, never fresh", cleared.IsClear && cleared.Flags.Any(f => f.Kind == IntelFlagKind.Clear) && !cleared.IsFresh);
            c.Check("panel: the single line's system is the nearest with hostiles", view.Nearest?.SystemId == Gang, view.Nearest?.SystemId.ToString(CultureInfo.InvariantCulture) ?? "none");

            // A row built again from the same snapshot reads the same: the panel keeps the one on screen, pictures and all.
            var again = PanelIntelVm.Lines(snapshot, view, Now, IntelRowActions.None).Single(r => r.SystemId == Gang);
            c.Check("panel: a row reading the same has the same signature", again.Signature == gang.Signature);
        }

        private static void PilotSort(Checks c)
        {
            var snapshot = Snapshot();
            var hops = Hops();
            var seen = Input().LastSeen.Where(r => r.Source != IntelLastSeenSource.KillVictim).ToList();
            // One seen far away, where no walk reaches: jumps not known.
            seen.Add(new IntelLastSeen
            {
                CharacterId = 94_910_099, Name = "Pilot Qzx 99", SystemId = 39_910_099, SeenAt = "2026-11-13T17:30:00Z",
                Source = IntelLastSeenSource.Report, SourceRank = 1, WrittenAt = "2026-11-13T17:30:00Z",
            });
            var rows = seen.Select(r => new IntelPilotListRowVm(r, Names, snapshot,
                hops.TryGetValue(r.SystemId, out var h) ? h.Jumps : null, false, Me, false, IntelRowActions.None, Now)).ToList();

            IReadOnlyList<IntelPilotListRowVm> Sorted(IntelViewModel.PilotColumn col, bool desc) => [.. IntelViewModel.SortPilots(rows, col, desc)];
            bool Ordered<T>(IEnumerable<T> keys, IComparer<T> cmp, bool desc)
            {
                var list = keys.ToList();
                for (var i = 1; i < list.Count; i++)
                {
                    var o = cmp.Compare(list[i - 1], list[i]);
                    if (desc ? o < 0 : o > 0) return false;
                }
                return true;
            }
            var text = StringComparer.CurrentCultureIgnoreCase;

            var newest = Sorted(IntelViewModel.PilotColumn.Seen, true);
            c.Check("pilots: newest first by default", Ordered(newest.Select(r => r.SeenAt), Comparer<DateTimeOffset>.Default, true),
                string.Join(",", newest.Select(r => r.AgeText)));
            var byName = Sorted(IntelViewModel.PilotColumn.Name, false);
            c.Check("pilots: by name, A to Z", Ordered(byName.Select(r => r.Name), text, false), string.Join(",", byName.Select(r => r.Name)));
            var byNameDown = Sorted(IntelViewModel.PilotColumn.Name, true);
            c.Check("pilots: and Z to A", byNameDown.Select(r => r.Name).SequenceEqual(byName.Select(r => r.Name).Reverse()));
            var byJumps = Sorted(IntelViewModel.PilotColumn.Jumps, false);
            c.Check("pilots: by jumps, nearest first, the unknown last",
                Ordered(byJumps.Where(r => r.Jumps is not null).Select(r => r.Jumps!.Value), Comparer<int>.Default, false) && byJumps[^1].Jumps is null,
                string.Join(",", byJumps.Select(r => r.JumpsText)));
            var byJumpsDown = Sorted(IntelViewModel.PilotColumn.Jumps, true);
            c.Check("pilots: farthest first, the unknown still last", byJumpsDown[0].Jumps == byJumps.Where(r => r.Jumps is not null).Max(r => r.Jumps)
                && byJumpsDown[^1].Jumps is null, string.Join(",", byJumpsDown.Select(r => r.JumpsText)));
            var bySystem = Sorted(IntelViewModel.PilotColumn.System, false);
            c.Check("pilots: by system", Ordered(bySystem.Select(r => r.SystemName), text, false), string.Join(",", bySystem.Select(r => r.SystemName)));
            var byShip = Sorted(IntelViewModel.PilotColumn.Ship, false);
            c.Check("pilots: by ship, no hull last", Ordered(byShip.Where(r => r.HasHull).Select(r => r.Hull!.Text), text, false) && !byShip[^1].HasHull,
                string.Join(",", byShip.Select(r => r.Hull?.Text ?? "-")));
            var byCorp = Sorted(IntelViewModel.PilotColumn.Corporation, false);
            c.Check("pilots: by corporation, none last",
                Ordered(byCorp.Where(r => r.HasCorporation).Select(r => r.Corporation!.Text), text, false) && !byCorp[^1].HasCorporation,
                string.Join(",", byCorp.Select(r => r.Corporation?.Text ?? "-")));
            var byAlliance = Sorted(IntelViewModel.PilotColumn.Alliance, true);
            c.Check("pilots: by alliance Z to A, none still last",
                Ordered(byAlliance.Where(r => r.HasAlliance).Select(r => r.Alliance!.Text), text, true) && !byAlliance[^1].HasAlliance
                && byAlliance.SkipWhile(r => r.HasAlliance).All(r => !r.HasAlliance),
                string.Join(",", byAlliance.Select(r => r.Alliance?.Text ?? "-")));
            var byHow = Sorted(IntelViewModel.PilotColumn.How, false);
            c.Check("pilots: by how they were seen", Ordered(byHow.Select(r => r.SourceText), text, false), string.Join(",", byHow.Select(r => r.SourceText)));
            c.Check("pilots: alike in the column, newest first",
                byHow.GroupBy(r => r.SourceText).All(g => Ordered(g.Select(r => r.SeenAt), Comparer<DateTimeOffset>.Default, true)));

            var s = (IntelViewModel.PilotColumn.Seen, true);
            var name1 = IntelViewModel.NextPilotSort(s.Item1, s.Item2, IntelViewModel.PilotColumn.Name);
            var name2 = IntelViewModel.NextPilotSort(name1.Column, name1.Descending, IntelViewModel.PilotColumn.Name);
            var seen1 = IntelViewModel.NextPilotSort(name2.Column, name2.Descending, IntelViewModel.PilotColumn.Seen);
            var jumps1 = IntelViewModel.NextPilotSort(seen1.Column, seen1.Descending, IntelViewModel.PilotColumn.Jumps);
            c.Check("pilots: a header clicked sorts by it A to Z; again, Z to A; Seen starts newest first; Jumps nearest first",
                name1 == (IntelViewModel.PilotColumn.Name, false) && name2 == (IntelViewModel.PilotColumn.Name, true)
                && seen1 == (IntelViewModel.PilotColumn.Seen, true) && jumps1 == (IntelViewModel.PilotColumn.Jumps, false),
                $"{name1} {name2} {seen1} {jumps1}");
        }
    }
}
