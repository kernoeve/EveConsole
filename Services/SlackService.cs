using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EveCortex.Auth;

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
    public const string TokenKey    = "slack.user_token";
    private const string RefreshKey = "slack.refresh_token";
    private const string ExpiresKey = "slack.token_expires_at";
    private const string TeamKey    = "slack.team_name";

    // Areas of the app that post to Slack; each maps to its own configured channel.
    public const string AreaCorpTop10 = "corp_top10";

    private static string ChanIdKey(string area)   => $"slack.channel.{area}.id";
    private static string ChanNameKey(string area) => $"slack.channel.{area}.name";
    private static string LastPostKey(string area) => $"slack.lastpost.{area}";

    private readonly IHttpClientFactory     _httpFactory;
    private readonly AppPreferencesService  _prefs;
    private readonly AppErrorLogger         _errors;
    private readonly SlackAuthService       _auth;

    public SlackService(IHttpClientFactory httpFactory, AppPreferencesService prefs,
                        AppErrorLogger errors, SlackAuthService auth)
    {
        _httpFactory = httpFactory;
        _prefs       = prefs;
        _errors      = errors;
        _auth        = auth;
    }

    public string? Token       => _prefs.Get(TokenKey);
    public bool    HasToken    => !string.IsNullOrWhiteSpace(Token);
    public string? TeamName    => _prefs.Get(TeamKey);
    public string? ChannelId(string area)   => _prefs.Get(ChanIdKey(area));
    public string? ChannelName(string area) => _prefs.Get(ChanNameKey(area));

    /// <summary>True when both a token and a channel for this area are set.</summary>
    public bool IsConfigured(string area)
        => HasToken && !string.IsNullOrWhiteSpace(ChannelId(area));

    public Task SetTokenAsync(string? token)
        => _prefs.SetAsync(TokenKey, string.IsNullOrWhiteSpace(token) ? null : token.Trim());

    /// <summary>When this area last posted successfully — used to warn about accidental reposts.</summary>
    public DateTimeOffset? LastPostAt(string area)
        => DateTimeOffset.TryParse(_prefs.Get(LastPostKey(area)), out var t) ? t : null;

    public Task SetLastPostAsync(string area, DateTimeOffset when)
        => _prefs.SetAsync(LastPostKey(area), when.ToString("o"));

    /// <summary>Runs the PKCE browser flow and stores the resulting user token.</summary>
    public async Task<SlackAuthResult> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            var tokens = await _auth.LoginAsync(ct);
            await StoreAsync(tokens);
            // auth.test confirms the token works and gives us the display name to show.
            return await TestAuthAsync(ct: ct);
        }
        catch (OperationCanceledException) { return new SlackAuthResult(false, null, null, "Cancelled."); }
        catch (Exception ex)
        {
            _errors.Log("SlackService", "Connect", ex);
            return new SlackAuthResult(false, null, null, ex.Message);
        }
    }

    /// <summary>Clears the stored token and its refresh state.</summary>
    public async Task DisconnectAsync()
    {
        await _prefs.SetAsync(TokenKey,   null);
        await _prefs.SetAsync(RefreshKey, null);
        await _prefs.SetAsync(ExpiresKey, null);
        await _prefs.SetAsync(TeamKey,    null);
    }

    private async Task StoreAsync(SlackTokenSet t)
    {
        await _prefs.SetAsync(TokenKey,   t.AccessToken);
        await _prefs.SetAsync(RefreshKey, t.RefreshToken);
        await _prefs.SetAsync(ExpiresKey, t.ExpiresAt?.ToString("o"));
        if (t.TeamName is not null) await _prefs.SetAsync(TeamKey, t.TeamName);
    }

    /// <summary>
    /// Renews the token when it's rotating and close to expiry. Non-rotating tokens (the usual
    /// case for a loopback redirect) have no refresh token and are left alone.
    /// </summary>
    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        var refresh = _prefs.Get(RefreshKey);
        if (string.IsNullOrEmpty(refresh)) return;
        if (!DateTimeOffset.TryParse(_prefs.Get(ExpiresKey), out var expiresAt)) return;
        if (DateTimeOffset.UtcNow < expiresAt.AddMinutes(-5)) return;

        try { await StoreAsync(await _auth.RefreshAsync(refresh, ct)); }
        catch (Exception ex) { _errors.Log("SlackService", "RefreshToken", ex); }
    }

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
            if (token is null) await EnsureFreshTokenAsync(ct);
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
            await EnsureFreshTokenAsync(ct);
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
            await EnsureFreshTokenAsync(ct);
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
