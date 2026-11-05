using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Jabber;

public enum JabberState { Offline, Connecting, Connected, Reconnecting, Failed }

/// <summary>What a stopped connection needs from the person, if anything.</summary>
public enum JabberProblem { None, Password, Certificate, NoEncryption, Replaced, Other }

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

    internal XmppClient? Client { get; set; }
    internal CancellationTokenSource? Run { get; set; }
    internal Task? Loop { get; set; }

    internal Dictionary<string, JabberContact> ContactMap { get; } = new(StringComparer.Ordinal);
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
public readonly record struct JabberUnread(int AccountId, string Conversation, int Count, int Alerts);

/// <summary>
/// The Jabber accounts: keeping each connected as asked, and saving what they say.
///
/// <para>⚠️ Desktop only. Started after the headless branch has returned, and it checks again: a worker
/// connecting as the owner would fight their real client for the same login.</para>
/// </summary>
public sealed class JabberService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors)
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

    /// <summary>Someone corrected a saved message: the conversation needs reading again.</summary>
    public event Action<int, string>? ConversationEdited;

    public static string PasswordReference(int accountId) => $"jabber-{accountId}";

    /// <summary>Loads the accounts and connects the ones set to connect on their own.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (AppRuntime.IsHeadless || AppRuntime.IsTray) return;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var account in await db.JabberAccounts.AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct))
        {
            var session = Track(account);
            if (account.AutoConnect) Connect(session);
        }

        await PurgeOldAsync(ct);
    }

    public async Task StopAsync()
    {
        JabberSession[] all;
        lock (_gate) all = [.. _sessions.Values];
        await Task.WhenAll(all.Select(DisconnectAsync));
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
            return _sessions[account.Id] = new JabberSession(account);
        }
    }

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
        SessionChanged?.Invoke(session);
        return session;
    }

    /// <summary>Forgets an account, its rooms and every message saved under it. Permanent.</summary>
    public async Task DeleteAccountAsync(int accountId, CancellationToken ct = default)
    {
        JabberSession? session;
        lock (_gate) _sessions.Remove(accountId, out session);
        if (session is not null) await DisconnectAsync(session);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JabberMessages.Where(m => m.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.JabberRooms.Where(r => r.AccountId == accountId).ExecuteDeleteAsync(ct);
        await db.JabberAccounts.Where(a => a.Id == accountId).ExecuteDeleteAsync(ct);

        await Task.Run(() => AppConfig.SetServicePassword(PasswordReference(accountId), null), ct);
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
        if (AppRuntime.IsHeadless || AppRuntime.IsTray) return;

        lock (_gate)
        {
            if (session.Loop is { IsCompleted: false }) return;

            session.Run  = new CancellationTokenSource();
            session.Loop = Task.Run(() => KeepConnectedAsync(session, session.Run.Token));
        }
    }

    public async Task DisconnectAsync(JabberSession session)
    {
        CancellationTokenSource? run;
        Task? loop;
        lock (_gate) { run = session.Run; loop = session.Loop; }

        if (run is not null) await run.CancelAsync();

        if (session.Client is { } client) await client.DisposeAsync();

        if (loop is not null)
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { /* it is told to stop, and will */ }

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
            catch (Exception e) when (e is XmppException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                session.Detail = e.Message;
            }
            finally
            {
                if (session.Client is { } client)
                {
                    session.Client = null;
                    await client.DisposeAsync();
                }
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
            throw new XmppException(XmppFailure.Login, "No password is saved on this machine.");

        var connection = new XmppConnection(new XmppLogin(
            account.Address, password, account.Host, account.Port,
            account.Resource.Length > 0 ? account.Resource : MachineResource(),
            account.TrustedCertificate, account.DirectTls));

        await connection.ConnectAsync(ct);

        var client = new XmppClient(connection);
        session.Client = client;

        // One worker per connection keeps messages in order, and the database off the reading thread.
        var inbox = Channel.CreateUnbounded<XmppMessage>(new UnboundedChannelOptions { SingleReader = true });
        client.MessageReceived += m => inbox.Writer.TryWrite(m);
        client.PresenceChanged += p => OnPresence(session, p);
        client.OccupantChanged += o => OnOccupant(session, o);
        client.RosterChanged += r => OnRosterItem(session, r);
        client.SubscriptionRequested += j => { session.Locked(s => s.Requests.Add(j)); SessionChanged?.Invoke(session); };
        client.RoomJoinFailed += (room, why, text) => OnJoinFailed(session, client, room, why, text, ct);
        client.InviteReceived += invite =>
        {
            session.Locked(s => s.InviteMap[invite.Room] = invite);
            SessionChanged?.Invoke(session);
        };
        client.RoomSubjectChanged += (room, subject) => session.Locked(s => { if (s.RoomMap.TryGetValue(room, out var r)) r.Subject = subject; });

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

    /// <summary>Everything a fresh connection needs, in the order that loses nothing.</summary>
    private async Task SetUpSessionAsync(JabberSession session, XmppClient client, CancellationToken ct)
    {
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

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Rooms saved on the server join the account's list the first time they are seen.
        foreach (var bookmark in await client.GetBookmarksAsync(ct))
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
            foreach (var room in rooms) s.RoomMap[room.RoomJid] = new JabberRoomState(room);
        });

        foreach (var room in rooms.Where(r => r.AutoJoin))
            await JoinCoreAsync(session, client, room, ct);
    }

    private async Task JoinCoreAsync(JabberSession session, XmppClient client, JabberRoom room, CancellationToken ct)
    {
        var nick = NicknameFor(session.Account, room);
        var password = await Task.Run(() => AppConfig.GetServicePassword(RoomPasswordReference(room.Id)) ?? "", ct);
        var since = await LastSeenInAsync(session.Account.Id, room.RoomJid, ct);

        session.Locked(s => { if (s.RoomMap.TryGetValue(room.RoomJid, out var r)) { r.Nick = nick; r.Failure = null; r.FailureText = ""; } });
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
    /// A resource of this machine's own. ⚠️ Fixed for the machine, so a reconnect takes the session's place,
    /// but different across machines: the same one on two would have the owner's clients replacing each other.
    /// </summary>
    public static string MachineResource() =>
        "EVEConsole-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)))[..8];

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

    private void OnOccupant(JabberSession session, XmppOccupant occupant)
    {
        session.Locked(s =>
        {
            if (!s.RoomMap.TryGetValue(occupant.Room, out var room)) return;

            if (occupant.Left) room.Occupants.Remove(occupant.Nick);
            else room.Occupants[occupant.Nick] = occupant;

            if (occupant.Self)
            {
                room.Joined = !occupant.Left;
                if (!occupant.Left) room.Nick = occupant.Nick;
            }
        });
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
            else { state.Failure = why; state.FailureText = text; }
        });

        if (retry is { } r)
        {
            var nick = NicknameFor(session.Account, r) + "_";
            session.Locked(s => { if (s.RoomMap.TryGetValue(room, out var state)) state.Nick = nick; });
            _ = client.JoinRoomAsync(room, nick, "", null, ct)
                      .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        SessionChanged?.Invoke(session);
    }

    private async Task SaveInboxAsync(JabberSession session, ChannelReader<XmppMessage> inbox, CancellationToken ct)
    {
        try
        {
            await foreach (var message in inbox.ReadAllAsync(CancellationToken.None))
            {
                try { await StoreAsync(session, message, CancellationToken.None); }
                catch (Exception e) when (e is DbUpdateException or InvalidOperationException or IOException)
                {
                    errors.Log(nameof(JabberService), "save a message", e);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* going */ }
    }

    /// <summary>Saves a message unless it has been seen, and says so. Returns what was saved, or null.</summary>
    public async Task<JabberStoredMessage?> StoreAsync(JabberSession session, XmppMessage message, CancellationToken ct = default)
    {
        var known = session.Locked(s => s.RoomMap.GetValueOrDefault(message.From.Bare));
        var room  = message.Type == "groupchat" ? known : null;

        var row = ToStored(session.Account, room?.Nick, room?.Room, message, DateTimeOffset.UtcNow, privateInRoom: known is not null && room is null);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (message.IsCorrection && await ApplyCorrectionAsync(db, row, ct)) return null;

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
        if (row.IsAlert && !row.IsRead) AlertArrived?.Invoke(row);
        return row;
    }

    /// <summary>
    /// A correction changes that person's latest message in the conversation, which is the only one the
    /// protocol lets them correct. False when nothing of theirs is saved, and it stands as a new message.
    /// </summary>
    private async Task<bool> ApplyCorrectionAsync(AppDbContext db, JabberStoredMessage row, CancellationToken ct)
    {
        var last = await db.JabberMessages
            .Where(m => m.AccountId == row.AccountId && m.Conversation == row.Conversation)
            .OrderByDescending(m => m.Id).Take(50)
            .FirstOrDefaultAsync(m => m.Sender == row.Sender, ct);
        if (last is null) return false;

        last.Body = row.Body;
        await db.SaveChangesAsync(ct);
        ConversationEdited?.Invoke(row.AccountId, row.Conversation);
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
        var conversation = privateInRoom ? message.From.Full
                         : isRoom || !message.FromThisAccount ? message.From.Bare : message.To.Bare;
        var sender       = isRoom || privateInRoom ? message.From.Resource : message.From.Bare;

        var outgoing = message.FromThisAccount
                    || isRoom && roomNick is { Length: > 0 } && string.Equals(sender, roomNick, StringComparison.Ordinal);

        var replayed = isRoom && message.Sent is not null;
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
            DedupeKey    = DedupeKey(conversation, sender, message),
        };
    }

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
        if (message.Id.Length > 0)       return $"i:{conversation}:{sender}:{message.Id}";

        var stamp = message.Sent?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? Guid.NewGuid().ToString("N");
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes($"{conversation}|{sender}|{message.Body}|{stamp}"));
        return "h:" + Convert.ToHexString(hash)[..32];
    }

    // ── Asking the server for things ──────────────────────────────────────────

    /// <summary>Sends a message. Rooms echo a message back, so only a direct one is saved here.</summary>
    public async Task SendAsync(JabberSession session, string conversation, bool isRoom, string body, CancellationToken ct = default)
    {
        var client = session.Client ?? throw new XmppException(XmppFailure.Network, "Not connected.");
        var text   = XmppClient.Clean(body);
        var id     = await client.SendMessageAsync(conversation, isRoom ? "groupchat" : "chat", text, ct);

        if (isRoom) return;

        await StoreAsync(session, new XmppMessage(client.Self, XmppAddress.Parse(conversation), "chat", text, "", id, null, "", true), ct);
    }

    public async Task SetPresenceAsync(JabberSession session, XmppShow show, string status, CancellationToken ct = default)
    {
        session.Show   = show;
        session.Status = status;
        if (session.Client is { } client) await client.SetPresenceAsync(show, status, ct);
        SessionChanged?.Invoke(session);
    }

    /// <summary>Adds a room to the account, and joins it. A password, if any, is held per machine.</summary>
    public async Task<JabberRoom> JoinRoomAsync(JabberSession session, string roomJid, string name, string nickname, string password, CancellationToken ct = default)
    {
        var bare = XmppAddress.Parse(roomJid).Bare;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var room = await db.JabberRooms.FirstOrDefaultAsync(r => r.AccountId == session.Account.Id && r.RoomJid == bare, ct);
        if (room is null)
        {
            room = new JabberRoom { AccountId = session.Account.Id, RoomJid = bare, Name = name, Nickname = nickname };
            db.JabberRooms.Add(room);
        }
        else
        {
            if (nickname.Length > 0) room.Nickname = nickname;
            room.AutoJoin = true;
        }
        await db.SaveChangesAsync(ct);

        if (password.Length > 0)
        {
            var id = room.Id;
            await Task.Run(() => AppConfig.SetServicePassword(RoomPasswordReference(id), password), ct);
        }

        session.Locked(s => s.RoomMap[bare] = new JabberRoomState(room));
        if (session.Client is { } client) await JoinCoreAsync(session, client, room, ct);

        SessionChanged?.Invoke(session);
        return room;
    }

    /// <summary>Leaves a room and stops rejoining it. Its saved messages stay until they age out.</summary>
    public async Task LeaveRoomAsync(JabberSession session, string roomJid, CancellationToken ct = default)
    {
        var state = session.Locked(s => { s.RoomMap.Remove(roomJid, out var r); return r; });
        if (state is not null && session.Client is { } client && state.Joined)
            await client.LeaveRoomAsync(roomJid, state.Nick, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.JabberRooms.Where(r => r.AccountId == session.Account.Id && r.RoomJid == roomJid).ExecuteDeleteAsync(ct);
        SessionChanged?.Invoke(session);
    }

    /// <summary>Answers an invitation. A yes joins the room with whatever password came with it.</summary>
    public async Task AnswerInviteAsync(JabberSession session, string room, bool accept, CancellationToken ct = default)
    {
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
        SessionChanged?.Invoke(session);
    }

    public Task AddContactAsync(JabberSession session, string jid, string name, CancellationToken ct = default) =>
        session.Client?.AddContactAsync(XmppAddress.Parse(jid).Bare, name, ct)
        ?? throw new XmppException(XmppFailure.Network, "Not connected.");

    public Task RemoveContactAsync(JabberSession session, string jid, CancellationToken ct = default) =>
        session.Client?.RemoveContactAsync(jid, ct)
        ?? throw new XmppException(XmppFailure.Network, "Not connected.");

    public async Task AnswerRequestAsync(JabberSession session, string jid, bool accept, CancellationToken ct = default)
    {
        if (session.Client is { } client) await client.AnswerSubscriptionAsync(jid, accept, ct);
        session.Locked(s => s.Requests.Remove(jid));
        SessionChanged?.Invoke(session);
    }

    /// <summary>The rooms a conference service lists, for browsing.</summary>
    public Task<IReadOnlyList<XmppRoomInfo>> BrowseRoomsAsync(JabberSession session, string service, CancellationToken ct = default) =>
        session.Client?.ItemsOfAsync(service, ct) ?? throw new XmppException(XmppFailure.Network, "Not connected.");

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

    public async Task<List<JabberUnread>> UnreadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.JabberMessages.AsNoTracking().Where(m => !m.IsRead)
            .GroupBy(m => new { m.AccountId, m.Conversation })
            .Select(g => new { g.Key.AccountId, g.Key.Conversation, Count = g.Count(), Alerts = g.Count(m => m.IsAlert) })
            .ToListAsync(ct);
        return [.. rows.Select(r => new JabberUnread(r.AccountId, r.Conversation, r.Count, r.Alerts))];
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
        if (changed > 0) UnreadChanged?.Invoke();
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
