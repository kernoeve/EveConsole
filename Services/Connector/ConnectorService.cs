using System.Collections.Concurrent;
using System.Globalization;
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

public enum ConnectorRefreshOutcome
{
    /// <summary>The connector is not connected.</summary>
    NotConnected,
    /// <summary>New linked characters were stored.</summary>
    Imported,
    /// <summary>The connector answered with nothing newer than what is stored.</summary>
    Unchanged,
    /// <summary>The connector answered, and offers nothing this build stores.</summary>
    NothingOffered,
    /// <summary>Connected, but no corporation or alliance is chosen for it, or linked characters are not
    /// ticked: only <c>/v1/me</c> was asked.</summary>
    NotUsed,
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

/// <summary>Whether the user's choices for a connector were saved, and if not, why, in words fit to show.</summary>
public sealed record ConnectorChoicesResult(bool Ok, string Message);

/// <summary>A corporation or alliance a connector can be chosen for. <see cref="OfOwnCharacter"/> is false for
/// one that is only still chosen on some connector, with none of the user's characters in it now.</summary>
public sealed record ConnectorScopeChoice(bool IsAlliance, int Id, string Name, bool OfOwnCharacter);

/// <summary>
/// Connectors: separate programs that answer EVE Console's questions about services a corporation or
/// alliance runs — first, which characters are one player's. The user runs one, usually on the same PC,
/// or their corporation hosts one. The interface is EVE Console's own and names no service: see
/// <c>docs/connector/</c>. Nothing here does anything until somebody connects.
///
/// <para><b>Several connectors.</b> The user may set up more than one (<see cref="ConnectorEntry"/>), for
/// example when two of their corporations, or a corporation and an alliance, each run their own. Each is
/// used only for the corporations and alliances the user chose for it, and only for the functions they
/// ticked (<see cref="ConnectorFunctions"/>); <see cref="ConnectorRouter"/> says which one to use for a
/// character. ⚠️ A connector is never asked about characters or corporations outside its scopes, and one
/// with no scope is asked nothing but <c>/v1/info</c>, <c>/v1/me</c> and the sign-in. Each connector's
/// answers are kept apart (<see cref="LinkedCharacterStore.SourceKey"/>).</para>
///
/// <para><b>Connecting.</b> Each connector runs EVE's sign-in, with its own EVE application and callback;
/// EVE Console only opens the page and shows the code the person confirms there (see
/// <see cref="ConnectAsync"/>). The connector decides whether that character may connect and hands EVE
/// Console a key; every later call sends the key. The connector keeps checking that the character still
/// may, and answers 403 once it may not.</para>
///
/// <para>⚠️ The connections belong to the database, not to the machine, as the online services' sign-in
/// does: the refresh is background work, run by whichever client holds the worker lease. So they are read
/// from the database each time, and a connector on <c>localhost</c> is only reachable from the PC that
/// runs it — which, on a shared database, wants to be where the worker runs.</para>
///
/// <para>⚠️ What a connector gave is kept in <see cref="LinkedCharacterStore"/> while it is down, and
/// forgotten when it is disconnected or removed, or no longer used for linked characters. A key it no
/// longer knows is forgotten here, but what it gave is kept: the person reconnects, and nothing they rely
/// on vanished in the meantime.</para>
/// </summary>
public sealed class ConnectorService
{
    public const string ClientName = "connector";

    /// <summary>The interface this build speaks, as <c>/v1/info</c> names it.</summary>
    public const string InterfaceName    = "eveconsole.connector";
    public const int    InterfaceVersion = 1;

    /// <summary>What a connector offers, by the names <c>/v1/me</c> lists.</summary>
    public const string LinkedCharactersCapability = ConnectorFunctions.LinkedCharacters;

    /// <summary>The longest "offers" kept from <c>/v1/me</c>, in characters of JSON.</summary>
    private const int MaxOffersLength = 64 * 1024;

    private static readonly TimeSpan Interval   = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);

    private readonly IHttpClientFactory              _httpFactory;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LinkedCharacterStore            _store;
    private readonly Action<Uri>                     _openBrowser;
    private readonly AppErrorLogger?                 _errors;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;

    // Per connector entry, by its id.
    // One refresh, disconnect or remove at a time in this process for each entry. See RefreshAsync.
    private readonly ConcurrentDictionary<int, SemaphoreSlim>          _gates         = new();
    // Logged once per connector until it changes or clears.
    private readonly ConcurrentDictionary<int, string>                 _lastProblem   = new();
    private readonly ConcurrentDictionary<int, bool>                   _offersTooLong = new();
    // The last refresh's result, for the list's status. In memory: a PC without the worker lease sees only its own.
    private readonly ConcurrentDictionary<int, ConnectorRefreshResult> _lastResult    = new();

    private SemaphoreSlim Gate(int id) => _gates.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

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

    /// <summary>Raised after a connector entry was added, removed, connected or disconnected here, or had its
    /// choices or capabilities saved, so what is on screen can follow.</summary>
    public event Action? Changed;

    /// <summary>The last refresh's result for an entry in this process, or null.</summary>
    public ConnectorRefreshResult? LastResult(int id) => _lastResult.GetValueOrDefault(id);


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

    // ── The entries ──────────────────────────────────────────────────────────

    /// <summary>Every connector entry, without keys, by id.</summary>
    public async Task<IReadOnlyList<ConnectorSnapshot>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Connectors.AsNoTracking().OrderBy(e => e.Id).ToListAsync(ct);
        return rows.Select(ConnectorSnapshot.From).ToList();
    }

    /// <summary>One connector entry, without its key, or null when there is none by that id.</summary>
    public async Task<ConnectorSnapshot?> GetAsync(int id, CancellationToken ct = default)
        => await ReadEntryAsync(id, ct) is { } row ? ConnectorSnapshot.From(row) : null;

    private async Task<ConnectorEntry?> ReadEntryAsync(int id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Connectors.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
    }

    /// <summary>
    /// The connector the user chose for <paramref name="function"/> for a character in this corporation and
    /// alliance, or null. ⚠️ The one entry point later features call: see <see cref="ConnectorRouter.ForCharacter"/>.
    /// To read linked characters for a character, read the rows whose Source is
    /// <see cref="LinkedCharacterStore.SourceKey"/> of the connector this returns.
    /// </summary>
    public async Task<ConnectorSnapshot?> ForCharacterAsync(string function, int corporationId, int? allianceId, CancellationToken ct = default)
        => ConnectorRouter.ForCharacter(await ListAsync(ct), function, corporationId, allianceId);

    /// <summary>Adds a blank entry, not connected and chosen for nothing, and returns its id.</summary>
    public async Task<int> AddAsync(CancellationToken ct = default)
    {
        var id = await SqliteBusy.RetryAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = new ConnectorEntry();
            db.Connectors.Add(row);
            await db.SaveChangesAsync(ct);
            return row.Id;
        }, null, ct);
        Changed?.Invoke();
        return id;
    }

    /// <summary>
    /// One entry changed in one transaction. With <paramref name="ifKey"/>, only while that is still this
    /// entry's key: what a refresh learned with one key must not land on a connection made, or ended, since.
    /// <paramref name="change"/> returns true when it cleared links. Returns the row after the change, or null
    /// when the row is gone or <paramref name="ifKey"/> no longer matches.
    ///
    /// <para>⚠️ Under the store's lock, so the check and the write are one step for every client of a shared
    /// PostgreSQL database, and an import can never land between a clear here and the key written with it:
    /// another client's refresh then either finished before, and is cleared, or comes after, and finds a key
    /// that is not the one it was read with.</para>
    /// </summary>
    private async Task<ConnectorEntry?> WriteEntryAsync(int id, Func<AppDbContext, ConnectorEntry, Task<bool>> change,
                                                        CancellationToken ct, string? ifKey = null)
    {
        var cleared = false;
        var after = await SqliteBusy.RetryAsync<ConnectorEntry?>(async () =>
        {
            cleared = false;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LinkedCharacterStore.LockAsync(db, ct);

            var row = await db.Connectors.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (row is null) return null;
            if (ifKey is not null && row.Key != ifKey) return null;

            cleared = await change(db, row);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return row;
        }, null, ct);

        if (cleared) _store.RaiseChanged();
        if (after is not null) Changed?.Invoke();
        return after;
    }

    /// <summary>
    /// Saves which corporations and alliances the entry is for, and which functions it is used for. Refused
    /// when another connector already has one of those functions for one of those corporations, or for one of
    /// those alliances (a corporation on one and its alliance on another is fine: the corporation wins).
    ///
    /// <para>Unknown function names are ignored. A new tick needs the connector to offer the function; one
    /// already saved may stay ticked, or be unticked, after the connector stopped offering it. The check is on
    /// the saved choices, not on what is offered now, so routing never meets two owners.</para>
    ///
    /// <para>⚠️ When the entry is no longer used for linked characters (unticked, or no scope left), what it
    /// gave goes in the same transaction: the user chose to stop using them.</para>
    /// </summary>
    public async Task<ConnectorChoicesResult> SaveChoicesAsync(int id, IReadOnlyCollection<int> corporations, IReadOnlyCollection<int> alliances,
                                                               IReadOnlyCollection<string> functions, CancellationToken ct = default)
    {
        var corps = corporations.Where(i => i > 0).Distinct().ToList();
        var allis = alliances.Where(i => i > 0).Distinct().ToList();
        var cleared = false;

        var result = await SqliteBusy.RetryAsync(async () =>
        {
            cleared = false;
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LinkedCharacterStore.LockAsync(db, ct);

            var row = await db.Connectors.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (row is null) return new ConnectorChoicesResult(false, SettingsText.ConnectorRemovedMeanwhile);

            var before = ConnectorSnapshot.From(row);
            var fns = functions.Where(ConnectorFunctions.IsKnown).Where(f => before.Offers(f) || before.Chose(f)).Distinct().ToList();

            var others = (await db.Connectors.AsNoTracking().Where(e => e.Id != id).ToListAsync(ct))
                .Select(ConnectorSnapshot.From).ToList();
            if (ConnectorRouter.FindConflict(others, corps, allis, fns) is { } conflict)
            {
                var names = await NamesAsync(db, conflict.IsAlliance ? [] : [conflict.EntityId], conflict.IsAlliance ? [conflict.EntityId] : [], ct);
                return new ConnectorChoicesResult(false, string.Format(SettingsText.ConnectorConflict,
                    ConnectorFunctions.Label(conflict.Function), ScopeName(names, conflict.IsAlliance, conflict.EntityId), conflict.OtherTitle));
            }

            row.Corporations = ConnectorLists.Join(corps);
            row.Alliances    = ConnectorLists.Join(allis);
            row.Functions    = ConnectorLists.Join(fns);
            // Chosen only once the user could see something offered to tick: a save before /v1/me answered
            // (just connected, or the connector unreachable) must not stop the defaults when it does.
            if (row.Key.Length > 0 && before.Capabilities.Any(ConnectorFunctions.IsKnown)) row.FunctionsChosen = true;

            var usedNow = (corps.Count > 0 || allis.Count > 0) && fns.Contains(ConnectorFunctions.LinkedCharacters);
            if (!usedNow) cleared = await LinkedCharacterStore.ClearSourceAsync(db, LinkedCharacterStore.SourceKey(id), ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new ConnectorChoicesResult(true, "");
        }, null, ct);

        if (cleared) _store.RaiseChanged();
        if (result.Ok) Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// The corporations and alliances of the user's own characters, which a connector can be chosen for, with
    /// every one still chosen on some entry. Names from what is stored; asks ESI nothing.
    /// </summary>
    public async Task<IReadOnlyList<ConnectorScopeChoice>> ScopeChoicesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var chars = await db.Characters.AsNoTracking().Select(c => new { c.CorporationId, c.AllianceId }).ToListAsync(ct);
        var ownCorps = chars.Select(c => c.CorporationId).Where(i => i > 0 && !IsNpcCorporation(i)).ToHashSet();
        var ownAllis = chars.Where(c => c.AllianceId is > 0).Select(c => c.AllianceId!.Value).ToHashSet();

        var saved = await db.Connectors.AsNoTracking().Select(e => new { e.Corporations, e.Alliances }).ToListAsync(ct);
        var allCorps = ownCorps.Concat(saved.SelectMany(s => ConnectorLists.Ids(s.Corporations))).Distinct().ToList();
        var allAllis = ownAllis.Concat(saved.SelectMany(s => ConnectorLists.Ids(s.Alliances))).Distinct().ToList();

        // The user's own first, by name; then those still chosen with none of their characters in them now,
        // whose names may be only a number ("Corp 98…"), which would otherwise sort ahead of every real name.
        var names = await NamesAsync(db, allCorps, allAllis, ct);
        return allCorps.Select(i => new ConnectorScopeChoice(false, i, ScopeName(names, false, i), ownCorps.Contains(i)))
                       .OrderBy(c => !c.OfOwnCharacter).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(allAllis.Select(i => new ConnectorScopeChoice(true, i, ScopeName(names, true, i), ownAllis.Contains(i)))
                            .OrderBy(c => !c.OfOwnCharacter).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    private static bool IsNpcCorporation(int id) => id is >= 1_000_000 and < 2_000_000;

    /// <summary>The stored names of these corporations (the user's own corporations, then the name cache) and
    /// alliances (the name cache).</summary>
    private static async Task<(Dictionary<int, string> Corps, Dictionary<int, string> Allis)> NamesAsync(
        AppDbContext db, IReadOnlyCollection<int> corps, IReadOnlyCollection<int> allis, CancellationToken ct)
    {
        var corpNames = new Dictionary<int, string>();
        var alliNames = new Dictionary<int, string>();
        if (corps.Count == 0 && allis.Count == 0) return (corpNames, alliNames);

        var corpIds = corps.ToList();
        foreach (var c in await db.Corporations.AsNoTracking().Where(c => corpIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
            if (c.Name.Length > 0) corpNames.TryAdd(c.Id, c.Name);

        var cached = corps.Concat(allis).Select(i => (long)i).Distinct().ToList();
        foreach (var u in await db.UniverseNames.AsNoTracking().Where(u => cached.Contains(u.EntityId)).Select(u => new { u.EntityId, u.Name }).ToListAsync(ct))
        {
            if (u.Name.Length == 0) continue;
            var id = (int)u.EntityId;
            if (corps.Contains(id)) corpNames.TryAdd(id, u.Name);
            if (allis.Contains(id)) alliNames.TryAdd(id, u.Name);
        }
        return (corpNames, alliNames);
    }

    private static string ScopeName((Dictionary<int, string> Corps, Dictionary<int, string> Allis) names, bool isAlliance, int id) =>
        isAlliance
            ? names.Allis.GetValueOrDefault(id) ?? string.Format(CharactersText.SummaryAllianceNumbered, id)
            : names.Corps.GetValueOrDefault(id) ?? string.Format(CharactersText.SummaryCorpNumbered, id);

    /// <summary>
    /// Once, on the worker: drops the preferences a single connector was kept in by earlier builds of this
    /// branch, and any links not kept for an existing entry (the old label-keyed rows, and orphans of removed
    /// entries), which nothing else would ever remove.
    /// </summary>
    public async Task CleanUpAsync(CancellationToken ct = default)
    {
        var forgot = await SqliteBusy.RetryAsync(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LinkedCharacterStore.LockAsync(db, ct);

            await db.AppPreferences.Where(p => p.Key.StartsWith("connector.")).ExecuteDeleteAsync(ct);
            var keep = (await db.Connectors.Select(e => e.Id).ToListAsync(ct)).Select(LinkedCharacterStore.SourceKey).ToList();
            var gone = await LinkedCharacterStore.ForgetOtherSourcesAsync(db, keep, ct);

            await tx.CommitAsync(ct);
            return gone;
        }, null, ct);
        if (forgot) _store.RaiseChanged();
    }

    // ── Connecting ───────────────────────────────────────────────────────────

    /// <summary>
    /// Connects the entry <paramref name="id"/> to the connector at <paramref name="typedUrl"/>: checks it is
    /// one, asks it to start a sign-in, shows the person the sign-in's code (<paramref name="showCode"/>), opens
    /// the connector's page in the browser, and waits for the connector to hand over a key. Then refreshes.
    /// Throws only when cancelled.
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
    public async Task<ConnectorConnectResult> ConnectAsync(int id, string typedUrl, Action<string>? showCode, CancellationToken ct)
    {
        var (url, urlError) = ParseUrl(typedUrl);
        if (url is null) return new(false, urlError!);

        try
        {
            if (await ReadEntryAsync(id, ct) is null) return new(false, SettingsText.ConnectorRemovedMeanwhile);

            var info = await InfoAsync(url, ct);
            if (info.Error is not null) return new(false, info.Error);

            var (login, startError) = await StartAsync(url, ct);
            if (login is null) return new(false, startError!);

            showCode?.Invoke(login.Code);
            _openBrowser(login.Page);

            var (signedIn, pollError) = await PollAsync(url, login, ct);
            if (signedIn is null) return new(false, pollError!);

            var (found, replaced) = await StoreKeyAsync(id, url, info.Name!, signedIn.Key, signedIn.Character, ct);
            if (!found)
            {
                // Removed meanwhile, perhaps from another PC: the new key is ended where it came from.
                await EndReplacedAsync(new Replaced(url, signedIn.Key), ct);
                return new(false, SettingsText.ConnectorRemovedMeanwhile);
            }

            // Connected either way from here; a refresh that went wrong, or a connector with nothing to give,
            // says so beside it.
            await EndReplacedAsync(replaced, ct);
            var refreshed = await RefreshAsync(id, ct);
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
    private async Task EndReplacedAsync(Replaced? replaced, CancellationToken ct, string context = "connect")
    {
        if (replaced is null) return;
        try
        {
            using var http = Authorised(replaced.Key);
            using var _ = await http.PostAsync(new Uri(replaced.Url, "v1/auth/sign-out"), null, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _errors?.Warn(nameof(ConnectorService), context, "The key replaced was not ended at the connector: " + ex.Message);
        }
    }

    /// <summary>The row's key, with its address, when it is not <paramref name="signedOut"/>: one stored meanwhile,
    /// by a connect on another client of a shared database, that a disconnect or remove is about to drop.</summary>
    private static Replaced? StoredSince(ConnectorEntry row, string signedOut) =>
        row.Key.Length > 0 && row.Key != signedOut && ParseUrl(row.Url).Url is { } url ? new Replaced(url, row.Key) : null;

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

    /// <summary>
    /// Stores the key a sign-in produced in the entry, and says which key it replaced, for ending afterwards.
    /// Found is false when the entry was removed meanwhile.
    ///
    /// <para>⚠️ Pointing an entry at a different connector forgets that entry's links, in the same transaction,
    /// and only that entry's. And the address goes with the key, never before it: a key is only sent where it
    /// came from.</para>
    ///
    /// <para>A new connection with nothing chosen yet is chosen for the signed-in character's corporation, when
    /// that is one of the user's own characters here and not an NPC corporation.</para>
    /// </summary>
    private async Task<(bool Found, Replaced? Replaced)> StoreKeyAsync(int id, Uri url, string connectorName, string key,
                                                                      CharacterAnswer character, CancellationToken ct)
    {
        Replaced? replaced = null;
        var after = await WriteEntryAsync(id, async (db, row) =>
        {
            replaced = row.Key.Length > 0 && row.Key != key && ParseUrl(row.Url).Url is { } oldUrl ? new Replaced(oldUrl, row.Key) : null;

            var cleared = false;
            if (row.Url.Length > 0 && row.Url != url.ToString())
                cleared = await LinkedCharacterStore.ClearSourceAsync(db, LinkedCharacterStore.SourceKey(id), ct);

            row.Url           = url.ToString();
            row.Key           = key;
            row.Name          = connectorName;
            row.CharacterId   = character.Id;
            row.CharacterName = character.Name ?? "";
            row.Capabilities  = "";      // asked for next, by the refresh
            row.Offers        = "";

            if (row.Corporations.Length == 0 && row.Alliances.Length == 0 && !row.FunctionsChosen)
            {
                var corp = await db.Characters.AsNoTracking().Where(c => c.Id == character.Id)
                    .Select(c => (int?)c.CorporationId).FirstOrDefaultAsync(ct);
                if (corp is > 0 && !IsNpcCorporation(corp.Value))
                    row.Corporations = corp.Value.ToString(CultureInfo.InvariantCulture);
            }
            return cleared;
        }, ct);

        _lastProblem.TryRemove(id, out _);
        return (after is not null, after is null ? null : replaced);
    }

    /// <summary>
    /// Ends the entry's key on the connector, forgets it here, and forgets what the connector gave. Also what is
    /// left over after the connector stopped knowing the key: there is no key to end then, only links. The
    /// address and the user's choices are kept, so connecting again is one click. Other connectors are untouched.
    ///
    /// <para>⚠️ Forgotten here whatever the connector says. Someone disconnecting from a connector that is
    /// down, or hung until the request times out, should still end up disconnected; the key is then simply
    /// never used again.</para>
    ///
    /// <para>⚠️ The sign-out can take until the request times out, and meanwhile another client of a shared
    /// database may have connected this entry anew. Whatever key is stored when the write runs is the one
    /// dropped, so that one is ended at the connector too, after the commit, as a connect ends the key it
    /// replaced.</para>
    /// </summary>
    public async Task DisconnectAsync(int id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            if (await ReadEntryAsync(id, ct) is not { } row) return;
            await SignOutAsync(row, "disconnect", ct);

            Replaced? since = null;
            await WriteEntryAsync(id, async (db, r) =>
            {
                since = StoredSince(r, row.Key);
                var cleared = await LinkedCharacterStore.ClearSourceAsync(db, LinkedCharacterStore.SourceKey(id), ct);
                r.Key = ""; r.Name = ""; r.CharacterId = 0; r.CharacterName = ""; r.Capabilities = ""; r.Offers = "";
                return cleared;
            }, ct);
            await EndReplacedAsync(since, ct, "disconnect");
            _lastProblem.TryRemove(id, out _);
            _lastResult.TryRemove(id, out _);
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Removes the entry: ends its key on the connector (best effort, as <see cref="DisconnectAsync"/>), and
    /// forgets the entry and what it gave. Other connectors are untouched. A key stored meanwhile by another
    /// client is ended too, as in <see cref="DisconnectAsync"/>.
    /// </summary>
    public async Task RemoveAsync(int id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var signedOut = "";
            if (await ReadEntryAsync(id, ct) is { } row)
            {
                await SignOutAsync(row, "remove", ct);
                signedOut = row.Key;
            }

            Replaced? since = null;
            await SqliteBusy.RetryAsync(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await LinkedCharacterStore.LockAsync(db, ct);
                var current = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
                since = current is null ? null : StoredSince(current, signedOut);
                await LinkedCharacterStore.ClearSourceAsync(db, LinkedCharacterStore.SourceKey(id), ct);
                await db.Connectors.Where(e => e.Id == id).ExecuteDeleteAsync(ct);
                await tx.CommitAsync(ct);
            }, null, ct);
            await EndReplacedAsync(since, ct, "remove");

            _lastProblem.TryRemove(id, out _);
            _lastResult.TryRemove(id, out _);
            _offersTooLong.TryRemove(id, out _);
        }
        finally { gate.Release(); }

        // The gate goes with the entry. Not disposed: a caller still waiting on it is let through, and finds no row.
        _gates.TryRemove(id, out _);
        _store.RaiseChanged();
        Changed?.Invoke();
    }

    /// <summary>Tells the connector the entry's key is ended, if it has one. A warning when that fails, never
    /// more: forgotten here is what was asked, and it happens.</summary>
    private async Task SignOutAsync(ConnectorEntry row, string context, CancellationToken ct)
    {
        if (row.Key.Length == 0 || ParseUrl(row.Url).Url is not { } url) return;
        try
        {
            using var http = Authorised(row.Key);
            using var res = await http.PostAsync(new Uri(url, "v1/auth/sign-out"), null, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _errors?.Warn(nameof(ConnectorService), context, "The connector was not told: " + ex.Message);
        }
    }

    /// <summary>Forgets the entry's key and who it was for, only if that is still its key. Links are kept.</summary>
    private Task ForgetKeyAsync(int id, string ifKey, CancellationToken ct) =>
        WriteEntryAsync(id, (_, r) =>
        {
            r.Key = ""; r.CharacterId = 0; r.CharacterName = ""; r.Capabilities = ""; r.Offers = "";
            return Task.FromResult(false);
        }, ct, ifKey);

    // ── Refreshing ───────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the connector what it offers, and stores what this build keeps of it. Never throws for the
    /// connector's answer or its absence: the result says what happened.
    ///
    /// <para>⚠️ One at a time in this process for each entry, with disconnecting and removing it: a refresh
    /// that had its answer when Disconnect ran would otherwise store it after the links were cleared. Across
    /// clients of a shared database, the store checks inside its own transaction that the key it was read with
    /// is still the one stored, and that the entry is still used for linked characters.</para>
    /// </summary>
    public async Task<ConnectorRefreshResult> RefreshAsync(int id, CancellationToken ct = default)
    {
        var gate = Gate(id);
        await gate.WaitAsync(ct);
        try
        {
            var result = await RefreshLockedAsync(id, ct);
            _lastResult[id] = result;
            return result;
        }
        finally { gate.Release(); }
    }

    /// <summary>Refreshes every connected entry, one after another; a failure of one is logged and the next
    /// one asked all the same.</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        foreach (var entry in await ListAsync(ct))
        {
            if (!entry.Connected) continue;
            try { await RefreshAsync(entry.Id, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _errors?.Log(nameof(ConnectorService), $"refresh {entry.Id}", ex); }
        }
    }

    private async Task<ConnectorRefreshResult> RefreshLockedAsync(int id, CancellationToken ct)
    {
        var entry = await ReadEntryAsync(id, ct);
        if (entry is not { Key.Length: > 0 } || ParseUrl(entry.Url).Url is not { } url)
            return new(ConnectorRefreshOutcome.NotConnected, SettingsText.ConnectorNotConnected);

        var key   = entry.Key;
        var title = ConnectorSnapshot.From(entry).Title;
        try
        {
            using var http = Authorised(key);

            using var meRes = await http.GetAsync(new Uri(url, "v1/me"), ct);
            if (await Refused(meRes, id, title, key, ct) is { } refusedMe) return refusedMe;

            var me = JsonSerializer.Deserialize<MeAnswer>(await meRes.Content.ReadAsStringAsync(ct), Json);
            var caps = me?.Capabilities?.OfType<string>().Select(c => c.Trim())
                .Where(c => c.Length > 0 && c.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                .Distinct().ToList() ?? [];
            var offers = OffersText(id, title, me?.Offers);

            var after = await WriteEntryAsync(id, async (db, row) =>
            {
                row.Capabilities = ConnectorLists.Join(caps);
                row.Offers       = offers;
                if (me?.Character?.Name is { Length: > 0 } n) row.CharacterName = n;

                // The defaults after a new connection: what it offers, except what another connector already has
                // for the same corporation or alliance. Not before it offers something this build knows: a
                // connector that starts offering later still gets them.
                if (!row.FunctionsChosen && caps.Any(ConnectorFunctions.IsKnown))
                {
                    var others = (await db.Connectors.AsNoTracking().Where(o => o.Id != id).ToListAsync(ct))
                        .Select(ConnectorSnapshot.From).ToList();
                    var corps = ConnectorLists.Ids(row.Corporations);
                    var allis = ConnectorLists.Ids(row.Alliances);
                    row.Functions = ConnectorLists.Join(ConnectorFunctions.All.Where(f => caps.Contains(f)
                        && ConnectorRouter.FindConflict(others, corps, allis, [f]) is null));
                    row.FunctionsChosen = true;
                }
                return false;
            }, ct, ifKey: key);
            // Disconnected, removed, or connected anew, while this was asking: nothing to say about this answer.
            if (after is null) return new(ConnectorRefreshOutcome.NotConnected, SettingsText.ConnectorNotConnected);

            // ⚠️ Nothing more is asked of a connector chosen for no corporation or alliance.
            var snap = ConnectorSnapshot.From(after);
            if (!snap.HasScope)
                return Settled(id, new(ConnectorRefreshOutcome.NotUsed, SettingsText.ConnectorNoScope));
            if (!snap.Offers(ConnectorFunctions.LinkedCharacters))
                return Settled(id, new(ConnectorRefreshOutcome.NothingOffered, SettingsText.ConnectorNothingOffered));
            if (!snap.Chose(ConnectorFunctions.LinkedCharacters))
                return Settled(id, new(ConnectorRefreshOutcome.NotUsed, SettingsText.ConnectorLinkedNotChosen));

            using var linkedRes = await http.GetAsync(new Uri(url, "v1/linked-characters"), ct);
            if (linkedRes.StatusCode == HttpStatusCode.NotFound)
                return Settled(id, new(ConnectorRefreshOutcome.NothingOffered, SettingsText.ConnectorNothingOffered));
            if (await Refused(linkedRes, id, title, key, ct) is { } refusedLinked) return refusedLinked;

            var (snapshot, error) = LinkedCharacterStore.Parse(await linkedRes.Content.ReadAsStringAsync(ct));
            if (snapshot is null)
                return Problem(id, title, new(ConnectorRefreshOutcome.Rejected, string.Format(SettingsText.ConnectorErrRejected, error)), error);

            // ⚠️ Stored as this entry's own source, whatever label the answer names, and only while the key this
            // was read with is still the entry's and the entry is still used for linked characters, checked inside
            // the store's transaction: another client may have disconnected, unticked, or connected anew meanwhile.
            var stored = await _store.ImportAsync(snapshot, ct, sourceKey: LinkedCharacterStore.SourceKey(id),
                stillWanted: async (db, c) =>
                {
                    var r = await db.Connectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, c);
                    return r is not null && r.Key == key
                        && ConnectorSnapshot.From(r) is { HasScope: true } s && s.Chose(ConnectorFunctions.LinkedCharacters);
                });
            return stored switch
            {
                LinkedStoreResult.Stored    => Settled(id, new(ConnectorRefreshOutcome.Imported,  SettingsText.ConnectorRefreshed)),
                LinkedStoreResult.NotNewer  => Settled(id, new(ConnectorRefreshOutcome.Unchanged, SettingsText.ConnectorUnchanged)),
                _                           => new(ConnectorRefreshOutcome.NotConnected, SettingsText.ConnectorNotConnected),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or JsonException)
        {
            return Problem(id, title, new(ConnectorRefreshOutcome.Unreachable, string.Format(SettingsText.ConnectorErrUnreachable, url.Authority)),
                           ex.Message);
        }
    }

    /// <summary>The raw "offers" array, kept for later features; "" when absent, not an array, or too long.</summary>
    private string OffersText(int id, string title, JsonElement? offers)
    {
        if (offers is not { ValueKind: JsonValueKind.Array } array) { _offersTooLong.TryRemove(id, out _); return ""; }
        var raw = array.GetRawText();
        if (raw.Length <= MaxOffersLength) { _offersTooLong.TryRemove(id, out _); return raw; }

        if (_offersTooLong.TryAdd(id, true))
            _errors?.Warn(nameof(ConnectorService), "refresh",
                $"Connector {title} (#{id}): \"offers\" is longer than {MaxOffersLength:N0} characters, so it is not kept.");
        return "";
    }

    /// <summary>A refusal by the connector, as a result; null for an answer to read.</summary>
    private async Task<ConnectorRefreshResult?> Refused(HttpResponseMessage res, int id, string title, string key, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return null;
        var body = await res.Content.ReadAsStringAsync(ct);

        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Only the key that was refused: a newer one, from connecting again meanwhile, stays.
            await ForgetKeyAsync(id, key, ct);
            return Problem(id, title, new(ConnectorRefreshOutcome.SignedOut, SettingsText.ConnectorErrSignedOut), "the connector no longer knows the key");
        }
        if (res.StatusCode == HttpStatusCode.Forbidden)
            return Problem(id, title, new(ConnectorRefreshOutcome.NotAllowed, ErrorText(body, res.StatusCode)), "refused: " + body);

        // A redirect counts as a refusal: they are not followed, so a key never goes anywhere it wasn't issued.
        return Problem(id, title, new(ConnectorRefreshOutcome.Unreachable, ErrorText(body, res.StatusCode)), $"{(int)res.StatusCode}: {body}");
    }

    /// <summary>A problem, written to the error log once per connector until it changes or clears.</summary>
    private ConnectorRefreshResult Problem(int id, string title, ConnectorRefreshResult result, string? detail)
    {
        var said = $"{result.Outcome}: {detail}";
        if (_lastProblem.GetValueOrDefault(id) != said)
            _errors?.Warn(nameof(ConnectorService), "refresh", $"Connector {title} (#{id}) refresh: {result.Outcome}", detail);
        _lastProblem[id] = said;
        return result;
    }

    private ConnectorRefreshResult Settled(int id, ConnectorRefreshResult result)
    {
        _lastProblem.TryRemove(id, out _);
        return result;
    }

    // ── The background refresh ───────────────────────────────────────────────

    /// <summary>
    /// The refresh of each connected entry every half hour. ⚠️ Worker-lease only: it writes the shared cache,
    /// and one client doing so is enough. Does nothing but read the connector list while nothing is connected.
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

        try { await CleanUpAsync(ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _errors?.Log(nameof(ConnectorService), "clean up", ex); }

        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAllAsync(ct); }
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
    private sealed record MeAnswer(CharacterAnswer? Character, List<string?>? Capabilities, JsonElement? Offers);
    private sealed record ErrorAnswer(string? Error, string? Message);
}
