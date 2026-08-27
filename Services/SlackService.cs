using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EveConsole.Auth;

namespace EveConsole.Services;

public record SlackAuthResult(bool Ok, string? User, string? Team, string? Error, string? UserId = null);

// Ts is the posted message's id — persist it (with Channel) to thread replies under it later.
public record SlackPostResult(bool Ok, string? Channel, string? Ts, string? Error);

public class SlackChannel
{
    public string Id        { get; init; } = "";
    public string Name      { get; init; } = "";
    public bool   IsPrivate { get; init; }
    // Your own DM-with-yourself — Slack's "note to self" conversation. Other people's DMs aren't
    // surfaced (conversations.list gives no name for them without the users:read scope).
    public bool   IsSelfDm  { get; init; }
    public override string ToString() => IsSelfDm ? $"📝 {Name}" : IsPrivate ? $"🔒 {Name}" : $"# {Name}";
}

/// <summary>
/// Posts to Slack on the capsuleer's behalf using a user token (xoxp-), so messages appear as
/// them rather than as an app. The token is created by the user in their own workspace
/// (api.slack.com/apps → User Token Scopes → Install), so no client secret ships with EVE Console.
/// Slack returns HTTP 200 even for failures, with {"ok":false,"error":"..."} — always check "ok".
/// </summary>
public class SlackService
{
    public const string TokenKey    = "slack.user_token";
    private const string RefreshKey = "slack.refresh_token";
    private const string ExpiresKey = "slack.token_expires_at";
    private const string TeamKey    = "slack.team_name";
    private const string SelfIdKey  = "slack.self_user_id";

    // Areas of the app that post to Slack; each maps to its own configured channel.
    public const string AreaCorpTop10   = "corp_top10";
    public const string AreaCorpMonthly = "corp_monthly";
    public const string AreaSalePosting = "sale_posting";

    private static string ChanIdKey(string area)   => $"slack.channel.{area}.id";
    private static string ChanNameKey(string area) => $"slack.channel.{area}.name";
    private static string LastPostKey(string area) => $"slack.lastpost.{area}";
    private static string WebhookKey(string area)  => $"slack.webhook.{area}";

    /// <summary>
    /// The incoming-webhook URL for an area, or empty when this area posts as the user.
    ///
    /// <para>⚠️ A webhook is bound to ONE channel by whoever created it — the URL carries the
    /// destination, so nothing here chooses where it lands. That is the point of it: an alliance
    /// workspace will hand out a webhook where it would never hand out a user token.</para>
    /// </summary>
    public string WebhookUrl(string area) => (_prefs.Get(WebhookKey(area)) ?? "").Trim();

    public Task SetWebhookUrlAsync(string area, string? url) =>
        _prefs.SetAsync(WebhookKey(area), (url ?? "").Trim());

    /// <summary>Whether this area posts through a webhook rather than as the connected user.</summary>
    public bool UsesWebhook(string area) => WebhookUrl(area).Length > 0;

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
    public string? SelfUserId  => _prefs.Get(SelfIdKey);
    public string? ChannelId(string area)   => _prefs.Get(ChanIdKey(area));
    public string? ChannelName(string area) => _prefs.Get(ChanNameKey(area));

    /// <summary>True when both a token and a channel for this area are set.</summary>
    /// <summary>
    /// Whether this area can post at all — as the connected user, or through a webhook.
    ///
    /// <para>⚠️ Either route counts. This gates the Post buttons, and while it asked only about a
    /// token a webhook-only setup could be configured, tested successfully, and still have no
    /// button to press — the one workspace a webhook exists to reach was the one the app would
    /// not offer to post to.</para>
    /// </summary>
    public bool IsConfigured(string area)
        => UsesWebhook(area)
        || (HasToken && !string.IsNullOrWhiteSpace(ChannelId(area)));

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
        await _prefs.SetAsync(SelfIdKey,  null);
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

            if (!IsOk(root)) return new SlackAuthResult(false, null, null, Err(root));

            // user_id identifies your own self-DM ("note to self") in the channel picker — it needs
            // no extra scope, auth.test always returns it.
            var userId = Str(root, "user_id");
            if (userId is not null) await _prefs.SetAsync(SelfIdKey, userId);
            return new SlackAuthResult(true, Str(root, "user"), Str(root, "team"), null, userId);
        }
        catch (Exception ex) { return new SlackAuthResult(false, null, null, ex.Message); }
    }

    /// <summary>Public + private channels the user can see, plus their own self-DM, for the
    /// channel pickers. Other people's DMs aren't listed — conversations.list gives no name for
    /// them without the users:read scope.</summary>
    public async Task<(List<SlackChannel> Channels, string? Error)> ListChannelsAsync(CancellationToken ct = default)
    {
        var all    = new List<SlackChannel>();
        var selfId = SelfUserId;
        try
        {
            await EnsureFreshTokenAsync(ct);
            using var client = Client(null);
            string? cursor = null;
            do
            {
                var url = "conversations.list?types=public_channel,private_channel,im"
                        + "&exclude_archived=true&limit=200"
                        + (string.IsNullOrEmpty(cursor) ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

                using var res = await client.GetAsync(url, ct);
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = doc.RootElement;
                if (!IsOk(root)) return (all, Err(root));

                if (root.TryGetProperty("channels", out var chans))
                    foreach (var c in chans.EnumerateArray())
                    {
                        bool isIm = c.TryGetProperty("is_im", out var im) && im.ValueKind == JsonValueKind.True;
                        if (isIm)
                        {
                            // A DM's "user" is the other party — only your own self-DM is nameable
                            // without users:read, so that's the only DM we surface.
                            if (selfId is null || Str(c, "user") != selfId) continue;
                            all.Add(new SlackChannel { Id = Str(c, "id") ?? "", Name = "Note to Self", IsSelfDm = true });
                            continue;
                        }
                        all.Add(new SlackChannel
                        {
                            Id        = Str(c, "id")   ?? "",
                            Name      = Str(c, "name") ?? "",
                            IsPrivate = c.TryGetProperty("is_private", out var p) && p.ValueKind == JsonValueKind.True,
                        });
                    }

                cursor = root.TryGetProperty("response_metadata", out var meta)
                      && meta.TryGetProperty("next_cursor", out var nc) ? nc.GetString() : null;
            }
            while (!string.IsNullOrEmpty(cursor));

            return (all.Where(c => c.Id.Length > 0)
                       .OrderBy(c => c.IsSelfDm ? 0 : 1).ThenBy(c => c.Name)
                       .ToList(), null);
        }
        catch (Exception ex) { return (all, ex.Message); }
    }

    /// <summary>
    /// Posts as the token's user. Pass threadTs to reply under an existing message; broadcast also
    /// surfaces that reply in the channel (edits never resurface a message, threaded broadcasts do).
    /// Pass blocks for rich formatting Slack's legacy mrkdwn text field can't do (e.g. underline) —
    /// text is still sent as the notification/accessibility fallback Slack recommends alongside it.
    /// </summary>
    public async Task<SlackPostResult> PostMessageAsync(
        string channelId, string text, string? threadTs = null, bool broadcast = false,
        object? blocks = null, CancellationToken ct = default)
    {
        try
        {
            await EnsureFreshTokenAsync(ct);
            var payload = new Dictionary<string, object?>
            {
                ["channel"] = channelId,
                ["text"]    = text,
            };
            if (blocks is not null) payload["blocks"] = blocks;
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

    /// <summary>
    /// Posts whatever this area is configured for: a webhook when one is set, otherwise the
    /// connected user's token.
    ///
    /// <para>⚠️ The two are not equivalent, and the difference is threading. A webhook returns no
    /// message id, so nothing posted through one can be replied to — a sale posting that would
    /// have put its detail in a thread posts it as a second message instead. Everything else is
    /// the same, including block formatting.</para>
    /// </summary>
    public async Task<SlackPostResult> PostAreaAsync(
        string area, string text, string? threadTs = null, bool broadcast = false,
        object? blocks = null, CancellationToken ct = default)
    {
        var hook = WebhookUrl(area);
        if (hook.Length == 0)
            return await PostMessageAsync(ChannelId(area) ?? "", text, threadTs, broadcast, blocks, ct);

        // ⚠️ threadTs is dropped rather than sent. A webhook has never returned an id for anything,
        // so any value reaching here came from a different post — threading onto it would attach
        // this message under an unrelated parent.
        return await PostWebhookAsync(hook, text, blocks, ct);
    }

    /// <summary>
    /// Posts to an incoming webhook.
    ///
    /// <para>⚠️ No bearer token, and no JSON envelope to check: a webhook answers with the literal
    /// body "ok" and an HTTP status, not with Slack's usual <c>{"ok":true}</c>. Parsing the reply
    /// as JSON throws on success.</para>
    /// </summary>
    public async Task<SlackPostResult> PostWebhookAsync(
        string url, string text, object? blocks = null, CancellationToken ct = default)
    {
        try
        {
            var payload = new Dictionary<string, object?> { ["text"] = text };
            if (blocks is not null) payload["blocks"] = blocks;

            // ⚠️ A bare client, not Client(): a webhook URL is its own credential and carries the
            // whole destination. Sending an Authorization header alongside it is at best noise and
            // at worst a token posted to a workspace that is not the token's own.
            using var client  = _httpFactory.CreateClient();
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var res  = await client.PostAsync(url, content, ct);
            var       body = (await res.Content.ReadAsStringAsync(ct)).Trim();

            if (res.IsSuccessStatusCode && body.Equals("ok", StringComparison.OrdinalIgnoreCase))
                return new SlackPostResult(true, null, null, null);

            // Webhook errors are plain words — invalid_payload, channel_not_found, no_service.
            var err = body.Length > 0 ? body : $"HTTP {(int)res.StatusCode}";
            _errors.Log("SlackService", "incoming webhook", err);
            return new SlackPostResult(false, null, null, err);
        }
        catch (Exception ex)
        {
            _errors.Log("SlackService", "incoming webhook", ex);
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
