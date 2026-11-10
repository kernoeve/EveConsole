using System.Collections.Concurrent;
using System.Text.Json;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Irc;

/// <summary>
/// How one IRC connection serves every client of a shared database, as Jabber's does (see JabberRelay).
///
/// <para>Whichever process holds the worker lease keeps the connections and writes what they bring to the database, so
/// what is said is saved while no window is open, and every client shows the same channels, people and unread. The
/// others show what it wrote, and ask it to do anything that needs the server. The view models neither know nor care:
/// an <see cref="IrcSession"/> looks the same filled by a connection here as filled from the database, and the service
/// methods they call work out which is which.</para>
///
/// <list type="bullet">
/// <item>Messages: rows in IrcMessages, as ever. A signal says to look; every client finds them by id, so a lost signal
/// only delays them until the next look.</item>
/// <item>How a connection stands (its nickname, who is in which channel): <see cref="IrcLiveState"/>, one row per server
/// and one per channel.</item>
/// <item>Asking: <see cref="IrcCommand"/>. ⚠️ Never a password: logins and channel keys are saved first, with the other
/// service logins, where the worker reads them.</item>
/// </list>
///
/// <para>⚠️ One connection per server for the whole database, which is also what IRC wants: a nickname has one
/// connection, and two computers each connecting would fight over it.</para>
///
/// <para>On SQLite there is one process, so none of this runs: <see cref="ActsHere"/> is always true.</para>
/// </summary>
public sealed partial class IrcService
{
    /// <summary>
    /// How these look on the shared channel. ⚠️ JSON with a Kind, like everything else on it: a client of an older build
    /// hands whatever it does not recognise to its alarm path. ⚠️ Fields rather than one string split on a mark: a
    /// nickname or a channel can hold any mark that might be chosen.
    /// </summary>
    private sealed record IrcSignal(string Kind, string What, int Server = 0, long Id = 0, string Conversation = "", string Nick = "", bool On = false);

    private const string SignalKind = "irc";
    private const string SignalStart = "{\"Kind\":\"" + SignalKind + "\"";

    /// <summary>Which row holds the server's own state, the rest being channels by folded name.</summary>
    private const string ServerPart = "";

    private static readonly TimeSpan LiveEvery = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan WatchEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoFinalAnswerAfter = TimeSpan.FromSeconds(90);
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

    /// <summary>Whether this process holds the connections.</summary>
    public bool IsWorker => _isWorker;

    private void Tell(string what, int server = 0, long id = 0, string conversation = "", string nick = "", bool on = false)
    {
        if (Relays) _ = signals!.PublishAsync(JsonSerializer.Serialize(new IrcSignal(SignalKind, what, server, id, conversation, nick, on)));
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
            await LoadServersAsync(ct);

            if (Relays)
            {
                // What the last worker left says nothing about this one.
                await using (var db = await dbFactory.CreateDbContextAsync(ct))
                    await db.IrcLiveStates.ExecuteDeleteAsync(ct);
                _written.Clear();

                // ⚠️ From the clock, not from one: a client that read the last worker's rows would take the same numbers
                // from this one as nothing new.
                _liveVersion = DateTimeOffset.UtcNow.UtcTicks;

                _workerRun = new CancellationTokenSource();
                HookLiveWriter();
                foreach (var session in Sessions) _liveDirty[session.Server.Id] = 0;
                _ = RunCommandsAsync(_workerRun.Token);
                _ = RunLiveWriterAsync(_workerRun.Token);
            }

            foreach (var session in Sessions.Where(s => s.Server.AutoConnect)) Connect(session);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errors.Log(nameof(IrcService), "take over the connections", e);
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
                await db.IrcLiveStates.ExecuteDeleteAsync();
            foreach (var session in Sessions) Tell("live", session.Server.Id);
        }
        catch (Exception e) { errors.Log(nameof(IrcService), "stand down", e); }

        await StartShowingAsync(CancellationToken.None);
    }

    /// <summary>From here on, what is shown comes from whoever holds the lease.</summary>
    private async Task StartShowingAsync(CancellationToken ct)
    {
        if (!Relays || _isWorker || _stopping || AppRuntime.IsHeadless) return;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            _messageFloor = await db.IrcMessages.AsNoTracking().MaxAsync(m => (long?)m.Id, ct) ?? 0;
        _lastDelivered = _messageFloor;
        lock (_delivered) { _delivered.Clear(); _withSound.Clear(); }

        foreach (var session in Sessions) await RefreshLiveAsync(session.Server.Id, ct);

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

                foreach (var session in Sessions) await RefreshLiveAsync(session.Server.Id, ct);
                await DeliverNewMessagesAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { errors.Log(nameof(IrcService), "watch the background process", e); }
        }
    }

    private void ShowWorkerGone()
    {
        foreach (var session in Sessions)
        {
            ClearShown(session);
            session.State = IrcState.WaitingForWorker;
            SessionChanged?.Invoke(session);
        }
        lock (_seenLive) _seenLive.Clear();
    }

    /// <summary>What was shown came from another worker. ⚠️ Kept, it would look like this one's until replaced.</summary>
    private void ForgetWhatWasShown()
    {
        lock (_seenLive) _seenLive.Clear();
        foreach (var session in Sessions)
        {
            ClearShown(session);
            session.Locked(s => s.ChannelMap.Clear());
        }
    }

    private static void ClearShown(IrcSession session)
    {
        session.Locked(s =>
        {
            foreach (var c in s.ChannelMap.Values) { c.Joined = false; c.Members.Clear(); c.Topic = ""; c.TopicBy = ""; c.Failure = ""; }
            s.PeopleMap.Clear();
            s.InviteMap.Clear();
            s.ShownSupport = null;
        });
        session.State = IrcState.Offline;
        session.Problem = IrcProblem.None;
        session.Detail = "";
        session.NextAttempt = null;
        session.Nick = "";
        session.LastRefusal = "";
    }

    /// <summary>The servers as saved: another client may have added, changed or removed one.</summary>
    private async Task LoadServersAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var servers = await db.IrcServers.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);

        List<IrcSession> tracked = [.. servers.Select(Track)];

        List<IrcSession> gone;
        lock (_gate)
        {
            gone = [.. _sessions.Values.Where(s => !servers.Any(x => x.Id == s.Server.Id))];
            foreach (var session in gone) _sessions.Remove(session.Server.Id);
        }

        foreach (var session in gone)
        {
            if (!_isWorker) continue;
            await DisconnectHereAsync(session);
            var id = session.Server.Id;
            await db.IrcLiveStates.Where(l => l.ServerId == id).ExecuteDeleteAsync(ct);
            _liveDirty.TryRemove(id, out _);
        }

        // ⚠️ Said even when one stays offline: what lists the servers looked before this read them.
        foreach (var session in tracked.Concat(gone)) SessionChanged?.Invoke(session);
    }

    // ── Signals ───────────────────────────────────────────────────────────────

    /// <summary>For the signal listener: true when the signal is IRC's, which is then handled away from the caller's thread.</summary>
    public bool TryApplySignal(string payload)
    {
        if (!payload.StartsWith(SignalStart, StringComparison.Ordinal)) return false;

        // A worker with no window has nothing to show, and one waiting for the lease has nothing to do.
        if ((!AppRuntime.IsHeadless || _isWorker) && JsonSerializer.Deserialize<IrcSignal>(payload) is { } signal)
            _ = ApplySignalAsync(signal);
        return true;
    }

    private async Task ApplySignalAsync(IrcSignal signal)
    {
        await Task.Yield();

        try
        {
            switch (signal.What)
            {
                case "msg" when !_isWorker:
                    await MessageSignalAsync(signal.Id, signal.On);
                    break;

                case "react" when !_isWorker:
                    ReactionsChanged?.Invoke(signal.Server, signal.Conversation);
                    break;

                case "typing" when !_isWorker:
                    TypingChanged?.Invoke(signal.Server, signal.Conversation, signal.Nick, signal.On);
                    break;

                case "typeout" when _isWorker:
                    if (SessionOf(signal.Server) is { } writing) await SendTypingAsync(writing, signal.Conversation, signal.On);
                    break;

                case "unread":
                    UnreadChanged?.Invoke();
                    break;

                case "live" when !_isWorker:
                    await RefreshLiveAsync(signal.Server, CancellationToken.None);
                    break;

                case "servers":
                    await LoadServersAsync(CancellationToken.None);
                    break;

                case "intelrooms" when !_isWorker:
                    IntelRoomsChanged?.Invoke();
                    break;

                case "cmd" when _isWorker:
                    WakeCommands();
                    break;

                case "done":
                    if (_waiting.TryGetValue(signal.Id, out var waiter)) waiter.TrySetResult();
                    break;
            }
        }
        catch (Exception e) { errors.Log(nameof(IrcService), "handle a signal", e); }
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
    /// sound. ⚠️ The rest are gathered up: catching up on a channel can save hundreds in a burst.
    /// </summary>
    private void TellMessageSaved(long id, bool sound)
    {
        if (!Relays) return;

        if (sound) { Tell("msg", id: id, on: true); return; }
        if (Interlocked.Exchange(ref _quietMessageTold, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            Interlocked.Exchange(ref _quietMessageTold, 0);
            Tell("msg");
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
                if (await db.IrcMessages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id) is { } row) AlertArrived?.Invoke(row);
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
                var rows = await db.IrcMessages.AsNoTracking().Where(m => m.Id > from).OrderBy(m => m.Id).Take(MessagePage).ToListAsync(ct);

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

    private sealed record LiveServer(
        IrcState State, IrcProblem Problem, string Detail, string CertificateFingerprint, string CertificateSubject,
        DateTimeOffset? NextAttempt, string Nick, string Network, bool IsAway, bool HasHistory, bool HasTags, bool IsEncrypted,
        string LastRefusal, string ChannelTypes, string RankModes, string RankSymbols, List<IrcPerson> People, List<IrcInvite> Invites);

    private sealed record LiveMember(string Nick, string UserHost, string Account, string Ranks, bool Away, string AwayMessage);

    private sealed record LiveChannel(IrcChannel Channel, bool Joined, string ServerName, string Topic, string TopicBy, string Failure, List<LiveMember> Members);

    private readonly ConcurrentDictionary<int, byte> _liveDirty = new();
    private readonly Dictionary<int, Dictionary<string, string>> _written = [];
    private bool _liveWriterHooked;
    private long _liveVersion;
    private bool _liveWriteFailing;

    private void HookLiveWriter()
    {
        if (_liveWriterHooked) return;
        _liveWriterHooked = true;
        SessionChanged += session => { if (_isWorker) _liveDirty[session.Server.Id] = 0; };
    }

    private static Dictionary<string, string> LiveParts(IrcSession session) => session.Locked(s =>
    {
        var support = s.Support;
        var parts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ServerPart] = JsonSerializer.Serialize(new LiveServer(
                s.State, s.Problem, s.Detail, s.CertificateFingerprint, s.CertificateSubject, s.NextAttempt, s.Nick, s.Network,
                s.IsAway, s.HasHistory, s.HasTags, s.IsEncrypted, s.LastRefusal,
                support.ChannelTypes, new string([.. support.Ranks.Select(r => r.Mode)]), new string([.. support.Ranks.Select(r => r.Symbol)]),
                [.. s.PeopleMap.Values], [.. s.InviteMap.Values])),
        };

        foreach (var (key, c) in s.ChannelMap)
            parts[key] = JsonSerializer.Serialize(new LiveChannel(
                c.Channel, c.Joined, c.ServerName, c.Topic, c.TopicBy, c.Failure,
                [.. c.Members.Values.Select(m => new LiveMember(m.Nick, m.UserHost, m.Account, m.Ranks, m.Away, m.AwayMessage))]));

        return parts;
    });

    private async Task RunLiveWriterAsync(CancellationToken ct)
    {
        var passes = 0;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(LiveEvery, ct); }
            catch (OperationCanceledException) { return; }

            // ⚠️ Not everything that changes raises anything: a topic's setter, for one. And a lost "servers" signal
            // would leave a deleted server connected, saving rows nothing shows: so the servers are read again too.
            if (++passes % 30 == 0)
            {
                try { await LoadServersAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { errors.Log(nameof(IrcService), "read the servers again", e); }
                foreach (var session in Sessions) _liveDirty[session.Server.Id] = 0;
            }

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
                    if (!_liveWriteFailing) errors.Log(nameof(IrcService), "write how the connections stand", e);
                    _liveWriteFailing = true;
                }
            }
        }
    }

    /// <summary>Writes what changed since the last write, and tells the clients.</summary>
    private async Task WriteLiveAsync(IrcSession session, CancellationToken ct)
    {
        var id = session.Server.Id;
        var parts = LiveParts(session);
        var had = _written.GetValueOrDefault(id) ?? [];

        var changed = parts.Where(p => !had.TryGetValue(p.Key, out var json) || json != p.Value).Select(p => p.Key).ToList();
        var gone = had.Keys.Where(k => !parts.ContainsKey(k)).ToList();
        if (changed.Count == 0 && gone.Count == 0) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.IrcLiveStates.Where(l => l.ServerId == id && (changed.Contains(l.Part) || gone.Contains(l.Part))).ToListAsync(ct);

        foreach (var part in changed)
        {
            var row = rows.FirstOrDefault(r => r.Part == part);
            if (row is null) db.IrcLiveStates.Add(row = new IrcLiveState { ServerId = id, Part = part });
            row.Json = parts[part];
            row.Version = ++_liveVersion;
        }
        db.IrcLiveStates.RemoveRange(rows.Where(r => gone.Contains(r.Part)));
        await db.SaveChangesAsync(ct);

        _written[id] = parts;
        Tell("live", id);
    }

    // ── How the connections stand: read by every other client ─────────────────

    private readonly SemaphoreSlim _viewGate = new(1, 1);
    private readonly Dictionary<(int Server, string Part), long> _seenLive = [];

    /// <summary>Brings a session up to what the worker last wrote, reading only the rows that changed.</summary>
    private async Task RefreshLiveAsync(int serverId, CancellationToken ct)
    {
        await _viewGate.WaitAsync(ct);
        try
        {
            if (_isWorker) return;

            if (SessionOf(serverId) is null) await LoadServersAsync(ct);
            if (SessionOf(serverId) is not { } session) return;

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var versions = await db.IrcLiveStates.AsNoTracking().Where(l => l.ServerId == serverId)
                .Select(l => new { l.Part, l.Version }).ToListAsync(ct);

            List<string> wanted;
            lock (_seenLive)
                wanted = [.. versions.Where(v => !_seenLive.TryGetValue((serverId, v.Part), out var seen) || seen != v.Version).Select(v => v.Part)];

            var rows = wanted.Count == 0 ? [] : await db.IrcLiveStates.AsNoTracking()
                .Where(l => l.ServerId == serverId && wanted.Contains(l.Part)).ToListAsync(ct);

            var parts = versions.Select(v => v.Part).ToHashSet(StringComparer.Ordinal);

            // Nothing new. ⚠️ Channels checked as well: one this client just added is shown only until the worker says otherwise.
            if (rows.Count == 0 && session.Locked(s => s.ChannelMap.Keys.All(parts.Contains))
                && (parts.Contains(ServerPart) || session.State == IrcState.Offline)) return;

            ApplyLive(session, rows, parts);

            lock (_seenLive)
            {
                foreach (var key in _seenLive.Keys.Where(k => k.Server == serverId && !parts.Contains(k.Part)).ToList()) _seenLive.Remove(key);
                foreach (var row in rows) _seenLive[(serverId, row.Part)] = row.Version;
            }

            SessionChanged?.Invoke(session);
        }
        finally { _viewGate.Release(); }
    }

    private static void ApplyLive(IrcSession session, List<IrcLiveState> rows, HashSet<string> parts)
    {
        // Nobody has written anything for it: the worker has it offline, or has only just started.
        if (!parts.Contains(ServerPart))
        {
            ClearShown(session);
            session.Locked(s => s.ChannelMap.Clear());
            return;
        }

        session.Locked(s =>
        {
            foreach (var row in rows)
            {
                if (row.Part == ServerPart)
                {
                    if (JsonSerializer.Deserialize<LiveServer>(row.Json) is { } a) ApplyServer(s, a);
                }
                else if (JsonSerializer.Deserialize<LiveChannel>(row.Json) is { } c)
                {
                    // ⚠️ The same object when there is one: what is on screen may be holding on to it.
                    if (!s.ChannelMap.TryGetValue(row.Part, out var state)) s.ChannelMap[row.Part] = state = new IrcChannelState(c.Channel);
                    state.Channel = c.Channel; state.Joined = c.Joined; state.ServerName = c.ServerName;
                    state.Topic = c.Topic; state.TopicBy = c.TopicBy; state.Failure = c.Failure;
                    state.Members.Clear();
                    foreach (var m in c.Members)
                        state.Members[s.Fold(m.Nick)] = new IrcMember(m.Nick) { UserHost = m.UserHost, Account = m.Account, Ranks = m.Ranks, Away = m.Away, AwayMessage = m.AwayMessage };
                }
            }

            foreach (var key in s.ChannelMap.Keys.Where(k => !parts.Contains(k)).ToList()) s.ChannelMap.Remove(key);
        });
    }

    private static void ApplyServer(IrcSession s, LiveServer a)
    {
        s.State = a.State; s.Problem = a.Problem; s.Detail = a.Detail;
        s.CertificateFingerprint = a.CertificateFingerprint; s.CertificateSubject = a.CertificateSubject;
        s.NextAttempt = a.NextAttempt; s.Nick = a.Nick; s.Network = a.Network; s.IsAway = a.IsAway;
        s.HasHistory = a.HasHistory; s.HasTags = a.HasTags; s.IsEncrypted = a.IsEncrypted; s.LastRefusal = a.LastRefusal;

        var support = new IrcSupport();
        support.Apply([$"CHANTYPES={a.ChannelTypes}", $"PREFIX=({a.RankModes}){a.RankSymbols}"]);
        s.ShownSupport = support;

        s.PeopleMap.Clear();
        foreach (var p in a.People) s.PeopleMap[s.Fold(p.Nick)] = p;
        s.InviteMap.Clear();
        foreach (var i in a.Invites) s.InviteMap[s.Fold(i.Channel)] = i;
    }

    // ── Asking the worker ─────────────────────────────────────────────────────

    private const int CommandWaiting = 0;
    private const int CommandRunning = 1;
    private const int CommandDone    = 2;

    /// <param name="Secret">
    /// Where the words of a typed line wait for the worker, instead of <paramref name="Text"/>: with the other service
    /// logins (SharedLogins), read once and removed. ⚠️ A typed line can carry a password ("/msg NickServ IDENTIFY ...",
    /// "/join #chan key", "/quote PASS ..."), and this row is plain text in a shared table.
    /// </param>
    private sealed record CommandArgs(
        string Target = "", string Text = "", string Nick = "", string UserHost = "", string ReplyTo = "",
        string Conversation = "", bool On = false, char Mode = ' ', string Secret = "");

    public static string TypedLineReference(string id) => $"irc-line-{id}";

    /// <summary>Asks the worker to send what was typed, the words kept out of the request: see <see cref="CommandArgs"/>.</summary>
    private async Task<string> AskWorkerToSendAsync(IrcSession session, string target, string text, string replyTo, CancellationToken ct)
    {
        var reference = TypedLineReference(Guid.NewGuid().ToString("N"));
        await Task.Run(() => AppConfig.SetServicePassword(reference, text), ct);
        try { return await AskWorkerAsync(session.Server.Id, "send", new CommandArgs(target, ReplyTo: replyTo, Secret: reference), ct); }
        finally { await Task.Run(() => AppConfig.SetServicePassword(reference, null), CancellationToken.None); }
    }

    /// <summary>The words a client left for the worker, taken so they are read once.</summary>
    private static async Task<string> TakeTypedLineAsync(string reference, CancellationToken ct)
    {
        if (!reference.StartsWith("irc-line-", StringComparison.Ordinal)) return "";
        var text = await Task.Run(() => AppConfig.GetServicePassword(reference), ct) ?? "";
        await Task.Run(() => AppConfig.SetServicePassword(reference, null), ct);
        return text;
    }

    private readonly ConcurrentDictionary<long, TaskCompletionSource> _waiting = new();

    /// <summary>⚠️ One at a time, so the ids follow the order asked: a Connect then a Disconnect must not swap.</summary>
    private readonly SemaphoreSlim _addGate = new(1, 1);

    private async Task<long> AddCommandAsync(int serverId, string kind, CommandArgs args, CancellationToken ct)
    {
        await _addGate.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var row = new IrcCommand
            {
                ServerId = serverId, Kind = kind, Args = JsonSerializer.Serialize(args),
                CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            db.IrcCommands.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }
        finally { _addGate.Release(); }
    }

    /// <summary>Asks for something nobody waits on. If it is not done, what comes back from the worker says so.</summary>
    private async Task PostToWorkerAsync(int serverId, string kind, CommandArgs args, CancellationToken ct = default)
    {
        await AddCommandAsync(serverId, kind, args, ct);
        Tell("cmd");
    }

    /// <summary>
    /// The same, from something that cannot wait for it. ⚠️ Its place in the order is taken now, not when a thread gets to
    /// it: a Disconnect asked just after must come after.
    /// </summary>
    private void PostToWorker(int serverId, string kind, CommandArgs args)
    {
        var turn = _addGate.WaitAsync();
        _ = Task.Run(async () =>
        {
            await turn;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                db.IrcCommands.Add(new IrcCommand
                {
                    ServerId = serverId, Kind = kind, Args = JsonSerializer.Serialize(args),
                    CreatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });
                await db.SaveChangesAsync();
                Tell("cmd");
            }
            catch (Exception e) { errors.Log(nameof(IrcService), $"ask for {kind}", e); }
            finally { _addGate.Release(); }
        });
    }

    /// <summary>
    /// Asks the worker and waits for its answer. Fails as if it had been tried here when it fails there.
    ///
    /// <para>⚠️ Taken back if nothing picks it up, so "not done" is true. The signal only saves waiting: the row is the
    /// answer, and is looked at anyway in case the signal was lost.</para>
    /// </summary>
    private async Task<string> AskWorkerAsync(int serverId, string kind, CommandArgs args, CancellationToken ct = default)
    {
        var id = await AddCommandAsync(serverId, kind, args, ct);
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
                var row = await db.IrcCommands.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

                if (row is { State: CommandDone })
                {
                    await db.IrcCommands.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
                    if (row.Error.Length > 0) throw new InvalidOperationException(row.Error);
                    return row.Result;
                }

                var waited = DateTimeOffset.UtcNow - started;

                if (row is { State: CommandWaiting } && waited > PickupWait
                    && await db.IrcCommands.Where(c => c.Id == id && c.State == CommandWaiting).ExecuteDeleteAsync(ct) > 0)
                    throw new InvalidOperationException("Nothing is running the background processes, so this was not done.");

                if (row is null || waited > NoFinalAnswerAfter)
                    throw new InvalidOperationException("The background process did not answer, so this may not have been done.");
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
                errors.Log(nameof(IrcService), "run what was asked", e);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunWaitingCommandsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var tooOld = DateTimeOffset.UtcNow.Subtract(CommandsExpireAfter).ToUnixTimeSeconds();
        await db.IrcCommands.Where(c => c.CreatedUnix < tooOld).ExecuteDeleteAsync(ct);

        foreach (var command in await db.IrcCommands.AsNoTracking().Where(c => c.State == CommandWaiting).OrderBy(c => c.Id).ToListAsync(ct))
        {
            var id = command.Id;
            if (await db.IrcCommands.Where(c => c.Id == id && c.State == CommandWaiting)
                        .ExecuteUpdateAsync(u => u.SetProperty(c => c.State, CommandRunning), ct) == 0) continue;

            // ⚠️ Sends one at a time, to keep what someone says in the order they said it, and connecting and
            // disconnecting with them, as one may follow the other. Nothing else waits behind them: listing a big
            // network's channels takes a minute.
            if (command.Kind is "send" or "react" or "connect" or "disconnect" or "reconnect") await RunCommandAsync(command, ct);
            else _ = RunCommandAsync(command, ct);
        }
    }

    private async Task RunCommandAsync(IrcCommand command, CancellationToken ct)
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
            await db.IrcCommands.Where(c => c.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.State, CommandDone)
                .SetProperty(c => c.Result, result)
                .SetProperty(c => c.Error, error));
            Tell("done", id: id);
        }
        catch (Exception e) { errors.Log(nameof(IrcService), "answer what was asked", e); }
    }

    private async Task<string> DoCommandAsync(IrcCommand command, CancellationToken ct)
    {
        // ⚠️ Read again first for anything that connects: the server's settings may have been changed by the client asking.
        if (SessionOf(command.ServerId) is null || command.Kind is "connect" or "reconnect") await LoadServersAsync(ct);
        var session = SessionOf(command.ServerId) ?? throw new InvalidOperationException("That server is gone.");
        var a = JsonSerializer.Deserialize<CommandArgs>(command.Args) ?? new CommandArgs();

        switch (command.Kind)
        {
            case "connect":    Connect(session); break;
            case "disconnect": await DisconnectHereAsync(session); break;
            case "reconnect":  await DisconnectHereAsync(session); Connect(session); break;
            case "send":       return await SendAsync(session, a.Target, a.Secret.Length > 0 ? await TakeTypedLineAsync(a.Secret, ct) : a.Text, a.ReplyTo, ct);
            case "react":      await ReactAsync(session, a.Target, a.Conversation, a.ReplyTo, a.Text, ct); break;
            case "away":       await SetAwayAsync(session, a.Text, ct); break;
            case "nick":       await ChangeNickAsync(session, a.Nick, ct); break;
            case "join":       await JoinChannelAsync(session, a.Target, "", null, ct); break;
            case "leave":      await LeaveChannelAsync(session, a.Target, ct); break;
            case "rejoin":     await RejoinAsync(session, a.Target, ct); break;
            case "invite":     await AnswerInviteAsync(session, a.Target, a.On, ct); break;
            case "channel":    await ReadChannelAgainAsync(session, a.Target, ct); break;
            case "list":       return JsonSerializer.Serialize(await ListChannelsAsync(session, ct));
            case "topic":      await SetTopicAsync(session, a.Target, a.Text, ct); break;
            case "kick":       await KickAsync(session, a.Target, a.Nick, a.Text, ct); break;
            case "ban":        await BanAsync(session, a.Target, new IrcMember(a.Nick) { UserHost = a.UserHost }, a.Text, ct); break;
            case "rank":       await SetRankAsync(session, a.Target, a.Nick, a.Mode, a.On); break;
            case "bans":       return JsonSerializer.Serialize(await BansAsync(session, a.Target, ct));
            case "liftban":    await LiftBanAsync(session, a.Target, a.Text, ct); break;
            case "inviteto":   await InviteAsync(session, a.Nick, a.Target, ct); break;
            default:           throw new InvalidOperationException($"Nothing knows how to do {command.Kind}.");
        }

        return "";
    }

    /// <summary>A client changed a channel's settings in the database. The connection goes by what it holds, so it holds this too.</summary>
    private async Task ReadChannelAgainAsync(IrcSession session, string name, CancellationToken ct)
    {
        if (session.ChannelOf(name) is not { } state) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = state.Channel.Id;
        if (await db.IrcChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) is not { } saved) return;

        session.Locked(_ => { state.Channel.AlertMode = saved.AlertMode; state.Channel.RoomType = saved.RoomType; });
        RoomTypesChanged();
        SessionChanged?.Invoke(session);
    }

    /// <summary>Which channels feed intel may have changed, here and on every client.</summary>
    private void RoomTypesChanged()
    {
        IntelRoomsChanged?.Invoke();
        Tell("intelrooms");
    }
}
