using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Auth;
using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>The character this database is signed in to the online services as.</summary>
public sealed record OnlineAccount(long CharacterId, string CharacterName);

/// <summary>What the service holds for the character, against its limits.</summary>
public sealed record OnlineUsage(long StoredBytes, long MaxStoredBytes, int UploadsToday, int MaxUploadsPerDay);

/// <summary>An upload's answer: a URL, or why not, in words fit to show.</summary>
public sealed record OnlineUpload(string? Id, string? Url, string? Error)
{
    public bool Ok => Url is not null;
}

/// <summary>
/// EVE Console's online services on eveconsole.com: signing in, and hosting images so a Slack
/// webhook can show them. Optional, and nothing here is available until signed in.
///
/// <para>⚠️ The sign-in belongs to the database, not to the machine. Everyone attached to one
/// database is the same person on different devices, and a scheduled task may run on whichever of
/// them is up. So the key lives in AppPreferences, and is read from the database each time rather
/// than from the preferences' cache: that is filled once at startup, and a sign-in on another
/// client would not be seen until a restart.</para>
/// </summary>
public class OnlineServices(
    IHttpClientFactory              httpFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    AppPreferencesService           prefs,
    OnlineSignInService             signIn,
    AppErrorLogger                  errors)
{
    private const string KeyPref       = "online.key";
    private const string CharacterPref = "online.character_id";
    private const string NamePref      = "online.character_name";

    public const string ClientName = "online-services";
    public const string TermsUrl   = "https://eveconsole.com/services/terms";

    /// <summary>Raised after signing in or out here, so what is on screen can follow.</summary>
    public event Action? AccountChanged;

    // ── Who is signed in ─────────────────────────────────────────────────────

    private async Task<(OnlineAccount? Account, string? Key)> ReadAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AppPreferences.AsNoTracking()
            .Where(p => p.Key == KeyPref || p.Key == CharacterPref || p.Key == NamePref)
            .ToDictionaryAsync(p => p.Key, p => p.Value, ct);

        if (!rows.TryGetValue(KeyPref, out var key) || key.Length == 0) return (null, null);

        long.TryParse(rows.GetValueOrDefault(CharacterPref), out var id);
        return (new OnlineAccount(id, rows.GetValueOrDefault(NamePref) ?? ""), key);
    }

    /// <summary>The account, or null when this database has not signed in.</summary>
    public async Task<OnlineAccount?> AccountAsync(CancellationToken ct = default) => (await ReadAsync(ct)).Account;

    private async Task ForgetAsync()
    {
        await prefs.SetAsync(KeyPref, null);
        await prefs.SetAsync(CharacterPref, null);
        await prefs.SetAsync(NamePref, null);
        AccountChanged?.Invoke();
    }

    // ── Signing in and out ───────────────────────────────────────────────────

    /// <summary>The browser sign-in, start to finish. Throws only when cancelled.</summary>
    public async Task<OnlineSignInOutcome> SignInAsync(CancellationToken ct)
    {
        try
        {
            return await signIn.SignInAsync(ExchangeAsync, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors.Log(nameof(OnlineServices), "sign-in", ex);
            return new OnlineSignInOutcome(false, string.Format(SettingsText.OnlineSignInFailedWith, ex.Message));
        }
    }

    /// <summary>The EVE token for the service's key. The token goes no further than this call.</summary>
    private async Task<OnlineSignInOutcome> ExchangeAsync(string accessToken, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(ClientName);
        using var content = new StringContent(
            JsonSerializer.Serialize(new { accessToken }), Encoding.UTF8, "application/json");

        using var res = await http.PostAsync("v1/auth/eve", content, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode) return new OnlineSignInOutcome(false, ErrorText(body, res.StatusCode));

        var answer = JsonSerializer.Deserialize<SignInAnswer>(body, Json);
        if (answer?.Key is not { Length: > 0 } key || answer.Character is null)
            return new OnlineSignInOutcome(false, SettingsText.OnlineErrUnreachable);

        await prefs.SetAsync(KeyPref, key);
        await prefs.SetAsync(CharacterPref, answer.Character.Id.ToString());
        await prefs.SetAsync(NamePref, answer.Character.Name);
        AccountChanged?.Invoke();

        return new OnlineSignInOutcome(true, string.Format(SettingsText.OnlinePageSignedInBody, answer.Character.Name));
    }

    /// <summary>
    /// Ends the key on the service and forgets it here.
    ///
    /// <para>⚠️ Forgotten here whatever the service says. Someone signing out with the network
    /// down should still end up signed out; the key is then simply never used again.</para>
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var (_, key) = await ReadAsync(ct);
        if (key is not null)
        {
            try
            {
                using var http = Authorised(key);
                using var res = await http.PostAsync("v1/auth/sign-out", null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Log(nameof(OnlineServices), "sign-out", ex);
            }
        }
        await ForgetAsync();
    }

    // ── Usage ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The account's usage and limits, or null when they could not be read.
    ///
    /// <para>A key the service no longer knows is forgotten here: signed out elsewhere, or banned.
    /// The reader sees "not signed in", which is the truth, rather than a post failing later.</para>
    /// </summary>
    public async Task<OnlineUsage?> UsageAsync(CancellationToken ct = default)
    {
        var (_, key) = await ReadAsync(ct);
        if (key is null) return null;

        try
        {
            using var http = Authorised(key);
            using var res = await http.GetAsync("v1/me", ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized) { await ForgetAsync(); return null; }
            if (!res.IsSuccessStatusCode) return null;

            var me = JsonSerializer.Deserialize<MeAnswer>(await res.Content.ReadAsStringAsync(ct), Json);
            return me?.Usage is null || me.Limits is null ? null : new OnlineUsage(
                me.Usage.StoredBytes, me.Limits.MaxStoredBytes, me.Usage.UploadsToday, me.Limits.MaxUploadsPerDay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Log(nameof(OnlineServices), "usage", ex);
            return null;
        }
    }

    // ── Images ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads an image and answers with its public URL.
    ///
    /// <para>Kept for the service's longest time, 12 months. Never throws for a refusal or an
    /// outage: a scheduled post reports it in its result, beside whatever did get through.</para>
    /// </summary>
    public async Task<OnlineUpload> UploadImageAsync(byte[] png, CancellationToken ct = default)
    {
        var (_, key) = await ReadAsync(ct);
        if (key is null) return new OnlineUpload(null, null, SettingsText.OnlineErrNotSignedIn);

        try
        {
            using var http = Authorised(key);
            using var content = new ByteArrayContent(png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

            using var res = await http.PostAsync("v1/images", content, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                var error = ErrorText(body, res.StatusCode);
                errors.Log(nameof(OnlineServices), "image upload", error);
                return new OnlineUpload(null, null, error);
            }

            var image = JsonSerializer.Deserialize<ImageAnswer>(body, Json);
            return image?.Url is { Length: > 0 } url
                ? new OnlineUpload(image.Id, url, null)
                : new OnlineUpload(null, null, SettingsText.OnlineErrUnreachable);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Log(nameof(OnlineServices), "image upload", ex);
            return new OnlineUpload(null, null, SettingsText.OnlineErrUnreachable);
        }
    }

    /// <summary>Removes an image that was uploaded and then could not be posted, so a failed post
    /// does not hold storage for a year. Best effort: it expires anyway.</summary>
    public async Task DeleteImageAsync(string id, CancellationToken ct = default)
    {
        var (_, key) = await ReadAsync(ct);
        if (key is null) return;

        try
        {
            using var http = Authorised(key);
            using var content = new StringContent(
                JsonSerializer.Serialize(new { ids = new[] { id } }), Encoding.UTF8, "application/json");
            using var res = await http.PostAsync("v1/images/delete", content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Log(nameof(OnlineServices), "image delete", ex);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HttpClient Authorised(string key)
    {
        var http = httpFactory.CreateClient(ClientName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    /// <summary>The service's refusal in the interface language, by its code: the site's own message
    /// is English, and only shown for a code this build does not know.</summary>
    private static string ErrorText(string body, HttpStatusCode status)
    {
        string? code = null, message = null;
        try
        {
            var error = JsonSerializer.Deserialize<ErrorAnswer>(body, Json);
            code    = error?.Error;
            message = error?.Message;
        }
        catch (JsonException) { /* A page from something in between, not the service. */ }

        return code switch
        {
            "not_signed_in"   => SettingsText.OnlineErrNotSignedIn,
            "banned"          => SettingsText.OnlineErrBanned,
            "storage_limit"   => SettingsText.OnlineErrStorageFull,
            "daily_limit"     => SettingsText.OnlineErrDailyLimit,
            "image_too_large" => SettingsText.OnlineErrTooLarge,
            "eve_unavailable" => SettingsText.OnlineErrEveUnavailable,
            _ => message is { Length: > 0 } ? message : string.Format(SettingsText.OnlineErrStatus, (int)status),
        };
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record SignInAnswer(string? Key, CharacterAnswer? Character);
    private sealed record CharacterAnswer(long Id, string Name);
    private sealed record MeAnswer(UsageAnswer? Usage, LimitsAnswer? Limits);
    private sealed record UsageAnswer(long StoredBytes, int UploadsToday);
    private sealed record LimitsAnswer(long MaxStoredBytes, int MaxUploadsPerDay);
    private sealed record ImageAnswer(string? Id, string? Url);
    private sealed record ErrorAnswer(string? Error, string? Message);
}
