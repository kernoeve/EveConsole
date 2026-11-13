using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using EveConsole.Localization;

namespace EveConsole.Services.Irc;

public sealed partial class IrcService
{
    // ── Sending a file (draft/FILEHOST) ───────────────────────────────────────
    //
    // The server names a web address that takes files (its 005 line). The file goes up there by HTTP POST, with the
    // account's login when it logged in by SASL, and the address it is then kept at is sent to the conversation as an
    // ordinary message: everyone sees a link, whatever their client.

    /// <summary>
    /// Whether a file host may be used: https always, plain http only to this machine and only while the IRC
    /// connection is not encrypted either. ⚠️ The protocol forbids plain http under an encrypted connection: the
    /// login would go in the clear where the chat does not.
    /// </summary>
    public static bool IsUsableFileHost(string url, bool ircEncrypted) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && !ircEncrypted);

    /// <summary>Files can be sent on this connection: it is up, and its server named a file host fit to use.</summary>
    public static bool CanSendFiles(IrcSession session) =>
        session.State == IrcState.Connected && IsUsableFileHost(session.FileHost, session.IsEncrypted);

    /// <summary>Longest a file is given to go up.</summary>
    private static readonly TimeSpan UploadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Puts a file on the server's file host and sends its address to <paramref name="target"/> (a channel, or a
    /// person). Throws with words for the screen when the host refuses it.
    /// </summary>
    /// <remarks>
    /// ⚠️ The file goes up from this PC, as it is on this PC: the worker, maybe on another, only sends the address.
    /// </remarks>
    public async Task SendFileAsync(IrcSession session, string target, Stream content, string fileName, long size, string contentType, CancellationToken ct = default)
    {
        if (session.State != IrcState.Connected) throw new IrcException(IrcFailure.Network, "Not connected.");
        var host = session.FileHost;
        if (host.Length == 0) throw new IrcException(IrcFailure.Refused, IrcText.FileNoHost);
        if (!IsUsableFileHost(host, session.IsEncrypted)) throw new IrcException(IrcFailure.Refused, IrcText.FileHostUnencrypted);
        var upload = new Uri(host);

        var login = await Task.Run(() => FileHostLogin(session), ct);
        using var http = FileHostClient(session, upload);

        await CheckTypeTakenAsync(http, upload, contentType, ct);

        using var body = new StreamContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        body.Headers.ContentLength = size;
        var name = Path.GetFileName(fileName);
        body.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileName = AsciiName(name), FileNameStar = name };

        // ⚠️ Asks before sending the file (Expect: 100-continue), so a host can refuse one too big, or a login, before
        // the bytes go: without, it answers 413 and drops the connection part-way, and all that shows is a broken send.
        using var request = new HttpRequestMessage(HttpMethod.Post, upload) { Content = body };
        request.Headers.ExpectContinue = true;
        if (login is { } basic) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        HttpResponseMessage sent;
        try { sent = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) when (e.InnerException is IOException)
        {
            throw new IrcException(IrcFailure.Refused, IrcText.FileCutOff, e);
        }
        using var response = sent;
        if (response.StatusCode != HttpStatusCode.Created) throw new IrcException(IrcFailure.Refused, RefusedWords(response));

        var kept = response.Headers.Location is { } location
            ? location.IsAbsoluteUri ? location : new Uri(upload, location)
            : throw new IrcException(IrcFailure.Refused, IrcText.FileNoLink);
        if (kept.Scheme != Uri.UriSchemeHttps && kept.Scheme != Uri.UriSchemeHttp) throw new IrcException(IrcFailure.Refused, IrcText.FileNoLink);

        var said = await SendAsync(session, target, kept.AbsoluteUri, "", ct);
        if (said.Length > 0) throw new IrcException(IrcFailure.Refused, said);
    }

    /// <summary>
    /// The login to show the file host, as HTTP Basic: the one SASL logged in with, as the protocol asks. Null when
    /// the connection did not log in by SASL, and the host is asked without.
    /// </summary>
    private static string? FileHostLogin(IrcSession session)
    {
        var server = session.Server;
        if ((IrcLoginKind)server.LoginKind != IrcLoginKind.Sasl || !session.LoggedIn) return null;
        var password = AppConfig.GetServicePassword(PasswordReference(server.Id)) ?? "";
        if (password.Length == 0) return null;
        var account = server.Account.Trim().Length > 0 ? server.Account.Trim() : server.Nickname.Trim();
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(account + ":" + password));
    }

    /// <summary>
    /// A client for this upload alone. ⚠️ Never follows a redirect: the login must go to the address the server named
    /// and nowhere else. A certificate not trusted by Windows is taken only when it is the one already trusted for this
    /// IRC server, on the same host (a private server's own certificate).
    /// </summary>
    private static HttpClient FileHostClient(IrcSession session, Uri upload)
    {
        var trusted = session.Server.TrustedCertificate;
        var sameHost = string.Equals(upload.Host, session.Server.Host.Trim(), StringComparison.OrdinalIgnoreCase);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || certificate is not null && sameHost && trusted.Length > 0
                       && string.Equals(Jabber.XmppConnection.Fingerprint(certificate), trusted, StringComparison.OrdinalIgnoreCase),
            },
        };
        var http = new HttpClient(handler) { Timeout = UploadTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EVEConsole");
        return http;
    }

    /// <summary>
    /// Asks the host which kinds of file it takes (OPTIONS, Accept-Post), and refuses one it says it does not before
    /// sending it. A host that says nothing is taken to take anything.
    /// </summary>
    private static async Task CheckTypeTakenAsync(HttpClient http, Uri upload, string contentType, CancellationToken ct)
    {
        IEnumerable<string>? accepted = null;
        try
        {
            using var asked = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, upload), ct);
            if (asked.IsSuccessStatusCode && asked.Headers.TryGetValues("Accept-Post", out var values)) accepted = values;
        }
        catch (HttpRequestException) { return; }   // the upload itself will say what is wrong

        if (accepted is null) return;
        var kinds = accepted.SelectMany(v => v.Split(',')).Select(k => k.Split(';')[0].Trim()).Where(k => k.Length > 0).ToList();
        if (kinds.Count == 0 || kinds.Any(k => TypeMatches(k, contentType))) return;
        throw new IrcException(IrcFailure.Refused, string.Format(CultureInfo.CurrentCulture, IrcText.FileTypeNotTaken, string.Join(", ", kinds)));
    }

    /// <summary>"image/*" takes image/png; "*/*" takes anything.</summary>
    public static bool TypeMatches(string accepted, string contentType)
    {
        if (accepted == "*/*") return true;
        if (accepted.EndsWith("/*", StringComparison.Ordinal))
            return contentType.StartsWith(accepted[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(accepted, contentType, StringComparison.OrdinalIgnoreCase);
    }

    private static string RefusedWords(HttpResponseMessage response) => response.StatusCode switch
    {
        HttpStatusCode.RequestEntityTooLarge => IrcText.FileTooBigForHost,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => IrcText.FileNotAllowed,
        HttpStatusCode.UnsupportedMediaType => IrcText.FileTypeRefused,
        _ => string.Format(CultureInfo.CurrentCulture, IrcText.FileRefused, (int)response.StatusCode, response.ReasonPhrase ?? ""),
    };

    /// <summary>The name with only plain letters, for hosts that read the old filename= alone.</summary>
    private static string AsciiName(string name)
    {
        var plain = new string([.. name.Select(c => c is >= ' ' and < (char)127 and not '"' and not '\\' ? c : '_')]);
        return plain.Trim().Length > 0 ? plain : "file";
    }
}
