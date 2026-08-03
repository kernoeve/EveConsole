using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>Persisted settings for the map statistics pipeline.</summary>
public class MapStatsSettings(AppPreferencesService prefs)
{
    public bool Enabled
    {
        get => prefs.GetBool("mapstats.enabled", true);
        set => _ = prefs.SetBoolAsync("mapstats.enabled", value);
    }

    /// <summary>How much history to pull on the first run.</summary>
    public int BackfillDays
    {
        get => (int)prefs.GetLong("mapstats.backfill_days", 30);
        set => _ = prefs.SetLongAsync("mapstats.backfill_days", value);
    }

    /// <summary>Days of hourly detail to keep before rolling up to daily totals.</summary>
    public int KeepHourlyDays
    {
        get => (int)prefs.GetLong("mapstats.keep_hourly_days", 14);
        set => _ = prefs.SetLongAsync("mapstats.keep_hourly_days", value);
    }

    /// <summary>Set once the first backfill has completed, so it does not re-run every start.</summary>
    public bool InitialBackfillDone
    {
        get => prefs.GetBool("mapstats.initial_done");
        set => _ = prefs.SetBoolAsync("mapstats.initial_done", value);
    }

    public string LastRollUp
    {
        get => prefs.Get("mapstats.last_rollup") ?? "";
        set => _ = prefs.SetAsync("mapstats.last_rollup", value);
    }
}

/// <summary>
/// Fills map statistics from the EVE Ref archive.
///
/// ESI only ever serves the current hour, so any period the app was closed is unrecoverable
/// from ESI. This is what makes the app survivable being shut overnight — and it is why the
/// archive is the backbone of the pipeline rather than a fallback.
/// </summary>
public class MapStatsBackfillService(
    EveRefArchiveClient archive,
    MapStatsService     stats,
    MapStatsSettings    settings,
    AppErrorLogger?     errors = null)
{
    public bool   IsRunning       { get; private set; }
    public int    ProgressCurrent { get; private set; }
    public int    ProgressTotal   { get; private set; }
    public string StatusText      { get; private set; } = "";

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _lifetime;

    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// Kicks off the archive catch-up and the daily rollup, detached. On a first run the
    /// catch-up fetches 30 days across seven datasets, which takes minutes — it must not hold
    /// up application start, and nothing else depends on it having finished.
    /// </summary>
    public void Start()
    {
        if (_lifetime is not null) return;
        _lifetime = new CancellationTokenSource();
        var ct = _lifetime.Token;

        _ = Task.Run(async () =>
        {
            await CatchUpAsync(ct);
            await MaybeRollUpAsync(ct);
        }, ct);
    }

    public void Stop()
    {
        _lifetime?.Cancel();
        _cts?.Cancel();
    }

    /// <summary>
    /// Walks back <paramref name="days"/> days, oldest first, storing any hourly bucket not
    /// already held. Oldest first so an interrupted run leaves a contiguous block rather than
    /// islands, and so the resume point is simply "the oldest gap".
    /// </summary>
    public async Task BackfillAsync(int days, CancellationToken outerCt = default)
    {
        if (IsRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;

        IsRunning       = true;
        ProgressCurrent = 0;
        ProgressTotal   = days * MapDataset.All.Length;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);

            for (var back = days; back >= 0 && !ct.IsCancellationRequested; back--)
            {
                var day = today.AddDays(-back);
                foreach (var dataset in MapDataset.All)
                {
                    if (ct.IsCancellationRequested) break;
                    StatusText = $"{dataset} {day:yyyy-MM-dd}";
                    await BackfillDayAsync(dataset, day, ct);
                    ProgressCurrent++;
                }
            }

            StatusText = ct.IsCancellationRequested ? "Cancelled" : "Complete";
        }
        catch (OperationCanceledException) { StatusText = "Cancelled"; }
        catch (Exception ex)
        {
            errors?.Log("MapStats", "backfill", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Stores every hour of one dataset on one day that is not already held. Hours absent from
    /// the archive are skipped silently: EVE Ref itself has occasional gaps (2026-08-01 has 24
    /// files for system-jumps but 22 for system-kills), so a missing hour is normal.
    /// </summary>
    public async Task<int> BackfillDayAsync(string dataset, DateOnly day, CancellationToken ct = default)
    {
        var files = await archive.ListDayAsync(dataset, day, ct);
        if (files.Count == 0) return 0;

        var from = MapStatsService.BucketOf(files[0].FileTime);
        var to   = MapStatsService.BucketOf(files[^1].FileTime);
        var held = await stats.GetStoredBucketsAsync(dataset, from, to, ct);

        var stored = 0;
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var bucket = MapStatsService.BucketOf(file.FileTime);
            if (held.Contains(bucket)) continue;

            if (await StoreSnapshotAsync(dataset, bucket, file, ct)) stored++;
        }
        return stored;
    }

    /// <summary>Downloads one snapshot and hands the mapped rows to the shared write path.</summary>
    private async Task<bool> StoreSnapshotAsync(
        string dataset, string bucket, ArchiveFile file, CancellationToken ct)
    {
        const string src = "everef";

        switch (dataset)
        {
            case MapDataset.Jumps:
            {
                var d = await archive.GetSnapshotAsync<List<EsiSystemJump>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.Jumps(bucket, d), ct) >= 0;
            }
            case MapDataset.Kills:
            {
                var d = await archive.GetSnapshotAsync<List<EsiSystemKill>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.Kills(bucket, d), ct) >= 0;
            }
            case MapDataset.Sovereignty:
            {
                var d = await archive.GetSnapshotAsync<List<EsiSovereigntyEntry>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.Sovereignty(bucket, d), ct) >= 0;
            }
            case MapDataset.SovStructures:
            {
                var d = await archive.GetSnapshotAsync<List<EsiSovStructureEntry>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.SovStructures(bucket, d), ct) >= 0;
            }
            case MapDataset.Industry:
            {
                var d = await archive.GetSnapshotAsync<List<EsiIndustrySystem>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.Industry(bucket, d), ct) >= 0;
            }
            case MapDataset.FactionWar:
            {
                var d = await archive.GetSnapshotAsync<List<EsiFwSystem>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.FactionWarfare(bucket, d), ct) >= 0;
            }
            case MapDataset.Incursions:
            {
                var d = await archive.GetSnapshotAsync<List<EsiIncursion>>(file, ct);
                return d is not null &&
                       await stats.StoreAsync(dataset, bucket, src, MapStatsIngest.Incursions(bucket, d), ct) >= 0;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Runs at startup: the full backfill on a first run, otherwise just the days since the
    /// app was last open. Cheap when nothing is missing, because every hour already held is
    /// skipped without being downloaded.
    /// </summary>
    public async Task CatchUpAsync(CancellationToken ct = default)
    {
        if (!settings.Enabled) return;

        try
        {
            if (!settings.InitialBackfillDone)
            {
                await BackfillAsync(settings.BackfillDays, ct);
                if (!ct.IsCancellationRequested) settings.InitialBackfillDone = true;
                return;
            }

            // Three days is comfortably more than a normal overnight gap and still only a few
            // index reads when there is nothing to fetch.
            await BackfillAsync(3, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { errors?.Log("MapStats", "catch-up", ex); }
    }

    /// <summary>Rolls hourly rows past the retention window into daily totals, once a day.</summary>
    public async Task MaybeRollUpAsync(CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        if (settings.LastRollUp == today) return;

        try
        {
            await stats.RollUpAsync(settings.KeepHourlyDays, ct);
            settings.LastRollUp = today;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { errors?.Log("MapStats", "rollup", ex); }
    }
}
