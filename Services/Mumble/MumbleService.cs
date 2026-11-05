using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Mumble;

public enum MumbleState { Offline, Connecting, Connected, Reconnecting, Failed }

public enum MumbleProblem { None, Certificate, Password, Username, Other }

/// <summary>What pressing a server's keys does.</summary>
public enum MumbleTalk { None, Channel, Shout }

/// <summary>A line of a server's text chat, in memory for as long as the app runs.</summary>
public sealed record MumbleChatLine(DateTimeOffset When, string Sender, string Text, bool IsPrivate, bool IsOutgoing);

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
public sealed partial class MumbleService(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errors)
{
    public const int MaxChatLines = 500;
    public const string InputDevicePref = "mumble.input_device";
    public const string OutputDevicePref = "mumble.output_device";
    public const string OutputVolumePref = "mumble.output_volume";
    public const string MicGainPref = "mumble.mic_gain";

    private const int ShoutTarget = 1;

    /// <summary>How long the microphone stays open after the last key lets go, so a quick re-press loses nothing.</summary>
    private static readonly TimeSpan MicLinger = TimeSpan.FromSeconds(5);

    private static readonly int[] BackoffSeconds = [5, 10, 30, 60, 120, 300];
    private static readonly TimeSpan StableAfter = TimeSpan.FromMinutes(2);

    private readonly Dictionary<int, MumbleSession> _sessions = [];
    private readonly object _gate = new();
    private readonly List<short> _micBuffer = new(MumbleEncoder.FrameSamples * 2);
    private readonly Channel<short[]> _frames = Channel.CreateBounded<short[]>(new BoundedChannelOptions(50)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

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
        StartTalkerWatch();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var server in await db.MumbleServers.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct))
        {
            var session = Track(server);
            if (server.AutoConnect) Connect(session);
        }
    }

    public async Task StopAsync()
    {
        MumbleSession[] all;
        lock (_gate) all = [.. _sessions.Values];
        await Task.WhenAll(all.Select(DisconnectAsync));
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

        if (saved is not null)
        {
            try { return MumbleClient.LoadIdentity(Convert.FromBase64String(saved)); }
            catch (Exception e) when (e is FormatException or System.Security.Cryptography.CryptographicException) { /* made afresh below */ }
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

        if (run is not null) await run.CancelAsync();
        if (session.Client is { } client) await client.DisposeAsync();

        if (loop is not null)
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { /* it is told to stop, and will */ }

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
            catch (Exception e) when (e is MumbleException or IOException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                session.Detail = e.Message;
            }
            finally
            {
                await EndConnectionAsync(session);
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
            // Shout names a channel by id, so it follows this client whenever it moves.
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
        try { await client.SetChannelTargetAsync(ShoutTarget, channel, session.Server.ShoutChildren, session.Server.ShoutLinks, ct); }
        catch (Exception e) when (e is MumbleException or OperationCanceledException or IOException) { /* the next connection sets it again */ }
    }

    /// <summary>A channel as its names from the root, which survive a server restart better than its id.</summary>
    public static string PathOf(MumbleClient client, uint channel)
    {
        var names = new List<string>();
        for (var c = client.ChannelOf(channel); c?.Parent is { } parent; c = client.ChannelOf(parent)) names.Insert(0, c.Name);
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

        var gain = client.UserOf(packet.Session) is { } user ? session.VolumeOf(user) : 1.0;
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

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    /// <summary>Mumble text is HTML, and anyone can send anything. It is shown as plain words, never rendered.</summary>
    public static string PlainText(string html)
    {
        var withBreaks = html.Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                             .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        return WebUtility.HtmlDecode(Tags().Replace(withBreaks, "")).Trim();
    }

    private void OnText(MumbleSession session, MumbleClient client, MumbleTextMessage text)
    {
        var sender = text.Actor is { } actor ? client.UserOf(actor)?.Name ?? "" : "";
        var line = new MumbleChatLine(DateTimeOffset.UtcNow, sender, PlainText(text.Message), text.Sessions.Count > 0, false);
        session.AddChat(line);
        ChatReceived?.Invoke(session, line);
    }

    public async Task SendTextAsync(MumbleSession session, string message, CancellationToken ct = default)
    {
        if (session.Client is not { Me: { } me } client) throw new MumbleException(MumbleFailure.Network, "Not connected.");

        await client.SendTextToChannelAsync(me.ChannelId, WebUtility.HtmlEncode(message).Replace("\n", "<br>", StringComparison.Ordinal), ct);

        var line = new MumbleChatLine(DateTimeOffset.UtcNow, me.Name, message, false, true);
        session.AddChat(line);
        ChatReceived?.Invoke(session, line);
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
                OnPress   = () => StartTalking(session, mode),
                OnRelease = () => StopTalking(session, mode),
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

        Interlocked.Exchange(ref session.Talking, (int)mode);
        _micClose?.Cancel();

        if (Devices is { } devices && !devices.StartInput()) session.Detail = devices.LastError ?? "";

        TalkersChanged?.Invoke();
    }

    /// <summary>Stops, if this key is the one talking: letting go of Shout while holding the other changes nothing.</summary>
    public void StopTalking(MumbleSession session, MumbleTalk? onlyIf = null)
    {
        if (onlyIf is { } mode && session.TalkingNow != mode) return;

        var was = (MumbleTalk)Interlocked.Exchange(ref session.Talking, (int)MumbleTalk.None);
        if (was == MumbleTalk.None) return;

        // The last frame says they let go, so the listeners' clients stop at once rather than timing out.
        if (session.Client is { } client && session.Encoder is { } encoder)
        {
            var packet = encoder.Encode(new short[MumbleEncoder.FrameSamples], TargetOf(was), last: true);
            _ = client.SendVoiceAsync(packet, CancellationToken.None)
                      .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        ReleaseMicIfIdle();
        TalkersChanged?.Invoke();
    }

    private static int TargetOf(MumbleTalk mode) => mode == MumbleTalk.Shout ? ShoutTarget : 0;

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
                _frames.Writer.TryWrite(frame);
            }
        }
    }

    /// <summary>Starts the worker a test drives without any sound hardware.</summary>
    public void StartWithoutDevices()
    {
        _sender ??= Task.Run(SendFramesAsync, CancellationToken.None);
        StartTalkerWatch();
    }

    private async Task SendFramesAsync()
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync())
        {
            foreach (var session in Sessions)
            {
                var mode = session.TalkingNow;
                if (mode == MumbleTalk.None || session.Client is not { } client || session.Encoder is not { } encoder) continue;

                try { await client.SendVoiceAsync(encoder.Encode(frame, TargetOf(mode), last: false), CancellationToken.None); }
                catch (Exception e) when (e is MumbleException or IOException or ObjectDisposedException) { /* the connection's own loop reports it */ }
            }
        }
    }

    private Timer? _talkerTimer;
    private string _lastTalkers = "";

    private void StartTalkerWatch()
    {
        _talkerTimer ??= new Timer(_ =>
        {
            var now = string.Join('|', Talkers().Select(t => $"{t.ServerId}:{t.Session}:{t.Mode}"));
            if (now == _lastTalkers) return;
            _lastTalkers = now;
            TalkersChanged?.Invoke();
        }, null, 100, 100);
    }

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
