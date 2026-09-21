using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EveConsole.Services.WebStore;

/// <summary>Cloudflare said no, in its own words.</summary>
public sealed class CloudflareException(string message) : Exception(message);

/// <summary>
/// The little of Cloudflare's API that putting a store site up needs: whether a token works and
/// which accounts it reaches, a D1 database by name, a Worker upload, and the free workers.dev
/// address.
///
/// <para>⚠️ The token travels in the Authorization header of each call and nowhere else — never
/// in a log line, an error message or a URL. Errors quote Cloudflare's own message, which does not
/// contain it either.</para>
///
/// <para>The client is handed the base address (<c>https://api.cloudflare.com/client/v4/</c> in
/// the app, a fake in a harness), so paths here are relative and start without a slash.</para>
/// </summary>
public sealed class CloudflareClient(HttpClient http)
{
    public sealed record Account(string Id, string Name);
    public sealed record Database(string Id, string Name);

    /// <summary>Null when the token is active; otherwise why it is not, for the screen.</summary>
    public async Task<string?> VerifyTokenAsync(string token, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Get, "user/tokens/verify", null, ct, throwOnFailure: false);
        if (!Succeeded(doc)) return FirstError(doc) ?? "Cloudflare refused the token.";
        var status = doc.RootElement.GetProperty("result").TryGetProperty("status", out var s) ? s.GetString() : null;
        return status == "active" ? null : $"The token is {status ?? "not active"}.";
    }

    /// <summary>The accounts the token can act on. A token made for one account lists that one.</summary>
    public async Task<IReadOnlyList<Account>> AccountsAsync(string token, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Get, "accounts?per_page=50", null, ct);
        var list = new List<Account>();
        foreach (var a in doc.RootElement.GetProperty("result").EnumerateArray())
            list.Add(new Account(a.GetProperty("id").GetString() ?? "", a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""));
        return list;
    }

    /// <summary>The account's workers.dev name, or null when none has been chosen yet.</summary>
    public async Task<string?> SubdomainAsync(string token, string accountId, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Get, $"accounts/{accountId}/workers/subdomain", null, ct, throwOnFailure: false);
        if (!Succeeded(doc)) return null;
        var result = doc.RootElement.GetProperty("result");
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("subdomain", out var sub)) return null;
        var s = sub.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Chooses the account's workers.dev name. Refused when the name is taken or malformed.</summary>
    public async Task CreateSubdomainAsync(string token, string accountId, string subdomain, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Put, $"accounts/{accountId}/workers/subdomain",
            new StringContent(JsonSerializer.Serialize(new { subdomain }), Encoding.UTF8, "application/json"), ct);
    }

    /// <summary>Sets one secret on a Worker without a new upload — what <c>wrangler secret put</c> does.</summary>
    public async Task PutSecretAsync(string token, string accountId, string scriptName, string name, string text, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Put, $"accounts/{accountId}/workers/scripts/{scriptName}/secrets",
            new StringContent(JsonSerializer.Serialize(new { name, text, type = "secret_text" }), Encoding.UTF8, "application/json"), ct);
    }

    public async Task<Database?> FindDatabaseAsync(string token, string accountId, string name, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Get, $"accounts/{accountId}/d1/database?name={Uri.EscapeDataString(name)}", null, ct);
        foreach (var d in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var n = d.TryGetProperty("name", out var nn) ? nn.GetString() : null;
            if (n == name) return new Database(d.GetProperty("uuid").GetString() ?? "", n);
        }
        return null;
    }

    public async Task<Database> CreateDatabaseAsync(string token, string accountId, string name, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Post, $"accounts/{accountId}/d1/database",
            new StringContent(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json"), ct);
        var r = doc.RootElement.GetProperty("result");
        return new Database(r.GetProperty("uuid").GetString() ?? "", r.TryGetProperty("name", out var n) ? n.GetString() ?? name : name);
    }

    /// <summary>
    /// Uploads a module Worker: the metadata part says what to bind and how to run it, the module
    /// part is the script itself, named as the metadata's main module.
    /// </summary>
    public async Task UploadScriptAsync(string token, string accountId, string scriptName, string metadataJson, byte[] module, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var metadata = new StringContent(metadataJson, Encoding.UTF8, "application/json");
        form.Add(metadata, "metadata");
        var script = new ByteArrayContent(module);
        script.Headers.ContentType = new MediaTypeHeaderValue("application/javascript+module");
        form.Add(script, "index.js", "index.js");
        using var doc = await CallAsync(token, HttpMethod.Put, $"accounts/{accountId}/workers/scripts/{scriptName}", form, ct);
    }

    /// <summary>Puts the Worker on the account's workers.dev address.</summary>
    public Task EnableWorkersDevAsync(string token, string accountId, string scriptName, CancellationToken ct)
        => SetWorkersDevAsync(token, accountId, scriptName, enabled: true, ct);

    /// <summary>Puts the Worker on, or takes it off, the account's workers.dev address.</summary>
    public async Task SetWorkersDevAsync(string token, string accountId, string scriptName, bool enabled, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Post, $"accounts/{accountId}/workers/scripts/{scriptName}/subdomain",
            new StringContent(JsonSerializer.Serialize(new { enabled, previews_enabled = false }), Encoding.UTF8, "application/json"), ct);
    }

    // ── A domain of the owner's own ───────────────────────────────────────────

    public sealed record Zone(string Id, string Name);
    public sealed record WorkerDomain(string Id, string Hostname, string Service);

    /// <summary>The account's zone of exactly that name, or null.</summary>
    public async Task<Zone?> FindZoneAsync(string token, string accountId, string name, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Get,
            $"zones?name={Uri.EscapeDataString(name)}&account.id={Uri.EscapeDataString(accountId)}", null, ct);
        foreach (var z in doc.RootElement.GetProperty("result").EnumerateArray())
            return new Zone(z.GetProperty("id").GetString() ?? "", z.GetProperty("name").GetString() ?? "");
        return null;
    }

    /// <summary>Attaches the Worker to a hostname in one of the account's zones. Cloudflare makes
    /// the DNS record and the certificate itself; every path of the name goes to the Worker.</summary>
    public async Task AttachDomainAsync(string token, string accountId, string zoneId, string hostname, string scriptName, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Put, $"accounts/{accountId}/workers/domains",
            new StringContent(JsonSerializer.Serialize(new { zone_id = zoneId, hostname, service = scriptName, environment = "production" }),
                              Encoding.UTF8, "application/json"), ct);
    }

    /// <summary>The Worker domains on the account for a hostname.</summary>
    public async Task<IReadOnlyList<WorkerDomain>> DomainsAsync(string token, string accountId, string hostname, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Get, $"accounts/{accountId}/workers/domains?hostname={Uri.EscapeDataString(hostname)}", null, ct);
        var list = new List<WorkerDomain>();
        foreach (var d in doc.RootElement.GetProperty("result").EnumerateArray())
            list.Add(new WorkerDomain(d.GetProperty("id").GetString() ?? "", d.GetProperty("hostname").GetString() ?? "",
                                      d.TryGetProperty("service", out var s) ? s.GetString() ?? "" : ""));
        return list;
    }

    /// <summary>Lets a domain go; Cloudflare removes the record it made.</summary>
    public async Task DetachDomainAsync(string token, string accountId, string domainId, CancellationToken ct)
    {
        using var doc = await CallAsync(token, HttpMethod.Delete, $"accounts/{accountId}/workers/domains/{domainId}", null, ct, throwOnFailure: false);
    }

    // ── The envelope every call comes back in ─────────────────────────────────

    private async Task<JsonDocument> CallAsync(string token, HttpMethod method, string path, HttpContent? body, CancellationToken ct, bool throwOnFailure = true)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        JsonDocument doc;
        try { doc = JsonDocument.Parse(text.Length > 0 ? text : "{}"); }
        catch (JsonException)
        {
            throw new CloudflareException($"Cloudflare answered {(int)response.StatusCode} {response.ReasonPhrase} with something other than JSON.");
        }

        if (throwOnFailure && !Succeeded(doc))
        {
            var why = FirstError(doc) ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
            doc.Dispose();
            throw new CloudflareException($"Cloudflare refused {method} {path.Split('?')[0]}: {why}");
        }
        return doc;
    }

    private static bool Succeeded(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object
        && doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;

    /// <summary>The first error, as "message (code)" — Cloudflare's codes are what its docs index.</summary>
    private static string? FirstError(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("errors", out var errors)) return null;
        if (errors.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in errors.EnumerateArray())
        {
            var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code    = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : 0;
            if (message is { Length: > 0 }) return code > 0 ? $"{message} (code {code})" : message;
        }
        return null;
    }
}
