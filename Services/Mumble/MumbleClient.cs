using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EveConsole.Services;

namespace EveConsole.Services.Mumble;

public enum MumbleFailure { Network, Certificate, Rejected, Protocol, Removed }

public enum MumbleReject { None, WrongVersion, InvalidUsername, WrongUserPassword, WrongServerPassword, UsernameInUse, ServerFull, NoCertificate, AuthenticatorFail }

public sealed class MumbleException(MumbleFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MumbleFailure Failure { get; } = failure;
    public MumbleReject Reject { get; init; }
    public string? CertificateFingerprint { get; init; }
    public string? CertificateSubject { get; init; }
}

/// <summary>What the server lets this client do in a channel, by the bits on the wire. Kick, ban and the two registers only mean anything at the root.</summary>
[Flags]
public enum MumblePermission : uint
{
    None = 0, Write = 0x1, Traverse = 0x2, Enter = 0x4, Speak = 0x8, MuteDeafen = 0x10, Move = 0x20, MakeChannel = 0x40,
    LinkChannel = 0x80, Whisper = 0x100, TextMessage = 0x200, MakeTempChannel = 0x400, Listen = 0x800,
    Kick = 0x10000, Ban = 0x20000, Register = 0x40000, SelfRegister = 0x80000, ResetUserContent = 0x100000,
}

/// <summary>Someone banned from a server. Kept as it came, as changing the list means sending all of it back.</summary>
/// <summary>One part of a voice target: a channel, with what comes along with it, or some people by session.</summary>
public readonly record struct MumbleTargetEntry(uint? Channel, string Group, bool Links, bool Subchannels, IReadOnlyList<uint> Sessions);

public sealed record MumbleBan(byte[] Raw, string Name, string Reason, string Started, uint DurationSeconds, string Address)
{
    public static MumbleBan Read(byte[] raw)
    {
        var m = new PbMessage(raw);
        return new MumbleBan(raw, m.Text(3) ?? "", m.Text(5) ?? "", m.Text(6) ?? "", (uint)(m.UInt(7) ?? 0), AddressText(m.Bytes(1)));
    }

    /// <summary>A server holds every address in the long form, with an old one sitting inside a new one.</summary>
    private static string AddressText(byte[]? bytes)
    {
        if (bytes is not { Length: 16 }) return "";
        var address = new System.Net.IPAddress(bytes);
        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }
}

public sealed class MumbleChannel
{
    public uint   Id          { get; init; }
    public uint?  Parent      { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>A long description comes only as this, until asked for.</summary>
    public bool   HasDescriptionToAsk { get; set; }
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

    /// <summary>A long comment comes only as this, until asked for.</summary>
    public bool   HasCommentToAsk { get; set; }
    public bool   PrioritySpeaker { get; set; }

    /// <summary>What the server last said about them when asked. Null until then.</summary>
    public uint?  OnlineSeconds { get; set; }
    public uint?  IdleSeconds { get; set; }
    public string ClientRelease { get; set; } = "";
    public string ClientOs { get; set; } = "";
    public HashSet<uint> ListeningTo { get; } = [];

    /// <summary>Their picture's hash as lower-case hex, blank until known, and the picture once it has come.</summary>
    public string TextureHash { get; set; } = "";
    public byte[]? Texture { get; set; }

    /// <summary>A picture too big to have come with the person, until asked for.</summary>
    public bool HasTextureToAsk { get; set; }
}

public sealed record MumbleLogin(
    string Host, int Port, string Username, string Password, X509Certificate2? ClientCertificate,
    string TrustedCertificate, IReadOnlyList<string> Tokens, bool ForceTcp = false);

public sealed record MumbleTextMessage(uint? Actor, IReadOnlyList<uint> Sessions, IReadOnlyList<uint> Channels, IReadOnlyList<uint> Trees, string Message);

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

    /// <summary>Far beyond any real server, and short of what a hostile one could otherwise make this hold.</summary>
    private const int MostChannels = 20_000, MostUsers = 20_000;

    /// <summary>A server that stops reading must not leave a write waiting for ever: pings go through here too.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);

    /// <summary>Pings a server may leave unanswered before the connection counts as dead.</summary>
    private const int MissedPings = 3;

    private readonly MumbleLogin _login;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<uint, MumbleChannel> _channels = [];
    private readonly Dictionary<uint, MumbleUser> _users = [];
    private readonly Dictionary<uint, MumblePermission> _permissions = [];
    private readonly HashSet<uint> _permissionsAsked = [];
    private readonly Dictionary<uint, string> _userNames = [];
    private readonly Dictionary<string, uint> _userIds = new(StringComparer.OrdinalIgnoreCase);

    private TcpClient? _tcp;
    private Stream? _stream;
    private int _unansweredPings;

    private readonly MumbleCrypt _crypt = new();
    private MumbleUdpChannel? _udp;
    private int _udpOpening;
    private volatile bool _closed;

    /// <summary>Whether voice is going over UDP right now, rather than the TLS connection.</summary>
    public bool IsUsingUdp => _udp?.IsWorking == true;

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

    /// <summary>The server's ban list, as asked for.</summary>
    public event Action<IReadOnlyList<MumbleBan>>? BansReceived;

    public event Action<MumbleTextMessage>? TextReceived;

    /// <summary>A permission or action was refused, in the server's words if it gave any.</summary>
    public event Action<string>? Denied;

    /// <summary>A channel's permissions and groups, as asked for with <see cref="AskAclAsync"/>.</summary>
    public event Action<MumbleAcl>? AclReceived;

    /// <summary>The server said who some registered users are: see <see cref="RegisteredName"/> and <see cref="RegisteredId"/>.</summary>
    public event Action? RegisteredNamesChanged;

    /// <summary>A registered user's name, by their id, once the server has said it.</summary>
    public string? RegisteredName(uint userId) { lock (_gate) return _userNames.GetValueOrDefault(userId); }

    /// <summary>A registered user's id, by their name, once the server has said it.</summary>
    public uint? RegisteredId(string name) { lock (_gate) return _userIds.TryGetValue(name, out var id) ? id : null; }

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

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(WriteTimeout);
        try
        {
            await _writeLock.WaitAsync(limit.Token);
            try
            {
                var stream = _stream ?? throw new MumbleException(MumbleFailure.Network, "Not connected.");
                await stream.WriteAsync(frame, limit.Token);
                await stream.FlushAsync(limit.Token);
            }
            finally { _writeLock.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Close();   // wakes the read loop, which reports the loss and reconnects
            throw new MumbleException(MumbleFailure.Network, "The server stopped taking data.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            throw new MumbleException(MumbleFailure.Network, e.Message, e);
        }
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
                if (m.UInt(4) is { } rootPermissions) lock (_gate) _permissions[0] = (MumblePermission)(uint)rootPermissions;
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

                // This client's own removal: kicked, banned, or this login connecting from somewhere else.
                if (Me?.Session == gone) throw new MumbleException(MumbleFailure.Removed, m.Text(3) ?? "");

                lock (_gate) _users.Remove(gone);
                UserLeft?.Invoke(gone);
                Changed?.Invoke();
                break;

            case MumbleMessage.TextMessage:
                TextReceived?.Invoke(new MumbleTextMessage(
                    m.UInt(1) is { } actor ? (uint)actor : null,
                    [.. m.UInts(2).Select(s => (uint)s)],
                    [.. m.UInts(3).Select(c => (uint)c)],
                    [.. m.UInts(4).Select(c => (uint)c)],
                    m.Text(5) ?? ""));
                break;

            case MumbleMessage.PermissionDenied:
                Denied?.Invoke(m.Text(4) ?? "");
                break;

            case MumbleMessage.CryptSetup:
                ApplyCrypt(m);
                break;

            case MumbleMessage.BanList:
                var bans = new List<MumbleBan>();
                try { foreach (var entry in m.AllBytes(1)) bans.Add(MumbleBan.Read(entry)); }
                catch (InvalidDataException) { /* one that cannot be read cannot be shown, and the list is not worth the connection */ }
                BansReceived?.Invoke(bans);
                break;

            case MumbleMessage.UserStats:
                lock (_gate)
                    if (_users.TryGetValue((uint)(m.UInt(1) ?? 0), out var seen))
                    {
                        if (m.UInt(16) is { } online) seen.OnlineSeconds = (uint)online;
                        if (m.UInt(17) is { } idle) seen.IdleSeconds = (uint)idle;
                        if (m.Bytes(12) is { Length: > 0 } version)
                        {
                            var about = new PbMessage(version);
                            seen.ClientRelease = about.Text(2) ?? seen.ClientRelease;
                            seen.ClientOs = about.Text(3) ?? seen.ClientOs;
                        }
                    }
                Changed?.Invoke();
                break;

            case MumbleMessage.Acl:
                MumbleAcl acl;
                try { acl = MumbleAcl.Read(m); }
                catch (InvalidDataException) { break; /* not worth the connection */ }
                AclReceived?.Invoke(acl);
                break;

            case MumbleMessage.QueryUsers:
                // Two lists side by side, ids and names. ⚠️ A name the server doesn't know is simply left out of the answer.
                var ids = m.UInts(1);
                var names = m.AllBytes(2);
                lock (_gate)
                    for (var i = 0; i < Math.Min(ids.Count, names.Count) && _userNames.Count < MostUsers * 4; i++)
                    {
                        var name = System.Text.Encoding.UTF8.GetString(names[i]);
                        _userNames[(uint)ids[i]] = name;
                        _userIds[name] = (uint)ids[i];
                    }
                RegisteredNamesChanged?.Invoke();
                break;

            case MumbleMessage.PermissionQuery:
                lock (_gate)
                {
                    // Everything known is out of date: what was granted has changed.
                    if (m.Bool(3) == true) { _permissions.Clear(); _permissionsAsked.Clear(); }
                    if (m.UInt(2) is { } granted) _permissions[(uint)(m.UInt(1) ?? 0)] = (MumblePermission)(uint)granted;
                }
                Changed?.Invoke();
                break;
        }
    }

    private void ApplyChannel(PbMessage m)
    {
        lock (_gate)
        {
            var id = (uint)(m.UInt(1) ?? 0);
            if (!_channels.TryGetValue(id, out var channel))
            {
                if (_channels.Count >= MostChannels) return;
                _channels[id] = channel = new MumbleChannel { Id = id };
            }

            // ⚠️ Never a loop: a channel inside itself would have anything walking the tree going round for ever.
            if (m.UInt(2) is { } parent && !WouldLoop(id, (uint)parent)) channel.Parent = (uint)parent;
            if (m.Text(3) is { } name) channel.Name = name;
            if (m.Text(5) is { } description) { channel.Description = description; channel.HasDescriptionToAsk = false; }
            if (m.Bytes(10) is { Length: > 0 }) channel.HasDescriptionToAsk = m.Text(5) is null;
            if (m.Bool(8) is { } temporary) channel.Temporary = temporary;
            if (m.UInt(9) is { } position) channel.Position = (int)position;

            // The whole list when it is sent, otherwise what to add and remove.
            if (m.UInts(4).Count > 0) { channel.Links.Clear(); foreach (var l in m.UInts(4)) channel.Links.Add((uint)l); }
            foreach (var l in m.UInts(6)) channel.Links.Add((uint)l);
            foreach (var l in m.UInts(7)) channel.Links.Remove((uint)l);
        }
    }

    /// <summary>Whether this channel is already somewhere above the proposed parent, or is that parent.</summary>
    private bool WouldLoop(uint channel, uint parent)
    {
        var at = parent;
        for (var steps = 0; steps <= MostChannels; steps++)
        {
            if (at == channel) return true;
            if (!_channels.TryGetValue(at, out var above) || above.Parent is not { } next) return false;
            at = next;
        }
        return true;
    }

    private void ApplyUser(PbMessage m)
    {
        lock (_gate)
        {
            var session = (uint)(m.UInt(1) ?? 0);
            if (!_users.TryGetValue(session, out var user))
            {
                if (_users.Count >= MostUsers) return;
                _users[session] = user = new MumbleUser { Session = session };
            }

            if (m.Text(3) is { } name) user.Name = name;
            if (m.UInt(4) is { } userId) user.UserId = (uint)userId;
            if (m.UInt(5) is { } channel) user.ChannelId = (uint)channel;
            if (m.Bool(6) is { } mute) user.Mute = mute;
            if (m.Bool(7) is { } deaf) user.Deaf = deaf;
            if (m.Bool(8) is { } suppress) user.Suppress = suppress;
            if (m.Bool(9) is { } selfMute) user.SelfMute = selfMute;
            if (m.Bool(10) is { } selfDeaf) user.SelfDeaf = selfDeaf;
            if (m.Text(14) is { } comment) { user.Comment = comment; user.HasCommentToAsk = false; }
            if (m.Bytes(16) is { Length: > 0 }) user.HasCommentToAsk = m.Text(14) is null;
            if (m.Bool(18) is { } priority) user.PrioritySpeaker = priority;
            foreach (var c in m.UInts(21)) user.ListeningTo.Add((uint)c);
            foreach (var c in m.UInts(22)) user.ListeningTo.Remove((uint)c);
            if (m.Text(15) is { } hash) user.Hash = hash;

            // A picture comes with the person, or only as its hash until asked for.
            if (m.Bytes(11) is { } texture)
            {
                user.Texture = texture.Length > 0 ? texture : null;
                if (m.Bytes(17) is null) user.TextureHash = "";
                user.HasTextureToAsk = false;
            }
            if (m.Bytes(17) is { } textureHash && Convert.ToHexStringLower(textureHash) is var hex && hex != user.TextureHash)
            {
                user.TextureHash = hex;
                if (m.Bytes(11) is null) { user.Texture = null; user.HasTextureToAsk = hex.Length > 0; }
            }
            if (m.Bool(19) is { } recording) user.Recording = recording;
        }
    }

    // ── Asking the server for things ──────────────────────────────────────────

    public Task JoinChannelAsync(uint channel, CancellationToken ct) =>
        SendAsync(MumbleMessage.UserState, new PbWriter().UInt(1, Session).UInt(5, channel).ToArray(), ct);

    /// <summary>Says this client is muted or deafened, which also stops the server sending it voice when deaf.</summary>
    public Task SetSelfMuteAsync(bool mute, bool deaf, CancellationToken ct) =>
        SendAsync(MumbleMessage.UserState, new PbWriter().UInt(1, Session).Bool(9, mute || deaf).Bool(10, deaf).ToArray(), ct);

    /// <summary>What this client may do in a channel, or null until the server has said. See <see cref="AskPermissions"/>.</summary>
    public MumblePermission? PermissionsIn(uint channel)
    {
        lock (_gate)
        {
            // ⚠️ A server never answers SuperUser's questions: they may do anything, anywhere, so what it said for the root is what holds.
            if (IsSuperUser && _permissions.TryGetValue(0, out var everything)) return everything;
            return _permissions.TryGetValue(channel, out var granted) ? granted : null;
        }
    }

    private bool IsSuperUser => _users.TryGetValue(Session, out var me) && me.UserId == 0;

    /// <summary>Asks once per channel what this client may do there. The answer arrives as <see cref="Changed"/>.</summary>
    public void AskPermissions(uint channel)
    {
        // Takes the lock itself, around asking whether it is known at all.
        lock (_gate)
            if (IsSuperUser || _permissions.ContainsKey(channel) || !_permissionsAsked.Add(channel)) return;

        Quietly(SendAsync(MumbleMessage.PermissionQuery, new PbWriter().UInt(1, channel).ToArray(), CancellationToken.None));
    }

    /// <summary>Asks for a comment too long to have come with the person. The answer arrives as <see cref="Changed"/>.</summary>
    public void AskComment(MumbleUser user)
    {
        lock (_gate)
        {
            if (!user.HasCommentToAsk) return;
            user.HasCommentToAsk = false;
        }
        Quietly(SendAsync(MumbleMessage.RequestBlob, new PbWriter().UInt(2, user.Session).ToArray(), CancellationToken.None));
    }

    /// <summary>The same for a picture. The answer arrives as <see cref="Changed"/>.</summary>
    public void AskTexture(MumbleUser user)
    {
        lock (_gate)
        {
            if (!user.HasTextureToAsk) return;
            user.HasTextureToAsk = false;
        }
        Quietly(SendAsync(MumbleMessage.RequestBlob, new PbWriter().UInt(1, user.Session).ToArray(), CancellationToken.None));
    }

    /// <summary>The same for a channel's description.</summary>
    public void AskDescription(MumbleChannel channel)
    {
        lock (_gate)
        {
            if (!channel.HasDescriptionToAsk) return;
            channel.HasDescriptionToAsk = false;
        }
        Quietly(SendAsync(MumbleMessage.RequestBlob, new PbWriter().UInt(3, channel.Id).ToArray(), CancellationToken.None));
    }

    /// <summary>Asks what the server knows of someone. The answer arrives as <see cref="Changed"/>.</summary>
    public void AskStats(uint session) =>
        Quietly(SendAsync(MumbleMessage.UserStats, new PbWriter().UInt(1, session).ToArray(), CancellationToken.None));

    /// <summary>Asks for the ban list. The answer arrives as <see cref="BansReceived"/>.</summary>
    public Task AskBansAsync(CancellationToken ct) =>
        SendAsync(MumbleMessage.BanList, new PbWriter().Bool(2, true).ToArray(), ct);

    public static byte[] BanListMessage(IEnumerable<MumbleBan> bans)
    {
        var message = new PbWriter();
        foreach (var ban in bans) message.Bytes(1, ban.Raw);
        message.Bool(2, false);
        return message.ToArray();
    }

    /// <summary>⚠️ This is the whole list: a server takes whatever it is sent as all there is, and anyone left off is unbanned.</summary>
    public Task SetBansAsync(IEnumerable<MumbleBan> bans, CancellationToken ct) =>
        SendAsync(MumbleMessage.BanList, BanListMessage(bans), ct);

    /// <summary>Asks for a channel's permissions and groups. The answer arrives as <see cref="AclReceived"/>, or a refusal as <see cref="Denied"/>.</summary>
    public Task AskAclAsync(uint channel, CancellationToken ct) => SendAsync(MumbleMessage.Acl, MumbleAcl.QueryMessage(channel), ct);

    /// <summary>⚠️ Replaces all of the channel's own permissions and groups with these.</summary>
    public Task SetAclAsync(MumbleAcl acl, CancellationToken ct) => SendAsync(MumbleMessage.Acl, acl.ToMessage(), ct);

    /// <summary>Asks who registered users are, by id or by name. The answer arrives as <see cref="RegisteredNamesChanged"/>.</summary>
    public Task AskRegisteredAsync(IEnumerable<uint> ids, IEnumerable<string> names, CancellationToken ct)
    {
        var message = new PbWriter();
        foreach (var id in ids) message.UInt(1, id);
        foreach (var name in names) message.Text(2, name);
        return SendAsync(MumbleMessage.QueryUsers, message.ToArray(), ct);
    }

    /// <summary>Changes a channel's name or description. Whichever is null stays as it is.</summary>
    public Task EditChannelAsync(uint channel, string? name, string? description, CancellationToken ct)
    {
        var state = new PbWriter().UInt(1, channel);
        if (name is not null) state.Text(3, name);
        if (description is not null) state.Text(5, description);
        return SendAsync(MumbleMessage.ChannelState, state.ToArray(), ct);
    }

    // Anything below can be refused, which arrives later as Denied: asking is all these do.

    public static byte[] UserStateMessage(uint session, Action<PbWriter> fill)
    {
        var message = new PbWriter().UInt(1, session);
        fill(message);
        return message.ToArray();
    }

    public static byte[] RemoveUserMessage(uint session, string reason, bool ban)
    {
        var message = new PbWriter().UInt(1, session);
        if (reason.Length > 0) message.Text(3, reason);
        if (ban) message.Bool(4, true);
        return message.ToArray();
    }

    private Task SendUserStateAsync(uint session, Action<PbWriter> fill, CancellationToken ct) =>
        SendAsync(MumbleMessage.UserState, UserStateMessage(session, fill), ct);

    public Task SetServerMuteAsync(uint session, bool mute, CancellationToken ct) => SendUserStateAsync(session, m => m.Bool(6, mute), ct);
    public Task SetServerDeafAsync(uint session, bool deaf, CancellationToken ct) => SendUserStateAsync(session, m => m.Bool(7, deaf), ct);
    public Task SetPrioritySpeakerAsync(uint session, bool priority, CancellationToken ct) => SendUserStateAsync(session, m => m.Bool(18, priority), ct);
    public Task MoveUserAsync(uint session, uint channel, CancellationToken ct) => SendUserStateAsync(session, m => m.UInt(5, channel), ct);

    /// <summary>Ties a name to its certificate on this server, for good unless an admin undoes it.</summary>
    public Task RegisterUserAsync(uint session, CancellationToken ct) => SendUserStateAsync(session, m => m.UInt(4, 0), ct);

    public Task SetCommentAsync(string html, CancellationToken ct) => SendUserStateAsync(Session, m => m.Text(14, html), ct);

    /// <summary>Hears a channel without going there, for this connection.</summary>
    public Task SetListeningAsync(uint channel, bool listen, CancellationToken ct) =>
        SendUserStateAsync(Session, m => m.UInt(listen ? 21 : 22, channel), ct);

    public Task KickOrBanAsync(uint session, string reason, bool ban, CancellationToken ct) =>
        SendAsync(MumbleMessage.UserRemove, RemoveUserMessage(session, reason, ban), ct);

    public Task RemoveChannelAsync(uint channel, CancellationToken ct) =>
        SendAsync(MumbleMessage.ChannelRemove, new PbWriter().UInt(1, channel).ToArray(), ct);

    public Task SetChannelLinkAsync(uint channel, uint with, bool linked, CancellationToken ct) =>
        SendAsync(MumbleMessage.ChannelState, new PbWriter().UInt(1, channel).UInt(linked ? 6 : 7, with).ToArray(), ct);

    public Task SendTextToChannelAsync(uint channel, string message, CancellationToken ct) =>
        SendAsync(MumbleMessage.TextMessage, new PbWriter().UInt(3, channel).Text(5, message).ToArray(), ct);

    /// <summary>To a channel and every channel under it, as far as this user may write in each.</summary>
    public Task SendTextToTreeAsync(uint channel, string message, CancellationToken ct) =>
        SendAsync(MumbleMessage.TextMessage, new PbWriter().UInt(4, channel).Text(5, message).ToArray(), ct);

    public Task SendTextToUserAsync(uint session, string message, CancellationToken ct) =>
        SendAsync(MumbleMessage.TextMessage, new PbWriter().UInt(2, session).Text(5, message).ToArray(), ct);

    /// <summary>
    /// A voice target that reaches this one channel and nothing linked to it or under it. It has to be set again whenever
    /// this client moves, as a target names a channel by id rather than "wherever I am".
    /// </summary>
    public static byte[] ChannelTargetMessage(int targetId, uint channel) =>
        VoiceTargetMessage(targetId, [new MumbleTargetEntry(channel, "", false, false, [])]);

    /// <summary>Saves a voice target, under a number each packet of voice can name. It replaces what that number held.</summary>
    public static byte[] VoiceTargetMessage(int targetId, IEnumerable<MumbleTargetEntry> entries)
    {
        var message = new PbWriter().UInt(1, (ulong)targetId);
        foreach (var entry in entries)
        {
            var target = new PbWriter();
            foreach (var session in entry.Sessions) target.UInt(1, session);
            if (entry.Channel is { } channel) target.UInt(2, channel);
            if (entry.Group.Length > 0) target.Text(3, entry.Group);
            if (entry.Links) target.Bool(4, true);
            if (entry.Subchannels) target.Bool(5, true);
            message.Bytes(2, target.ToArray());
        }
        return message.ToArray();
    }

    public Task SendVoiceTargetAsync(byte[] message, CancellationToken ct) => SendAsync(MumbleMessage.VoiceTarget, message, ct);

    /// <summary>
    /// The other channels that plain talk from this one reaches. The server follows links onward, so a link of a
    /// link counts. Being linked is not always enough: a channel's own permissions can still keep someone from
    /// being heard in it.
    /// </summary>
    public IReadOnlyList<uint> LinkedChannels(uint channel)
    {
        lock (_gate)
        {
            var seen = new HashSet<uint> { channel };
            var waiting = new Queue<uint>();
            waiting.Enqueue(channel);
            var found = new List<uint>();

            while (waiting.TryDequeue(out var at))
            {
                if (!_channels.TryGetValue(at, out var current)) continue;
                foreach (var link in current.Links)
                    if (_channels.ContainsKey(link) && seen.Add(link)) { found.Add(link); waiting.Enqueue(link); }
            }
            return found;
        }
    }

    public Task CreateChannelAsync(uint parent, string name, bool temporary, CancellationToken ct)
    {
        var state = new PbWriter().UInt(2, parent).Text(3, name);
        if (temporary) state.Bool(8, true);
        return SendAsync(MumbleMessage.ChannelState, state.ToArray(), ct);
    }

    /// <summary>A voice frame, over the control connection.</summary>
    public Task SendVoiceAsync(MumbleVoicePacket packet, CancellationToken ct)
    {
        var bytes = packet.Encode();
        return _udp?.TrySend(bytes) == true ? Task.CompletedTask : SendAsync(MumbleMessage.UdpTunnel, bytes, ct);
    }

    /// <summary>Starts on UDP once the server has given the keys. Nothing depends on it working.</summary>
    private void ApplyCrypt(PbMessage m)
    {
        byte[]? key = m.Bytes(1), clientNonce = m.Bytes(2), serverNonce = m.Bytes(3);
        try
        {
            if (key is { Length: > 0 } && clientNonce is not null && serverNonce is not null)
            {
                _crypt.SetKey(key, clientNonce, serverNonce);
                if (!_login.ForceTcp && Interlocked.Exchange(ref _udpOpening, 1) == 0) _ = OpenUdpAsync();
            }
            else if (serverNonce is { Length: > 0 })
            {
                _crypt.SetDecryptNonce(serverNonce);
            }
            else if (_crypt.IsReady)
            {
                // The server has lost track of this client's nonce, and is asking where it has got to.
                Quietly(SendAsync(MumbleMessage.CryptSetup, new PbWriter().Bytes(2, _crypt.EncryptNonce()).ToArray(), CancellationToken.None));
            }
        }
        catch (InvalidDataException) { /* a bad key means no UDP, and TCP carries on */ }
    }

    private async Task OpenUdpAsync()
    {
        var channel = await MumbleUdpChannel.OpenAsync(_login.Host, _login.Port, _crypt, DeliverUdp,
            askForResync: () => Quietly(SendAsync(MumbleMessage.CryptSetup, [], CancellationToken.None)),
            onSilent: TellServerToUseTcp, CancellationToken.None);

        if (channel is null) return;
        if (_closed) { channel.Dispose(); return; }

        _udp = channel;
        if (_closed) channel.Dispose();
    }

    private Task DeliverUdp(byte[] plain)
    {
        if (MumbleVoicePacket.Decode(plain) is { } voice) VoiceReceived?.Invoke(voice);
        return Task.CompletedTask;
    }

    /// <summary>
    /// ⚠️ A server answers on UDP once it has heard this client there, even when its answers are not getting
    /// through, which would leave someone connected and hearing nothing. Anything tunnelled over TCP tells it to
    /// stop, and a ping is the smallest thing to send.
    /// </summary>
    private void TellServerToUseTcp()
    {
        using var to = new MemoryStream(12);
        to.WriteByte(1 << 5);
        MumbleVarint.Write(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Quietly(SendAsync(MumbleMessage.UdpTunnel, to.ToArray(), CancellationToken.None));
    }

    private static void Quietly(Task send) => _ = send.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Who hears what this client says on a target, by the rule the server applies. For display.</summary>
    public IReadOnlyList<MumbleUser> UsersIn(uint channel)
    {
        lock (_gate) return [.. _users.Values.Where(u => u.ChannelId == channel)];
    }

    private void Close()
    {
        _closed = true;
        _udp?.Dispose();
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
