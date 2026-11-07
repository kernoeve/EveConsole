using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Jabber;

public enum JabberState { Offline, Connecting, Connected, Reconnecting, Failed, WaitingForWorker }

/// <summary>How much has come through one conversation: its last message, as saved, and how many in the last day.</summary>
public sealed record JabberConversationActivity(int AccountId, string Conversation, bool IsRoom, string LastAt, int LastDay);

/// <summary>What a stopped connection needs from the person, if anything.</summary>
public enum JabberProblem { None, Password, Certificate, NoEncryption, Replaced, Other }

/// <summary>How one saved direct conversation stands.</summary>
public sealed record JabberDirectSummary(int AccountId, string Conversation, string LastText, string LastAt, int Unread, int Alerts);

/// <summary>A contact as of the latest news: on the list, and how they appear right now.</summary>
public sealed class JabberContact(string jid)
{
    public string   Jid          { get; } = jid;
    public string   Name         { get; set; } = "";
    public string   Subscription { get; set; } = "none";
    public XmppShow Show         { get; set; } = XmppShow.Offline;
    public string   Status       { get; set; } = "";

    /// <summary>Online per connected resource: gone only when the last of their clients goes.</summary>
    public Dictionary<string, XmppShow> Resources { get; } = [];
}

public sealed class JabberRoomState(JabberRoom room)
{
    public JabberRoom Room   { get; set; } = room;
    public bool   Joined     { get; set; }
    public string Nick       { get; set; } = "";
    public string Subject    { get; set; } = "";

    /// <summary>What the last attempt to join ran into, and the room's own words.</summary>
    public XmppRoomFailure? Failure { get; set; }
    public string FailureText { get; set; } = "";

    /// <summary>
    /// The room has finished replaying its history, which its subject marks. ⚠️ After that a date on a message says
    /// nothing: a member could put an old one on theirs to name someone without setting off the alert.
    /// </summary>
    public bool HistoryDone { get; set; }

    /// <summary>The room says it keeps an archive. Null until asked.</summary>
    public bool? HasArchive { get; set; }

    /// <summary>It says it keeps one, but would not share it with this account.</summary>
    public bool ArchiveRefused { get; set; }

    /// <summary>Where catching up on this room starts. Null for a room with nothing saved yet.</summary>
    internal DateTimeOffset? CatchUpFrom { get; set; }

    /// <summary>
    /// What the room has sent while its archive is being read, or null when it is not. ⚠️ A conversation lists in
    /// the order messages were saved, so these wait their turn behind the older ones the archive is still giving.
    /// </summary>
    internal List<XmppMessage>? Held { get; set; }

    public Dictionary<string, XmppOccupant> Occupants { get; } = new(StringComparer.Ordinal);
}

/// <summary>One account's connection, kept up for as long as someone wants it.</summary>
public sealed class JabberSession(JabberAccount account)
{
    private readonly object _gate = new();

    public JabberAccount Account { get; set; } = account;
    public JabberState   State   { get; internal set; } = JabberState.Offline;
    public JabberProblem Problem { get; internal set; }
    public string        Detail  { get; internal set; } = "";

    /// <summary>For a certificate problem: what the person is asked to accept.</summary>
    public string CertificateFingerprint { get; internal set; } = "";
    public string CertificateSubject     { get; internal set; } = "";

    /// <summary>When the next attempt is, for one waiting to reconnect.</summary>
    public DateTimeOffset? NextAttempt { get; internal set; }

    public XmppShow Show   { get; internal set; } = XmppShow.Online;
    public string   Status { get; internal set; } = "";

    public IReadOnlyList<string> ConferenceServices { get; internal set; } = [];

    /// <summary>Where this server takes files, if it does.</summary>
    public XmppUploadService? UploadService { get; internal set; }

    /// <summary>The server says it keeps an archive of this account's direct messages. Null until asked.</summary>
    public bool? HasArchive { get; internal set; }

    internal XmppClient? Client { get; set; }

    /// <summary>Where this connection's messages go to be saved.</summary>
    internal ChannelWriter<XmppMessage>? Inbox { get; set; }
    internal CancellationTokenSource? Run { get; set; }
    internal Task? Loop { get; set; }

    internal Dictionary<string, JabberContact> ContactMap { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Whose clients have shown they understand typing notices. ⚠️ Nobody else is sent any, as the protocol
    /// asks: a broadcast bot or a pager has no use for them, and some treat every message as a command.
    /// </summary>
    internal HashSet<string> SendsTyping { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, JabberRoomState> RoomMap { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Requests { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, XmppInvite> InviteMap { get; } = new(StringComparer.Ordinal);

    public T Locked<T>(Func<JabberSession, T> read) { lock (_gate) return read(this); }
    internal void Locked(Action<JabberSession> change) { lock (_gate) change(this); }

    public IReadOnlyList<JabberContact> Contacts => Locked(s => (IReadOnlyList<JabberContact>)[.. s.ContactMap.Values]);
    public IReadOnlyList<JabberRoomState> Rooms => Locked(s => (IReadOnlyList<JabberRoomState>)[.. s.RoomMap.Values]);
    public IReadOnlyList<string> SubscriptionRequests => Locked(s => (IReadOnlyList<string>)[.. s.Requests]);
    public IReadOnlyList<XmppInvite> Invites => Locked(s => (IReadOnlyList<XmppInvite>)[.. s.InviteMap.Values]);
}

/// <summary>Unread messages in one conversation.</summary>
public readonly record struct JabberUnread(int AccountId, string Conversation, int Count, int Alerts, int Mentions);

/// <summary>
/// The Jabber accounts: keeping each connected as asked, and saving what they say.
///
/// <para>⚠️ One process connects, and that is whichever holds the worker lease, with or without a window: two would fight
/// for the same login. Every other client shows what it saves, and asks it for the rest. See JabberRelay.cs.</para>
/// </summary>
public sealed partial class JabberService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors, ClientSignals? signals = null)
{
    /// <summary>How long to wait between attempts: quick for a blip, then settling once a server is down.</summary>
    private static readonly int[] BackoffSeconds = [5, 10, 30, 60, 120, 300];

    /// <summary>A connection that stayed up this long starts its backoff over.</summary>
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<int, JabberSession> _sessions = [];
    private readonly object _gate = new();

    public event Action<JabberSession>? SessionChanged;

    /// <summary>A message was saved. Fires only for one not seen before, on a background thread.</summary>
    public event Action<JabberStoredMessage>? MessageStored;

    /// <summary>A message arrived that calls for attention, for an alert sound.</summary>
    public event Action<JabberStoredMessage>? AlertArrived;

    public event Action? UnreadChanged;

    /// <summary>A room marked Intel brought lines for the intel parser.</summary>
    public event Action? IntelLinesSaved;

    /// <summary>Which rooms are marked Intel may have changed.</summary>
    public event Action? IntelRoomsChanged;

    /// <summary>Someone corrected a saved message: the conversation needs reading again.</summary>
    public event Action<int, string>? ConversationEdited;

    /// <summary>Someone started or stopped typing: account, conversation, whether they are.</summary>
    public event Action<int, string, bool>? TypingChanged;

    public static string PasswordReference(int accountId) => $"jabber-{accountId}";

    /// <summary>Loads the accounts, and starts showing how they stand. Connecting is for whoever holds the lease: <see cref="BecomeWorkerAsync"/>.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (AppRuntime.IsTray) return;

        await LoadAccountsAsync(ct);
        await StartShowingAsync(ct);
    }

    /// <summary>The process is closing: the connections, closed properly, and what said they were up.</summary>
    public async Task StopAsync()
    {
        _stopping = true;
        _watchRun?.Cancel();

        if (_isWorker) await StandDownAsync();
        else await Task.WhenAll(Sessions.Select(DisconnectHereAsync));
    }

    public IReadOnlyList<JabberSession> Sessions
    {
        get { lock (_gate) return [.. _sessions.Values.OrderBy(s => s.Account.Id)]; }
    }

    public JabberSession? SessionOf(int accountId)
    {
        lock (_gate) return _sessions.GetValueOrDefault(accountId);
    }

    private JabberSession Track(JabberAccount account)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(account.Id, out var known)) { known.Account = account; return known; }
            return _sessions[account.Id] = new JabberSession(account) { Show = SavedShowOf(account), Status = account.SavedStatus };
        }
    }

    /// <summary>The status this account was left on. Nothing but a real choice: a client cannot sign in as offline.</summary>
    private static XmppShow SavedShowOf(JabberAccount account) =>
        Enum.IsDefined((XmppShow)account.SavedShow) && (XmppShow)account.SavedShow != XmppShow.Offline ? (XmppShow)account.SavedShow : XmppShow.Online;

    // ── Accounts ──────────────────────────────────────────────────────────────

    /// <summary>Saves an account, new or changed. A null password leaves the saved one as it is.</summary>
    public async Task<JabberSession> SaveAccountAsync(JabberAccount account, string? password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (account.Id == 0) db.JabberAccounts.Add(account);
        else db.JabberAccounts.Update(account);
        await db.SaveChangesAsync(ct);

        if (password is not null)
        {
            var id = account.Id;
            await Task.Run(() => AppConfig.SetServicePassword(PasswordReference(id), password), ct);
        }

        var session = Track(account);
        Tell("accounts");
        SessionChanged?.Invoke(session);
        return session;
    }

    /// <summary>Forgets an account, its rooms and every message saved under it. Permanent.</summary>
    public async Task DeleteAccountAsync(int accountId, CancellationToken ct = default)
    {
        JabberSession? session;
        lock (_gate) _sessions.Remove(accountId, out session);
        if (session is not null && ActsHere) await DisconnectHereAsync(session);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JabberMessages.Where(m => m.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.JabberRooms.Where(r => r.AccountId == accountId).ExecuteDeleteAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.JabberEmotePacks.Where(l => l.AccountId == accountId).ExecuteDeleteAsync(ct);
            await EmoteService.DeleteUnusedAsync(db, ct);
            await tx.CommitAsync(ct);
        }
        await db.JabberAccounts.Where(a => a.Id == accountId).ExecuteDeleteAsync(ct);

        await Task.Run(() => AppConfig.SetServicePassword(PasswordReference(accountId), null), ct);
        Tell("accounts");
        Tell("unread");
        UnreadChanged?.Invoke();
    }

    /// <summary>Accepts the certificate a connection stopped on, and tries again.</summary>
    public async Task TrustCertificateAsync(JabberSession session, CancellationToken ct = default)
    {
        session.Account.TrustedCertificate = session.CertificateFingerprint;
        await SaveAccountAsync(session.Account, null, ct);
        Connect(session);
    }

    // ── Connecting ────────────────────────────────────────────────────────────

    /// <summary>Asks for this account to be connected, and to stay connected until told otherwise.</summary>
    public void Connect(JabberSession session)
    {
        if (AppRuntime.IsTray) return;

        if (!ActsHere)
        {
            PostToWorker(session.Account.Id, "connect", new CommandArgs());
            return;
        }

        lock (_gate)
        {
            if (session.Loop is { IsCompleted: false }) return;

            session.Run  = new CancellationTokenSource();
            session.Loop = Task.Run(() => KeepConnectedAsync(session, session.Run.Token));
        }
    }

    public Task DisconnectAsync(JabberSession session) =>
        ActsHere ? DisconnectHereAsync(session) : PostToWorkerAsync(session.Account.Id, "disconnect", new CommandArgs());

    private async Task DisconnectHereAsync(JabberSession session)
    {
        CancellationTokenSource? run;
        Task? loop;
        lock (_gate) { run = session.Run; loop = session.Loop; }

        // ⚠️ Nothing here may throw: this is what the Disconnect button and closing the app wait on, and a failure
        // would leave one stuck on and the other unable to quit.
        try
        {
            if (run is not null) await run.CancelAsync();
            if (session.Client is { } client) await client.DisposeAsync();
            if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is not (TimeoutException or OperationCanceledException))
        {
            errors.Log(nameof(JabberService), "disconnect", e);
        }
        catch (Exception) { /* it is told to stop, and will */ }

        SetState(session, JabberState.Offline, JabberProblem.None, "");
    }

    /// <summary>
    /// Connects and reconnects until cancelled.
    ///
    /// <para>⚠️ Stops on what trying again cannot fix. A wrong password retried every few minutes can
    /// lock an account, and a login replaced by the owner's other client would have the two throwing
    /// each other off for as long as both are running.</para>
    /// </summary>
    private async Task KeepConnectedAsync(JabberSession session, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            SetState(session, attempt == 0 ? JabberState.Connecting : JabberState.Reconnecting, JabberProblem.None, session.Detail);

            try
            {
                await RunOnceAsync(session, ct);
                if (ct.IsCancellationRequested) break;
                session.Detail = "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (XmppException e) when (e.Failure is XmppFailure.Login or XmppFailure.Certificate or XmppFailure.NoEncryption or XmppFailure.Replaced)
            {
                session.CertificateFingerprint = e.CertificateFingerprint ?? "";
                session.CertificateSubject     = e.CertificateSubject ?? "";
                SetState(session, JabberState.Failed, e.Failure switch
                {
                    XmppFailure.Login        => JabberProblem.Password,
                    XmppFailure.Certificate  => JabberProblem.Certificate,
                    XmppFailure.NoEncryption => JabberProblem.NoEncryption,
                    _                        => JabberProblem.Replaced,
                }, e.Message);
                return;
            }
            catch (Exception e)
            {
                // ⚠️ Everything. This loop is the only thing bringing the connection back, and a database that is
                // busy or a value it does not like must not end that without a word.
                if (e is not (XmppException or IOException or TimeoutException or OperationCanceledException))
                    errors.Log(nameof(JabberService), "keep connected", e);
                session.Detail = e.Message;
            }
            finally
            {
                try
                {
                    if (session.Client is { } client)
                    {
                        session.Client = null;
                        await client.DisposeAsync();
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Log(nameof(JabberService), "end a connection", e); }
                MarkDisconnected(session);
            }

            // Told to stop: the failure that just ended the read loop is that, and not worth retrying.
            if (ct.IsCancellationRequested) break;

            if (DateTimeOffset.UtcNow - started > StableAfter) attempt = 0;

            var wait = TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);
            attempt++;

            session.NextAttempt = DateTimeOffset.UtcNow + wait;
            SetState(session, JabberState.Reconnecting, JabberProblem.None, session.Detail);

            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
            finally { session.NextAttempt = null; }
        }
    }

    private async Task RunOnceAsync(JabberSession session, CancellationToken ct)
    {
        var account  = session.Account;
        var password = await Task.Run(() => AppConfig.GetServicePassword(PasswordReference(account.Id)), ct);
        if (password is null)
            throw new XmppException(XmppFailure.Login, await Task.Run(() => AppConfig.HasServicePassword(PasswordReference(account.Id)), ct)
                ? "The saved password cannot be opened, which is what happens after the database password changes. Type it again."
                : "No password is saved.");

        var connection = new XmppConnection(new XmppLogin(
            account.Address, password, account.Host, account.Port,
            account.Resource.Length > 0 ? account.Resource : WorkerResource,
            account.TrustedCertificate, account.DirectTls));

        await connection.ConnectAsync(ct);

        var client = new XmppClient(connection);
        session.Client = client;

        // One worker per connection keeps messages in order, and the database off the reading thread.
        var inbox = Channel.CreateUnbounded<XmppMessage>(new UnboundedChannelOptions { SingleReader = true });
        session.Inbox = inbox.Writer;
        client.MessageReceived += m => { if (Arriving(session, m) is { } now) inbox.Writer.TryWrite(now); };
        client.TypingChanged += (from, typing) =>
        {
            session.Locked(s => s.SendsTyping.Add(from.Bare));
            TypingChanged?.Invoke(account.Id, from.Bare, typing);
            Tell($"typing|{account.Id}|{(typing ? 1 : 0)}|{from.Bare}");
        };
        client.PresenceChanged += p => OnPresence(session, p);
        client.OccupantChanged += o => OnOccupant(session, client, o, ct);
        client.RosterChanged += r => OnRosterItem(session, r);
        client.SubscriptionRequested += j => { session.Locked(s => s.Requests.Add(j)); SessionChanged?.Invoke(session); };
        client.RoomJoinFailed += (room, why, text) => OnJoinFailed(session, client, room, why, text, ct);
        client.InviteReceived += invite =>
        {
            session.Locked(s => s.InviteMap[invite.Room] = invite);
            SessionChanged?.Invoke(session);
        };
        client.RoomSubjectChanged += (room, subject) => session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(room, out var r)) return;
            r.Subject = subject;
            r.HistoryDone = true;
        });

        var reading = client.RunAsync(ct);
        var saving  = SaveInboxAsync(session, inbox.Reader, ct);

        try
        {
            await SetUpSessionAsync(session, client, ct);
            SetState(session, JabberState.Connected, JabberProblem.None, "");
            await reading;
        }
        finally
        {
            inbox.Writer.TryComplete();
            await saving;
        }
    }

    /// <summary>
    /// What was missed while away, if the server keeps an archive. The server's stored messages come out on
    /// connecting anyway, so this mostly finds what those miss: what was said while another client was connected,
    /// and what this account said from elsewhere.
    ///
    /// <para>⚠️ Only for an account with something saved already. A new one starts from now: years of an archive
    /// arriving as unread messages would be no use to anyone.</para>
    /// </summary>
    private async Task CatchUpFromArchiveAsync(JabberSession session, XmppClient client, CancellationToken ct)
    {
        try
        {
            session.HasArchive = await client.HasArchiveAsync(ct);
            SessionChanged?.Invoke(session);
            if (session.HasArchive != true) return;

            string? last;
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
                last = await db.JabberMessages.AsNoTracking()
                    .Where(m => m.AccountId == session.Account.Id && !m.IsRoom)
                    .OrderByDescending(m => m.Id).Select(m => m.SentAt).FirstOrDefaultAsync(ct);

            if (last is null || !DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var since)) return;

            await client.QueryArchiveAsync(since - ArchiveOverlap, ct);
        }
        catch (OperationCanceledException) { /* disconnected meanwhile: the next connection asks again */ }
        catch (TimeoutException) { /* an archive too slow to wait for is not worth failing over */ }
        catch (XmppException) { /* not having it costs only what the server's stored messages already cover */ }
        catch (IOException) { /* the connection went: the next one asks again */ }
        catch (Exception e) { errors.Log(nameof(JabberService), "catch up from the archive", e); }
    }

    /// <summary>Asked for a little before, since neither clock is to be trusted. What overlaps is recognised and dropped.</summary>
    private static readonly TimeSpan ArchiveOverlap = TimeSpan.FromMinutes(5);

    private const string StoredDate = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>From what is saved, so it reads the same on any client, whichever of them holds the connection. Reads every message: for now and then, not for each tick.</summary>
    public async Task<IReadOnlyList<JabberConversationActivity>> ActivityAsync(CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1).UtcDateTime.ToString(StoredDate, CultureInfo.InvariantCulture);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.JabberMessages.AsNoTracking()
            .GroupBy(m => new { m.AccountId, m.Conversation, m.IsRoom })
            .Select(g => new JabberConversationActivity(g.Key.AccountId, g.Key.Conversation, g.Key.IsRoom,
                                                        g.Max(m => m.SentAt)!, g.Sum(m => m.SentAt.CompareTo(since) >= 0 ? 1 : 0)))
            .ToListAsync(ct);
    }

    /// <summary>
    /// A message as it comes off the connection: what to save now, or null if a room's catch-up is keeping it back.
    ///
    /// <para>⚠️ A room's date is judged here, on the thread reading the connection, and not when the message is saved.
    /// The subject that ends a room's replay takes effect at once while saving waits its turn, so judged at saving, a
    /// replay could be taken for live talk: unread, dated now, and setting off alerts for what was said long ago.</para>
    /// </summary>
    private static XmppMessage? Arriving(JabberSession session, XmppMessage message)
    {
        if (message.Type != "groupchat" || message.ArchiveId.Length > 0) return message;

        return session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(message.From.Bare, out var room)) return message;

            var judged = message with { Sent = room.HistoryDone ? null : message.Sent, DateJudged = true };
            if (room.Held is not { } held) return judged;

            held.Add(judged);
            return null;
        });
    }

    /// <summary>Lets go what a room's catch-up kept back, in order, ahead of anything that arrives from now on.</summary>
    private static void ReleaseHeld(JabberSession session, JabberRoomState room) => session.Locked(s =>
    {
        if (room.Held is { } held && s.Inbox is { } inbox)
            foreach (var message in held) inbox.TryWrite(message);
        room.Held = null;
    });

    private async Task SetArchiveResumeAsync(int roomId, DateTimeOffset? from, CancellationToken ct)
    {
        var text = from?.UtcDateTime.ToString(StoredDate, CultureInfo.InvariantCulture) ?? "";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JabberRooms.Where(r => r.Id == roomId).ExecuteUpdateAsync(u => u.SetProperty(r => r.ArchiveResumeFrom, text), ct);
    }

    /// <summary>
    /// Where catching up on a room starts: where an unfinished one stopped, or else the last message saved. Null for
    /// a room with nothing saved, which starts from what it replays on joining. No further back than history is kept:
    /// what was unread would never be purged, and a long absence should not bring back what would be deleted anyway.
    /// </summary>
    private async Task<DateTimeOffset?> CatchUpStartAsync(JabberRoom room, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var resume = await db.JabberRooms.AsNoTracking().Where(r => r.Id == room.Id).Select(r => r.ArchiveResumeFrom).FirstOrDefaultAsync(ct);
        var from = DateTimeOffset.TryParse(resume, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var stopped)
            ? stopped
            : await LastSeenInAsync(room.AccountId, room.RoomJid, ct);
        if (from is null) return null;

        var saved = await db.AppPreferences.AsNoTracking().Where(p => p.Key == HistoryDaysPref).Select(p => p.Value).FirstOrDefaultAsync(ct);
        var days  = int.TryParse(saved, out var d) ? d : DefaultHistoryDays;
        var oldest = DateTimeOffset.UtcNow.AddDays(-days);

        return days > 0 && from < oldest ? oldest : from;
    }

    /// <summary>
    /// What was said in a room while away, if it keeps an archive. Runs once this client is in the room, since some
    /// rooms only show their archive to those who are.
    /// </summary>
    private async Task CatchUpRoomAsync(JabberSession session, XmppClient client, JabberRoomState room, CancellationToken ct)
    {
        try
        {
            // A room that keeps its description from outsiders may tell someone inside.
            if (room.HasArchive != true && await client.RoomHasArchiveAsync(room.Room.RoomJid, ct))
                session.Locked(_ =>
                {
                    room.HasArchive = true;
                    if (room.CatchUpFrom is not null) room.Held ??= [];
                });

            if (room.HasArchive != true || room.CatchUpFrom is not { } from) return;

            await SetArchiveResumeAsync(room.Room.Id, from, ct);
            var result = await client.QueryArchiveAsync(from - ArchiveOverlap, ct, room.Room.RoomJid);

            // Cut short by its size, it carries on from there next time. Otherwise there is nothing to carry on from.
            await SetArchiveResumeAsync(room.Room.Id, result.Complete ? null : result.Newest ?? from, ct);
        }
        catch (XmppException e) when (e.Failure == XmppFailure.Protocol)
        {
            // Asking again would only be refused again, and what it replayed on joining is about to be saved.
            session.Locked(_ => room.ArchiveRefused = true);
            await SetArchiveResumeAsync(room.Room.Id, null, CancellationToken.None);
        }
        catch (OperationCanceledException) { /* disconnected meanwhile: the next connection carries on from the mark */ }
        catch (TimeoutException) { /* too slow to wait for: the next connection carries on from the mark */ }
        catch (XmppException) { /* the connection went: the next one carries on from the mark */ }
        catch (IOException) { /* the same */ }
        catch (Exception e) { errors.Log(nameof(JabberService), "catch up on a room", e); }
        finally
        {
            ReleaseHeld(session, room);
            SessionChanged?.Invoke(session);
        }
    }

    private async Task FindUploadServiceAsync(JabberSession session, XmppClient client, CancellationToken ct)
    {
        try
        {
            session.UploadService = await client.FindUploadServiceAsync(ct);
            SessionChanged?.Invoke(session);
        }
        catch (Exception e) when (e is not (OperationCanceledException or XmppException or TimeoutException or IOException))
        {
            errors.Log(nameof(JabberService), "look for file upload", e);
        }
        catch (Exception) { session.UploadService = null; }
    }

    /// <summary>Everything a fresh connection needs, in the order that loses nothing.</summary>
    private async Task SetUpSessionAsync(JabberSession session, XmppClient client, CancellationToken ct)
    {
        session.HasArchive = null;

        // Contacts first, so presence arriving next has someone to attach to.
        var roster = await client.GetRosterAsync(ct);
        session.Locked(s =>
        {
            s.ContactMap.Clear();
            foreach (var item in roster) ApplyRosterItem(s, item);
        });

        // Carbons before presence: the server's stored messages come out as soon as presence goes in.
        await client.EnableCarbonsAsync(ct);
        await client.SetPresenceAsync(session.Show, session.Status, ct);

        try { session.ConferenceServices = await client.ConferenceServicesAsync(ct); }
        catch (XmppException e) when (e.Failure == XmppFailure.Protocol) { session.ConferenceServices = []; }

        // Not waited on: being connected should not hang on a long archive, or on a service that never answers.
        _ = FindUploadServiceAsync(session, client, ct);
        _ = CatchUpFromArchiveAsync(session, client, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Rooms saved on the server join the account's list the first time they are seen.
        // ⚠️ By address, once each: a list saved with a room in it twice would otherwise break the login every time.
        foreach (var bookmark in (await client.GetBookmarksAsync(ct)).DistinctBy(b => b.Jid))
        {
            if (await db.JabberRooms.AnyAsync(r => r.AccountId == session.Account.Id && r.RoomJid == bookmark.Jid, ct)) continue;
            db.JabberRooms.Add(new JabberRoom
            {
                AccountId = session.Account.Id, RoomJid = bookmark.Jid, Name = bookmark.Name,
                Nickname = bookmark.Nick, AutoJoin = bookmark.AutoJoin,
            });
        }
        await db.SaveChangesAsync(ct);

        var rooms = await db.JabberRooms.AsNoTracking().Where(r => r.AccountId == session.Account.Id).ToListAsync(ct);
        session.Locked(s =>
        {
            s.RoomMap.Clear();
            foreach (var room in rooms.Where(r => !r.Left)) s.RoomMap[room.RoomJid] = new JabberRoomState(room);
        });

        // Together: each first asks whether its room keeps an archive, and one slow room should not hold up the rest.
        await Task.WhenAll(rooms.Where(r => r.AutoJoin && !r.Left).Select(room => JoinCoreAsync(session, client, room, ct)));
    }

    private async Task JoinCoreAsync(JabberSession session, XmppClient client, JabberRoom room, CancellationToken ct)
    {
        var nick = NicknameFor(session.Account, room);
        var password = await Task.Run(() => AppConfig.GetServicePassword(RoomPasswordReference(room.Id)) ?? "", ct);
        var since = await CatchUpStartAsync(room, ct);
        var hasArchive = await client.RoomHasArchiveAsync(room.RoomJid, ct);

        session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(room.RoomJid, out var r)) return;

            r.Nick = nick; r.Failure = null; r.FailureText = ""; r.HistoryDone = false;
            r.HasArchive = hasArchive; r.ArchiveRefused = false;
            r.CatchUpFrom = since;

            // ⚠️ From before the join goes out, so that nothing it replays is saved ahead of what is older.
            r.Held = hasArchive && since is not null ? [] : null;
        });

        await client.JoinRoomAsync(room.RoomJid, nick, password, since, ct);
    }

    public static string RoomPasswordReference(int roomId) => $"jabber-room-{roomId}";

    /// <summary>When the last message saved for a room was sent, so a rejoin asks only for what is new.</summary>
    private async Task<DateTimeOffset?> LastSeenInAsync(int accountId, string room, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var last = await db.JabberMessages.AsNoTracking()
            .Where(m => m.AccountId == accountId && m.Conversation == room)
            .OrderByDescending(m => m.Id).Select(m => m.SentAt).FirstOrDefaultAsync(ct);

        return DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null;
    }

    /// <summary>The nickname for a room: its own, then the account's, then the login's name.</summary>
    public static string NicknameFor(JabberAccount account, JabberRoom? room)
    {
        if (room is { Nickname.Length: > 0 }) return room.Nickname;
        if (account.Nickname.Length > 0) return account.Nickname;
        return XmppAddress.Parse(account.Address).Local;
    }

    /// <summary>
    /// The resource this app connects as. ⚠️ The same on every PC, as only the lease holder connects. A holder that died
    /// leaves its session open on the server for a while, holding its nickname in every room, and a new connection under
    /// the same resource replaces it rather than joining as Name_ alongside.
    /// </summary>
    public const string WorkerResource = "EVEConsole";

    private void MarkDisconnected(JabberSession session)
    {
        session.Locked(s =>
        {
            foreach (var c in s.ContactMap.Values) { c.Resources.Clear(); c.Show = XmppShow.Offline; }
            foreach (var r in s.RoomMap.Values) { r.Joined = false; r.Occupants.Clear(); }
        });
    }

    private void SetState(JabberSession session, JabberState state, JabberProblem problem, string detail)
    {
        session.State   = state;
        session.Problem = problem;
        session.Detail  = detail;
        SessionChanged?.Invoke(session);
    }

    // ── What the server tells us ──────────────────────────────────────────────

    private void OnRosterItem(JabberSession session, XmppRosterItem item)
    {
        session.Locked(s =>
        {
            if (item.Subscription == "remove") s.ContactMap.Remove(item.Jid);
            else ApplyRosterItem(s, item);
        });
        SessionChanged?.Invoke(session);
    }

    private static void ApplyRosterItem(JabberSession s, XmppRosterItem item)
    {
        if (!s.ContactMap.TryGetValue(item.Jid, out var contact)) s.ContactMap[item.Jid] = contact = new JabberContact(item.Jid);
        contact.Name = item.Name;
        contact.Subscription = item.Subscription;
    }

    private void OnPresence(JabberSession session, XmppPresence presence)
    {
        session.Locked(s =>
        {
            if (!s.ContactMap.TryGetValue(presence.From.Bare, out var contact)) return;

            if (presence.Show == XmppShow.Offline) contact.Resources.Remove(presence.From.Resource);
            else contact.Resources[presence.From.Resource] = presence.Show;

            contact.Show = contact.Resources.Count == 0 ? XmppShow.Offline : contact.Resources.Values.Min(Rank);
            if (presence.Show != XmppShow.Offline) contact.Status = presence.Status;
        });
        SessionChanged?.Invoke(session);

        // Whichever of their clients is most available is how they appear.
        static XmppShow Rank(XmppShow x) => x;
    }

    private void OnOccupant(JabberSession session, XmppClient client, XmppOccupant occupant, CancellationToken ct)
    {
        JabberRoomState? entered = null;
        session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(occupant.Room, out var room)) return;

            if (occupant.Left) room.Occupants.Remove(occupant.Nick);
            else room.Occupants[occupant.Nick] = occupant;

            if (occupant.Self)
            {
                if (!occupant.Left && !room.Joined) entered = room;
                room.Joined = !occupant.Left;
                if (!occupant.Left) room.Nick = occupant.Nick;
            }
        });

        if (entered is not null) _ = CatchUpRoomAsync(session, client, entered, ct);
        SessionChanged?.Invoke(session);
    }

    private void OnJoinFailed(JabberSession session, XmppClient client, string room, XmppRoomFailure why, string text, CancellationToken ct)
    {
        // A nickname in use is usually this person's other client, or a ghost: one retry with a mark on it.
        JabberRoom? retry = null;
        session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(room, out var state)) return;
            if (why == XmppRoomFailure.NickInUse && !state.Nick.EndsWith('_')) retry = state.Room;
            else
            {
                state.Failure = why; state.FailureText = text;
                ReleaseHeld(session, state);
            }
        });

        if (retry is { } r)
        {
            var nick = NicknameFor(session.Account, r) + "_";
            session.Locked(s => { if (s.RoomMap.TryGetValue(room, out var state)) { state.Nick = nick; state.HistoryDone = false; } });
            _ = client.JoinRoomAsync(room, nick, "", null, ct)
                      .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        SessionChanged?.Invoke(session);
    }

    private const int SaveAttempts = 5;

    /// <summary>A date this far ahead is a wrong clock or a lie: left in, it would stop catch-up asking for anything.</summary>
    private static readonly TimeSpan MostAhead = TimeSpan.FromMinutes(5);

    private async Task SaveInboxAsync(JabberSession session, ChannelReader<XmppMessage> inbox, CancellationToken ct)
    {
        try
        {
            await foreach (var message in inbox.ReadAllAsync(CancellationToken.None))
            {
                // ⚠️ A message is the whole point, and a busy database usually clears in moments, so it gets several
                // tries. This worker must also never end: the inbox would fill up behind it unsaved.
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await StoreAsync(session, message, CancellationToken.None);
                        break;
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        if (attempt >= SaveAttempts)
                        {
                            errors.Log(nameof(JabberService), "save a message", e);
                            break;
                        }
                        await Task.Delay(TimeSpan.FromSeconds(attempt * 2), CancellationToken.None);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* going */ }
    }

    /// <summary>
    /// Whether an address is one person in a room. ⚠️ By the service as well as the rooms kept: the archive is read
    /// before the rooms are, and can hold words with someone in a room since left. Filed under the room's own address,
    /// those would all run together as if the room were one person.
    /// </summary>
    public static bool IsSomeoneInARoom(XmppAddress address, bool roomIsKept, IReadOnlyList<string> conferenceServices) =>
        address.Resource.Length > 0 && (roomIsKept || conferenceServices.Contains(address.Domain, StringComparer.OrdinalIgnoreCase));

    /// <summary>Saves a message unless it has been seen, and says so. Returns what was saved, or null.</summary>
    public async Task<JabberStoredMessage?> StoreAsync(JabberSession session, XmppMessage message, CancellationToken ct = default)
    {
        var known = session.Locked(s => s.RoomMap.GetValueOrDefault(message.From.Bare));
        var room  = message.Type == "groupchat" ? known : null;

        // ⚠️ Room talk from a room this account is not in has no conversation to go to and could never be read.
        if (message.Type == "groupchat" && known is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (room is { HistoryDone: true } && message is { Sent: not null, ArchiveId.Length: 0, DateJudged: false }) message = message with { Sent = null };
        if (message.Sent > now + MostAhead) message = message with { Sent = now };

        // What is said privately to someone in a room stays with them, not with the room, either way round.
        var other = message.FromThisAccount ? message.To : message.From;
        var privateInRoom = message.Type != "groupchat" && session.Locked(s => IsSomeoneInARoom(other, s.RoomMap.ContainsKey(other.Bare), s.ConferenceServices));

        var row = ToStored(session.Account, room?.Nick, room?.Room, message, now, privateInRoom: privateInRoom || known is not null && room is null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (message.IsCorrection)
        {
            if (await ApplyCorrectionAsync(db, row, message.ReplaceId, ct)) return null;

            // Replayed history can hold corrections of things long since changed again. They are not new messages.
            if (message.Sent is not null) return null;
        }

        // Something a room replayed may have just come out of its archive too, known there by other ids.
        if (message.ArchiveId.Length > 0 || room is not null && message.Sent is not null)
        {
            if (await AlreadyHaveArchivedAsync(db, row, message, ct)) return null;

            // The server's own id where there is one, since the room's replay and live messages carry that one.
            if (message.ArchiveId.Length > 0)
                row.DedupeKey = $"s:{row.Conversation}:{(message.ServerId.Length > 0 ? message.ServerId : message.ArchiveId)}";
        }

        if (await db.JabberMessages.AnyAsync(m => m.AccountId == row.AccountId && m.DedupeKey == row.DedupeKey, ct)) return null;

        db.JabberMessages.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // The owner's other machine saved it first, which is fine. Anything else is a real failure.
            await using var check = await dbFactory.CreateDbContextAsync(ct);
            if (await check.JabberMessages.AnyAsync(m => m.AccountId == row.AccountId && m.DedupeKey == row.DedupeKey, ct)) return null;
            throw;
        }

        MessageStored?.Invoke(row);
        if (!row.IsRead) UnreadChanged?.Invoke();
        if (room is { Room.RoomType: JabberRoomType.Intel }) await FeedIntelAsync(db, room.Room, row, ct);

        // ⚠️ Not one sound per mention for a whole absence: a room's catch-up shows as unread, and that is enough.
        var sound = row.IsAlert && !row.IsRead && !(row.IsRoom && message.ArchiveId.Length > 0);
        if (sound) AlertArrived?.Invoke(row);
        TellMessageSaved(row.Id, sound);
        return row;
    }

    /// <summary>Hands a room's message to the intel parser. A failure here is the parser's loss, not the message's: that is saved.</summary>
    private async Task FeedIntelAsync(AppDbContext db, JabberRoom room, JabberStoredMessage row, CancellationToken ct)
    {
        var lines = IntelRooms.ToChatLines(room, row);
        if (lines.Count == 0) return;

        try
        {
            db.ChatMessages.AddRange(lines);
            await db.SaveChangesAsync(ct);
            IntelLinesSaved?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            errors.Log(nameof(JabberService), "saving a room's intel", e);
        }
    }

    /// <summary>
    /// Whether something out of the archive was already delivered. ⚠️ A delivered message is known by whatever it
    /// carried then, so all of those are tried. One with no id at all, as some bots send, can only be known by
    /// who said what and about when.
    /// </summary>
    private static async Task<bool> AlreadyHaveArchivedAsync(AppDbContext db, JabberStoredMessage row, XmppMessage message, CancellationToken ct)
    {
        var plain = message with { ServerId = "", ArchiveId = "" };

        List<string> keys = [$"s:{row.Conversation}:{message.ArchiveId}"];
        if (message.ServerId.Length > 0) keys.Add($"s:{row.Conversation}:{message.ServerId}");
        if (message.Id.Length > 0) keys.Add(DedupeKey(row.Conversation, row.Sender, plain));

        if (await db.JabberMessages.AnyAsync(m => m.AccountId == row.AccountId && keys.Contains(m.DedupeKey), ct)) return true;
        if (message.Id.Length > 0)
            return await db.JabberMessages.AnyAsync(m => m.AccountId == row.AccountId && m.Conversation == row.Conversation
                                                      && m.MessageId == message.Id && m.Sender == row.Sender && m.Body == row.Body, ct);

        var recent = await db.JabberMessages.AsNoTracking()
            .Where(m => m.AccountId == row.AccountId && m.Conversation == row.Conversation && m.Sender == row.Sender && m.Body == row.Body)
            .OrderByDescending(m => m.Id).Take(20).Select(m => m.SentAt).ToListAsync(ct);

        return recent.Any(at => DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                                && DateTimeOffset.TryParse(row.SentAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mine)
                                && (when - mine).Duration() < ArchiveSameMoment);
    }

    private static readonly TimeSpan ArchiveSameMoment = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A correction changes that person's latest message in the conversation, which is the only one the
    /// protocol lets them correct. False when nothing of theirs is saved, and it stands as a new message.
    /// </summary>
    private async Task<bool> ApplyCorrectionAsync(AppDbContext db, JabberStoredMessage row, string replaces, CancellationToken ct)
    {
        var last = await db.JabberMessages
            .Where(m => m.AccountId == row.AccountId && m.Conversation == row.Conversation)
            .OrderByDescending(m => m.Id).Take(50)
            .FirstOrDefaultAsync(m => m.Sender == row.Sender, ct);

        // ⚠️ The one it names. In a room a nickname can pass to someone else, who could otherwise rewrite the last
        // thing its old owner said, and replayed history would overwrite whatever came since.
        if (last is null || replaces.Length == 0 || last.MessageId != replaces) return false;

        last.Body = row.Body;
        await db.SaveChangesAsync(ct);
        ConversationEdited?.Invoke(row.AccountId, row.Conversation);
        Tell($"edit|{row.AccountId}|{row.Conversation}");
        return true;
    }

    /// <summary>
    /// A message as a row: whose conversation it belongs to, and whether it calls for attention.
    ///
    /// <para>⚠️ History a room replays on joining is already read. It is old talk, and a reconnect would
    /// otherwise set off a flood of alerts for what scrolled by while nobody cared. A direct message
    /// that waited on the server is the opposite: it is exactly what the person came back for.</para>
    /// </summary>
    /// <param name="privateInRoom">From someone in a room, to this person alone. It is a conversation
    /// with that nickname, kept apart from the room's own, with the same reply address it came from.</param>
    public static JabberStoredMessage ToStored(
        JabberAccount account, string? roomNick, JabberRoom? room, XmppMessage message, DateTimeOffset now, bool privateInRoom = false)
    {
        var isRoom = message.Type == "groupchat";

        // A carbon of what this account said elsewhere belongs to the person it was said to.
        var conversation = privateInRoom ? (message.FromThisAccount ? message.To.Full : message.From.Full)
                         : isRoom || !message.FromThisAccount ? message.From.Bare : message.To.Bare;
        var sender       = isRoom || privateInRoom ? message.From.Resource : message.From.Bare;

        var outgoing = message.FromThisAccount
                    || isRoom && roomNick is { Length: > 0 } && string.Equals(sender, roomNick, StringComparison.Ordinal);

        // ⚠️ What a room's archive gives is not this: it is asked only for what came after the last message seen, so
        // all of it really was missed, and someone named in it should be told.
        var replayed = isRoom && message.Sent is not null && message.ArchiveId.Length == 0;
        var alert    = !outgoing && !replayed && (!isRoom || RoomAlerts(account, roomNick, room, message.Body));

        return new JabberStoredMessage
        {
            AccountId    = account.Id,
            Conversation = conversation,
            IsRoom       = isRoom,
            Sender       = sender,
            Body         = message.Body,
            SentAt       = (message.Sent ?? now).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            IsOutgoing   = outgoing,
            IsRead       = outgoing || replayed,
            IsAlert      = alert,
            IsMention    = isRoom && !outgoing && NamesThem(account, roomNick, message.Body),
            DedupeKey    = DedupeKey(conversation, sender, message),
            MessageId    = message.Id,
        };
    }

    /// <summary>Whether a room message says this person's name on this server: the one they go by in the room, or their usual one.</summary>
    public static bool NamesThem(JabberAccount account, string? roomNick, string body) =>
        roomNick is { Length: > 0 } && ContainsWord(body, roomNick)
        || account.Nickname.Length > 0 && ContainsWord(body, account.Nickname);

    /// <summary>Whether a room message should call for attention, by that room's setting.</summary>
    public static bool RoomAlerts(JabberAccount account, string? nick, JabberRoom? room, string body)
    {
        var mode = room?.AlertMode ?? JabberRoomAlert.Mentions;
        if (mode == JabberRoomAlert.All) return true;
        if (mode == JabberRoomAlert.None) return false;

        if (nick is { Length: > 0 } && ContainsWord(body, nick)) return true;

        return account.AlertWords.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                      .Any(word => ContainsWord(body, word));
    }

    /// <summary>The word on its own, in any case: "Al" should not fire on every "already".</summary>
    public static bool ContainsWord(string text, string word)
    {
        var at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end    = at + word.Length;
            var after  = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
            at = end;
        }
        return false;
    }

    /// <summary>
    /// What makes two arrivals the same message. The server's own id where it gave one, then the sender's,
    /// then the words and the time, since a stored message has nothing else to go on.
    /// </summary>
    public static string DedupeKey(string conversation, string sender, XmppMessage message)
    {
        if (message.ServerId.Length > 0) return $"s:{conversation}:{message.ServerId}";

        // ⚠️ With the words as well: a bot whose counter started again after a restart reuses its ids, and its next
        // broadcast is a new message however familiar the number. The same id and the same words is a real repeat.
        if (message.Id.Length > 0)
        {
            var words = SHA256.HashData(Encoding.UTF8.GetBytes(message.Body));
            return $"i:{conversation}:{sender}:{message.Id}:{Convert.ToHexString(words)[..16]}";
        }

        var stamp = message.Sent?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? Guid.NewGuid().ToString("N");
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes($"{conversation}|{sender}|{message.Body}|{stamp}"));
        return "h:" + Convert.ToHexString(hash)[..32];
    }

    // ── Asking the server for things ──────────────────────────────────────────

    /// <summary>Sends a message. Rooms echo a message back, so only a direct one is saved here.</summary>
    public async Task SendAsync(JabberSession session, string conversation, bool isRoom, string body, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            await AskWorkerAsync(session.Account.Id, "send", new CommandArgs(conversation, isRoom, body), ct);
            return;
        }

        var client = session.Client ?? throw new XmppException(XmppFailure.Network, "Not connected.");
        var text   = XmppClient.Clean(body);
        var id     = await client.SendMessageAsync(conversation, isRoom ? "groupchat" : "chat", text, ct);

        if (isRoom) return;

        await StoreAsync(session, new XmppMessage(client.Self, XmppAddress.Parse(conversation), "chat", text, "", id, null, "", true), ct);
    }

    private static readonly HttpClient Uploads = new() { Timeout = TimeSpan.FromMinutes(15) };

    /// <summary>
    /// ⚠️ Encrypted only, as the protocol requires: the file's address is the only thing protecting it. Plain http
    /// is allowed to this machine alone, for testing.
    /// </summary>
    public static bool IsSafeUploadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>Puts a file on the server, and sends its address to the conversation.</summary>
    public async Task SendFileAsync(JabberSession session, string conversation, bool isRoom, Stream content, string fileName, long size, string contentType, CancellationToken ct = default)
    {
        // ⚠️ The worker asks the server for where to put it and sends its address, but the file goes up from here: it
        // is on this PC, and the worker may be on another.
        var slot = ActsHere
            ? await UploadSlotAsync(session, fileName, size, contentType, ct)
            : JsonSerializer.Deserialize<XmppUploadSlot>(await AskWorkerAsync(session.Account.Id, "slot", new CommandArgs(Name: fileName, Size: size, ContentType: contentType), ct))
              ?? throw new XmppException(XmppFailure.Protocol, "The server did not say where to put the file.");

        if (!IsSafeUploadUrl(slot.PutUrl) || !IsSafeUploadUrl(slot.GetUrl))
            throw new XmppException(XmppFailure.Protocol, "The server offered an unencrypted address for the file.");

        using var body = new StreamContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        body.Headers.ContentLength = size;

        using var request = new HttpRequestMessage(HttpMethod.Put, slot.PutUrl) { Content = body };
        foreach (var header in slot.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);

        using var response = await Uploads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (ActsHere) await SendUrlAsync(session, conversation, isRoom, slot.GetUrl, ct);
        else await AskWorkerAsync(session.Account.Id, "sendurl", new CommandArgs(conversation, isRoom, slot.GetUrl), ct);
    }

    private static async Task<XmppUploadSlot> UploadSlotAsync(JabberSession session, string fileName, long size, string contentType, CancellationToken ct)
    {
        var client  = session.Client ?? throw new XmppException(XmppFailure.Network, "Not connected.");
        var service = session.UploadService ?? throw new XmppException(XmppFailure.Protocol, "This server does not take files.");
        return await client.RequestUploadSlotAsync(service.Jid, XmppClient.Clean(Path.GetFileName(fileName)), size, contentType, ct);
    }

    /// <summary>Sends the address of a file now on the server.</summary>
    private async Task SendUrlAsync(JabberSession session, string conversation, bool isRoom, string url, CancellationToken ct)
    {
        var client = session.Client ?? throw new XmppException(XmppFailure.Network, "Not connected.");
        var id = await client.SendMessageAsync(conversation, isRoom ? "groupchat" : "chat", url, ct, url);
        if (!isRoom)
            await StoreAsync(session, new XmppMessage(client.Self, XmppAddress.Parse(conversation), "chat", url, "", id, null, "", true), ct);
    }

    /// <summary>Tells someone this account is typing to them, if their client has shown it can use that.</summary>
    public async Task SendTypingAsync(JabberSession session, string conversation, bool typing, CancellationToken ct = default)
    {
        // A signal alone, as nothing depends on it and a row each key press would be silly.
        if (!ActsHere)
        {
            Tell($"typeout|{session.Account.Id}|{(typing ? 1 : 0)}|{conversation}");
            return;
        }

        if (session.Client is { } client && session.Locked(s => s.SendsTyping.Contains(conversation)))
            await client.SendTypingAsync(conversation, typing, ct);
    }

    public async Task SetPresenceAsync(JabberSession session, XmppShow show, string status, CancellationToken ct = default)
    {
        session.Show   = show;
        session.Status = status;
        session.Account.SavedShow   = (int)show;
        session.Account.SavedStatus = status;

        // Saved before it is sent: the choice is the person's, whether or not the connection lasts to deliver it.
        var id = session.Account.Id;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.JabberAccounts.Where(a => a.Id == id)
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.SavedShow, (int)show).SetProperty(a => a.SavedStatus, status), ct);

        if (!ActsHere)
        {
            await PostToWorkerAsync(id, "presence", new CommandArgs(Show: show, Status: status), ct);
            SessionChanged?.Invoke(session);
            return;
        }

        if (session.Client is { } client)
        {
            await client.SetPresenceAsync(show, status, ct);

            var rooms = session.Locked(s => s.RoomMap.Values.Where(r => r.Joined).Select(r => (r.Room.RoomJid, r.Nick)).ToList());
            foreach (var (room, nick) in rooms) await client.SetRoomPresenceAsync(room, nick, ct);
        }
        SessionChanged?.Invoke(session);
    }

    /// <summary>Adds a room to the account, and joins it.</summary>
    public async Task<JabberRoom> JoinRoomAsync(JabberSession session, string roomJid, string name, string nickname, string password, CancellationToken ct = default, int? roomType = null)
    {
        var bare = XmppAddress.Parse(roomJid).Bare;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var room = await db.JabberRooms.FirstOrDefaultAsync(r => r.AccountId == session.Account.Id && r.RoomJid == bare, ct);
        if (room is null)
        {
            room = new JabberRoom { AccountId = session.Account.Id, RoomJid = bare, Name = name, Nickname = nickname, RoomType = roomType ?? JabberRoomType.General };
            db.JabberRooms.Add(room);
        }
        else
        {
            if (nickname.Length > 0) room.Nickname = nickname;
            if (roomType is { } type) room.RoomType = type;
            room.AutoJoin = true;
            room.Left = false;
        }
        await db.SaveChangesAsync(ct);

        if (password.Length > 0)
        {
            var id = room.Id;
            await Task.Run(() => AppConfig.SetServicePassword(RoomPasswordReference(id), password), ct);
        }

        session.Locked(s => s.RoomMap[bare] = new JabberRoomState(room));

        // ⚠️ The room and its password are already saved, so its password is never part of what is asked.
        if (ActsHere) await JoinSavedRoomAsync(session, bare, ct);
        else await AskWorkerAsync(session.Account.Id, "join", new CommandArgs(bare), ct);

        SessionChanged?.Invoke(session);
        return room;
    }

    private async Task JoinSavedRoomAsync(JabberSession session, string roomJid, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var id = session.Account.Id;
        var room = await db.JabberRooms.AsNoTracking().FirstOrDefaultAsync(r => r.AccountId == id && r.RoomJid == roomJid, ct);
        if (room is null) return;

        session.Locked(s => s.RoomMap[roomJid] = new JabberRoomState(room));
        if (session.Client is { } client) await JoinCoreAsync(session, client, room, ct);

        RoomTypesChanged();
        SessionChanged?.Invoke(session);
    }

    /// <summary>Leaves a room and stops rejoining it. Its saved messages stay until they age out.</summary>
    public async Task LeaveRoomAsync(JabberSession session, string roomJid, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            await AskWorkerAsync(session.Account.Id, "leave", new CommandArgs(roomJid), ct);
            session.Locked(s => s.RoomMap.Remove(roomJid));
            SessionChanged?.Invoke(session);
            return;
        }

        var state = session.Locked(s => { s.RoomMap.Remove(roomJid, out var r); return r; });
        if (state is not null && session.Client is { } client && state.Joined)
            await client.LeaveRoomAsync(roomJid, state.Nick, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // ⚠️ Kept, marked as left: a room saved in the server's bookmarks would otherwise be back on the next connect.
        var id = session.Account.Id;
        await db.JabberRooms.Where(r => r.AccountId == id && r.RoomJid == roomJid)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Left, true), ct);

        // Nothing is left to open them with, and an alert nobody can clear would stay for ever.
        var cleared = await db.JabberMessages.Where(m => m.AccountId == id && m.Conversation == roomJid && !m.IsRead)
                              .ExecuteUpdateAsync(u => u.SetProperty(m => m.IsRead, true), ct);
        if (cleared > 0)
        {
            UnreadChanged?.Invoke();
            Tell("unread");
        }

        RoomTypesChanged();
        SessionChanged?.Invoke(session);
    }

    /// <summary>Answers an invitation. A yes joins the room with whatever password came with it.</summary>
    public async Task AnswerInviteAsync(JabberSession session, string room, bool accept, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            await AskWorkerAsync(session.Account.Id, "invite", new CommandArgs(room, Accept: accept), ct);
            return;
        }

        var invite = session.Locked(s => { s.InviteMap.Remove(room, out var i); return i; });
        if (accept && invite is not null) await JoinRoomAsync(session, invite.Room, "", "", invite.Password, ct);
        SessionChanged?.Invoke(session);
    }

    public async Task SetRoomAlertAsync(JabberSession session, string roomJid, int mode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JabberRooms.Where(r => r.AccountId == session.Account.Id && r.RoomJid == roomJid)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.AlertMode, mode), ct);
        session.Locked(s => { if (s.RoomMap.TryGetValue(roomJid, out var r)) r.Room.AlertMode = mode; });
        if (!ActsHere) await PostToWorkerAsync(session.Account.Id, "room", new CommandArgs(roomJid), ct);
        SessionChanged?.Invoke(session);
    }

    /// <summary>Changes what a room is for. Only what it says from now on is affected, either way.</summary>
    public async Task SetRoomTypeAsync(JabberSession session, string roomJid, int roomType, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JabberRooms.Where(r => r.AccountId == session.Account.Id && r.RoomJid == roomJid)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.RoomType, roomType), ct);
        session.Locked(s => { if (s.RoomMap.TryGetValue(roomJid, out var r)) r.Room.RoomType = roomType; });
        if (ActsHere) RoomTypesChanged();
        else
        {
            IntelRoomsChanged?.Invoke();
            await PostToWorkerAsync(session.Account.Id, "room", new CommandArgs(roomJid), ct);
        }
        SessionChanged?.Invoke(session);
    }

    public Task AddContactAsync(JabberSession session, string jid, string name, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Account.Id, "addcontact", new CommandArgs(jid, Name: name), ct)
        : session.Client?.AddContactAsync(XmppAddress.Parse(jid).Bare, name, ct)
          ?? throw new XmppException(XmppFailure.Network, "Not connected.");

    public Task RemoveContactAsync(JabberSession session, string jid, CancellationToken ct = default) =>
        !ActsHere ? AskWorkerAsync(session.Account.Id, "removecontact", new CommandArgs(jid), ct)
        : session.Client?.RemoveContactAsync(jid, ct)
          ?? throw new XmppException(XmppFailure.Network, "Not connected.");

    public async Task AnswerRequestAsync(JabberSession session, string jid, bool accept, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            await AskWorkerAsync(session.Account.Id, "request", new CommandArgs(jid, Accept: accept), ct);
            return;
        }

        if (session.Client is { } client) await client.AnswerSubscriptionAsync(jid, accept, ct);
        session.Locked(s => s.Requests.Remove(jid));
        SessionChanged?.Invoke(session);
    }

    /// <summary>Every room kept, for every account, whether or not it is connected. ⚠️ From the database: an account's live list only fills on connecting.</summary>
    public async Task<List<JabberRoom>> SavedRoomsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.JabberRooms.AsNoTracking().Where(r => !r.Left).ToListAsync(ct);
    }

    /// <summary>What a room says about itself. Null when it will not say in reasonable time, so one silent room holds up nothing.</summary>
    public async Task<XmppRoomDetails?> RoomDetailsAsync(JabberSession session, string roomJid, CancellationToken ct = default)
    {
        if (!ActsHere)
        {
            try { return JsonSerializer.Deserialize<XmppRoomDetails>(await AskWorkerAsync(session.Account.Id, "details", new CommandArgs(roomJid), ct)); }
            catch (XmppException) { return null; }
        }

        if (session.Client is not { } client) return null;

        try { return await client.RoomDetailsAsync(roomJid, ct).WaitAsync(TimeSpan.FromSeconds(6), ct); }
        catch (Exception e) when (e is TimeoutException or XmppException) { return null; }
    }

    /// <summary>The rooms a conference service lists, for browsing.</summary>
    public async Task<IReadOnlyList<XmppRoomInfo>> BrowseRoomsAsync(JabberSession session, string service, CancellationToken ct = default)
    {
        if (!ActsHere)
            return JsonSerializer.Deserialize<List<XmppRoomInfo>>(await AskWorkerAsync(session.Account.Id, "browse", new CommandArgs(service), ct)) ?? [];

        return await (session.Client?.ItemsOfAsync(service, ct) ?? throw new XmppException(XmppFailure.Network, "Not connected."));
    }

    // ── History ───────────────────────────────────────────────────────────────

    public const string HistoryDaysPref = "jabber.history_days";
    public const int DefaultHistoryDays = 30;

    /// <summary>A page of a conversation, oldest first, ending before a message or at the newest.</summary>
    public async Task<List<JabberStoredMessage>> LoadConversationAsync(
        int accountId, string conversation, long beforeId = long.MaxValue, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.JabberMessages.AsNoTracking()
            .Where(m => m.AccountId == accountId && m.Conversation == conversation && m.Id < beforeId)
            .OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        page.Reverse();
        return page;
    }

    /// <summary>Every conversation of an account that has saved messages, newest first.</summary>
    public async Task<List<(string Conversation, bool IsRoom, long LastId)>> ConversationsAsync(int accountId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.JabberMessages.AsNoTracking().Where(m => m.AccountId == accountId)
            .GroupBy(m => new { m.Conversation, m.IsRoom })
            .Select(g => new { g.Key.Conversation, g.Key.IsRoom, Last = g.Max(m => m.Id) })
            .OrderByDescending(g => g.Last).ToListAsync(ct);
        return [.. rows.Select(r => (r.Conversation, r.IsRoom, r.Last))];
    }

    /// <summary>Every direct conversation across accounts, with how it stands, for the Messages window.</summary>
    public async Task<List<JabberDirectSummary>> DirectSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groups = await db.JabberMessages.AsNoTracking().Where(m => !m.IsRoom)
            .GroupBy(m => new { m.AccountId, m.Conversation })
            .Select(g => new { g.Key.AccountId, g.Key.Conversation, Last = g.Max(m => m.Id), Unread = g.Count(m => !m.IsRead), Alerts = g.Count(m => !m.IsRead && m.IsAlert) })
            .ToListAsync(ct);

        var ids  = groups.Select(g => g.Last).ToList();
        var last = await db.JabberMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

        return [.. groups.Select(g => new JabberDirectSummary(g.AccountId, g.Conversation, last[g.Last].Body, last[g.Last].SentAt, g.Unread, g.Alerts))];
    }

    public async Task<List<JabberUnread>> UnreadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.JabberMessages.AsNoTracking().Where(m => !m.IsRead)
            .GroupBy(m => new { m.AccountId, m.Conversation })
            .Select(g => new { g.Key.AccountId, g.Key.Conversation, Count = g.Count(), Alerts = g.Count(m => m.IsAlert), Mentions = g.Count(m => m.IsMention) })
            .ToListAsync(ct);
        return [.. rows.Select(r => new JabberUnread(r.AccountId, r.Conversation, r.Count, r.Alerts, r.Mentions))];
    }

    /// <summary>Messages waiting that call for attention: what the Overview's alert counts.</summary>
    public async Task<int> UnreadAlertsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.JabberMessages.AsNoTracking().CountAsync(m => !m.IsRead && m.IsAlert, ct);
    }

    public async Task MarkReadAsync(int accountId, string conversation, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.JabberMessages
            .Where(m => m.AccountId == accountId && m.Conversation == conversation && !m.IsRead)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.IsRead, true), ct);
        if (changed > 0)
        {
            UnreadChanged?.Invoke();
            Tell("unread");
        }
    }

    /// <summary>Deletes messages past the kept days. ⚠️ Never an unread one: age is no reason to lose what was not seen.</summary>
    public async Task PurgeOldAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var saved = await db.AppPreferences.AsNoTracking().Where(p => p.Key == HistoryDaysPref).Select(p => p.Value).FirstOrDefaultAsync(ct);
            var days  = int.TryParse(saved, out var d) ? d : DefaultHistoryDays;
            if (days <= 0) return;   // keep it all

            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            await db.JabberMessages.Where(m => m.IsRead && string.Compare(m.SentAt, cutoff) < 0).ExecuteDeleteAsync(ct);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            errors.Log(nameof(JabberService), "purge old messages", e);
        }
    }
}
