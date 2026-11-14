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
                PilotsFilter(c);
                FeedWho(c);
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
            var jumps2 = IntelViewModel.NextPilotSort(jumps1.Column, jumps1.Descending, IntelViewModel.PilotColumn.Jumps);
            var jumps3 = IntelViewModel.NextPilotSort(jumps2.Column, jumps2.Descending, IntelViewModel.PilotColumn.Jumps);
            c.Check("pilots: Jumps clicked again is farthest first, and a third time nearest first again",
                jumps2 == (IntelViewModel.PilotColumn.Jumps, true) && jumps3 == (IntelViewModel.PilotColumn.Jumps, false), $"{jumps2} {jumps3}");
        }

        /// <summary>
        /// The Pilots tab's standings filter: the Board's rule. The tier the nearest origin's character gives (the forced
        /// character first; every own character's friendliest when counting from a system or not reached yet); own
        /// characters never shown; corporation and alliance mates blue.
        /// </summary>
        private static void PilotsFilter(Checks c)
        {
            const long OtherAlly = 94_910_090, CorpMate = 94_910_091, AllianceMate = 94_910_092, Neutral = 94_910_093,
                       Friend = 94_910_094, Other = 93_910_002, OtherCorp = 98_910_090, MyOtherCorp = 98_910_091, FriendCorp = 98_910_092;
            IntelLastSeen Row(long id, long corp, long alliance, int system = Gang) => new()
            {
                CharacterId = id, Name = PilotName(id), SystemId = system, SeenAt = "2026-11-13T17:59:00Z", WrittenAt = "2026-11-13T17:59:00Z",
                CorporationId = corp, AllianceId = alliance == 0 ? null : alliance, Source = IntelLastSeenSource.Report,
            };
            var red = Row(OtherAlly, CorpA1, AllyA);
            var corpMate = Row(CorpMate, MyCorp, MyAlliance);
            var allianceMate = Row(AllianceMate, MyOtherCorp, MyAlliance);
            var own = Row(Me, MyCorp, MyAlliance);
            var neutral = Row(Neutral, CorpN, 0);
            var hostile = new HashSet<StandingTier>([StandingTier.Red, StandingTier.Orange, StandingTier.Neutral]);
            var fromMe = new IntelPilotFilter(Standings(), [(new IntelOrigin(Me, Home), Hops())], hostile);

            c.Check("pilots filter: a red is shown, a neutral too, with the chips as they start",
                fromMe.Keeps(red) && fromMe.Keeps(neutral) && fromMe.Place(red).Tier == StandingTier.Red);
            c.Check("pilots filter: a corporation mate and an alliance mate are blue, and hidden with Blue off",
                fromMe.Place(corpMate).Tier == StandingTier.Blue && fromMe.Place(allianceMate).Tier == StandingTier.Blue
                && !fromMe.Keeps(corpMate) && !fromMe.Keeps(allianceMate),
                $"{fromMe.Place(corpMate).Tier} {fromMe.Place(allianceMate).Tier}");
            c.Check("pilots filter: an own character is never listed", fromMe.Place(own).Tier == StandingTier.Own && !fromMe.Keeps(own));
            c.Check("pilots filter: an own character is left out as own, not hidden by a chip; a blue with Blue off is hidden",
                fromMe.Judge(own) == IntelPilotVerdict.Own && fromMe.Judge(corpMate) == IntelPilotVerdict.Hidden && fromMe.Judge(red) == IntelPilotVerdict.Shown);
            var blueOn = new IntelPilotFilter(Standings(), [(new IntelOrigin(Me, Home), Hops())], new HashSet<StandingTier>(hostile) { StandingTier.Blue });
            c.Check("pilots filter: Blue switched on shows them, the own character still not", blueOn.Keeps(corpMate) && blueOn.Keeps(allianceMate) && !blueOn.Keeps(own));
            c.Check("pilots filter: measured from the origin, from its character", fromMe.Place(red) is { Jumps: 1, Nearest: Me });

            // From a system, nobody views: every own character's friendliest. Two characters: a corporation red to one, blue
            // to the other, is blue; the character the setting forces decides.
            IntelStandings Two(long forced = 0) => new(
                [new IntelStandings.OwnCharacter(Me, "Pilot Qzx Home", MyCorp, MyAlliance), new IntelStandings.OwnCharacter(Other, "Pilot Qzx Other", OtherCorp, 0)],
                [MyCorp, OtherCorp], [(MyCorp, FriendCorp, -10.0), (OtherCorp, FriendCorp, 10.0)], forced);
            var friend = Row(Friend, FriendCorp, 0);
            var fromSystem = new IntelPilotFilter(Two(), [(new IntelOrigin(0, Home), Hops())], hostile);
            c.Check("pilots filter: from a system, the friendliest of the characters (blue to one): hidden with Blue off",
                fromSystem.Place(friend) is { Nearest: null, Tier: StandingTier.Blue } && !fromSystem.Keeps(friend),
                fromSystem.Place(friend).ToString());
            var forced = new IntelPilotFilter(Two(Me), [(new IntelOrigin(0, Home), Hops())], hostile);
            c.Check("pilots filter: the forced character wins (red to them): shown",
                forced.Place(friend).Tier == StandingTier.Red && forced.Keeps(friend), forced.Place(friend).ToString());
            var unreached = new IntelPilotFilter(Two(), [], hostile);
            c.Check("pilots filter: a row no walk reaches yet is coloured by the rule with no viewer",
                unreached.Place(Row(Friend, FriendCorp, 0)) is { Jumps: null, Nearest: null, Tier: StandingTier.Blue });
        }

        /// <summary>
        /// Every Feed line answers who: a kill's victim by portrait and name with their group's logo on it, and its
        /// attackers by group logo with counts, largest first, "+N", then their hulls; a report's pilots by portrait and
        /// name with their group's logo; a camp's campers by group. Victims stay out of the hostile counts. Narrow, the
        /// names go and the pictures and counts stay.
        /// </summary>
        private static void FeedWho(Checks c)
        {
            var snapshot = FeedSnapshot();
            var view = View(snapshot);
            IntelEventRowVm Line(Func<IntelFeedRow, bool> which) => new(view.Feed.First(which), snapshot, Now, false);
            string Says(IntelEventRowVm r) => string.Join(",", r.Pictures.Select(p => p.Kind switch
            {
                IntelPictureVm.Kinds.Pilot => "@" + p.Text + (p.Badge is { } b ? "[" + b.Kind + ":" + b.Id + "]" : ""),
                IntelPictureVm.Kinds.Alliance or IntelPictureVm.Kinds.Corporation => "#" + p.Kind + ":" + p.Id + p.CountText,
                IntelPictureVm.Kinds.Ship => "^" + p.Text,
                _ => p.Text,
            }));

            var kill = Line(r => r.Event.KillMailId == BrawlKill);
            var said = Says(kill);
            c.Check("feed who: a kill leads with its victim, their portrait with their alliance's logo on it, then their hull",
                kill.Pictures.Count > 2 && kill.Pictures[0] is { IsPilot: true, Id: BrawlVictim, Text: "Pilot Qzx 30" } v
                && v.Badge is { Kind: IntelPictureVm.Kinds.Alliance, Id: AllyB } && kill.Pictures[1] is { IsShip: true, Id: Lancer }
                && v.PictureTip == "Pilot Qzx 30\nQzx Thistle Pact", said);
            c.Check("feed who: lost to its attackers by group, largest first, each a logo with a count, then \"+1\" for the rest",
                said.Contains("lost to,#Alliance:" + AllyA + "×5,#Alliance:" + AllyC + "×2,+1,", StringComparison.Ordinal), said);
            c.Check("feed who: the groups' names are in the tooltips, the whole breakout on the first",
                kill.Pictures.First(p => p.Id == AllyA) is { IconOnly: true } a && a.PictureTip.StartsWith(IntelText.KillAttackersTip, StringComparison.Ordinal)
                && a.PictureTip.Contains("Qzx Lantern Alliance · 5") && a.PictureTip.Contains("Qzx Ember Union · 2") && a.PictureTip.Contains("Qzx Quiet Traders · 1")
                && kill.Pictures.First(p => p.Id == AllyC).PictureTip == "Qzx Ember Union · 2",
                kill.Pictures.First(p => p.Id == AllyA).PictureTip);
            c.Check("feed who: then the attackers' hulls, most flown first", said.EndsWith("^Raider QX ×5,^Lancer QX ×2,^Bastion QX", StringComparison.Ordinal), said);
            var brawl = view.Systems.Single(s => s.SystemId == Brawl);
            // A victim whose name no cache has: "Unknown pilot", the id only in the tooltip, never shown as a name.
            var killRow = view.Feed.First(r => r.Event.KillMailId == BrawlKill);
            var nameless = new IntelEventRowVm(killRow with { Event = killRow.Event with { VictimId = 94_910_013, VictimName = null } }, snapshot, Now, false);
            c.Check("feed who: a victim with no name known is \"Unknown pilot\", the id in the tooltip",
                nameless.Pictures[0] is { IsPilot: true, Id: 94_910_013 } u && u.Text == IntelText.UnknownPilot
                && u.PictureTip.Contains("94910013", StringComparison.Ordinal) && !Says(nameless).Contains("@94910013", StringComparison.Ordinal),
                Says(nameless));
            c.Check("feed who: the victim stays out of the hostile counts", brawl.Pilots.Count == 8 && brawl.Pilots.All(p => p.Pilot.CharacterId != BrawlVictim)
                && IntelHostiles.Of(brawl, snapshot, Now).Count == 8, brawl.Pilots.Count.ToString(CultureInfo.InvariantCulture));

            // Drawn: each picture, its name and its count apart; narrow, hull names go first, then group names (none here:
            // a group is its logo), then pilots'. Pictures, counts and words never.
            var tokens = kill.Tokens;
            c.Check("feed who: drawn as pieces: the victim's portrait, then their name, which may go",
                tokens[0] is { IsPicture: true, Drop: 0 } && tokens[1] is { IsName: true, Drop: 3, Label: "Pilot Qzx 30" });
            c.Check("feed who: a group is its logo and count, with no name piece",
                tokens.Where(t => t.Source.Id == AllyA).Select(t => t.Part).SequenceEqual([IntelTokenVm.Parts.Picture, IntelTokenVm.Parts.Count]));
            c.Check("feed who: a hull's name goes first, its icon and count stay",
                tokens.Where(t => t.Source is { IsShip: true, Id: Raider }).Select(t => (t.Part, t.Drop))
                    .SequenceEqual([(IntelTokenVm.Parts.Picture, 0), (IntelTokenVm.Parts.Name, 1), (IntelTokenVm.Parts.Count, 0)]));
            c.Check("feed who: words never go", tokens.Where(t => t.Source.IsWords).All(t => t.Drop == 0));

            // A report: each named pilot's portrait and name, their group's logo small on the portrait.
            var report = Line(r => r.Event.Kind == IntelEventKind.Report && r.Event.SystemId == Gang && r.Pilots.Any(p => p.Pilot.CharacterId == B1));
            var pilots = report.Pictures.Where(p => p.IsPilot).ToList();
            c.Check("feed who: a report's pilots each with their alliance's logo, or their corporation's with none",
                pilots.Count > 0 && pilots.All(p => p.Id == Unknown1 ? p.Badge is null
                    : p.Badge is { } b && (p.Id == C1 ? b is { Kind: IntelPictureVm.Kinds.Corporation, Id: CorpC }
                                                       : b.Kind == IntelPictureVm.Kinds.Alliance && b.Id == (p.Id == B1 ? AllyB : AllyA))),
                Says(report));

            // A camp: who is camping, by group, then what they fly.
            var camp = Line(r => r.Event.Kind == IntelEventKind.CampFormed);
            c.Check("feed who: a camp says who camps by group, then their hulls",
                camp.Pictures.Count > 1 && camp.Pictures[0] is { Kind: IntelPictureVm.Kinds.Corporation, Id: CorpC, CountText: "×2", IconOnly: true }
                && camp.Pictures.Skip(1).All(p => p.IsShip), Says(camp));
            c.Check("feed who: a line reading the same has the same signature",
                Line(r => r.Event.KillMailId == BrawlKill).Signature == kill.Signature);
        }
    }
}
