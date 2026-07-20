using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EveCortex.Services;

public record SlackAuthResult(bool Ok, string? User, string? Team, string? Error);

// Ts is the posted message's id — persist it (with Channel) to thread replies under it later.
public record SlackPostResult(bool Ok, string? Channel, string? Ts, string? Error);

public class SlackChannel
{
    public string Id        { get; init; } = "";
    public string Name      { get; init; } = "";
    public bool   IsPrivate { get; init; }
    public override string ToString() => IsPrivate ? $"🔒 {Name}" : $"# {Name}";
}

/// <summary>
/// Posts to Slack on the capsuleer's behalf using a user token (xoxp-), so messages appear as
/// them rather than as an app. The token is created by the user in their own workspace
/// (api.slack.com/apps → User Token Scopes → Install), so no client secret ships with EveCortex.
/// Slack returns HTTP 200 even for failures, with {"ok":false,"error":"..."} — always check "ok".
/// </summary>
public class SlackService
{
    public const string TokenKey = "slack.user_token";

    // Areas of the app that post to Slack; each maps to its own configured channel.
    public const string AreaCorpTop10 = "corp_top10";

    private static string ChanIdKey(string area)   => $"slack.channel.{area}.id";
    private static string ChanNameKey(string area) => $"slack.channel.{area}.name";

    private readonly IHttpClientFactory     _httpFactory;
    private readonly AppPreferencesService  _prefs;
    private readonly AppErrorLogger         _errors;

    public SlackService(IHttpClientFactory httpFactory, AppPreferencesService prefs, AppErrorLogger errors)
    {
        _httpFactory = httpFactory;
        _prefs       = prefs;
        _errors      = errors;
    }

    public string? Token       => _prefs.Get(TokenKey);
    public bool    HasToken    => !string.IsNullOrWhiteSpace(Token);
    public string? ChannelId(string area)   => _prefs.Get(ChanIdKey(area));
    public string? ChannelName(string area) => _prefs.Get(ChanNameKey(area));

    /// <summary>True when both a token and a channel for this area are set.</summary>
    public bool IsConfigured(string area)
        => HasToken && !string.IsNullOrWhiteSpace(ChannelId(area));

    public Task SetTokenAsync(string? token)
        => _prefs.SetAsync(TokenKey, string.IsNullOrWhiteSpace(token) ? null : token.Trim());

    public async Task SetChannelAsync(string area, SlackChannel? channel)
    {
        await _prefs.SetAsync(ChanIdKey(area),   channel?.Id);
        await _prefs.SetAsync(ChanNameKey(area), channel?.Name);
    }

    // ── API ──────────────────────────────────────────────────────────────────

    /// <summary>Validates a token and returns who it posts as. Pass a token to test before saving.</summary>
    public async Task<SlackAuthResult> TestAuthAsync(string? token = null, CancellationToken ct = default)
    {
        try
        {
            using var client = Client(token);
            using var res    = await client.PostAsync("auth.test", null, ct);
            using var doc    = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            return IsOk(root)
                ? new SlackAuthResult(true, Str(root, "user"), Str(root, "team"), null)
                : new SlackAuthResult(false, null, null, Err(root));
        }
        catch (Exception ex) { return new SlackAuthResult(false, null, null, ex.Message); }
    }

    /// <summary>Public + private channels the user can see, for the channel pickers.</summary>
    public async Task<(List<SlackChannel> Channels, string? Error)> ListChannelsAsync(CancellationToken ct = default)
    {
        var all = new List<SlackChannel>();
        try
        {
            using var client = Client(null);
            string? cursor = null;
            do
            {
                var url = "conversations.list?types=public_channel,private_channel"
                        + "&exclude_archived=true&limit=200"
                        + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

                using var res = await client.GetAsync(url, ct);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (!IsOk(root)) return (all, Err(root));

                if (root.TryGetProperty("channels", out var chans))
                    foreach (var c in chans.EnumerateArray())
                        all.Add(new SlackChannel
                        {
                            Id        = Str(c, "id")   ?? "",
                            Name      = Str(c, "name") ?? "",
                            IsPrivate = c.TryGetProperty("is_private", out var p) && p.ValueKind == JsonValueKind.True,
                        });

                cursor = root.TryGetProperty("response_metadata", out var meta)
                      && meta.TryGetProperty("next_cursor", out var nc) ? nc.GetString() : null;
            }
            while (!string.IsNullOrEmpty(cursor));

            return (all.Where(c => c.Id.Length > 0).OrderBy(c => c.Name).ToList(), null);
        }
        catch (Exception ex) { return (all, ex.Message); }
    }

    /// <summary>
    /// Posts as the token's user. Pass threadTs to reply under an existing message; broadcast also
    /// surfaces that reply in the channel (edits never resurface a message, threaded broadcasts do).
    /// </summary>
    public async Task<SlackPostResult> PostMessageAsync(
        string channelId, string text, string? threadTs = null, bool broadcast = false,
        CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["channel"] = channelId,
                ["text"]    = text,
            };
            if (!string.IsNullOrEmpty(threadTs))
            {
                payload["thread_ts"] = threadTs;
                if (broadcast) payload["reply_broadcast"] = true;
            }

            using var client  = Client(null);
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var res = await client.PostAsync("chat.postMessage", content, ct);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            if (!IsOk(root))
            {
                var err = Err(root);
                _errors.Log("SlackService", $"chat.postMessage channel={channelId}", err ?? "unknown error");
                return new SlackPostResult(false, null, null, err);
            }
            return new SlackPostResult(true, Str(root, "channel"), Str(root, "ts"), null);
        }
        catch (Exception ex)
        {
            _errors.Log("SlackService", $"chat.postMessage channel={channelId}", ex);
            return new SlackPostResult(false, null, null, ex.Message);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HttpClient Client(string? token)
    {
        var client = _httpFactory.CreateClient("slack");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", (token ?? Token ?? "").Trim());
        return client;
    }

    private static bool IsOk(JsonElement root)
        => root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Slack error codes are terse (invalid_auth, not_in_channel, channel_not_found…) — surface as-is.
    private static string? Err(JsonElement root) => Str(root, "error") ?? "unknown error";
}
