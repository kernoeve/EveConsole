using System.Net.Http.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// A sovereignty campaign: CCP's scheduled contest for a sovereignty structure that has been
/// reinforced — command nodes appear across the constellation at <see cref="Start"/> and the
/// scores move as each side captures them. Names English; the screen puts them in the interface
/// language.
/// </summary>
public sealed record SovCampaign(
    long           CampaignId,
    string         EventType,
    int            SystemId,
    string         SystemName,
    int            RegionId,
    string         RegionName,
    int            ConstellationId,
    long           StructureId,
    long?          DefenderId,
    string         DefenderName,
    double?        DefenderScore,
    double?        AttackersScore,
    DateTimeOffset Start,
    int            Participants)
{
    /// <summary>Started: the command nodes are out.</summary>
    public bool IsRunning(DateTimeOffset now) => Start <= now;
}

/// <summary>
/// The sovereignty campaigns now and coming, from ESI's public /sovereignty/campaigns. Read at
/// most once a minute — the list is small and only matters as it stands — and never stored:
/// a campaign a day old is of no use. The map tool asks while it is on screen.
/// </summary>
public sealed class SovCampaignService(
    IHttpClientFactory              httpFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    CorpActivityService?            names = null,
    AppErrorLogger?                 errors = null)
{
    /// <summary>Lives at ESI's root, like /sovereignty/systems, and is asked at the app's date.</summary>
    private const string Url = "https://esi.evetech.net/sovereignty/campaigns";
    private const string CompatibilityDate = "2026-08-01";
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    private sealed class CampaignDto
    {
        [JsonPropertyName("campaign_id")]      public long            CampaignId      { get; set; }
        [JsonPropertyName("event_type")]       public string          EventType       { get; set; } = "";
        [JsonPropertyName("solar_system_id")]  public int             SystemId        { get; set; }
        [JsonPropertyName("constellation_id")] public int             ConstellationId { get; set; }
        [JsonPropertyName("structure_id")]     public long            StructureId     { get; set; }
        [JsonPropertyName("defender_id")]      public long?           DefenderId      { get; set; }
        [JsonPropertyName("defender_score")]   public double?         DefenderScore   { get; set; }
        [JsonPropertyName("attackers_score")]  public double?         AttackersScore  { get; set; }
        [JsonPropertyName("start_time")]       public DateTimeOffset  StartTime       { get; set; }
        [JsonPropertyName("participants")]     public List<object>?   Participants    { get; set; }
    }

    private IReadOnlyList<SovCampaign> _last = [];
    private DateTimeOffset _readAt;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task _resolving = Task.CompletedTask;

    /// <summary>The campaigns, soonest first. The last good list stands in when ESI fails.</summary>
    public async Task<IReadOnlyList<SovCampaign>> GetAsync(CancellationToken ct = default)
    {
        if (DateTimeOffset.UtcNow - _readAt < Fresh) return _last;
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _readAt < Fresh) return _last;

            var client = httpFactory.CreateClient("esi-public");
            using var request = new HttpRequestMessage(HttpMethod.Get, Url);
            request.Headers.Add("X-Compatibility-Date", CompatibilityDate);
            using var resp = await client.SendAsync(request, ct);
            if (!resp.IsSuccessStatusCode)
            {
                errors?.Log("SovCampaigns", "read", $"sovereignty/campaigns answered {(int)resp.StatusCode}");
                _readAt = DateTimeOffset.UtcNow;   // not again for a minute
                return _last;
            }
            var list = await resp.Content.ReadFromJsonAsync<List<CampaignDto>>(ct) ?? [];

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var systemIds = list.Select(c => c.SystemId).Distinct().ToList();
            var systems = await (from s in db.SdeSolarSystems.AsNoTracking()
                                 join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
                                 where systemIds.Contains(s.SolarSystemId)
                                 select new { s.SolarSystemId, s.Name, s.RegionId, Region = r.Name })
                                .ToDictionaryAsync(s => s.SolarSystemId, ct);
            var defenders = list.Where(c => c.DefenderId is > 0).Select(c => c.DefenderId!.Value).Distinct().ToList();
            var known = await db.UniverseNames.AsNoTracking().Where(u => defenders.Contains(u.EntityId))
                                .ToDictionaryAsync(u => u.EntityId, u => u.Name, ct);
            var missing = defenders.Where(id => !known.ContainsKey(id)).ToList();
            if (missing.Count > 0 && names is not null && _resolving.IsCompleted)
                _resolving = Task.Run(async () =>
                {
                    try { await names.ResolveNamesAsync(missing); }
                    catch { /* shown by id; asked again next time */ }
                });

            _last = [.. list
                .Select(c =>
                {
                    systems.TryGetValue(c.SystemId, out var s);
                    return new SovCampaign(c.CampaignId, c.EventType, c.SystemId, s?.Name ?? "", s?.RegionId ?? 0, s?.Region ?? "",
                        c.ConstellationId, c.StructureId, c.DefenderId,
                        c.DefenderId is { } d ? known.GetValueOrDefault(d, string.Format(Localization.MapText.BridgeAllianceId, d)) : "",
                        c.DefenderScore, c.AttackersScore, c.StartTime, c.Participants?.Count ?? 0);
                })
                .OrderBy(c => c.Start)];
            _readAt = DateTimeOffset.UtcNow;
            return _last;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errors?.Log("SovCampaigns", "read", ex);
            return _last;
        }
        finally { _gate.Release(); }
    }
}
