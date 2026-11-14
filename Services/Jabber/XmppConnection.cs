using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace EveConsole.Services.Jabber;

/// <summary>Why a connection could not be made or was lost, for the words shown and for whether to retry.</summary>
public enum XmppFailure
{
    /// <summary>The network or the server went away. Worth trying again.</summary>
    Network,
    /// <summary>The server offers no encryption. Never retried in the clear.</summary>
    NoEncryption,
    /// <summary>The certificate is not trusted and has not been accepted.</summary>
    Certificate,
    /// <summary>The server refused the name or password, or could not prove its own identity.</summary>
    Login,
    /// <summary>The same account connected elsewhere with the same resource and this one was replaced.</summary>
    Replaced,
    /// <summary>The server spoke something this client cannot follow.</summary>
    Protocol,
}

public sealed class XmppException(XmppFailure failure, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public XmppFailure Failure { get; } = failure;

    /// <summary>For <see cref="XmppFailure.Certificate"/>: what the person is being asked to accept.</summary>
    public string? CertificateFingerprint { get; init; }
    public string? CertificateSubject     { get; init; }
    public string? CertificateProblem     { get; init; }
}

/// <param name="Address">The account, as <c>name@domain</c>.</param>
/// <param name="Host">Where to connect when it is not the domain itself. Blank for the domain.</param>
/// <param name="TrustedCertificate">SHA-256 fingerprint of a certificate the person has accepted.</param>
/// <param name="DirectTls">Encrypted from the first byte, as on port 5223, rather than STARTTLS.</param>
public sealed record XmppLogin(
    string Address, string Password, string Host = "", int Port = 5222, string Resource = "",
    string TrustedCertificate = "", bool DirectTls = false);

/// <summary>
/// What picking a stream up again takes (XEP-0198), kept from the connection that dropped: the server's name for it,
/// the address it had, how much came in, and what went out that the server had not yet said it got.
/// </summary>
public sealed class XmppStreamResume
{
    internal string Id { get; init; } = "";
    internal XmppAddress Bound { get; init; }
    internal uint Inbound { get; init; }
    internal uint Outbound { get; init; }
    internal IReadOnlyList<(uint Seq, XElement Stanza)> Unacked { get; init; } = [];
}

/// <summary>
/// One encrypted XMPP stream, up to the point stanzas can flow: connect, STARTTLS, log in, bind.
///
/// <para>Then, where the server offers it, stream management (XEP-0198): each side counts what it has had from the
/// other and says so when asked, so what went out unconfirmed is known, and a stream whose connection dropped can be
/// picked up again a moment later as it was, rooms still joined, with what was missed meanwhile delivered.</para>
///
/// <para>⚠️ Encryption is required, always. A server offering none gets no credentials, and a connection
/// the certificate check rejects sends nothing further. A self-signed certificate is common on group
/// servers, so a person may accept one by its fingerprint: from then on only that one passes.</para>
/// </summary>
public sealed class XmppConnection : IAsyncDisposable
{
    public static readonly XNamespace Client  = "jabber:client";
    public static readonly XNamespace Streams = "http://etherx.jabber.org/streams";
    public static readonly XNamespace Tls     = "urn:ietf:params:xml:ns:xmpp-tls";
    public static readonly XNamespace Sasl    = "urn:ietf:params:xml:ns:xmpp-sasl";
    public static readonly XNamespace BindNs  = "urn:ietf:params:xml:ns:xmpp-bind";
    public static readonly XNamespace Session = "urn:ietf:params:xml:ns:xmpp-session";
    public static readonly XNamespace Sm      = "urn:xmpp:sm:3";

    /// <summary>The most kept unconfirmed. A server that never says what it got cannot make this hold more.</summary>
    private const int MostUnacked = 500;

    /// <summary>Largest stanza accepted. A server, or anyone it relays, cannot make this client hold more.</summary>
    private const long MaxStanzaBytes = 2 * 1024 * 1024;

    /// <summary>Deeper than any real stanza, and a limit on what a hostile one can make this build.</summary>
    private const int MaxDepth = 64;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Strongest first. PLAIN is only reached with the encryption already required above it.</summary>
    private static readonly string[] Preferred = ["SCRAM-SHA-512", "SCRAM-SHA-256", "SCRAM-SHA-1", "PLAIN"];

    private readonly XmppLogin _login;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private TcpClient? _tcp;
    private Stream? _stream;
    private LimitedStream? _limited;
    private XmlReader? _reader;

    private readonly XmppStreamResume? _resumeFrom;
    private readonly object _smGate = new();
    private readonly Queue<(uint Seq, XElement Stanza)> _unacked = new();
    private List<XElement> _unsent = [];
    private volatile bool _counting;
    private string _smId = "";
    private uint _inbound, _outbound;

    /// <param name="resume">The stream of a connection that dropped, to pick up again if the server still has it.</param>
    public XmppConnection(XmppLogin login, XmppStreamResume? resume = null)
    {
        _login = login;
        _resumeFrom = resume;
    }

    /// <summary>This connection picked up the stream of one that dropped: everything joined then still is.</summary>
    public bool Resumed { get; private set; }

    /// <summary>The server is counting with this client, so what went out unconfirmed is known.</summary>
    public bool Counting => _counting;

    /// <summary>What picking this stream up again would take, or null where the server will not keep it.</summary>
    public XmppStreamResume? Resumable
    {
        get
        {
            lock (_smGate)
                return _counting && _smId.Length > 0
                    ? new XmppStreamResume { Id = _smId, Bound = Bound, Inbound = _inbound, Outbound = _outbound, Unacked = [.. _unacked] }
                    : null;
        }
    }

    /// <summary>
    /// What a dropped connection sent that its server never confirmed, when this one could not pick its stream up:
    /// for the caller to send again once the new session is set up, if it should be. Given once.
    /// </summary>
    public IReadOnlyList<XElement> TakeUnsent()
    {
        var unsent = _unsent;
        _unsent = [];
        return unsent;
    }

    /// <summary>The address the server gave this connection, with its resource.</summary>
    public XmppAddress Bound { get; private set; }

    /// <summary>The features offered once logged in, which say what the server can do.</summary>
    public XElement? Features { get; private set; }

    public async Task ConnectAsync(CancellationToken ct)
    {
        var address = XmppAddress.Parse(_login.Address);
        if (address.Local.Length == 0 || address.Domain.Length == 0)
            throw new XmppException(XmppFailure.Login, "The address must look like name@domain.");

        // A host someone typed is the only place to go. Otherwise the domain's DNS says where its server
        // is, and each is tried in turn: only a place that could not be reached moves on to the next. A
        // refused password or an untrusted certificate would say the same from all of them.
        IReadOnlyList<XmppEndpoint> endpoints = _login.Host.Trim().Length > 0
            ? [new XmppEndpoint(_login.Host.Trim(), _login.Port, _login.DirectTls)]
            : await XmppDns.FindAsync(address.Domain, ct);

        for (var i = 0; ; i++)
        {
            try
            {
                await ConnectToAsync(address, endpoints[i], ct);
                return;
            }
            catch (XmppException e) when (e.Failure == XmppFailure.Network && i < endpoints.Count - 1 && !ct.IsCancellationRequested)
            {
                CloseSocket();
            }
        }
    }

    private async Task ConnectToAsync(XmppAddress address, XmppEndpoint endpoint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        // ⚠️ Reads block inside the XML reader, which never looks at a token. Closing the socket is the
        // one thing that wakes them, so a hung server cannot hold this past the timeout.
        await using var abort = timeout.Token.Register(CloseSocket);

        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            _stream = _tcp.GetStream();

            if (endpoint.DirectTls) await StartTlsAsync(address.Domain, timeout.Token);

            var features = await OpenStreamAsync(address.Domain, timeout.Token);

            if (_stream is not SslStream)
            {
                if (features.Element(Tls + "starttls") is null)
                    throw new XmppException(XmppFailure.NoEncryption, "The server does not offer encryption.");

                await WriteAsync(new XElement(Tls + "starttls"), timeout.Token);
                var answer = await ReadStanzaAsync(timeout.Token)
                             ?? throw new XmppException(XmppFailure.Network, "The server closed the connection.");
                if (answer.Name != Tls + "proceed")
                    throw new XmppException(XmppFailure.NoEncryption, "The server refused to start encryption.");

                await StartTlsAsync(address.Domain, timeout.Token);
                features = await OpenStreamAsync(address.Domain, timeout.Token);
            }

            await LogInAsync(address, features, timeout.Token);
            features = await OpenStreamAsync(address.Domain, timeout.Token);
            Features = features;

            var managed = features.Element(Sm + "sm") is not null;
            if (managed && _resumeFrom is { } from && await ResumeAsync(from, timeout.Token)) return;

            await BindAsync(features, timeout.Token);
            if (managed) await EnableAsync(timeout.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is not XmppException || timeout.IsCancellationRequested))
        {
            CloseSocket();
            throw new XmppException(XmppFailure.Network, timeout.IsCancellationRequested
                ? "The server did not answer in time." : e.Message, e);
        }
    }

    // ── Encryption ────────────────────────────────────────────────────────────

    private async Task StartTlsAsync(string domain, CancellationToken ct)
    {
        string? fingerprint = null, subject = null, problem = null;

        var tls = new SslStream(_stream!, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            // The name to check is the account's domain, whichever host was connected to: an alternate
            // host is only a route, and the certificate must still belong to the domain.
            TargetHost = domain,
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (certificate is null) { problem = errors.ToString(); return false; }

                fingerprint = Fingerprint(certificate);
                subject     = certificate.Subject;
                problem     = errors.ToString();

                return string.Equals(fingerprint, _login.TrustedCertificate, StringComparison.OrdinalIgnoreCase);
            },
        };

        try
        {
            await tls.AuthenticateAsClientAsync(options, ct);
        }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException && fingerprint is not null)
        {
            throw new XmppException(XmppFailure.Certificate, "The server's certificate is not trusted.", e)
            {
                CertificateFingerprint = fingerprint,
                CertificateSubject     = subject,
                CertificateProblem     = problem,
            };
        }

        _stream = tls;
    }

    /// <summary>SHA-256 of the whole certificate, as hex.</summary>
    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    // ── Stream ────────────────────────────────────────────────────────────────

    /// <summary>Says hello, and returns what the server offers. Done afresh after encryption and login.</summary>
    private async Task<XElement> OpenStreamAsync(string domain, CancellationToken ct)
    {
        _limited = new LimitedStream(_stream!, MaxStanzaBytes);

        var hello = $"<?xml version='1.0'?><stream:stream to='{Escape(domain)}' version='1.0' "
                  + "xmlns='jabber:client' xmlns:stream='http://etherx.jabber.org/streams'>";
        await WriteRawAsync(hello, ct);

        _reader?.Dispose();
        _reader = XmlReader.Create(_limited, new XmlReaderSettings
        {
            Async            = true,
            ConformanceLevel = ConformanceLevel.Fragment,
            // ⚠️ No DTDs, no external anything: this XML comes from a stranger on the network.
            DtdProcessing    = DtdProcessing.Prohibit,
            XmlResolver      = null,
            MaxCharactersFromEntities = 1024,
            CloseInput       = false,
            IgnoreComments   = true,
            IgnoreProcessingInstructions = true,
        });

        while (await _reader.ReadAsync())
        {
            if (_reader.NodeType == XmlNodeType.Element
                && _reader.LocalName == "stream" && _reader.NamespaceURI == Streams.NamespaceName)
            {
                var first = await ReadStanzaAsync(ct)
                            ?? throw new XmppException(XmppFailure.Network, "The server closed the connection.");

                if (first.Name == Streams + "error") throw StreamError(first);
                if (first.Name != Streams + "features")
                    throw new XmppException(XmppFailure.Protocol, "The server sent no list of features.");

                return first;
            }
        }

        throw new XmppException(XmppFailure.Network, "The server closed the connection.");
    }

    /// <summary>
    /// The next thing the server sent, or null when it ended the stream. ⚠️ Stream management's own messages are
    /// answered and taken here, and each stanza counted as it is handed on: the count is what says it arrived.
    /// </summary>
    public async Task<XElement?> ReadStanzaAsync(CancellationToken ct)
    {
        while (true)
        {
            var stanza = await ReadNextAsync(ct);
            if (stanza is null || !_counting) return stanza;

            if (stanza.Name == Sm + "r")
            {
                uint handled;
                lock (_smGate) handled = _inbound;
                await WriteAsync(new XElement(Sm + "a", new XAttribute("h", handled)), ct);
                continue;
            }
            if (stanza.Name == Sm + "a")
            {
                if (uint.TryParse((string?)stanza.Attribute("h"), out var h)) Acked(h);
                continue;
            }

            if (stanza.Name.Namespace == Client) lock (_smGate) _inbound++;
            return stanza;
        }
    }

    private async Task<XElement?> ReadNextAsync(CancellationToken ct)
    {
        try
        {
            var reader = _reader ?? throw new InvalidOperationException("Not connected.");
            ct.ThrowIfCancellationRequested();

            while (await reader.ReadAsync())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        _limited?.Reset();
                        return await ReadElementAsync(reader);

                    case XmlNodeType.EndElement:
                        return null;   // the server closed the stream

                    case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        continue;      // its keep-alive: nothing to hand on

                    default:
                        throw new XmppException(XmppFailure.Protocol, $"Unexpected {reader.NodeType} between stanzas.");
                }
            }
            return null;
        }
        catch (Exception e) when (e is XmlException or IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            throw new XmppException(e is XmlException ? XmppFailure.Protocol : XmppFailure.Network, e.Message, e);
        }
    }

    /// <summary>
    /// The element the reader is on, read up to its own end tag and not one node further.
    ///
    /// <para>⚠️ Why not <see cref="XNode.ReadFromAsync"/>: it moves past the end tag, and on a live stream
    /// that means waiting for the server's next data. The server is usually waiting for us, so a login
    /// hung for its whole timeout on the very first list of features.</para>
    /// </summary>
    private static async Task<XElement> ReadElementAsync(XmlReader reader)
    {
        var root = StartElement(reader, out var empty);
        if (empty) return root;

        var current = root;
        var depth   = 1;

        while (await reader.ReadAsync())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    var child = StartElement(reader, out var childEmpty);
                    current.Add(child);
                    if (childEmpty) break;

                    if (++depth > MaxDepth) throw new XmlException("A message from the server was nested too deeply.");
                    current = child;
                    break;

                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    current.Add(new XText(await reader.GetValueAsync()));
                    break;

                case XmlNodeType.EndElement:
                    if (--depth == 0) return root;
                    current = current.Parent!;
                    break;
            }
        }

        throw new IOException("The server closed the connection part way through a message.");
    }

    private static XElement StartElement(XmlReader reader, out bool empty)
    {
        empty = reader.IsEmptyElement;

        var element = new XElement(XNamespace.Get(reader.NamespaceURI).GetName(reader.LocalName));

        if (reader.MoveToFirstAttribute())
        {
            do
            {
                // A namespace declaration has done its work: the names already carry the namespace.
                if (reader.NamespaceURI == XNamespace.Xmlns.NamespaceName) continue;
                element.SetAttributeValue(XNamespace.Get(reader.NamespaceURI).GetName(reader.LocalName), reader.Value);
            }
            while (reader.MoveToNextAttribute());

            reader.MoveToElement();
        }

        return element;
    }

    public Task WriteAsync(XElement stanza, CancellationToken ct) =>
        WriteRawAsync(stanza.ToString(SaveOptions.DisableFormatting), ct, stanza.Name.Namespace == Client ? stanza : null);

    /// <summary>A single space: the keep-alive the protocol allows, which also proves the link still works.</summary>
    public Task KeepAliveAsync(CancellationToken ct) => WriteRawAsync(" ", ct);

    /// <summary>A server that stops reading must not leave a write waiting for ever: pings go through here too.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    /// <param name="stanza">A stanza, which the server counts, so it is kept until the server says it has it.</param>
    private async Task WriteRawAsync(string text, CancellationToken ct, XElement? stanza = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(WriteTimeout);
        try
        {
            await _writeLock.WaitAsync(limit.Token);
            try
            {
                var stream = _stream ?? throw new XmppException(XmppFailure.Network, "Not connected.");

                // Counted in the order written, which is the order the server counts. A message asks at once whether it
                // arrived, as other clients do: it is what someone would miss.
                if (stanza is not null && _counting)
                {
                    lock (_smGate)
                    {
                        _outbound++;
                        _unacked.Enqueue((_outbound, new XElement(stanza)));
                        while (_unacked.Count > MostUnacked) _unacked.Dequeue();
                    }
                    if (stanza.Name == Client + "message") text += "<r xmlns='urn:xmpp:sm:3'/>";
                }

                var bytes = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(bytes, limit.Token);
                await stream.FlushAsync(limit.Token);
            }
            finally { _writeLock.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { _tcp?.Close(); }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { /* closing it was the point */ }
            throw new XmppException(XmppFailure.Network, "The server stopped taking data.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            throw new XmppException(XmppFailure.Network, e.Message, e);
        }
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    private async Task LogInAsync(XmppAddress address, XElement features, CancellationToken ct)
    {
        var offered = features.Element(Sasl + "mechanisms")?.Elements(Sasl + "mechanism")
                              .Select(m => m.Value.Trim()).ToHashSet() ?? [];

        var mechanism = Preferred.FirstOrDefault(offered.Contains)
            ?? throw new XmppException(XmppFailure.Login,
                   $"The server offers no login method this client knows ({string.Join(", ", offered)}).");

        if (mechanism == "PLAIN")
        {
            await WriteAsync(new XElement(Sasl + "auth", new XAttribute("mechanism", mechanism),
                ToBase64($"\0{address.Local}\0{_login.Password}")), ct);
            await FinishLoginAsync(null, ct);
            return;
        }

        var scram = new XmppScram(mechanism, address.Local, _login.Password);
        await WriteAsync(new XElement(Sasl + "auth", new XAttribute("mechanism", mechanism), ToBase64(scram.ClientFirst())), ct);
        await FinishLoginAsync(scram, ct);
    }

    private async Task FinishLoginAsync(XmppScram? scram, CancellationToken ct)
    {
        while (true)
        {
            var answer = await ReadStanzaAsync(ct)
                         ?? throw new XmppException(XmppFailure.Network, "The server closed the connection.");

            if (answer.Name == Sasl + "challenge" && scram is not null)
            {
                var reply = scram.ClientFinal(FromBase64(answer.Value));
                await WriteAsync(new XElement(Sasl + "response", ToBase64(reply)), ct);
            }
            else if (answer.Name == Sasl + "success")
            {
                if (scram is not null && !scram.ServerIsGenuine(FromBase64(answer.Value)))
                    throw new XmppException(XmppFailure.Login, "The server could not prove it knows the password.");
                return;
            }
            else if (answer.Name == Sasl + "failure")
            {
                var why = answer.Elements().FirstOrDefault(e => e.Name.LocalName != "text")?.Name.LocalName ?? "not-authorized";
                throw new XmppException(XmppFailure.Login, $"The server refused the login ({why}).");
            }
            else if (answer.Name == Streams + "error") throw StreamError(answer);
            else throw new XmppException(XmppFailure.Protocol, $"Unexpected answer to the login: {answer.Name.LocalName}.");
        }
    }

    private async Task BindAsync(XElement features, CancellationToken ct)
    {
        if (features.Element(BindNs + "bind") is null)
            throw new XmppException(XmppFailure.Protocol, "The server does not offer a session.");

        var bind = new XElement(BindNs + "bind");
        if (_login.Resource.Length > 0) bind.Add(new XElement(BindNs + "resource", _login.Resource));

        var answer = await QueryDuringSetupAsync("bind", bind, ct);
        var jid    = answer.Element(BindNs + "bind")?.Element(BindNs + "jid")?.Value;
        if (string.IsNullOrEmpty(jid))
            throw new XmppException(XmppFailure.Protocol, "The server did not say what address it bound.");
        Bound = XmppAddress.Parse(jid);

        // Older servers ask for this and say so; a server marking it optional, or not at all, does not need it.
        var session = features.Element(Session + "session");
        if (session is not null && session.Element(Session + "optional") is null)
            await QueryDuringSetupAsync("session", new XElement(Session + "session"), ct);
    }

    // ── Stream management ─────────────────────────────────────────────────────

    /// <summary>Asks the server to count, and to keep the stream a while if the connection drops. A refusal changes nothing else.</summary>
    private async Task EnableAsync(CancellationToken ct)
    {
        await WriteAsync(new XElement(Sm + "enable", new XAttribute("resume", "true")), ct);
        while (true)
        {
            var answer = await ReadStanzaAsync(ct) ?? throw new XmppException(XmppFailure.Network, "The server closed the connection.");
            if (answer.Name == Streams + "error") throw StreamError(answer);
            if (answer.Name == Sm + "failed") return;
            if (answer.Name != Sm + "enabled") continue;

            lock (_smGate)
            {
                _inbound = 0;
                _outbound = 0;
                _smId = (string?)answer.Attribute("resume") is "true" or "1" ? (string?)answer.Attribute("id") ?? "" : "";
                _counting = true;
            }
            return;
        }
    }

    /// <summary>
    /// Picks up the stream of a connection that dropped, if the server still has it, and sends again what it had not
    /// confirmed. Where it does not, what was unconfirmed is kept for <see cref="TakeUnsent"/> and a new session is made.
    /// </summary>
    private async Task<bool> ResumeAsync(XmppStreamResume from, CancellationToken ct)
    {
        await WriteAsync(new XElement(Sm + "resume", new XAttribute("previd", from.Id), new XAttribute("h", from.Inbound)), ct);

        var answer = await ReadStanzaAsync(ct) ?? throw new XmppException(XmppFailure.Network, "The server closed the connection.");
        if (answer.Name == Streams + "error") throw StreamError(answer);

        uint? h = uint.TryParse((string?)answer.Attribute("h"), out var got) ? got : null;
        var waiting = from.Unacked.Where(u => h is not { } done || After(u.Seq, done)).Select(u => u.Stanza).ToList();

        if (answer.Name != Sm + "resumed")
        {
            _unsent = waiting;
            return false;
        }

        lock (_smGate)
        {
            _smId = from.Id;
            _inbound = from.Inbound;
            _outbound = h ?? from.Outbound;
            _counting = true;
        }
        Bound = from.Bound;
        Resumed = true;

        foreach (var stanza in waiting) await WriteAsync(stanza, ct);
        return true;
    }

    /// <summary>The server has everything up to this count: no need to keep it any longer.</summary>
    private void Acked(uint h)
    {
        lock (_smGate)
            while (_unacked.TryPeek(out var first) && !After(first.Seq, h)) _unacked.Dequeue();
    }

    /// <summary>Whether one count comes after another, the counts going round past the largest number as the protocol says.</summary>
    internal static bool After(uint seq, uint than) => (int)(seq - than) > 0;

    private async Task<XElement> QueryDuringSetupAsync(string id, XElement payload, CancellationToken ct)
    {
        await WriteAsync(new XElement(Client + "iq", new XAttribute("type", "set"), new XAttribute("id", id), payload), ct);

        while (true)
        {
            var answer = await ReadStanzaAsync(ct)
                         ?? throw new XmppException(XmppFailure.Network, "The server closed the connection.");

            if (answer.Name == Streams + "error") throw StreamError(answer);
            if (answer.Name != Client + "iq" || (string?)answer.Attribute("id") != id) continue;
            if ((string?)answer.Attribute("type") == "error")
                throw new XmppException(XmppFailure.Protocol, $"The server refused the {id}.");
            return answer;
        }
    }

    public static XmppException StreamError(XElement error)
    {
        var why = error.Elements().FirstOrDefault(e => e.Name.LocalName != "text")?.Name.LocalName ?? "unknown";
        var failure = why switch
        {
            "conflict"       => XmppFailure.Replaced,
            "not-authorized" => XmppFailure.Login,
            _                => XmppFailure.Network,
        };
        return new XmppException(failure, $"The server ended the connection ({why}).");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToBase64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static string FromBase64(string text)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(text.Trim())); }
        catch (FormatException e) { throw new XmppException(XmppFailure.Protocol, "The server sent bad login data.", e); }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;");

    /// <summary>
    /// Drops the connection without ending the stream, so a server that keeps streams has this one to pick up again.
    /// ⚠️ For a connection that has failed. One being ended on purpose is disposed, which tells the server it is over.
    /// </summary>
    public void Abandon() => CloseSocket();

    private void CloseSocket()
    {
        try { _tcp?.Close(); } catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { /* going anyway */ }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_stream is not null && _tcp?.Connected == true)
                await WriteRawAsync("</stream:stream>", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is XmppException or TimeoutException or IOException or ObjectDisposedException) { /* the socket closes below regardless */ }

        // ⚠️ The socket only. A read is usually still in flight here, closing the socket is what ends it,
        // and an XML reader refuses to be disposed under one. It holds nothing but memory.
        CloseSocket();
    }

    /// <summary>Counts what comes in, and refuses to read past the limit until told a new stanza began.</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public void Reset() => _read = 0;

        private int Count(int n)
        {
            _read += n;
            if (_read > limit) throw new IOException("A single message from the server was far too large.");
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, count), ct));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Count(await inner.ReadAsync(buffer, ct));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
