using System.Net.Http.Json;
using System.Text.Json;

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

    public string StatusText { get; private set; } = "Map stats: idle";

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
        stored += await PollAsync<EsiSovereigntyEntry>(MapDataset.Sovereignty, "sovereignty/map/",
            (b, d) => MapStatsIngest.Sovereignty(b, d), ct);
        stored += await PollAsync<EsiSovStructureEntry>(MapDataset.SovStructures, "sovereignty/structures/",
            (b, d) => MapStatsIngest.SovStructures(b, d), ct);
        stored += await PollAsync<EsiIndustrySystem>(MapDataset.Industry, "industry/systems/",
            (b, d) => MapStatsIngest.Industry(b, d), ct);
        stored += await PollAsync<EsiFwSystem>(MapDataset.FactionWar, "fw/systems/",
            (b, d) => MapStatsIngest.FactionWarfare(b, d), ct);
        stored += await PollAsync<EsiIncursion>(MapDataset.Incursions, "incursions/",
            (b, d) => MapStatsIngest.Incursions(b, d), ct);

        StatusText = stored > 0
            ? $"Map stats: stored {stored:N0} rows at {DateTime.Now:HH:mm}"
            : $"Map stats: up to date at {DateTime.Now:HH:mm}";
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
