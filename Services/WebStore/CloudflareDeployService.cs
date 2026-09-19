using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EveConsole.Data;
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
    public static string DefaultWorkerName(string storeName)
    {
        var sb   = new StringBuilder("eveconsole-");
        var dash = true;
        foreach (var ch in storeName.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') { sb.Append(ch); dash = false; }
            else if (!dash) { sb.Append('-'); dash = true; }
            if (sb.Length >= 40) break;
        }
        var name = sb.ToString().TrimEnd('-');
        return name.Length > "eveconsole-".Length ? name : "eveconsole-store";
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
                    : $"Token works for {accounts[0].Name}, which has no workers.dev name yet — choose one under Workers & Pages in the Cloudflare dashboard before deploying.";
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
    /// not exist and keeping what does. The EVE application's id and secret are set when given
    /// and left as they are on the site when not.
    /// </summary>
    public async Task<Outcome> DeployAsync(
        int storeId, string? eveClientId, string? eveClientSecret,
        IProgress<string>? progress = null, CancellationToken ct = default)
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

            progress?.Report("Looking up the newest site release…");
            var release  = await Releases.LatestCompatibleAsync(WebStoreProtocol.Version, progress, ct);
            var manifest = release.Manifest;

            progress?.Report($"Database {name}…");
            var database = await cf.FindDatabaseAsync(token, accountId, name, ct)
                        ?? await cf.CreateDatabaseAsync(token, accountId, name, ct);

            if (store.WebSecret.Length == 0) store.WebSecret = WebStoreSigner.NewSecret();

            var bindings = new List<Dictionary<string, string>>
            {
                new() { ["type"] = "d1",          ["name"] = manifest.Bindings?.D1 ?? "DB", ["id"]   = database.Id },
                new() { ["type"] = "plain_text",  ["name"] = "SITE_VERSION",                ["text"] = manifest.Version },
                new() { ["type"] = "secret_text", ["name"] = "STORE_SYNC_SECRET",           ["text"] = store.WebSecret },
            };
            foreach (var (key, value) in manifest.Bindings?.Vars ?? new Dictionary<string, string>())
                if (key != "SITE_VERSION")
                    bindings.Add(new() { ["type"] = "plain_text", ["name"] = key, ["text"] = value });
            if (!string.IsNullOrWhiteSpace(eveClientId))
                bindings.Add(new() { ["type"] = "secret_text", ["name"] = "EVE_CLIENT_ID",     ["text"] = eveClientId.Trim() });
            if (!string.IsNullOrWhiteSpace(eveClientSecret))
                bindings.Add(new() { ["type"] = "secret_text", ["name"] = "EVE_CLIENT_SECRET", ["text"] = eveClientSecret.Trim() });

            var metadata = JsonSerializer.Serialize(new
            {
                main_module         = "index.js",
                compatibility_date  = string.IsNullOrEmpty(manifest.CompatibilityDate) ? "2025-06-01" : manifest.CompatibilityDate,
                compatibility_flags = manifest.CompatibilityFlags ?? [],
                bindings,
                // Secrets already on the site stay unless re-declared above, so an update never
                // needs the EVE application's secret typed again.
                keep_bindings       = new[] { "secret_text" },
                observability       = new { enabled = true },
            });

            progress?.Report($"Uploading site {manifest.Version} as {name}…");
            await cf.UploadScriptAsync(token, accountId, name, metadata, release.Module, ct);

            progress?.Report("Switching on the workers.dev address…");
            await cf.EnableWorkersDevAsync(token, accountId, name, ct);
            var subdomain = await cf.SubdomainAsync(token, accountId, ct);

            store.WebCloudflareAccountId = accountId;
            store.WebWorkerName          = name;
            if (!string.IsNullOrWhiteSpace(eveClientId)) store.WebEveClientId = eveClientId.Trim();
            // The workers.dev address, unless the owner has put a domain of their own in.
            if (subdomain is not null
                && (store.WebUrl.Length == 0 || store.WebUrl.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase)))
                store.WebUrl = $"https://{name}.{subdomain}.workers.dev";
            store.WebLastError = "";
            await db.SaveChangesAsync(ct);

            if (store.WebUrl.Length == 0)
                return new Outcome(true,
                    $"Site {manifest.Version} uploaded as {name}, but the account has no workers.dev name yet, so it has no address. "
                    + "Choose one under Workers & Pages in the Cloudflare dashboard, then deploy again.",
                    "", manifest.Version);

            progress?.Report("Waiting for the site to answer…");
            var seen = await SiteVersionAsync(store.WebUrl, VerifyAttempts, ct);
            var text = seen is null
                ? $"Site {manifest.Version} uploaded to {store.WebUrl}, but it is not answering yet; give it a minute and press Check."
                : Same(seen.Value.Version, manifest.Version)
                    ? $"Site {manifest.Version} is up at {store.WebUrl}."
                    : $"Site uploaded to {store.WebUrl}; it still answers as {seen.Value.Version}, so give it a minute.";
            if (store.WebEveClientId.Length == 0)
                text += $" Sign-in needs an EVE application registered at developers.eveonline.com with callback {store.WebUrl}/auth/callback and no scopes; enter its id and secret here and deploy again.";

            if (store.WebEnabled) sync.Nudge();
            return new Outcome(true, text, store.WebUrl, manifest.Version);
        }
        catch (Exception ex) when (ex is CloudflareException or InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            errorLogger.Log(nameof(CloudflareDeployService), "deploy", ex);
            return new Outcome(false, Plain(ex));
        }
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
            var (version, protocol) = seen.Value;
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
            return new Outcome(true, text, store.WebUrl, version);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return new Outcome(false, Plain(ex));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(string Version, int Protocol)?> SiteVersionAsync(string url, int attempts, CancellationToken ct)
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
                    if (v.Length > 0) return (v, p);
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
