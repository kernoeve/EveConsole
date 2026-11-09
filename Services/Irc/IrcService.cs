using System.Threading.Channels;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Irc;

public enum IrcState { Offline, Connecting, Connected, Reconnecting, Failed, WaitingForWorker }

public enum IrcProblem { None, Password, Certificate, NoEncryption, Refused, Nickname, Other }

/// <summary>Someone in a channel: their nickname, ranks there, and what the server says about them.</summary>
public sealed class IrcMember(string nick)
{
    public string Nick { get; set; } = nick;
    public string UserHost { get; set; } = "";
    public string Account { get; set; } = "";

    /// <summary>Their rank mode letters in this channel, highest first ("o", "ov"). Blank for none.</summary>
    public string Ranks { get; set; } = "";

    public bool Away { get; set; }
    public string AwayMessage { get; set; } = "";
}

/// <summary>How a channel stands on a connection right now. Nothing here is saved.</summary>
public sealed class IrcChannelState(IrcChannel channel)
{
    public IrcChannel Channel { get; set; } = channel;
    public bool Joined { get; set; }

    /// <summary>The channel's name as the server writes it.</summary>
    public string ServerName { get; set; } = channel.Name;

    public string Topic { get; set; } = "";
    public string TopicBy { get; set; } = "";

    /// <summary>Why the last join failed, or why this person was put out. Blank when nothing went wrong.</summary>
    public string Failure { get; set; } = "";

    /// <summary>By folded nickname.</summary>
    public Dictionary<string, IrcMember> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>A NAMES reply being gathered: it comes in several lines and replaces the list at its end.</summary>
    internal Dictionary<string, IrcMember>? NamesComing { get; set; }

    /// <summary>A catch-up from the server's history has been asked for since joining.</summary>
    internal bool HistoryAsked { get; set; }
}

/// <summary>Someone this connection has written with, and whether they are online, as far as the server says.</summary>
public sealed class IrcPerson(string nick)
{
    public string Nick { get; set; } = nick;
    public bool? Online { get; set; }
    public bool Away { get; set; }
    public string AwayMessage { get; set; } = "";
}

public sealed record IrcInvite(string Channel, string From);

/// <summary>One channel a server lists: its name, how many are in it, and its topic.</summary>
public sealed record IrcListEntry(string Channel, int People, string Topic);

/// <summary>One ban on a channel: who it keeps out (a mask such as *!*@host), and who set it.</summary>
public sealed record IrcBan(string Mask, string SetBy);

/// <summary>One server login, connected or not.</summary>
public sealed class IrcSession(IrcServer server)
{
    private readonly object _gate = new();

    public IrcServer Server { get; set; } = server;
    public IrcState State { get; internal set; } = IrcState.Offline;
    public IrcProblem Problem { get; internal set; }
    public string Detail { get; internal set; } = "";

    public string CertificateFingerprint { get; internal set; } = "";
    public string CertificateSubject { get; internal set; } = "";

    public DateTimeOffset? NextAttempt { get; internal set; }

    /// <summary>The nickname the server knows this connection by. Blank when not connected.</summary>
    public string Nick { get; internal set; } = "";

    /// <summary>The network's name, as it says it.</summary>
    public string Network { get; internal set; } = "";

    public bool IsAway { get; internal set; }

    /// <summary>The server keeps history to ask for (CHATHISTORY), so what was missed comes back on reconnecting.</summary>
    public bool HasHistory { get; internal set; }

    /// <summary>The server passes tags on, so typing, replies and reactions work.</summary>
    public bool HasTags { get; internal set; }

    public bool IsEncrypted { get; internal set; }

    internal IrcClient? Client { get; set; }
    internal CancellationTokenSource? Run { get; set; }
    internal Task? Loop { get; set; }
    internal ChannelWriter<IrcMessage>? Inbox { get; set; }

    internal Dictionary<string, IrcChannelState> ChannelMap { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, IrcPerson> PeopleMap { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, IrcInvite> InviteMap { get; } = new(StringComparer.Ordinal);

    internal List<IrcListEntry>? ListComing { get; set; }
    internal TaskCompletionSource<IReadOnlyList<IrcListEntry>>? ListDone { get; set; }

    /// <summary>
    /// Channels left before the server had said they were joined, by folded name. ⚠️ Their JOIN may already be on its
    /// way, and arriving it would keep the channel again: it is answered with a PART instead.
    /// </summary>
    internal HashSet<string> Leaving { get; } = new(StringComparer.Ordinal);

    /// <summary>A channel's ban list being read (367 lines until 368), by folded channel.</summary>
    internal Dictionary<string, (List<IrcBan> Bans, TaskCompletionSource<IReadOnlyList<IrcBan>> Done)> BansComing { get; } = new(StringComparer.Ordinal);

    /// <summary>The last thing the server refused, for the screen to say (not being an operator, no such nick).</summary>
    public string LastRefusal { get; internal set; } = "";

    public T Locked<T>(Func<IrcSession, T> read) { lock (_gate) return read(this); }
    public void Locked(Action<IrcSession> act) { lock (_gate) act(this); }

    public IReadOnlyList<IrcChannelState> Channels => Locked(s => (IReadOnlyList<IrcChannelState>)[.. s.ChannelMap.Values]);

    /// <summary>A kept channel's state, by name in any case. Null when it is not kept.</summary>
    public IrcChannelState? ChannelOf(string name) => Locked(s => s.ChannelMap.GetValueOrDefault(s.Fold(name)));
    public IReadOnlyList<IrcInvite> Invites => Locked(s => (IReadOnlyList<IrcInvite>)[.. s.InviteMap.Values]);
    public IReadOnlyList<IrcPerson> People => Locked(s => (IReadOnlyList<IrcPerson>)[.. s.PeopleMap.Values]);

    /// <summary>How the server compares names, or the usual rules before it has said.</summary>
    /// <remarks>
    /// ⚠️ Always the usual rules (rfc1459), whatever the server says, for everything kept by name: what is saved, the
    /// channels and people here, and what the screens look up. Folding the server's way when connected and the usual
    /// way when not made a channel with [ ] in its name lose its history and unread while offline on a server that
    /// compares names otherwise. Two names only those rules tell apart are rarer than that.
    /// </remarks>
    public string Fold(string name) => IrcSupport.Fold(name, "rfc1459");

    /// <summary>What the server said about itself, as the connection holds it or as the worker last wrote it.</summary>
    public IrcSupport Support => Client?.Support ?? ShownSupport ?? DefaultSupport;

    /// <summary>On a client that is not the worker: the server's channel marks and ranks, as the worker wrote them.</summary>
    internal IrcSupport? ShownSupport { get; set; }

    private static readonly IrcSupport DefaultSupport = new();

    public bool IsChannelName(string name) => Support.IsChannel(name);

    public string DisplayName => Server.Name.Length > 0 ? Server.Name : Server.Host;
}

public readonly record struct IrcUnread(int ServerId, string Conversation, int Count, int Alerts, int Mentions);

public sealed record IrcDirectSummary(int ServerId, string Conversation, string PeerName, string LastText, string LastAt, int Unread, int Alerts);

/// <summary>
/// IRC: one connection per saved server, kept up while it is wanted; channels and direct messages saved to the
/// database, with unread counts, mentions and alerts as Jabber has them.
/// </summary>
/// <remarks>
/// <para>Runs under the worker lease, as Jabber does (see IrcRelay): whichever process holds it keeps the connections,
/// so what is said is saved while every window is closed, and every client shows the same. That is also the one
/// connection per nickname IRC wants.</para>
/// <para>What was said while nothing held the lease comes back where the server keeps history (CHATHISTORY); a server
/// without history has no way to give it, as with any IRC client.</para>
/// </remarks>
public sealed partial class IrcService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors, ClientSignals? signals = null)
{
    private static readonly int[] BackoffSeconds = [5, 10, 20, 40, 60, 120, 300];
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);

    public const string AvatarServiceName = "irc";

    private readonly object _gate = new();
    private readonly Dictionary<int, IrcSession> _sessions = [];

    /// <summary>A connection's state, its channels or its people changed.</summary>
    public event Action<IrcSession>? SessionChanged;

    /// <summary>A message was saved, arriving or sent.</summary>
    public event Action<IrcStoredMessage>? MessageStored;

    /// <summary>A message that calls for attention arrived live (not from history).</summary>
    public event Action<IrcStoredMessage>? AlertArrived;

    public event Action? UnreadChanged;

    /// <summary>Someone started or stopped typing: server, conversation, their nickname, and whether.</summary>
    public event Action<int, string, string, bool>? TypingChanged;

    public event Action<int, string>? ReactionsChanged;

    /// <summary>A line from an intel channel was saved, for the intel reader.</summary>
    public event Action? IntelLinesSaved;

    public event Action? IntelRoomsChanged;

    public static string PasswordReference(int serverId) => $"irc-{serverId}";
    public static string ChannelKeyReference(int channelId) => $"irc-channel-{channelId}";

    public IReadOnlyList<IrcSession> Sessions { get { lock (_gate) return [.. _sessions.Values.OrderBy(s => s.Server.Id)]; } }

    public IrcSession? SessionOf(int serverId) { lock (_gate) return _sessions.GetValueOrDefault(serverId); }

    private IrcSession Track(IrcServer server)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(server.Id, out var have)) { have.Server = server; return have; }
            return _sessions[server.Id] = new IrcSession(server);
        }
    }

    /// <summary>Lists the saved servers. Connecting is the worker's (<see cref="BecomeWorkerAsync"/>).</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await LoadServersAsync(ct);
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

    // ── Servers ───────────────────────────────────────────────────────────────

    /// <summary>Saves a server. A null password keeps the saved one; blank removes it.</summary>
    public async Task<IrcSession> SaveServerAsync(IrcServer server, string? password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (server.Id == 0) db.IrcServers.Add(server);
        else db.IrcServers.Update(server);
        await db.SaveChangesAsync(ct);

        if (password is not null)
            await Task.Run(() => AppConfig.SetServicePassword(PasswordReference(server.Id), password.Length > 0 ? password : null), ct);

        var session = Track(server);
        Tell("servers");
        SessionChanged?.Invoke(session);
        return session;
    }

    public async Task DeleteServerAsync(int serverId, CancellationToken ct = default)
    {
        IrcSession? session;
        lock (_gate) _sessions.Remove(serverId, out session);
        if (session is not null && ActsHere) await DisconnectHereAsync(session);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channelIds = await db.IrcChannels.Where(c => c.ServerId == serverId).Select(c => c.Id).ToListAsync(ct);
        await db.IrcMessages.Where(m => m.ServerId == serverId).ExecuteDeleteAsync(ct);
        await db.IrcReactions.Where(r => r.ServerId == serverId).ExecuteDeleteAsync(ct);
        await db.IrcChannels.Where(c => c.ServerId == serverId).ExecuteDeleteAsync(ct);
        await db.IrcServers.Where(s => s.Id == serverId).ExecuteDeleteAsync(ct);

        await Task.Run(() =>
        {
            AppConfig.SetServicePassword(PasswordReference(serverId), null);
            foreach (var id in channelIds) AppConfig.SetServicePassword(ChannelKeyReference(id), null);
        }, ct);

        Tell("servers");
        Tell("unread");
        UnreadChanged?.Invoke();
        RoomTypesChanged();
    }

    /// <summary>Accepts the certificate the server showed last, and tries again with it.</summary>
    public async Task TrustCertificateAsync(IrcSession session, CancellationToken ct = default)
    {
        if (session.CertificateFingerprint.Length == 0) return;
        session.Server.TrustedCertificate = session.CertificateFingerprint;
        await SaveServerAsync(session.Server, null, ct);
        await ReconnectAsync(session, ct);
    }

    // ── Connecting ────────────────────────────────────────────────────────────

    /// <summary>Asks for this server to be connected, and to stay connected until told otherwise.</summary>
    public void Connect(IrcSession session)
    {
        if (!ActsHere)
        {
            PostToWorker(session.Server.Id, "connect", new CommandArgs());
            return;
        }

        lock (_gate)
        {
            if (session.Loop is { IsCompleted: false }) return;
            session.Run = new CancellationTokenSource();
            var token = session.Run.Token;
            session.Loop = Task.Run(() => KeepConnectedAsync(session, token));
        }
    }

    public Task DisconnectAsync(IrcSession session) =>
        ActsHere ? DisconnectHereAsync(session) : PostToWorkerAsync(session.Server.Id, "disconnect", new CommandArgs());

    /// <summary>Connects again, to take a change to the server's settings. One after the other, wherever it runs.</summary>
    public async Task ReconnectAsync(IrcSession session, CancellationToken ct = default)
    {
        if (!ActsHere) { await PostToWorkerAsync(session.Server.Id, "reconnect", new CommandArgs(), ct); return; }
        await DisconnectHereAsync(session);
        Connect(session);
    }

    private async Task DisconnectHereAsync(IrcSession session)
    {
        CancellationTokenSource? run;
        Task? loop;
        lock (_gate) { run = session.Run; loop = session.Loop; }

        // ⚠️ Nothing here may throw: the Disconnect button and closing the app wait on it.
        try
        {
            if (session.Client is { } client) await client.QuitAsync(IrcClient.ClientVersion);
            if (run is not null) await run.CancelAsync();
            if (session.Client is { } c) await c.DisposeAsync();
            if (loop is not null) await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or ObjectDisposedException or IrcException) { }

        SetState(session, IrcState.Offline, IrcProblem.None, "");
    }

    private void SetState(IrcSession session, IrcState state, IrcProblem problem, string detail)
    {
        session.State = state;
        session.Problem = problem;
        session.Detail = detail;
        if (state != IrcState.Connected)
            session.Locked(s =>
            {
                foreach (var c in s.ChannelMap.Values) { c.Joined = false; c.Members.Clear(); c.NamesComing = null; c.HistoryAsked = false; }
                foreach (var p in s.PeopleMap.Values) p.Online = null;
            });
        SessionChanged?.Invoke(session);
    }

    private async Task KeepConnectedAsync(IrcSession session, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            SetState(session, attempt == 0 ? IrcState.Connecting : IrcState.Reconnecting, IrcProblem.None, "");

            try
            {
                await RunOnceAsync(session, ct);
                if (ct.IsCancellationRequested) break;
            }
            catch (IrcException e) when (!ct.IsCancellationRequested)
            {
                switch (e.Failure)
                {
                    case IrcFailure.Certificate:
                        session.CertificateFingerprint = e.CertificateFingerprint ?? "";
                        session.CertificateSubject = e.CertificateSubject ?? "";
                        SetState(session, IrcState.Failed, IrcProblem.Certificate, e.Message);
                        return;
                    case IrcFailure.Login:
                        SetState(session, IrcState.Failed, IrcProblem.Password, e.Message);
                        return;
                    case IrcFailure.Nickname:
                        SetState(session, IrcState.Failed, IrcProblem.Nickname, e.Message);
                        return;
                    case IrcFailure.Refused:
                        // A ban or a refusal says the same next time. Encryption missing too.
                        SetState(session, IrcState.Failed, e.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase) ? IrcProblem.NoEncryption : IrcProblem.Refused, e.Message);
                        return;
                    default:
                        session.Detail = e.Message;
                        break;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not OperationCanceledException)
            {
                session.Detail = e.Message;
                errors.Log(nameof(IrcService), "keep connected", e);
            }
            finally
            {
                if (session.Client is { } client) { session.Client = null; await client.DisposeAsync(); }
                session.Inbox?.TryComplete();
                session.Inbox = null;
                session.Nick = "";
            }

            if (ct.IsCancellationRequested) break;
            if (DateTimeOffset.UtcNow - started > StableAfter) attempt = 0;

            var wait = TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);
            attempt++;
            session.NextAttempt = DateTimeOffset.UtcNow + wait;
            SetState(session, IrcState.Reconnecting, IrcProblem.None, session.Detail);

            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
            finally { session.NextAttempt = null; }
        }
    }

    private async Task RunOnceAsync(IrcSession session, CancellationToken ct)
    {
        var server = session.Server;
        var kind = (IrcLoginKind)server.LoginKind;
        var password = "";
        if (kind != IrcLoginKind.None)
        {
            password = await Task.Run(() => AppConfig.GetServicePassword(PasswordReference(server.Id)), ct) ?? "";
            if (password.Length == 0)
                throw new IrcException(IrcFailure.Login, await Task.Run(() => AppConfig.HasServicePassword(PasswordReference(server.Id)), ct)
                    ? "The saved password cannot be opened, which is what happens after the database password changes. Type it again."
                    : "No password is saved.");
        }

        var client = new IrcClient(new IrcLogin(
            server.Host.Trim(), server.Port, server.Tls, server.Nickname.Trim(), server.Username, server.RealName,
            kind, server.Account, password, server.TrustedCertificate));
        session.Client = client;

        // ⚠️ Bounded: the reading waits while the lines are handled, so a fast server, or a long history, cannot make
        // the lines waiting grow without end.
        var inbox = Channel.CreateBounded<IrcMessage>(new BoundedChannelOptions(MostWaiting) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        session.Inbox = inbox.Writer;
        client.Deliver = m => inbox.Writer.WriteAsync(m, ct);

        await client.ConnectAsync(ct);

        session.Nick = client.Nick;
        session.Network = client.Support.Network;
        session.IsEncrypted = client.IsEncrypted;
        session.HasHistory = client.HasChatHistory;
        session.HasTags = client.HasTags;
        SetState(session, IrcState.Connected, IrcProblem.None, "");

        // One reader per connection keeps lines in order, and the database off the reading thread. ⚠️ Stopped with the
        // connection and waited for: lines of an old connection handled after a new one starts would mark channels
        // joined, or list people, on a connection that has not joined them.
        using var workerStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var worker = Task.Run(() => ProcessInboxAsync(session, client, inbox.Reader, workerStop.Token), workerStop.Token);

        // ⚠️ Reading starts now, alongside what follows logging in: joining many channels at the sending pace takes a
        // while, and a server whose PING goes unread meanwhile drops the connection.
        var reading = Task.Run(() => client.RunAsync(ct), ct);
        try
        {
            try { await AfterLoginAsync(session, client, ct); }
            catch (Exception e) when (e is IrcException or InvalidOperationException) { session.Detail = e.Message; }
            throw new IrcException(IrcFailure.Network, await reading);
        }
        finally
        {
            inbox.Writer.TryComplete();
            await workerStop.CancelAsync();
            try { await worker; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Most lines read and not yet handled: reading waits beyond this.</summary>
    private const int MostWaiting = 10_000;

    /// <summary>What follows logging in: away as it was left, the network's own commands, the channels, and catching up.</summary>
    private async Task AfterLoginAsync(IrcSession session, IrcClient client, CancellationToken ct)
    {
        var server = session.Server;
        if (server.AwayMessage.Length > 0)
        {
            await client.AwayAsync(server.AwayMessage, ct);
            session.IsAway = true;
        }

        // ⚠️ A saved command that looks like it carries a password goes only over encryption, or to this machine, as the
        // login's own password does: "/msg NickServ IDENTIFY ..." saved here would otherwise cross the network readable.
        var safe = client.IsEncrypted || IrcClient.IsThisMachine(server.Host.Trim());
        foreach (var line in server.OnConnect.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!safe && CarriesSecret(line)) { session.Detail = "A command once connected was not sent: it looks like a password, and the connection is not encrypted."; continue; }
            if (CommandLine(line) is { } m) await client.SendRawAsync(m, ct);
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channels = await db.IrcChannels.AsNoTracking().Where(c => c.ServerId == server.Id && !c.Left).ToListAsync(ct);
        session.Locked(s =>
        {
            foreach (var c in channels)
            {
                var key = s.Fold(c.Name);
                if (s.ChannelMap.TryGetValue(key, out var have)) have.Channel = c;
                else s.ChannelMap[key] = new IrcChannelState(c);
            }
        });

        foreach (var c in channels.Where(c => c.AutoJoin))
        {
            // Left while the ones before it were being joined: the sending is paced, so this takes a while.
            if (session.ChannelOf(c.Name) is null) continue;
            var key = await Task.Run(() => AppConfig.GetServicePassword(ChannelKeyReference(c.Id)), ct) ?? "";
            await client.JoinAsync(c.Name, key, ct);
        }

        // People written with: watched for coming and going, where the server can.
        var peers = await db.IrcMessages.AsNoTracking().Where(m => m.ServerId == server.Id && !m.IsChannel)
                            .Select(m => m.PeerName).Distinct().Take(Math.Max(0, client.Support.Monitor)).ToListAsync(ct);
        session.Locked(s => { foreach (var p in peers.Where(p => p.Length > 0)) s.PeopleMap.TryAdd(s.Fold(p), new IrcPerson(p)); });
        await client.MonitorAsync(peers, ct);

        if (client.HasChatHistory)
        {
            var since = await db.IrcMessages.AsNoTracking().Where(m => m.ServerId == server.Id && !m.IsChannel)
                                .OrderByDescending(m => m.Id).Select(m => m.SentAt).FirstOrDefaultAsync(ct);
            var from = since is { Length: > 0 } && DateTimeOffset.TryParse(since, out var at) ? at.AddMinutes(-1) : DateTimeOffset.UtcNow.AddDays(-7);
            await client.HistoryTargetsAsync(from, ct);
        }
    }

    private static bool CarriesSecret(string line) =>
        new[] { "identify", "pass", "login", "auth", "register", "ghost", "recover" }
            .Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A line as a person types it in a client, /command and all, as the IRC line it stands for. Null for nothing to send.
    /// The leading slash is optional here: these are the network's own instructions, saved per server.
    /// </summary>
    public static IrcMessage? CommandLine(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '/') text = text[1..];

        var space = text.IndexOf(' ');
        var verb = (space < 0 ? text : text[..space]).ToUpperInvariant();
        var rest = space < 0 ? "" : text[(space + 1)..].Trim();

        static (string, string) Two(string s)
        {
            var i = s.IndexOf(' ');
            return i < 0 ? (s, "") : (s[..i], s[(i + 1)..]);
        }

        switch (verb)
        {
            case "MSG" or "PRIVMSG" or "QUERY":
                var (to, words) = Two(rest);
                return to.Length > 0 && words.Length > 0 ? new IrcMessage("PRIVMSG", to, words) : null;
            case "NOTICE":
                var (nTo, nWords) = Two(rest);
                return nTo.Length > 0 && nWords.Length > 0 ? new IrcMessage("NOTICE", nTo, nWords) : null;
            case "QUOTE" or "RAW":
                return IrcMessage.Parse(rest);
            case "":
                return null;
            default:
                // Anything else goes as written: MODE, JOIN, NICKSERV IDENTIFY..., which servers take as commands.
                return IrcMessage.Parse(text);
        }
    }
}
