using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>A mercenary tactical operation open at a den.</summary>
public sealed record MercenaryOperationInfo(string Id, int DungeonTypeId, string State, DateTimeOffset Expires, DateTimeOffset SeenAt);

/// <summary>
/// One of the user's characters' mercenary dens, as Mercenary Dens shows it: where it is and on
/// whose skyhook, its state, infomorphs and how fast they build, development, anarchy, and the
/// operations it offers.
/// </summary>
public sealed class MercenaryDenInfo
{
    public long   DenId          { get; init; }
    public long   CharacterId    { get; init; }
    public string Owner          { get; init; } = "";
    public int    TypeId         { get; init; }
    public string TypeName       { get; init; } = "";
    public int    PlanetId       { get; init; }
    public string Planet         { get; init; } = "";
    public int    SystemId       { get; init; }
    public string SystemName     { get; init; } = "";
    public double? Security      { get; init; }
    public string RegionName     { get; init; } = "";
    public long   SkyhookId      { get; init; }
    public long   SkyhookCorporationId { get; init; }
    public string SkyhookCorporation   { get; init; } = "";
    /// <summary>Running, Paused or Disabled; empty until the detail has been read.</summary>
    public string State          { get; init; } = "";
    public long   Infomorphs     { get; init; }
    /// <summary>Infomorphs gained an hour between the last two readings; null until there are two,
    /// or when the count fell (they were taken out).</summary>
    public double? InfomorphsPerHour { get; init; }
    public int    DevelopmentLevel   { get; init; }
    public long   DevelopmentAmount  { get; init; }
    public int    AnarchyLevel       { get; init; }
    public long   AnarchyAmount      { get; init; }
    public DateTimeOffset? ReinforcedUntil { get; init; }
    public DateTimeOffset? ReadAt    { get; init; }
    public IReadOnlyList<MercenaryOperationInfo> Operations { get; init; } = [];
}

/// <summary>Reads what Mercenary Dens shows: every den of every character with a token.</summary>
public static class MercenaryDens
{
    public static async Task<List<MercenaryDenInfo>> LoadAsync(IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var dens = await db.EsiMercenaryDens.AsNoTracking().ToListAsync(ct);
        if (dens.Count == 0) return [];

        var ops = (await db.EsiMercenaryOperations.AsNoTracking().ToListAsync(ct))
            .GroupBy(o => o.DenId).ToDictionary(g => g.Key, g => g.ToList());
        var owners = await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var planetIds = dens.Select(d => (long)d.PlanetId).Distinct().ToList();
        var planets = await db.SdeCelestials.AsNoTracking()
            .Where(c => planetIds.Contains(c.ItemId))
            .Select(c => new { c.ItemId, c.Name, c.SolarSystemId })
            .ToDictionaryAsync(c => c.ItemId, ct);
        var systemIds = planets.Values.Select(p => p.SolarSystemId).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .Select(s => new { s.SolarSystemId, s.Name, s.Security, s.RegionId })
            .ToDictionaryAsync(s => s.SolarSystemId, ct);
        var regionIds = systems.Values.Select(s => s.RegionId).Distinct().ToList();
        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => SdeNames.Region(r.RegionId, r.Name), ct);

        var typeIds = dens.Select(d => d.TypeId).Distinct().ToList();
        var types = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => SdeNames.Type(t.TypeId, t.Name), ct);

        // The skyhooks' corporations: the user's own by their names, anyone else's by ESI's.
        var corpIds = dens.Select(d => d.SkyhookCorporationId).Where(id => id > 0).Distinct().ToList();
        var corpNames = await db.Corporations.AsNoTracking()
            .Where(c => corpIds.Contains((long)c.Id))
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name, ct);
        foreach (var n in await db.UniverseNames.AsNoTracking()
                     .Where(u => corpIds.Contains(u.EntityId)).ToListAsync(ct))
            corpNames.TryAdd(n.EntityId, n.Name);

        return [.. dens.Select(d =>
        {
            var planet = planets.GetValueOrDefault(d.PlanetId);
            var system = planet is null ? null : systems.GetValueOrDefault(planet.SolarSystemId);
            double? rate = d.PrevInfomorphs is { } prev && d.PrevPulledAt is { } at && d.PulledAt is { } now
                           && now > at && d.Infomorphs >= prev
                ? (d.Infomorphs - prev) / (now - at).TotalHours : null;
            return new MercenaryDenInfo
            {
                DenId              = d.DenId,
                CharacterId        = d.CharacterId,
                Owner              = owners.GetValueOrDefault(d.CharacterId) ?? $"#{d.CharacterId}",
                TypeId             = d.TypeId,
                TypeName           = types.GetValueOrDefault(d.TypeId) ?? "",
                PlanetId           = d.PlanetId,
                Planet             = planet?.Name ?? $"#{d.PlanetId}",
                SystemId           = planet?.SolarSystemId ?? 0,
                SystemName         = system is null ? "" : SdeNames.SolarSystem(system.SolarSystemId, system.Name),
                Security           = system?.Security,
                RegionName         = system is null ? "" : regions.GetValueOrDefault(system.RegionId) ?? "",
                SkyhookId          = d.SkyhookId,
                SkyhookCorporationId = d.SkyhookCorporationId,
                SkyhookCorporation = d.SkyhookCorporationId > 0
                    ? corpNames.GetValueOrDefault(d.SkyhookCorporationId) ?? $"#{d.SkyhookCorporationId}" : "",
                State              = d.State,
                Infomorphs         = d.Infomorphs,
                InfomorphsPerHour  = rate,
                DevelopmentLevel   = d.DevelopmentLevel,
                DevelopmentAmount  = d.DevelopmentAmount,
                AnarchyLevel       = d.AnarchyLevel,
                AnarchyAmount      = d.AnarchyAmount,
                ReinforcedUntil    = d.ReinforcedUntil,
                ReadAt             = d.PulledAt,
                Operations         = [.. (ops.GetValueOrDefault(d.DenId) ?? [])
                                         .Where(o => o.CharacterId == d.CharacterId)
                                         .Select(o => new MercenaryOperationInfo(o.OperationId, o.DungeonTypeId, o.State, o.Expires, o.SeenAt))
                                         .OrderBy(o => o.State is "Available" or "Started" ? 0 : 1)
                                         .ThenBy(o => o.Expires)],
            };
        })];
    }
}
