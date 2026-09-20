using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.WebStore;

/// <summary>
/// Puts a store's site on the owner's own Cloudflare account, and keeps it current.
///
/// <para>Deploying is: the newest site release this app can talk to (<see cref="SiteReleases"/>),
/// a D1 database named after the Worker (found if it exists, created if not), the Worker uploaded
/// with that database bound, its version as a variable, the store's secret and — when given — the
/// EVE application's id and secret as secrets, and the free workers.dev address switched on. Doing
/// it again is an update: the same steps with the same names, and the secrets already on the site
/// stay unless new ones are given. A site the owner set up by hand with wrangler is the same thing
/// afterwards; either can be updated either way.</para>
///
/// <para>⚠️ The site's data is never touched. An upload replaces the code and re-declares the
/// bindings; the database keeps every order and setting it holds, and the site migrates its own
/// schema on the first request after an update.</para>
///
/// <para>⚠️ The API token lives on this machine only (<see cref="AppConfig.SetCloudflareToken"/>),
/// never in the database: the machine that deploys is the owner's, and a second client on the same
/// database has no business holding a key to their Cloudflare account. Syncing needs no token —
/// only the store's shared secret, which does live with the store.</para>
/// </summary>
public sealed partial class CloudflareDeployService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory              httpFactory,
    WebStoreSyncService             sync,
    AppErrorLogger                  errorLogger)
{
    public sealed record TokenCheck(
        bool Ok, string Text,
        IReadOnlyList<CloudflareClient.Account> Accounts,
        IReadOnlyDictionary<string, string>     Subdomains);

    public sealed record Outcome(bool Ok, string Text, string Url = "", string Version = "");

    /// <summary>How many times, three seconds apart, a freshly uploaded site is asked for its version; none skips the wait.</summary>
    public int VerifyAttempts { get; set; } = 6;

    /// <summary>Where the token comes from: this machine's config, or whatever a harness hands in.</summary>
    public Func<string?> TokenSource { get; set; } = AppConfig.GetCloudflareToken;

    private CloudflareClient Cloudflare => new(httpFactory.CreateClient("cloudflare"));
    private SiteReleases     Releases   => new(httpFactory.CreateClient("github"));

    /// <summary>A name Cloudflare accepts — lower-case letters, digits and hyphens — from the store's name.</summary>
    public static string DefaultWorkerName(string storeName) => "eveconsole-" + Slug(storeName, 29, "store");

    /// <summary>Lower-case letters, digits and single hyphens, at most <paramref name="max"/> long.</summary>
    private static string Slug(string text, int max, string fallback)
    {
        var sb   = new StringBuilder();
        var dash = true;
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
            if (sb.Length >= max) break;
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 0 ? s : fallback;
    }

    public static bool IsValidWorkerName(string name) => WorkerName().IsMatch(name);

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex WorkerName();

    // ── The token ─────────────────────────────────────────────────────────────

    /// <summary>Whether a token works and what it reaches. Nothing is saved here.</summary>
    public async Task<TokenCheck> CheckTokenAsync(string token, CancellationToken ct = default)
    {
        var none = new Dictionary<string, string>();
        var cf   = Cloudflare;
        try
        {
            if (await cf.VerifyTokenAsync(token, ct) is { } why)
                return new TokenCheck(false, why, [], none);

            var accounts = await cf.AccountsAsync(token, ct);
            if (accounts.Count == 0)
                return new TokenCheck(false, "The token is active but reaches no account. Make it from the \"Edit Cloudflare Workers\" template with D1 Edit added.", [], none);

            var subdomains = new Dictionary<string, string>();
            foreach (var a in accounts)
                if (await cf.SubdomainAsync(token, a.Id, ct) is { } s) subdomains[a.Id] = s;

            string text;
            if (accounts.Count == 1)
                text = subdomains.TryGetValue(accounts[0].Id, out var sub)
                    ? $"Token works for {accounts[0].Name}; sites go to *.{sub}.workers.dev."
                    : $"Token works for {accounts[0].Name}, which has no workers.dev name yet; the first deploy chooses one.";
            else

                text = $"Token works for {accounts.Count} accounts; pick the one to deploy to.";
            return new TokenCheck(true, text, accounts, subdomains);
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return new TokenCheck(false, Plain(ex), [], none);
        }
    }

    // ── Deploy, which is also update ──────────────────────────────────────────

    /// <summary>
    /// Puts the newest compatible site release on Cloudflare for this store, creating what does
    /// not exist and keeping what does. The store's secret and its EVE application keys go on
    /// the site every time; secrets the site holds that the store does not know stay as they are.
    /// </summary>
    public async Task<Outcome> DeployAsync(int storeId, IProgress<string>? progress = null, CancellationToken ct = default)

    {
        var token = TokenSource();

        if (token is null)
            return new Outcome(false, "No Cloudflare API token is saved on this machine. Paste one and press Save token first.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return new Outcome(false, "The store no longer exists.");

        var cf = Cloudflare;
        try
        {
            // Which account. A token that reaches one account needs no choosing.
            var accountId = store.WebCloudflareAccountId;
            if (accountId.Length == 0)
            {
                var accounts = await cf.AccountsAsync(token, ct);
                if (accounts.Count != 1)
                    return new Outcome(false, accounts.Count == 0
                        ? "The token reaches no Cloudflare account."
                        : "The token reaches several Cloudflare accounts; pick the one to deploy to first.");
                accountId = accounts[0].Id;
            }

            var name = store.WebWorkerName.Length > 0 ? store.WebWorkerName : DefaultWorkerName(store.Name);
            if (!IsValidWorkerName(name))
                return new Outcome(false, $"\"{name}\" is not a name Cloudflare accepts: lower-case letters, digits and hyphens, up to 63 of them.");

            var hostname = "";
            if (store.WebCustomHostname.Trim().Length > 0)
            {
                hostname = CleanHostname(store.WebCustomHostname)
                        ?? throw new InvalidOperationException($"\"{store.WebCustomHostname}\" is not a hostname: something like store.example.com, letters, digits, hyphens and dots only.");
            }

            progress?.Report("Looking up the newest site release…");
            var release  = await Releases.LatestCompatibleAsync(WebStoreProtocol.Version, progress, ct);
            var manifest = release.Manifest;

            progress?.Report($"Database {name}…");
            var database = await cf.FindDatabaseAsync(token, accountId, name, ct)
                        ?? await cf.CreateDatabaseAsync(token, accountId, name, ct);

            if (store.WebSecret.Length == 0) store.WebSecret = WebStoreSigner.NewSecret();

            // What the upload declares. Secrets are not among them: they go through the secrets
            // endpoint after the upload, the way wrangler sets them, and keep_bindings below keeps
            // every secret the site already has across the new version.
            var bindings = new List<Dictionary<string, string>>
            {
                new() { ["type"] = "d1",         ["name"] = manifest.Bindings?.D1 ?? "DB", ["id"]   = database.Id },
                new() { ["type"] = "plain_text", ["name"] = "SITE_VERSION",                ["text"] = manifest.Version },
            };
            foreach (var (key, value) in manifest.Bindings?.Vars ?? new Dictionary<string, string>())
                if (key != "SITE_VERSION")
                    bindings.Add(new() { ["type"] = "plain_text", ["name"] = key, ["text"] = value });

            var metadata = JsonSerializer.Serialize(new
            {
                main_module         = "index.js",
                compatibility_date  = string.IsNullOrEmpty(manifest.CompatibilityDate) ? "2025-06-01" : manifest.CompatibilityDate,
                compatibility_flags = manifest.CompatibilityFlags ?? [],
                bindings,
                // Every secret the site holds stays across the upload; the ones the store knows
                // are set again right after it.
                keep_bindings       = new[] { "secret_text" },

                observability       = new { enabled = true },
            });

            progress?.Report($"Uploading site {manifest.Version} as {name}…");
            await cf.UploadScriptAsync(token, accountId, name, metadata, release.Module, ct);

            progress?.Report("Setting the site's secrets…");
            await PutSecretsAsync(cf, token, accountId, name, store, ct);

            string? subdomain = null;
            var letGo = false;
            if (hostname.Length > 0)
            {
                // A domain of the owner's own: the Worker is attached to it — Cloudflare makes the
                // DNS record and the certificate — and taken off workers.dev, so buyers and the
                // EVE application's callback see one name.
                progress?.Report($"Putting the site on {hostname}…");
                var zone = await FindZoneForAsync(cf, token, accountId, hostname, ct);
                if (zone is null)
                    return new Outcome(false,
                        $"No domain on this Cloudflare account holds {hostname}. The domain must be on Cloudflare, in this account, with its DNS there: "
                        + "add it in the dashboard first, or use the free workers.dev address.");
                try { await cf.AttachDomainAsync(token, accountId, zone.Id, hostname, name, ct); }
                catch (CloudflareException ex)
                {
                    return new Outcome(false, Plain(ex)
                        + " If Cloudflare is saying the token may not do this, give the token Zone → Workers Routes: Edit and Zone → DNS: Edit for that domain, on top of the Workers permissions it has.");
                }
                await cf.SetWorkersDevAsync(token, accountId, name, enabled: false, ct);
            }
            else
            {
                progress?.Report("Switching on the workers.dev address…");
                await cf.SetWorkersDevAsync(token, accountId, name, enabled: true, ct);
                subdomain = await cf.SubdomainAsync(token, accountId, ct);
                if (subdomain is null)
                {
                    // The account has no workers.dev name yet. One is chosen from the account's
                    // name, as the dashboard would suggest, with a number added when that is taken.
                    progress?.Report("Choosing the account's workers.dev name…");
                    subdomain = await ChooseSubdomainAsync(cf, token, accountId, ct);
                }

                // A domain this Worker used to be on is let go, so the name stops pointing at it.
                // Cloudflare's own list says whether the address was one of ours; an address the
                // owner typed in by hand is not there and is left alone.
                if (Uri.TryCreate(store.WebUrl, UriKind.Absolute, out var old) && old.Host.Length > 0
                    && !old.Host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase))
                    foreach (var d in await cf.DomainsAsync(token, accountId, old.Host, ct))
                        if (d.Service == name) { await cf.DetachDomainAsync(token, accountId, d.Id, ct); letGo = true; }
            }

            store.WebCloudflareAccountId = accountId;
            store.WebWorkerName          = name;
            if (hostname.Length > 0)
                store.WebUrl = $"https://{hostname}";
            else if (subdomain is not null
                && (store.WebUrl.Length == 0 || letGo || store.WebUrl.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase)))
                store.WebUrl = $"https://{name}.{subdomain}.workers.dev";   // unless the owner put an address of their own in by hand
            store.WebLastError = "";
            await db.SaveChangesAsync(ct);

            if (store.WebUrl.Length == 0)
                return new Outcome(true,
                    $"Site {manifest.Version} uploaded as {name}, but no workers.dev name could be chosen for the account, so it has no address yet. "
                    + "Choose one under Workers & Pages in the Cloudflare dashboard, then deploy again.",
                    "", manifest.Version);

            progress?.Report("Waiting for the site to answer…");
            var seen = await SiteVersionAsync(store.WebUrl, VerifyAttempts, ct);
            var text = seen is null
                ? $"Site {manifest.Version} uploaded to {store.WebUrl}, but it is not answering yet; give it a minute and press Check."
                : Same(seen.Version, manifest.Version)
                    ? $"Site {manifest.Version} is up at {store.WebUrl}."
                    : $"Site uploaded to {store.WebUrl}; it still answers as {seen.Version}, so give it a minute.";

            if (store.WebEveClientId.Trim().Length == 0 || store.WebEveClientSecret.Trim().Length == 0)
                text += $" Buyers cannot sign in yet: register an EVE application with callback {store.WebUrl}/auth/callback and enter its Client ID and Secret Key; they go to the site as soon as they are saved.";
            else if (seen?.SsoConfigured == false)
                text += " The site does not report its EVE application keys yet; give it a minute and press Check.";

            if (store.WebEnabled) sync.Nudge();
            return new Outcome(true, text, store.WebUrl, manifest.Version);
        }
        catch (Exception ex) when (ex is CloudflareException or InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            errorLogger.Log(nameof(CloudflareDeployService), "deploy", ex);
            return new Outcome(false, Plain(ex));
        }
    }

    // ── The site's secrets ────────────────────────────────────────────────────

    /// <summary>
    /// Puts the store's EVE application keys on a site this app deployed, without a new upload:
    /// what the Config tab does the moment the keys are saved, so sign-in works from then on.
    /// </summary>
    public async Task<Outcome> PutEveKeysAsync(int storeId, CancellationToken ct = default)
    {
        var token = TokenSource();
        if (token is null)
            return new Outcome(false, "No Cloudflare API token is saved on this machine, so the keys wait here for the next deploy.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return new Outcome(false, "The store no longer exists.");
        if (store.WebCloudflareAccountId.Length == 0 || store.WebWorkerName.Length == 0)
            return new Outcome(false, "This site was not deployed from here: set the keys on it as its EVE_CLIENT_ID and EVE_CLIENT_SECRET secrets (wrangler secret put).");
        if (store.WebEveClientId.Trim().Length == 0 || store.WebEveClientSecret.Trim().Length == 0)
            return new Outcome(false, "Both the Client ID and the Secret Key are needed before they can go on the site.");

        try
        {
            var cf = Cloudflare;
            await cf.PutSecretAsync(token, store.WebCloudflareAccountId, store.WebWorkerName, "EVE_CLIENT_ID",     store.WebEveClientId.Trim(),     ct);
            await cf.PutSecretAsync(token, store.WebCloudflareAccountId, store.WebWorkerName, "EVE_CLIENT_SECRET", store.WebEveClientSecret.Trim(), ct);
            return new Outcome(true, "EVE application keys placed on the site; buyers can sign in from now on.", store.WebUrl);
        }
        catch (Exception ex) when (ex is CloudflareException or HttpRequestException or TaskCanceledException or JsonException)
        {
            errorLogger.Log(nameof(CloudflareDeployService), "put keys", ex);
            return new Outcome(false, Plain(ex));
        }
    }

    /// <summary>The store's secret and its EVE application keys, one call each; a blank one is left as the site has it.</summary>
    private static async Task PutSecretsAsync(CloudflareClient cf, string token, string accountId, string script, Store store, CancellationToken ct)
    {
        foreach (var (name, value) in new[]
        {
            ("STORE_SYNC_SECRET", store.WebSecret),
            ("EVE_CLIENT_ID",     store.WebEveClientId.Trim()),
            ("EVE_CLIENT_SECRET", store.WebEveClientSecret.Trim()),
        })
            if (value.Length > 0) await cf.PutSecretAsync(token, accountId, script, name, value, ct);
    }

    // ── Check, which changes nothing ──────────────────────────────────────────

    /// <summary>What the site runs, against what is released.</summary>
    public async Task<Outcome> CheckSiteAsync(int storeId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return new Outcome(false, "The store no longer exists.");
        if (store.WebUrl.Length == 0)
            return new Outcome(false, "No site address yet. Deploy, or enter the address of a site set up by hand.");

        try
        {
            var seen = await SiteVersionAsync(store.WebUrl, attempts: 1, ct);
            if (seen is null) return new Outcome(false, $"{store.WebUrl} is not answering at /api/version.");
            var (version, protocol) = (seen.Version, seen.Protocol);

            if (version != store.WebSiteVersion) { store.WebSiteVersion = version; await db.SaveChangesAsync(ct); }

            var text = $"Site runs {version}";
            if (protocol != 0 && protocol != WebStoreProtocol.Version)
                text += protocol > WebStoreProtocol.Version
                    ? $" (protocol {protocol}; this EVE Console speaks {WebStoreProtocol.Version} — update EVE Console)"
                    : $" (protocol {protocol}; this EVE Console needs {WebStoreProtocol.Version} — update the site)";

            var newest = await Releases.NewestAsync(ct);
            if (newest is null)                                   text += $". No release was found at {SiteReleases.ReleasesUrl}.";
            else if (Same(newest.Version, version))               text += ", the newest release.";
            else if (newest.Protocol > WebStoreProtocol.Version)  text += $". Release {newest.Version} is out but needs a newer EVE Console (protocol {newest.Protocol}).";
            else if (Newer(newest.Version, version))              text += $". Release {newest.Version} is available — press Deploy to update.";
            else                                                  text += $"; the newest release is {newest.Version}.";
            if (seen.SsoConfigured == false)
                text += " Sign-in is not set up on the site: it has no EVE application keys. Enter them on this tab; a site deployed from here gets them at once.";
            else if (seen.SsoClientId is not null && store.WebEveClientId.Trim().Length > 0 && store.WebEveClientSecret.Trim().Length > 0 && !KeysMatch(store, seen))
                text += " The site's EVE application keys are not the ones saved here; press Deploy or update site to send these.";
            return new Outcome(true, text, store.WebUrl, version);

        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Outcome(false, Plain(ex));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>The zone on the account that holds a hostname: the name itself, then each parent
    /// down to two labels — store.shop.example.com may sit in shop.example.com or in example.com.</summary>
    private static async Task<CloudflareClient.Zone?> FindZoneForAsync(CloudflareClient cf, string token, string accountId, string hostname, CancellationToken ct)
    {
        var labels = hostname.Split('.');
        for (var i = 0; i <= labels.Length - 2; i++)
        {
            var zone = await cf.FindZoneAsync(token, accountId, string.Join('.', labels[i..]), ct);
            if (zone is not null) return zone;
        }
        return null;
    }

    /// <summary>A hostname as typed, tidied — no scheme, path or capitals — or null when it is
    /// not one: labels of letters, digits and hyphens, at least two of them, dots between.</summary>
    public static string? CleanHostname(string typed)
    {
        var s = typed.Trim().ToLowerInvariant();
        if (s.StartsWith("https://")) s = s[8..]; else if (s.StartsWith("http://")) s = s[7..];
        s = s.Split('/')[0].TrimEnd('.');
        return System.Text.RegularExpressions.Regex.IsMatch(s, @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$") ? s : null;
    }

    /// <summary>The account's name as a workers.dev name, or that with a number when it is taken; null when neither can be had.</summary>
    private static async Task<string?> ChooseSubdomainAsync(CloudflareClient cf, string token, string accountId, CancellationToken ct)
    {
        var accounts = await cf.AccountsAsync(token, ct);
        var basis    = Slug(accounts.FirstOrDefault(a => a.Id == accountId)?.Name ?? "", 40, "eveconsole");
        foreach (var candidate in new[] { basis, $"{basis}-{Random.Shared.Next(1000, 9999)}", $"{basis}-{Random.Shared.Next(10000, 99999)}" })
        {
            try { await cf.CreateSubdomainAsync(token, accountId, candidate, ct); return candidate; }
            catch (CloudflareException) { /* taken, or refused: the next candidate */ }
        }
        return null;
    }

    /// <summary>What a site answers at /api/version. The sign-in fields came with 0.1.1 (whether it has keys) and 0.1.2 (which).</summary>
    public sealed record SiteProbe(string Version, int Protocol, bool? SsoConfigured, string? SsoClientId, string? SsoFingerprint);

    /// <summary>The first 12 hex characters of SHA-256 over the secret: enough to tell two keys apart, useless for finding one.</summary>
    public static string KeyFingerprint(string secret) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()))).ToLowerInvariant()[..12];

    /// <summary>Whether the keys a site reports are the store's own. False for a site too old to say.</summary>
    public static bool KeysMatch(Store store, SiteProbe probe) =>
        probe.SsoClientId is not null && probe.SsoFingerprint is not null
        && probe.SsoClientId == store.WebEveClientId.Trim()
        && probe.SsoFingerprint == KeyFingerprint(store.WebEveClientSecret);

    /// <summary>What the site answers at /api/version, for the Config tab's sign-in line; null when it does not answer.</summary>
    public Task<SiteProbe?> ProbeSiteAsync(string url, CancellationToken ct = default)
        => SiteVersionAsync(url, attempts: 1, ct);

    private async Task<SiteProbe?> SiteVersionAsync(string url, int attempts, CancellationToken ct)

    {
        var web = httpFactory.CreateClient("webstore");
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var response = await web.GetAsync(url.TrimEnd('/') + "/api/version", ct);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    var v = doc.RootElement.TryGetProperty("siteVersion", out var sv) ? sv.GetString() ?? "" : "";
                    var p = doc.RootElement.TryGetProperty("protocol", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetInt32() : 0;
                    // Sites from 0.1.1 say whether they hold EVE application keys; older ones say nothing.
                    var sso = doc.RootElement.TryGetProperty("ssoConfigured", out var sc) && sc.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? sc.GetBoolean() : (bool?)null;
                    var cid = doc.RootElement.TryGetProperty("ssoClientId",       out var ci) && ci.ValueKind == JsonValueKind.String ? ci.GetString() : null;
                    var fp  = doc.RootElement.TryGetProperty("ssoKeyFingerprint", out var kf) && kf.ValueKind == JsonValueKind.String ? kf.GetString() : null;
                    if (v.Length > 0) return new SiteProbe(v, p, sso, cid, fp);

                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
            if (i + 1 < attempts) await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return null;
    }

    private static string Trim(string v) => v.Trim().TrimStart('v', 'V');
    private static bool Same(string a, string b) => Trim(a) == Trim(b);

    private static bool Newer(string a, string b) =>
        Version.TryParse(Trim(a), out var x) && Version.TryParse(Trim(b), out var y) ? x > y : string.CompareOrdinal(Trim(a), Trim(b)) > 0;

    private static string Plain(Exception ex) => ex switch
    {
        TaskCanceledException => "Timed out waiting for an answer.",
        HttpRequestException  => $"Could not reach the service: {ex.Message}",
        _                     => ex.Message,
    };
}
