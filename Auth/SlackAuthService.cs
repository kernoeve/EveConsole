using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveCortex.Auth;

/// <summary>Result of a successful Slack user-token authorization.</summary>
public record SlackTokenSet(
    string  AccessToken,       // xoxp- user token — posts are attributed to this user
    string? RefreshToken,      // only present when the app issues rotating tokens
    DateTimeOffset? ExpiresAt, // only meaningful alongside RefreshToken
    string? UserId,
    string? TeamName);

/// <summary>
/// Slack OAuth using PKCE (generally available since 2026-03-30), which lets a public client —
/// a distributed desktop app — complete the flow with no client secret, exactly like EVE's SSO.
/// One Eve Cortex Slack app serves every user; each authorizes it into their own workspace.
///
/// Only USER scopes are requested, so posts appear as the capsuleer rather than as a bot.
/// (Slack forbids bot scopes on desktop redirects anyway.)
/// </summary>
public class SlackAuthService
{
    // -----------------------------------------------------------------------
    // Register the Eve Cortex app at https://api.slack.com/apps, then:
    //   • OAuth & Permissions → enable PKCE (this is permanent — it marks the
    //     app a public client)
    //   • add the User Token Scopes listed below
    //   • add the redirect URL below
    //   • Manage Distribution → activate public distribution so other
    //     workspaces can install it
    // The client id is public by design (same as the ESI one) — no secret ships.
    // -----------------------------------------------------------------------
    public const string ClientId = "5825064640678.11610347528311";

    private const string CallbackUrl   = "http://localhost:5051/slack/callback";
    private const string ListenPrefix  = "http://localhost:5051/slack/callback/";
    private const string AuthEndpoint  = "https://slack.com/oauth/v2/authorize";
    private const string TokenEndpoint = "https://slack.com/api/oauth.v2.access";

    /// <summary>User scopes requested. chat:write posts as the user; the read scopes list
    /// conversations for the channel pickers (im/mpim are for planned notification work).</summary>
    public static readonly string[] UserScopes =
    [
        "chat:write",
        "channels:read",
        "groups:read",
        "im:read",
        "mpim:read",
    ];

    /// <summary>False until a Client ID is compiled in; the UI hides "Connect" and falls back
    /// to pasting a token manually.</summary>
    public static bool IsAvailable => ClientId.Length > 0;

    private readonly HttpClient _http;

    public SlackAuthService(IHttpClientFactory httpFactory)
        => _http = httpFactory.CreateClient("slack");

    /// <summary>Runs the full browser authorization and returns the user token.</summary>
    public async Task<SlackTokenSet> LoginAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("No Slack Client ID is configured in this build.");

        var (verifier, challenge) = GeneratePkce();
        var state = GenerateState();

        // Start listening before opening the browser so a fast redirect can't be missed.
        var callbackTask = ListenForCallbackAsync(state, ct);
        Process.Start(new ProcessStartInfo(BuildAuthUrl(challenge, state)) { UseShellExecute = true });

        var code = await callbackTask;
        return await ExchangeCodeAsync(code, verifier, ct);
    }

    /// <summary>Renews a rotating token. PKCE apps refresh without a client secret too.</summary>
    public async Task<SlackTokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = ClientId,
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken,
        });
        return await PostTokenAsync(body, ct);
    }

    // -----------------------------------------------------------------------

    private static string BuildAuthUrl(string challenge, string state)
        => $"{AuthEndpoint}" +
           $"?client_id={ClientId}" +
           // user_scope (not scope) — we want a user token, not a bot token
           $"&user_scope={Uri.EscapeDataString(string.Join(",", UserScopes))}" +
           $"&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
           $"&state={state}" +
           $"&code_challenge={challenge}" +
           $"&code_challenge_method=S256";

    private async Task<SlackTokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        // No client_secret — PKCE proves this is the client that started the flow.
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"]     = ClientId,
            ["code"]          = code,
            ["redirect_uri"]  = CallbackUrl,
            ["code_verifier"] = verifier,
        });
        return await PostTokenAsync(body, ct);
    }

    private async Task<SlackTokenSet> PostTokenAsync(FormUrlEncodedContent body, CancellationToken ct)
    {
        using var response = await _http.PostAsync(TokenEndpoint, body, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        // Slack answers 200 even for failures — the envelope carries the verdict.
        if (!(root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True))
            throw new InvalidOperationException($"Slack authorization failed: {Str(root, "error") ?? "unknown error"}");

        if (!root.TryGetProperty("authed_user", out var user))
            throw new InvalidOperationException("Slack returned no user token — were User Token Scopes requested?");

        var access = Str(user, "access_token")
            ?? throw new InvalidOperationException("Slack returned no user access token.");

        DateTimeOffset? expiresAt = user.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var secs)
            ? DateTimeOffset.UtcNow.AddSeconds(secs) : null;

        string? teamName = root.TryGetProperty("team", out var team) ? Str(team, "name") : null;

        return new SlackTokenSet(access, Str(user, "refresh_token"), expiresAt, Str(user, "id"), teamName);
    }

    private static async Task<string> ListenForCallbackAsync(string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();

        var contextTask = listener.GetContextAsync();
        var cancelTask  = Task.Delay(Timeout.Infinite, ct);
        if (await Task.WhenAny(contextTask, cancelTask) == cancelTask)
            throw new OperationCanceledException("Slack authorization timed out or was cancelled.");

        var context = await contextTask;
        var query   = context.Request.QueryString;

        // Slack reports a declined/failed authorization here rather than sending a code.
        if (query["error"] is { Length: > 0 } err)
        {
            await RespondAsync(context, FailureHtml, ct);
            listener.Stop();
            throw new InvalidOperationException($"Slack authorization failed: {err}");
        }

        var code  = query["code"]  ?? throw new InvalidOperationException("No code in Slack callback.");
        var state = query["state"] ?? throw new InvalidOperationException("No state in Slack callback.");
        if (state != expectedState)
            throw new InvalidOperationException("State mismatch — possible CSRF.");

        await RespondAsync(context, SuccessHtml, ct);
        listener.Stop();
        return code;
    }

    private static async Task RespondAsync(HttpListenerContext context, string html, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType     = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.Close();
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── PKCE helpers ────────────────────────────────────────────────────────

    private static (string verifier, string challenge) GeneratePkce()
    {
        var verifier  = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string GenerateState()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Browser result pages ────────────────────────────────────────────────

    private const string SuccessHtml = """
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8" />
        <title>Eve Cortex — Slack Connected</title><style>
        :root{color-scheme:dark}html,body{height:100%;margin:0}
        body{display:flex;align-items:center;justify-content:center;
        background:radial-gradient(1200px 600px at 50% -10%,#16161f 0%,#0d0d12 60%);
        font-family:'Segoe UI',system-ui,-apple-system,Roboto,sans-serif;color:#aaaabc}
        .card{width:440px;max-width:90vw;background:#12121a;border:1px solid #1e1e28;border-radius:10px;
        padding:40px 44px 36px;text-align:center;box-shadow:0 20px 60px rgba(0,0,0,.5)}
        .brand{font-size:24px;font-weight:700;letter-spacing:2px;margin-bottom:22px}
        .brand .eve{color:#e8e8ec}.brand .cortex{color:#c8a84b}
        h1{color:#e8e8ec;font-size:20px;font-weight:600;margin:0 0 10px}
        p{font-size:14px;line-height:21px;margin:0}
        .hint{color:#555566;font-size:12px;margin-top:22px}</style></head>
        <body><div class="card">
        <div class="brand"><span class="eve">EVE</span> <span class="cortex">CORTEX</span></div>
        <h1>Slack connected</h1>
        <p>Eve Cortex can now post to Slack as you.</p>
        <p class="hint">You can close this tab and return to the app.</p>
        </div></body></html>
        """;

    private const string FailureHtml = """
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8" />
        <title>Eve Cortex — Slack Authorization Failed</title><style>
        :root{color-scheme:dark}html,body{height:100%;margin:0}
        body{display:flex;align-items:center;justify-content:center;
        background:radial-gradient(1200px 600px at 50% -10%,#16161f 0%,#0d0d12 60%);
        font-family:'Segoe UI',system-ui,-apple-system,Roboto,sans-serif;color:#aaaabc}
        .card{width:440px;max-width:90vw;background:#12121a;border:1px solid #1e1e28;border-radius:10px;
        padding:40px 44px 36px;text-align:center}
        h1{color:#e8e8ec;font-size:20px;margin:0 0 10px}
        p{font-size:14px;line-height:21px;margin:0}
        .hint{color:#555566;font-size:12px;margin-top:22px}</style></head>
        <body><div class="card">
        <h1>Authorization cancelled</h1>
        <p>Eve Cortex was not connected to Slack.</p>
        <p class="hint">You can close this tab and try again from Settings.</p>
        </div></body></html>
        """;
}
