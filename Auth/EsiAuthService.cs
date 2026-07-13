using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveCortex.Auth;

/// <summary>
/// Handles the Eve ESI OAuth2 PKCE authentication flow.
/// Opens the system browser for login, catches the redirect via a local HTTP listener,
/// and exchanges the auth code for access/refresh tokens.
/// </summary>
public class EsiAuthService
{
    // -----------------------------------------------------------------------
    // Register your app at https://developers.eveonline.com
    // Set the callback URL to: http://localhost:5050/callback
    // -----------------------------------------------------------------------
    private const string ClientId      = "f56351b397494c5eb8de65f760e630bc";
    private const string CallbackUrl   = "http://localhost:5050/callback";
    private const string AuthEndpoint  = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenEndpoint = "https://login.eveonline.com/v2/oauth/token";

    // Character-level scopes: no "corporation" in the scope name, not esi-corporations.* group.
    public static readonly string[] CharacterScopes =
    [
        "esi-access.read_lists.v1",
        "esi-activities.read_character.v1",
        "esi-alliances.read_contacts.v1",
        "esi-assets.read_assets.v1",
        "esi-calendar.read_calendar_events.v1",
        "esi-calendar.respond_calendar_events.v1",
        "esi-characters.read_agents_research.v1",
        "esi-characters.read_blueprints.v1",
        "esi-characters.read_chat_channels.v1",
        "esi-characters.read_contacts.v1",
        // Reads the character's OWN corp roles (/characters/{id}/roles/) — a character-token
        // scope, despite the "corporation" in the name. Must live here so character tokens
        // request it; the corp roles endpoint uses read_corporation_membership instead.
        "esi-characters.read_corporation_roles.v1",
        "esi-characters.read_fatigue.v1",
        "esi-characters.read_freelance_jobs.v1",
        "esi-characters.read_fw_stats.v1",
        "esi-characters.read_loyalty.v1",
        "esi-characters.read_medals.v1",
        "esi-characters.read_notifications.v1",
        "esi-characters.read_standings.v1",
        "esi-characters.read_titles.v1",
        "esi-characters.write_contacts.v1",
        "esi-clones.read_clones.v1",
        "esi-clones.read_implants.v1",
        "esi-contracts.read_character_contracts.v1",
        "esi-fittings.read_fittings.v1",
        "esi-fittings.write_fittings.v1",
        "esi-fleets.read_fleet.v1",
        "esi-fleets.write_fleet.v1",
        "esi-industry.read_character_jobs.v1",
        "esi-industry.read_character_mining.v1",
        "esi-killmails.read_killmails.v1",
        "esi-location.read_location.v1",
        "esi-location.read_online.v1",
        "esi-location.read_ship_type.v1",
        "esi-mail.organize_mail.v1",
        "esi-mail.read_mail.v1",
        "esi-mail.send_mail.v1",
        "esi-markets.read_character_orders.v1",
        "esi-markets.structure_markets.v1",
        "esi-planets.manage_planets.v1",
        "esi-planets.read_customs_offices.v1",
        "esi-search.search_structures.v1",
        "esi-skills.read_skills.v1",
        "esi-skills.read_skillqueue.v1",
        "esi-structures.read_character.v1",
        "esi-ui.open_window.v1",
        "esi-ui.write_waypoint.v1",
        "esi-universe.read_structures.v1",
        "esi-wallet.read_character_wallet.v1",
    ];

    // Corporation-level scopes: "corporation" in the scope name OR esi-corporations.* group.
    // ESI calls succeed only if the authenticated character holds the required in-game corp roles.
    public static readonly string[] CorporationScopes =
    [
        "esi-assets.read_corporation_assets.v1",
        "esi-contracts.read_corporation_contracts.v1",
        "esi-corporations.read_blueprints.v1",
        "esi-corporations.read_contacts.v1",
        "esi-corporations.read_container_logs.v1",
        "esi-corporations.read_corporation_membership.v1",
        "esi-corporations.read_divisions.v1",
        "esi-corporations.read_facilities.v1",
        "esi-corporations.read_freelance_jobs.v1",
        "esi-corporations.read_fw_stats.v1",
        "esi-corporations.read_medals.v1",
        "esi-corporations.read_projects.v1",
        "esi-corporations.read_standings.v1",
        "esi-corporations.read_starbases.v1",
        "esi-corporations.read_structures.v1",
        "esi-corporations.read_titles.v1",
        "esi-corporations.track_members.v1",
        "esi-industry.read_corporation_jobs.v1",
        "esi-industry.read_corporation_mining.v1",
        "esi-killmails.read_corporation_killmails.v1",
        "esi-markets.read_corporation_orders.v1",
        "esi-structures.read_corporation.v1",
        "esi-wallet.read_corporation_wallet.v1",
        "esi-wallet.read_corporation_wallets.v1",
    ];

    // Combined — used for display in the character details panel (all possible scopes).
    public static readonly string[] AllScopes = [.. CharacterScopes, .. CorporationScopes];

    // Alias kept so existing call sites compile without change.
    public static readonly string[] DefaultCharacterScopes = CharacterScopes;

    private readonly HttpClient _http;

    public EsiAuthService(HttpClient http)
    {
        _http = http;
    }

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Launches the full browser-based OAuth2 PKCE login and returns a token set.
    /// Pass custom scopes to request corporation-level access.
    /// </summary>
    public async Task<TokenSet> LoginAsync(string[]? scopes = null, CancellationToken ct = default)
    {
        var scopesToUse = scopes ?? DefaultCharacterScopes;
        var (verifier, challenge) = GeneratePkce();
        var state = GenerateState();

        var authUrl = BuildAuthUrl(challenge, state, scopesToUse);
        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        var code = await ListenForCallbackAsync(state, ct);
        return await ExchangeCodeAsync(code, verifier, ct);
    }

    /// <summary>
    /// Uses a saved refresh token to obtain a new access token without a browser prompt.
    /// </summary>
    public async Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"]     = ClientId,
        });

        var response = await _http.PostAsync(TokenEndpoint, body, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        return TokenSet.FromResponse(json!);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static string BuildAuthUrl(string challenge, string state, string[] scopes)
    {
        var scope = Uri.EscapeDataString(string.Join(" ", scopes));
        return $"{AuthEndpoint}" +
               $"?response_type=code" +
               $"&client_id={ClientId}" +
               $"&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
               $"&scope={scope}" +
               $"&state={state}" +
               $"&code_challenge={challenge}" +
               $"&code_challenge_method=S256";
    }

    private static async Task<string> ListenForCallbackAsync(string expectedState, CancellationToken ct)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:5050/callback/");
        listener.Start();

        var contextTask = listener.GetContextAsync();
        var cancelTask  = Task.Delay(Timeout.Infinite, ct);

        var completed = await Task.WhenAny(contextTask, cancelTask);
        if (completed == cancelTask)
            throw new OperationCanceledException("Login timed out or was cancelled.");

        var context = await contextTask;
        var query   = context.Request.QueryString;
        var code    = query["code"]  ?? throw new InvalidOperationException("No code in callback.");
        var state   = query["state"] ?? throw new InvalidOperationException("No state in callback.");

        if (state != expectedState)
            throw new InvalidOperationException("State mismatch — possible CSRF.");

        var html = Encoding.UTF8.GetBytes(LoginSuccessHtml);
        context.Response.ContentType     = "text/html; charset=utf-8";
        context.Response.ContentLength64 = html.Length;
        await context.Response.OutputStream.WriteAsync(html, ct);
        context.Response.Close();

        listener.Stop();
        return code;
    }

    // App-themed page shown in the browser after a successful ESI SSO callback.
    private const string LoginSuccessHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Eve Cortex — Login Successful</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            html, body { height: 100%; margin: 0; }
            body {
              display: flex; align-items: center; justify-content: center;
              background: radial-gradient(1200px 600px at 50% -10%, #16161f 0%, #0d0d12 60%);
              font-family: 'Segoe UI', system-ui, -apple-system, Roboto, sans-serif;
              color: #aaaabc;
            }
            .card {
              width: 440px; max-width: 90vw;
              background: #12121a;
              border: 1px solid #1e1e28;
              border-radius: 10px;
              padding: 40px 44px 36px;
              text-align: center;
              box-shadow: 0 20px 60px rgba(0,0,0,0.5);
            }
            .brand { font-size: 24px; font-weight: 700; letter-spacing: 2px; margin-bottom: 22px; }
            .brand .eve { color: #e8e8ec; }
            .brand .cortex { color: #c8a84b; }
            .check {
              width: 66px; height: 66px; margin: 6px auto 20px;
              border-radius: 50%;
              background: rgba(112, 173, 71, 0.12);
              border: 2px solid #70ad47;
              display: flex; align-items: center; justify-content: center;
            }
            .check svg { width: 34px; height: 34px; stroke: #70ad47; stroke-width: 3;
              fill: none; stroke-linecap: round; stroke-linejoin: round; }
            h1 { color: #e8e8ec; font-size: 20px; font-weight: 600; margin: 0 0 10px; }
            p { font-size: 14px; line-height: 21px; margin: 0; }
            .hint { color: #555566; font-size: 12px; margin-top: 22px; }
            .divider { height: 1px; background: #1e1e28; margin: 22px 0; }
          </style>
        </head>
        <body>
          <div class="card">
            <div class="brand"><span class="eve">EVE </span><span class="cortex">CORTEX</span></div>
            <div class="check">
              <svg viewBox="0 0 24 24"><polyline points="20 6 9 17 4 12"></polyline></svg>
            </div>
            <h1>Login Successful</h1>
            <p>Your character has been authenticated with EVE Online.</p>
            <div class="divider"></div>
            <p class="hint">You can close this tab and return to Eve Cortex.</p>
          </div>
        </body>
        </html>
        """;

    private async Task<TokenSet> ExchangeCodeAsync(string code, string verifier, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["client_id"]     = ClientId,
            ["redirect_uri"]  = CallbackUrl,
            ["code_verifier"] = verifier,
        });

        var response = await _http.PostAsync(TokenEndpoint, body, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
        return TokenSet.FromResponse(json!);
    }

    // -----------------------------------------------------------------------
    // PKCE helpers
    // -----------------------------------------------------------------------

    private static (string verifier, string challenge) GeneratePkce()
    {
        var bytes     = RandomNumberGenerator.GetBytes(32);
        var verifier  = Base64UrlEncode(bytes);
        var hash      = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var challenge = Base64UrlEncode(hash);
        return (verifier, challenge);
    }

    private static string GenerateState()
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

// -----------------------------------------------------------------------
// Data transfer types
// -----------------------------------------------------------------------

public record TokenSet(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt)
{
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddSeconds(-30);

    public static TokenSet FromResponse(TokenResponse r) => new(
        r.AccessToken,
        r.RefreshToken,
        DateTimeOffset.UtcNow.AddSeconds(r.ExpiresIn));
}

public class TokenResponse
{
    [JsonPropertyName("access_token")]  public string AccessToken  { get; init; } = "";
    [JsonPropertyName("refresh_token")] public string RefreshToken { get; init; } = "";
    [JsonPropertyName("expires_in")]    public int    ExpiresIn    { get; init; }
    [JsonPropertyName("token_type")]    public string TokenType    { get; init; } = "";
}
