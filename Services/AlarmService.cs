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

    private readonly CancellationTokenSource _cts = new();
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

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }
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

            if (matches.Count > 0) BankKeys(db, alarmId, matches, DateTimeOffset.Now);
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
            if (matches.Count > 0) BankKeys(db, alarm.Id, matches, now);
            alarm.Primed = true;
            return false;
        }

        // Checks over mutable state forget keys that have gone away, so the same key can be
        // news again if it returns. Runs before the empty-set exit, because an empty result IS
        // the signal that re-arms an "alert me when there are no rows" alarm.
        if (condition.ForgetsUnseenKeys)
            await ForgetVanishedKeysAsync(db, alarm.Id, matches, ct);

        if (matches.Count == 0) return false;

        var seen = await db.AlarmSeenKeys.AsNoTracking()
            .Where(k => k.AlarmId == alarm.Id)
            .Select(k => k.MatchKey)
            .ToListAsync(ct);
        var seenSet = seen.ToHashSet(StringComparer.Ordinal);

        var fresh = matches.Where(m => !seenSet.Contains(m.Key)).ToList();
        if (fresh.Count == 0) return false;

        // A cooldown suppresses the firing but must NOT bank the keys, or the matches it is
        // damping would be lost for good. They stay unseen and go out together once it lapses.
        if (alarm.CooldownSeconds > 0 && alarm.LastFiredAt is { } last
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

        BankKeys(db, alarm.Id, fresh, now);

        alarm.LastFiredAt = now;
        alarm.FireCount  += 1;
        if (alarm.Repeat == AlarmRepeat.OneShot) alarm.Enabled = false;

        // Persist before acting: an action that raises a dialog or calls the agent must not be
        // able to run twice because the write that recorded it had not landed yet.
        await db.SaveChangesAsync(ct);

        var actions = await db.AlarmActions.AsNoTracking()
            .Where(a => a.AlarmId == alarm.Id)
            .OrderBy(a => a.Ordinal)
            .ToListAsync(ct);

        await _actions.RunAsync(alarm, actions, evt, fresh, ct);
        return true;
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

    private static void BankKeys(AppDbContext db, long alarmId, IEnumerable<AlarmMatch> matches, DateTimeOffset now)
    {
        foreach (var m in matches)
            db.AlarmSeenKeys.Add(new AlarmSeenKey
            {
                AlarmId     = alarmId,
                MatchKey    = m.Key,
                FirstSeenAt = now,
            });
    }

    private static string BuildSummary(IReadOnlyList<AlarmMatch> fresh) =>
        fresh.Count == 1
            ? fresh[0].Summary
            : $"{fresh.Count} new: " + string.Join("; ", fresh.Take(3).Select(m => m.Summary))
              + (fresh.Count > 3 ? $"; +{fresh.Count - 3} more" : "");

    /// <summary>
    /// Keeps the ledger from growing without bound on high-volume checks. Retention is far
    /// longer than any plausible re-match window, so pruning cannot resurrect old news.
    /// </summary>
    private static async Task PruneSeenKeysAsync(AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        // Must match EF Core's on-disk shape for DateTimeOffset (space separator, trailing
        // offset). An ISO "o" string sorts above every stored value because 'T' > ' ', which
        // would make this comparison true for every row and empty the ledger — at which point
        // every alarm re-announces everything it has ever seen.
        var cutoff = (now - SeenKeyRetention).ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";
        await db.Database.ExecuteSqlRawAsync(
            """DELETE FROM "AlarmSeenKeys" WHERE "FirstSeenAt" < {0}""", [cutoff], ct);

        await db.Database.ExecuteSqlRawAsync($"""
            DELETE FROM "AlarmSeenKeys" WHERE rowid IN (
              SELECT rowid FROM (
                SELECT rowid, ROW_NUMBER() OVER (PARTITION BY "AlarmId" ORDER BY "FirstSeenAt" DESC) AS rn
                FROM "AlarmSeenKeys")
              WHERE rn > {MaxSeenKeysPerAlarm})
            """, ct);
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
