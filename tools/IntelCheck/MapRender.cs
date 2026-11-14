// The Universe Map's intel marks, drawn headless from a snapshot made up here, to a picture: what a review of the map
// can look at without the app, a database or a game client. Two zooms of the same made-up corner of space, close
// and wider. Every name and id here is invented.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using EveConsole.Controls;
using EveConsole.Models;
using EveConsole.Monitoring;
using EveConsole.Services;
using EveConsole.Services.Intel;
using EveConsole.ViewModels;

namespace EveConsole.Tools.IntelCheck
{
    internal static class MapScene
    {
        public static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

        // Systems, with ids no SDE holds and names known absent from it.
        public const int S1 = 39_900_001, S2 = 39_900_002, S3 = 39_900_003, S4 = 39_900_004, S5 = 39_900_005, S6 = 39_900_006,
                         S7 = 39_900_007, S8 = 39_900_008, S9 = 39_900_009, S10 = 39_900_010, S11 = 39_900_011, S12 = 39_900_012;

        public static readonly (int Id, string Name, double X, double Y)[] Systems =
        [
            (S1, "QZ-X77", 120, 280), (S2, "XQ1-Z2", 260, 160), (S3, "ZZQ-71", 260, 400), (S4, "X2-QZ7", 400, 280),
            (S5, "K7-QZP", 540, 160), (S6, "Q2X-91", 540, 400), (S7, "ZXQ-4F", 680, 280), (S8, "9Q-ZX2", 820, 280),
            (S9, "QX-7ZK", 400, 60), (S10, "Z9-QQ1", 820, 100), (S11, "XZQ-02", 820, 460), (S12, "Q7-ZZX", 680, 480),
        ];

        public static readonly (int A, int B)[] Gates =
        [
            (S1, S2), (S1, S3), (S2, S4), (S3, S4), (S2, S9), (S9, S5), (S4, S5), (S4, S6), (S5, S7), (S6, S7),
            (S7, S8), (S8, S10), (S8, S11), (S6, S12),
        ];

        // The user's character, its corporation and alliance; the pilots seen.
        public const long Alpha = 91_900_001, CorpA = 98_900_001, AllyA = 99_900_001;
        public const long Red1 = 92_900_001, Orange1 = 92_900_002, Blue1 = 92_900_003, Red2 = 92_900_004,
                          Camper1 = 92_900_005, Camper2 = 92_900_006, Victim1 = 92_900_007, Victim2 = 92_900_008;
        public const long FarCorp = 98_900_020;
        public const long GateS7toS8 = 50_900_001, GateS8toS7 = 50_900_002;
        public const int Hull = 70_901, ShipCat = 9_906, FrigateGroup = 9_925;

        public static MapGraph Graph() => new(
            [.. Systems.Select(s => new MapNode(s.Id, s.Name, s.X, s.Y, -0.3, false, SecurityClass: "null", RegionId: 10_900_001, RegionName: "Q-ZX9"))],
            [.. Gates.Select(g => new MapEdge(Math.Min(g.A, g.B), Math.Max(g.A, g.B)))]);

        public static IntelStandings Standings() => new(
            [new IntelStandings.OwnCharacter(Alpha, "Pilot Alpha", CorpA, AllyA)],
            [CorpA],
            [(Alpha, Red1, -10), (Alpha, Red2, -10), (CorpA, Orange1, -3), (AllyA, Blue1, 10)]);

        private static int _id = 5_000;

        private static IntelReport Report(int system, double minutesAgo, int count = 0, int flags = 0, int kind = IntelReportKind.Sighting,
                                          int? from = null, int? heading = null, bool noVisual = false) => new()
        {
            Id = ++_id, ReportedAt = IntelLastSeenWriter.Format(Now.AddMinutes(-minutesAgo)), ChannelName = "intelcheck",
            ReporterName = "Scout Alpha", SystemId = system, SystemName = Systems.First(s => s.Id == system).Name,
            PlayerCount = count, Flags = flags, Kind = kind, Source = IntelReportSource.Chat, FromSystemId = from,
            HeadingSystemId = heading, NoVisual = noVisual, Message = "intelcheck",
        };

        private static IntelLastSeen Seen(long pilot, int system, double minutesAgo, int? reportId,
                                          int source = IntelLastSeenSource.Report, long corp = FarCorp) => new()
        {
            CharacterId = pilot, Name = $"Pilot {pilot % 1000}", SystemId = system,
            SeenAt = IntelLastSeenWriter.Format(Now.AddMinutes(-minutesAgo)), Source = source,
            SourceRank = IntelLastSeenSource.RankOf(source), ReportId = reportId, CorporationId = corp,
            ShipTypeId = Hull, WrittenAt = IntelLastSeenWriter.Format(Now.AddMinutes(-minutesAgo)),
        };

        private static IntelKill Kill(int id, double minutesAgo, long victim, params long[] attackers) =>
            new(id, Now.AddMinutes(-minutesAgo), S7, victim, FarCorp, 0, Hull, FrigateGroup, 20_000, 0, 0,
                [.. attackers.Select(a => new IntelKillAttacker(a, FarCorp, 0, Hull, FrigateGroup, null, null))]);

        /// <summary>
        /// The corner of space the pictures show:
        /// <list type="bullet">
        /// <item>XQ1-Z2: a red pilot, a minute ago, said to have come from QX-7ZK (a move).</item>
        /// <item>ZZQ-71: an orange pilot ten minutes ago, heading for QZ-X77, where the user's character is.</item>
        /// <item>X2-QZ7: three unnamed, no visual.</item>
        /// <item>K7-QZP: bubbles and nobody counted.</item>
        /// <item>Q2X-91: called clear three minutes ago.</item>
        /// <item>ZXQ-4F: a camp on the 9Q-ZX2 gate: two pilots lost to the same pair in seven minutes.</item>
        /// <item>Z9-QQ1: a red pilot seen fourteen minutes ago, nearly gone.</item>
        /// <item>XZQ-02: a blue pilot, never drawn.</item>
        /// </list>
        /// </summary>
        public static IntelSnapshot Snapshot() => IntelSnapshotBuilder.Build(Input(), new IntelCampRegister(), 1);

        /// <summary>The scene's rows. <paramref name="clearS2After"/> adds a clear of XQ1-Z2 after its move;
        /// <paramref name="manyMoves"/> adds that many reports in Q7-ZZX, each saying they came from a different
        /// system, a few seconds apart.</summary>
        public static IntelSnapshotInput Input(bool clearS2After = false, int manyMoves = 0)
        {
            var move    = Report(S2, 1, count: 1, from: S9);
            var heading = Report(S3, 10, count: 1, heading: S1);
            var nv      = Report(S4, 2, count: 3, noVisual: true);
            var bubbles = Report(S5, 4, flags: (int)IntelRules.IntelFlags.Bubbles);
            var clear   = Report(S6, 3, kind: IntelReportKind.Clear);
            var faded   = Report(S10, 14, count: 1);
            var blue    = Report(S11, 2, count: 1);
            var reports = new List<IntelReport> { move, heading, nv, bubbles, clear, faded, blue };
            if (clearS2After) reports.Add(Report(S2, 0.5, kind: IntelReportKind.Clear));
            for (var i = 0; i < manyMoves; i++)
                reports.Add(Report(S12, 0.1 * (i + 1), count: 1, from: 39_901_000 + i));

            var named = new[]
            {
                new IntelReportCharacter { IntelReportId = move.Id,    CharacterId = Red1,    CharacterName = "Pilot 001" },
                new IntelReportCharacter { IntelReportId = heading.Id, CharacterId = Orange1, CharacterName = "Pilot 002" },
                new IntelReportCharacter { IntelReportId = faded.Id,   CharacterId = Red2,    CharacterName = "Pilot 004" },
                new IntelReportCharacter { IntelReportId = blue.Id,    CharacterId = Blue1,   CharacterName = "Pilot 003" },
            };
            var seen = new[]
            {
                Seen(Red1, S2, 1, move.Id), Seen(Orange1, S3, 10, heading.Id), Seen(Red2, S10, 14, faded.Id),
                Seen(Blue1, S11, 2, blue.Id),
                Seen(Camper1, S7, 2, null, IntelLastSeenSource.KillAttacker), Seen(Camper2, S7, 2, null, IntelLastSeenSource.KillAttacker),
            };
            var kills = new[] { Kill(1, 9, Victim1, Camper1, Camper2), Kill(2, 2, Victim2, Camper1) };
            var gates = new Dictionary<int, IReadOnlyList<IntelGate>>
            {
                [S7] = [new IntelGate(GateS7toS8, S7, S8, 0, 0, 0)],
                [S8] = [new IntelGate(GateS8toS7, S8, S7, 0, 0, 0)],
            };
            var groups = new IntelSdeGroups(
                new (int, int, string)[] { (FrigateGroup, ShipCat, "Frigate") },
                [(ShipCat, IntelSdeGroups.ShipCategoryName)]);

            return new IntelSnapshotInput
            {
                Now = Now, Settings = new IntelSettingsValues(),
                Reports = reports, ReportPilots = named, LastSeen = seen, Kills = kills, Gates = gates, Groups = groups,
                Standings = Standings(),
                Own = [new OwnPosition(Alpha, "Pilot Alpha", S1, Now.AddMinutes(-30), OwnPositionSource.Esi, CorpA, AllyA)],
                SystemNames = Systems.ToDictionary(s => s.Id, s => s.Name),
            };
        }
    }

    /// <summary>Draws the scene's map, headless, to pictures.</summary>
    internal static class MapRender
    {
        public const string Arg = "--map-render";

        /// <summary>Screen pixels per world unit at each zoom: close, and further out with smaller spacing.</summary>
        private static readonly (string Name, double Scale)[] Zooms = [("close", 1.0), ("wide", 0.55)];

        private const int Width = 960, Height = 560;

        public static int Run(string outDir, string label)
        {
            var profile = Path.Combine(Path.GetTempPath(), "intelcheck-map-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            AppConfig.UseProfile(profile);
            Directory.CreateDirectory(outDir);
            try
            {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();

                var graph = MapScene.Graph();
                var live  = LiveIntelService.ToMap(MapScene.Snapshot(), MapScene.Now);
                var marks = MapToolViewModel.BuildMarkers(live, graph);

                foreach (var (zoom, scale) in Zooms)
                {
                    var canvas = new MapCanvas { Graph = graph, Markers = marks };
                    Dress(canvas, live, graph);
                    canvas.Camera = new MapCamera(470, 270, scale);
                    var window = new Window { Width = Width, Height = Height, Content = canvas };
                    window.Show();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    using var frame = window.CaptureRenderedFrame();
                    var path = Path.Combine(outDir, $"map-{label}-{zoom}.png");
                    frame?.Save(path);
                    Console.WriteLine(frame is null ? $"no frame for {zoom}" : $"wrote {path}");
                    window.Close();
                }
                return 0;
            }
            finally
            {
                try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>Everything the map draws from intel besides the marks, every layer switched on: the camped
        /// gates, both kinds of arrow, and an alert zone of QZ-X77 and its neighbours (what an alarm 1 jump
        /// round the character there would watch).</summary>
        private static void Dress(MapCanvas canvas, LiveMapSnapshot live, MapGraph graph)
        {
            canvas.CampedGates = MapToolViewModel.BuildCamps(live, graph);
            canvas.IntelArrows = MapToolViewModel.BuildArrows(live, graph);
            canvas.AlertZone   = new HashSet<int> { MapScene.S1, MapScene.S2, MapScene.S3 };
        }
    }
}
