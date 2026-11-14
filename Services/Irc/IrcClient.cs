using System.Globalization;
using System.Net;
using System.Text;

namespace EveConsole.Services.Irc;

/// <summary>How a login proves who it is.</summary>
public enum IrcLoginKind
{
    /// <summary>No password: a nickname only.</summary>
    None = 0,
    /// <summary>SASL PLAIN while connecting, as most networks with accounts expect.</summary>
    Sasl = 1,
    /// <summary>A message to NickServ once connected, for networks without SASL.</summary>
    NickServ = 2,
    /// <summary>The server's own password (PASS), as a private server or a bouncer asks for.</summary>
    ServerPassword = 3,
}

/// <param name="Host">Where to connect.</param>
/// <param name="Tls">Encrypted from the first byte, as on port 6697. ⚠️ Without it no password is ever sent, except to
/// this machine itself.</param>
/// <param name="Account">The account name SASL and NickServ log in as. Blank for the nickname.</param>
public sealed record IrcLogin(
    string Host, int Port, bool Tls, string Nickname, string Username = "", string RealName = "",
    IrcLoginKind LoginKind = IrcLoginKind.None, string Account = "", string Password = "", string TrustedCertificate = "");

/// <summary>
/// The IRC protocol over one connection: logging in, keeping the connection alive, and the commands. What arrives is
/// handed on as it is (<see cref="Received"/>), with the few things every line needs worked out here: when it was said,
/// whether it came back from this client, and which history batch it belongs to.
/// </summary>
/// <remarks>
/// <para>Capabilities (IRCv3) are asked for when the server offers them, and everything works without: a plain server
/// gives plain IRC. server-time dates history; echo-message shows a message once the server has taken it;
/// message-tags carries typing, replies and reactions; chathistory fills what was missed while away.</para>
/// <para>⚠️ A server answers what it is asked in its own time and interleaves everything else, so nothing here waits for
/// an answer except during login.</para>
/// </remarks>
public sealed class IrcClient : IAsyncDisposable
{
    public const string ClientVersion = "EVE Console";

    /// <summary>What is asked for, when offered. sasl is added when a SASL login is wanted.</summary>
    private static readonly string[] Wanted =
    [
        "server-time", "message-tags", "echo-message", "batch", "away-notify", "account-notify", "account-tag",
        "extended-join", "multi-prefix", "userhost-in-names", "chghost", "cap-notify", "invite-notify", "setname",
        "draft/chathistory", "chathistory", "draft/event-playback", "standard-replies", "znc.in/self-message",
        "draft/message-redaction", "draft/read-marker", "draft/multiline", "draft/metadata-2",
    ];

    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(60);

    private readonly IrcLogin _login;
    private readonly IrcConnection _connection = new();
    private readonly HashSet<string> _offered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _batches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Multiline> _multiline = new(StringComparer.Ordinal);

    /// <summary>
    /// One message of several lines (draft/multiline) as it arrives: its batch's opening line carries who said it, to
    /// whom, its id and time; its lines carry the words. A line marked draft/multiline-concat goes on without a break.
    /// </summary>
    private sealed class Multiline(IrcMessage opening)
    {
        private readonly StringBuilder _text = new();
        private string _command = "";
        private string _prefix = "";
        private int _lines;

        /// <summary>Most a message is let hold, whatever the server says: one that goes past this is cut there.</summary>
        private const int MostChars = 64 * 1024;
        private const int MostLines = 1000;

        public void Add(IrcMessage line)
        {
            if (line.Command is not ("PRIVMSG" or "NOTICE") || _lines >= MostLines) return;
            if (_command.Length == 0) { _command = line.Command; _prefix = line.Prefix; }
            var joins = line.Tag("draft/multiline-concat") is not null;
            // A line of spaces is a blank line: some clients send one so, as an empty line can be taken for no message.
            var words = !joins && line.Last.Trim().Length == 0 ? "" : line.Last;
            if (_lines > 0 && !joins) _text.Append('\n');
            if (_text.Length + words.Length > MostChars) words = words[..Math.Max(0, MostChars - _text.Length)];
            _text.Append(words);
            _lines++;
        }

        /// <summary>The whole message, as one line would have carried it. Null when nothing was said in it.</summary>
        /// <remarks>⚠️ To whom is the opening line's third parameter: BATCH +id draft/multiline #target.</remarks>
        public IrcMessage? Joined() => _lines == 0 ? null : new IrcMessage(_command, opening[2], _text.ToString())
        {
            Prefix = opening.Prefix.Length > 0 ? opening.Prefix : _prefix,
            Tags = opening.Tags,
        };
    }
    private DateTime _lastCtcpReply;
    private string? _stsOffer;

    public IrcClient(IrcLogin login) => _login = login;

    public IrcSupport Support { get; } = new();

    /// <summary>The nickname the server knows this connection by right now.</summary>
    public string Nick { get; private set; } = "";

    /// <summary>The account the server says this connection is logged in to. Blank when none.</summary>
    public string LoggedInAs { get; private set; } = "";

    public bool IsEncrypted => _connection.IsEncrypted;

    /// <summary>What the server said on connecting: its name, and its message of the day.</summary>
    public string ServerName { get; private set; } = "";

    public bool Has(string capability) => _enabled.Contains(capability);
    public bool HasTags => Has("message-tags");
    public bool HasEcho => Has("echo-message");
    public bool HasChatHistory => (Has("draft/chathistory") || Has("chathistory")) && Support.ChatHistory > 0;

    /// <summary>Messages can be taken back (REDACT): the sender's own, or by a channel's operators where the server lets them.</summary>
    public bool HasRedact => Has("draft/message-redaction") && HasTags;

    /// <summary>
    /// The server keeps where each conversation was read up to (MARKREAD), shared by every client of the account. Only
    /// when logged in to one: a marker is the account's, and a server may refuse it otherwise.
    /// </summary>
    public bool HasReadMarker => Has("draft/read-marker") && LoggedInAs.Length > 0;

    /// <summary>A message can hold several lines (a draft/multiline batch), within the server's limits.</summary>
    public bool HasMultiline => Has("draft/multiline") && Has("batch") && MultilineMaxBytes > 0;

    /// <summary>The most bytes of words one multiline message may hold, as the server said (max-bytes). 0 when it cannot.</summary>
    public int MultilineMaxBytes { get; private set; }

    /// <summary>The most lines one multiline message may hold (max-lines), or 0 for no limit beyond the bytes.</summary>
    public int MultilineMaxLines { get; private set; }

    /// <summary>
    /// What the server said of encryption (STS) on an encrypted connection: how long it asks to be reached only so. Null
    /// when it said nothing; zero when it takes back a rule given before.
    /// </summary>
    public TimeSpan? StsDuration { get; private set; }

    /// <summary>The server keeps values for people and channels (draft/metadata-2): pictures among them.</summary>
    public bool HasMetadata => Has("draft/metadata-2");

    /// <summary>What the server offered with a capability ("max-bytes=4096,max-lines=100"), for those that say more.</summary>
    private void OfferValue(string cap, string value)
    {
        if (cap == "sts") { _stsOffer = value; return; }
        if (cap != "draft/multiline") return;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0 || !int.TryParse(part[(eq + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) continue;
            switch (part[..eq])
            {
                case "max-bytes": MultilineMaxBytes = Math.Min(n, 64 * 1024); break;
                case "max-lines": MultilineMaxLines = Math.Min(n, 1000); break;
            }
        }
    }

    /// <summary>Every line after logging in, on the reading thread. A handler must not block.</summary>
    public event Action<IrcMessage>? Received;

    /// <summary>Every line, read or written, for a test to follow. Null outside tests.</summary>
    public Action<string, bool>? Trace { get; set; }

    /// <summary>Connects and logs in. Returns once the server has welcomed this client.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _connection.Sent = line => Trace?.Invoke(line, true);
        await _connection.ConnectAsync(_login.Host, _login.Port, _login.Tls, _login.TrustedCertificate, ct);

        if (_login.LoginKind != IrcLoginKind.None && _login.Password.Length > 0 && !_connection.IsEncrypted && !IsThisMachine(_login.Host))
            throw new IrcException(IrcFailure.Refused, "A password is only sent over an encrypted connection. Turn on encryption (TLS) for this server.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginTimeout);
        try { await LogInAsync(timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IrcException(IrcFailure.Network, "The server did not finish logging in in time.");
        }
    }

    public static bool IsThisMachine(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);

    private IEnumerable<string> NickChoices()
    {
        var nick = _login.Nickname.Trim();
        yield return nick;
        yield return nick + "_";
        yield return nick + "__";
        for (var i = 1; i <= 9; i++) yield return nick + i.ToString(CultureInfo.InvariantCulture);
    }

    private async Task LogInAsync(CancellationToken ct)
    {
        var nicks = NickChoices().GetEnumerator();
        nicks.MoveNext();

        await SendRawAsync(new IrcMessage("CAP", "LS", "302"), ct);
        if (_login.LoginKind == IrcLoginKind.ServerPassword && _login.Password.Length > 0)
            await SendRawAsync(new IrcMessage("PASS", _login.Password), ct);
        await SendRawAsync(new IrcMessage("NICK", nicks.Current), ct);
        var user = _login.Username.Trim().Length > 0 ? _login.Username.Trim() : nicks.Current;
        await SendRawAsync(new IrcMessage("USER", Safe(user), "0", "*", _login.RealName.Trim().Length > 0 ? _login.RealName.Trim() : ClientVersion), ct);

        var negotiating = true;
        var saslWanted = _login.LoginKind == IrcLoginKind.Sasl && _login.Password.Length > 0;
        var askedSaslAlone = false;

        while (true)
        {
            var m = await ReadAsync(ct) ?? throw new IrcException(IrcFailure.Network, "The server closed the connection while logging in.");

            switch (m.Command)
            {
                case "PING":
                    await SendRawAsync(new IrcMessage("PONG", m.Last), ct, urgent: true);
                    break;

                case "CAP" when m[1] == "LS":
                    foreach (var cap in m.Last.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var eq = cap.IndexOf('=');
                        _offered.Add(eq < 0 ? cap : cap[..eq]);
                        if (eq > 0) OfferValue(cap[..eq], cap[(eq + 1)..]);
                    }
                    if (m[2] == "*") break;   // more to come

                    CheckSts();
                    var ask = Wanted.Where(_offered.Contains).ToList();
                    if (saslWanted && _offered.Contains("sasl")) ask.Add("sasl");
                    if (ask.Count == 0) { await SendRawAsync(new IrcMessage("CAP", "END"), ct); negotiating = false; }
                    else await SendRawAsync(new IrcMessage("CAP", "REQ", string.Join(' ', ask)), ct);
                    break;

                case "CAP" when m[1] is "ACK" or "NAK":
                    if (m[1] == "ACK")
                        foreach (var cap in m.Last.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                            if (cap.StartsWith('-')) _enabled.Remove(cap[1..]); else _enabled.Add(cap);

                    // ⚠️ A NAK refuses the whole request, for one capability the server dislikes. The login matters most:
                    // asked again on its own before concluding the server cannot do it.
                    if (m[1] == "NAK" && saslWanted && !askedSaslAlone && _offered.Contains("sasl"))
                    {
                        askedSaslAlone = true;
                        await SendRawAsync(new IrcMessage("CAP", "REQ", "sasl"), ct);
                        break;
                    }

                    if (saslWanted && _enabled.Contains("sasl"))
                        await SendRawAsync(new IrcMessage("AUTHENTICATE", "PLAIN"), ct);
                    else
                    {
                        if (saslWanted) throw new IrcException(IrcFailure.Login, "The server does not offer an account login (SASL). Try NickServ instead.");
                        await SendRawAsync(new IrcMessage("CAP", "END"), ct);
                        negotiating = false;
                    }
                    break;

                case "AUTHENTICATE" when m[0] == "+":
                    await SendSaslAsync(ct);
                    break;

                case "900":   // logged in as
                    LoggedInAs = m[2];
                    break;

                case "903":   // SASL worked
                    if (negotiating) { await SendRawAsync(new IrcMessage("CAP", "END"), ct); negotiating = false; }
                    break;

                case "902" or "904" or "905" or "906":
                    throw new IrcException(IrcFailure.Login, m.Last.Length > 0 ? m.Last : "The server refused the login.");

                case "908":   // mechanisms it does take: PLAIN is not one of them
                    throw new IrcException(IrcFailure.Login, "The server's account login does not take a password this way (" + m[1] + ").");

                case "432":
                    // The nickname itself is refused: the same next time, until the person changes it.
                    throw new IrcException(IrcFailure.Nickname, "The server does not accept that nickname: " + m.Last);

                case "433" or "437":   // nickname in use, or held for a while
                    // Every choice taken is worth trying again later: someone's old connection ends in time.
                    if (!nicks.MoveNext()) throw new IrcException(IrcFailure.Network, "Every nickname tried is in use.");
                    await SendRawAsync(new IrcMessage("NICK", nicks.Current), ct);
                    break;

                case "464":
                    throw new IrcException(IrcFailure.Login, "The server refused its password.");

                case "465" or "463":
                    throw new IrcException(IrcFailure.Refused, m.Last.Length > 0 ? m.Last : "The server refused this connection.");

                case "ERROR":
                    // ⚠️ Not a refusal for good: a server says ERROR when it throttles a quick reconnect, times a login
                    // out, or restarts. Tried again later. A real ban says 465 first, which stops above.
                    throw new IrcException(IrcFailure.Network, m.Last.Length > 0 ? m.Last : "The server closed the connection.");

                case "001":
                    Nick = m[0];
                    ServerName = m.Prefix;
                    if (negotiating) { await SendRawAsync(new IrcMessage("CAP", "END"), ct); negotiating = false; }
                    break;

                case "004" when m.Params.Count > 2:   // nick, server, program and version, then mode letters
                    Support.Software = m[2];
                    break;

                case "005":
                    Support.Apply(m.Params.Skip(1).Take(Math.Max(0, m.Params.Count - 2)).ToList());
                    break;

                case "376" or "422":   // end of the message of the day, or none: logged in
                    if (_login.LoginKind == IrcLoginKind.NickServ && _login.Password.Length > 0)
                    {
                        var account = _login.Account.Trim().Length > 0 ? _login.Account.Trim() + " " : "";
                        await SendRawAsync(new IrcMessage("PRIVMSG", "NickServ", "IDENTIFY " + account + _login.Password), ct);
                    }
                    return;
            }
        }
    }

    /// <summary>
    /// Strict transport security: a server can say it is to be reached only encrypted. ⚠️ Said on an unencrypted
    /// connection it is an instruction to go and connect encrypted, now, on the port it names; said on an encrypted one it
    /// is a rule, for how long, to keep for next time. Never kept from an unencrypted connection: anyone in the middle
    /// could have written it.
    /// </summary>
    private void CheckSts()
    {
        if (StsOffer.Parse(_stsOffer) is not { } offer) return;
        if (!_connection.IsEncrypted)
        {
            if (offer.Port is > 0 and <= 65535)
                throw new IrcException(IrcFailure.Upgrade, "The server asks to be reached encrypted.") { UpgradePort = offer.Port };
            return;
        }
        StsDuration = offer.Duration;
    }

    private async Task SendSaslAsync(CancellationToken ct)
    {
        var account = _login.Account.Trim().Length > 0 ? _login.Account.Trim() : _login.Nickname.Trim();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(account + "\0" + account + "\0" + _login.Password));

        // In pieces of 400, and a "+" after a last piece of exactly 400, as the protocol asks.
        for (var i = 0; i < payload.Length; i += 400)
            await SendRawAsync(new IrcMessage("AUTHENTICATE", payload.Substring(i, Math.Min(400, payload.Length - i))), ct);
        if (payload.Length % 400 == 0) await SendRawAsync(new IrcMessage("AUTHENTICATE", "+"), ct);
    }

    /// <summary>How long the server may say nothing before it is asked whether it is still there, and then how long it has to answer.</summary>
    public TimeSpan QuietBeforePing { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan PingAnswerTime { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Most batches open at once: a server that opens them and never closes them cannot make this hold more.</summary>
    private const int MostBatches = 100;

    /// <summary>
    /// Hands each line on and waits while whoever takes them is behind, so a fast server cannot make the lines waiting
    /// to be handled grow without end. Optional: <see cref="Received"/> is told either way.
    /// </summary>
    public Func<IrcMessage, ValueTask>? Deliver { get; set; }

    /// <summary>
    /// Reads lines until the connection ends, handing each on. Returns why it ended.
    /// <para>⚠️ A connection can die without a word: a sleeping laptop, a router's timeout, a change of network. A
    /// server quiet for <see cref="QuietBeforePing"/> is sent a PING, and one that does not answer that in
    /// <see cref="PingAnswerTime"/> is taken for gone, so the connection is made again.</para>
    /// </summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        Task<string?>? pending = null;
        var pinged = false;
        var deadline = DateTime.UtcNow + QuietBeforePing;

        while (true)
        {
            pending ??= _connection.ReadLineAsync(ct);
            var wait = deadline - DateTime.UtcNow;
            if (wait > TimeSpan.Zero && await Task.WhenAny(pending, Task.Delay(wait, ct)) != pending)
                ct.ThrowIfCancellationRequested();

            if (!pending.IsCompleted)
            {
                if (pinged) throw new IrcException(IrcFailure.Network, "The server stopped answering.");
                pinged = true;
                deadline = DateTime.UtcNow + PingAnswerTime;
                _ = SendQuietlyAsync(new IrcMessage("PING", "alive"), urgent: true, ct);
                continue;
            }

            var line = await pending;
            pending = null;
            pinged = false;
            deadline = DateTime.UtcNow + QuietBeforePing;
            if (line is null) return "The server closed the connection.";
            Trace?.Invoke(line, false);
            if (IrcMessage.Parse(line) is not { } m) continue;

            // A line of a message that holds several: gathered, and handed on as one when its batch ends.
            if (m.Tag("batch") is { } inside && _multiline.TryGetValue(inside, out var gathering) && m.Command != "BATCH")
            {
                gathering.Add(m);
                continue;
            }

            switch (m.Command)
            {
                case "PING":
                    await SendRawAsync(new IrcMessage("PONG", m.Last), ct, urgent: true);
                    continue;

                case "PONG":
                    continue;

                case "ERROR":
                    return m.Last.Length > 0 ? m.Last : "The server closed the connection.";

                case "NICK" when Support.Same(m.Nick, Nick):
                    Nick = m[0];
                    break;

                case "005":
                    Support.Apply(m.Params.Skip(1).Take(Math.Max(0, m.Params.Count - 2)).ToList());
                    break;

                case "900":
                    LoggedInAs = m[2];
                    break;

                case "901":
                    LoggedInAs = "";
                    break;

                case "CAP" when m[1] is "NEW" or "DEL" or "ACK":
                    OnCapChange(m, ct);
                    continue;

                case "BATCH":
                    var reference = m[0];
                    if (reference.StartsWith('+') && reference.Length > 1 && _batches.Count < MostBatches)
                    {
                        _batches[reference[1..]] = m[1];
                        if (m[1] == "draft/multiline" && m.Params.Count > 2) { _multiline[reference[1..]] = new Multiline(m); continue; }
                    }
                    else if (reference.StartsWith('-'))
                    {
                        // ⚠️ Joined before this batch is forgotten: the whole message is judged by the batch it sits in.
                        if (_multiline.Remove(reference[1..], out var whole))
                        {
                            _batches.Remove(reference[1..]);
                            if (whole.Joined() is not { } joined) continue;
                            m = joined;
                            break;
                        }
                        _batches.Remove(reference[1..]);
                    }
                    break;

                case "PRIVMSG" when IsCtcp(m.Last, out var ctcp, out var rest) && ctcp != "ACTION":
                    AnswerCtcp(m, ctcp, rest, ct);
                    continue;
            }

            // ⚠️ Here, as it is read: see IrcMessage.Batch.
            m.Batch = m.Tag("batch") is { } batchId && _batches.TryGetValue(batchId, out var batchKind) ? batchKind : "";

            if (Deliver is { } deliver) await deliver(m);

            try { Received?.Invoke(m); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException)
            {
                // One line the handler could not take must not end the connection.
                Trace?.Invoke("handler: " + e.Message, false);
            }
        }
    }

    private void OnCapChange(IrcMessage m, CancellationToken ct)
    {
        if (m[1] == "NEW")
            foreach (var offer in m.Last.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (offer.IndexOf('=') is var eq and > 0) OfferValue(offer[..eq], offer[(eq + 1)..]);
        var caps = m.Last.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Split('=')[0]).ToList();
        if (m[1] == "DEL") { foreach (var c in caps) { _offered.Remove(c); _enabled.Remove(c); } return; }
        if (m[1] == "ACK") { foreach (var c in caps) if (c.StartsWith('-')) _enabled.Remove(c[1..]); else _enabled.Add(c); return; }

        foreach (var c in caps) _offered.Add(c);
        var ask = caps.Where(c => Wanted.Contains(c) && !_enabled.Contains(c)).ToList();
        if (ask.Count > 0) _ = SendQuietlyAsync(new IrcMessage("CAP", "REQ", string.Join(' ', ask)), urgent: false, ct);
    }

    /// <summary>
    /// Sends without the reading thread waiting for it. ⚠️ A send can wait its turn for seconds behind a long paste, and
    /// reading must not stop meanwhile: the server's PINGs would go unanswered and it would drop the connection.
    /// </summary>
    private async Task SendQuietlyAsync(IrcMessage m, bool urgent, CancellationToken ct)
    {
        try { await SendRawAsync(m, ct, urgent); }
        catch (Exception e) when (e is IrcException or OperationCanceledException or ObjectDisposedException) { }
    }

    // ── What every line needs worked out ──────────────────────────────────────

    /// <summary>The kind of history batch a line belongs to ("chathistory", "znc.in/playback"...), or blank.</summary>
    public static string BatchOf(IrcMessage m) => m.Batch;

    /// <summary>When it was said: the server's time where it gives one, otherwise now.</summary>
    public static DateTimeOffset TimeOf(IrcMessage m) =>
        m.Tag("time") is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : DateTimeOffset.UtcNow;

    /// <summary>A line this client sent, come back from the server.</summary>
    public bool IsFromMe(IrcMessage m) => m.Nick.Length > 0 && Support.Same(m.Nick, Nick);

    /// <summary>A CTCP request or answer (\x01WORD rest\x01), as ACTION for /me.</summary>
    public static bool IsCtcp(string text, out string command, out string rest)
    {
        command = rest = "";
        if (text.Length < 2 || text[0] != '\x01') return false;
        var body = text[1..].TrimEnd('\x01');
        var space = body.IndexOf(' ');
        command = (space < 0 ? body : body[..space]).ToUpperInvariant();
        rest = space < 0 ? "" : body[(space + 1)..];
        return command.Length > 0;
    }

    private void AnswerCtcp(IrcMessage m, string command, string rest, CancellationToken ct)
    {
        // Only to a person, and not too often: a channel full of people asking at once is a flood.
        if (Support.IsChannel(m[0]) || DateTime.UtcNow - _lastCtcpReply < TimeSpan.FromSeconds(2)) return;
        var answer = command switch
        {
            "VERSION" => "VERSION " + ClientVersion,
            "PING" => "PING " + rest,
            "CLIENTINFO" => "CLIENTINFO ACTION CLIENTINFO PING VERSION",
            _ => null,
        };
        if (answer is null) return;
        _lastCtcpReply = DateTime.UtcNow;
        _ = SendQuietlyAsync(new IrcMessage("NOTICE", m.Nick, "\x01" + answer + "\x01"), urgent: false, ct);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task<IrcMessage?> ReadAsync(CancellationToken ct)
    {
        while (true)
        {
            var line = await _connection.ReadLineAsync(ct);
            if (line is null) return null;
            Trace?.Invoke(line, false);
            if (IrcMessage.Parse(line) is { } m) return m;
        }
    }

    public Task SendRawAsync(IrcMessage m, CancellationToken ct, bool urgent = false) => _connection.SendAsync(m, urgent, ct);

    /// <summary>A word that goes in the middle of a line: no spaces, and not starting with ':'.</summary>
    private static string Safe(string word)
    {
        word = word.Replace(' ', '_');
        return word.TrimStart(':') is { Length: > 0 } w ? w : "user";
    }

    public Task JoinAsync(string channel, string key, CancellationToken ct) =>
        SendRawAsync(key.Length > 0 ? new IrcMessage("JOIN", channel, key) : new IrcMessage("JOIN", channel), ct);

    public Task PartAsync(string channel, string reason, CancellationToken ct) =>
        SendRawAsync(reason.Length > 0 ? new IrcMessage("PART", channel, reason) : new IrcMessage("PART", channel), ct);

    /// <summary>
    /// Sends words to a channel or person, as as many lines as they need. A line break in them starts a new message;
    /// a long one is split between words, short enough that the server can pass it on with the sender's name in front.
    /// </summary>
    /// <param name="replyTo">The message these answer, by its id, where the server passes tags on.</param>
    public async Task<IReadOnlyList<string>> SayAsync(string target, string text, bool action, string replyTo, CancellationToken ct)
    {
        // Several lines, where the server takes them as one message: each line is a line of it, not a message of its own.
        if (!action && HasMultiline && Lines(text).Count > 1) return await SayMultilineAsync(target, text, replyTo, ct);

        var sent = new List<string>();
        foreach (var piece in Split(target, text, action))
        {
            var body = action ? "\x01" + "ACTION " + piece + "\x01" : piece;
            var tags = new Dictionary<string, string>();
            if (replyTo.Length > 0 && HasTags) tags["+draft/reply"] = replyTo;
            await SendRawAsync(new IrcMessage("PRIVMSG", target, body) { Tags = tags }, ct);
            sent.Add(piece);
            replyTo = "";   // the first piece answers; the rest follow it
        }
        return sent;
    }

    public Task NoticeAsync(string target, string text, CancellationToken ct) =>
        SendRawAsync(new IrcMessage("NOTICE", target, text), ct);

    /// <summary>The words split to fit, in order: by line, then by length.</summary>
    public IEnumerable<string> Split(string target, string text, bool action)
    {
        var most = MostWords(target, action);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            foreach (var piece in Fit(line, most)) yield return piece;
        }
    }

    /// <summary>The most bytes of words one line can carry, leaving room for what the server puts in front when it passes it on.</summary>
    private int MostWords(string target, bool action)
    {
        // ":nick!user@host PRIVMSG target :"
        var overhead = 1 + Math.Max(Nick.Length, Support.NickLength) + 1 + 10 + 1 + 63 + " PRIVMSG ".Length + target.Length + 2 + 2;
        if (action) overhead += "ACTION ".Length + 2;   // and the two \x01 around it
        return Math.Max(60, IrcMessage.MaxOutgoingBytes - overhead);
    }

    /// <summary>One line cut into pieces of at most so many bytes, between words where it can be.</summary>
    private static IEnumerable<string> Fit(string line, int most, bool keepSpace = false)
    {
        while (Encoding.UTF8.GetByteCount(line) > most)
        {
            var cut = 0;
            var lastSpace = -1;
            while (cut < line.Length)
            {
                var next = cut + (char.IsHighSurrogate(line[cut]) && cut + 1 < line.Length ? 2 : 1);
                if (Encoding.UTF8.GetByteCount(line.AsSpan(0, next)) > most) break;
                if (line[cut] == ' ') lastSpace = cut;
                cut = next;
            }
            var at = lastSpace > most / 2 ? lastSpace : cut;
            // ⚠️ Pieces joined again without a break (multiline-concat) keep the space they were cut at, or two words become one.
            if (keepSpace && at == lastSpace)
            {
                yield return line[..(at + 1)];
                line = line[(at + 1)..];
                continue;
            }
            yield return line[..at];
            line = line[at..].TrimStart();
        }
        if (line.Length > 0) yield return line;
    }

    /// <summary>The lines of what was typed, line ends trimmed, without blank lines at the start or end.</summary>
    private static List<string> Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private int _batchCount;

    /// <summary>
    /// Sends words of several lines as one message (a draft/multiline batch): a line too long for the wire goes in
    /// pieces marked to be joined again without a break. Past the server's limits it goes as several such messages.
    /// Returns each message sent, its lines joined.
    /// </summary>
    private async Task<IReadOnlyList<string>> SayMultilineAsync(string target, string text, string replyTo, CancellationToken ct)
    {
        var most = MostWords(target, action: false);
        var messages = new List<List<(string Piece, bool Joins)>>();
        var current = new List<(string Piece, bool Joins)>();
        var bytes = 0;
        var lines = 0;

        foreach (var line in Lines(text))
        {
            // A blank line goes as an empty line of the batch, which the spec allows.
            List<string> pieces = line.Length == 0 ? [""] : [.. Fit(line, most, keepSpace: true)];
            var size = pieces.Sum(p => Encoding.UTF8.GetByteCount(p)) + 1;
            if (current.Count > 0 && (bytes + size > MultilineMaxBytes || MultilineMaxLines > 0 && lines + pieces.Count > MultilineMaxLines))
            {
                messages.Add(current);
                current = [];
                bytes = lines = 0;
            }
            for (var i = 0; i < pieces.Count; i++) current.Add((pieces[i], i > 0));
            bytes += size;
            lines += pieces.Count;
        }
        if (current.Count > 0) messages.Add(current);

        var sent = new List<string>();
        foreach (var message in messages)
        {
            var reference = "ml" + Interlocked.Increment(ref _batchCount).ToString(CultureInfo.InvariantCulture);
            var opening = new Dictionary<string, string>();
            if (replyTo.Length > 0 && HasTags) opening["+draft/reply"] = replyTo;
            var batch = new List<IrcMessage> { new("BATCH", "+" + reference, "draft/multiline", target) { Tags = opening } };
            foreach (var (piece, joins) in message)
            {
                var tags = new Dictionary<string, string> { ["batch"] = reference };
                if (joins) tags["draft/multiline-concat"] = "";
                batch.Add(new IrcMessage("PRIVMSG", target, piece) { Tags = tags });
            }
            batch.Add(new IrcMessage("BATCH", "-" + reference));
            await _connection.SendAllAsync(batch, ct);

            var whole = new StringBuilder();
            for (var i = 0; i < message.Count; i++)
            {
                if (i > 0 && !message[i].Joins) whole.Append('\n');
                whole.Append(message[i].Piece);
            }
            sent.Add(whole.ToString());
            replyTo = "";   // the first answers; the rest follow it
        }
        return sent;
    }

    /// <summary>A message without words, carrying tags only: typing, a reaction. Only where the server passes tags on.</summary>
    public Task TagAsync(string target, Dictionary<string, string> tags, CancellationToken ct) =>
        HasTags ? SendRawAsync(new IrcMessage("TAGMSG", target) { Tags = tags }, ct) : Task.CompletedTask;

    public Task TypingAsync(string target, string state, CancellationToken ct) =>
        TagAsync(target, new Dictionary<string, string> { ["+typing"] = state }, ct);

    /// <summary>Adds a reaction to a message, or takes one back (+draft/unreact), as clients that show them do.</summary>
    public Task ReactAsync(string target, string msgid, string emoji, bool takeBack, CancellationToken ct) =>
        TagAsync(target, new Dictionary<string, string> { [takeBack ? "+draft/unreact" : "+draft/react"] = emoji, ["+draft/reply"] = msgid }, ct);

    /// <summary>Takes a message back for everyone (REDACT), with why if a reason is given. Only where the server can.</summary>
    public Task RedactAsync(string target, string msgid, string reason, CancellationToken ct) =>
        !HasRedact || msgid.Length == 0 ? Task.CompletedTask
        : SendRawAsync(reason.Length > 0 ? new IrcMessage("REDACT", target, msgid, reason) : new IrcMessage("REDACT", target, msgid), ct);

    /// <summary>Tells the server a conversation has been read up to a time (MARKREAD), for every client of the account.</summary>
    public Task MarkReadAsync(string target, DateTimeOffset upTo, CancellationToken ct) =>
        !HasReadMarker ? Task.CompletedTask
        : SendRawAsync(new IrcMessage("MARKREAD", target, "timestamp=" + upTo.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)), ct);

    /// <summary>Asks where a conversation was read up to. The answer comes as a MARKREAD line.</summary>
    public Task AskReadMarkerAsync(string target, CancellationToken ct) =>
        !HasReadMarker ? Task.CompletedTask : SendRawAsync(new IrcMessage("MARKREAD", target), ct);

    public Task TopicAsync(string channel, string topic, CancellationToken ct) => SendRawAsync(new IrcMessage("TOPIC", channel, topic), ct);

    public Task ModeAsync(string target, string modes, params string[] values) =>
        SendRawAsync(new IrcMessage("MODE", [target, modes, .. values]), CancellationToken.None);

    public Task KickAsync(string channel, string nick, string reason, CancellationToken ct) =>
        SendRawAsync(reason.Length > 0 ? new IrcMessage("KICK", channel, nick, reason) : new IrcMessage("KICK", channel, nick), ct);

    public Task InviteAsync(string nick, string channel, CancellationToken ct) => SendRawAsync(new IrcMessage("INVITE", nick, channel), ct);

    /// <summary>Sets this person away with the words given, or back with none.</summary>
    public Task AwayAsync(string message, CancellationToken ct) =>
        SendRawAsync(message.Length > 0 ? new IrcMessage("AWAY", message) : new IrcMessage("AWAY"), ct);

    public Task NickAsync(string nick, CancellationToken ct) => SendRawAsync(new IrcMessage("NICK", nick), ct);

    public Task ListAsync(CancellationToken ct) => SendRawAsync(new IrcMessage("LIST"), ct);

    public Task WhoAsync(string target, CancellationToken ct) => SendRawAsync(new IrcMessage("WHO", target), ct);

    /// <summary>
    /// Asks the server who someone is. Their nickname twice: that asks the server they are connected to, which is the
    /// one that knows how long they have been idle.
    /// </summary>
    public Task WhoisAsync(string nick, CancellationToken ct) => SendRawAsync(new IrcMessage("WHOIS", nick, nick), ct);

    /// <summary>The keys of people's values this client wants told about as they change: their pictures.</summary>
    public Task SubscribeMetadataAsync(CancellationToken ct) =>
        HasMetadata ? SendRawAsync(new IrcMessage("METADATA", "*", "SUB", "avatar"), ct) : Task.CompletedTask;

    /// <summary>Asks for someone's picture (or a channel's). The answer is a 761, or a 766 for none.</summary>
    public Task AskAvatarAsync(string target, CancellationToken ct) =>
        HasMetadata ? SendRawAsync(new IrcMessage("METADATA", target, "GET", "avatar"), ct) : Task.CompletedTask;

    /// <summary>Sets this person's picture, as a web link, or takes it away with a blank one.</summary>
    public Task SetAvatarAsync(string url, CancellationToken ct) =>
        !HasMetadata ? Task.CompletedTask
        : SendRawAsync(url.Length > 0 ? new IrcMessage("METADATA", "*", "SET", "avatar", url) : new IrcMessage("METADATA", "*", "SET", "avatar"), ct);

    /// <summary>Asks for what was said in a channel or with a person after a time, where the server keeps history.</summary>
    public Task HistoryAfterAsync(string target, DateTimeOffset after, int limit, CancellationToken ct) =>
        SendRawAsync(new IrcMessage("CHATHISTORY", "AFTER", target, "timestamp=" + after.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Math.Min(limit, Support.ChatHistory).ToString(CultureInfo.InvariantCulture)), ct);

    public Task HistoryLatestAsync(string target, int limit, CancellationToken ct) =>
        SendRawAsync(new IrcMessage("CHATHISTORY", "LATEST", target, "*", Math.Min(limit, Support.ChatHistory).ToString(CultureInfo.InvariantCulture)), ct);

    /// <summary>The conversations with people the server has history for since a time (CHATHISTORY TARGETS).</summary>
    public Task HistoryTargetsAsync(DateTimeOffset from, CancellationToken ct) =>
        SendRawAsync(new IrcMessage("CHATHISTORY", "TARGETS",
            "timestamp=" + from.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            "timestamp=" + DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            Math.Min(50, Support.ChatHistory).ToString(CultureInfo.InvariantCulture)), ct);

    /// <summary>Watches names for coming online and going offline (MONITOR), where the server can.</summary>
    /// <remarks>⚠️ In lines of a few hundred bytes: one line holding them all would be cut at the line's limit, mid-name.</remarks>
    public async Task MonitorAsync(IEnumerable<string> nicks, CancellationToken ct)
    {
        if (Support.Monitor <= 0) return;
        var line = new StringBuilder();
        foreach (var nick in nicks.Where(n => n.Length > 0 && !n.Contains(',') && !n.Contains(' ')).Distinct().Take(Support.Monitor))
        {
            if (line.Length > 0 && line.Length + nick.Length + 1 > 400)
            {
                await SendRawAsync(new IrcMessage("MONITOR", "+", line.ToString()), ct);
                line.Clear();
            }
            if (line.Length > 0) line.Append(',');
            line.Append(nick);
        }
        if (line.Length > 0) await SendRawAsync(new IrcMessage("MONITOR", "+", line.ToString()), ct);
    }

    public async Task QuitAsync(string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await SendRawAsync(new IrcMessage("QUIT", reason), cts.Token, urgent: true);
        }
        catch (Exception e) when (e is IrcException or OperationCanceledException or ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>What a server offered as "sts": a port to go to encrypted, and how long to keep to encryption.</summary>
public sealed record StsOffer(int Port, TimeSpan? Duration)
{
    /// <summary>Most a rule is kept for, whatever the server says: a typo of a few zeroes must not hold for centuries.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(400);

    /// <summary>"port=6697,duration=2592000,preload" as its parts. Null for nothing usable.</summary>
    public static StsOffer? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var port = 0;
        TimeSpan? duration = null;
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            var number = part[(eq + 1)..];
            switch (part[..eq])
            {
                case "port" when int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var p):
                    port = p;
                    break;
                case "duration" when long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds):
                    duration = TimeSpan.FromSeconds(Math.Min(seconds, (long)Longest.TotalSeconds));
                    break;
            }
        }
        return port == 0 && duration is null ? null : new StsOffer(port, duration);
    }
}
