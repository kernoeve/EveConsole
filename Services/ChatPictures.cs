using System.Net.Security;

namespace EveConsole.Services;

/// <param name="Host">The web host files are kept on.</param>
/// <param name="Subdomains">Its subdomains too (a Jabber server's upload.domain).</param>
/// <param name="Port">Only on this port, or any.</param>
/// <param name="TrustedCertificate">A certificate the person already trusted for the chat server, taken here too when
/// Windows does not trust the host's own (a private server's self-signed one).</param>
public sealed record PictureHost(string Host, bool Subdomains, int? Port, string TrustedCertificate);

/// <summary>
/// Pictures linked in chat: which links are pictures, which come from the chat server's own file host and so are shown
/// at once, and fetching them.
/// </summary>
/// <remarks>
/// ⚠️ Fetching a link tells its web host this person's address, and anyone can post one. Only a chat server's own host is
/// fetched unasked: a server the person chose to connect to sees their address already. Any other waits for a click,
/// and then comes only from the internet, as <see cref="WebPictures"/> does.
/// </remarks>
public static class ChatPictures
{
    /// <summary>Largest picture fetched. A bigger one is opened in the browser instead.</summary>
    public const long MostBytes = 20 * 1024 * 1024;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    private static readonly Lock Gate = new();
    private static readonly List<Func<IEnumerable<PictureHost>>> Providers = [];

    /// <summary>Adds where a chat service's servers keep files, asked afresh each time, as servers connect and go.</summary>
    public static void AddHosts(Func<IEnumerable<PictureHost>> provider)
    {
        lock (Gate) Providers.Add(provider);
    }

    /// <summary>A web link whose name says it is a picture (.png, .jpg, .gif, .webp, .bmp).</summary>
    public static bool IsPictureLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && Extensions.Contains(Path.GetExtension(uri.AbsolutePath));

    /// <summary>The chat server's own host the link is on, or null when it is anywhere else.</summary>
    public static PictureHost? OwnHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        Func<IEnumerable<PictureHost>>[] providers;
        lock (Gate) providers = [.. Providers];
        foreach (var provider in providers)
        {
            IEnumerable<PictureHost> hosts;
            try { hosts = [.. provider()]; }
            catch (InvalidOperationException) { continue; }   // a service's list changing while read: asked again next time
            foreach (var host in hosts)
                if (Matches(host, uri)) return host;
        }
        return null;
    }

    public static bool Matches(PictureHost host, Uri uri) =>
        host.Host.Length > 0
        && (string.Equals(uri.Host, host.Host, StringComparison.OrdinalIgnoreCase)
            || host.Subdomains && uri.Host.EndsWith("." + host.Host, StringComparison.OrdinalIgnoreCase))
        && (host.Port is not { } port || uri.Port == port);

    /// <summary>
    /// The picture's bytes, or null when it cannot be had or is too big. From the chat server's own host wherever that is
    /// (it may be on the local network, as a private server is); from anywhere else, only from the internet.
    /// </summary>
    public static async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        if (OwnHost(url) is not { } own) return await WebPictures.FetchAsync(url, ct, MostBytes);

        // ⚠️ No redirects: what is fetched without asking is what the server's own host has, and nothing it points to.
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    errors == SslPolicyErrors.None
                    || certificate is not null && own.TrustedCertificate.Length > 0
                       && string.Equals(Jabber.XmppConnection.Fingerprint(certificate), own.TrustedCertificate, StringComparison.OrdinalIgnoreCase),
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveConsole/1.0");
        return await WebPictures.ReadAsync(http, new Uri(url), MostBytes, ct);
    }
}
