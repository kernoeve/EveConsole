using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace EveConsole.Services;

/// <summary>
/// Fetches pictures from the web: those people on IRC point to, and EVE portraits. A link there is anyone's to set, so
/// what is fetched is kept small, and only from the internet.
/// </summary>
/// <remarks>
/// ⚠️ Never from this machine or the local network: a link someone sets to http://192.168.1.1/... would otherwise have
/// this app ask the router, or anything else inside, on their behalf. Checked on the address actually connected to, so a
/// name that points inside after it was checked gets nowhere either.
/// </remarks>
public static class WebPictures
{
    /// <summary>For the checks only: lets a picture come from this machine, where their test server runs.</summary>
    public static bool AllowThisMachine { get; set; }

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var allowed = addresses.Where(a => IsInternet(a) || AllowThisMachine && IPAddress.IsLoopback(a)).ToArray();
                if (allowed.Length == 0) throw new HttpRequestException("Pictures are only fetched from the internet.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("EveConsole/1.0");
        return http;
    }

    /// <summary>An address out on the internet: not this machine, the local network, or anything set aside.</summary>
    public static bool IsInternet(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || b[0] == 169 && b[1] == 254
                     || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                     || b[0] == 192 && b[1] == 168
                     || b[0] == 100 && b[1] >= 64 && b[1] <= 127
                     || b[0] == 198 && b[1] is 18 or 19);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                     || (b[0] & 0xfe) == 0xfc);   // unique local, fc00::/7
        }

        return false;
    }

    /// <summary>The picture at a link, or null when it cannot be had or is too big to be one.</summary>
    public static async Task<byte[]?> FetchAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > AvatarService.MostBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var kept = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (kept.Length + read > AvatarService.MostBytes) return null;
                kept.Write(chunk, 0, read);
            }
            return kept.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or SocketException or InvalidOperationException)
        {
            return null;
        }
    }
}
