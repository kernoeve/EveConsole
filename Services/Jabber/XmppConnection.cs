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
/// One encrypted XMPP stream, up to the point stanzas can flow: connect, STARTTLS, log in, bind.
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

    public XmppConnection(XmppLogin login) => _login = login;

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
            await BindAsync(features, timeout.Token);
            Features = features;
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

    /// <summary>The next thing the server sent, or null when it ended the stream.</summary>
    public async Task<XElement?> ReadStanzaAsync(CancellationToken ct)
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
        WriteRawAsync(stanza.ToString(SaveOptions.DisableFormatting), ct);

    /// <summary>A single space: the keep-alive the protocol allows, which also proves the link still works.</summary>
    public Task KeepAliveAsync(CancellationToken ct) => WriteRawAsync(" ", ct);

    /// <summary>A server that stops reading must not leave a write waiting for ever: pings go through here too.</summary>
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    private async Task WriteRawAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(WriteTimeout);
        try
        {
            await _writeLock.WaitAsync(limit.Token);
            try
            {
                var stream = _stream ?? throw new XmppException(XmppFailure.Network, "Not connected.");
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
