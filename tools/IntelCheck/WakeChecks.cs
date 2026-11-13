// Showing versus waking, for intel alarms: only news wakes (seen, or come into range, within the fresh window; a kill
// from when the alarm caught it), farther than the wake range only shows, the sound quieter further out, the memory of
// when each system came into reach, the wake cooldown the intel alarms share (held, not dropped; urgent skips it; a
// sighting two alarms catch said once), and the whole of it through the alarm service on a scratch database: a
// show-only firing recorded and shown but not counted, two alarms speaking a sighting once, the cooldown holding and
// then releasing, the sound's volume scaled, an urgent match going through. Every name and id here is invented.
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

        /// <summary>What wakes and what only shows, and how loud: pure, on matches from a hand-built snapshot.</summary>
        public static void Waking(Checks c)
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
            c.Check("wake: the snapshot gives the matches the cases below need",
                p1.Length > 0 && p2.Length > 0 && p3.Length > 0 && matches.Any(m => m.Key == k1) && matches.Any(m => m.Key == k2), Keys(matches));

            IReadOnlyList<AlarmMatch> Judge(IntelCondition.IntelAlarmOptions? options = null, IntelWatchMemory.Look? look = null,
                                            TimeSpan? fresh = null) =>
                IntelCondition.Waking(matches, Zone(), options ?? Defaults, Now, fresh ?? Fresh, look ?? IntelWatchMemory.Look.AllAlong);
            AlarmMatch? Of(IEnumerable<AlarmMatch> judged, string key) => judged.FirstOrDefault(m => m.Key == key);

            // ── News only ──
            var all = Judge();
            c.Check("wake: a sighting seen five minutes ago only shows (stale)", Of(all, p1) is { Quiet: true }, Keys(all));
            c.Check("wake: one seen thirty seconds ago wakes", Of(all, p2) is { Quiet: false });
            c.Check("wake: a kill ten minutes old, caught then, only shows", Of(all, k1) is { Quiet: true, Urgent: true });
            c.Check("wake: a fresh kill wakes", Of(all, k2) is { Quiet: false });

            var arrived = Judge(look: new IntelWatchMemory.Look(
                new Dictionary<int, DateTimeOffset> { [S1] = Now.AddSeconds(-10) }, new Dictionary<string, DateTimeOffset>()));
            c.Check("wake: an old sighting in a system that just came into reach is news (heard on arrival)",
                Of(arrived, p1) is { Quiet: false });
            var late = Judge(look: new IntelWatchMemory.Look(
                new Dictionary<int, DateTimeOffset>(), new Dictionary<string, DateTimeOffset> { [k1] = Now.AddSeconds(-5) }));
            c.Check("wake: a kill the feed brought late is news from when the alarm caught it", Of(late, k1) is { Quiet: false });
            c.Check("wake: the fresh window is Settings → Intel's: ten minutes, and five-minute-old news wakes",
                Of(Judge(fresh: TimeSpan.FromMinutes(10)), p1) is { Quiet: false });

            // ── The wake range ──
            var near = Judge(Defaults with { WakeJumps = 2 });
            c.Check("wake: within a wake range of 2, a sighting three jumps out only shows", Of(near, p3) is { Quiet: true });
            c.Check("wake: one a jump out still wakes", Of(near, p2) is { Quiet: false });
            c.Check("wake: a kill three jumps out wakes whatever the wake range", Of(near, k2) is { Quiet: false });

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
            var farUrgent = IntelCondition.Waking([.. matches.Select(m => m.Key == k2 ? m with { Urgent = true } : m)],
                Zone(), Defaults, Now, Fresh, IntelWatchMemory.Look.AllAlong);
            c.Check("loud: urgent news three jumps out still plays full", Of(farUrgent, k2) is { Loudness: 1 },
                Of(farUrgent, k2)?.Loudness.ToString("0.000") ?? "none");
            c.Check("loud: with Quieter further out off, everything plays full",
                Judge(Defaults with { QuieterFar = false }).All(m => m.Loudness == 1));

            // ── The editor's options ──
            using (var cfg = JsonDocument.Parse("""{"jumps":8,"wake_jumps":3,"quieter_far":false,"ly":6}"""))
            {
                var o = IntelCondition.Options(cfg.RootElement);
                c.Check("options: wake range, quieter far and the light-year range are read", o is { WakeJumps: 3, QuieterFar: false, Ly: 6 });
            }
            using (var cfg = JsonDocument.Parse("""{"jumps":8}"""))
            {
                var o = IntelCondition.Options(cfg.RootElement);
                c.Check("options: by default the whole range wakes, quieter further out", o is { WakeJumps: null, QuieterFar: true, Ly: null });
            }

            // ── An urgent kill through a cooldown ──
            {
                var fresh = Snap(seen: [Seen(P2, S2, Ago(0.2))], kills: [Kill(K3, S2, Ago(0.3), P9, (P4, HullA, FarCorp))]);
                var judged = IntelCondition.Waking(Match(fresh), Zone(), Defaults, Now, Fresh, IntelWatchMemory.Look.AllAlong)
                    .Where(m => !m.Quiet && !m.Silent).ToList();
                var gate = new AlarmWakeGate();
                gate.Woke(IntelCondition.WakeGroupName, ["intel:earlier"], Now.AddSeconds(-5));
                var cooldown = TimeSpan.FromSeconds(IntelSettings.DefaultWakeCooldownSeconds);
                var withKill = gate.Judge(IntelCondition.WakeGroupName, judged, Now, cooldown);
                var sighting = gate.Judge(IntelCondition.WakeGroupName, [.. judged.Where(m => !m.Urgent)], Now, cooldown);
                c.Check("cooldown: an urgent kill a jump out goes through the shared cooldown, the sighting with it",
                    withKill.Wake.Any(m => m.Key == $"intel:k:{K3}") && withKill.Held.Count == 0, Keys(withKill.Wake));
                c.Check("cooldown: the sighting alone is held", sighting.Wake.Count == 0 && sighting.Held.Count == 1, Keys(sighting.Held));
            }

            // ── The editor's sections ──
            var schema = JsonSerializer.SerializeToElement(new IntelCondition(null!).ParameterSchema).GetProperty("properties");
            var condition = (IAlarmCondition)new IntelCondition(null!);
            var sections = schema.EnumerateObject()
                .Select(p => p.Value.TryGetProperty("section", out var s) ? s.GetString() : null).ToList();
            c.Check("editor: every intel field is in a section, each with words of its own",
                sections.All(s => s is not null && condition.ScreenSection(s) is { Length: > 0 }), string.Join(",", sections));
            c.Check("editor: the sections come in order — where, what, waking — each in one run",
                string.Join(",", sections.Distinct()) == "where,what,waking"
                && sections.Select((s, i) => (s, i)).GroupBy(x => x.s).All(g => g.Max(x => x.i) - g.Min(x => x.i) == g.Count() - 1),
                string.Join(",", sections));
            c.Check("editor: the new fields have words in the interface language",
                condition.ScreenField("wake_jumps") is { Label.Length: > 0, Suffix.Length: > 0 } && condition.ScreenField("quieter_far") is { Label.Length: > 0 });
            c.Check("intel alarms are one wake group", condition.WakeGroup == IntelCondition.WakeGroupName);
            c.Check("the wake cooldown Settings → Intel sets is the one the intel wake group reads",
                IntelSettings.KeyWakeCooldown == AlarmWakeGate.CooldownKey(IntelCondition.WakeGroupName)
                && IntelSettings.DefaultWakeCooldownSeconds == AlarmWakeGate.DefaultCooldownSeconds
                && IntelSettings.MaxWakeCooldownSeconds == AlarmWakeGate.MaxCooldownSeconds);
            c.Check("only the Alert action shows; the rest wake",
                !AlarmActionKinds.Wakes(AlarmActionKind.Alert) && AlarmActionKinds.Wakes(AlarmActionKind.Sound)
                && AlarmActionKinds.Wakes(AlarmActionKind.TtsDirect) && AlarmActionKinds.Wakes(AlarmActionKind.AgentNotify)
                && AlarmActionKinds.Wakes(AlarmActionKind.Dialog));
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

        /// <summary>The wake cooldown and memory a wake group shares.</summary>
        public static void Gate(Checks c)
        {
            const string G = "intel";
            var cooldown = TimeSpan.FromSeconds(15);
            var gate = new AlarmWakeGate();
            AlarmMatch M(string key, bool urgent = false) => new(key, key) { Urgent = urgent };

            var first = gate.Judge(G, [M("a")], Now, cooldown);
            c.Check("gate: with nothing woken yet, it wakes", first.Wake.Count == 1 && first.Held.Count == 0);
            gate.Woke(G, ["a"], Now);

            var again = gate.Judge(G, [M("a"), M("b")], Now.AddSeconds(1), cooldown);
            c.Check("gate: a match another alarm of the group woke for is only shown (said once)",
                again.Quietened.Select(m => m.Key).SequenceEqual(["a"]));
            c.Check("gate: within the cooldown the rest is held, not dropped", again.Held.Select(m => m.Key).SequenceEqual(["b"]) && again.Wake.Count == 0);

            var urgent = gate.Judge(G, [M("b"), M("c", urgent: true)], Now.AddSeconds(2), cooldown);
            c.Check("gate: an urgent match goes through the cooldown", urgent.Wake.Count == 2 && urgent.Held.Count == 0);

            var lapsed = gate.Judge(G, [M("b")], Now.AddSeconds(16), cooldown);
            c.Check("gate: once the cooldown lapses the held match wakes", lapsed.Wake.Count == 1);

            var none = gate.Judge(G, [M("b")], Now.AddSeconds(1), TimeSpan.Zero);
            c.Check("gate: a cooldown of 0 holds nothing", none.Wake.Count == 1);

            var otherGroup = gate.Judge("other", [M("a")], Now.AddSeconds(1), cooldown);
            c.Check("gate: another group has a cooldown and memory of its own", otherGroup.Wake.Count == 1);

            var later = gate.Judge(G, [M("a")], Now + AlarmWakeGate.Remember, cooldown);
            c.Check("gate: a woken match is forgotten after a while", later.Quietened.Count == 0 && later.Wake.Count == 1);
        }

        // ── Through the alarm service ────────────────────────────────────────────────────────────────────────────

        /// <summary>A check of the intel wake group whose matches each test sets by hand, per alarm.</summary>
        private sealed class ScriptedCondition : IAlarmCondition
        {
            public const string Type = "intelcheck-wake";
            public ConcurrentDictionary<long, IReadOnlyList<AlarmMatch>> Script { get; } = new();

            public string TypeKey => Type;
            public string DisplayName => "Intel check wake";
            public string Description => "Matches set by the intel check.";
            public object ParameterSchema => new { type = "object", properties = new { } };
            public string? WakeGroup => IntelCondition.WakeGroupName;
            public string Describe(JsonElement config) => "intel check";

            public Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default) =>
                Task.FromResult(Script.TryGetValue(ctx.Alarm.Id, out var m) ? m : (IReadOnlyList<AlarmMatch>)[]);
        }

        /// <summary>
        /// Showing versus waking through the real alarm service and action runner, on a scratch database: what each
        /// alarm published (the sound, the voice) is read off the signals, what it recorded off its tables.
        /// </summary>
        public static async Task WakeServiceAsync(Checks c, DbContextOptions<AppDbContext> options)
        {
            await using (var db = new AppDbContext(options))
            {
                await db.Database.EnsureCreatedAsync();
                // A short shared cooldown, so the test can wait it out.
                db.AppPreferences.Add(new AppPreference { Key = IntelSettings.KeyWakeCooldown, Value = "2" });
                await db.SaveChangesAsync();
            }

            long near, home, once, gate;
            await using (var db = new AppDbContext(options))
            {
                Alarm New(string name, AlarmRepeat repeat = AlarmRepeat.Continuous) =>
                    new() { Name = name, ConditionType = ScriptedCondition.Type, Repeat = repeat, PollSeconds = 600, CreatedAt = DateTimeOffset.Now };
                var a = New("near me"); var b = New("home system"); var d = New("tell me once", AlarmRepeat.OneShot);
                var g = New("the gate out");
                db.Alarms.AddRange(a, b, d, g);
                await db.SaveChangesAsync();
                (near, home, once, gate) = (a.Id, b.Id, d.Id, g.Id);
                db.AlarmActions.AddRange(
                    new AlarmAction { AlarmId = near, Kind = AlarmActionKind.Sound, ConfigJson = """{"volume":80}""", Ordinal = 0 },
                    new AlarmAction { AlarmId = near, Kind = AlarmActionKind.TtsDirect, Ordinal = 1 },
                    new AlarmAction { AlarmId = near, Kind = AlarmActionKind.Alert, Ordinal = 2 },
                    new AlarmAction { AlarmId = home, Kind = AlarmActionKind.TtsDirect, Ordinal = 0 },
                    new AlarmAction { AlarmId = home, Kind = AlarmActionKind.Alert, Ordinal = 1 },
                    new AlarmAction { AlarmId = once, Kind = AlarmActionKind.TtsDirect, Ordinal = 0 },
                    new AlarmAction { AlarmId = once, Kind = AlarmActionKind.Alert, Ordinal = 1 },
                    // The capsuleer's own words: what {summary} and {count} say must be only what wakes.
                    new AlarmAction { AlarmId = gate, Kind = AlarmActionKind.TtsDirect, ConfigJson = """{"message":"{summary} ({count})"}""", Ordinal = 0 },
                    new AlarmAction { AlarmId = gate, Kind = AlarmActionKind.Dialog, ConfigJson = """{"title":"{count} woke","message":"{summary}"}""", Ordinal = 1 },
                    new AlarmAction { AlarmId = gate, Kind = AlarmActionKind.Alert, ConfigJson = """{"body":"{summary}"}""", Ordinal = 2 });
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
            AlarmSignal? From(long id) => heard.FirstOrDefault(s => s.AlarmId == id);

            try
            {
                // Primed on the first pass: nothing yet.
                await Pass();

                // ── One sighting, two alarms: said once ──
                IReadOnlyList<AlarmMatch> sighting = [new("intel:p:1", "Pilot 1 in QZ-X77") { Loudness = 0.5 }];
                condition.Script[near] = sighting;
                condition.Script[home] = sighting;
                await Pass();
                var n1 = await Read(near); var h1 = await Read(home);
                c.Check("service: the first alarm to catch a sighting says it", From(near) is { DirectText.Length: > 0 }, string.Join(" | ", heard.Select(s => s.AlarmId)));
                c.Check("service: the second alarm that caught it does not say it again", From(home) is null);
                c.Check("service: the second records it and shows it as an Alert", h1.Events == 1 && h1.Alerts == 1 && h1.Keys.Contains("intel:p:1"),
                    $"{h1.Events} event(s), {h1.Alerts} alert(s)");
                c.Check("service: a show-only firing does not count as the alarm firing", h1.Alarm is { LastFiredAt: null, FireCount: 0 });
                c.Check("service: the waking one counts", n1.Alarm is { FireCount: 1, LastFiredAt: not null });
                c.Check("service: the sound plays at the match's loudness (80 × 0.5)", From(near) is { SoundVolume: 40, SoundKey: not null },
                    From(near)?.SoundVolume.ToString() ?? "no signal");

                // ── Within the shared cooldown: held, not dropped ──
                condition.Script[home] = [.. sighting, new("intel:p:2", "Pilot 2 in XQ1-Z2")];
                await Pass();
                var h2 = await Read(home);
                c.Check("service: within the shared cooldown a new sighting is not said", From(home) is null);
                c.Check("service: and not recorded or banked either: held for the next look",
                    h2.Events == 1 && !h2.Keys.Contains("intel:p:2"), $"{h2.Events} event(s); {string.Join(",", h2.Keys)}");

                // ── Urgent goes through it ──
                condition.Script[near] = [.. sighting, new("intel:k:3", "Kill in QZ-X77") { Urgent = true }];
                await Pass();
                c.Check("service: an urgent kill is said within the cooldown", From(near) is { DirectText.Length: > 0, SoundVolume: 80 },
                    From(near)?.SoundVolume.ToString() ?? "no signal");
                c.Check("service: the held sighting stays held: the urgent wake began the cooldown again", From(home) is null);

                // ── The cooldown lapses: the held sighting is said ──
                await Task.Delay(TimeSpan.FromSeconds(2.3));
                await Pass();
                var h3 = await Read(home);
                c.Check("service: once the cooldown lapses the held sighting is said", From(home) is { DirectText.Length: > 0 },
                    string.Join(" | ", heard.Select(s => s.AlarmId)));
                c.Check("service: and the alarm fired", h3.Alarm.FireCount == 1 && h3.Keys.Contains("intel:p:2"));

                // ── Stale: shows only, and a one-shot is not ended by it ──
                await Task.Delay(TimeSpan.FromSeconds(2.3));
                condition.Script[once] = [new("intel:p:5", "Pilot 5 in ZZQ-71") { Quiet = true }];
                await Pass();
                var o1 = await Read(once);
                c.Check("service: a stale match is not said", From(once) is null);
                c.Check("service: it is recorded and shown as an Alert", o1.Events == 1 && o1.Alerts == 1 && o1.Keys.Contains("intel:p:5"));
                c.Check("service: a one-shot alarm stays on: only a wake ends it", o1.Alarm is { Enabled: true, LastFiredAt: null, FireCount: 0 });
                await using (var db = new AppDbContext(options))
                {
                    var detail = await db.AlarmEvents.Where(x => x.AlarmId == once).Select(x => x.DetailJson).SingleAsync();
                    c.Check("service: the event says it did not wake", detail is not null && detail.Contains("\"wake\":false", StringComparison.Ordinal), detail ?? "");
                }

                condition.Script[once] = [new("intel:p:6", "Pilot 6 in ZZQ-71")];
                await Pass();
                var o2 = await Read(once);
                c.Check("service: news then wakes the one-shot and ends it", From(once) is not null && o2.Alarm is { Enabled: false, FireCount: 1 });

                // ── The user's own {summary}: said once, stale only shows, inside the words too ──
                await Task.Delay(TimeSpan.FromSeconds(2.3));
                AlarmMatch said = new("intel:p:7", "Pilot 7 in Q2X-91");
                condition.Script[near] = [.. condition.Script[near], said];
                await Pass();
                c.Check("service: (the first alarm says the sighting)", From(near) is not null);
                condition.Script[gate] = [said, new("intel:p:8", "Pilot 8 in Q2X-91") { Quiet = true }, new("intel:k:9", "Kill in Q2X-91") { Urgent = true }];
                await Pass();
                var spoken = From(gate);
                c.Check("service: the user's {summary} in a voice says only what wakes, not what another alarm said or what is stale",
                    spoken?.DirectText == "Kill in Q2X-91 (1)", spoken?.DirectText ?? "no signal");
                c.Check("service: and in a dialog, with {count} the waking count",
                    spoken is { DialogTitle: "1 woke", DialogBody: "Kill in Q2X-91" }, $"{spoken?.DialogTitle} / {spoken?.DialogBody}");
                c.Check("service: the signal's summary is what wakes", spoken?.Summary == "Kill in Q2X-91", spoken?.Summary ?? "no signal");
                await using (var db = new AppDbContext(options))
                {
                    var body = await db.AlarmAlerts.Where(x => x.AlarmId == gate).Select(x => x.Body).SingleOrDefaultAsync();
                    var evt  = await db.AlarmEvents.Where(x => x.AlarmId == gate).SingleOrDefaultAsync();
                    c.Check("service: the Alert row's {summary} still shows all three",
                        evt is { MatchCount: 3 } && body is not null && body.Contains("Pilot 7", StringComparison.Ordinal)
                        && body.Contains("Pilot 8", StringComparison.Ordinal) && body.Contains("Kill in", StringComparison.Ordinal), body ?? "no alert");
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
