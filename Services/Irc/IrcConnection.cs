using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EveConsole.Services.Irc;

public enum IrcFailure
{
    /// <summary>The server could not be reached, or the connection dropped.</summary>
    Network,
    /// <summary>The server's certificate is not trusted, and it is not the one the person accepted.</summary>
    Certificate,
    /// <summary>The server refused the login: a wrong password, or an account it does not know.</summary>
    Login,
    /// <summary>The server turned the connection away: banned, full, or closed to this address.</summary>
    Refused,
    /// <summary>The server spoke something this client cannot follow.</summary>
    Protocol,
}

public sealed class IrcException(IrcFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public IrcFailure Failure { get; } = failure;

    /// <summary>For <see cref="IrcFailure.Certificate"/>: what the person is being asked to accept.</summary>
    public string? CertificateFingerprint { get; init; }
    public string? CertificateSubject { get; init; }
    public string? CertificateProblem { get; init; }
}

/// <summary>
/// One connection to an IRC server, line by line: connect, encrypt, read, write. What the lines mean is
/// <see cref="IrcClient"/>'s business.
/// </summary>
/// <remarks>
/// <para>A self-signed certificate is common on group servers, so, as with Jabber, a person may accept one by its
/// fingerprint: from then on only that one passes.</para>
/// <para>⚠️ Sending is paced, a few lines at once and then one a second, because a server disconnects a client that sends
/// faster ("Excess Flood"), and a long paste or a burst of joins on connecting would do just that. Answers to the server's
/// PING skip the queue: a server waiting on one behind a long paste would drop the connection for not answering.</para>
/// </remarks>
public sealed class IrcConnection : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Lines that may go at once before pacing starts, and the pace after.</summary>
    private const int Burst = 5;
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(1000);

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private TcpClient? _tcp;
    private Stream? _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start, _end;

    private double _tokens = Burst;
    private DateTime _lastRefill = DateTime.UtcNow;

    public bool IsEncrypted => _stream is SslStream;

    /// <summary>Every line written, for a test to read back. Null outside tests.</summary>
    public Action<string>? Sent { get; set; }

    public async Task ConnectAsync(string host, int port, bool tls, string trustedFingerprint, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(host, port, timeout.Token);
            _stream = _tcp.GetStream();
            if (tls) await StartTlsAsync(host, trustedFingerprint, timeout.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is not IrcException || timeout.IsCancellationRequested))
        {
            Close();
            throw new IrcException(IrcFailure.Network, timeout.IsCancellationRequested ? "The server did not answer in time." : e.Message, e);
        }
    }

    private async Task StartTlsAsync(string host, string trusted, CancellationToken ct)
    {
        string? fingerprint = null, subject = null, problem = null;

        var tls = new SslStream(_stream!, leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;
                if (certificate is null) { problem = errors.ToString(); return false; }

                fingerprint = Jabber.XmppConnection.Fingerprint(certificate);
                subject = certificate.Subject;
                problem = errors.ToString();
                return string.Equals(fingerprint, trusted, StringComparison.OrdinalIgnoreCase);
            },
        };

        try
        {
            await tls.AuthenticateAsClientAsync(options, ct);
        }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException && fingerprint is not null)
        {
            throw new IrcException(IrcFailure.Certificate, "The server's certificate is not trusted.", e)
            {
                CertificateFingerprint = fingerprint,
                CertificateSubject = subject,
                CertificateProblem = problem,
            };
        }

        _stream = tls;
    }

    /// <summary>The next line, without its line end. Null when the server closed the connection.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new IrcException(IrcFailure.Network, "Not connected.");
        var discarding = false;

        while (true)
        {
            var lf = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (lf >= 0)
            {
                var length = lf - _start;
                if (length > 0 && _buffer[lf - 1] == '\r') length--;
                var from = _start;
                _start = lf + 1;
                if (discarding) { discarding = false; continue; }
                return Decode(_buffer, from, length);
            }

            // No line end yet: make room, or give up on a line longer than any real one.
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length) { _end = 0; discarding = true; }

            int read;
            try { read = await stream.ReadAsync(_buffer.AsMemory(_end), ct); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw new IrcException(IrcFailure.Network, e.Message, e);
            }
            if (read == 0) return null;
            _end += read;
        }
    }

    /// <summary>
    /// UTF-8, and where the bytes are not UTF-8, Latin-1: older networks and clients still send that, and a line in it
    /// should read as near as can be rather than as replacement marks.
    /// </summary>
    public static string Decode(byte[] bytes, int index, int count)
    {
        try { return StrictUtf8.GetString(bytes, index, count); }
        catch (DecoderFallbackException) { return Latin1.GetString(bytes, index, count); }
    }

    public Task SendAsync(IrcMessage message, CancellationToken ct) => SendAsync(message, urgent: false, ct);

    /// <param name="urgent">Goes now, ahead of the pace: an answer to the server's PING, or leaving.</param>
    public async Task SendAsync(IrcMessage message, bool urgent, CancellationToken ct)
    {
        var line = Fit(message);
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");

        await _writeLock.WaitAsync(ct);
        try
        {
            if (!urgent) await WaitTurnAsync(ct);
            var stream = _stream ?? throw new IrcException(IrcFailure.Network, "Not connected.");
            try
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
                throw new IrcException(IrcFailure.Network, e.Message, e);
            }
            Sent?.Invoke(line);
        }
        finally { _writeLock.Release(); }
    }

    private async Task WaitTurnAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _tokens = Math.Min(Burst, _tokens + (now - _lastRefill) / Pace);
        _lastRefill = now;
        if (_tokens < 1)
        {
            await Task.Delay(Pace * (1 - _tokens), ct);
            _tokens = 1;
            _lastRefill = DateTime.UtcNow;
        }
        _tokens--;
    }

    /// <summary>
    /// The line as written, cut to what servers take: 512 bytes for the part after the tags. Cut at a character, never
    /// through one. Callers split long words before this; here is only the last guard.
    /// </summary>
    private static string Fit(IrcMessage message)
    {
        var line = message.Serialize();
        var tagEnd = line.StartsWith('@') ? line.IndexOf(' ') + 1 : 0;
        var rest = line[tagEnd..];
        if (Encoding.UTF8.GetByteCount(rest) <= IrcMessage.MaxOutgoingBytes - 2) return line;

        var cut = rest.Length;
        while (cut > 0 && Encoding.UTF8.GetByteCount(rest.AsSpan(0, cut)) > IrcMessage.MaxOutgoingBytes - 2) cut--;
        if (cut > 0 && char.IsHighSurrogate(rest[cut - 1])) cut--;
        return line[..tagEnd] + rest[..cut];
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        try { _tcp?.Dispose(); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }
}
