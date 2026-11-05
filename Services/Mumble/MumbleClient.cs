using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EveConsole.Services;

namespace EveConsole.Services.Mumble;

public enum MumbleFailure { Network, Certificate, Rejected, Protocol }

public enum MumbleReject { None, WrongVersion, InvalidUsername, WrongUserPassword, WrongServerPassword, UsernameInUse, ServerFull, NoCertificate, AuthenticatorFail }

public sealed class MumbleException(MumbleFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MumbleFailure Failure { get; } = failure;
    public MumbleReject Reject { get; init; }
    public string? CertificateFingerprint { get; init; }
    public string? CertificateSubject { get; init; }
}

public sealed class MumbleChannel
{
    public uint   Id          { get; init; }
    public uint?  Parent      { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public int    Position    { get; set; }
    public bool   Temporary   { get; set; }
    public HashSet<uint> Links { get; } = [];
}

public sealed class MumbleUser
{
    public uint   Session    { get; init; }
    public string Name       { get; set; } = "";
    public uint?  UserId     { get; set; }
    public uint   ChannelId  { get; set; }
    public bool   Mute       { get; set; }
    public bool   Deaf       { get; set; }
    public bool   Suppress   { get; set; }
    public bool   SelfMute   { get; set; }
    public bool   SelfDeaf   { get; set; }
    public string Hash       { get; set; } = "";
    public bool   Recording  { get; set; }
    public string Comment    { get; set; } = "";
}

public sealed record MumbleLogin(
    string Host, int Port, string Username, string Password, X509Certificate2? ClientCertificate,
    string TrustedCertificate, IReadOnlyList<string> Tokens);

public sealed record MumbleTextMessage(uint? Actor, IReadOnlyList<uint> Sessions, IReadOnlyList<uint> Channels, string Message);

/// <summary>
/// One logged-in connection to a Mumble server: the channel tree, who is where, and voice over its control
/// connection until the UDP path is proved.
///
/// <para>⚠️ It says it is version 1.4. From 1.5 a client is sent voice in a newer format; every server still
/// serves this one, and it is the published, simpler of the two. Nothing else about the version matters.</para>
///
/// <para>Ends when the connection does. A new one is made to reconnect, so nothing stale can survive one.</para>
/// </summary>
public sealed class MumbleClient : IAsyncDisposable
{
    private const uint ClaimedVersion = 1 << 16 | 4 << 8;   // 1.4.0
    private const int MaxMessage = 8 * 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    /// <summary>Pings a server may leave unanswered before the connection counts as dead.</summary>
    private const int MissedPings = 3;

    private readonly MumbleLogin _login;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<uint, MumbleChannel> _channels = [];
    private readonly Dictionary<uint, MumbleUser> _users = [];

    private TcpClient? _tcp;
    private Stream? _stream;
    private int _unansweredPings;

    public MumbleClient(MumbleLogin login) => _login = login;

    public uint Session { get; private set; }
    public int MaxBandwidth { get; private set; }
    public string WelcomeText { get; private set; } = "";
    public string ServerRelease { get; private set; } = "";

    public MumbleUser? Me { get { lock (_gate) return _users.GetValueOrDefault(Session); } }
    public IReadOnlyList<MumbleChannel> Channels { get { lock (_gate) return [.. _channels.Values]; } }
    public IReadOnlyList<MumbleUser> Users { get { lock (_gate) return [.. _users.Values]; } }

    public MumbleChannel? ChannelOf(uint id) { lock (_gate) return _channels.GetValueOrDefault(id); }
    public MumbleUser? UserOf(uint session) { lock (_gate) return _users.GetValueOrDefault(session); }

    /// <summary>The channel tree or who is in it changed.</summary>
    public event Action? Changed;

    /// <summary>A user left: their session, and whether they are this client.</summary>
    public event Action<uint>? UserLeft;

    public event Action<MumbleTextMessage>? TextReceived;

    /// <summary>A permission or action was refused, in the server's words if it gave any.</summary>
    public event Action<string>? Denied;

    /// <summary>A voice packet arrived. ⚠️ On the reading thread: anything slow here holds up the connection.</summary>
    public event Action<MumbleVoicePacket>? VoiceReceived;

    /// <summary>Connects and logs in, returning once the server has said everything it has to say.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        await using var abort = timeout.Token.Register(Close);

        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(_login.Host, _login.Port, timeout.Token);

            string? fingerprint = null, subject = null;
            var tls = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);

            try
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = _login.Host,
                    ClientCertificates = _login.ClientCertificate is { } mine ? [mine] : null,
                    // The client's certificate is its identity on the server, and is sent whoever asks.
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => _login.ClientCertificate!,
                    RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        if (errors == SslPolicyErrors.None) return true;
                        if (certificate is null) return false;

                        fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
                        subject     = certificate.Subject;
                        return string.Equals(fingerprint, _login.TrustedCertificate, StringComparison.OrdinalIgnoreCase);
                    },
                }, timeout.Token);
            }
            catch (System.Security.Authentication.AuthenticationException e) when (fingerprint is not null)
            {
                throw new MumbleException(MumbleFailure.Certificate, "The server's certificate is not trusted.", e)
                {
                    CertificateFingerprint = fingerprint,
                    CertificateSubject     = subject,
                };
            }

            _stream = tls;

            await SendAsync(MumbleMessage.Version, new PbWriter()
                .UInt(1, ClaimedVersion).Text(2, "EVE Console").Text(3, OsName()).ToArray(), timeout.Token);

            var auth = new PbWriter().Text(1, _login.Username).Bool(5, true);
            if (_login.Password.Length > 0) auth.Text(2, _login.Password);
            foreach (var token in _login.Tokens) auth.Text(3, token);
            await SendAsync(MumbleMessage.Authenticate, auth.ToArray(), timeout.Token);

            // Everything up to ServerSync is the server describing itself: channels, then users.
            while (Session == 0)
            {
                var (type, body) = await ReadAsync(timeout.Token);
                Handle(type, body);
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is not MumbleException || timeout.IsCancellationRequested))
        {
            Close();
            throw new MumbleException(MumbleFailure.Network, timeout.IsCancellationRequested
                ? "The server did not answer in time." : e.Message, e);
        }
    }

    private static string OsName() =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>Reads until the connection ends. Returns when cancelled, and throws why it ended otherwise.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var abort = run.Token.Register(Close);
        var pinger = PingLoopAsync(run.Token);

        try
        {
            while (!run.Token.IsCancellationRequested)
            {
                var (type, body) = await ReadAsync(run.Token);
                Handle(type, body);
            }
        }
        catch (Exception e) when (ct.IsCancellationRequested && e is IOException or MumbleException or ObjectDisposedException) { /* told to stop */ }
        finally
        {
            await run.CancelAsync();
            await Task.WhenAny(pinger);
        }
    }

    /// <summary>
    /// Keeps the connection open and notices when it is not. ⚠️ A server drops a client after 30 seconds of
    /// silence, and a dead link may accept writes for a long time, so unanswered pings are what end it.
    /// </summary>
    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingEvery, ct);

                if (Interlocked.Increment(ref _unansweredPings) > MissedPings)
                {
                    Close();   // wakes the read loop, which reports the loss
                    return;
                }

                await SendAsync(MumbleMessage.Ping, new PbWriter().UInt(1, (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToArray(), ct);
            }
        }
        catch (OperationCanceledException) { /* the session is ending */ }
        catch (Exception e) when (e is IOException or MumbleException or ObjectDisposedException) { Close(); }
    }

    private async Task<(MumbleMessage Type, byte[] Body)> ReadAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new MumbleException(MumbleFailure.Network, "Not connected.");

        var header = new byte[6];
        await stream.ReadExactlyAsync(header, ct);

        var type   = (MumbleMessage)(header[0] << 8 | header[1]);
        var length = header[2] << 24 | header[3] << 16 | header[4] << 8 | header[5];
        if (length is < 0 or > MaxMessage)
            throw new MumbleException(MumbleFailure.Protocol, "The server sent a message far too large.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return (type, body);
    }

    private async Task SendAsync(MumbleMessage type, byte[] body, CancellationToken ct)
    {
        var frame = new byte[6 + body.Length];
        frame[0] = (byte)((ushort)type >> 8);
        frame[1] = (byte)type;
        frame[2] = (byte)(body.Length >> 24);
        frame[3] = (byte)(body.Length >> 16);
        frame[4] = (byte)(body.Length >> 8);
        frame[5] = (byte)body.Length;
        body.CopyTo(frame, 6);

        await _writeLock.WaitAsync(ct);
        try
        {
            var stream = _stream ?? throw new MumbleException(MumbleFailure.Network, "Not connected.");
            await stream.WriteAsync(frame, ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            throw new MumbleException(MumbleFailure.Network, e.Message, e);
        }
        finally { _writeLock.Release(); }
    }

    // ── What the server says ──────────────────────────────────────────────────

    private void Handle(MumbleMessage type, byte[] body)
    {
        if (type == MumbleMessage.UdpTunnel)
        {
            try
            {
                if (MumbleVoicePacket.Decode(body) is { } voice) VoiceReceived?.Invoke(voice);
            }
            catch (InvalidDataException) { /* one bad packet is not worth the connection */ }
            return;
        }

        PbMessage m;
        try { m = new PbMessage(body); }
        catch (InvalidDataException e) { throw new MumbleException(MumbleFailure.Protocol, e.Message, e); }

        switch (type)
        {
            case MumbleMessage.Version:
                ServerRelease = m.Text(2) ?? "";
                break;

            case MumbleMessage.Reject:
                var reason = (MumbleReject)(m.UInt(1) ?? 0);
                throw new MumbleException(MumbleFailure.Rejected, m.Text(2) ?? reason.ToString()) { Reject = reason };

            case MumbleMessage.ServerSync:
                MaxBandwidth = (int)(m.UInt(2) ?? 0);
                WelcomeText  = m.Text(3) ?? WelcomeText;
                Session      = (uint)(m.UInt(1) ?? 0);
                Changed?.Invoke();
                break;

            case MumbleMessage.ServerConfig:
                if (m.UInt(1) is { } bandwidth) MaxBandwidth = (int)bandwidth;
                if (m.Text(2) is { } welcome) WelcomeText = welcome;
                break;

            case MumbleMessage.Ping:
                Interlocked.Exchange(ref _unansweredPings, 0);
                break;

            case MumbleMessage.ChannelState:
                ApplyChannel(m);
                Changed?.Invoke();
                break;

            case MumbleMessage.ChannelRemove:
                lock (_gate) _channels.Remove((uint)(m.UInt(1) ?? 0));
                Changed?.Invoke();
                break;

            case MumbleMessage.UserState:
                ApplyUser(m);
                Changed?.Invoke();
                break;

            case MumbleMessage.UserRemove:
                var gone = (uint)(m.UInt(1) ?? 0);
                lock (_gate) _users.Remove(gone);
                UserLeft?.Invoke(gone);
                Changed?.Invoke();
                break;

            case MumbleMessage.TextMessage:
                TextReceived?.Invoke(new MumbleTextMessage(
                    m.UInt(1) is { } actor ? (uint)actor : null,
                    [.. m.UInts(2).Select(s => (uint)s)],
                    [.. m.UInts(3).Select(c => (uint)c)],
                    m.Text(5) ?? ""));
                break;

            case MumbleMessage.PermissionDenied:
                Denied?.Invoke(m.Text(4) ?? "");
                break;
        }
    }

    private void ApplyChannel(PbMessage m)
    {
        lock (_gate)
        {
            var id = (uint)(m.UInt(1) ?? 0);
            if (!_channels.TryGetValue(id, out var channel)) _channels[id] = channel = new MumbleChannel { Id = id };

            if (m.UInt(2) is { } parent) channel.Parent = (uint)parent;
            if (m.Text(3) is { } name) channel.Name = name;
            if (m.Text(5) is { } description) channel.Description = description;
            if (m.Bool(8) is { } temporary) channel.Temporary = temporary;
            if (m.UInt(9) is { } position) channel.Position = (int)position;

            // The whole list when it is sent, otherwise what to add and remove.
            if (m.UInts(4).Count > 0) { channel.Links.Clear(); foreach (var l in m.UInts(4)) channel.Links.Add((uint)l); }
            foreach (var l in m.UInts(6)) channel.Links.Add((uint)l);
            foreach (var l in m.UInts(7)) channel.Links.Remove((uint)l);
        }
    }

    private void ApplyUser(PbMessage m)
    {
        lock (_gate)
        {
            var session = (uint)(m.UInt(1) ?? 0);
            if (!_users.TryGetValue(session, out var user)) _users[session] = user = new MumbleUser { Session = session };

            if (m.Text(3) is { } name) user.Name = name;
            if (m.UInt(4) is { } userId) user.UserId = (uint)userId;
            if (m.UInt(5) is { } channel) user.ChannelId = (uint)channel;
            if (m.Bool(6) is { } mute) user.Mute = mute;
            if (m.Bool(7) is { } deaf) user.Deaf = deaf;
            if (m.Bool(8) is { } suppress) user.Suppress = suppress;
            if (m.Bool(9) is { } selfMute) user.SelfMute = selfMute;
            if (m.Bool(10) is { } selfDeaf) user.SelfDeaf = selfDeaf;
            if (m.Text(14) is { } comment) user.Comment = comment;
            if (m.Text(15) is { } hash) user.Hash = hash;
            if (m.Bool(19) is { } recording) user.Recording = recording;
        }
    }

    // ── Asking the server for things ──────────────────────────────────────────

    public Task JoinChannelAsync(uint channel, CancellationToken ct) =>
        SendAsync(MumbleMessage.UserState, new PbWriter().UInt(1, Session).UInt(5, channel).ToArray(), ct);

    /// <summary>Says this client is muted or deafened, which also stops the server sending it voice when deaf.</summary>
    public Task SetSelfMuteAsync(bool mute, bool deaf, CancellationToken ct) =>
        SendAsync(MumbleMessage.UserState, new PbWriter().UInt(1, Session).Bool(9, mute || deaf).Bool(10, deaf).ToArray(), ct);

    public Task SendTextToChannelAsync(uint channel, string message, CancellationToken ct) =>
        SendAsync(MumbleMessage.TextMessage, new PbWriter().UInt(3, channel).Text(5, message).ToArray(), ct);

    public Task SendTextToUserAsync(uint session, string message, CancellationToken ct) =>
        SendAsync(MumbleMessage.TextMessage, new PbWriter().UInt(2, session).Text(5, message).ToArray(), ct);

    /// <summary>
    /// Saves where a voice target sends to. Shout is a channel with what hangs off it, set again whenever
    /// this client moves, because the target names a channel by id rather than "wherever I am".
    /// </summary>
    public Task SetChannelTargetAsync(int targetId, uint channel, bool children, bool links, CancellationToken ct)
    {
        var target = new PbWriter().UInt(2, channel);
        if (links) target.Bool(4, true);
        if (children) target.Bool(5, true);

        return SendAsync(MumbleMessage.VoiceTarget, new PbWriter().UInt(1, (ulong)targetId).Bytes(2, target.ToArray()).ToArray(), ct);
    }

    public Task CreateChannelAsync(uint parent, string name, bool temporary, CancellationToken ct)
    {
        var state = new PbWriter().UInt(2, parent).Text(3, name);
        if (temporary) state.Bool(8, true);
        return SendAsync(MumbleMessage.ChannelState, state.ToArray(), ct);
    }

    /// <summary>A voice frame, over the control connection.</summary>
    public Task SendVoiceAsync(MumbleVoicePacket packet, CancellationToken ct) =>
        SendAsync(MumbleMessage.UdpTunnel, packet.Encode(), ct);

    /// <summary>Who hears what this client says on a target, by the rule the server applies. For display.</summary>
    public IReadOnlyList<MumbleUser> UsersIn(uint channel)
    {
        lock (_gate) return [.. _users.Values.Where(u => u.ChannelId == channel)];
    }

    private void Close()
    {
        try { _tcp?.Close(); } catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { /* going anyway */ }
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// <summary>A certificate to be known by: the server recognises a returning user by it, not by name.</summary>
    public static X509Certificate2 NewClientCertificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name.Replace(",", " ").Replace("=", " ")}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(30));
        return LoadIdentity(made.Export(X509ContentType.Pkcs12));
    }

    /// <summary>
    /// A saved certificate, ready to log in with. ⚠️ Not an ephemeral key set: Windows' TLS cannot use a key
    /// held only in memory for a client certificate, and macOS does not have them at all.
    /// </summary>
    public static X509Certificate2 LoadIdentity(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
}
