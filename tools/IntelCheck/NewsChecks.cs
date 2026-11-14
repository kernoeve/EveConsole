// News for intel alarms, and every action for it: only news fires (seen, or come into range, within the fresh
// window; a kill from when the alarm caught it), the rest is banked unsaid; the sound plays quieter further out; the
// memory of when each system came into reach; jumps from and thru, so two alarms can split a range; and the whole of
// it through the alarm service on a scratch database: every action of every alarm runs for all it fires on, two alarms
// catching one sighting both say it, and no alarm holds another back (user, 2026-11-13). Every name and id here is
// invented.
using System.Collections.Concurrent;
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
        private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(IntelSettings.DefaultFreshSeconds);

        private static AlarmMatch? KeyOf(IEnumerable<AlarmMatch> matches, string prefix) =>
            matches.FirstOrDefault(m => m.Key.StartsWith(prefix, StringComparison.Ordinal));

        /// <summary>What is news and how loud: pure, on matches from a hand-built snapshot.</summary>
        public static void News(Checks c)
        {
            var snap = Snap(
                seen:
                [
                    Seen(P1, S1, Ago(5)),                 // five minutes old, where Alpha is
                    Seen(P2, S2, Ago(0.5)),               // thirty seconds old, a jump out
                    Seen(P3, S4, Ago(1)),                 // a minute old, three jumps out
                ],
                kills:
                [
                    Kill(K1, S2, Ago(10), P9, (P4, HullA, FarCorp)),   // ten minutes old, a jump out: urgent
                    Kill(K2, S4, Ago(0.5), P8, (P7, HullB, FarCorp)),  // fresh, three jumps out
                ]);
            var matches = Match(snap);
            string Key(string prefix) => KeyOf(matches, prefix)?.Key ?? "";
            var p1 = Key($"intel:p:{P1}|"); var p2 = Key($"intel:p:{P2}|"); var p3 = Key($"intel:p:{P3}|");
            var k1 = $"intel:k:{K1}"; var k2 = $"intel:k:{K2}";
            c.Check("news: the snapshot gives the matches the cases below need",
                p1.Length > 0 && p2.Length > 0 && p3.Length > 0 && matches.Any(m => m.Key == k1) && matches.Any(m => m.Key == k2), Keys(matches));

            IReadOnlyList<AlarmMatch> Judge(IntelCondition.IntelAlarmOptions? options = null, IntelWatchMemory.Look? look = null,
                                            TimeSpan? fresh = null) =>
                IntelCondition.News(matches, Zone(), options ?? Defaults, Now, fresh ?? Fresh, look ?? IntelWatchMemory.Look.AllAlong);
            AlarmMatch? Of(IEnumerable<AlarmMatch> judged, string key) => judged.FirstOrDefault(m => m.Key == key);

            // ── News only ──
            var all = Judge();
            c.Check("news: a sighting seen five minutes ago is banked unsaid (stale)", Of(all, p1) is { Silent: true }, Keys(all));
            c.Check("news: one seen thirty seconds ago fires", Of(all, p2) is { Silent: false });
            c.Check("news: a kill ten minutes old, caught then, is banked unsaid", Of(all, k1) is { Silent: true, Urgent: true });
            c.Check("news: a fresh kill three jumps out fires", Of(all, k2) is { Silent: false });
            c.Check("news: everything new in the range fires, near or far: nothing keeps some actions for nearer news",
                Of(all, p3) is { Silent: false });

            var arrived = Judge(look: new IntelWatchMemory.Look(
                new Dictionary<int, DateTimeOffset> { [S1] = Now.AddSeconds(-10) }, new Dictionary<string, DateTimeOffset>()));
            c.Check("news: an old sighting in a system that just came into reach is news (heard on arrival)",
                Of(arrived, p1) is { Silent: false });
            var late = Judge(look: new IntelWatchMemory.Look(
                new Dictionary<int, DateTimeOffset>(), new Dictionary<string, DateTimeOffset> { [k1] = Now.AddSeconds(-5) }));
            c.Check("news: a kill the feed brought late is news from when the alarm caught it", Of(late, k1) is { Silent: false });
            c.Check("news: the fresh window is Settings → Intel's: ten minutes, and five-minute-old news fires",
                Of(Judge(fresh: TimeSpan.FromMinutes(10)), p1) is { Silent: false });

            // ── Loudness ──
            c.Check("loud: full within a jump", IntelCondition.Loudness(0, null, 10, null) == 1 && IntelCondition.Loudness(1, null, 10, null) == 1);
            c.Check("loud: 40% at the edge of the range, and past it",
                Math.Abs(IntelCondition.Loudness(10, null, 10, null) - 0.4) < 1e-9 && Math.Abs(IntelCondition.Loudness(15, null, 10, null) - 0.4) < 1e-9);
            c.Check("loud: evenly between", Math.Abs(IntelCondition.Loudness(4, null, 10, null) - (1 - 0.6 * 3 / 9)) < 1e-9,
                IntelCondition.Loudness(4, null, 10, null).ToString("0.000"));
            c.Check("loud: a system only the light-year range caught, by its light years",
                Math.Abs(IntelCondition.Loudness(null, 5, 10, 10) - 0.7) < 1e-9);
            c.Check("loud: a range of 0 or 1 is all full", IntelCondition.Loudness(0, null, 0, null) == 1 && IntelCondition.Loudness(1, null, 1, null) == 1);
            c.Check("loud: three jumps out of ten plays at 87%", Of(all, p3) is { } m3 && Math.Abs(m3.Loudness - (1 - 0.6 * 2 / 9)) < 1e-9,
                Of(all, p3)?.Loudness.ToString("0.000") ?? "none");
            c.Check("loud: an urgent kill plays full", Of(all, k1) is { Loudness: 1 });
            // Urgent is full by rule, not because today's urgent kills are all within a jump.
            var farUrgent = IntelCondition.News([.. matches.Select(m => m.Key == k2 ? m with { Urgent = true } : m)],
                Zone(), Defaults, Now, Fresh, IntelWatchMemory.Look.AllAlong);
            c.Check("loud: urgent news three jumps out still plays full", Of(farUrgent, k2) is { Loudness: 1 },
                Of(farUrgent, k2)?.Loudness.ToString("0.000") ?? "none");
            c.Check("loud: with Quieter further out off, everything plays full",
                Judge(Defaults with { QuieterFar = false }).All(m => m.Loudness == 1));

            // ── The editor's options ──
            using (var cfg = JsonDocument.Parse("""{"jumps":8,"quieter_far":false,"ly":6}"""))
                c.Check("options: quieter far and the light-year range are read", IntelCondition.Options(cfg.RootElement) is { QuieterFar: false, Ly: 6 });
            using (var cfg = JsonDocument.Parse("""{"jumps":8}"""))
                c.Check("options: by default quieter further out", IntelCondition.Options(cfg.RootElement) is { QuieterFar: true, Ly: null });
            int From(string json, int jumps) { using var d = JsonDocument.Parse(json); return IntelCondition.ReadJumpsFrom(d.RootElement, jumps); }
            c.Check("jumps from: read, 0 when left out, and never past thru",
                From("""{"jumps_from":3}""", 5) == 3 && From("{}", 5) == 0 && From("""{"jumps_from":4}""", 2) == 2 && From("""{"jumps_from":-1}""", 5) == 0);
            IAlarmCondition described = new IntelCondition(null!);
            using (var cfg = JsonDocument.Parse("""{"jumps":5,"jumps_from":3}"""))
                c.Check("jumps from: the alarm list says the band", described.Describe(cfg.RootElement).Contains("within 3 to 5 jumps", StringComparison.Ordinal),
                    described.Describe(cfg.RootElement));
            using (var cfg = JsonDocument.Parse("""{"jumps":5,"wake_jumps":2,"approach_wakes":false}"""))
                c.Check("an alarm saved with the old wake fields reads as the whole range, nothing about waking",
                    described.Describe(cfg.RootElement) is var said && said.Contains("within 5 jumps", StringComparison.Ordinal)
                    && !said.Contains("wake", StringComparison.OrdinalIgnoreCase), described.Describe(cfg.RootElement));

            // ── The editor's sections ──
            var schema = JsonSerializer.SerializeToElement(new IntelCondition(null!).ParameterSchema).GetProperty("properties");
            var condition = (IAlarmCondition)new IntelCondition(null!);
            var sections = schema.EnumerateObject()
                .Select(p => p.Value.TryGetProperty("section", out var s) ? s.GetString() : null).ToList();
            c.Check("editor: every intel field is in a section, each with words of its own",
                sections.All(s => s is not null && condition.ScreenSection(s) is { Length: > 0 }), string.Join(",", sections));
            c.Check("editor: the sections come in order — where, what — each in one run",
                string.Join(",", sections.Distinct()) == "where,what"
                && sections.Select((s, i) => (s, i)).GroupBy(x => x.s).All(g => g.Max(x => x.i) - g.Min(x => x.i) == g.Count() - 1),
                string.Join(",", sections));
            c.Check("editor: no field picks what wakes: the wake range and approaching switch are gone",
                !schema.TryGetProperty("wake_jumps", out _) && !schema.TryGetProperty("approach_wakes", out _)
                && condition.ScreenSection("waking") is null, string.Join(",", schema.EnumerateObject().Select(p => p.Name)));
            c.Check("editor: jumps from, with thru beside it, in the interface language",
                condition.ScreenField("jumps_from") is { Label.Length: > 0, Description.Length: > 0 }
                && condition.ScreenField("jumps") is { Label.Length: > 0 }
                && schema.GetProperty("jumps").TryGetProperty("beside", out var beside) && beside.GetString() == "jumps_from"
                && !schema.GetProperty("ly").TryGetProperty("beside", out _));
            c.Check("editor: quieter further out is still there, with words", condition.ScreenField("quieter_far") is { Label.Length: > 0 });
        }

        /// <summary>When each system came into an alarm's reach, and each kill was caught.</summary>
        public static void Memory(Checks c)
        {
            var memory = new IntelWatchMemory();
            var first = memory.Observe(1, [S1, S2], ["intel:k:1"], Now);
            c.Check("memory: on an alarm's first look everything was there all along",
                first.EnteredAt(S1) == DateTimeOffset.MinValue && first.CaughtAt("intel:k:1") == DateTimeOffset.MinValue);

            var moved = memory.Observe(1, [S2, S3], ["intel:k:1", "intel:k:2"], Now.AddSeconds(15));
            c.Check("memory: a system new since the last look came into reach then",
                moved.EnteredAt(S3) == Now.AddSeconds(15) && moved.EnteredAt(S2) == DateTimeOffset.MinValue);
            c.Check("memory: a kill new since the last look was caught then",
                moved.CaughtAt("intel:k:2") == Now.AddSeconds(15) && moved.CaughtAt("intel:k:1") == DateTimeOffset.MinValue);

            var back = memory.Observe(1, [S1, S2, S3], [], Now.AddSeconds(30));
            c.Check("memory: a system that left and came back is new again", back.EnteredAt(S1) == Now.AddSeconds(30));

            var other = memory.Observe(2, [S3], [], Now.AddSeconds(30));
            c.Check("memory: each alarm has its own", other.EnteredAt(S3) == DateTimeOffset.MinValue);

            var docked = memory.Observe(1, [S4], [], Now.AddSeconds(30) + IntelWatchMemory.Stale + TimeSpan.FromSeconds(1));
            c.Check("memory: after a long gap (a long dock) it begins again: nothing is news for having come into reach",
                docked.EnteredAt(S4) == DateTimeOffset.MinValue);
        }

        // ── Through the alarm service ────────────────────────────────────────────────────────────────────────────

        /// <summary>A check whose matches each test sets by hand, per alarm.</summary>
        private sealed class ScriptedCondition : IAlarmCondition
        {
            public const string Type = "intelcheck-news";
            public ConcurrentDictionary<long, IReadOnlyList<AlarmMatch>> Script { get; } = new();

            public string TypeKey => Type;
            public string DisplayName => "Intel check news";
            public string Description => "Matches set by the intel check.";
            public object ParameterSchema => new { type = "object", properties = new { } };
            public string Describe(JsonElement config) => "intel check";

            public Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default) =>
                Task.FromResult(Script.TryGetValue(ctx.Alarm.Id, out var m) ? m : (IReadOnlyList<AlarmMatch>)[]);
        }

        /// <summary>
        /// Every action for everything an alarm fires on, through the real alarm service and action runner, on a scratch
        /// database: what each alarm published (the sound, the voice, the dialog) is read off the signals, what it
        /// recorded off its tables.
        /// </summary>
        public static async Task NewsServiceAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options)) await db.Database.EnsureCreatedAsync();

            long near, home, once, words;
            await using (var db = new AppDbContext(options))
            {
                Alarm New(string name, AlarmRepeat repeat = AlarmRepeat.Continuous) =>
                    new() { Name = name, ConditionType = ScriptedCondition.Type, Repeat = repeat, PollSeconds = 600, CreatedAt = DateTimeOffset.Now };
                var a = New("near me"); var b = New("home system"); var d = New("tell me once", AlarmRepeat.OneShot);
                var w = New("my own words");
                db.Alarms.AddRange(a, b, d, w);
                await db.SaveChangesAsync();
                (near, home, once, words) = (a.Id, b.Id, d.Id, w.Id);
                db.AlarmActions.AddRange(
                    new AlarmAction { AlarmId = near, Kind = AlarmActionKind.Sound, ConfigJson = """{"volume":80}""", Ordinal = 0 },
                    new AlarmAction { AlarmId = near, Kind = AlarmActionKind.TtsDirect, Ordinal = 1 },
                    new AlarmAction { AlarmId = near, Kind = AlarmActionKind.Alert, Ordinal = 2 },
                    // No Alert: speech alone, as the user's own alarm has it.
                    new AlarmAction { AlarmId = home, Kind = AlarmActionKind.TtsDirect, Ordinal = 0 },
                    new AlarmAction { AlarmId = once, Kind = AlarmActionKind.TtsDirect, Ordinal = 0 },
                    new AlarmAction { AlarmId = once, Kind = AlarmActionKind.Alert, Ordinal = 1 },
                    new AlarmAction { AlarmId = words, Kind = AlarmActionKind.TtsDirect, ConfigJson = """{"message":"{summary} ({count})"}""", Ordinal = 0 },
                    new AlarmAction { AlarmId = words, Kind = AlarmActionKind.Dialog, ConfigJson = """{"title":"{count} seen","message":"{summary}"}""", Ordinal = 1 },
                    new AlarmAction { AlarmId = words, Kind = AlarmActionKind.Alert, ConfigJson = """{"body":"{summary}"}""", Ordinal = 2 });
                await db.SaveChangesAsync();
            }

            // ⚠️ SQLite is pinned, so signals are delivered in this process: nothing reaches any server.
            var logger  = new AppErrorLogger(null!);
            var signals = new ClientSignals(logger);
            signals.Start();
            var heard = new ConcurrentQueue<AlarmSignal>();
            signals.Received += payload =>
            {
                try { if (JsonSerializer.Deserialize<AlarmSignal>(payload) is { Kind: AlarmSignal.SignalKind } s) heard.Enqueue(s); }
                catch (JsonException) { }
            };

            var factory   = new Factory(options);
            var condition = new ScriptedCondition();
            var registry  = new AlarmConditionRegistry([condition]);
            var runner    = new AlarmActionRunner(factory, new AlarmSoundService(), logger, signals, new AlarmMuteState(), registry);
            var service   = new AlarmService(factory, "", registry, runner, logger, signals);
            service.Start();   // the worker: its loop waits ten seconds before its first tick, so each trigger below is the only pass

            async Task Pass() { heard.Clear(); await service.TriggerAsync(ScriptedCondition.Type); }
            async Task<(Alarm Alarm, int Events, int Alerts, List<string> Keys)> Read(long id)
            {
                await using var db = new AppDbContext(options);
                var alarm  = await db.Alarms.AsNoTracking().SingleAsync(x => x.Id == id);
                var events = await db.AlarmEvents.CountAsync(x => x.AlarmId == id);
                var alerts = await db.AlarmAlerts.CountAsync(x => x.AlarmId == id);
                var keys   = await db.AlarmSeenKeys.Where(x => x.AlarmId == id).Select(x => x.MatchKey).ToListAsync();
                return (alarm, events, alerts, keys);
            }
            AlarmSignal? Heard(long id) => heard.FirstOrDefault(s => s.AlarmId == id);

            try
            {
                // Primed on the first pass: nothing yet.
                await Pass();

                // ── One sighting, two alarms: each runs all its actions ──
                IReadOnlyList<AlarmMatch> sighting = [new("intel:p:1", "Pilot 1 in QZ-X77") { Loudness = 0.5 }];
                condition.Script[near] = sighting;
                condition.Script[home] = sighting;
                await Pass();
                var n1 = await Read(near); var h1 = await Read(home);
                c.Check("service: an alarm runs every action it has: the sound, the voice and the Alert",
                    Heard(near) is { DirectText.Length: > 0, SoundKey: not null } && n1.Alerts == 1, string.Join(" | ", heard.Select(s => s.AlarmId)));
                c.Check("service: the sound plays at the match's loudness (80 × 0.5)", Heard(near) is { SoundVolume: 40 },
                    Heard(near)?.SoundVolume.ToString() ?? "no signal");
                c.Check("service: a second alarm catching the same sighting says it too: no alarm holds another back",
                    Heard(home) is { DirectText.Length: > 0 });
                c.Check("service: an alarm with speech and no Alert speaks, and both count as fired",
                    h1.Alarm is { FireCount: 1, LastFiredAt: not null } && h1.Alerts == 0 && n1.Alarm.FireCount == 1,
                    $"home fired {h1.Alarm.FireCount}, near {n1.Alarm.FireCount}");

                // ── Straight after: no wait shared between alarms ──
                condition.Script[home] = [.. sighting, new("intel:p:2", "Pilot 2 in XQ1-Z2")];
                await Pass();
                var h2 = await Read(home);
                c.Check("service: a new sighting straight after another alarm spoke is said at once",
                    Heard(home) is { DirectText.Length: > 0 } && h2.Alarm.FireCount == 2 && h2.Keys.Contains("intel:p:2"),
                    $"{h2.Alarm.FireCount} firing(s); {string.Join(",", h2.Keys)}");

                // ── Stale: banked unsaid, and a one-shot is not ended by it ──
                condition.Script[once] = [new("intel:p:5", "Pilot 5 in ZZQ-71") { Silent = true }];
                await Pass();
                var o1 = await Read(once);
                c.Check("service: stale news runs nothing: not said, no Alert, no firing", Heard(once) is null && o1.Events == 0 && o1.Alerts == 0,
                    $"{o1.Events} event(s), {o1.Alerts} alert(s)");
                c.Check("service: it is banked, so it is never said later", o1.Keys.Contains("intel:p:5"));
                c.Check("service: a one-shot alarm stays on", o1.Alarm is { Enabled: true, FireCount: 0 });
                condition.Script[once] = [new("intel:p:6", "Pilot 6 in ZZQ-71")];
                await Pass();
                var o2 = await Read(once);
                c.Check("service: news fires the one-shot, every action, and ends it",
                    Heard(once) is not null && o2.Alerts == 1 && o2.Alarm is { Enabled: false, FireCount: 1 });

                // ── The user's own {summary} and {count}: everything it fired on ──
                condition.Script[words] = [new("intel:p:7", "Pilot 7 in Q2X-91"), new("intel:p:8", "Pilot 8 in Q2X-91"),
                                           new("intel:k:9", "Kill in Q2X-91") { Urgent = true }];
                await Pass();
                var spoken = Heard(words);
                c.Check("service: the user's {summary} and {count} in a voice say all it fired on",
                    spoken?.DirectText is { } text && text.EndsWith("(3)", StringComparison.Ordinal) && text.Contains("Pilot 7", StringComparison.Ordinal)
                    && text.Contains("Pilot 8", StringComparison.Ordinal) && text.Contains("Kill in", StringComparison.Ordinal), spoken?.DirectText ?? "no signal");
                c.Check("service: and the dialog the same", spoken is { DialogTitle: "3 seen" } && spoken.DialogBody?.Contains("Pilot 8", StringComparison.Ordinal) == true,
                    $"{spoken?.DialogTitle} / {spoken?.DialogBody}");
                await using (var db = new AppDbContext(options))
                {
                    var body = await db.AlarmAlerts.Where(x => x.AlarmId == words).Select(x => x.Body).SingleOrDefaultAsync();
                    c.Check("service: and the Alert row the same", body is not null && body.Contains("Pilot 7", StringComparison.Ordinal)
                        && body.Contains("Kill in", StringComparison.Ordinal), body ?? "no alert");
                }
            }
            finally
            {
                await service.StopAsync();
                signals.Stop();
            }
        }
    }
}
