using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;

namespace EveCortex.Services;

public record NewsItem(string Title, string Link, DateTimeOffset PubDate, string DescriptionHtml)
{
    public string PubDateText => PubDate == default
        ? ""
        : PubDate.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
}

public class NewsService(AppErrorLogger errorLogger)
{
    private static readonly string FeedUrl = "https://www.eveonline.com/rss";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private List<NewsItem> _cache = [];
    private DateTimeOffset  _cachedAt = DateTimeOffset.MinValue;

    public async Task<List<NewsItem>> GetNewsAsync(CancellationToken ct = default)
    {
        if (_cache.Count > 0 && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            return _cache;

        try
        {
            var xml = await _http.GetStringAsync(FeedUrl, ct);
            var doc  = XDocument.Parse(xml);

            // RSS 2.0 pubDate format: "Thu, 19 Jun 2026 15:00:00 GMT"
            static DateTimeOffset ParsePubDate(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return default;
                // Try standard RFC-822 variants
                string[] formats =
                [
                    "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
                    "ddd, dd MMM yyyy HH:mm:ss zzz",
                    "ddd,  d MMM yyyy HH:mm:ss 'GMT'",
                ];
                foreach (var fmt in formats)
                    if (DateTimeOffset.TryParseExact(s, fmt, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var dt))
                        return dt;
                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var fallback))
                    return fallback;
                return default;
            }

            _cache = doc.Descendants("item")
                .Take(12)
                .Select(item => new NewsItem(
                    Title:           item.Element("title")?.Value       ?? "(no title)",
                    Link:            item.Element("link")?.Value        ?? "",
                    PubDate:         ParsePubDate(item.Element("pubDate")?.Value),
                    DescriptionHtml: item.Element("description")?.Value ?? ""))
                .ToList();

            _cachedAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) { /* cancelled — return cached */ }
        catch (Exception ex)
        {
            errorLogger.Log("NewsService", "GetNewsAsync", ex);
        }

        return _cache;
    }
}
