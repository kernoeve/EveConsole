using System.Globalization;
using System.Text.Json;
using EveConsole.Alarms;
using EveConsole.Data;
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
    private const int MinPollSeconds = 5;

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
        AppErrorLogger                  errors)
    {
        _dbFactory  = dbFactory;
        _connString = connString;
        _registry   = registry;
        _actions    = actions;
        _errors     = errors;
    }

    public AlarmConditionRegistry Registry => _registry;

    private string _statusText = "Idle";
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
    /// </summary>
    public async Task TriggerAsync(string conditionType, CancellationToken ct = default)
    {
        try
        {
            List<long> ids;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            {
                ids = await db.Alarms
                    .Where(a => a.Enabled && a.ConditionType == conditionType)
                    .Select(a => a.Id)
                    .ToListAsync(ct);
            }

            if (ids.Count == 0) return;

            lock (_nextDue)
                foreach (var id in ids) _nextDue.Remove(id);

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
            StatusText = "No alarms armed";
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
                if (!_nextDue.TryGetValue(a.Id, out var at) || at <= now)
                    due.Add(a);

            NextDueAt = _nextDue.Count > 0 ? _nextDue.Values.Min() : now;
        }

        if (due.Count == 0)
        {
            StatusText = $"{alarms.Count} armed · next check {Relative(NextDueAt, now)}";
            return;
        }

        var fired = 0;
        foreach (var alarm in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (await EvaluateAsync(db, alarm, now, ct)) fired++;
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

        if (fired > 0)
        {
            LastFireAt = now;
            Fired?.Invoke();
        }

        StatusText = $"{alarms.Count} armed · checked {due.Count}" +
                     (fired > 0 ? $" · fired {fired}" : "");

        if (now - _lastPrune > PruneInterval)
        {
            _lastPrune = now;
            try { await PruneSeenKeysAsync(db, now, ct); }
            catch (Exception ex) { _errors.Log("AlarmService", "prune", ex); }
        }
    }

    /// <summary>Returns true if the alarm fired.</summary>
    private async Task<bool> EvaluateAsync(AppDbContext db, Alarm alarm, DateTimeOffset now, CancellationToken ct)
    {
        var condition = _registry.Find(alarm.ConditionType);
        if (condition is null)
        {
            alarm.LastError = $"Unknown condition type '{alarm.ConditionType}'.";
            return false;
        }

        JsonElement config;
        try { config = JsonDocument.Parse(alarm.ConditionJson ?? "{}").RootElement.Clone(); }
        catch (Exception ex) { alarm.LastError = $"Bad condition config: {ex.Message}"; return false; }

        var ctx = new AlarmEvaluationContext
        {
            DbFactory        = _dbFactory,
            ConnectionString = _connString,
            Alarm            = alarm,
            Now              = now,
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
            if (matches.Count > 0) BankKeys(db, alarm.Id, matches, await SeenAsync(db, alarm.Id, ct), now);
            alarm.Primed = true;
            return false;
        }

        // Checks over mutable state forget keys that have gone away, so the same key can be
        // news again if it returns. Runs before the empty-set exit, because an empty result IS
        // the signal that re-arms an "alert me when there are no rows" alarm.
        if (condition.ForgetsUnseenKeys)
            await ForgetVanishedKeysAsync(db, alarm.Id, matches, ct);

        if (matches.Count == 0) return false;

        var seenSet = await SeenAsync(db, alarm.Id, ct);

        var fresh = matches.Where(m => !seenSet.Contains(m.Key)).ToList();
        if (fresh.Count == 0) return false;

        // A staged check sets its own cadence — a cooldown would swallow stage two, and one-shot
        // would end the alarm at stage one.
        var staged = condition.Stages > 0;

        // A cooldown suppresses the firing but must NOT bank the keys, or the matches it is
        // damping would be lost for good. They stay unseen and go out together once it lapses.
        if (!staged && alarm.CooldownSeconds > 0 && alarm.LastFiredAt is { } last
            && now < last.AddSeconds(alarm.CooldownSeconds))
        {
            return false;
        }

        var evt = new AlarmEvent
        {
            AlarmId    = alarm.Id,
            FiredAt    = now,
            MatchCount = fresh.Count,
            Summary    = BuildSummary(fresh),
            DetailJson = JsonSerializer.Serialize(fresh.Select(m => new
            {
                key     = m.Key,
                summary = m.Summary,
                detail  = m.Detail,
            })),
        };
        db.AlarmEvents.Add(evt);

        BankKeys(db, alarm.Id, fresh, seenSet, now);

        alarm.LastFiredAt = now;
        alarm.FireCount  += 1;
        if (!staged && alarm.Repeat == AlarmRepeat.OneShot) alarm.Enabled = false;

        // Persist before acting: an action that raises a dialog or calls the agent must not be
        // able to run twice because the write that recorded it had not landed yet.
        await db.SaveChangesAsync(ct);

        var actions = await db.AlarmActions.AsNoTracking()
            .Where(a => a.AlarmId == alarm.Id)
            .OrderBy(a => a.Ordinal)
            .ToListAsync(ct);

        if (!staged)
        {
            // The condition supplies wording for any Alert or Dialog left on its default, so what
            // the capsuleer reads reflects what was actually being watched for.
            var defaults = condition.DefaultText(alarm.Name, config, fresh);

            // And, for a condition that would rather be quoted than paraphrased, the exact words.
            var announcement = condition.Announcement(config, fresh);

            await _actions.RunAsync(alarm, actions, evt, fresh, defaults, announcement, ct: ct);
            return true;
        }

        // Staged: one firing per scope and stage — two pilots adrift are two wake-up calls, each
        // with the actions that have joined in by its stage (an untied action is there from the
        // first; one tied to stage 2 is there at 2 and after, because stages escalate).
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

            var stageActions = actions.Where(a => ActionStage(a) is not { } s || group.Key.Stage >= s).ToList();
            if (stageActions.Count == 0) continue;

            await _actions.RunAsync(
                alarm, stageActions, evt, list,
                condition.DefaultText(alarm.Name, config, list),
                condition.Announcement(config, list),
                stage,
                condition.AgentPrompt(config, list),
                ct);
        }
        return true;
    }

    private static string DetailText(AlarmMatch m, string key)
        => m.Detail is { } d && d.TryGetValue(key, out var v) && v is string s ? s : "";

    /// <summary>The stage an action joins in at, from its config — null for every stage.</summary>
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

    private static string Relative(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "—";
        var d = at.Value - now;
        if (d <= TimeSpan.Zero)     return "now";
        if (d < TimeSpan.FromMinutes(1)) return $"in {d.TotalSeconds:F0}s";
        if (d < TimeSpan.FromHours(1))   return $"in {d.TotalMinutes:F0}m";
        return $"in {d.TotalHours:F0}h";
    }
}
