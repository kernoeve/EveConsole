// The panel beside a client (its list with one system opened, and its single line, wide and narrow), the Intel
// tool's Board (a system opened), its Sources choice open, its Pilots tab and its Feed (a kill, reports and a camp, wide and narrow), drawn headless from the invented hostiles of HostileScene, to pictures a review can
// look at without the app, a database or a game client. The pictures of pilots, corporations, alliances and hulls are
// drawn here too (no network): shapes and colours made up from each id. Every name and id is invented.
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveConsole.Models;
using EveConsole.Services;
using EveConsole.Services.ClientLayouts;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;
using EveConsole.Views;

namespace EveConsole.Tools.IntelCheck
{
    internal static class HostileShots
    {
        public const string Arg = "--hostile-shots";

        public static int Run(string outDir)
        {
            var profile = Path.Combine(Path.GetTempPath(), "intelcheck-shots-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            AppConfig.UseProfile(profile);
            Directory.CreateDirectory(outDir);
            // The rows ask the image cache for their pictures as they are built: with invented ids, none is fetched.
            EveImageCache.Offline = true;
            try
            {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();

                var snapshot = HostileScene.Snapshot();
                var distances = new IntelDistanceService(HostileScene.Gates.ToDictionary(kv => kv.Key, kv => kv.Value), IntelDistanceService.Edges.None);
                distances.JumpsFromAsync(HostileScene.Home, IntelGraphMode.Threat).GetAwaiter().GetResult();
                var characters = new ObservableCollection<Character> { new() { Id = HostileScene.Me, Name = "Pilot Qzx Home" } };
                var failed = 0;

                // ── The panel: its list with XQ1-Z2 opened, then its single line ──
                var focus = new MonitorFocus();
                focus.Update([new ClientOnMonitor(1, "M1", false, "Pilot Qzx Home")]);
                var panel = new PanelIntelVm(null, distances, characters, focus, "M1", null) { Clock = () => HostileScene.Now };
                panel.Start();
                panel.Apply(snapshot);
                Pump(() => panel.Rows.Count > 0 && panel.Rows.All(r => r.HasJumps && !r.IsPending));
                if (panel.Rows.Count == 0) { Console.WriteLine("the panel showed no rows: " + panel.Note); failed++; }
                if (panel.Rows.FirstOrDefault(r => r.SystemId == HostileScene.Gang) is { } gang) gang.IsOpen = true;
                void GivePanel()
                {
                    foreach (var row in panel.Rows) Give(row.PicturesToLoad.Concat(row.Events.SelectMany(e => e.Pictures)));
                }
                failed += Shot(new PanelIntelControl { DataContext = panel, ManagesLifetime = false }, 400, 720,
                               Path.Combine(outDir, "panel-list-opened.png"), GivePanel);
                failed += Shot(new PanelIntelControl { DataContext = panel, ManagesLifetime = false }, 300, 420,
                               Path.Combine(outDir, "panel-list-narrow.png"), GivePanel, close: r => r.IsOpen = false);
                failed += Shot(Strip(new PanelIntelLineContent { DataContext = panel }), 620, 34,
                               Path.Combine(outDir, "panel-single-line.png"), GivePanel);
                failed += Shot(Strip(new PanelIntelLineContent { DataContext = panel }), 400, 34,
                               Path.Combine(outDir, "panel-single-line-narrow.png"), GivePanel);
                panel.Dispose();

                // ── The Board, with XQ1-Z2 opened ──
                var tool = new IntelViewModel(null, distances, null, null, null, characters) { Clock = () => HostileScene.Now };
                var view = new IntelToolView { DataContext = tool };
                var window = new Window { Width = 1500, Height = 760, Content = view, Background = Brush("SurfaceBaseBrush") };
                window.Show();
                tool.Apply(snapshot);
                Pump(() => tool.BoardRows.Count > 0 && tool.BoardRows.All(r => r.HasJumps && !r.IsPending));
                if (tool.BoardRows.FirstOrDefault(r => r.SystemId == HostileScene.Gang) is { } row) row.IsOpen = true;
                else { Console.WriteLine("the Board showed no XQ1-Z2 row"); failed++; }
                foreach (var r in tool.BoardRows)
                {
                    Give(r.PicturesToLoad.Concat(r.Events.SelectMany(e => e.Pictures)));
                    if (r.NearestId > 0) r.ShowNearest(Picture(IntelPictureVm.Kinds.Pilot, r.NearestId));
                }
                failed += Capture(window, Path.Combine(outDir, "board-opened.png"), () =>
                {
                    foreach (var r in tool.BoardRows)
                    {
                        Give(r.PicturesToLoad.Concat(r.Events.SelectMany(e => e.Pictures)));
                        if (r.NearestId > 0) r.ShowNearest(Picture(IntelPictureVm.Kinds.Pilot, r.NearestId));
                    }
                });

                // ── The Sources choice, open: the game's channels, then each server's rooms, then the kills and the pastes ──
                tool.Apply(ChannelLabelChecks.WithRooms(snapshot));
                Pump(() => tool.Sources.Any(s => s.Key == ChannelLabelChecks.Watch));
                failed += SourcesShot(window, view, Path.Combine(outDir, "sources.png"));
                tool.Apply(snapshot);

                // ── The Pilots tab, sorted by its Alliance header ──
                tool.UsePilots(new IntelPilotResults([.. HostileScene.Input().LastSeen], HostileScene.Names));
                tool.SortPilotsBy(IntelViewModel.PilotColumn.Alliance);
                tool.SelectedTab = IntelViewModel.TabPilots;
                Pump(() => tool.PilotsShown.IsCompleted && tool.PilotRows.Count > 0);
                failed += Capture(window, Path.Combine(outDir, "pilots-sorted.png"), () =>
                {
                    foreach (var r in tool.PilotRows)
                        Give(new[] { r.Pilot, r.Hull, r.Corporation, r.Alliance }.Where(p => p is not null).Select(p => p!));
                });

                // ── The Feed: a kill, reports and a camp, each saying who; wide, then narrow ──
                tool.Apply(HostileScene.FeedSnapshot());
                tool.SelectedTab = IntelViewModel.TabFeed;
                Pump(() => tool.FeedRows.OfType<IntelFeedLineVm>().Any(l => l.Row.Kind == IntelEventKind.Kill && l.Row.SystemId == HostileScene.Brawl)
                           && tool.FeedRows.OfType<IntelFeedLineVm>().All(l => !l.Row.IsPending));
                void GiveFeed()
                {
                    foreach (var l in tool.FeedRows.OfType<IntelFeedLineVm>())
                    {
                        Give(l.Row.Pictures);
                        if (l.Row.HasReporter) l.Row.ShowReporter(Picture(IntelPictureVm.Kinds.Pilot, l.Row.ReporterId));
                    }
                }
                failed += Capture(window, Path.Combine(outDir, "feed-who.png"), GiveFeed);
                window.Width = 760;
                failed += Capture(window, Path.Combine(outDir, "feed-who-narrow.png"), GiveFeed);
                window.Close();
                tool.Dispose();
                return failed == 0 ? 0 : 1;
            }
            finally
            {
                EveImageCache.Offline = false;
                try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>The single line as the top strip has it: a thin bar across the panel.</summary>
        private static Control Strip(Control line) => new Border
        {
            Padding = new Thickness(6, 0), Child = line, Background = Brush("SurfacePanelBrush"),
            BorderBrush = Brush("BorderSubtleBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
        };

        private static IBrush? Brush(string key) =>
            Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) ? value as IBrush : null;

        private static int Shot(Control content, double width, double height, string path, Action give,
                                Action<IntelSystemLineVm>? close = null)
        {
            var window = new Window { Width = width, Height = height, Content = content, Background = Brush("SurfaceBaseBrush") };
            if (close is not null && content.DataContext is PanelIntelVm vm) foreach (var r in vm.Rows) close(r);
            window.Show();
            var failed = Capture(window, path, give);
            window.Close();
            if (close is not null && content.DataContext is PanelIntelVm again && again.Rows.FirstOrDefault(r => r.SystemId == HostileScene.Gang) is { } g)
                g.IsOpen = true;
            return failed;
        }

        /// <summary>Lays the window out, gives the made-up pictures (the image cache is offline, so none is fetched), and
        /// saves what it draws.</summary>
        private static int Capture(Window window, string path, Action give)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            give();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame();
            if (frame is null) { Console.WriteLine($"no frame for {path}"); return 1; }
            frame.Save(path);
            Console.WriteLine($"wrote {path}");
            return 0;
        }

        /// <summary>
        /// The top bar with its Sources choice open. The choice opens in a popup, its own surface headless, so it is drawn
        /// on the window's picture where it opens: under the button, left edges aligned.
        /// </summary>
        private static int SourcesShot(Window window, Control view, string path)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (view.GetVisualDescendants().OfType<DropDownButton>().FirstOrDefault() is not { Flyout: Flyout flyout } button)
            {
                Console.WriteLine("no Sources button on the top bar");
                return 1;
            }
            flyout.ShowAt(button);
            Pump(() => flyout.IsOpen);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            try
            {
                using var frame = window.CaptureRenderedFrame();
                var popup = flyout.Content is Control content ? TopLevel.GetTopLevel(content) : null;
                using var popupFrame = popup is not null && !ReferenceEquals(popup, window) ? popup.CaptureRenderedFrame() : null;
                if (frame is null) { Console.WriteLine($"no frame for {path}"); return 1; }

                var at = button.TranslatePoint(new Point(0, button.Bounds.Height), window) ?? default;
                var below = popupFrame is null ? 0 : at.Y + popupFrame.Size.Height + 16;
                var height = (int)Math.Min(frame.Size.Height, Math.Max(below, at.Y + 300));
                var width = (int)frame.Size.Width;
                using var picture = new RenderTargetBitmap(new PixelSize(width, height));
                using (var ctx = picture.CreateDrawingContext())
                {
                    ctx.DrawImage(frame, new Rect(0, 0, width, height), new Rect(0, 0, width, height));
                    if (popupFrame is not null)
                        ctx.DrawImage(popupFrame, new Rect(at.X, at.Y, popupFrame.Size.Width, popupFrame.Size.Height));
                }
                picture.Save(path);
                Console.WriteLine($"wrote {path}" + (popupFrame is null ? " (the choice drew in the window itself)" : ""));
                return 0;
            }
            finally { flyout.Hide(); }
        }

        /// <summary>Runs the UI thread's work until <paramref name="done"/>, or ten seconds.</summary>
        private static void Pump(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done() && DateTime.UtcNow < until)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(20);
            }
            Dispatcher.UIThread.RunJobs();
        }

        // ── Pictures, made up ────────────────────────────────────────────────────

        private static readonly Dictionary<(IntelPictureVm.Kinds, long), Bitmap> Made = [];

        private static void Give(IEnumerable<IntelPictureVm> pictures)
        {
            foreach (var p in pictures.SelectMany(x => x.WithBadge))
                if (p.HasPictureSlot) p.Show(Picture(p.Kind, p.Id));
        }

        /// <summary>A picture for an id: a portrait as a head and shoulders, a corporation's or alliance's logo as a shape
        /// on a field, a hull as a wedge; colours from the id.</summary>
        private static Bitmap Picture(IntelPictureVm.Kinds kind, long id)
        {
            if (Made.TryGetValue((kind, id), out var made)) return made;
            const int size = 64;
            var hue = (int)((ulong)id * 2654435761UL % 360);
            Color Hsl(double h, double s, double l)
            {
                var c = (1 - Math.Abs(2 * l - 1)) * s;
                var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
                var m = l - c / 2;
                var (r, g, b) = h switch { < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x) };
                return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
            }
            var bitmap = new RenderTargetBitmap(new PixelSize(size, size));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                var field = new SolidColorBrush(Hsl(hue, 0.35, 0.22));
                var fore = new SolidColorBrush(Hsl(hue, 0.65, 0.62));
                ctx.FillRectangle(field, new Rect(0, 0, size, size));
                switch (kind)
                {
                    case IntelPictureVm.Kinds.Pilot:
                        ctx.DrawEllipse(fore, null, new Point(32, 25), 13, 14);
                        ctx.DrawEllipse(fore, null, new Point(32, 66), 26, 22);
                        break;
                    case IntelPictureVm.Kinds.Alliance:
                        ctx.DrawGeometry(fore, null, StreamGeometry.Parse("M32,6 L58,32 L32,58 L6,32 Z"));
                        ctx.DrawEllipse(field, null, new Point(32, 32), 10, 10);
                        break;
                    case IntelPictureVm.Kinds.Corporation:
                        ctx.DrawGeometry(fore, null, StreamGeometry.Parse("M32,6 L55,19 L55,45 L32,58 L9,45 L9,19 Z"));
                        ctx.FillRectangle(field, new Rect(24, 24, 16, 16));
                        break;
                    default:
                        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(18, 20, 24)), new Rect(0, 0, size, size));
                        ctx.DrawGeometry(fore, null, StreamGeometry.Parse("M10,46 L50,14 L54,22 L22,52 Z"));
                        ctx.DrawGeometry(new SolidColorBrush(Hsl(hue, 0.4, 0.4)), null, StreamGeometry.Parse("M14,30 L34,20 L44,40 L28,48 Z"));
                        break;
                }
            }
            Made[(kind, id)] = bitmap;
            return bitmap;
        }
    }
}
