using System.Security.Cryptography;
using System.Text.Json;

namespace EveConsole.Services.WebStore;

/// <summary>
/// The site's releases on GitHub, and the one this app may deploy.
///
/// <para>Each release carries the built Worker (<c>index.js</c>) and a manifest that says what it
/// is: version, the protocol it speaks, its schema version, the SHA-256 of the module, and the
/// compatibility settings and bindings it needs. The app deploys only a module whose hash matches
/// its manifest and whose protocol is the app's own — a newer site speaking a newer protocol waits
/// for the app to catch up rather than being installed and refused on the next sync.</para>
///
/// <para>The client is handed the API's base address (<c>https://api.github.com/</c> in the app, a
/// fake in a harness); asset downloads follow the absolute addresses the API gives.</para>
/// </summary>
public sealed class SiteReleases(HttpClient github)
{
    public const string Repository = "kernoeve/eveconsole-store";
    public static string ReleasesUrl => $"https://github.com/{Repository}/releases";

    public sealed record Manifest(
        string Name, string Version, int Protocol, int SchemaVersion, string Script, string Sha256,
        string? CompatibilityDate, string[]? CompatibilityFlags, ManifestBindings? Bindings);

    public sealed record ManifestBindings(string? D1, Dictionary<string, string>? Vars, string[]? Secrets);

    /// <summary>A release the app may deploy: its module already read and checked against the manifest.</summary>
    public sealed record Release(string Tag, Manifest Manifest, byte[] Module);

    /// <summary>What the newest release is, for the update check.</summary>
    public sealed record Newest(string Tag, string Version, int Protocol);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The newest release that speaks <paramref name="protocol"/>, its module verified.
    ///
    /// <para>Looks back through recent releases when the newest is ahead of this app, so an app
    /// that has not been updated still deploys the last site it can talk to.</para>
    /// </summary>
    public async Task<Release> LatestCompatibleAsync(int protocol, IProgress<string>? progress, CancellationToken ct)
    {
        var entries = await ListAsync(ct);
        if (entries.Count == 0)
            throw new InvalidOperationException($"No release of the site was found at {ReleasesUrl}.");

        int? newest = null;
        foreach (var e in entries)
        {
            var manifest = await ManifestOfAsync(e, ct);
            if (manifest is null) continue;
            newest ??= manifest.Protocol;
            if (manifest.Protocol != protocol) continue;

            progress?.Report($"Downloading site {manifest.Version}…");
            var module = await github.GetByteArrayAsync(e.ModuleUrl, ct);
            var hash   = Convert.ToHexString(SHA256.HashData(module)).ToLowerInvariant();
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The module in release {e.Tag} does not match its manifest (SHA-256 differs). Not deployed.");
            return new Release(e.Tag, manifest, module);
        }

        throw new InvalidOperationException(newest is { } n && n > protocol
            ? $"The site's newest release speaks protocol {n}; this EVE Console speaks {protocol}. Update EVE Console, then deploy."
            : $"No release of the site speaks protocol {protocol}.");
    }

    /// <summary>The newest release, whatever protocol it speaks, or null when there is none.</summary>
    public async Task<Newest?> NewestAsync(CancellationToken ct)
    {
        foreach (var e in await ListAsync(ct))
        {
            var m = await ManifestOfAsync(e, ct);
            if (m is not null) return new Newest(e.Tag, m.Version, m.Protocol);
        }
        return null;
    }

    private sealed record Entry(string Tag, string ManifestUrl, string ModuleUrl);

    private async Task<List<Entry>> ListAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await github.GetStringAsync($"repos/{Repository}/releases?per_page=10", ct));
        var list = new List<Entry>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;

            string? manifest = null, module = null;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == "manifest.json") manifest = url;
                    else if (name == "index.js") module = url;
                }
            if (manifest is null || module is null) continue;   // a release without the bundle is not deployable
            list.Add(new Entry(r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "", manifest, module));
        }
        return list;
    }

    private async Task<Manifest?> ManifestOfAsync(Entry e, CancellationToken ct)
    {
        try
        {
            var m = JsonSerializer.Deserialize<Manifest>(await github.GetStringAsync(e.ManifestUrl, ct), Json);
            return m is { Version.Length: > 0, Sha256.Length: > 0 } ? m : null;
        }
        catch (JsonException) { return null; }
    }
}
