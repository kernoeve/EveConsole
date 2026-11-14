// The Intel tool's grids, headless, as a user clicks them: the Pilots tab hides the standings tiers switched off;
// on Pilots, Camps and the Watch list every header sorts its column, the second click turns it round, a refresh keeps
// the direction and the rows that did not change, and the choice is kept on this PC; the long lists (Pilots, Feed) are
// virtualized, so a click or a refresh lays out only the rows in sight. The Board sorts from its own choice.
//
// Every name and id here is invented (see HostileScene).
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.ReactiveUI;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;
using EveConsole.Views;
using static EveConsole.Tools.IntelCheck.HostileScene;

namespace EveConsole.Tools.IntelCheck
{
    internal static class ToolGridChecks
    {
        private static bool _setUp;

        public static void Run(Checks c)
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var format = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            EveImageCache.Offline = true;
            try
            {
                if (!_setUp)
                {
                    // Headless drawing: no fonts or graphics library needed, so it runs on any CI runner; layout is real.
                    // ⚠️ ReactiveUI on the UI thread, as the app has it: without it a command's "can execute" is raised
                    // on a pool thread, and a button reading it there throws.
                    AppBuilder.Configure<App>()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
                        .UseReactiveUI()
                        .SetupWithoutStarting();
                    _setUp = true;
                }
                Grids(c);
                KillLineClicks(c);
                CampsAndWatch(c);
                ChannelLabelChecks.SavedSelection(c);
                PasteTabChecks.Run(c);
            }
            catch (Exception ex)
            {
                c.Check("tool grids: ran headless", false, ex.GetBaseException().Message);
            }
            finally
            {
                EveImageCache.Offline = false;
                System.Globalization.CultureInfo.CurrentUICulture = culture;
                System.Globalization.CultureInfo.CurrentCulture = format;
            }
        }

        private static void Grids(Checks c)
        {
            var distances = new IntelDistanceService(Gates.ToDictionary(kv => kv.Key, kv => kv.Value), IntelDistanceService.Edges.None);
            distances.JumpsFromAsync(Home, IntelGraphMode.Threat).GetAwaiter().GetResult();
            var characters = new ObservableCollection<Character> { new() { Id = Me, Name = "Pilot Qzx Home" } };
            var tool = new IntelViewModel(null, distances, null, null, null, characters) { Clock = () => Now };
            var view = new IntelToolView { DataContext = tool };
            var window = new Window { Width = 1500, Height = 760, Content = view };
            window.Show();
            try
            {
                tool.Apply(FeedSnapshot());
                tool.SelectedTab = IntelViewModel.TabPilots;
                Pump(() => tool.BoardRows.Count > 0);
                foreach (var t in new[] { StandingTier.Red, StandingTier.Orange, StandingTier.Neutral, StandingTier.LightBlue, StandingTier.Blue })
                    SetTier(tool, t, t is StandingTier.Red or StandingTier.Orange or StandingTier.Neutral);

                // Hostiles, a corporation mate, an alliance mate and the user's own character, all seen lately.
                const long CorpMate = 94_910_091, AllianceMate = 94_910_092;
                IntelLastSeen Row(long id, long corp, long alliance, int system, double minutes) => new()
                {
                    CharacterId = id, Name = PilotName(id), SystemId = system, SeenAt = Iso(Now.AddMinutes(-minutes)), WrittenAt = Iso(Now.AddMinutes(-minutes)),
                    CorporationId = corp, AllianceId = alliance == 0 ? null : alliance, Source = IntelLastSeenSource.Report,
                };
                var rows = FeedInput().LastSeen.Where(r => r.Source != IntelLastSeenSource.KillVictim).ToList();
                rows.Add(Row(CorpMate, MyCorp, MyAlliance, Lone, 0.2));
                rows.Add(Row(AllianceMate, 98_910_091, MyAlliance, Camp, 0.3));
                rows.Add(Row(Me, MyCorp, MyAlliance, Home, 0.1));
                tool.UsePilots(new IntelPilotResults(rows, FeedInput().Names));
                Settle(window, tool);

                var shown = tool.PilotRows.Select(r => r.CharacterId).ToHashSet();
                c.Check("tool grids: Pilots hides the blues (a corporation mate, an alliance mate) and the user's own character with Blue off",
                    !shown.Contains(CorpMate) && !shown.Contains(AllianceMate) && !shown.Contains(Me) && shown.Contains(A1),
                    $"{tool.PilotRows.Count} rows: {string.Join(",", tool.PilotRows.Select(r => r.Name))}");
                c.Check("tool grids: and says how many the chips hid, the user's own character not among them",
                    tool.PilotsCountText.Contains("2 hidden by standings", StringComparison.Ordinal), tool.PilotsCountText);
                c.Check("tool grids: the standings chips show on the Pilots tab", tool.ShowsTierFilters && !tool.ShowsAreaFilters);

                SetTier(tool, StandingTier.Blue, true);
                Settle(window, tool);
                shown = [.. tool.PilotRows.Select(r => r.CharacterId)];
                c.Check("tool grids: Blue switched on lists them; the user's own character never",
                    shown.Contains(CorpMate) && shown.Contains(AllianceMate) && !shown.Contains(Me), tool.PilotsCountText);
                SetTier(tool, StandingTier.LightBlue, true);
                Settle(window, tool);
                c.Check("tool grids: every chip on, nothing counted hidden (the user's own character is not)",
                    !tool.PilotsCountText.Contains("hidden", StringComparison.Ordinal), tool.PilotsCountText);

                // Every chip on and only the user's own character read: no hidden count, and not "switch on more standings".
                tool.UsePilots(new IntelPilotResults([Row(Me, MyCorp, MyAlliance, Home, 0.1)], FeedInput().Names));
                Settle(window, tool);
                c.Check("tool grids: only the user's own character, every chip on: no hidden count and no \"switch on more standings\"",
                    tool.PilotRows.Count == 0 && !tool.PilotsCountText.Contains("hidden", StringComparison.Ordinal)
                    && tool.PilotsEmptyText != IntelText.PilotsAllHidden && tool.PilotsEmptyText.Length > 0,
                    $"count '{tool.PilotsCountText}', empty '{tool.PilotsEmptyText}'");
                tool.UsePilots(new IntelPilotResults(rows, FeedInput().Names));
                SetTier(tool, StandingTier.LightBlue, false);
                SetTier(tool, StandingTier.Blue, false);
                Settle(window, tool);

                // The lists lay out only the rows in sight.
                var pilotList = view.GetVisualDescendants().OfType<ItemsControl>().First(i => ReferenceEquals(i.ItemsSource, tool.PilotRows));
                c.Check("tool grids: the Pilots list is virtualized", pilotList.ItemsPanelRoot is VirtualizingStackPanel,
                    pilotList.ItemsPanelRoot?.GetType().Name ?? "no panel");

                // Every header: first click, second click (turned round), and a refresh keeps the direction.
                var heads = Heads<IntelViewModel.PilotColumn>(view);
                c.Check("tool grids: a header for each Pilots column", heads.Count == Enum.GetValues<IntelViewModel.PilotColumn>().Length, heads.Count.ToString());
                var failures = new List<string>();
                var version = 10L;
                foreach (var column in Enum.GetValues<IntelViewModel.PilotColumn>())
                {
                    var head = heads.First(b => b.DataContext is IntelSortHeaderVm<IntelViewModel.PilotColumn> h && h.Column == column);
                    Click(window, head);
                    Settle(window, tool);
                    var first = tool.PilotSortDescending;
                    var firstOk = Sorted(tool) && tool.PilotSort == column;
                    Click(window, head);
                    Settle(window, tool);
                    var second = tool.PilotSortDescending;
                    var secondOk = Sorted(tool) && tool.PilotSort == column && second == !first && Arrow(tool, column);

                    // A refresh (a new snapshot): the same column, the same way round.
                    tool.Apply(IntelSnapshotBuilder.Build(FeedInput(), new IntelCampRegister(), ++version));
                    Settle(window, tool);
                    var refreshedOk = tool.PilotSort == column && tool.PilotSortDescending == second && Sorted(tool) && Arrow(tool, column);
                    if (!firstOk || !secondOk || !refreshedOk)
                        failures.Add($"{column}: first {(first ? "desc" : "asc")} {firstOk}, second {(second ? "desc" : "asc")} {secondOk}, after a refresh {refreshedOk}");
                }
                c.Check("tool grids: every Pilots header sorts by its column, the second click turns it round, a refresh keeps it",
                    failures.Count == 0, string.Join(" | ", failures));
                Console.WriteLine($"Tool grids: {heads.Count} Pilots headers clicked twice each, then refreshed; {tool.PilotRows.Count} pilots shown, {tool.PilotsCountText}");

                // Jumps by name: nearest first, then farthest first.
                var jumps = heads.First(b => b.DataContext is IntelSortHeaderVm<IntelViewModel.PilotColumn> { Column: IntelViewModel.PilotColumn.Jumps });
                if (tool.PilotSort == IntelViewModel.PilotColumn.Jumps) Click(window, heads.First(b => b.DataContext is IntelSortHeaderVm<IntelViewModel.PilotColumn> { Column: IntelViewModel.PilotColumn.Name }));
                Settle(window, tool);
                Click(window, jumps);
                Settle(window, tool);
                var near = tool.PilotRows.Where(r => r.Jumps is not null).Select(r => r.Jumps!.Value).ToList();
                Click(window, jumps);
                Settle(window, tool);
                var far = tool.PilotRows.Where(r => r.Jumps is not null).Select(r => r.Jumps!.Value).ToList();
                c.Check("tool grids: Jumps clicked is nearest first; clicked again, farthest first",
                    near.Count > 1 && near.SequenceEqual(near.Order()) && far.SequenceEqual(far.OrderDescending()) && far[0] > near[0],
                    $"{string.Join(",", near)} / {string.Join(",", far)}");

                // The Feed too.
                tool.SelectedTab = IntelViewModel.TabFeed;
                Settle(window, tool);
                var feedList = view.GetVisualDescendants().OfType<ItemsControl>().First(i => ReferenceEquals(i.ItemsSource, tool.FeedRows));
                c.Check("tool grids: the Feed is virtualized", feedList.ItemsPanelRoot is VirtualizingStackPanel,
                    feedList.ItemsPanelRoot?.GetType().Name ?? "no panel");
                var line = tool.FeedRows.OfType<IntelFeedLineVm>().FirstOrDefault();
                tool.Apply(IntelSnapshotBuilder.Build(FeedInput(), new IntelCampRegister(), ++version));
                Settle(window, tool);
                c.Check("tool grids: a Feed line reading the same after a refresh is the very line on screen",
                    line is not null && tool.FeedRows.OfType<IntelFeedLineVm>().Contains(line));
            }
            finally
            {
                window.Close();
                tool.Dispose();
            }
        }

        /// <summary>
        /// The Camps tab and the Watch list, as a user clicks them (the user's rule, 2026-11-13: on any grid, clicking a
        /// column a second time turns the sort round): every header sorts by its column, the second click turns it
        /// round, a click keeps the very rows (only their order changes), a refresh keeps the column, the direction and
        /// the rows that did not change, and the choice is kept on this PC for the next time the tool opens.
        /// </summary>
        private static void CampsAndWatch(Checks c)
        {
            var distances = new IntelDistanceService(Gates.ToDictionary(kv => kv.Key, kv => kv.Value), IntelDistanceService.Edges.None);
            distances.JumpsFromAsync(Home, IntelGraphMode.Threat).GetAwaiter().GetResult();
            var characters = new ObservableCollection<Character> { new() { Id = Me, Name = "Pilot Qzx Home" } };
            var tool = new IntelViewModel(null, distances, null, null, null, characters) { Clock = () => Now };
            var view = new IntelToolView { DataContext = tool };
            var window = new Window { Width = 1500, Height = 760, Content = view };
            window.Show();
            var version = 100L;
            try
            {
                // ── Camps ──
                tool.Apply(IntelSnapshotBuilder.Build(CampsInput(), new IntelCampRegister(), ++version));
                tool.SelectedTab = IntelViewModel.TabCamps;
                Pump(() => tool.CampRows.Count >= 4);
                Settle(window, tool);
                var campCount = tool.CampRows.Count;
                c.Check("tool grids: the Camps scene has a camp from kills and camps only reported, active and ended",
                    campCount >= 4 && tool.CampRows.Any(r => r.KillCount is not null) && tool.CampRows.Any(r => r.KillCount is null),
                    $"{campCount}: {string.Join(", ", tool.CampRows.Select(r => $"{r.SystemName} {r.JumpsText} {r.StateText} {r.KillsText}"))}");
                c.Check("tool grids: Camps opens active first, then nearest (the tab's own order before headers sorted it)",
                    tool.CampSort == IntelViewModel.CampColumn.Status && !tool.CampSortDescending && CampsSorted(tool)
                    && CampArrow(tool, IntelViewModel.CampColumn.Status),
                    $"{tool.CampSort} {(tool.CampSortDescending ? "desc" : "asc")}");

                var heads = Heads<IntelViewModel.CampColumn>(view);
                c.Check("tool grids: a header for each Camps column", heads.Count == Enum.GetValues<IntelViewModel.CampColumn>().Length, heads.Count.ToString());
                var failures = new List<string>();
                foreach (var column in Enum.GetValues<IntelViewModel.CampColumn>())
                {
                    var head = heads.First(b => b.DataContext is IntelSortHeaderVm<IntelViewModel.CampColumn> h && h.Column == column);
                    var before = tool.CampRows.ToHashSet();
                    // Newest, most or nearest first, active first, else A to Z; the column already sorting turns round.
                    var expected = IntelViewModel.NextCampSort(tool.CampSort, tool.CampSortDescending, column).Descending;
                    Click(window, head);
                    Settle(window, tool);
                    var first = tool.CampSortDescending;
                    var firstOk = CampsSorted(tool) && tool.CampSort == column && first == expected && CampArrow(tool, column)
                                  && before.SetEquals(tool.CampRows);
                    Click(window, head);
                    Settle(window, tool);
                    var second = tool.CampSortDescending;
                    var secondOk = CampsSorted(tool) && tool.CampSort == column && second == !first && CampArrow(tool, column)
                                   && before.SetEquals(tool.CampRows);

                    // A refresh with one camp more: the same column, the same way round, and every camp that read the
                    // same is the very row on screen (its pictures not fetched again). Then back.
                    var held = tool.CampRows.ToDictionary(r => r.Key);
                    tool.Apply(IntelSnapshotBuilder.Build(CampsInput(more: true), new IntelCampRegister(), ++version));
                    Pump(() => tool.CampRows.Count == campCount + 1);
                    Settle(window, tool);
                    var keptOk = tool.CampRows.Count == campCount + 1
                                 && held.Values.All(r => tool.CampRows.Any(n => ReferenceEquals(n, r)));
                    var refreshedOk = keptOk && tool.CampSort == column && tool.CampSortDescending == second && CampsSorted(tool) && CampArrow(tool, column);
                    tool.Apply(IntelSnapshotBuilder.Build(CampsInput(), new IntelCampRegister(), ++version));
                    Pump(() => tool.CampRows.Count == campCount);
                    Settle(window, tool);
                    if (!firstOk || !secondOk || !refreshedOk)
                        failures.Add($"{column}: first {(first ? "desc" : "asc")} {firstOk}, second {(second ? "desc" : "asc")} {secondOk}, "
                                   + $"after a refresh {refreshedOk} (rows kept {keptOk})");
                }
                c.Check("tool grids: every Camps header sorts by its column, the second click turns it round, a refresh keeps it and the rows",
                    failures.Count == 0, string.Join(" | ", failures));

                // An identical refresh changes nothing on screen: the very same rows, in the same order.
                var order = tool.CampRows.ToList();
                tool.Apply(IntelSnapshotBuilder.Build(CampsInput(), new IntelCampRegister(), ++version));
                Settle(window, tool, quietMs: 600);
                c.Check("tool grids: a Camps refresh that changed nothing keeps every row, in order",
                    order.SequenceEqual(tool.CampRows), $"{order.Count} → {tool.CampRows.Count}");

                // Jumps by name: nearest first, then farthest first.
                var jumps = heads.First(b => b.DataContext is IntelSortHeaderVm<IntelViewModel.CampColumn> { Column: IntelViewModel.CampColumn.Jumps });
                if (tool.CampSort == IntelViewModel.CampColumn.Jumps) Click(window, heads.First(b => b.DataContext is IntelSortHeaderVm<IntelViewModel.CampColumn> { Column: IntelViewModel.CampColumn.Camp }));
                Settle(window, tool);
                Click(window, jumps);
                Settle(window, tool);
                var near = tool.CampRows.Where(r => r.Jumps is not null).Select(r => r.Jumps!.Value).ToList();
                Click(window, jumps);
                Settle(window, tool);
                var far = tool.CampRows.Where(r => r.Jumps is not null).Select(r => r.Jumps!.Value).ToList();
                c.Check("tool grids: Camps Jumps clicked is nearest first; clicked again, farthest first",
                    near.Count > 1 && near.SequenceEqual(near.Order()) && far.SequenceEqual(far.OrderDescending()) && far[0] > near[0],
                    $"{string.Join(",", near)} / {string.Join(",", far)}");
                Console.WriteLine($"Tool grids: {heads.Count} Camps headers clicked twice each, then refreshed; {tool.CampRows.Count} camps, {tool.CampsCountText}");

                // ── The Watch list ──
                tool.SelectedTab = IntelViewModel.TabWatch;
                Settle(window, tool);
                var entries = WatchEntries();
                tool.WatchList.Show(entries);
                Settle(window, tool);
                c.Check("tool grids: the Watch list opens in its own order: by kind, then name",
                    tool.WatchList.Sort == IntelWatchTabVm.WatchColumn.Kind && !tool.WatchList.SortDescending && WatchSorted(tool.WatchList)
                    && WatchArrow(tool.WatchList, IntelWatchTabVm.WatchColumn.Kind),
                    string.Join(", ", tool.WatchList.Entries.Select(e => e.Label)));

                var watchHeads = Heads<IntelWatchTabVm.WatchColumn>(view);
                c.Check("tool grids: a header for each Watch list column", watchHeads.Count == Enum.GetValues<IntelWatchTabVm.WatchColumn>().Length,
                    watchHeads.Count.ToString());
                failures.Clear();
                foreach (var column in Enum.GetValues<IntelWatchTabVm.WatchColumn>())
                {
                    var head = watchHeads.First(b => b.DataContext is IntelSortHeaderVm<IntelWatchTabVm.WatchColumn> h && h.Column == column);
                    var before = tool.WatchList.Entries.ToHashSet();
                    var expected = IntelWatchTabVm.NextWatchSort(tool.WatchList.Sort, tool.WatchList.SortDescending, column).Descending;
                    Click(window, head);
                    Settle(window, tool);
                    var first = tool.WatchList.SortDescending;
                    var firstOk = WatchSorted(tool.WatchList) && tool.WatchList.Sort == column && first == expected
                                  && WatchArrow(tool.WatchList, column) && before.SetEquals(tool.WatchList.Entries);
                    Click(window, head);
                    Settle(window, tool);
                    var second = tool.WatchList.SortDescending;
                    var secondOk = WatchSorted(tool.WatchList) && tool.WatchList.Sort == column && second == !first
                                   && WatchArrow(tool.WatchList, column) && before.SetEquals(tool.WatchList.Entries);

                    // Read again, the same entries: the same column and way round, the very rows, in the same order.
                    var order2 = tool.WatchList.Entries.ToList();
                    tool.WatchList.Show(WatchEntries());
                    Settle(window, tool);
                    var refreshedOk = tool.WatchList.Sort == column && tool.WatchList.SortDescending == second
                                      && order2.SequenceEqual(tool.WatchList.Entries) && WatchArrow(tool.WatchList, column);
                    if (!firstOk || !secondOk || !refreshedOk)
                        failures.Add($"{column}: first {(first ? "desc" : "asc")} {firstOk}, second {(second ? "desc" : "asc")} {secondOk}, after a read {refreshedOk}");
                }
                c.Check("tool grids: every Watch list header sorts by its column, the second click turns it round, a read again keeps it and the rows",
                    failures.Count == 0, string.Join(" | ", failures));

                // One entry changed (its note): it alone is a new row; the rest are the very rows shown.
                var heldEntries = tool.WatchList.Entries.ToDictionary(e => e.Id);
                var changed = WatchEntries();
                changed[0].Note = "qzx changed";
                tool.WatchList.Show(changed);
                Settle(window, tool);
                var replaced = tool.WatchList.Entries.Where(e => !ReferenceEquals(heldEntries[e.Id], e)).Select(e => e.Id).ToList();
                c.Check("tool grids: a Watch entry changed is replaced alone, the others kept (pictures not fetched again)",
                    replaced.SequenceEqual([changed[0].Id]) && WatchSorted(tool.WatchList), string.Join(",", replaced));

                // An entry added (the list laid out again): every entry already shown is still the very row.
                heldEntries = tool.WatchList.Entries.ToDictionary(e => e.Id);
                changed.Add(new() { Id = 7, Kind = IntelWatchKind.Pilot, EntityId = B1, Name = PilotName(B1), Mode = IntelWatchMode.Always,
                                    Note = "qzx new", AddedBy = "QZX-PC", AddedAt = "2026-11-13T10:00:00Z" });
                tool.WatchList.Show(changed);
                Settle(window, tool);
                c.Check("tool grids: a Watch entry added keeps every entry already shown, sorted the same way",
                    tool.WatchList.Entries.Count == heldEntries.Count + 1 && WatchSorted(tool.WatchList)
                    && heldEntries.Values.All(e => tool.WatchList.Entries.Any(n => ReferenceEquals(n, e))),
                    $"{tool.WatchList.Entries.Count} entries, sorted {WatchSorted(tool.WatchList)} by {tool.WatchList.Sort}, new rows for "
                    + string.Join(",", heldEntries.Values.Where(e => !tool.WatchList.Entries.Any(n => ReferenceEquals(n, e))).Select(e => e.Id)));
                Console.WriteLine($"Tool grids: {watchHeads.Count} Watch list headers clicked twice each, then read again; {tool.WatchList.Entries.Count} entries");

                // ── Kept on this PC ──
                var (campSort, campDescending) = (tool.CampSort, tool.CampSortDescending);
                var (watchSort, watchDescending) = (tool.WatchList.Sort, tool.WatchList.SortDescending);
                var again = new IntelViewModel(null, distances, null, null, null, characters) { Clock = () => Now };
                try
                {
                    c.Check("tool grids: the Camps and Watch list sorts are kept on this PC: the tool opened again sorts the same way",
                        again.CampSort == campSort && again.CampSortDescending == campDescending
                        && again.WatchList.Sort == watchSort && again.WatchList.SortDescending == watchDescending
                        && again.CampHeaders.Single(h => h.IsSorted).Column == campSort
                        && again.WatchList.Headers.Single(h => h.IsSorted).Column == watchSort,
                        $"camps {campSort}/{campDescending} → {again.CampSort}/{again.CampSortDescending}; "
                        + $"watch {watchSort}/{watchDescending} → {again.WatchList.Sort}/{again.WatchList.SortDescending}");
                }
                finally
                {
                    again.Dispose();
                }
            }
            finally
            {
                window.Close();
                tool.Dispose();
            }
        }

        /// <summary>Invented entries: two pilots (one with no note), a corporation, an alliance, a hull and a group,
        /// added on different days, both modes.</summary>
        private static List<IntelWatchEntry> WatchEntries() =>
        [
            new() { Id = 1, Kind = IntelWatchKind.Pilot, EntityId = A1, Name = PilotName(A1), Mode = IntelWatchMode.Always, Note = "qzx scout", AddedBy = "QZX-PC", AddedAt = "2026-11-02T10:00:00Z" },
            new() { Id = 2, Kind = IntelWatchKind.Pilot, EntityId = Loner, Name = PilotName(Loner), Mode = IntelWatchMode.Ignore, Note = null, AddedBy = "QZX-PC", AddedAt = "2026-11-09T10:00:00Z" },
            new() { Id = 3, Kind = IntelWatchKind.Corporation, EntityId = CorpC, Name = Names[CorpC], Mode = IntelWatchMode.Always, Note = "campers", AddedBy = "QZX-LAPTOP", AddedAt = "2026-11-05T10:00:00Z" },
            new() { Id = 4, Kind = IntelWatchKind.Alliance, EntityId = AllyA, Name = Names[AllyA], Mode = IntelWatchMode.Always, Note = "alpha", AddedBy = "QZX-PC", AddedAt = "2026-11-12T10:00:00Z" },
            new() { Id = 5, Kind = IntelWatchKind.Hull, EntityId = Bastion, Name = TypeNames[Bastion], Mode = IntelWatchMode.Ignore, Note = "bait", AddedBy = "QZX-PC", AddedAt = "2026-11-01T10:00:00Z" },
            new() { Id = 6, Kind = IntelWatchKind.Group, EntityId = 25_910, Name = "Qzx Interdictor Group", Mode = IntelWatchMode.Always, Note = "", AddedBy = "QZX-PC", AddedAt = "2026-11-07T10:00:00Z" },
        ];

        /// <summary>The sort headers of one grid's columns, as drawn.</summary>
        private static List<Button> Heads<TColumn>(IntelToolView view) where TColumn : struct, Enum =>
            [.. view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("sortHead") && b.DataContext is IntelSortHeaderVm<TColumn>)];

        private static bool CampsSorted(IntelViewModel tool) =>
            tool.CampRows.Count > 1
            && IntelViewModel.SortCamps(tool.CampRows.ToList(), tool.CampSort, tool.CampSortDescending).Select(r => r.Key)
                .SequenceEqual(tool.CampRows.Select(r => r.Key));

        private static bool CampArrow(IntelViewModel tool, IntelViewModel.CampColumn column) =>
            tool.CampHeaders.Count(h => h.IsSorted) == 1
            && tool.CampHeaders.Single(h => h.IsSorted) is { } h && h.Column == column
            && h.IsDown == tool.CampSortDescending && h.IsUp == !tool.CampSortDescending;

        private static bool WatchSorted(IntelWatchTabVm watch) =>
            watch.Entries.Count > 1
            && IntelWatchTabVm.SortWatch(watch.Entries.ToList(), watch.Sort, watch.SortDescending).Select(e => e.Id)
                .SequenceEqual(watch.Entries.Select(e => e.Id));

        private static bool WatchArrow(IntelWatchTabVm watch, IntelWatchTabVm.WatchColumn column) =>
            watch.Headers.Count(h => h.IsSorted) == 1
            && watch.Headers.Single(h => h.IsSorted) is { } h && h.Column == column
            && h.IsDown == watch.SortDescending && h.IsUp == !watch.SortDescending;

        private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        private static void SetTier(IntelViewModel tool, StandingTier tier, bool on)
        {
            switch (tier)
            {
                case StandingTier.Red:       tool.ShowRed = on; break;
                case StandingTier.Orange:    tool.ShowOrange = on; break;
                case StandingTier.Neutral:   tool.ShowNeutral = on; break;
                case StandingTier.LightBlue: tool.ShowLightBlue = on; break;
                case StandingTier.Blue:      tool.ShowBlue = on; break;
            }
        }

        /// <summary>The rows in the order the sort says, and only that column's header with an arrow, the right way.</summary>
        private static bool Sorted(IntelViewModel tool) =>
            tool.PilotRows.Count > 1
            && IntelViewModel.SortPilots(tool.PilotRows.ToList(), tool.PilotSort, tool.PilotSortDescending).Select(r => r.CharacterId)
                .SequenceEqual(tool.PilotRows.Select(r => r.CharacterId));

        private static bool Arrow(IntelViewModel tool, IntelViewModel.PilotColumn column) =>
            tool.PilotHeaders.Count(h => h.IsSorted) == 1
            && tool.PilotHeaders.Single(h => h.IsSorted) is { } h && h.Column == column
            && h.IsDown == tool.PilotSortDescending && h.IsUp == !tool.PilotSortDescending;

        /// <summary>
        /// A kill's line, clicked away from its links, opens the killmail in the Killmail tool; its system link still
        /// opens the system and nothing else; a report's line opens nothing on a click.
        /// </summary>
        private static void KillLineClicks(Checks c)
        {
            var snapshot = FeedSnapshot();
            var feed = HostileChecks.View(snapshot).Feed;
            var kill = new IntelEventRowVm(feed.First(r => r.Event.KillMailId == BrawlKill), snapshot, Now, false);
            var report = new IntelEventRowVm(feed.First(r => r.Event.Kind == IntelEventKind.Report), snapshot, Now, false);
            var nav = EntityNavigator.Instance;
            var (keptKill, keptSystem) = (nav.OpenKillmail, nav.OpenSystem);
            var killmails = new List<int>();
            var systems = new List<int>();
            nav.OpenKillmail = killmails.Add;
            nav.OpenSystem = systems.Add;
            var killLine = new IntelEventRow { DataContext = kill };
            var reportLine = new IntelEventRow { DataContext = report };
            var window = new Window { Width = 600, Height = 200, Content = new StackPanel { Children = { killLine, reportLine } } };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                // Away from the links: the right-hand end of the first line, where the age sits.
                ClickAt(window, killLine, new Point(killLine.Bounds.Width - 4, 10));
                c.Check("kill line: a click away from its links opens the killmail", killmails.SequenceEqual([BrawlKill]) && systems.Count == 0,
                        $"killmails [{string.Join(',', killmails)}], systems [{string.Join(',', systems)}]");
                killmails.Clear();
                var link = killLine.GetVisualDescendants().OfType<Button>().First();
                Click(window, link);
                Dispatcher.UIThread.RunJobs();
                c.Check("kill line: a click on its system link opens the system, not the killmail",
                        killmails.Count == 0 && systems.SequenceEqual([kill.SystemId]),
                        $"killmails [{string.Join(',', killmails)}], systems [{string.Join(',', systems)}]");
                systems.Clear();
                ClickAt(window, reportLine, new Point(reportLine.Bounds.Width - 4, 10));
                c.Check("kill line: a report's line opens nothing on a click", killmails.Count == 0 && systems.Count == 0,
                        $"killmails [{string.Join(',', killmails)}], systems [{string.Join(',', systems)}]");
            }
            finally
            {
                window.Close();
                (nav.OpenKillmail, nav.OpenSystem) = (keptKill, keptSystem);
            }
        }

        /// <summary>A real click, through the window's input, at a point on a control.</summary>
        private static void ClickAt(Window window, Control control, Point at)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var point = control.TranslatePoint(at, window) ?? default;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>A real click, through the window's input, in the middle of the header.</summary>
        private static void Click(Window window, Button button)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window) ?? default;
            window.MouseMove(centre);
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
        }

        /// <summary>Runs the UI thread's work until the projection and the Pilots tab's newest showing are done, and
        /// nothing more came for <paramref name="quietMs"/>.</summary>
        private static void Settle(Window window, IntelViewModel tool, int quietMs = 150)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            var quiet = 0;
            while (DateTime.UtcNow < until && quiet < quietMs / 15)
            {
                Dispatcher.UIThread.RunJobs();
                quiet = tool.PilotsShown.IsCompleted ? quiet + 1 : 0;
                Thread.Sleep(15);
            }
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        private static void Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(15);
            }
        }
    }
}
