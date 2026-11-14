// The Intel tool's grids, headless, as a user clicks them: the Pilots tab hides the standings tiers switched off;
// every header sorts its column, the second click turns it round, and a refresh keeps the direction; the long lists
// (Pilots, Feed) are virtualized, so a click or a refresh lays out only the rows in sight. Pilots is the one intel grid
// sorted by its headers: the Board sorts from its own choice, Camps and the Watch list do not sort.
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
                var heads = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("sortHead")).ToList();
                c.Check("tool grids: a header for each Pilots column", heads.Count == Enum.GetValues<IntelViewModel.PilotColumn>().Length, heads.Count.ToString());
                var failures = new List<string>();
                var version = 10L;
                foreach (var column in Enum.GetValues<IntelViewModel.PilotColumn>())
                {
                    var head = heads.First(b => b.DataContext is IntelSortHeaderVm h && h.Column == column);
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
                var jumps = heads.First(b => b.DataContext is IntelSortHeaderVm { Column: IntelViewModel.PilotColumn.Jumps });
                if (tool.PilotSort == IntelViewModel.PilotColumn.Jumps) Click(window, heads.First(b => b.DataContext is IntelSortHeaderVm { Column: IntelViewModel.PilotColumn.Name }));
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

        /// <summary>Runs the UI thread's work until the projection and the Pilots tab's newest showing are done.</summary>
        private static void Settle(Window window, IntelViewModel tool)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            var quiet = 0;
            while (DateTime.UtcNow < until && quiet < 10)
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
