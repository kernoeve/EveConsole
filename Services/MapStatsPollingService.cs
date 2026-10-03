using System.Net.Http.Json;
using System.Text.Json;
using EveConsole.Localization;

namespace EveConsole.Services;

/// <summary>
/// Keeps the current hour fresh from ESI. Everything older comes from the EVE Ref archive via
/// <see cref="MapStatsBackfillService"/>.
///
/// The bucket a response belongs to is taken from its Last-Modified header, never from the
/// clock. CCP recomputes these endpoints on a fixed hourly boundary and serves the same body
/// until the next one, so polling at 10 past or 50 past yields the same bucket — and a row
/// written here is byte-identical to the one the archive would later supply for that hour.
/// That is what lets the two sources deduplicate against each other, and why this loop does
/// not need to fire at any particular moment.
/// </summary>
public class MapStatsPollingService(
    IHttpClientFactory   httpFactory,
    MapStatsService      stats,
    MapStatsSettings     settings,
    EveServerStatusService? serverStatus = null,
    AppErrorLogger?      errors = null)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Checked more often than hourly so a bucket is picked up soon after it appears,
    /// without hammering: everything already stored is skipped before any request is made.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public string StatusText { get; private set; } = SettingsText.MapStatsPollIdle;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;

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

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Nothing new is published while Tranquility is down, and ESI is unreliable
                // through downtime anyway.
                if (settings.Enabled && serverStatus?.IsOnline != false)
                    await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errors?.Log("MapStats", "poll", ex); }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        var stored = 0;
        stored += await PollAsync<EsiSystemJump>(MapDataset.Jumps, "universe/system_jumps/",
            (b, d) => MapStatsIngest.Jumps(b, d), ct);
        stored += await PollAsync<EsiSystemKill>(MapDataset.Kills, "universe/system_kills/",
            (b, d) => MapStatsIngest.Kills(b, d), ct);
        stored += await PollSovereigntyAsync(ct);
        stored += await PollAsync<EsiIndustrySystem>(MapDataset.Industry, "industry/systems/",
            (b, d) => MapStatsIngest.Industry(b, d), ct);
        stored += await PollAsync<EsiFwSystem>(MapDataset.FactionWar, "fw/systems/",
            (b, d) => MapStatsIngest.FactionWarfare(b, d), ct);
        stored += await PollAsync<EsiIncursion>(MapDataset.Incursions, "incursions/",
            (b, d) => MapStatsIngest.Incursions(b, d), ct);

        StatusText = stored > 0
            ? string.Format(SettingsText.MapStatsPollStored, stored, DateTime.Now)
            : string.Format(SettingsText.MapStatsPollUpToDate, DateTime.Now);
    }

    /// <summary>
    /// The date the sovereignty route is asked at. /sovereignty/systems lives at ESI's root, not
    /// under /latest/, and answers only with a date; the retired /sovereignty/map already answers
    /// 404 at this one.
    /// </summary>
    private const string SovCompatibilityDate = "2026-08-01";

    /// <summary>
    /// Both sovereignty datasets from the one route that replaced /sovereignty/map and
    /// /sovereignty/structures (retired at compatibility date 2026-05-19), stored as the same
    /// rows those gave, so the overlays, the system page and the archive backfill go on as they
    /// were. The archive still publishes the old routes' snapshots; MapStatsIngest keeps the two
    /// sources' rows alike.
    /// </summary>
    private async Task<int> PollSovereigntyAsync(CancellationToken ct)
    {
        try
        {
            var client = httpFactory.CreateClient("esi-public");
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/sovereignty/systems");
            request.Headers.Add("X-Compatibility-Date", SovCompatibilityDate);
            using var resp = await client.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Said, not swallowed: a route that stops answering is how the last one went
                // unnoticed.
                errors?.Log("MapStats", "poll sovereignty", $"sovereignty/systems answered {(int)resp.StatusCode}");
                return 0;
            }

            var modified = resp.Content.Headers.LastModified ?? resp.Headers.Date;
            if (modified is null) return 0;
            var bucket = MapStatsService.BucketOf(modified.Value);

            var needHolders = !await stats.HasBucketAsync(MapDataset.Sovereignty, bucket, ct);
            var needHubs    = !await stats.HasBucketAsync(MapDataset.SovStructures, bucket, ct);
            if (!needHolders && !needHubs) return 0;

            var data = await resp.Content.ReadFromJsonAsync<EveConsole.Api.EsiClient.EsiSovSystems>(Json, ct);
            if (data is null) return 0;

            var n = 0;
            if (needHolders)
                n += Math.Max(await stats.StoreAsync(MapDataset.Sovereignty, bucket, "esi",
                    MapStatsIngest.Sovereignty(bucket, data.SolarSystems), ct), 0);
            if (needHubs)
                n += Math.Max(await stats.StoreAsync(MapDataset.SovStructures, bucket, "esi",
                    MapStatsIngest.SovStructures(bucket, data.SolarSystems), ct), 0);
            return n;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors?.Log("MapStats", "poll sovereignty", ex);
            return 0;
        }
    }

    /// <summary>Fetches one endpoint and stores it under the bucket its Last-Modified names.</summary>
    private async Task<int> PollAsync<T>(
        string dataset, string path, Func<string, List<T>, IEnumerable<object>> map,
        CancellationToken ct) where T : class
    {
        try
        {
            var client = httpFactory.CreateClient("esi-public");
            using var resp = await client.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode) return 0;

            // Falling back to "now" would be wrong rather than merely imprecise: it would key
            // the row to a bucket CCP never published, so the archive could never match it and
            // the same hour would be stored twice under different keys.
            var modified = resp.Content.Headers.LastModified ?? resp.Headers.Date;
            if (modified is null) return 0;

            var bucket = MapStatsService.BucketOf(modified.Value);
            if (await stats.HasBucketAsync(dataset, bucket, ct)) return 0;

            var data = await resp.Content.ReadFromJsonAsync<List<T>>(Json, ct);
            if (data is null) return 0;

            var rows = map(bucket, data).ToList();
            var n = await stats.StoreAsync(dataset, bucket, "esi", rows, ct);
            return Math.Max(n, 0);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors?.Log("MapStats", $"poll {dataset}", ex);
            return 0;
        }
    }
}
