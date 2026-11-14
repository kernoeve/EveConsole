using DnsClient;
using DnsClient.Protocol;

namespace EveConsole.Services.Jabber;

/// <summary>One place to try connecting.</summary>
public sealed record XmppEndpoint(string Host, int Port, bool DirectTls);

/// <summary>
/// Where a domain says its Jabber server is, from its DNS service records.
///
/// <para>A domain's server is often on another machine, so the address alone is not enough. The
/// standard order: encrypted-from-the-start records and STARTTLS ones together by priority, then the
/// domain itself on the usual port as the fallback every server is meant to answer on.</para>
/// </summary>
public static class XmppDns
{
    public const int DefaultPort = 5222;

    public static async Task<IReadOnlyList<XmppEndpoint>> FindAsync(string domain, CancellationToken ct)
    {
        var found = new List<(int Priority, int Weight, XmppEndpoint Endpoint)>();

        try
        {
            var dns = new LookupClient(new LookupClientOptions { UseCache = true, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });
            foreach (var (service, direct) in new[] { ("_xmpps-client._tcp.", true), ("_xmpp-client._tcp.", false) })
            {
                var answer = await dns.QueryAsync(service + domain, QueryType.SRV, cancellationToken: ct);
                foreach (var srv in answer.Answers.OfType<SrvRecord>())
                {
                    var host = srv.Target.Value.TrimEnd('.');
                    // RFC 2782: a target of "." says the service is not offered at this domain.
                    if (host.Length > 0) found.Add((srv.Priority, srv.Weight, new XmppEndpoint(host, srv.Port, direct)));
                }
            }
        }
        catch (Exception e) when (e is DnsResponseException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            // No answer from DNS is not an answer about the server: the domain itself is still tried.
        }

        return Order(domain, found);
    }

    /// <summary>The lowest priority first, heavier first within one, and the domain itself last.</summary>
    public static IReadOnlyList<XmppEndpoint> Order(string domain, IEnumerable<(int Priority, int Weight, XmppEndpoint Endpoint)> records)
    {
        var ordered = records.OrderBy(r => r.Priority).ThenByDescending(r => r.Weight).Select(r => r.Endpoint).ToList();
        var fallback = new XmppEndpoint(domain, DefaultPort, false);
        if (!ordered.Contains(fallback)) ordered.Add(fallback);
        return ordered;
    }
}
