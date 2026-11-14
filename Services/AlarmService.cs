using System.Globalization;
using System.Text.Json;
using EveConsole.Alarms;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Evaluates alarms and fires the ones with something new to say.
///
/// <para>The rule that keeps alarms from becoming noise is that a check reports <em>what</em> it
/// matched rather than merely <em>that</em> it matched. Each match carries a stable key; keys this
/// alarm has already announced are banked in <c>AlarmSeenKeys</c> and are not news. So a hostile
/// parked in a system for an hour produces one alert, a second hostile produces a second, and
/// several arriving between two evaluations coalesce into one firing rather than a burst.</para>
/// </summary>
public sealed class AlarmService : ReactiveObject
{
    /// <summary>Nothing is evaluated faster than this regardless of what an alarm asks for.</summary>
    private const int MinPollSeconds = 2;

    /// <summary>
    /// How often the loop wakes to see whether any alarm is due. Cheap: a tick with nothing due
    /// costs one small query, and checks that matter are usually driven by
    /// <see cref="TriggerAsync"/> rather than by waiting for this.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan SeenKeyRetention = TimeSpan.FromDays(30);
    private static readonly TimeSpan PruneInterval    = TimeSpan.FromHours(6);
    private const int MaxSeenKeysPerAlarm = 5_000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly string                          _connString;
    private readonly AlarmConditionRegistry          _registry;
    private readonly AlarmActionRunner               _actions;
    private readonly AppErrorLogger                  _errors;
    private readonly ClientSignals?                  _signals;

    /// <summary>Discriminator of the nudge a PC that is not the worker sends instead of evaluating.</summary>
    public const string TriggerSignalKind = "alarm-trigger";

    private CancellationTokenSource? _cts;
    private Task?          _loop;
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>One evaluation pass at a time — the timer loop and a trigger must not overlap.</summary>
    private readonly SemaphoreSlim _passGate = new(1, 1);

    /// <summary>Next-due times, kept in memory so a tick costs nothing when nothing is due.</summary>
    private readonly Dictionary<long, DateTimeOffset> _nextDue = [];

    public AlarmService(
        IDbContextFactory<AppDbContext> dbFactory,
        string                          connString,
        AlarmConditionRegistry          registry,
        AlarmActionRunner               actions,
        AppErrorLogger                  errors,
        ClientSignals?                  signals  = null,
        AlarmWakeGate?                  wakeGate = null)
    {
        _dbFactory  = dbFactory;
        _connString = connString;
        _registry   = registry;
        _actions    = actions;
        _errors     = errors;
        _signals    = signals;
        _wakeGate   = wakeGate ?? new AlarmWakeGate();
    }

    /// <summary>The wake cooldown and memory each wake group's alarms share (see <see cref="IAlarmCondition.WakeGroup"/>).</summary>
    private readonly AlarmWakeGate _wakeGate;

    /// <summary>Each wake group's cooldown as last read, and when: read again at most every
    /// <see cref="CooldownReadEvery"/>.</summary>
    private readonly Dictionary<string, (TimeSpan Cooldown, DateTimeOffset At)> _cooldowns = new(StringComparer.Ordinal);
    private static readonly TimeSpan CooldownReadEvery = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A wake group's cooldown, from the shared preference (<see cref="AlarmWakeGate.CooldownKey"/>),
    /// default <see cref="AlarmWakeGate.DefaultCooldownSeconds"/>, 0 for none.
    /// <para>⚠️ Read from the database, never this PC's preference cache: it is set on Settings → Intel,
    /// on whichever PC, and the worker that evaluates may be another one that loaded its copy at
    /// startup.</para>
    /// </summary>
    private async Task<TimeSpan> WakeCooldownAsync(AppDbContext db, string group, DateTimeOffset now, CancellationToken ct)
    {
        if (_cooldowns.TryGetValue(group, out var held) && now - held.At < CooldownReadEvery && now >= held.At)
            return held.Cooldown;

        var key  = AlarmWakeGate.CooldownKey(group);
        var text = await db.AppPreferences.AsNoTracking().Where(p => p.Key == key).Select(p => p.Value).FirstOrDefaultAsync(ct);
        var seconds = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Clamp(n, 0, AlarmWakeGate.MaxCooldownSeconds)
            : AlarmWakeGate.DefaultCooldownSeconds;
        var cooldown = TimeSpan.FromSeconds(seconds);
        _cooldowns[group] = (cooldown, now);
        return cooldown;
    }

    public AlarmConditionRegistry Registry => _registry;

    // ⚠️ In this machine's interface language — and it is also what other clients are sent as
    // the worker's status, so they see it in the worker's.
    private string _statusText = DataText.StateIdle;
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private int _armedCount;
    public int ArmedCount
    {
        get => _armedCount;
        private set => this.RaiseAndSetIfChanged(ref _armedCount, value);
    }

    private DateTimeOffset? _nextDueAt;
    public DateTimeOffset? NextDueAt
    {
        get => _nextDueAt;
        private set => this.RaiseAndSetIfChanged(ref _nextDueAt, value);
    }

    private DateTimeOffset? _lastFireAt;
    public DateTimeOffset? LastFireAt
    {
        get => _lastFireAt;
        private set => this.RaiseAndSetIfChanged(ref _lastFireAt, value);
    }

    /// <summary>Raised after any firing so open views can refresh without polling the database.</summary>
    public event Action? Fired;

    public void Start()
    {
        if (_loop is not null) return;
        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }

        // ⚠️ Cleared, not merely cancelled. A CancellationTokenSource stays cancelled once it
        // has been, so restarting onto the same one hands the loop a token that is already dead:
        // it returns on its first await and never runs again. Stop used to be called only on the
        // way out, where that could not matter. The worker lease can be lost and regained, so it
        // has to be an undoable thing now.
        _cts.Dispose();
        _cts  = null;
        _loop = null;
    }

    /// <summary>
    /// Forces the next tick to re-evaluate an alarm immediately — used after the editor saves,
    /// so a change takes effect without waiting out the old interval.
    /// </summary>
    public void Invalidate(long alarmId)
    {
        lock (_nextDue) _nextDue.Remove(alarmId);
    }

    /// <summary>
    /// Re-evaluates every alarm of a given condition type right now, rather than at its next
    /// interval. Called by whatever produced the data — for intel, the parser calls this the
    /// moment it has written new sightings, so the alarm fires within a second of the post
    /// instead of waiting out a poll it has no way of knowing is pointless.
    ///
    /// <para>A fixed interval is the fallback for sources that cannot say when they changed,
    /// which is most of them; anything that can say so should.</para>
    ///
    /// <para>⚠️ Alarms are evaluated on the worker only (the loop runs there). Anywhere else this
    /// asks the worker, with an <see cref="TriggerSignalKind"/> signal, and evaluates nothing: a PC
    /// that is not the worker evaluating its own copy would fire every alarm a second time, from a
    /// second set of seen keys read a moment apart.</para>
    /// </summary>
    public async Task TriggerAsync(string conditionType, CancellationToken ct = default)
    {
        if (_loop is null)
        {
            await PublishTriggerAsync(conditionType, ct);
            return;
        }

        try
        {
            if (!await ExpireDueTimesAsync(conditionType, ct)) return;

            // Evaluate immediately rather than waiting for the next tick, but never on top of a
            // pass already running — the seen-key diff is read-then-write and two overlapping
            // passes could each decide the same match was new.
            if (!await _passGate.WaitAsync(0, ct)) return;
            try { await TickAsync(ct); }
            finally { _passGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _errors.Log("AlarmService", $"trigger {conditionType}", ex); }
    }

    private async Task PublishTriggerAsync(string conditionType, CancellationToken ct)
    {
        if (_signals is null) return;
        try
        {
            await _signals.PublishAsync(JsonSerializer.Serialize(new TriggerSignal
            {
                Kind = TriggerSignalKind, Type = conditionType,
            }), ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _errors.Log("AlarmService", $"asking the worker to evaluate {conditionType}", ex); }
    }

    /// <summary>
    /// The nudge from a PC that is not the worker; false for any other signal. On the worker the
    /// alarms of that type are made due at once, and the loop's next tick (within
    /// <see cref="TickInterval"/>) evaluates them, so a nudge never adds a pass of its own or meets
    /// one already running. Anywhere else it is heard and ignored.
    /// </summary>
    public bool TryApplySignal(string payload)
    {
        if (!payload.Contains(TriggerSignalKind, StringComparison.Ordinal)) return false;

        TriggerSignal? signal;
        try { signal = JsonSerializer.Deserialize<TriggerSignal>(payload); }
        catch (JsonException) { return false; }
        if (signal is not { Kind: TriggerSignalKind }) return false;

        if (_loop is not null && !string.IsNullOrEmpty(signal.Type))
            _ = Task.Run(async () =>
            {
                try { await ExpireDueTimesAsync(signal.Type, CancellationToken.None); }
                catch (Exception ex) { _errors.Log("AlarmService", $"nudged to evaluate {signal.Type}", ex); }
            });
        return true;
    }

    private sealed class TriggerSignal
    {
        public string Kind { get; set; } = "";
        public string Type { get; set; } = "";
    }

    /// <summary>Makes every enabled, active alarm of the type due now; false when there is none.</summary>
    private async Task<bool> ExpireDueTimesAsync(string conditionType, CancellationToken ct)
    {
        List<long> ids;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var now = DateTimeOffset.Now;
            ids = (await db.Alarms
                .Where(a => a.Enabled && a.ConditionType == conditionType)
                .ToListAsync(ct))
                .Where(a => a.IsActiveAt(now))
                .Select(a => a.Id)
                .ToList();
        }

        if (ids.Count == 0) return false;

        lock (_nextDue)
            foreach (var id in ids) _nextDue.Remove(id);
        return true;
    }

    /// <summary>
    /// Banks an alarm's already-matching state at the moment it is saved, rather than leaving
    /// it for the first tick. Closes a narrow but real gap: an alarm saved just before it comes
    /// due would otherwise be primed *after* the fact, and its one occurrence banked as history
    /// instead of announced.
    /// </summary>
    public async Task PrimeAsync(long alarmId, CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var alarm = await db.Alarms.FirstOrDefaultAsync(a => a.Id == alarmId, ct);
            if (alarm is null || alarm.Primed) return;

            var condition = _registry.Find(alarm.ConditionType);
            if (condition is null) return;

            var config  = JsonDocument.Parse(alarm.ConditionJson ?? "{}").RootElement.Clone();
            var matches = await condition.EvaluateAsync(config, new AlarmEvaluationContext
            {
                DbFactory        = _dbFactory,
                ConnectionString = _connString,
                Alarm            = alarm,
                Now              = DateTimeOffset.Now,
            }, ct);

            if (matches.Count > 0) BankKeys(db, alarmId, matches, await SeenAsync(db, alarmId, ct), DateTimeOffset.Now);
            alarm.Primed = true;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Leaving it unprimed is safe — the next tick primes it instead.
            _errors.Log("AlarmService", $"prime {alarmId}", ex);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Let startup settle before touching the database.
        try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TickInterval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _passGate.WaitAsync(ct);
                try { await TickAsync(ct); }
                finally { _passGate.Release(); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors.Log("AlarmService", "tick", ex); }

            try { await timer.WaitForNextTickAsync(ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Alarm counts are in the tens, so loading them whole beats a filtered query — and
        // EF Core cannot translate a DateTimeOffset comparison against SQLite anyway, so the
        // due-time test has to happen in memory regardless.
        var alarms = await db.Alarms.Where(a => a.Enabled).ToListAsync(ct);
        ArmedCount = alarms.Count;

        if (alarms.Count == 0)
        {
            StatusText = DataText.AlarmsStatusNoneArmed;
            NextDueAt  = null;
            return;
        }

        var due = new List<Alarm>();
        lock (_nextDue)
        {
            // Drop bookkeeping for alarms that have been deleted or disabled.
            var live = alarms.Select(a => a.Id).ToHashSet();
            foreach (var gone in _nextDue.Keys.Where(k => !live.Contains(k)).ToList())
                _nextDue.Remove(gone);

            foreach (var a in alarms)
                if (a.IsActiveAt(now) && (!_nextDue.TryGetValue(a.Id, out var at) || at <= now))
                    due.Add(a);

            NextDueAt = _nextDue.Count > 0 ? _nextDue.Values.Min() : now;
        }

        if (due.Count == 0)
        {
            StatusText = string.Format(DataText.AlarmsStatusNextCheck, alarms.Count, Relative(NextDueAt, now));
            return;
        }

        var fired = 0;
        var shown = 0;
        foreach (var alarm in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                switch (await EvaluateAsync(db, alarm, now, ct))
                {
                    case Outcome.Woke:  fired++; break;
                    case Outcome.Shown: shown++; break;
                }
            }
            catch (Exception ex)
            {
                _errors.Log("AlarmService", $"alarm {alarm.Id} ({alarm.Name})", ex);
                alarm.LastError = ex.Message;
                DropPending(db, alarm.Id);
            }

            alarm.LastCheckedAt = now;
            lock (_nextDue)
                _nextDue[alarm.Id] = now.AddSeconds(Math.Max(MinPollSeconds, alarm.PollSeconds));
        }

        await db.SaveChangesAsync(ct);

        // A show-only firing is not the alarm firing, but its history and Alert row are new: open
        // views refresh for it all the same.
        if (fired > 0) LastFireAt = now;
        if (fired + shown > 0) Fired?.Invoke();

        StatusText = fired > 0
            ? string.Format(DataText.AlarmsStatusCheckedFired, alarms.Count, due.Count, fired)
            : string.Format(DataText.AlarmsStatusChecked, alarms.Count, due.Count);

        if (now - _lastPrune > PruneInterval)
        {
            _lastPrune = now;
            try { await PruneSeenKeysAsync(db, now, ct); }
            catch (Exception ex) { _errors.Log("AlarmService", "prune", ex); }
        }
    }

    /// <summary>What one evaluation of an alarm came to.</summary>
    private enum Outcome
    {
        /// <summary>Nothing new, or all of it held back by a cooldown.</summary>
        None,
        /// <summary>Recorded and shown only (the Alert row): nothing new enough or near enough to wake for.</summary>
        Shown,
        /// <summary>Fired: something woke the capsuleer, or would have, had the alarm a waking action.</summary>
        Woke,
    }

    private async Task<Outcome> EvaluateAsync(AppDbContext db, Alarm alarm, DateTimeOffset now, CancellationToken ct)
    {
        var condition = _registry.Find(alarm.ConditionType);
        if (condition is null)
        {
            alarm.LastError = $"Unknown condition type '{alarm.ConditionType}'.";
            return Outcome.None;
        }

        JsonElement config;
        try { config = JsonDocument.Parse(alarm.ConditionJson ?? "{}").RootElement.Clone(); }
        catch (Exception ex) { alarm.LastError = $"Bad condition config: {ex.Message}"; return Outcome.None; }

        var seenSet = await SeenAsync(db, alarm.Id, ct);

        var ctx = new AlarmEvaluationContext
        {
            DbFactory        = _dbFactory,
            ConnectionString = _connString,
            Alarm            = alarm,
            Now              = now,
            Seen             = seenSet,
        };

        var matches = await condition.EvaluateAsync(config, ctx, ct);
        alarm.LastError = null;

        // Priming banks whatever already matched when the alarm was created, so switching on a
        // killmail alarm does not immediately announce every kill in history.
        //
        // This has to happen on the first evaluation whether or not anything matched. Priming
        // only on the first *match* would mean a timer set for later today is still unprimed
        // when it comes due, so its one and only occurrence would be banked as backlog and the
        // alarm would never fire at all.
        if (!alarm.Primed)
        {
            if (matches.Count > 0) BankKeys(db, alarm.Id, matches, seenSet, now);
            alarm.Primed = true;
            return Outcome.None;
        }

        // Checks over mutable state forget keys that have gone away, so the same key can be
        // news again if it returns. Runs before the empty-set exit, because an empty result IS
        // the signal that re-arms an "alert me when there are no rows" alarm.
        if (condition.ForgetsUnseenKeys)
            await ForgetVanishedKeysAsync(db, alarm.Id, matches, ct);

        if (matches.Count == 0) return Outcome.None;

        // Silent matches are banked and never announced: what a check folded into another
        // announcement, or an event kind the user switched off, which must not fire later if
        // switched back on.
        var unseen = matches.Where(m => !seenSet.Contains(m.Key)).ToList();
        var fresh  = unseen.Where(m => !m.Silent).ToList();
        if (fresh.Count == 0)
        {
            if (unseen.Count > 0) BankKeys(db, alarm.Id, unseen, seenSet, now);
            return Outcome.None;
        }

        // A staged check sets its own cadence — a cooldown would swallow stage two, and one-shot
        // would end the alarm at stage one.
        var staged = condition.Stages > 0;

        // A cooldown suppresses the firing but must NOT bank the keys, or the matches it is
        // damping would be lost for good. They stay unseen and go out together once it lapses.
        if (!staged && alarm.CooldownSeconds > 0 && alarm.LastFiredAt is { } last
            && now < last.AddSeconds(alarm.CooldownSeconds))
        {
            return Outcome.None;
        }

        var actions = await db.AlarmActions.AsNoTracking()
            .Where(a => a.AlarmId == alarm.Id)
            .OrderBy(a => a.Ordinal)
            .ToListAsync(ct);

        // ── Showing versus waking ──
        // What would wake: every fresh match the check did not mark quiet. An alarm of a wake group
        // with something to wake with then asks the group: a match another alarm of it already woke
        // for is only shown, and within the group's cooldown the rest are held — unless one is urgent.
        // A held match is neither shown nor banked: the next evaluation judges it again.
        var show = fresh;
        var wake = staged ? fresh : fresh.Where(m => !m.Quiet).ToList();
        var wakeGroup = staged ? null : condition.WakeGroup;
        if (wakeGroup is not null && wake.Count > 0 && actions.Any(a => AlarmActionKinds.Wakes(a.Kind)))
        {
            var decision = _wakeGate.Judge(wakeGroup, wake, now, await WakeCooldownAsync(db, wakeGroup, now, ct));
            if (decision.Held.Count > 0)
            {
                var held = decision.Held.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
                show   = [.. fresh.Where(m => !held.Contains(m.Key))];
                unseen = [.. unseen.Where(m => !held.Contains(m.Key))];
            }
            wake = [.. decision.Wake];
            if (show.Count == 0) return Outcome.None;
            if (wake.Count > 0) _wakeGate.Woke(wakeGroup, wake.Select(m => m.Key), now);
        }
        var woke     = wake.Count > 0;
        var wakeKeys = wake.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);

        var evt = new AlarmEvent
        {
            AlarmId    = alarm.Id,
            FiredAt    = now,
            MatchCount = show.Count,
            Summary    = BuildSummary(show),
            DetailJson = JsonSerializer.Serialize(show.Select(m => new
            {
                key     = m.Key,
                summary = m.Summary,
                detail  = m.Detail,
                wake    = wakeKeys.Contains(m.Key),
            })),
        };
        db.AlarmEvents.Add(evt);

        BankKeys(db, alarm.Id, unseen, seenSet, now);

        // ⚠️ Only a firing that woke someone counts as the alarm firing. A show-only one is recorded
        // and shown, but a one-shot "tell me when" must end on a real wake-up, never a stale
        // catch-up row, and "last fired" must say when it last made a sound.
        if (woke)
        {
            alarm.LastFiredAt = now;
            alarm.FireCount  += 1;
            if (!staged && alarm.Repeat == AlarmRepeat.OneShot) alarm.Enabled = false;
        }

        // Persist before acting: an action that raises a dialog or calls the agent must not be
        // able to run twice because the write that recorded it had not landed yet.
        await db.SaveChangesAsync(ct);

        if (!staged)
        {
            // The condition supplies wording for any Alert or Dialog left on its default, so what
            // the capsuleer reads reflects what was actually being watched for: the Alert row all
            // that is shown, the dialog and the voice what wakes.
            var defaults = condition.DefaultText(alarm.Name, config, show);
            var wakeSet  = woke
                ? new AlarmWakeSet(wake,
                    wake.Count == show.Count ? defaults : condition.DefaultText(alarm.Name, config, wake),
                    // For a condition that would rather be quoted than paraphrased, the exact words.
                    condition.Announcement(config, wake),
                    // What {summary} says in the waking actions: what wakes, not all that is shown.
                    BuildSummary(wake))
                : AlarmWakeSet.None;

            await _actions.RunAsync(alarm, actions, evt, show, defaults, wakeSet.Announcement, ct: ct, wake: wakeSet);
            return woke ? Outcome.Woke : Outcome.Shown;
        }

        // Staged: one firing per scope and stage — two pilots adrift are two wake-up calls, each
        // with the actions under its stage (an action with no stage, as the agent's tool may
        // make, runs at every stage).
        foreach (var group in fresh.GroupBy(m => (Stage: IAlarmCondition.StageOf(m), Scope: DetailText(m, "scope_key"))))
        {
            var list  = group.ToList();
            var first = list[0];
            var stage = new AlarmStageInfo(
                group.Key.Stage,
                group.Key.Scope,
                DetailText(first, "episode"),
                first.Detail is { } d && d.TryGetValue("snooze_minutes", out var sm) && sm is int m ? m : 30,
                condition.TypeKey);

            var stageActions = actions.Where(a => ActionStage(a) is not { } s || s == group.Key.Stage).ToList();
            if (stageActions.Count == 0) continue;

            await _actions.RunAsync(
                alarm, stageActions, evt, list,
                condition.DefaultText(alarm.Name, config, list),
                condition.Announcement(config, list),
                stage,
                condition.AgentPrompt(config, list),
                ct);
        }
        return Outcome.Woke;
    }

    private static string DetailText(AlarmMatch m, string key)
        => m.Detail is { } d && d.TryGetValue(key, out var v) && v is string s ? s : "";

    /// <summary>The stage an action runs at, from its config — null for every stage.</summary>
    internal static int? ActionStage(AlarmAction action)
    {
        try
        {
            using var doc = JsonDocument.Parse(action.ConfigJson ?? "{}");
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("stage", out var p)
                && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var stage) && stage > 0
                ? stage : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Drops banked keys that are no longer in the current match set, for conditions that opt
    /// in. Written as one statement rather than a load-and-diff because the ledger for a busy
    /// alarm is the one thing here that can get large.
    /// </summary>
    private static async Task ForgetVanishedKeysAsync(
        AppDbContext db, long alarmId, IReadOnlyList<AlarmMatch> current, CancellationToken ct)
    {
        if (current.Count == 0)
        {
            await db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "AlarmSeenKeys" WHERE "AlarmId" = {0}""", [alarmId], ct);
            return;
        }

        var parameters = new List<object> { alarmId };
        var slots      = new List<string>(current.Count);
        foreach (var m in current)
        {
            slots.Add($"{{{parameters.Count}}}");
            parameters.Add(m.Key);
        }

        // Only placeholder text is interpolated — "{1}", "{2}" and so on. Every actual value,
        // including keys that came from a user-written query, travels in `parameters`.
        // $$ so a lone {0} stays literal and {{ }} interpolates.
#pragma warning disable EF1002 // interpolated values are placeholders, not data
        await db.Database.ExecuteSqlRawAsync(
            $$"""DELETE FROM "AlarmSeenKeys" WHERE "AlarmId" = {0} AND "MatchKey" NOT IN ({{string.Join(",", slots)}})""",
            parameters, ct);
#pragma warning restore EF1002
    }

    private static async Task<HashSet<string>> SeenAsync(AppDbContext db, long alarmId, CancellationToken ct)
    {
        var seen = await db.AlarmSeenKeys.AsNoTracking()
            .Where(k => k.AlarmId == alarmId)
            .Select(k => k.MatchKey)
            .ToListAsync(ct);
        return seen.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Adds each key in <paramref name="matches"/> to the ledger once, skipping those already in
    /// <paramref name="banked"/> — which is grown as it goes, so the caller's set stays true.
    /// </summary>
    private static void BankKeys(
        AppDbContext db, long alarmId, IEnumerable<AlarmMatch> matches, HashSet<string> banked, DateTimeOffset now)
    {
        // ⚠️ One row per KEY, not per match. A condition may key several matches alike on
        // purpose — intel keys the same pilots in the same system inside five minutes as one
        // sighting however many people called it — and the ledger's primary key takes each once;
        // EF's identity map refused the second Add before the database ever saw it, which
        // left the alarm unprimed with half its keys landed, and the next tick tripping over
        // those. The set is what is already on the ledger, so a priming pass after a partial
        // bank is safe too.
        foreach (var m in matches)
            if (banked.Add(m.Key))
                db.AlarmSeenKeys.Add(new AlarmSeenKey
                {
                    AlarmId     = alarmId,
                    MatchKey    = m.Key,
                    FirstSeenAt = now,
                });
    }

    /// <summary>
    /// Detaches whatever a failed evaluation had queued for this alarm — ledger rows, its event —
    /// so it neither lands half done nor takes the tick's own save, and every other alarm's
    /// bookkeeping with it, down. Anything already saved by the evaluation is not pending.
    /// </summary>
    private static void DropPending(AppDbContext db, long alarmId)
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
        {
            var mine = e.Entity switch
            {
                AlarmSeenKey k  => k.AlarmId  == alarmId,
                AlarmEvent   ev => ev.AlarmId == alarmId,
                _               => false,
            };
            if (mine) e.State = EntityState.Detached;
        }
    }

    // Matches that share a key are one thing said several times; the record says it once.
    private static string BuildSummary(IReadOnlyList<AlarmMatch> fresh)
    {
        var lines = fresh.Select(m => m.Summary).Distinct(StringComparer.Ordinal).ToList();
        return lines.Count == 1
            ? lines[0]
            : $"{lines.Count} new: " + string.Join("; ", lines.Take(3))
              + (lines.Count > 3 ? $"; +{lines.Count - 3} more" : "");
    }

    /// <summary>
    /// Keeps the ledger from growing without bound on high-volume checks. Retention is far
    /// longer than any plausible re-match window, so pruning cannot resurrect old news.
    /// </summary>
    private static async Task PruneSeenKeysAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // ⚠️ A DateTimeOffset, not a string shaped like one. FirstSeenAt is a timestamptz
        // on a server and PostgreSQL will not compare one against text at all: "operator does not
        // exist: timestamp with time zone < text".
        //
        // The string this replaced existed to match EF Core's on-disk shape for SQLite (space
        // separator, trailing offset), because an ISO "o" string sorts above every stored value
        // there — 'T' > ' ' — which made the comparison true for every row and emptied
        // the ledger, at which point every alarm re-announced everything it had ever seen. Handing
        // the provider a real DateTimeOffset gets that shape from the provider itself on SQLite,
        // and a typed comparison on PostgreSQL, so neither engine is being guessed at.
        var cutoff = (now - SeenKeyRetention).ToUniversalTime();
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "AlarmSeenKeys" WHERE "FirstSeenAt" < {0}""", [cutoff], ct);

        // Acknowledgements are only meaningful while their episode lasts; a day is generous.
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "AlarmSnoozes" WHERE "Until" < {0}""", [now.ToUniversalTime().AddDays(-1)], ct);

        // ⚠️ EF1002 suppressed rather than worked around, and only because of what is interpolated:
        // AppDb.RowId is the engine's row-address identifier ("rowid" or "ctid") and
        // MaxSeenKeysPerAlarm is a compile-time const. An identifier cannot be a parameter — that
        // is a SQL rule, not an EF one — and neither value can come from input. Contrast the
        // statement above, whose value goes through a {0} parameter, as any value must.
#pragma warning disable EF1002 // interpolated parts are an engine identifier and a const, never input
        await db.Database.ExecuteSqlRawAsync($"""
            DELETE FROM "AlarmSeenKeys" WHERE {AppDb.RowId} IN (
              SELECT {AppDb.RowId} FROM (
                SELECT {AppDb.RowId}, ROW_NUMBER() OVER (PARTITION BY "AlarmId" ORDER BY "FirstSeenAt" DESC) AS rn
                FROM "AlarmSeenKeys")
              WHERE rn > {MaxSeenKeysPerAlarm})
            """, ct);
#pragma warning restore EF1002
    }

    /// <summary>When the next check is, for <see cref="DataText.AlarmsStatusNextCheck"/>: "now", "in 40s".</summary>
    private static string Relative(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "—";
        var d = at.Value - now;
        if (d <= TimeSpan.Zero)     return DataText.AlarmsNextCheckNow;
        if (d < TimeSpan.FromMinutes(1)) return string.Format(DataText.AlarmsNextCheckInSeconds, d.TotalSeconds);
        if (d < TimeSpan.FromHours(1))   return string.Format(DataText.AlarmsNextCheckInMinutes, d.TotalMinutes);
        return string.Format(DataText.AlarmsNextCheckInHours, d.TotalHours);
    }
}
