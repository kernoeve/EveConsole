using System.Collections.Concurrent;
using System.Text.Json;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Jabber;

/// <summary>
/// How one Jabber connection serves every client of a shared database.
///
/// <para>Whichever process holds the worker lease keeps the connections and writes what they bring to the database. The
/// others show what it wrote, and ask it to do anything that needs the server. The view models neither know nor care:
/// a <see cref="JabberSession"/> looks the same filled by a connection here as filled from the database, and the service
/// methods they call work out which is which.</para>
///
/// <list type="bullet">
/// <item>Messages: rows in JabberMessages, as ever. A signal says to look. Every client finds them by id, so a lost
/// signal only delays them until the next look.</item>
/// <item>How a connection stands (contacts, who is in which room): <see cref="JabberLiveState"/>, one row per account and
/// one per room, so that someone joining a large room does not rewrite everything else.</item>
/// <item>Asking: <see cref="JabberCommand"/>. ⚠️ Never a password: logins go straight to <see cref="AppConfig"/>, where
/// the worker reads them, and an invitation's stays with the worker.</item>
/// </list>
///
/// <para>On SQLite there is one process, so none of this runs: <see cref="ActsHere"/> is always true.</para>
/// </summary>
public sealed partial class JabberService
{
    /// <summary>
    /// How these look on the shared channel. ⚠️ JSON with a Kind, like everything else on it: a client of an older build
    /// on the same database hands whatever it does not recognise to its alarm path, which logs an error for anything that
    /// does not parse, and these can come every second.
    /// </summary>
    private sealed record JabberSignal(string Kind, string What);

    private const string SignalKind = "jabber";
    private const string SignalStart = "{\"Kind\":\"" + SignalKind + "\"";

    /// <summary>Which row holds the account's own state, the rest being rooms by address.</summary>
    private const string AccountPart = "";

    /// <summary>How often what changed in a connection is written. ⚠️ Not per change: a busy room changes constantly.</summary>
    private static readonly TimeSpan LiveEvery = TimeSpan.FromSeconds(1);

    /// <summary>A little over one lease tick: a dead worker is judged by its heartbeat, and nothing tells of it sooner.</summary>
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan NoFinalAnswerAfter = TimeSpan.FromSeconds(90);

    /// <summary>A command nobody picked up. ⚠️ Left, it would run whenever a worker next came along, long after the point.</summary>
    private static readonly TimeSpan CommandsExpireAfter = TimeSpan.FromMinutes(5);

    /// <summary>Ids can be handed out in one order and committed in another, so each look goes back this far.</summary>
    private const long MessageOverlap = 100;

    private const int MessagePage = 500;

    /// <summary>
    /// How long a request may wait to be picked up before it is taken back and reported as not done. ⚠️ Short, and a
    /// refusal: a message sent minutes later, after its sender has written it again, is worse than being told.
    /// </summary>
    public TimeSpan PickupWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Relays even on SQLite, for the checks: they run two services over one file, needing no server.</summary>
    public bool RelayOnSqlite { get; init; }

    private volatile bool _isWorker;
    private volatile bool _stopping;
    private CancellationTokenSource? _workerRun;
    private CancellationTokenSource? _watchRun;

    private bool Relays => signals is not null && (DbEngine.IsPostgres || RelayOnSqlite);

    /// <summary>Whether this process does things itself, rather than asking the worker to.</summary>
    private bool ActsHere => _isWorker || !Relays;

    /// <summary>Says something to every client. Nothing here is worth failing over: each has a slower way of finding out.</summary>
    private void Tell(string what)
    {
        if (Relays) _ = signals!.PublishAsync(JsonSerializer.Serialize(new JabberSignal(SignalKind, what)));
    }

    // ── Becoming the worker, and stopping ─────────────────────────────────────

    /// <summary>This process now holds the lease: the connections are its to keep.</summary>
    public async Task BecomeWorkerAsync(CancellationToken ct = default)
    {
        try
        {
            _isWorker = true;
            _watchRun?.Cancel();
            ForgetWhatWasShown();
            await LoadAccountsAsync(ct);

            if (Relays)
            {
                // What the last worker left says nothing about this one.
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                    await db.JabberLiveStates.ExecuteDeleteAsync(ct);
                _written.Clear();

                // ⚠️ From the clock, not from one: a client that read the last worker's rows would take the same numbers
                // from this one as nothing new.
                _liveVersion = DateTimeOffset.UtcNow.UtcTicks;

                _workerRun = new CancellationTokenSource();
                HookLiveWriter();
                foreach (var session in Sessions) _liveDirty[session.Account.Id] = 0;
                _ = RunCommandsAsync(_workerRun.Token);
                _ = RunLiveWriterAsync(_workerRun.Token);
            }

            foreach (var session in Sessions.Where(s => s.Account.AutoConnect)) Connect(session);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errors.Log(nameof(JabberService), "take over the connections", e);
        }
    }

    /// <summary>The lease is gone: another process may connect at any moment, so these go now.</summary>
    public async Task StandDownAsync()
    {
        if (!_isWorker) return;

        if (_workerRun is { } run) await run.CancelAsync();
        _workerRun = null;

        await Task.WhenAll(Sessions.Select(DisconnectHereAsync));
        _isWorker = false;
        if (!Relays) return;

        try
        {
            await using (var db = await dbFactory.CreateDbContextAsync())
                await db.JabberLiveStates.ExecuteDeleteAsync();
            foreach (var session in Sessions) Tell($"live|{session.Account.Id}");
        }
        catch (Exception e) { errors.Log(nameof(JabberService), "stand down", e); }

        await StartShowingAsync(CancellationToken.None);
    }

    /// <summary>From here on, what is shown comes from whoever holds the lease.</summary>
    private async Task StartShowingAsync(CancellationToken ct)
    {
        if (!Relays || _isWorker || _stopping || AppRuntime.IsHeadless) return;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            _messageFloor = await db.JabberMessages.AsNoTracking().MaxAsync(m => (long?)m.Id, ct) ?? 0;
        _lastDelivered = _messageFloor;
        lock (_delivered) { _delivered.Clear(); _withSound.Clear(); }

        foreach (var session in Sessions) await RefreshLiveAsync(session.Account.Id, ct);

        _watchRun?.Cancel();
        _watchRun = new CancellationTokenSource();
        _ = WatchWorkerAsync(_watchRun.Token);
    }

    /// <summary>
    /// A worker can stop without a word: nothing is left to say so but its heartbeat going old. And a signal missed while
    /// reconnecting has no replay, so this is also what finds whatever that left behind.
    /// </summary>
    private async Task WatchWorkerAsync(CancellationToken ct)
    {
        var workerWasGone = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchEvery, ct);
                if (_isWorker) return;

                var alive = !DbEngine.IsPostgres
                         || await WorkerLease.ReadStatusAsync(ct) is { } status && WorkerLease.IsLive(status);

                if (!alive)
                {
                    if (!workerWasGone) ShowWorkerGone();
                    workerWasGone = true;
                    continue;
                }

                if (workerWasGone) lock (_seenLive) _seenLive.Clear();
                workerWasGone = false;

                foreach (var session in Sessions) await RefreshLiveAsync(session.Account.Id, ct);
                await DeliverNewMessagesAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { errors.Log(nameof(JabberService), "watch the background process", e); }
        }
    }

    private void ShowWorkerGone()
    {
        foreach (var session in Sessions)
        {
            MarkDisconnected(session, keep: false);
            SetState(session, JabberState.WaitingForWorker, JabberProblem.None, "");
        }
        lock (_seenLive) _seenLive.Clear();
    }

    /// <summary>What was shown came from another worker. ⚠️ Kept, it would look like this one's until replaced.</summary>
    private void ForgetWhatWasShown()
    {
        lock (_seenLive) _seenLive.Clear();

        foreach (var session in Sessions)
        {
            session.Locked(s => { s.ContactMap.Clear(); s.RoomMap.Clear(); s.Requests.Clear(); s.InviteMap.Clear(); });
            session.State = JabberState.Offline;
            session.Problem = JabberProblem.None;
            session.Detail = "";
        }
    }

    /// <summary>The accounts as saved: another client may have added, changed or removed one.</summary>
    private async Task LoadAccountsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.JabberAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);

        List<JabberSession> tracked = [.. accounts.Select(Track)];

        List<JabberSession> gone;
        lock (_gate)
        {
            gone = [.. _sessions.Values.Where(s => !accounts.Any(a => a.Id == s.Account.Id))];
            foreach (var session in gone) _sessions.Remove(session.Account.Id);
        }

        foreach (var session in gone)
        {
            if (!_isWorker) continue;
            await DisconnectHereAsync(session);
            var id = session.Account.Id;
            await db.JabberLiveStates.Where(l => l.AccountId == id).ExecuteDeleteAsync(ct);
            _liveDirty.TryRemove(id, out _);
        }

        // ⚠️ Said even when one stays offline: what lists the servers looked before this read them.
        foreach (var session in tracked.Concat(gone)) SessionChanged?.Invoke(session);
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    /// <summary>For the signal listener: true when the signal is Jabber's, which is then handled away from the caller's thread.</summary>
    public bool TryApplySignal(string payload)
    {
        if (!payload.StartsWith(SignalStart, StringComparison.Ordinal)) return false;

        // A worker with no window has nothing to show, and one waiting for the lease has nothing to do.
        if ((!AppRuntime.IsHeadless || _isWorker) && JsonSerializer.Deserialize<JabberSignal>(payload) is { } signal)
            _ = ApplySignalAsync(signal.What.Split('|', 4));
        return true;
    }

    private async Task ApplySignalAsync(string[] parts)
    {
        await Task.Yield();

        try
        {
            switch (parts[0])
            {
                case "msg" when !_isWorker:
                    await MessageSignalAsync(long.Parse(parts[1]), parts[2] == "1");
                    break;

                case "edit" when !_isWorker:
                    ConversationEdited?.Invoke(int.Parse(parts[1]), parts[2]);
                    break;

                case "react" when !_isWorker:
                    ReactionsChanged?.Invoke(int.Parse(parts[1]), parts[2]);
                    break;

                case "typing" when !_isWorker:
                    TypingChanged?.Invoke(int.Parse(parts[1]), parts[3], parts[2] == "1");
                    break;

                case "typeout" when _isWorker:
                    if (SessionOf(int.Parse(parts[1])) is { } writing)
                        await SendTypingAsync(writing, parts[3], parts[2] == "1");
                    break;

                case "unread":
                    UnreadChanged?.Invoke();
                    break;

                case "live" when !_isWorker:
                    await RefreshLiveAsync(int.Parse(parts[1]), CancellationToken.None);
                    break;

                case "accounts":
                    await LoadAccountsAsync(CancellationToken.None);
                    break;

                case "intelrooms" when !_isWorker:
                    IntelRoomsChanged?.Invoke();
                    break;

                case "cmd" when _isWorker:
                    WakeCommands();
                    break;

                case "done":
                    if (_waiting.TryGetValue(long.Parse(parts[1]), out var waiter)) waiter.TrySetResult();
                    break;
            }
        }
        catch (Exception e) { errors.Log(nameof(JabberService), "handle a signal", e); }
    }

    // ── Messages, for the clients that did not save them ──────────────────────

    private readonly SemaphoreSlim _deliverGate = new(1, 1);
    private readonly HashSet<long> _delivered = [];
    private readonly HashSet<long> _withSound = [];
    private long _messageFloor;
    private long _lastDelivered;
    private int _quietMessageTold;

    /// <summary>
    /// Tells the clients a message was saved. One that calls for attention goes at once and by id, since it decides a
    /// sound. ⚠️ The rest are gathered up: catching up on a room can save thousands in a burst.
    /// </summary>
    private void TellMessageSaved(long id, bool sound)
    {
        if (!Relays) return;

        if (sound) { Tell($"msg|{id}|1"); return; }
        if (Interlocked.Exchange(ref _quietMessageTold, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            Interlocked.Exchange(ref _quietMessageTold, 0);
            Tell("msg|0|0");
        });
    }

    private async Task MessageSignalAsync(long id, bool sound)
    {
        if (sound)
        {
            bool alreadyDelivered;
            lock (_delivered)
            {
                alreadyDelivered = _delivered.Contains(id);
                if (!alreadyDelivered) _withSound.Add(id);
            }

            // Found by an earlier look, before this arrived. The message has been shown, but its sound has not.
            if (alreadyDelivered)
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                if (await db.JabberMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is { } row) AlertArrived?.Invoke(row);
            }
        }

        await DeliverNewMessagesAsync(CancellationToken.None);
    }

    /// <summary>Raises for whatever has been saved since the last look, in order, each once.</summary>
    private async Task DeliverNewMessagesAsync(CancellationToken ct)
    {
        await _deliverGate.WaitAsync(ct);
        try
        {
            var anyUnread = false;

            while (true)
            {
                var from = Math.Max(_messageFloor, _lastDelivered - MessageOverlap);
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var rows = await db.JabberMessages.AsNoTracking().Where(m => m.Id > from).OrderBy(m => m.Id).Take(MessagePage).ToListAsync(ct);

                var fresh = 0;
                foreach (var row in rows)
                {
                    bool sound;
                    lock (_delivered)
                    {
                        if (!_delivered.Add(row.Id)) continue;
                        sound = _withSound.Remove(row.Id);
                    }

                    fresh++;
                    _lastDelivered = Math.Max(_lastDelivered, row.Id);
                    anyUnread |= !row.IsRead;
                    MessageStored?.Invoke(row);
                    if (sound) AlertArrived?.Invoke(row);
                }

                lock (_delivered)
                {
                    var keepFrom = _lastDelivered - MessageOverlap;
                    _delivered.RemoveWhere(id => id <= keepFrom);
                }

                if (fresh == 0 || rows.Count < MessagePage) break;
            }

            if (anyUnread) UnreadChanged?.Invoke();
        }
        finally { _deliverGate.Release(); }
    }

    // ── How the connections stand: written by the worker ──────────────────────

    private sealed record LiveContact(string Jid, string Name, string Subscription, XmppShow Show, string Status);

    private sealed record LiveAccount(
        JabberState State, JabberProblem Problem, string Detail, string CertificateFingerprint, string CertificateSubject,
        DateTimeOffset? NextAttempt, XmppShow Show, string Status, List<string> ConferenceServices,
        XmppUploadService? UploadService, bool? HasArchive, List<LiveContact> Contacts, List<string> Requests, List<XmppInvite> Invites);

    private sealed record LiveRoom(
        JabberRoom Room, bool Joined, string Nick, string Subject, XmppRoomFailure? Failure, string FailureText,
        bool? HasArchive, bool ArchiveRefused, List<XmppOccupant> Occupants, bool GivesOccupantIds = false, string MyOccupantId = "");

    private readonly ConcurrentDictionary<int, byte> _liveDirty = new();
    private readonly Dictionary<int, Dictionary<string, string>> _written = [];
    private bool _liveWriterHooked;
    private long _liveVersion;
    private bool _liveWriteFailing;

    private void HookLiveWriter()
    {
        if (_liveWriterHooked) return;
        _liveWriterHooked = true;
        SessionChanged += session => { if (_isWorker) _liveDirty[session.Account.Id] = 0; };
    }

    /// <summary>A session as rows. ⚠️ An invitation's password stays out: it is the worker that answers invitations.</summary>
    private static Dictionary<string, string> LiveParts(JabberSession session) => session.Locked(s =>
    {
        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AccountPart] = JsonSerializer.Serialize(new LiveAccount(
                s.State, s.Problem, s.Detail, s.CertificateFingerprint, s.CertificateSubject, s.NextAttempt, s.Show, s.Status,
                [.. s.ConferenceServices], s.UploadService, s.HasArchive,
                [.. s.ContactMap.Values.Select(c => new LiveContact(c.Jid, c.Name, c.Subscription, c.Show, c.Status))],
                [.. s.Requests], [.. s.InviteMap.Values.Select(i => i with { Password = "" })])),
        };

        foreach (var (jid, room) in s.RoomMap)
            parts[jid] = JsonSerializer.Serialize(new LiveRoom(
                room.Room, room.Joined, room.Nick, room.Subject, room.Failure, room.FailureText, room.HasArchive, room.ArchiveRefused,
                [.. room.Occupants.Values], room.GivesOccupantIds, room.MyOccupantId));

        return parts;
    });

    private async Task RunLiveWriterAsync(CancellationToken ct)
    {
        var passes = 0;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(LiveEvery, ct); }
            catch (OperationCanceledException) { return; }

            // ⚠️ Not everything that changes raises anything: a room's subject, for one.
            if (++passes % 30 == 0)
                foreach (var session in Sessions) _liveDirty[session.Account.Id] = 0;

            foreach (var id in _liveDirty.Keys.ToArray())
            {
                _liveDirty.TryRemove(id, out _);
                if (SessionOf(id) is not { } session) continue;

                try
                {
                    await WriteLiveAsync(session, ct);
                    _liveWriteFailing = false;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    // Tried again next pass. Said once: the error log is a table in the database that is failing.
                    _liveDirty[id] = 0;
                    if (!_liveWriteFailing) errors.Log(nameof(JabberService), "write how the connections stand", e);
                    _liveWriteFailing = true;
                }
            }
        }
    }

    /// <summary>Writes what changed since the last write, and tells the clients.</summary>
    private async Task WriteLiveAsync(JabberSession session, CancellationToken ct)
    {
        var id = session.Account.Id;
        var parts = LiveParts(session);
        var had = _written.GetValueOrDefault(id) ?? [];

        var changed = parts.Where(p => !had.TryGetValue(p.Key, out var json) || json != p.Value).Select(p => p.Key).ToList();
        var gone = had.Keys.Where(k => !parts.ContainsKey(k)).ToList();
        if (changed.Count == 0 && gone.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.JabberLiveStates.Where(l => l.AccountId == id && (changed.Contains(l.Part) || gone.Contains(l.Part))).ToListAsync(ct);

        foreach (var part in changed)
        {
            var row = rows.FirstOrDefault(r => r.Part == part);
            if (row is null) db.JabberLiveStates.Add(row = new JabberLiveState { AccountId = id, Part = part });
            row.Json = parts[part];
            row.Version = ++_liveVersion;
        }
        db.JabberLiveStates.RemoveRange(rows.Where(r => gone.Contains(r.Part)));
        await db.SaveChangesAsync(ct);

        _written[id] = parts;
        Tell($"live|{id}");
    }

    // ── How the connections stand: read by every other client ─────────────────

    private readonly SemaphoreSlim _viewGate = new(1, 1);
    private readonly Dictionary<(int Account, string Part), long> _seenLive = [];

    /// <summary>Brings a session up to what the worker last wrote, reading only the rows that changed.</summary>
    private async Task RefreshLiveAsync(int accountId, CancellationToken ct)
    {
        await _viewGate.WaitAsync(ct);
        try
        {
            if (_isWorker) return;

            if (SessionOf(accountId) is null) await LoadAccountsAsync(ct);
            if (SessionOf(accountId) is not { } session) return;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var versions = await db.JabberLiveStates.AsNoTracking().Where(l => l.AccountId == accountId)
                .Select(l => new { l.Part, l.Version }).ToListAsync(ct);

            List<string> wanted;
            lock (_seenLive)
                wanted = [.. versions.Where(v => !_seenLive.TryGetValue((accountId, v.Part), out var seen) || seen != v.Version).Select(v => v.Part)];

            var rows = wanted.Count == 0 ? [] : await db.JabberLiveStates.AsNoTracking()
                .Where(l => l.AccountId == accountId && wanted.Contains(l.Part)).ToListAsync(ct);

            var parts = versions.Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

            // Nothing new. ⚠️ Rooms checked as well: one this client just added is shown only until the worker says otherwise.
            if (rows.Count == 0 && session.Rooms.All(r => parts.Contains(r.Room.RoomJid))
                && (parts.Contains(AccountPart) || session.State == JabberState.Offline)) return;

            ApplyLive(session, rows, parts);

            lock (_seenLive)
            {
                foreach (var key in _seenLive.Keys.Where(k => k.Account == accountId && !parts.Contains(k.Part)).ToList()) _seenLive.Remove(key);
                foreach (var row in rows) _seenLive[(accountId, row.Part)] = row.Version;
            }

            SessionChanged?.Invoke(session);
        }
        finally { _viewGate.Release(); }
    }

    private static void ApplyLive(JabberSession session, List<JabberLiveState> rows, HashSet<string> parts)
    {
        // Nobody has written anything for it: the worker has it offline, or has only just started.
        if (!parts.Contains(AccountPart))
        {
            session.Locked(s => { s.RoomMap.Clear(); s.ContactMap.Clear(); s.Requests.Clear(); s.InviteMap.Clear(); });
            session.State = JabberState.Offline;
            session.Problem = JabberProblem.None;
            session.Detail = "";
            session.NextAttempt = null;
            return;
        }

        session.Locked(s =>
        {
            foreach (var row in rows)
            {
                if (row.Part == AccountPart)
                {
                    if (JsonSerializer.Deserialize<LiveAccount>(row.Json) is { } a) ApplyAccount(s, a);
                }
                else if (JsonSerializer.Deserialize<LiveRoom>(row.Json) is { } r)
                {
                    // ⚠️ The same object when there is one: what is on screen may be holding on to it.
                    if (!s.RoomMap.TryGetValue(row.Part, out var state)) s.RoomMap[row.Part] = state = new JabberRoomState(r.Room);
                    state.Room = r.Room; state.Joined = r.Joined; state.Nick = r.Nick; state.Subject = r.Subject;
                    state.Failure = r.Failure; state.FailureText = r.FailureText;
                    state.HasArchive = r.HasArchive; state.ArchiveRefused = r.ArchiveRefused;
                    state.GivesOccupantIds = r.GivesOccupantIds; state.MyOccupantId = r.MyOccupantId;
                    state.Occupants.Clear();
                    foreach (var person in r.Occupants) state.Occupants[person.Nick] = person;
                }
            }

            foreach (var jid in s.RoomMap.Keys.Where(k => !parts.Contains(k)).ToList()) s.RoomMap.Remove(jid);
        });
    }

    private static void ApplyAccount(JabberSession s, LiveAccount a)
    {
        s.State = a.State; s.Problem = a.Problem; s.Detail = a.Detail;
        s.CertificateFingerprint = a.CertificateFingerprint; s.CertificateSubject = a.CertificateSubject;
        s.NextAttempt = a.NextAttempt; s.Show = a.Show; s.Status = a.Status;
        s.ConferenceServices = a.ConferenceServices; s.UploadService = a.UploadService; s.HasArchive = a.HasArchive;

        s.ContactMap.Clear();
        foreach (var c in a.Contacts)
            s.ContactMap[c.Jid] = new JabberContact(c.Jid) { Name = c.Name, Subscription = c.Subscription, Show = c.Show, Status = c.Status };

        s.Requests.Clear();
        foreach (var jid in a.Requests) s.Requests.Add(jid);

        s.InviteMap.Clear();
        foreach (var invite in a.Invites) s.InviteMap[invite.Room] = invite;
    }

    // ── Asking the worker ─────────────────────────────────────────────────────

    private const int CommandWaiting = 0;
    private const int CommandRunning = 1;
    private const int CommandDone    = 2;

    private sealed record CommandArgs(
        string Conversation = "", bool IsRoom = false, string Text = "", bool Accept = false, XmppShow Show = XmppShow.Online,
        string Status = "", string Name = "", long Size = 0, string ContentType = "",
        string ReplaceId = "", string ReplyTo = "", string ReplyId = "", string ReplyQuote = "");

    private readonly ConcurrentDictionary<long, TaskCompletionSource> _waiting = new();

    private async Task<long> AddCommandAsync(int accountId, string kind, CommandArgs args, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = new JabberCommand
        {
            AccountId = accountId, Kind = kind, Args = JsonSerializer.Serialize(args),
            CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        db.JabberCommands.Add(row);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    /// <summary>Asks for something nobody waits on. If it is not done, what comes back from the worker says so.</summary>
    private async Task PostToWorkerAsync(int accountId, string kind, CommandArgs args, CancellationToken ct = default)
    {
        await AddCommandAsync(accountId, kind, args, ct);
        Tell("cmd");
    }

    /// <summary>The same, from something that cannot wait for it.</summary>
    private void PostToWorker(int accountId, string kind, CommandArgs args) => _ = Task.Run(async () =>
    {
        try { await PostToWorkerAsync(accountId, kind, args); }
        catch (Exception e) { errors.Log(nameof(JabberService), $"ask for {kind}", e); }
    });

    /// <summary>
    /// Asks the worker and waits for its answer. Fails as if it had been tried here when it fails there.
    ///
    /// <para>⚠️ Taken back if nothing picks it up, so "not done" is true. The signal only saves waiting: the row is the
    /// answer, and is looked at anyway in case the signal was lost.</para>
    /// </summary>
    private async Task<string> AskWorkerAsync(int accountId, string kind, CommandArgs args, CancellationToken ct = default)
    {
        var id = await AddCommandAsync(accountId, kind, args, ct);
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answered;

        try
        {
            Tell("cmd");
            var started = DateTimeOffset.UtcNow;

            while (true)
            {
                await Task.WhenAny(answered.Task, Task.Delay(TimeSpan.FromSeconds(1), ct));
                ct.ThrowIfCancellationRequested();

                await using var db = await dbFactory.CreateDbContextAsync(ct);
                var row = await db.JabberCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

                if (row is { State: CommandDone })
                {
                    await db.JabberCommands.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
                    if (row.Error.Length > 0) throw new XmppException(XmppFailure.Network, row.Error);
                    return row.Result;
                }

                var waited = DateTimeOffset.UtcNow - started;

                if (row is { State: CommandWaiting } && waited > PickupWait
                    && await db.JabberCommands.Where(c => c.Id == id && c.State == CommandWaiting).ExecuteDeleteAsync(ct) > 0)
                    throw new XmppException(XmppFailure.Network, "Nothing is running the background processes, so this was not done.");

                if (row is null || waited > NoFinalAnswerAfter)
                    throw new XmppException(XmppFailure.Network, "The background process did not answer, so this may not have been done.");
            }
        }
        finally { _waiting.TryRemove(id, out _); }
    }

    // ── Doing what was asked: the worker's side ───────────────────────────────

    private readonly SemaphoreSlim _commandWake = new(0);

    private void WakeCommands()
    {
        if (_commandWake.CurrentCount == 0) _commandWake.Release();
    }

    private async Task RunCommandsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunWaitingCommandsAsync(ct);

                // The signal is what makes it quick. This is what makes it certain.
                await _commandWake.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e)
            {
                errors.Log(nameof(JabberService), "run what was asked", e);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunWaitingCommandsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tooOld = DateTimeOffset.UtcNow.Subtract(CommandsExpireAfter).ToUnixTimeSeconds();
        await db.JabberCommands.Where(c => c.CreatedUnix < tooOld).ExecuteDeleteAsync(ct);

        foreach (var command in await db.JabberCommands.AsNoTracking().Where(c => c.State == CommandWaiting).OrderBy(c => c.Id).ToListAsync(ct))
        {
            var id = command.Id;
            if (await db.JabberCommands.Where(c => c.Id == id && c.State == CommandWaiting)
                        .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, CommandRunning), ct) == 0) continue;

            // ⚠️ Sends one at a time, to keep what someone says in the order they said it. Nothing else may wait behind
            // them, or behind each other: joining a room or asking one about itself can take seconds.
            if (command.Kind is "send" or "sendurl") await RunCommandAsync(command, ct);
            else _ = RunCommandAsync(command, ct);
        }
    }

    private async Task RunCommandAsync(JabberCommand command, CancellationToken ct)
    {
        var result = "";
        var error = "";

        try { result = await DoCommandAsync(command, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { error = "The background process is stopping."; }
        catch (Exception e) { error = e.Message; }

        try
        {
            var id = command.Id;
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.JabberCommands.Where(c => c.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.State, CommandDone)
                .SetProperty(c => c.Result, result)
                .SetProperty(c => c.Error, error));
            Tell($"done|{id}");
        }
        catch (Exception e) { errors.Log(nameof(JabberService), "answer what was asked", e); }
    }

    private async Task<string> DoCommandAsync(JabberCommand command, CancellationToken ct)
    {
        if (SessionOf(command.AccountId) is null) await LoadAccountsAsync(ct);
        var session = SessionOf(command.AccountId) ?? throw new XmppException(XmppFailure.Network, "That account is gone.");
        var a = JsonSerializer.Deserialize<CommandArgs>(command.Args) ?? new CommandArgs();

        switch (command.Kind)
        {
            case "connect":       Connect(session); break;
            case "disconnect":    await DisconnectHereAsync(session); break;
            case "send":          await SendAsync(session, a.Conversation, a.IsRoom, a.Text, ct,
                                      a.ReplaceId.Length + a.ReplyId.Length > 0 ? new XmppSendExtras(a.ReplaceId, a.ReplyTo, a.ReplyId, a.ReplyQuote) : null); break;
            case "retract":       await DeleteOwnAsync(session, a.Conversation, a.IsRoom, a.Text, a.Name, ct); break;
            case "displayed":     if (session.Client is { } c) await c.SendDisplayedAsync(a.Conversation, a.Text, ct); break;
            case "sendurl":       await SendUrlAsync(session, a.Conversation, a.IsRoom, a.Text, ct); break;
            case "presence":      await SetPresenceAsync(session, a.Show, a.Status, ct); break;
            case "picture":       await SetPictureAsync(session, a.Text.Length > 0 ? Convert.FromBase64String(a.Text) : null, ct); break;
            case "join":          await JoinSavedRoomAsync(session, a.Conversation, ct); break;
            case "leave":         await LeaveRoomAsync(session, a.Conversation, ct); break;
            case "invite":        await AnswerInviteAsync(session, a.Conversation, a.Accept, ct); break;
            case "room":          await ReadRoomAgainAsync(session, a.Conversation, ct); break;
            case "addcontact":    await AddContactAsync(session, a.Conversation, a.Name, ct); break;
            case "removecontact": await RemoveContactAsync(session, a.Conversation, ct); break;
            case "request":       await AnswerRequestAsync(session, a.Conversation, a.Accept, ct); break;
            case "role":          await SetRoomRoleAsync(session, a.Conversation, a.Name, a.Status, a.Text, ct); break;
            case "affiliation":   await SetRoomAffiliationAsync(session, a.Conversation, a.Name, a.Status, a.Text, ct); break;
            case "subject":       await SetRoomSubjectAsync(session, a.Conversation, a.Text, ct); break;
            case "roominvite":    await InviteToRoomAsync(session, a.Conversation, a.Name, a.Text, ct); break;
            case "bans":          return JsonSerializer.Serialize(await RoomBansAsync(session, a.Conversation, ct));
            case "react":         await SendReactionsAsync(session, a.Conversation, a.IsRoom, a.Text, [.. a.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)], ct); break;
            case "details":       return JsonSerializer.Serialize(await RoomDetailsAsync(session, a.Conversation, ct));
            case "browse":        return JsonSerializer.Serialize(await BrowseRoomsAsync(session, a.Conversation, ct));
            case "slot":          return JsonSerializer.Serialize(await UploadSlotAsync(session, a.Name, a.Size, a.ContentType, ct));
            default:              throw new InvalidOperationException($"Nothing knows how to do {command.Kind}.");
        }

        return "";
    }

    /// <summary>A client changed a room's settings in the database. The connection goes by what it holds, so it holds this too.</summary>
    private async Task ReadRoomAgainAsync(JabberSession session, string roomJid, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = session.Account.Id;
        if (await db.JabberRooms.AsNoTracking().FirstOrDefaultAsync(r => r.AccountId == id && r.RoomJid == roomJid, ct) is not { } saved) return;

        session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(roomJid, out var room)) return;
            room.Room.AlertMode = saved.AlertMode;
            room.Room.RoomType = saved.RoomType;
        });

        RoomTypesChanged();
        SessionChanged?.Invoke(session);
    }

    /// <summary>Which rooms feed intel may have changed, here and on every client.</summary>
    private void RoomTypesChanged()
    {
        IntelRoomsChanged?.Invoke();
        Tell("intelrooms");
    }
}
