using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EveConsole.Localization;

namespace EveConsole.Auth;

/// <summary>What became of a sign-in, in the words the finished page shows.</summary>
public sealed record OnlineSignInOutcome(bool Ok, string Message);

/// <summary>
/// The EVE sign-in behind EVE Console's online services: PKCE, like the ESI one, against an EVE
/// application of its own that has no scopes.
///
/// <para>⚠️ Not the ESI application. Its tokens can read wallets and mail, and they must never reach
/// a web service. This one's cannot read anything, and the service refuses a token with scopes.</para>
///
/// <para>⚠️ The refresh token EVE sends back is ignored. The token is used once and thrown away:
/// the service answers it with a key, which is what the app keeps.</para>
/// </summary>
public class OnlineSignInService(IHttpClientFactory httpFactory)
{
    // Public by design, like the ESI one. The application's secret is not used by PKCE, and ships nowhere.
    private const string ClientId      = "cb852ea95cbd4773b52215c3f85c3785";
    private const string CallbackUrl   = "http://localhost:5052/callback";
    private const string ListenPrefix  = "http://localhost:5052/callback/";
    private const string AuthEndpoint  = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenEndpoint = "https://login.eveonline.com/v2/oauth/token";

    /// <summary>
    /// Runs the browser sign-in and hands the access token to <paramref name="useToken"/>.
    ///
    /// <para>⚠️ The browser waits for that answer before it gets its page, so the page says what
    /// really happened and names the character, rather than promising a sign-in the service may
    /// then refuse.</para>
    /// </summary>
    public async Task<OnlineSignInOutcome> SignInAsync(
        Func<string, CancellationToken, Task<OnlineSignInOutcome>> useToken, CancellationToken ct)
    {
        var verifier  = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state     = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new HttpListener();
        listener.Prefixes.Add(ListenPrefix);
        listener.Start();

        // Listening before the browser opens, so a fast redirect cannot be missed.
        var contextTask = listener.GetContextAsync();

        // ⚠️ Observed from the start, as in the ESI sign-in: an abandoned wait faults when the
        // listener is disposed, and nothing awaits it any more.
        _ = contextTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

        Process.Start(new ProcessStartInfo(AuthUrl(challenge, state)) { UseShellExecute = true });

        var cancelled = Task.Delay(Timeout.Infinite, ct);
        if (await Task.WhenAny(contextTask, cancelled) == cancelled)
            throw new OperationCanceledException(ct);

        var context = await contextTask;
        var outcome = await FinishAsync(context.Request.QueryString, state, verifier, useToken, ct);
        await RespondAsync(context, outcome, ct);
        listener.Stop();
        return outcome;
    }

    private static string AuthUrl(string challenge, string state)
        // ⚠️ No scope at all, rather than an empty one: tested against EVE on 2026-11-04.
        => $"{AuthEndpoint}" +
           "?response_type=code" +
           $"&client_id={ClientId}" +
           $"&redirect_uri={Uri.EscapeDataString(CallbackUrl)}" +
           $"&state={state}" +
           $"&code_challenge={challenge}" +
           "&code_challenge_method=S256";

    private async Task<OnlineSignInOutcome> FinishAsync(
        System.Collections.Specialized.NameValueCollection query, string state, string verifier,
        Func<string, CancellationToken, Task<OnlineSignInOutcome>> useToken, CancellationToken ct)
    {
        // Declining on EVE's page comes back as an error, and with no code.
        if (query["state"] != state || query["code"] is not { Length: > 0 } code)
            return new OnlineSignInOutcome(false, SettingsText.OnlineSignInNotFinished);

        using var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Add("User-Agent", "EveConsole/1.0 (EVE Online companion app)");

        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "authorization_code",
            ["code"]          = code,
            ["client_id"]     = ClientId,
            ["code_verifier"] = verifier,
        });

        using var response = await http.PostAsync(TokenEndpoint, body, ct);
        if (!response.IsSuccessStatusCode)
            return new OnlineSignInOutcome(false, string.Format(SettingsText.OnlineSignInEveRefused, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } accessToken)
            return new OnlineSignInOutcome(false, SettingsText.OnlineSignInNotFinished);

        return await useToken(accessToken, ct);
    }

    private static async Task RespondAsync(HttpListenerContext context, OnlineSignInOutcome outcome, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(FinishedPage(outcome));
        context.Response.ContentType     = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct);
        context.Response.Close();
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The page the browser ends on, which matches the one <see cref="EsiAuthService"/> shows after
    /// registering a character: same card, same brand, the tick or a cross in the circle.
    /// </summary>
    public static string FinishedPage(OnlineSignInOutcome outcome)
    {
        var title  = outcome.Ok ? SettingsText.OnlinePageSignedInTitle : SettingsText.OnlinePageFailedTitle;
        var hint   = outcome.Ok ? SettingsText.OnlinePageSignedInHint  : SettingsText.OnlinePageFailedHint;
        var colour = outcome.Ok ? "#70ad47" : "#c0504d";
        var tint   = outcome.Ok ? "rgba(112, 173, 71, 0.12)" : "rgba(192, 80, 77, 0.12)";
        var mark   = outcome.Ok
            ? """<polyline points="20 6 9 17 4 12"></polyline>"""
            : """<line x1="6" y1="6" x2="18" y2="18"></line><line x1="18" y1="6" x2="6" y2="18"></line>""";

        return $$"""
            <!DOCTYPE html>
            <html lang="{{WebUtility.HtmlEncode(Languages.Active.Code)}}">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>EVE Console — {{WebUtility.HtmlEncode(title)}}</title>
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
                .brand .console { color: #c8a84b; }
                .check {
                  width: 66px; height: 66px; margin: 6px auto 20px;
                  border-radius: 50%;
                  background: {{tint}};
                  border: 2px solid {{colour}};
                  display: flex; align-items: center; justify-content: center;
                }
                .check svg { width: 34px; height: 34px; stroke: {{colour}}; stroke-width: 3;
                  fill: none; stroke-linecap: round; stroke-linejoin: round; }
                h1 { color: #e8e8ec; font-size: 20px; font-weight: 600; margin: 0 0 10px; }
                p { font-size: 14px; line-height: 21px; margin: 0; }
                .hint { color: #555566; font-size: 12px; margin-top: 22px; }
                .divider { height: 1px; background: #1e1e28; margin: 22px 0; }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="brand"><span class="eve">EVE </span><span class="console">CONSOLE</span></div>
                <div class="check">
                  <svg viewBox="0 0 24 24">{{mark}}</svg>
                </div>
                <h1>{{WebUtility.HtmlEncode(title)}}</h1>
                <p>{{WebUtility.HtmlEncode(outcome.Message)}}</p>
                <div class="divider"></div>
                <p class="hint">{{WebUtility.HtmlEncode(hint)}}</p>
              </div>
            </body>
            </html>
            """;
    }
}
