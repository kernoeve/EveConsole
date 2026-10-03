using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Thera and Turnur's wormhole connections, from EVE-Scout's public list. Read every five
/// minutes — the list says it may be cached that long — into <see cref="EveScoutConnection"/>,
/// replaced whole each time, so every client of a shared database routes by the same list.
///
/// <para>On unless switched off under Settings → Map Data. Switching it off deletes what is
/// stored: a wormhole closes within hours, and a list nobody refreshes would route pilots into
/// holes that are gone.</para>
/// </summary>
public sealed class EveScoutService(
    IHttpClientFactory              httpFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    MapStatsSettings                settings,
    AppErrorLogger?                 errors = null)
{
    public const string BaseUrl = "https://api.eve-scout.com/";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>Whether the list is read at all (Settings → Map Data).</summary>
    public bool Enabled => settings.EveScoutEnabled;

    /// <summary>Raised after the stored list changed — read, or cleared.</summary>
    public event Action? Changed;

    /// <summary>How many connections the last read stored, and when; or the status it was refused with.</summary>
    public int             LastCount  { get; private set; }
    public DateTimeOffset? LastRead   { get; private set; }
    public int?            LastRefused { get; private set; }

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

    /// <summary>The observations list may be cached an hour, and storms sit for many.</summary>
    private static readonly TimeSpan StormEvery = TimeSpan.FromHours(1);
    private DateTimeOffset _stormsDue;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (settings.EveScoutEnabled) await PollOnceAsync(ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errors?.Log("EveScout", "poll", ex); }

            try
            {
                if (settings.EveScoutEnabled && DateTimeOffset.UtcNow >= _stormsDue)
                {
                    await PollStormsAsync(ct);
                    _stormsDue = DateTimeOffset.UtcNow + StormEvery;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { errors?.Log("EveScout", "storms", ex); }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── EVE-Scout's list ─────────────────────────────────────────────────────

    private sealed class SignatureDto
    {
        [JsonPropertyName("id")]               public string          Id             { get; set; } = "";
        [JsonPropertyName("signature_type")]   public string          SignatureType  { get; set; } = "";
        [JsonPropertyName("out_system_id")]    public int             OutSystemId    { get; set; }
        [JsonPropertyName("out_system_name")]  public string          OutSystemName  { get; set; } = "";
        [JsonPropertyName("out_signature")]    public string?         OutSignature   { get; set; }
        [JsonPropertyName("in_system_id")]     public int             InSystemId     { get; set; }
        [JsonPropertyName("in_system_name")]   public string          InSystemName   { get; set; } = "";
        [JsonPropertyName("in_signature")]     public string?         InSignature    { get; set; }
        [JsonPropertyName("in_region_id")]     public int?            InRegionId     { get; set; }
        [JsonPropertyName("in_region_name")]   public string?         InRegionName   { get; set; }
        [JsonPropertyName("in_system_class")]  public string?         InSystemClass  { get; set; }
        [JsonPropertyName("wh_type")]          public string?         WormholeType   { get; set; }
        [JsonPropertyName("max_ship_size")]    public string?         MaxShipSize    { get; set; }
        [JsonPropertyName("expires_at")]       public DateTimeOffset? ExpiresAt      { get; set; }
    }

    /// <summary>Reads the list and replaces what is stored. Returns how many connections there are.</summary>
    public async Task<int> PollOnceAsync(CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient("eve-scout");
        using var resp = await client.GetAsync("v2/public/signatures", ct);
        if (!resp.IsSuccessStatusCode)
        {
            errors?.Log("EveScout", "poll", $"EVE-Scout answered {(int)resp.StatusCode}");
            LastRefused = (int)resp.StatusCode;
            return 0;
        }

        var list = await resp.Content.ReadFromJsonAsync<List<SignatureDto>>(ct) ?? [];
        var now  = DateTimeOffset.UtcNow;
        var rows = list
            .Where(s => s.SignatureType == "wormhole" && s.OutSystemId > 0 && s.InSystemId > 0 && s.Id.Length > 0)
            .DistinctBy(s => s.Id)
            .Select(s => new EveScoutConnection
            {
                Id              = s.Id,
                HubSystemId     = s.OutSystemId,
                HubSystemName   = s.OutSystemName,
                HubSignature    = s.OutSignature ?? "",
                OtherSystemId   = s.InSystemId,
                OtherSystemName = s.InSystemName,
                OtherSignature  = s.InSignature ?? "",
                OtherRegionId   = s.InRegionId,
                OtherRegionName = s.InRegionName ?? "",
                OtherClass      = s.InSystemClass ?? "",
                WormholeType    = s.WormholeType ?? "",
                MaxShipSize     = s.MaxShipSize ?? "",
                ExpiresAt       = s.ExpiresAt,
                ReadAt          = now,
            })
            .ToList();

        // Replaced inside one transaction, so a reader never sees the list empty halfway.
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            // Switched off while the request was out: what came back is not wanted either.
            if (!settings.EveScoutEnabled) return 0;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.EveScoutConnections.RemoveRange(await db.EveScoutConnections.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            db.EveScoutConnections.AddRange(rows);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        (LastCount, LastRead, LastRefused) = (rows.Count, now, null);
        Changed?.Invoke();
        return rows.Count;
    }

    // ── Storms ───────────────────────────────────────────────────────────────

    private sealed class ObservationDto
    {
        [JsonPropertyName("id")]                   public string         Id            { get; set; } = "";
        [JsonPropertyName("created_at")]           public DateTimeOffset CreatedAt     { get; set; }
        [JsonPropertyName("observation_type")]     public string         Type          { get; set; } = "";
        [JsonPropertyName("observation_category")] public string         Category      { get; set; } = "";
        [JsonPropertyName("display_name")]         public string?        DisplayName   { get; set; }
        [JsonPropertyName("hours_in_system")]      public int?           HoursInSystem { get; set; }
        [JsonPropertyName("system_id")]            public int            SystemId      { get; set; }
        [JsonPropertyName("system_name")]          public string?        SystemName    { get; set; }
        [JsonPropertyName("region_id")]            public int?           RegionId      { get; set; }
        [JsonPropertyName("region_name")]          public string?        RegionName    { get; set; }
    }

    /// <summary>Reads the metaliminal storms EVE-Scout lists and replaces what is stored.
    /// Other observations (a sighting of Tom's shuttle) are not storms and are left out.</summary>
    public async Task<int> PollStormsAsync(CancellationToken ct = default)
    {
        var client = httpFactory.CreateClient("eve-scout");
        using var resp = await client.GetAsync("v2/public/observations", ct);
        if (!resp.IsSuccessStatusCode)
        {
            errors?.Log("EveScout", "storms", $"EVE-Scout answered {(int)resp.StatusCode}");
            return 0;
        }
        var list = await resp.Content.ReadFromJsonAsync<List<ObservationDto>>(ct) ?? [];
        var now  = DateTimeOffset.UtcNow;
        var rows = list
            .Where(o => o.Category == "storm" && o.SystemId > 0 && o.Id.Length > 0)
            .DistinctBy(o => o.Id)
            .Select(o => new EveScoutStorm
            {
                Id            = o.Id,
                SystemId      = o.SystemId,
                SystemName    = o.SystemName ?? "",
                RegionId      = o.RegionId,
                RegionName    = o.RegionName ?? "",
                StormType     = o.Type,
                DisplayName   = o.DisplayName ?? o.Type,
                HoursInSystem = o.HoursInSystem ?? 0,
                ReportedAt    = o.CreatedAt,
                ReadAt        = now,
            })
            .ToList();

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            if (!settings.EveScoutEnabled) return 0;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.EveScoutStorms.RemoveRange(await db.EveScoutStorms.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            db.EveScoutStorms.AddRange(rows);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        Changed?.Invoke();
        return rows.Count;
    }

    public async Task<List<EveScoutStorm>> GetStormsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.EveScoutStorms.AsNoTracking().ToListAsync(ct);
    }

    /// <summary>Deletes every stored connection and storm. What switching the poll off does.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.EveScoutConnections.RemoveRange(await db.EveScoutConnections.ToListAsync(ct));
        db.EveScoutStorms.RemoveRange(await db.EveScoutStorms.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        _stormsDue = default;
        (LastCount, LastRead, LastRefused) = (0, null, null);
        Changed?.Invoke();
    }

    /// <summary>The connections still open, by their stated end. Compared here rather than in the
    /// query: SQLite cannot compare a DateTimeOffset in SQL.</summary>
    public async Task<List<EveScoutConnection>> GetOpenAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var all = await db.EveScoutConnections.AsNoTracking().ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return [.. all.Where(c => c.ExpiresAt is null || c.ExpiresAt > now)];
    }

    // ── Ship size ────────────────────────────────────────────────────────────

    /// <summary>EVE-Scout's size words, smallest first: a hole takes its size and everything below.</summary>
    public static readonly string[] Sizes = ["small", "medium", "large", "xlarge", "capital"];

    /// <summary>Whether a hole of <paramref name="holeSize"/> takes a ship of <paramref name="shipSize"/>.
    /// An unknown hole size is taken as the smallest.</summary>
    public static bool Fits(string holeSize, string shipSize) =>
        Math.Max(0, Array.IndexOf(Sizes, holeSize.ToLowerInvariant())) >= Array.IndexOf(Sizes, shipSize);
}
