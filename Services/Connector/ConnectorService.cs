using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Connector;

/// <summary>Who this database is connected to a connector as, and what that connector offers.</summary>
public sealed record ConnectorAccount(
    Uri                   Url,
    string                ConnectorName,
    long                  CharacterId,
    string                CharacterName,
    IReadOnlyList<string> Capabilities);

public enum ConnectorRefreshOutcome
{
    /// <summary>No connector is connected.</summary>
    NotConnected,
    /// <summary>New linked characters were stored.</summary>
    Imported,
    /// <summary>The connector answered with nothing newer than what is stored.</summary>
    Unchanged,
    /// <summary>The connector answered, and offers nothing this build stores.</summary>
    NothingOffered,
    /// <summary>The connector no longer knows the key: forgotten here, and Connect is needed again.</summary>
    SignedOut,
    /// <summary>The connector knows the key and refuses its character now, most likely having left.</summary>
    NotAllowed,
    /// <summary>The connector, or what it reads from, could not be reached: tried again next time.</summary>
    Unreachable,
    /// <summary>The connector answered with linked characters this build will not store.</summary>
    Rejected,
}

/// <summary>A refresh's result, with the reason in words fit to show.</summary>
public sealed record ConnectorRefreshResult(ConnectorRefreshOutcome Outcome, string Message = "");

/// <summary>A connect's result, in words fit to show.</summary>
public sealed record ConnectorConnectResult(bool Ok, string Message);

/// <summary>
/// A connector: a separate program that answers EVE Console's questions about services a corporation or
/// alliance runs — first, which characters are one player's. The user runs it, usually on the same PC,
/// or their corporation hosts one. The interface is EVE Console's own and names no service: see
/// <c>docs/connector/</c>. Nothing here does anything until somebody connects.
///
/// <para><b>Connecting.</b> The connector runs EVE's sign-in, with its own EVE application and callback;
/// EVE Console only opens the page and shows the code the person confirms there (see
/// <see cref="ConnectAsync"/>). The connector decides whether that character may connect and hands EVE
/// Console a key; every later call sends the key. The connector keeps checking that the character still
/// may, and answers 403 once it may not.</para>
///
/// <para>⚠️ The connection belongs to the database, not to the machine, as the online services' sign-in
/// does: the refresh is background work, run by whichever client holds the worker lease. So it is read
/// from the database each time, and a connector on <c>localhost</c> is only reachable from the PC that
/// runs it — which, on a shared database, wants to be where the worker runs.</para>
///
/// <para>⚠️ What a connector gave is kept in <see cref="LinkedCharacterStore"/> while it is down, and
/// forgotten when it is disconnected. A key it no longer knows is forgotten here, but what it gave is
/// kept: the person reconnects, and nothing they rely on vanished in the meantime.</para>
/// </summary>
public sealed class ConnectorService
{
    public const string ClientName = "connector";

    /// <summary>The interface this build speaks, as <c>/v1/info</c> names it.</summary>
    public const string InterfaceName    = "eveconsole.connector";
    public const int    InterfaceVersion = 1;

    /// <summary>What a connector offers, by the names <c>/v1/me</c> lists.</summary>
    public const string LinkedCharactersCapability = "linked-characters";

    private const string UrlPref          = "connector.url";
    private const string KeyPref          = "connector.key";
    private const string NamePref         = "connector.name";
    private const string CharacterPref    = "connector.character_id";
    private const string CharacterNamePref= "connector.character_name";
    private const string CapabilitiesPref = "connector.capabilities";

    private static readonly TimeSpan Interval   = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory              _httpFactory;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LinkedCharacterStore            _store;
    private readonly Action<Uri>                     _openBrowser;
    private readonly AppErrorLogger?                 _errors;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;
    private string?                  _lastProblem;

    // One refresh, connect or disconnect at a time in this process. See RefreshAsync.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectorService(
        IHttpClientFactory              httpFactory,
        IDbContextFactory<AppDbContext> dbFactory,
        LinkedCharacterStore            store,
        AppErrorLogger?                 errors      = null,
        Action<Uri>?                    openBrowser = null)
    {
        _httpFactory = httpFactory;
        _dbFactory   = dbFactory;
        _store       = store;
        _errors      = errors;
        _openBrowser = openBrowser ?? OpenInBrowser;
    }

    private static void OpenInBrowser(Uri page) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page.ToString()) { UseShellExecute = true });

    /// <summary>Raised after connecting or disconnecting here, so what is on screen can follow.</summary>
    public event Action? AccountChanged;


    // ── The address ──────────────────────────────────────────────────────────

    /// <summary>
    /// The connector's address as typed, made into the base its calls go under, or the reason it can't be.
    ///
    /// <para>⚠️ https unless the connector is on this PC. The sign-in's poll secret, and then the key, go with
    /// every call, and on a network they would be readable by anyone on the way. A connector in a container on
    /// this PC is reached through localhost, so it counts as this PC.</para>
    /// </summary>
    public static (Uri? Url, string? Error) ParseUrl(string? typed)
    {
        var text = typed?.Trim() ?? "";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0)
            return (null, SettingsText.ConnectorErrBadUrl);

        if (uri.Scheme == Uri.UriSchemeHttp && !IsThisPc(uri))
            return (null, SettingsText.ConnectorErrNeedsHttps);

        // A base the calls go under: "https://host/path" and "https://host/path/" are the same connector.
        var b = new UriBuilder(uri) { Query = "", Fragment = "" };
        if (!b.Path.EndsWith('/')) b.Path += "/";
        return (b.Uri, null);
    }

    private static bool IsThisPc(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    // ── Who is connected ─────────────────────────────────────────────────────

    private async Task<Dictionary<string, string>> ReadPrefsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.AppPreferences.AsNoTracking()
            .Where(p => p.Key.StartsWith("connector."))
            .ToDictionaryAsync(p => p.Key, p => p.Value, ct);
    }

    /// <summary>
    /// Writes connector preferences together, in one transaction. With <paramref name="ifKey"/>, only while
    /// that is still the stored key: what a refresh learned with one key must not land on a connection made,
    /// or ended, since. With <paramref name="clearLinks"/>, every stored link goes in the same transaction.
    ///
    /// <para>⚠️ Under the store's lock, so the check and the write are one step for every client of a shared
    /// PostgreSQL database, and an import can never land between a clear here and the key written with it:
    /// another client's refresh then either finished before, and is cleared, or comes after, and finds a key
    /// that is not the one it was read with.</para>
    /// </summary>
    private async Task WritePrefsAsync(IReadOnlyDictionary<string, string?> values, CancellationToken ct,
                                       string? ifKey = null, bool clearLinks = false)
    {
        await SqliteBusy.RetryAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LinkedCharacterStore.LockAsync(db, ct);

            var keys = values.Keys.Append(KeyPref).Distinct().ToList();
            var rows = await db.AppPreferences.Where(p => keys.Contains(p.Key)).ToDictionaryAsync(p => p.Key, ct);
            if (ifKey is not null && rows.GetValueOrDefault(KeyPref)?.Value != ifKey) return;

            if (clearLinks) await LinkedCharacterStore.ClearAsync(db, ct);
            foreach (var (key, value) in values)
            {
                var row = rows.GetValueOrDefault(key);
                if (value is null) { if (row is not null) db.AppPreferences.Remove(row); }
                else if (row is null) db.AppPreferences.Add(new AppPreference { Key = key, Value = value });
                else row.Value = value;
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }, null, ct);
        if (clearLinks) _store.RaiseChanged();
    }

    /// <summary>The address last connected to or typed, for the settings page to start from.</summary>
    public async Task<string?> UrlAsync(CancellationToken ct = default)
        => (await ReadPrefsAsync(ct)).GetValueOrDefault(UrlPref);

    /// <summary>The connection, or null when this database is not connected.</summary>
    public async Task<ConnectorAccount?> AccountAsync(CancellationToken ct = default)
        => Account(await ReadPrefsAsync(ct)).Account;

    private static (ConnectorAccount? Account, string? Key) Account(Dictionary<string, string> p)
    {
        if (p.GetValueOrDefault(KeyPref) is not { Length: > 0 } key) return (null, null);
        if (ParseUrl(p.GetValueOrDefault(UrlPref)).Url is not { } url) return (null, null);

        long.TryParse(p.GetValueOrDefault(CharacterPref), out var id);
        var caps = (p.GetValueOrDefault(CapabilitiesPref) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (new ConnectorAccount(url, p.GetValueOrDefault(NamePref) ?? url.Host, id,
                                     p.GetValueOrDefault(CharacterNamePref) ?? "", caps), key);
    }

    // ── Connecting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the connector at <paramref name="typedUrl"/>: checks it is one, asks it to start a sign-in,
    /// shows the person the sign-in's code (<paramref name="showCode"/>), opens the connector's page in the
    /// browser, and waits for the connector to hand over a key. Then refreshes. Throws only when cancelled.
    ///
    /// <para>⚠️ EVE Console never takes part in the EVE sign-in itself. The connector runs it, with its own EVE
    /// application and its own callback, so who signed in is something it learned from EVE directly, and a
    /// token issued to it is accepted nowhere else. EVE Console is a client it does not have to trust: all it
    /// ever holds is the poll secret for one sign-in, and then the key that sign-in produced.</para>
    ///
    /// <para>⚠️ The code is what stops somebody else's sign-in being finished by this person. Anyone can start a
    /// sign-in and send the link to someone; the page shows that sign-in's code, and the person is asked to
    /// confirm it only if it is the one EVE Console is showing them.</para>
    /// </summary>
    public async Task<ConnectorConnectResult> ConnectAsync(string typedUrl, Action<string>? showCode, CancellationToken ct)
    {
        var (url, urlError) = ParseUrl(typedUrl);
        if (url is null) return new(false, urlError!);

        try
        {
            var info = await InfoAsync(url, ct);
            if (info.Error is not null) return new(false, info.Error);

            var (login, startError) = await StartAsync(url, ct);
            if (login is null) return new(false, startError!);

            showCode?.Invoke(login.Code);
            _openBrowser(login.Page);

            var (signedIn, pollError) = await PollAsync(url, login, ct);
            if (signedIn is null) return new(false, pollError!);

            var replaced = await StoreKeyAsync(url, info.Name!, signedIn.Key, signedIn.Character, ct);

            // Connected either way from here; a refresh that went wrong, or a connector with nothing to give,
            // says so beside it.
            await EndReplacedAsync(replaced, ct);
            var refreshed = await RefreshAsync(ct);
            return new(true, refreshed.Outcome is ConnectorRefreshOutcome.Imported or ConnectorRefreshOutcome.Unchanged
                                 ? SettingsText.ConnectorConnected : refreshed.Message);
        }
        // ⚠️ Only the caller's own cancelling is a cancel. A timeout is an OperationCanceledException too,
        // and it is a failure to report, not the person changing their mind.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _errors?.Log(nameof(ConnectorService), "connect", ex);
            return new(false, string.Format(SettingsText.OnlineSignInFailedWith, ex.Message));
        }
    }

    /// <summary>A sign-in the connector has started, as far as EVE Console needs it.</summary>
    private sealed record Login(string Id, string Secret, Uri Page, string Code, TimeSpan Every, DateTimeOffset Until);

    /// <summary>A finished sign-in: the key, and whom the connector says it is for.</summary>
    private sealed record SignedIn(string Key, CharacterAnswer Character);

    private static readonly TimeSpan LongestSignIn = TimeSpan.FromMinutes(15);

    /// <summary>Asks the connector to start a sign-in, or says why it would not.</summary>
    private async Task<(Login? Login, string? Error)> StartAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient(ClientName);
            using var content = new StringContent(JsonSerializer.Serialize(new { client = "EVE Console" }), Encoding.UTF8, "application/json");
            using var res = await http.PostAsync(new Uri(url, "v1/auth/start"), content, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, ErrorText(body, res.StatusCode));

            var a = JsonSerializer.Deserialize<StartAnswer>(body, Json);
            if (a is not { LoginId.Length: > 0, PollSecret.Length: > 0, Code.Length: > 0, LoginUrl.Length: > 0 })
                return (null, string.Format(SettingsText.ConnectorErrNotConnector, url.Authority));

            // ⚠️ A page the browser is sent to on the connector's word: http(s) only, and https unless on this
            // PC, the same rule as the connector's own address.
            if (ParseUrl(a.LoginUrl).Url is null || !Uri.TryCreate(a.LoginUrl.Trim(), UriKind.Absolute, out var page))
                return (null, string.Format(SettingsText.ConnectorErrBadLoginPage, a.LoginUrl));

            var every = TimeSpan.FromSeconds(Math.Clamp(a.Interval ?? 2, 0.5, 10));
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(a.ExpiresIn ?? 600, 30, LongestSignIn.TotalSeconds));
            return (new Login(a.LoginId, a.PollSecret, page, a.Code.Trim(), every, until), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or JsonException)
        {
            return (null, string.Format(SettingsText.ConnectorErrUnreachable, url.Authority));
        }
    }

    /// <summary>
    /// Asks, every few seconds, whether the sign-in has finished, until it has, was refused or declined, or
    /// ran out of time. A connector that stops answering for a moment is asked again, not given up on.
    /// </summary>
    private async Task<(SignedIn? SignedIn, string? Error)> PollAsync(Uri url, Login login, CancellationToken ct)
    {
        var unreachable = 0;
        while (DateTimeOffset.UtcNow < login.Until)
        {
            await Task.Delay(login.Every, ct);
            try
            {
                using var http = _httpFactory.CreateClient(ClientName);
                using var content = new StringContent(JsonSerializer.Serialize(new { loginId = login.Id, pollSecret = login.Secret }),
                                                      Encoding.UTF8, "application/json");
                using var res = await http.PostAsync(new Uri(url, "v1/auth/poll"), content, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                unreachable = 0;

                if (res.StatusCode == HttpStatusCode.NotFound) return (null, SettingsText.ConnectorErrLoginExpired);
                if (!res.IsSuccessStatusCode) return (null, ErrorText(body, res.StatusCode));

                var a = JsonSerializer.Deserialize<PollAnswer>(body, Json);
                switch (a?.Status)
                {
                    case "pending":
                        continue;
                    case "done" when a.Key is { Length: > 0 } key && a.Character is { Id: > 0 } character:
                        return (new SignedIn(key, character), null);
                    case "refused":
                        return (null, ErrorText(body, HttpStatusCode.Forbidden));
                    case "declined":
                        return (null, SettingsText.ConnectorErrLoginDeclined);
                    case "expired":
                        return (null, SettingsText.ConnectorErrLoginExpired);
                    default:
                        return (null, string.Format(SettingsText.ConnectorErrNotConnector, url.Authority));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (JsonException) { return (null, string.Format(SettingsText.ConnectorErrNotConnector, url.Authority)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
            {
                if (++unreachable >= 3) return (null, string.Format(SettingsText.ConnectorErrUnreachable, url.Authority));
            }
        }
        return (null, SettingsText.ConnectorErrLoginExpired);
    }

    /// <summary>A key a new connection replaced, and where it came from.</summary>
    private sealed record Replaced(Uri Url, string Key);

    /// <summary>
    /// Ends a replaced key where it came from, so a connector's list of keys holds only ones in use. Best
    /// effort: unused, it expires there anyway.
    /// </summary>
    private async Task EndReplacedAsync(Replaced? replaced, CancellationToken ct)
    {
        if (replaced is null) return;
        try
        {
            using var http = Authorised(replaced.Key);
            using var _ = await http.PostAsync(new Uri(replaced.Url, "v1/auth/sign-out"), null, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _errors?.Warn(nameof(ConnectorService), "connect", "The key replaced was not ended at the connector: " + ex.Message);
        }
    }

    /// <summary>What the connector says it is, or why it isn't one this build can use.</summary>
    private async Task<(string? Name, string? Error)> InfoAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient(ClientName);
            using var res = await http.GetAsync(new Uri(url, "v1/info"), ct);
            if (!res.IsSuccessStatusCode) return (null, string.Format(SettingsText.ConnectorErrNotConnector, url.Authority));

            var info = JsonSerializer.Deserialize<InfoAnswer>(await res.Content.ReadAsStringAsync(ct), Json);
            if (info?.Interface != InterfaceName) return (null, string.Format(SettingsText.ConnectorErrNotConnector, url.Authority));
            if (info.Versions?.Contains(InterfaceVersion) != true) return (null, SettingsText.ConnectorErrNewerVersion);

            return (string.IsNullOrWhiteSpace(info.Name) ? url.Authority : info.Name.Trim(), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (JsonException) { return (null, string.Format(SettingsText.ConnectorErrNotConnector, url.Authority)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            return (null, string.Format(SettingsText.ConnectorErrUnreachable, url.Authority));
        }
    }

    /// <summary>Stores the key a sign-in produced, and says which key it replaced, for ending afterwards.</summary>
    private async Task<Replaced?> StoreKeyAsync(Uri url, string connectorName, string key, CharacterAnswer character, CancellationToken ct)
    {
        var prefs  = await ReadPrefsAsync(ct);
        var before = Account(prefs);
        var replaced = before is ({ } old, { } oldKey) && oldKey != key ? new Replaced(old.Url, oldKey) : null;

        // ⚠️ Another connector's links go with this one's key arriving, in the same transaction: only one
        // connector is connected at a time, and links from one it no longer is would otherwise stay for good.
        // And the address goes with the key, never before it: a key is only sent where it came from.
        var previous = prefs.GetValueOrDefault(UrlPref);
        await WritePrefsAsync(new Dictionary<string, string?>
        {
            [UrlPref]           = url.ToString(),
            [KeyPref]           = key,
            [NamePref]          = connectorName,
            [CharacterPref]     = character.Id.ToString(),
            [CharacterNamePref] = character.Name ?? "",
            [CapabilitiesPref]  = null,     // asked for next, by the refresh
        }, ct, clearLinks: previous is not null && previous != url.ToString());

        _lastProblem = null;
        AccountChanged?.Invoke();
        return replaced;
    }

    /// <summary>
    /// Ends the key on the connector, forgets it here, and forgets what the connector gave. Also what is
    /// left over after the connector stopped knowing the key: there is no key to end then, only links.
    ///
    /// <para>⚠️ Forgotten here whatever the connector says. Someone disconnecting from a connector that is
    /// down, or hung until the request times out, should still end up disconnected; the key is then simply
    /// never used again.</para>
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (account, key) = Account(await ReadPrefsAsync(ct));
            if (account is not null && key is not null)
            {
                try
                {
                    using var http = Authorised(key);
                    using var res = await http.PostAsync(new Uri(account.Url, "v1/auth/sign-out"), null, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // A warning, not an error: disconnected here is what was asked, and it happened.
                    _errors?.Warn(nameof(ConnectorService), "disconnect", "The connector was not told: " + ex.Message);
                }
            }

            await WritePrefsAsync(Forgotten, ct, clearLinks: true);
            AccountChanged?.Invoke();
        }
        finally { _gate.Release(); }
    }

    // The key and who it was for. The address is kept: connecting again is one click.
    private static readonly Dictionary<string, string?> Forgotten = new()
    {
        [KeyPref] = null, [NamePref] = null, [CharacterPref] = null, [CharacterNamePref] = null, [CapabilitiesPref] = null,
    };

    /// <summary>Forgets the key and who it was for; with <paramref name="ifKey"/>, only if that is still it.</summary>
    private async Task ForgetKeyAsync(string? ifKey, CancellationToken ct)
    {
        await WritePrefsAsync(Forgotten, ct, ifKey);
        AccountChanged?.Invoke();
    }

    // ── Refreshing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the connector what it offers, and stores what this build keeps of it. Never throws for the
    /// connector's answer or its absence: the result says what happened.
    ///
    /// <para>⚠️ One at a time in this process, with connecting and disconnecting: a refresh that had its
    /// answer when Disconnect ran would otherwise store it after the links were cleared. Across clients of
    /// a shared database, the store checks inside its own transaction that the key it was read with is
    /// still the one connected.</para>
    /// </summary>
    public async Task<ConnectorRefreshResult> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return await RefreshLockedAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<ConnectorRefreshResult> RefreshLockedAsync(CancellationToken ct)
    {
        var (account, key) = Account(await ReadPrefsAsync(ct));
        if (account is null || key is null) return new(ConnectorRefreshOutcome.NotConnected, SettingsText.ConnectorNotConnected);

        try
        {
            using var http = Authorised(key);

            using var meRes = await http.GetAsync(new Uri(account.Url, "v1/me"), ct);
            if (await Refused(meRes, key, ct) is { } refusedMe) return refusedMe;

            var me = JsonSerializer.Deserialize<MeAnswer>(await meRes.Content.ReadAsStringAsync(ct), Json);
            var caps = me?.Capabilities?.OfType<string>().Select(c => c.Trim()).Where(c => c.Length > 0).ToList() ?? [];
            await WritePrefsAsync(new Dictionary<string, string?>
            {
                [CapabilitiesPref]  = string.Join(",", caps),
                [CharacterNamePref] = me?.Character?.Name is { Length: > 0 } n ? n : account.CharacterName,
            }, ct, ifKey: key);

            if (!caps.Contains(LinkedCharactersCapability))
                return Settled(new(ConnectorRefreshOutcome.NothingOffered, SettingsText.ConnectorNothingOffered));

            using var linkedRes = await http.GetAsync(new Uri(account.Url, "v1/linked-characters"), ct);
            if (linkedRes.StatusCode == HttpStatusCode.NotFound)
                return Settled(new(ConnectorRefreshOutcome.NothingOffered, SettingsText.ConnectorNothingOffered));
            if (await Refused(linkedRes, key, ct) is { } refusedLinked) return refusedLinked;

            var (snapshot, error) = LinkedCharacterStore.Parse(await linkedRes.Content.ReadAsStringAsync(ct));
            if (snapshot is null)
                return Problem(new(ConnectorRefreshOutcome.Rejected, string.Format(SettingsText.ConnectorErrRejected, error)), error);

            // ⚠️ Only while the key this was read with is still the one stored, checked inside the store's
            // transaction: another client may have disconnected, or connected elsewhere, meanwhile. And it
            // replaces every other source, since one connector is connected at a time.
            var stored = await _store.ImportAsync(snapshot, ct, replaceOtherSources: true,
                stillWanted: async (db, c) => (await db.AppPreferences.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Key == KeyPref, c))?.Value == key);
            return stored switch
            {
                LinkedStoreResult.Stored    => Settled(new(ConnectorRefreshOutcome.Imported,  SettingsText.ConnectorRefreshed)),
                LinkedStoreResult.NotNewer  => Settled(new(ConnectorRefreshOutcome.Unchanged, SettingsText.ConnectorUnchanged)),
                // Disconnected, or connected anew, while this was asking: nothing to say about this answer.
                _                           => new(ConnectorRefreshOutcome.NotConnected, SettingsText.ConnectorNotConnected),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or JsonException)
        {
            return Problem(new(ConnectorRefreshOutcome.Unreachable, string.Format(SettingsText.ConnectorErrUnreachable, account.Url.Authority)),
                           ex.Message);
        }
    }

    /// <summary>A refusal by the connector, as a result; null for an answer to read.</summary>
    private async Task<ConnectorRefreshResult?> Refused(HttpResponseMessage res, string key, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return null;
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Only the key that was refused: a newer one, from connecting again meanwhile, stays.
            await ForgetKeyAsync(key, ct);
            return Problem(new(ConnectorRefreshOutcome.SignedOut, SettingsText.ConnectorErrSignedOut), "the connector no longer knows the key");
        }
        if (res.StatusCode == HttpStatusCode.Forbidden)
            return Problem(new(ConnectorRefreshOutcome.NotAllowed, ErrorText(body, res.StatusCode)), "refused: " + body);

        // A redirect counts as a refusal: they are not followed, so a key never goes anywhere it wasn't issued.
        return Problem(new(ConnectorRefreshOutcome.Unreachable, ErrorText(body, res.StatusCode)), $"{(int)res.StatusCode}: {body}");
    }

    /// <summary>A problem, written to the error log once until it changes or clears.</summary>
    private ConnectorRefreshResult Problem(ConnectorRefreshResult result, string? detail)
    {
        var said = $"{result.Outcome}: {detail}";
        if (said != _lastProblem)
            _errors?.Warn(nameof(ConnectorService), "refresh", $"Connector refresh: {result.Outcome}", detail);
        _lastProblem = said;
        return result;
    }

    private ConnectorRefreshResult Settled(ConnectorRefreshResult result)
    {
        _lastProblem = null;
        return result;
    }

    // ── The background refresh ───────────────────────────────────────────────

    /// <summary>
    /// The refresh every half hour. ⚠️ Worker-lease only: it writes the shared cache, and one client doing
    /// so is enough. Does nothing but read two preferences while nothing is connected.
    /// </summary>
    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_runTask is not null) { try { await _runTask; } catch { } }
        _cts.Dispose();
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try { await Task.Delay(FirstDelay, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _errors?.Log(nameof(ConnectorService), "refresh", ex); }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HttpClient Authorised(string key)
    {
        var http = _httpFactory.CreateClient(ClientName);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return http;
    }

    /// <summary>The connector's refusal in the interface language, by its code: its own message is
    /// English, and only shown for a code this build does not know.</summary>
    private static string ErrorText(string body, HttpStatusCode status)
    {
        string? code = null, message = null;
        try
        {
            var error = JsonSerializer.Deserialize<ErrorAnswer>(body, Json);
            code    = error?.Error;
            message = error?.Message;
        }
        catch (JsonException) { /* A page from something in between, not the connector. */ }

        return code switch
        {
            "not_allowed"        => SettingsText.ConnectorErrNotAllowed,
            "eve_token_invalid"  => SettingsText.ConnectorErrEveInvalid,
            "too_many_logins"    => SettingsText.ConnectorErrTooManyLogins,
            "eve_unavailable"    => SettingsText.OnlineErrEveUnavailable,
            "source_unavailable" => SettingsText.ConnectorErrSourceUnavailable,
            "not_offered"        => SettingsText.ConnectorNothingOffered,
            "bad_request"        => SettingsText.ConnectorErrBadRequest,
            "not_signed_in"      => SettingsText.ConnectorErrSignedOut,
            _ => message is { Length: > 0 } ? message : string.Format(SettingsText.ConnectorErrStatus, (int)status),
        };
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record InfoAnswer(
        [property: JsonPropertyName("interface")] string? Interface,
        [property: JsonPropertyName("versions")]  int[]?  Versions,
        [property: JsonPropertyName("name")]      string? Name);
    private sealed record StartAnswer(string? LoginId, string? PollSecret, string? LoginUrl, string? Code, double? Interval, double? ExpiresIn);
    private sealed record PollAnswer(string? Status, string? Key, CharacterAnswer? Character);
    private sealed record CharacterAnswer(long Id, string? Name);
    private sealed record MeAnswer(CharacterAnswer? Character, List<string?>? Capabilities);
    private sealed record ErrorAnswer(string? Error, string? Message);
}
