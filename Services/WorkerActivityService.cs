using System.Text.Json;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// What each background loop is doing, published by the client running them and readable by the
/// rest.
///
/// <para>The monitoring window used to read these straight off the services in this process, which
/// was correct while only one client could exist. With several it is a lie: on a client that is not
/// the worker every loop looks stopped, so the window reports "Idle" about a sweep running
/// perfectly well elsewhere — the worst thing a monitor can say, because idle is also what a
/// genuinely broken loop looks like.</para>
///
/// <para>⚠️ Both a table and a signal, for two different questions. The signal is how a window
/// already open hears about a change, in about a millisecond, so a client that is not the worker
/// feels no slower than one that is. The table is how a window opened later finds out what is
/// happening right now, which a signal cannot answer because it has no replay.</para>
///
/// <para>Only leader-only loops belong here. Intel is deliberately absent: it is driven by chat-log
/// import, which is host-bound, so every client runs its own and its local status is the true
/// one.</para>
/// </summary>
public sealed class WorkerActivityService
{
    // ⚠️ Publisher and readers match on these, so they are constants in one place rather than
    // strings written twice. A typo would not fail — it would silently show a blank row forever.
    public const string Polling         = "esi.polling";
    public const string Structures      = "esi.structures";
    public const string PublicStructs   = "esi.structures.public";
    public const string MarketHistory   = "market.history";
    public const string ZkbPolling      = "zkb.polling";
    public const string ZkbFirehose     = "zkb.firehose";
    public const string ZkbBackfill     = "zkb.backfill";
    public const string ZkbPost         = "zkb.post";
    public const string OrderFulfilment = "order.fulfilment";
    public const string NameCache       = "name.cache";
    public const string LpStore         = "lpstore";
    public const string Alarms          = "alarms";

    /// <summary>Discriminator on the wire, so a client can tell these from an alarm.</summary>
    private const string SignalKind = "activity";

    /// <summary>The ESI call log, which is a stream rather than a set of states.</summary>
    private const string LogKind = "activity-log";

    /// <summary>
    /// How often the worker looks at its own loops.
    ///
    /// <para>A second, because this is the delay a non-worker client sees and the whole point is
    /// for it to be small. The pass itself reads a handful of in-memory properties and compares
    /// them to the last set, so a quiet system costs nothing and writes nothing.</para>
    /// </summary>
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(1);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errors;
    private readonly ClientSignals                   _signals;
    private readonly ApiActivityLog                  _log;
    private readonly WorkerLease                     _lease;
    private readonly Func<IReadOnlyList<WorkerActivity>> _sample;

    private Task?                    _loop;
    private CancellationTokenSource? _cts;

    /// <summary>The last set published, so an unchanged pass writes and sends nothing.</summary>
    private Dictionary<string, string> _lastSent = new(StringComparer.Ordinal);

    /// <summary>What the worker last said, as this client understands it.</summary>
    private readonly Dictionary<string, WorkerActivity> _board = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, WorkerActivity> Board => _board;

    /// <summary>Raised after the board changes, so an open window can refresh without polling.</summary>
    public event Action? Changed;

    /// <param name="sample">
    /// Reads the current state of every published loop. Supplied by the caller rather than taken as
    /// ten constructor dependencies, so this class does not have to know what a zKillboard firehose
    /// is in order to relay what one says about itself.
    /// </param>
    public WorkerActivityService(
        IDbContextFactory<AppDbContext>       dbFactory,
        AppErrorLogger                        errors,
        ClientSignals                         signals,
        ApiActivityLog                        log,
        WorkerLease                           lease,
        Func<IReadOnlyList<WorkerActivity>>   sample)
    {
        _dbFactory = dbFactory;
        _errors    = errors;
        _signals   = signals;
        _log       = log;
        _lease     = lease;
        _sample    = sample;
    }

    // ── Publishing, on the client holding the lease ───────────────────────────

    public void Start(CancellationToken outerCt = default)
    {
        if (_loop is not null) return;

        // ⚠️ Subscribed only while this client is the worker. Every client's log raises this, and a
        // client that relayed its own empty feed would overwrite the worker's on everyone else.
        _log.Flushed += OnCallsLogged;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                // ⚠️ Guarded whole. This loop ending would leave every other client's window
                // frozen on whatever it last heard, with nothing at all to say the feed had
                // stopped — a monitor that lies quietly rather than going blank.
                try { await PublishAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _errors.Log(nameof(WorkerActivityService), "publishing", ex); }

                try { await PublishCallsAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _errors.Log(nameof(WorkerActivityService), "relaying calls", ex); }

                try { await Task.Delay(Cadence, ct); }
                catch (OperationCanceledException) { return; }
            }
        }, ct);
    }

    public async Task StopAsync()
    {
        _log.Flushed -= OnCallsLogged;
        lock (_pending) _pending.Clear();

        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }

        _cts.Dispose();
        _cts  = null;
        _loop = null;

        // ⚠️ Cleared, so a client that regains the lease republishes everything rather than
        // comparing against what it sent in a previous life and deciding nothing has changed.
        _lastSent = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        var now  = DateTimeOffset.UtcNow;
        var rows = _sample();

        var changed = new List<WorkerActivity>();
        foreach (var r in rows)
        {
            r.UpdatedUtc = now;

            // Compared on the fields a reader can see, deliberately not including UpdatedUtc —
            // stamping the time into the comparison would make every pass look like a change and
            // turn a quiet system into one write per row per second.
            var fingerprint = $"{r.Status}{r.Running}{r.LastRunUtc:O}{r.NextRunUtc:O}|{r.Count}";
            if (_lastSent.TryGetValue(r.Key, out var was) && was == fingerprint) continue;

            _lastSent[r.Key] = fingerprint;
            changed.Add(r);
        }

        if (changed.Count == 0) return;

        // The signal first: it is what an open window is waiting on, and the table write is only
        // needed by a window opened later.
        await _signals.PublishAsync(
            JsonSerializer.Serialize(new SignalPayload { Kind = SignalKind, Rows = rows }), ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var r in changed)
        {
            var existing = await db.WorkerActivities.FirstOrDefaultAsync(x => x.Key == r.Key, ct);
            if (existing is null) db.WorkerActivities.Add(r);
            else
            {
                existing.Status     = r.Status;
                existing.Running    = r.Running;
                existing.LastRunUtc = r.LastRunUtc;
                existing.NextRunUtc = r.NextRunUtc;

                // ⚠️ Count was missing here and nowhere else. A new row carried it, an updated row
                // did not — so every board that had ever been written kept a null count forever,
                // while the signal alongside it carried the real number. Found from the table, not
                // from the code: the alarms row read "1 armed" in its status and null in its count.
                existing.Count      = r.Count;
                existing.UpdatedUtc = r.UpdatedUtc;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    // ── Relaying the ESI call log ─────────────────────────────────────────────

    private readonly List<ActivityEntry> _pending = [];

    private void OnCallsLogged(IReadOnlyList<ActivityEntry> batch)
    {
        lock (_pending) _pending.AddRange(batch);
    }

    /// <summary>
    /// Sends the calls made since the last pass, and the ones still in flight.
    ///
    /// <para>⚠️ In flight goes every time there is anything to send, not only when it changes: it
    /// is a snapshot of "right now" rather than a stream, so a client that missed one update would
    /// otherwise show a call as still running long after it finished.</para>
    /// </summary>
    private async Task PublishCallsAsync(CancellationToken ct)
    {
        List<ActivityEntry> batch;
        lock (_pending)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }

        var inFlight = _log.InFlightCalls.ToList();

        // ⚠️ Trimmed until the SERIALISED payload fits, not to a fixed number of entries. Counting
        // was the first attempt and it was wrong: an entry carries an owner name, an endpoint and
        // possibly an error, so fifty came to 14 KB against a 7,900-byte limit — and the oversized
        // batch was then dropped whole, which is the opposite of what a cap is for. Now the oldest
        // go until the rest fit, and the gap is stated in the feed rather than left to be inferred
        // from a jump in timestamps.
        var    dropped = 0;
        string payload;

        while (true)
        {
            var toSend = batch.Skip(dropped).ToList();
            if (dropped > 0)
                toSend.Insert(0, new ActivityEntry(
                    DateTimeOffset.UtcNow, "—", $"({dropped} more calls not relayed)", true, 0, null));

            payload = JsonSerializer.Serialize(new CallsPayload
            {
                Kind     = LogKind,
                Calls    = toSend,
                InFlight = inFlight,
            });

            if (ClientSignals.Fits(payload) || dropped >= batch.Count) break;

            // Halve what remains rather than shedding one at a time: a burst can be hundreds, and
            // re-serialising the whole list per entry would cost more than the relay is worth.
            dropped = Math.Max(dropped + 1, (dropped + batch.Count) / 2);
        }

        await _signals.PublishAsync(payload, ct);
    }

    // ── Reading, on every client ──────────────────────────────────────────────

    /// <summary>
    /// Fills the board from the table. For a window opening, which has no signal to wait for.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.WorkerActivities.AsNoTracking().ToListAsync(ct);

            lock (_board)
            {
                foreach (var r in rows) _board[r.Key] = r;
            }
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A board that cannot be read leaves the window showing what it already had, which is
            // better than blanking it.
            _errors.Log(nameof(WorkerActivityService), "reading the activity board", ex);
        }
    }

    /// <summary>
    /// Applies a pushed snapshot. Returns false for anything that is not one, so the same
    /// subscription can carry other kinds of signal.
    /// </summary>
    public bool TryApplySignal(string payload)
    {
        string kind;
        try { kind = JsonSerializer.Deserialize<KindOnly>(payload)?.Kind ?? ""; }
        catch { return false; }

        if (kind != SignalKind && kind != LogKind) return false;

        // ⚠️ Claimed but not applied on the client that sent it. PostgreSQL delivers a notification
        // back to the sending session — which is exactly what keeps the alarm path uniform — but
        // the worker already has these calls in its own log, and ingesting them again would show
        // every one of them twice.
        if (_lease.IsHolder) return true;

        if (kind == LogKind)
        {
            CallsPayload? calls;
            try { calls = JsonSerializer.Deserialize<CallsPayload>(payload); }
            catch { return true; }

            if (calls?.Calls    is { Count: > 0 }) _log.Ingest(calls.Calls);
            if (calls?.InFlight is not null)       _log.ReplaceInFlight(calls.InFlight);
            return true;
        }

        SignalPayload? p;
        try { p = JsonSerializer.Deserialize<SignalPayload>(payload); }
        catch { return true; }

        if (p?.Rows is null) return true;

        lock (_board)
        {
            foreach (var r in p.Rows) _board[r.Key] = r;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>What the worker says about one loop, or null if it has never said anything.</summary>
    public WorkerActivity? Get(string key)
    {
        lock (_board) return _board.TryGetValue(key, out var r) ? r : null;
    }

    private sealed class SignalPayload
    {
        public string                Kind { get; set; } = "";
        public IReadOnlyList<WorkerActivity>? Rows { get; set; }
    }

    /// <summary>Reads the discriminator alone, so the kind is known before the shape is chosen.</summary>
    private sealed class KindOnly
    {
        public string Kind { get; set; } = "";
    }

    private sealed class CallsPayload
    {
        public string Kind { get; set; } = "";
        public List<ActivityEntry>? Calls    { get; set; }
        public List<InFlightCall>?  InFlight { get; set; }
    }
}
