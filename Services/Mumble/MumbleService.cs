using System.Globalization;
using System.Text;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Mumble;

public enum MumbleState { Offline, Connecting, Connected, Reconnecting, Failed }

public enum MumbleProblem { None, Certificate, Password, Username, Removed, Other }

/// <summary>What pressing a server's keys does.</summary>
public enum MumbleTalk { None, Channel, Shout }

/// <summary>A line of a server's text chat, in memory for as long as the app runs.</summary>
/// <summary>How one saved conversation stands.</summary>
public sealed record MumbleDirectSummary(int ServerId, string PeerKey, string PeerName, string LastText, string LastAt, int Unread);

/// <summary>A line of channel chat, which lasts the session. Private messages go to the Messages window.</summary>
public sealed record MumbleChatLine(DateTimeOffset When, string Sender, string Text, bool IsOutgoing)
{
    /// <summary>The channel it was said in.</summary>
    public string Channel { get; init; } = "";

    /// <summary>It went to that channel and the ones under it.</summary>
    public bool IsTree { get; init; }
}

/// <summary>Somebody talking right now, for the overlay.</summary>
public sealed record MumbleTalker(int ServerId, string Server, uint Session, string Name, bool IsSelf, MumbleTalk Mode);

/// <summary>One server's connection, kept up for as long as someone wants it.</summary>
public sealed class MumbleSession(MumbleServer server)
{
    public MumbleServer Server { get; set; } = server;
    public MumbleState  State  { get; internal set; }
    public MumbleProblem Problem { get; internal set; }
    public string Detail { get; internal set; } = "";

    public string CertificateFingerprint { get; internal set; } = "";
    public string CertificateSubject     { get; internal set; } = "";
    public DateTimeOffset? NextAttempt   { get; internal set; }

    public MumbleClient? Client { get; internal set; }

    internal CancellationTokenSource? Run { get; set; }
    internal Task? Loop { get; set; }
    internal Dictionary<string, double> Volumes { get; set; } = [];
    internal bool MicFailed { get; set; }
    internal Task? VoiceSending { get; set; }
    internal GlobalHotkeyService? WhisperHook { get; set; }
    internal GlobalHotkeyService? ShoutHook { get; set; }
    internal MumbleEncoder? Encoder { get; set; }

    /// <summary>What is being said right now, if anything, and what its last frame must be.</summary>
    internal volatile int Talking;

    public MumbleTalk TalkingNow => (MumbleTalk)Talking;

    private readonly List<MumbleChatLine> _chat = [];

    public IReadOnlyList<MumbleChatLine> Chat { get { lock (_chat) return [.. _chat]; } }

    internal void AddChat(MumbleChatLine line)
    {
        lock (_chat)
        {
            _chat.Add(line);
            if (_chat.Count > MumbleService.MaxChatLines) _chat.RemoveAt(0);
        }
    }

    public double VolumeOf(MumbleUser user) => Volumes.GetValueOrDefault(MumbleService.UserKey(user), 1.0);
}

/// <summary>
/// The Mumble servers: keeping each connected as asked, routing the microphone to whichever server's key is
/// held, and everyone being heard to the speakers.
///
/// <para>⚠️ Desktop only, for the same reason as Jabber: a worker logging in as the owner would fight
/// their real client for the same seat, and has no one to listen.</para>
/// </summary>
public sealed class MumbleService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors)
{
    public const int MaxChatLines = 500;
    public const string InputDevicePref = "mumble.input_device";
    public const string OutputDevicePref = "mumble.output_device";
    public const string OutputVolumePref = "mumble.output_volume";
    public const string MicGainPref = "mumble.mic_gain";
    public const string AutoLevelPref = "mumble.auto_level";

    // Plain talk needs no saved target: the server sends it to the channel and everything linked to it, which is
    // what the standard client's Push to Talk does, and Shout is that. Whisper has to name the channel to stop there.
    private const int PlainTalk = 0;
    private const int ChannelOnlyTarget = 1;

    /// <summary>How long the microphone stays open after the last key lets go, so a quick re-press loses nothing.</summary>
    private static readonly TimeSpan MicLinger = TimeSpan.FromSeconds(5);

    private static readonly int[] BackoffSeconds = [5, 10, 30, 60, 120, 300];
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<int, MumbleSession> _sessions = [];
    private readonly object _gate = new();
    private readonly List<short> _micBuffer = new(MumbleEncoder.FrameSamples * 2);
    private readonly Channel<VoiceWork> _frames = Channel.CreateBounded<VoiceWork>(new BoundedChannelOptions(50)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>
    /// Key presses, in the order they happened. ⚠️ Never handled on the keyboard hook's own thread: Windows gives it a
    /// few hundred milliseconds, opening a microphone can take longer, and a hook that runs late misses the release
    /// or is removed without a word, leaving someone transmitting.
    /// </summary>
    private readonly Channel<Action> _keyEvents = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _keyWorker;

    /// <summary>A frame for everyone talking, or the last one for someone who just stopped.</summary>
    private readonly record struct VoiceWork(short[]? Frame, MumbleSession? EndOf, MumbleTalk EndMode);

    /// <summary>What the protocol lets a message be, past which no real person's text goes.</summary>
    private const int MostChatChars = 20_000;

    private CancellationTokenSource? _micClose;
    private Task? _sender;

    public MumbleMixer Mixer { get; } = new();
    public MumbleAudioDevices? Devices { get; private set; }

    private string _inputDevice = "", _outputDevice = "";
    private double _micGain = 1.0;

    /// <summary>The microphone by name, blank for the system's. Takes effect from the next time it opens.</summary>
    public string InputDevice
    {
        get => _inputDevice;
        set { _inputDevice = value; if (Devices is { } d) { d.InputDevice = value; d.StopInput(); } }
    }

    /// <summary>The speakers by name, blank for the system's. Switches at once if anything is connected.</summary>
    public string OutputDevice
    {
        get => _outputDevice;
        set
        {
            _outputDevice = value;
            if (Devices is not { } d) return;
            d.OutputDevice = value;
            d.StopOutput();
            if (Sessions.Any(s => s.State == MumbleState.Connected)) d.StartOutput();
        }
    }

    public double OutputVolume { get => Mixer.Volume; set => Mixer.Volume = value; }

    public double MicrophoneGain
    {
        get => _micGain;
        set { _micGain = value; if (Devices is { } d) d.MicrophoneGain = value; }
    }

    /// <summary>Levels this person's voice before it is sent, as the standard client does.</summary>
    public bool AutoLevel { get; set; } = true;

    private readonly MumbleVoiceLeveler _leveler = new();

    public event Action<MumbleSession>? SessionChanged;
    public event Action<MumbleSession, MumbleChatLine>? ChatReceived;

    /// <summary>Who is talking changed. On a timer thread.</summary>
    public event Action? TalkersChanged;

    public static string PasswordReference(int id) => $"mumble-{id}";
    public static string CertificateReference(int id) => $"mumble-cert-{id}";

    /// <summary>A person's lasting identity on a server: their certificate if they have one.</summary>
    public static string UserKey(MumbleUser user) => user.Hash.Length > 0 ? user.Hash : $"name:{user.Name}";

    /// <summary>Loads the servers and connects the ones set to connect on their own.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (AppRuntime.IsHeadless || AppRuntime.IsTray) return;

        Devices = new MumbleAudioDevices(Mixer) { InputDevice = _inputDevice, OutputDevice = _outputDevice, MicrophoneGain = _micGain };
        Devices.Captured += OnCaptured;
        _sender = Task.Run(SendFramesAsync, CancellationToken.None);
        _keyWorker = Task.Run(HandleKeysAsync, CancellationToken.None);
        _chatSaver ??= Task.Run(SaveChatAsync, CancellationToken.None);
        StartTalkerWatch();

        await PurgeOldChatAsync(ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var server in await db.MumbleServers.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct))
        {
            var session = Track(server);
            if (server.AutoConnect) Connect(session);
        }
    }

    // ── Private messages, which outlast the app ───────────────────────────────

    private const string StoredDate = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    private const int SaveAttempts = 5;

    /// <summary>Waiting to be saved, in order, so that a busy database never holds up the connection's reader.</summary>
    private readonly Channel<MumbleDirectMessage> _chatSaves = Channel.CreateUnbounded<MumbleDirectMessage>();
    private Task? _chatSaver;   // ⚠️ Not SingleReader: that kind of channel cannot say how much is waiting, which shutting down asks.

    /// <summary>A private message was saved, sent or received.</summary>
    public event Action<MumbleDirectMessage>? DirectStored;

    public event Action? DirectUnreadChanged;

    private void Record(MumbleSession session, MumbleChatLine line)
    {
        session.AddChat(line);
        ChatReceived?.Invoke(session, line);
    }

    private void RecordDirect(int serverId, MumbleUser peer, string sender, string body, bool outgoing) =>
        _chatSaves.Writer.TryWrite(new MumbleDirectMessage
        {
            ServerId = serverId, PeerKey = UserKey(peer), PeerName = peer.Name, Sender = sender, Body = body,
            SentAt = DateTimeOffset.UtcNow.UtcDateTime.ToString(StoredDate, CultureInfo.InvariantCulture),
            IsOutgoing = outgoing, IsRead = outgoing, IsAlert = !outgoing,
        });

    private async Task SaveChatAsync()
    {
        await foreach (var row in _chatSaves.Reader.ReadAllAsync())
        {
            // ⚠️ A busy database usually clears in moments, so it gets several tries. This must never end, or nothing
            // said afterwards would be kept.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync();
                    row.Id = 0;
                    db.MumbleDirectMessages.Add(row);
                    await db.SaveChangesAsync();

                    DirectStored?.Invoke(row);
                    if (!row.IsRead) DirectUnreadChanged?.Invoke();
                    break;
                }
                catch (Exception e)
                {
                    if (attempt >= SaveAttempts)
                    {
                        errors.Log(nameof(MumbleService), "save a private message", e);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }
    }

    /// <summary>Someone on a server right now, by what a conversation with them is filed under. Null if they are not.</summary>
    public MumbleUser? PersonOf(int serverId, string peerKey) =>
        SessionOf(serverId)?.Client?.Users.FirstOrDefault(u => UserKey(u) == peerKey);

    /// <summary>Every saved conversation, with how it stands.</summary>
    public async Task<List<MumbleDirectSummary>> DirectSummariesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var groups = await db.MumbleDirectMessages.AsNoTracking().Where(m => m.PeerKey != "")
            .GroupBy(m => new { m.ServerId, m.PeerKey })
            .Select(g => new { g.Key.ServerId, g.Key.PeerKey, Last = g.Max(m => m.Id), Unread = g.Count(m => !m.IsRead) })
            .ToListAsync(ct);

        var ids  = groups.Select(g => g.Last).ToList();
        var last = await db.MumbleDirectMessages.AsNoTracking().Where(m => ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);

        return [.. groups.Select(g => new MumbleDirectSummary(g.ServerId, g.PeerKey, last[g.Last].PeerName, last[g.Last].Body, last[g.Last].SentAt, g.Unread))];
    }

    public async Task<List<MumbleDirectMessage>> LoadDirectAsync(int serverId, string peerKey, long beforeId = long.MaxValue, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.MumbleDirectMessages.AsNoTracking()
            .Where(m => m.ServerId == serverId && m.PeerKey == peerKey && m.Id < beforeId)
            .OrderByDescending(m => m.Id).Take(limit).ToListAsync(ct);
        page.Reverse();
        return page;
    }

    public async Task MarkDirectReadAsync(int serverId, string peerKey, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.MumbleDirectMessages.Where(m => m.ServerId == serverId && m.PeerKey == peerKey && !m.IsRead)
                              .ExecuteUpdateAsync(u => u.SetProperty(m => m.IsRead, true), ct);
        if (changed > 0) DirectUnreadChanged?.Invoke();
    }

    /// <summary>To someone by what their conversation is filed under. ⚠️ They have to be connected: a server holds nothing for later.</summary>
    public Task SendDirectAsync(int serverId, string peerKey, string message, CancellationToken ct = default)
    {
        if (SessionOf(serverId) is not { } session) throw new MumbleException(MumbleFailure.Network, "Not connected.");
        var person = PersonOf(serverId, peerKey) ?? throw new MumbleException(MumbleFailure.Network, "They are not connected.");
        return SendPrivateTextAsync(session, person, message, ct);
    }

    /// <summary>Private messages past the days Jabber's history is kept, by the same setting. Zero keeps them all.</summary>
    private async Task PurgeOldChatAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var saved = await db.AppPreferences.AsNoTracking().Where(p => p.Key == Jabber.JabberService.HistoryDaysPref).Select(p => p.Value).FirstOrDefaultAsync(ct);
            var days  = int.TryParse(saved, out var d) ? d : Jabber.JabberService.DefaultHistoryDays;
            if (days <= 0) return;

            var cutoff = DateTimeOffset.UtcNow.AddDays(-days).UtcDateTime.ToString(StoredDate, CultureInfo.InvariantCulture);
            // ⚠️ Never an unread one: age is no reason to lose what was not seen.
            await db.MumbleDirectMessages.Where(m => m.IsRead && string.Compare(m.SentAt, cutoff) < 0).ExecuteDeleteAsync(ct);
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException)
        {
            errors.Log(nameof(MumbleService), "purge old chat", e);
        }
    }

    public async Task StopAsync()
    {
        MumbleSession[] all;
        lock (_gate) all = [.. _sessions.Values];
        await Task.WhenAll(all.Select(DisconnectAsync));

        // A moment for the last lines said to reach the database.
        for (var waited = 0; _chatSaves.Reader.Count > 0 && waited < 20; waited++) await Task.Delay(100);

        _talkerTimer?.Dispose();
        Devices?.Dispose();
    }

    public IReadOnlyList<MumbleSession> Sessions
    {
        get { lock (_gate) return [.. _sessions.Values.OrderBy(s => s.Server.Id)]; }
    }

    public MumbleSession? SessionOf(int id)
    {
        lock (_gate) return _sessions.GetValueOrDefault(id);
    }

    private MumbleSession Track(MumbleServer server)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(server.Id, out var known)) { known.Server = server; return known; }
            return _sessions[server.Id] = new MumbleSession(server);
        }
    }

    // ── Servers ───────────────────────────────────────────────────────────────

    /// <summary>Saves a server, new or changed. A null password leaves the saved one as it is.</summary>
    public async Task<MumbleSession> SaveServerAsync(MumbleServer server, string? password, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (server.Id == 0) db.MumbleServers.Add(server);
        else db.MumbleServers.Update(server);
        await db.SaveChangesAsync(ct);

        if (password is not null)
        {
            var id = server.Id;
            await Task.Run(() => AppConfig.SetServicePassword(PasswordReference(id), password), ct);
        }

        var session = Track(server);

        // A key changed while held would never be seen to come up.
        StopTalking(session);
        BindKeys(session);
        SessionChanged?.Invoke(session);
        return session;
    }

    /// <summary>Forgets a server and how loud everyone on it was. Its certificate goes too. Permanent.</summary>
    public async Task DeleteServerAsync(int id, CancellationToken ct = default)
    {
        MumbleSession? session;
        lock (_gate) _sessions.Remove(id, out session);
        if (session is not null) await DisconnectAsync(session);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.MumbleUserVolumes.Where(v => v.ServerId == id).ExecuteDeleteAsync(ct);
        await db.MumbleDirectMessages.Where(m => m.ServerId == id).ExecuteDeleteAsync(ct);
        await db.MumbleServers.Where(s => s.Id == id).ExecuteDeleteAsync(ct);

        await Task.Run(() =>
        {
            AppConfig.SetServicePassword(PasswordReference(id), null);
            AppConfig.SetServicePassword(CertificateReference(id), null);
        }, ct);
    }

    public async Task TrustCertificateAsync(MumbleSession session, CancellationToken ct = default)
    {
        session.Server.TrustedCertificate = session.CertificateFingerprint;
        await SaveServerAsync(session.Server, null, ct);
        Connect(session);
    }

    /// <summary>
    /// This server's certificate, made the first time it is needed. ⚠️ It is how a server knows a returning
    /// user, including whether they are registered: losing it loses the registered name, so it is kept per
    /// server, protected, and never replaced while it can still be read.
    /// </summary>
    private static X509Certificate2 ClientCertificate(MumbleServer server)
    {
        var reference = CertificateReference(server.Id);
        var saved = AppConfig.GetServicePassword(reference);

        // ⚠️ Saved but not readable right now, as with a locked keychain, is not the same as never saved. Making a
        // new one would throw away the registered name for good, so this waits and tries again.
        if (saved is null && AppConfig.HasServicePassword(reference))
            throw new MumbleException(MumbleFailure.Network, "This server's saved identity cannot be opened right now.");

        if (saved is not null)
        {
            try { return MumbleClient.LoadIdentity(Convert.FromBase64String(saved)); }
            catch (System.Security.Cryptography.CryptographicException e)
            {
                throw new MumbleException(MumbleFailure.Network, "This server's saved identity cannot be opened right now.", e);
            }
            catch (FormatException) { /* not a certificate at all, so nothing is lost by making one */ }
        }

        var made = MumbleClient.NewClientCertificate(server.Username.Length > 0 ? server.Username : "EVE Console");
        AppConfig.SetServicePassword(reference, Convert.ToBase64String(made.Export(X509ContentType.Pkcs12)));
        return made;
    }

    // ── Connecting ────────────────────────────────────────────────────────────

    public void Connect(MumbleSession session)
    {
        if (AppRuntime.IsHeadless || AppRuntime.IsTray) return;

        lock (_gate)
        {
            if (session.Loop is { IsCompleted: false }) return;
            session.Run  = new CancellationTokenSource();
            session.Loop = Task.Run(() => KeepConnectedAsync(session, session.Run.Token));
        }
    }

    public async Task DisconnectAsync(MumbleSession session)
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
            errors.Log(nameof(MumbleService), "disconnect", e);
        }
        catch (Exception) { /* it is told to stop, and will */ }

        SetState(session, MumbleState.Offline, MumbleProblem.None, "");
    }

    /// <summary>
    /// Connects and reconnects until cancelled. ⚠️ Stops on what trying again cannot fix: a wrong password
    /// retried for hours looks like an attack and can get an address banned.
    /// </summary>
    private async Task KeepConnectedAsync(MumbleSession session, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var started = DateTimeOffset.UtcNow;
            SetState(session, attempt == 0 ? MumbleState.Connecting : MumbleState.Reconnecting, MumbleProblem.None, session.Detail);

            try
            {
                await RunOnceAsync(session, ct);
                if (ct.IsCancellationRequested) break;
                session.Detail = "";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (MumbleException e) when (e.Failure == MumbleFailure.Certificate)
            {
                session.CertificateFingerprint = e.CertificateFingerprint ?? "";
                session.CertificateSubject     = e.CertificateSubject ?? "";
                SetState(session, MumbleState.Failed, MumbleProblem.Certificate, e.Message);
                return;
            }
            catch (MumbleException e) when (e.Failure == MumbleFailure.Removed)
            {
                // ⚠️ Not a dropped connection. Going straight back would undo a kick, and two clients on one login would
                // throw each other off for ever. The standard client stays off too.
                SetState(session, MumbleState.Failed, MumbleProblem.Removed, e.Message);
                return;
            }
            catch (MumbleException e) when (e.Failure == MumbleFailure.Rejected
                && e.Reject is MumbleReject.WrongUserPassword or MumbleReject.WrongServerPassword or MumbleReject.NoCertificate
                               or MumbleReject.InvalidUsername or MumbleReject.WrongVersion or MumbleReject.AuthenticatorFail)
            {
                SetState(session, MumbleState.Failed, e.Reject switch
                {
                    MumbleReject.WrongUserPassword or MumbleReject.WrongServerPassword => MumbleProblem.Password,
                    MumbleReject.InvalidUsername => MumbleProblem.Username,
                    _ => MumbleProblem.Other,
                }, e.Message);
                return;
            }
            catch (Exception e)
            {
                // ⚠️ Everything. This loop is the only thing bringing the connection back, and a database that is
                // busy or a value it does not like must not end that without a word.
                if (e is not (MumbleException or IOException or TimeoutException or OperationCanceledException))
                    errors.Log(nameof(MumbleService), "keep connected", e);
                session.Detail = e.Message;
            }
            finally
            {
                try { await EndConnectionAsync(session); }
                catch (Exception e) when (e is not OperationCanceledException) { errors.Log(nameof(MumbleService), "end a connection", e); }
            }

            // Told to stop: the failure that just ended the read loop is that, and not worth retrying.
            if (ct.IsCancellationRequested) break;

            if (DateTimeOffset.UtcNow - started > StableAfter) attempt = 0;

            var wait = TimeSpan.FromSeconds(BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);
            attempt++;

            session.NextAttempt = DateTimeOffset.UtcNow + wait;
            SetState(session, MumbleState.Reconnecting, MumbleProblem.None, session.Detail);

            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
            finally { session.NextAttempt = null; }
        }
    }

    private async Task EndConnectionAsync(MumbleSession session)
    {
        Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
        ReleaseMicIfIdle();
        UnbindKeys(session);
        Mixer.RemoveServer(session.Server.Id);
        if (session.Client is { } client)
        {
            session.Client = null;
            await client.DisposeAsync();
        }
        TalkersChanged?.Invoke();
    }

    private async Task RunOnceAsync(MumbleSession session, CancellationToken ct)
    {
        var server   = session.Server;
        var password = await Task.Run(() => AppConfig.GetServicePassword(PasswordReference(server.Id)) ?? "", ct);
        var identity = await Task.Run(() => ClientCertificate(server), ct);

        var client = new MumbleClient(new MumbleLogin(
            server.Host, server.Port, server.Username, password, identity, server.TrustedCertificate,
            [.. server.Tokens.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]));

        client.VoiceReceived += v => OnVoice(session, v);
        client.UserLeft += s => { Mixer.Remove(server.Id, s); TalkersChanged?.Invoke(); };
        client.TextReceived += t => OnText(session, client, t);
        client.Denied += why => { if (why.Length > 0) { session.Detail = why; SessionChanged?.Invoke(session); } };

        // ⚠️ Where to return to is read before connecting, and nothing is remembered until that is done:
        // every login starts at the root, which would otherwise overwrite it on the way in.
        var returnTo = server.LastChannel;
        var settled  = false;

        uint lastChannel = uint.MaxValue;
        client.Changed += () =>
        {
            // Whisper names a channel by id, so it follows this client whenever it moves.
            if (client.Me is { } me && me.ChannelId != lastChannel)
            {
                lastChannel = me.ChannelId;
                _ = SendTargetAsync(session, client, me.ChannelId, ct);
                if (Volatile.Read(ref settled)) _ = RememberChannelAsync(session, client, me.ChannelId);
            }
            SessionChanged?.Invoke(session);
        };

        session.Client = client;
        await client.ConnectAsync(ct);

        session.Encoder = new MumbleEncoder(MumbleEncoder.BitrateFor(client.MaxBandwidth));
        session.Volumes = await LoadVolumesAsync(server.Id, ct);

        // Back where they were, and as muted as they were.
        if (FindChannel(client, returnTo) is { } channel && channel != client.Me?.ChannelId)
            await client.JoinChannelAsync(channel, ct);
        Volatile.Write(ref settled, true);

        if (server.SelfMuted || server.SelfDeafened)
            await client.SetSelfMuteAsync(server.SelfMuted, server.SelfDeafened, ct);

        Devices?.StartOutput();
        BindKeys(session);

        SetState(session, MumbleState.Connected, MumbleProblem.None, "");
        await client.RunAsync(ct);

        if (!ct.IsCancellationRequested)
            throw new MumbleException(MumbleFailure.Network, "The connection ended.");
    }

    private static async Task SendTargetAsync(MumbleSession session, MumbleClient client, uint channel, CancellationToken ct)
    {
        try { await client.SetChannelTargetAsync(ChannelOnlyTarget, channel, ct); }
        catch (Exception e) when (e is MumbleException or OperationCanceledException or IOException) { /* the next connection sets it again */ }
    }

    /// <summary>A channel as its names from the root, which survive a server restart better than its id.</summary>
    public static string PathOf(MumbleClient client, uint channel)
    {
        var names = new List<string>();
        // The client refuses loops, and the count is here so that a mistake there could not hang this.
        var steps = 0;
        for (var c = client.ChannelOf(channel); c?.Parent is { } parent && steps < 1000; c = client.ChannelOf(parent), steps++) names.Insert(0, c.Name);
        return string.Join('/', names);
    }

    public static uint? FindChannel(MumbleClient client, string path)
    {
        if (path.Length == 0) return null;

        uint at = 0;
        foreach (var name in path.Split('/'))
        {
            var next = client.Channels.FirstOrDefault(c => c.Parent == at && c.Name == name);
            if (next is null) return null;
            at = next.Id;
        }
        return at;
    }

    private async Task RememberChannelAsync(MumbleSession session, MumbleClient client, uint channel)
    {
        var path = PathOf(client, channel);
        if (path == session.Server.LastChannel) return;

        session.Server.LastChannel = path;
        var id = session.Server.Id;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.MumbleServers.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.LastChannel, path));
        }
        catch (Exception e) when (e is DbUpdateException or InvalidOperationException) { errors.Log(nameof(MumbleService), "remember the channel", e); }
    }

    private void SetState(MumbleSession session, MumbleState state, MumbleProblem problem, string detail)
    {
        session.State   = state;
        session.Problem = problem;
        session.Detail  = detail;
        SessionChanged?.Invoke(session);
    }

    // ── Hearing ───────────────────────────────────────────────────────────────

    private void OnVoice(MumbleSession session, MumbleVoicePacket packet)
    {
        if (session.Server.SelfDeafened || session.Client is not { } client) return;

        // ⚠️ Only from someone the server has said is there. Each new speaker costs a decoder kept until they leave,
        // so sessions made up on the spot would use up memory without limit.
        if (client.UserOf(packet.Session) is not { } user) return;

        var gain = session.VolumeOf(user);
        if (gain <= 0) return;

        Mixer.Receive(session.Server.Id, packet, gain);
    }

    private async Task<Dictionary<string, double>> LoadVolumesAsync(int serverId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.MumbleUserVolumes.AsNoTracking().Where(v => v.ServerId == serverId)
                       .ToDictionaryAsync(v => v.UserKey, v => v.Volume, ct);
    }

    /// <summary>Sets how loud someone is for this listener, and remembers it.</summary>
    public async Task SetUserVolumeAsync(MumbleSession session, MumbleUser user, double volume, CancellationToken ct = default)
    {
        var key = UserKey(user);
        var value = Math.Clamp(volume, 0, 3);
        session.Volumes[key] = value;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.MumbleUserVolumes.FirstOrDefaultAsync(v => v.ServerId == session.Server.Id && v.UserKey == key, ct);
        if (row is null) db.MumbleUserVolumes.Add(new MumbleUserVolume { ServerId = session.Server.Id, UserKey = key, Volume = value });
        else row.Volume = value;
        await db.SaveChangesAsync(ct);
    }

    // ── Text ──────────────────────────────────────────────────────────────────


    /// <summary>Mumble text is HTML, and anyone can send anything. It is shown as plain words, never rendered.</summary>
    public static string PlainText(string html)
    {
        var withBreaks = html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        return WebUtility.HtmlDecode(WithoutTags(withBreaks)).Trim();
    }

    /// <summary>
    /// ⚠️ By hand and in one pass, not a regex: megabytes of "&lt;" with no "&gt;" would take a regex minutes, on the
    /// connection's reading thread.
    /// </summary>
    private static string WithoutTags(string html)
    {
        var words = new StringBuilder(html.Length);
        var at = 0;
        while (at < html.Length)
        {
            var open = html.IndexOf('<', at);
            if (open < 0) { words.Append(html, at, html.Length - at); break; }

            words.Append(html, at, open - at);

            var close = html.IndexOf('>', open + 1);
            if (close < 0) { words.Append(html, open, html.Length - open); break; }

            at = close + 1;
        }
        return words.ToString();
    }

    private void OnText(MumbleSession session, MumbleClient client, MumbleTextMessage text)
    {
        var sender = text.Actor is { } actor ? client.UserOf(actor)?.Name ?? "" : "";
        var words = PlainText(text.Message);
        if (words.Length > MostChatChars) words = words[..MostChatChars] + "\u2026";

        if (text.Sessions.Count > 0)
        {
            // Filed under who sent it, so a server's own notices, from nobody in particular, stay in its chat.
            if (text.Actor is { } from && client.UserOf(from) is { } person)
            {
                RecordDirect(session.Server.Id, person, sender, words, false);
                return;
            }
        }

        var isTree = text.Trees.Count > 0;
        uint? where = isTree ? text.Trees[0] : text.Channels.Count > 0 ? text.Channels[0] : null;
        var channel = where is { } id ? client.ChannelOf(id)?.Name ?? "" : "";

        Record(session, new MumbleChatLine(DateTimeOffset.UtcNow, sender, words, false) { Channel = channel, IsTree = isTree });
    }

    /// <summary>To a channel, by default the one this client is in, and if asked the ones under it too.</summary>
    public async Task SendTextAsync(MumbleSession session, string message, uint? channel = null, bool andSubchannels = false, CancellationToken ct = default)
    {
        if (session.Client is not { Me: { } me } client) throw new MumbleException(MumbleFailure.Network, "Not connected.");

        var target = channel ?? me.ChannelId;
        var html = WebUtility.HtmlEncode(message).Replace("\n", "<br>", StringComparison.Ordinal);

        if (andSubchannels) await client.SendTextToTreeAsync(target, html, ct);
        else await client.SendTextToChannelAsync(target, html, ct);

        Record(session, new MumbleChatLine(DateTimeOffset.UtcNow, me.Name, message, true) { Channel = client.ChannelOf(target)?.Name ?? "", IsTree = andSubchannels });
    }

    /// <summary>To one person, wherever on the server they are.</summary>
    public async Task SendPrivateTextAsync(MumbleSession session, MumbleUser to, string message, CancellationToken ct = default)
    {
        if (session.Client is not { Me: { } me } client) throw new MumbleException(MumbleFailure.Network, "Not connected.");

        await client.SendTextToUserAsync(to.Session, WebUtility.HtmlEncode(message).Replace("\n", "<br>", StringComparison.Ordinal), ct);

        RecordDirect(session.Server.Id, to, me.Name, message, true);
    }

    public Task JoinChannelAsync(MumbleSession session, uint channel, CancellationToken ct = default) =>
        session.Client?.JoinChannelAsync(channel, ct) ?? Task.CompletedTask;

    public async Task SetMuteAsync(MumbleSession session, bool mute, bool deaf, CancellationToken ct = default)
    {
        session.Server.SelfMuted    = mute || deaf;
        session.Server.SelfDeafened = deaf;

        if (mute || deaf) StopTalking(session);
        if (deaf) Mixer.RemoveServer(session.Server.Id);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var id = session.Server.Id;
            await db.MumbleServers.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.SelfMuted, mute || deaf).SetProperty(s => s.SelfDeafened, deaf), ct);
        }

        if (session.Client is { } client && session.State == MumbleState.Connected)
            await client.SetSelfMuteAsync(mute || deaf, deaf, ct);

        SessionChanged?.Invoke(session);
    }

    // ── Talking ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Binds this server's keys, for as long as it is connected. ⚠️ One hook per key: the same keyboard
    /// service push-to-talk for the agent uses, once for each, so each server's keys are its alone.
    /// </summary>
    private void BindKeys(MumbleSession session)
    {
        UnbindKeys(session);
        if (session.State != MumbleState.Connected && session.Client is null) return;

        session.WhisperHook = Hook(session.Server.WhisperKey, MumbleTalk.Channel);
        session.ShoutHook   = Hook(session.Server.ShoutKey, MumbleTalk.Shout);

        GlobalHotkeyService? Hook(int key, MumbleTalk mode)
        {
            if (key == 0) return null;
            var hook = new GlobalHotkeyService
            {
                OnPress   = () => _keyEvents.Writer.TryWrite(() => StartTalking(session, mode)),
                OnRelease = () => _keyEvents.Writer.TryWrite(() => StopTalking(session, mode)),
            };
            hook.Configure(key);
            return hook;
        }
    }

    private static void UnbindKeys(MumbleSession session)
    {
        session.WhisperHook?.Dispose();
        session.ShoutHook?.Dispose();
        session.WhisperHook = session.ShoutHook = null;
    }

    /// <summary>Starts sending the microphone to this server, on this key's terms.</summary>
    public void StartTalking(MumbleSession session, MumbleTalk mode)
    {
        if (session.Client is null || session.Encoder is null || session.Server.SelfMuted) return;

        // A new press starts clean: a few samples left from the last one are not part of what is being said now.
        if (Sessions.All(s => s.TalkingNow == MumbleTalk.None))
            lock (_micBuffer)
            {
                _micBuffer.Clear();
                _leveler.StartTalk();
            }

        Interlocked.Exchange(ref session.Talking, (int)mode);
        _micClose?.Cancel();

        if (Devices is { } devices && !devices.StartInput())
        {
            // Saying so, rather than showing them as talking to a room that hears nothing.
            Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
            session.MicFailed = true;
            session.Detail = devices.LastError ?? "";
            SessionChanged?.Invoke(session);
        }
        else if (session.MicFailed)
        {
            session.MicFailed = false;
            session.Detail = "";
            SessionChanged?.Invoke(session);
        }

        TalkersChanged?.Invoke();
    }

    private async Task HandleKeysAsync()
    {
        await foreach (var key in _keyEvents.Reader.ReadAllAsync())
        {
            try { key(); }
            catch (Exception e) { errors.Log(nameof(MumbleService), "handle a key", e); }
        }
    }

    /// <summary>Stops, if this key is the one talking: letting go of Shout while holding the other changes nothing.</summary>
    public void StopTalking(MumbleSession session, MumbleTalk? onlyIf = null)
    {
        if (onlyIf is { } mode && session.TalkingNow != mode) return;

        var was = (MumbleTalk)Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
        if (was == MumbleTalk.None) return;

        // The last frame says they let go, so the listeners' clients stop at once rather than timing out. It goes
        // through the sender: an encoder has state, and only one thread may ever use it.
        _frames.Writer.TryWrite(new VoiceWork(null, session, was));

        ReleaseMicIfIdle();
        TalkersChanged?.Invoke();
    }

    public static int TargetOf(MumbleTalk mode) => mode == MumbleTalk.Shout ? PlainTalk : ChannelOnlyTarget;

    private void ReleaseMicIfIdle()
    {
        if (Devices is null || Sessions.Any(s => s.TalkingNow != MumbleTalk.None)) return;

        _micClose?.Cancel();
        var closing = _micClose = new CancellationTokenSource();
        _ = Task.Delay(MicLinger, closing.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !Sessions.Any(s => s.TalkingNow != MumbleTalk.None)) Devices?.StopInput();
        }, TaskScheduler.Default);
    }

    private void OnCaptured(short[] samples) => SubmitMicrophone(samples);

    /// <summary>Takes microphone samples in 20 ms frames. Fast, as it runs on the sound device's thread.</summary>
    public void SubmitMicrophone(ReadOnlySpan<short> samples)
    {
        lock (_micBuffer)
        {
            foreach (var s in samples) _micBuffer.Add(s);

            while (_micBuffer.Count >= MumbleEncoder.FrameSamples)
            {
                var frame = _micBuffer.GetRange(0, MumbleEncoder.FrameSamples).ToArray();
                _micBuffer.RemoveRange(0, MumbleEncoder.FrameSamples);
                if (AutoLevel) _leveler.Process(frame);
                _frames.Writer.TryWrite(new VoiceWork(frame, null, MumbleTalk.None));
            }
        }
    }

    /// <summary>Starts the worker a test drives without any sound hardware.</summary>
    public void StartWithoutDevices()
    {
        _sender ??= Task.Run(SendFramesAsync, CancellationToken.None);
        _keyWorker ??= Task.Run(HandleKeysAsync, CancellationToken.None);
        _chatSaver ??= Task.Run(SaveChatAsync, CancellationToken.None);
        StartTalkerWatch();
    }

    private async Task SendFramesAsync()
    {
        var silence = new short[MumbleEncoder.FrameSamples];

        await foreach (var work in _frames.Reader.ReadAllAsync())
        {
            // ⚠️ Whatever goes wrong costs one frame. Ending this loop would end voice on every server until a restart.
            try
            {
                if (work.EndOf is { Client: { } endClient, Encoder: { } endEncoder } ended)
                    SendOut(ended, endClient, endEncoder.Encode(silence, TargetOf(work.EndMode), last: true), mustGo: true);
                else if (work.Frame is { } frame)
                    foreach (var session in Sessions)
                    {
                        var mode = session.TalkingNow;
                        if (mode == MumbleTalk.None || session.Client is not { } client || session.Encoder is not { } encoder) continue;
                        SendOut(session, client, encoder.Encode(frame, TargetOf(mode), last: false), mustGo: false);
                    }
            }
            catch (Exception e) { errors.Log(nameof(MumbleService), "send voice", e); }
        }
    }

    /// <summary>
    /// ⚠️ Never waited on: one server that has stopped reading must not hold up the others. A frame that cannot go
    /// yet is dropped, which is the only useful thing to do with speech that is already late.
    /// </summary>
    private static void SendOut(MumbleSession session, MumbleClient client, MumbleVoicePacket packet, bool mustGo)
    {
        if (!mustGo && session.VoiceSending is { IsCompleted: false }) return;

        // ⚠️ Over UDP it has finished by now. Wrapping that in a continuation made it look unfinished for a moment, and a
        // sound device hands over frames in bursts, so the next one could arrive in that moment and be thrown away.
        var sending = client.SendVoiceAsync(packet, CancellationToken.None);
        session.VoiceSending = sending.IsCompletedSuccessfully
            ? sending
            : sending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private Timer? _talkerTimer;
    private int _talkerTicks;
    private string _lastTalkers = "";

    private void StartTalkerWatch()
    {
        _talkerTimer ??= new Timer(_ =>
        {
            // Every couple of seconds: a headset unplugged ends its stream without saying so.
            if (++_talkerTicks % 20 == 0) Devices?.KeepOutputAlive();

            var now = string.Join('|', Talkers().Select(t => $"{t.ServerId}:{t.Session}:{t.Mode}"));
            if (now == _lastTalkers) return;
            _lastTalkers = now;
            TalkersChanged?.Invoke();
        }, null, 100, 100);
    }

    /// <summary>Whether someone is muted and deafened as things stand, or null if they are no longer there.</summary>
    public (bool Muted, bool Deaf)? StateOf(int serverId, uint session) =>
        SessionOf(serverId)?.Client?.UserOf(session) is { } user ? (user.Mute || user.SelfMute || user.Suppress, user.Deaf || user.SelfDeaf) : null;

    /// <summary>Everyone talking right now, across every server, with this client first where it is.</summary>
    public IReadOnlyList<MumbleTalker> Talkers()
    {
        var result = new List<MumbleTalker>();

        foreach (var session in Sessions)
            if (session.TalkingNow != MumbleTalk.None && session.Client?.Me is { } me)
                result.Add(new MumbleTalker(session.Server.Id, Label(session), me.Session, me.Name, true, session.TalkingNow));

        foreach (var (server, user) in Mixer.TalkingNow())
            if (SessionOf(server) is { Client: { } client } session && client.UserOf(user) is { } who)
                result.Add(new MumbleTalker(server, Label(session), user, who.Name, false, MumbleTalk.Channel));

        return result;

        static string Label(MumbleSession s) => s.Server.Name.Length > 0 ? s.Server.Name : s.Server.Host;
    }

    /// <summary>
    /// A key another server uses, or the same as this one's other: keys must be unique, or one press would
    /// talk on two servers at once. Null when there is no clash.
    /// </summary>
    public static string? KeyClash(MumbleServer server, IEnumerable<MumbleServer> others)
    {
        if (server.WhisperKey != 0 && server.WhisperKey == server.ShoutKey) return server.Name;

        foreach (var other in others)
        {
            if (other.Id == server.Id) continue;
            foreach (var key in new[] { server.WhisperKey, server.ShoutKey })
                if (key != 0 && (key == other.WhisperKey || key == other.ShoutKey))
                    return other.Name.Length > 0 ? other.Name : other.Host;
        }
        return null;
    }
}
