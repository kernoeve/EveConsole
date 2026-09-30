using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>What became of one Discord message, or of one file sent with one.</summary>
public sealed record DiscordPostResult(bool Ok, string? Error);

/// <summary>What became of a message sent in parts. The same shape as
/// <see cref="SlackPartsResult"/>, so every status line can say the same things about either.</summary>
/// <param name="Posted">How many parts Discord took, in order. None after the first refusal was sent.</param>
/// <param name="Total">How many parts the message was cut into.</param>
/// <param name="Characters">What the parts Discord took add up to.</param>
/// <param name="Error">Why the part that stopped it was refused; null when every part went.</param>
public sealed record DiscordPartsResult(int Posted, int Total, int Characters, string? Error)
{
    public bool AllPosted => Total > 0 && Posted == Total;
}

/// <summary>
/// Posts to Discord channels through webhooks.
///
/// <para>Webhooks are the whole integration, by decision: no bot, no sign-in, no OAuth. A server's
/// managers make a webhook on a channel and hand out its link; that link is all a post needs, and
/// it reaches servers where nobody would ever invite a bot.</para>
///
/// <para>⚠️ A webhook URL is a credential — anyone holding it can post to that channel. It never
/// goes into a log line, an error, a status line or an exception message from here. What a person
/// reads names the webhook by the name they gave it, or as <see cref="Redact"/> spells it.</para>
///
/// <para>⚠️ Every post sends <c>allowed_mentions</c> with an empty parse list. The text comes from
/// corp data and from whatever a person typed into a scheduled post, and a pilot named "everyone"
/// or a line that happens to hold <c>@here</c> must never ping a whole server.</para>
/// </summary>
public class DiscordService
{
    // The areas that post; the same keys Slack's settings use, so one part of the app is one area
    // whichever service it posts through.
    public const string AreaCorpTop10   = SlackService.AreaCorpTop10;
    public const string AreaCorpMonthly = SlackService.AreaCorpMonthly;
    public const string AreaSalePosting = SlackService.AreaSalePosting;

    /// <summary>
    /// The most one part holds.
    ///
    /// <para>Discord refuses content over 2,000 characters outright rather than splitting it. This
    /// stays under that with room to spare: a part the splitter had to reopen a code block in
    /// carries the fence and a repeated header on top of its own lines.</para>
    /// </summary>
    public const int MaxPartLength = 1900;

    /// <summary>The longest wait a rate limit is allowed to impose before a post gives up. Discord
    /// asks for fractions of a second in ordinary use; minutes means something else is wrong.</summary>
    private static readonly TimeSpan MaxRateWait = TimeSpan.FromSeconds(30);

    /// <summary>How many times one message is tried when Discord answers 429.</summary>
    private const int MaxAttempts = 4;

    /// <summary>A later part gets a second try after a refusal that was not a rate limit, since the
    /// parts before it are already in the channel. Same reasoning, same gap, as Slack's.</summary>
    private static readonly TimeSpan RetryGap = TimeSpan.FromSeconds(3);

    private static string WebhookIdKey(string area) => $"discord.webhook_id.{area}";
    private static string LastPostKey(string key)   => $"discord.lastpost.{key}";

    /// <summary>
    /// A Discord webhook link: discord.com or the older discordapp.com, the canary and ptb clients'
    /// hosts, an optional API version, then the webhook's id and token. A query string is allowed
    /// (Discord's own <c>?thread_id=</c> posts into a thread) and kept.
    /// </summary>
    private static readonly Regex WebhookPattern = new(
        @"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api(?:/v\d+)?/webhooks/(?<id>\d+)/(?<token>[A-Za-z0-9_\-]+)/?(?:\?[^#\s]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The same shape found anywhere in a piece of text, for <see cref="Scrub"/>.</summary>
    private static readonly Regex WebhookAnywhere = new(
        @"https?://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api(?:/v\d+)?/webhooks/[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IHttpClientFactory    _httpFactory;
    private readonly AppPreferencesService _prefs;
    private readonly AppErrorLogger        _errors;
    private readonly IDbContextFactory<Data.AppDbContext> _dbFactory;

    public DiscordService(IHttpClientFactory httpFactory, AppPreferencesService prefs,
                          AppErrorLogger errors, IDbContextFactory<Data.AppDbContext> dbFactory)
    {
        _httpFactory = httpFactory;
        _prefs       = prefs;
        _errors      = errors;
        _dbFactory   = dbFactory;
    }

    // — Links ——————————————————————————————————————————————————————

    /// <summary>Whether a pasted link is a Discord webhook.</summary>
    public static bool IsWebhookUrl(string? url) => WebhookPattern.IsMatch((url ?? "").Trim());

    /// <summary>
    /// A webhook link as it may be shown: host and path up to the id, never the token.
    ///
    /// <para>The id alone cannot post anything, and it is what tells two webhooks of the same name
    /// apart in the settings list.</para>
    /// </summary>
    public static string Redact(string? url)
    {
        var m = WebhookPattern.Match((url ?? "").Trim());
        return m.Success ? $"discord.com/api/webhooks/{m.Groups["id"].Value}/…" : "discord.com/api/webhooks/…";
    }

    /// <summary>Any webhook link in a piece of text replaced by its redacted form, for anything
    /// that came back from the network stack and is about to be logged or shown.</summary>
    private static string Scrub(string? text)
        => WebhookAnywhere.Replace(text ?? "", m => Redact(m.Value));

    // — Named webhooks —————————————————————————————————————————————————

    /// <summary>Every webhook the user has named, in the order they appear in a dropdown.</summary>
    public async Task<List<Models.DiscordWebhook>> WebhooksAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DiscordWebhooks.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<Models.DiscordWebhook> AddWebhookAsync(string name, string url, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = new Models.DiscordWebhook { Name = (name ?? "").Trim(), Url = (url ?? "").Trim() };
        db.DiscordWebhooks.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateWebhooks();
        return row;
    }

    public async Task RemoveWebhookAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.DiscordWebhooks.FindAsync([id], ct).ConfigureAwait(false);
        if (row is null) return;
        db.DiscordWebhooks.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateWebhooks();
    }

    // Webhooks by id. Read whenever a post button decides whether to show, so it is cached; every
    // change made through this service drops it.
    private Dictionary<int, Models.DiscordWebhook>? _hooks;

    public void InvalidateWebhooks() => _hooks = null;

    private Dictionary<int, Models.DiscordWebhook> Hooks()
    {
        if (_hooks is not null) return _hooks;
        using var db = _dbFactory.CreateDbContext();
        return _hooks = db.DiscordWebhooks.AsNoTracking().ToDictionary(w => w.Id);
    }

    // — Per area ————————————————————————————————————————————————————

    /// <summary>
    /// Which named webhook an area posts to, if any.
    ///
    /// <para>⚠️ The id, not the URL — for the reason Slack's settings learned: two rows can carry
    /// the same link, and a link cannot say which of them was picked.</para>
    /// </summary>
    public int? WebhookId(string area)
        => int.TryParse(_prefs.Get(WebhookIdKey(area)), out var id) && id > 0 ? id : null;

    public Task SetWebhookIdAsync(string area, int? id)
        => _prefs.SetAsync(WebhookIdKey(area), id?.ToString(CultureInfo.InvariantCulture));

    /// <summary>The webhook an area posts to, or null when none is set or the one set was removed.</summary>
    private Models.DiscordWebhook? AreaHook(string area)
        => WebhookId(area) is int id && Hooks().TryGetValue(id, out var hook) ? hook : null;

    /// <summary>Whether this area has somewhere on Discord to post. Gates the Post to Discord buttons.</summary>
    public bool IsConfigured(string area) => AreaHook(area) is not null;

    /// <summary>The name of the webhook an area posts to, for a tooltip or a status line.</summary>
    public string WebhookName(string area) => AreaHook(area)?.Name ?? "";

    /// <summary>When this area (or a posting within it) last posted — used to warn about accidental
    /// reposts. ⚠️ Discord's own keys: a Slack post of the same thing says nothing about whether it
    /// reached Discord.</summary>
    public DateTimeOffset? LastPostAt(string key)
        => DateTimeOffset.TryParse(_prefs.Get(LastPostKey(key)), CultureInfo.InvariantCulture,
                                   DateTimeStyles.RoundtripKind, out var t) ? t : null;

    public Task SetLastPostAsync(string key, DateTimeOffset when)
        => _prefs.SetAsync(LastPostKey(key), when.ToString("o", CultureInfo.InvariantCulture));

    // — Posting ———————————————————————————————————————————————————————

    /// <summary>Posts to whatever webhook this area is set to, in as many parts as it needs.</summary>
    public async Task<DiscordPartsResult> PostAreaAsync(string area, string text, CancellationToken ct = default)
    {
        var hook = AreaHook(area);
        if (hook is null) return new DiscordPartsResult(0, 0, 0, SettingsText.DiscordErrNoWebhook);
        return await PostAsync(hook.Url, text, hook.Name, ct).ConfigureAwait(false);
    }

    /// <summary>A named webhook by id — what a scheduled task stores. Read fresh rather than from
    /// the cache: a task runs long after anything that would have dropped the cache.</summary>
    public async Task<Models.DiscordWebhook?> FindAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.DiscordWebhooks.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Posts <paramref name="text"/> in as many messages as it needs, in order.
    ///
    /// <para>⚠️ Cut by <see cref="SlackMessageSplitter"/> at Discord's limit rather than by a second
    /// splitter: the rules for where a message may end — never inside a code block, a heading never
    /// parted from its table — are the same on both, and only the length differs. Discord refuses
    /// an over-long message instead of cutting it, so without this a long Top 10 would not arrive
    /// at all.</para>
    ///
    /// <para>Stops at the first part Discord refuses, since anything sent after a gap would read
    /// as though nothing were missing.</para>
    /// </summary>
    /// <param name="name">What to call the webhook in the error log — its name, never its link.</param>
    public async Task<DiscordPartsResult> PostAsync(
        string url, string text, string? name = null, CancellationToken ct = default)
    {
        var parts = SlackMessageSplitter.Split(text ?? "", MaxPartLength);
        if (parts.Count == 0) return new DiscordPartsResult(0, 0, 0, SettingsText.DiscordErrNothingToPost);

        var characters = 0;

        for (var i = 0; i < parts.Count; i++)
        {
            var res = await SendMessageAsync(url, parts[i], name, ct).ConfigureAwait(false);

            // ⚠️ A later part gets a second try, as on Slack: the parts before it are already in
            // the channel, and nobody can simply run the whole thing again without repeating them.
            if (!res.Ok && i > 0)
            {
                await Task.Delay(RetryGap, ct).ConfigureAwait(false);
                res = await SendMessageAsync(url, parts[i], name, ct).ConfigureAwait(false);
            }

            if (!res.Ok) return new DiscordPartsResult(i, parts.Count, characters, res.Error);

            characters += parts[i].Length;
        }

        return new DiscordPartsResult(parts.Count, parts.Count, characters, null);
    }

    /// <summary>One message, which must already fit.</summary>
    private Task<DiscordPostResult> SendMessageAsync(string url, string content, string? name, CancellationToken ct)
        => SendAsync(url, () => new StringContent(
                JsonSerializer.Serialize(Payload(content)), Encoding.UTF8, "application/json"),
            name, ct);

    /// <summary>
    /// Posts one image, with an optional line of text above it.
    ///
    /// <para>Discord webhooks carry files, which Slack's cannot — so a scheduled task pointed at
    /// Discord sends its charts rather than skipping them. One file per message: the charts are
    /// small, and a message each keeps each under its own title.</para>
    /// </summary>
    public Task<DiscordPostResult> UploadAsync(
        string url, byte[] png, string filename, string? text = null,
        string? name = null, CancellationToken ct = default)
    {
        if (png.Length == 0) return Task.FromResult(new DiscordPostResult(false, SettingsText.DiscordErrNothingToPost));

        // ⚠️ ASCII only. A chart title in Chinese or Russian becomes the file name, and a
        // non-ASCII name travels as an RFC 5987 filename* parameter that Discord matches against
        // attachments[].filename less reliably than a plain one.
        var file = SafeFileName(filename);

        return SendAsync(url, () =>
        {
            var payload = Payload(text);
            payload["attachments"] = new[] { new Dictionary<string, object> { ["id"] = 0, ["filename"] = file } };

            var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), "payload_json");

            var bytes = new ByteArrayContent(png);
            bytes.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(bytes, "files[0]", file);
            return form;
        }, name, ct);
    }

    /// <summary>Posts one short line to a named webhook, to show whether it works.</summary>
    public async Task<DiscordPostResult> TestAsync(int id, CancellationToken ct = default)
    {
        var hook = await FindAsync(id, ct).ConfigureAwait(false);
        if (hook is null) return new DiscordPostResult(false, SettingsText.DiscordErrWebhookRemoved);
        return await SendMessageAsync(hook.Url, SettingsText.DiscordTestMessage, hook.Name, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The JSON every post carries.
    ///
    /// <para>⚠️ <c>allowed_mentions.parse</c> is always sent, and always empty. Left out, Discord
    /// resolves @everyone, @here, roles and users in the content — and none of what this app posts
    /// is meant to ping anybody.</para>
    /// </summary>
    private static Dictionary<string, object> Payload(string? content)
    {
        var payload = new Dictionary<string, object>
        {
            ["allowed_mentions"] = new Dictionary<string, object> { ["parse"] = Array.Empty<string>() },
        };
        if (!string.IsNullOrEmpty(content)) payload["content"] = content;
        return payload;
    }

    private static string SafeFileName(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename ?? "");
        var safe = new string(stem.Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'
                                               ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (safe.Contains("--", StringComparison.Ordinal)) safe = safe.Replace("--", "-", StringComparison.Ordinal);
        return (safe.Length == 0 ? "chart" : safe) + ".png";
    }

    // — Transport ——————————————————————————————————————————————————————

    /// <summary>
    /// When each webhook may next be posted to, from Discord's own rate-limit headers.
    ///
    /// <para>Keyed by the link, in memory only. A message that reports the bucket empty says how
    /// long until it refills, and the next part of the same post waits that long rather than
    /// walking into a 429.</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _notBefore = new(StringComparer.Ordinal);

    /// <summary>
    /// Sends one request, honouring rate limits, and turns the answer into something a person can
    /// read. The content is built afresh for each attempt, since a sent request disposes it.
    /// </summary>
    private async Task<DiscordPostResult> SendAsync(
        string url, Func<HttpContent> content, string? name, CancellationToken ct)
    {
        url = (url ?? "").Trim();
        var who = string.IsNullOrWhiteSpace(name) ? Redact(url) : $"\"{name}\"";

        if (!IsWebhookUrl(url)) return new DiscordPostResult(false, SettingsText.DiscordErrNotAWebhook);

        for (var attempt = 1; ; attempt++)
        {
            if (_notBefore.TryGetValue(url, out var until) && until > DateTimeOffset.UtcNow)
                await Task.Delay(Cap(until - DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            HttpResponseMessage? res = null;
            string body;
            try
            {
                using var client = _httpFactory.CreateClient("discord");
                using var req    = new HttpRequestMessage(HttpMethod.Post, WithWait(url)) { Content = content() };
                res  = await client.SendAsync(req, ct).ConfigureAwait(false);
                body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { res?.Dispose(); throw; }
            catch (Exception ex)
            {
                res?.Dispose();

                // A timeout arrives as a cancellation the caller did not ask for; either way the
                // server was not reached. ⚠️ Scrubbed: nothing guarantees an exception's text
                // leaves the request's address out.
                _errors.Log(nameof(DiscordService), $"post to webhook {who}", Scrub(ex.Message), Scrub(ex.InnerException?.Message));
                return new DiscordPostResult(false, SettingsText.DiscordErrUnreachable);
            }

            using (res)
            {
                NoteBucket(url, res);

                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= MaxAttempts)
                    {
                        _errors.Log(nameof(DiscordService), $"post to webhook {who}", "rate limited; gave up");
                        return new DiscordPostResult(false, SettingsText.DiscordErrRateLimited);
                    }

                    await Task.Delay(Cap(RetryAfter(res, body)), ct).ConfigureAwait(false);
                    continue;
                }

                if (res.IsSuccessStatusCode) return new DiscordPostResult(true, null);

                var status = (int)res.StatusCode;
                var said   = Scrub(DiscordMessage(body));
                _errors.Log(nameof(DiscordService), $"post to webhook {who}", $"HTTP {status}: {said}");

                // ⚠️ 401, 403 and 404 all mean the same thing to the person holding the link: the
                // webhook is gone, or the link was pasted wrong. Nothing retrying could fix.
                return status is 401 or 403 or 404
                    ? new DiscordPostResult(false, SettingsText.DiscordErrWebhookGone)
                    : new DiscordPostResult(false, string.Format(SettingsText.DiscordErrHttp, status,
                        said.Length > 0 ? said : res.ReasonPhrase ?? ""));
            }
        }
    }

    /// <summary>
    /// ⚠️ <c>wait=true</c>, always. Without it Discord answers 204 before it has checked anything,
    /// so a message it then drops still reads as sent. With it the answer is the message itself or
    /// a JSON error, and a failure is a failure.
    /// </summary>
    private static string WithWait(string url)
        => url.Contains('?') ? url + "&wait=true" : url + "?wait=true";

    /// <summary>Remembers an empty bucket, so the next message to this webhook waits it out.</summary>
    private void NoteBucket(string url, HttpResponseMessage res)
    {
        if (Header(res, "X-RateLimit-Remaining") is not "0") return;

        if (double.TryParse(Header(res, "X-RateLimit-Reset-After"), NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var secs) && secs > 0)
            _notBefore[url] = DateTimeOffset.UtcNow + Cap(TimeSpan.FromSeconds(secs));
    }

    /// <summary>How long a 429 asks to wait: the body's <c>retry_after</c> (fractional seconds)
    /// first, then the <c>Retry-After</c> header, then a second.</summary>
    private static TimeSpan RetryAfter(HttpResponseMessage res, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("retry_after", out var ra)
                && ra.TryGetDouble(out var secs) && secs >= 0)
                return TimeSpan.FromSeconds(secs);
        }
        catch (JsonException) { /* not JSON — fall through to the header */ }

        if (res.Headers.RetryAfter?.Delta is { } delta) return delta;

        return double.TryParse(Header(res, "Retry-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
            ? TimeSpan.FromSeconds(h)
            : TimeSpan.FromSeconds(1);
    }

    private static TimeSpan Cap(TimeSpan wait)
        => wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxRateWait ? MaxRateWait : wait;

    private static string? Header(HttpResponseMessage res, string name)
        => res.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>Discord's own words for a refusal — <c>{"message": "...", "code": n}</c> — or
    /// nothing when the body is not that.</summary>
    private static string DiscordMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "";
        }
        catch (JsonException) { /* an HTML error page from a proxy, say */ }

        return "";
    }
}
