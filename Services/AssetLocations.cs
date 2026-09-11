using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Where an asset's root location actually IS — the solar system and region behind a
/// station, structure or in-space id — resolved once, when the assets are written, into
/// <c>EsiAssets.SolarSystemId</c> and <c>RegionId</c>.
///
/// <para>ESI gives an asset's place as an id and a word, and which table can turn that id
/// into a system depends on the word: a station's system is in SdeStations, a structure's in
/// any of three structure tables, and an asset in space carries the system id itself. Every
/// tool that wanted "what do I have in this region" walked that by hand — LogisticsGenerator,
/// InvLevelService and the Asset Browser each carry their own copy — and the agent, handed the
/// same recipe in prose, got it wrong more often than right. Materialising the answer on the
/// row means a filter on a region is a filter on a column, for code and model alike.</para>
///
/// <para>Null means the place could not be resolved rather than "in space": a structure the
/// app has never been allowed to read, or a hangar inside another player's ship. Measured on
/// one account, 13 roots holding 0.2% of the rows. Readers should show those as unknown, not
/// drop them.</para>
///
/// <para>⚠️ Whole tables, not id lists. The stations table is ~5,000 rows of two integers and
/// the structure tables are hundreds; loading them outright is cheaper than shipping a
/// thousand distinct root ids into an IN list, and has no parameter ceiling to trip on.</para>
/// </summary>
public sealed class AssetLocations
{
    private readonly Dictionary<long, int> _systemOfStation;
    private readonly Dictionary<long, int> _systemOfStructure;
    private readonly Dictionary<int, int>  _regionOfSystem;

    private AssetLocations(
        Dictionary<long, int> systemOfStation,
        Dictionary<long, int> systemOfStructure,
        Dictionary<int, int>  regionOfSystem)
    {
        _systemOfStation   = systemOfStation;
        _systemOfStructure = systemOfStructure;
        _regionOfSystem    = regionOfSystem;
    }

    public static async Task<AssetLocations> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var stations = (await db.SdeStations.AsNoTracking()
                .Select(s => new { s.StationId, s.SolarSystemId })
                .ToListAsync(ct))
            .ToDictionary(s => (long)s.StationId, s => s.SolarSystemId);

        // Preferred source first, the rest only fill gaps. Structures is the app's own record
        // and the one place a structure ESI will not show us can be described by hand;
        // EsiStructureNames is what ESI last said; EsiCorpStructures covers a corporation's
        // own structures, which arrive by a different endpoint and may be in neither.
        var structures = new Dictionary<long, int>();
        foreach (var s in await db.Structures.AsNoTracking()
                     .Where(s => s.SolarSystemId != 0)
                     .Select(s => new { s.StructureId, s.SolarSystemId }).ToListAsync(ct))
            structures.TryAdd(s.StructureId, s.SolarSystemId);
        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.SolarSystemId != 0)
                     .Select(s => new { s.StructureId, s.SolarSystemId }).ToListAsync(ct))
            structures.TryAdd(s.StructureId, s.SolarSystemId);
        foreach (var s in await db.EsiCorpStructures.AsNoTracking()
                     .Where(s => s.SystemId != 0)
                     .Select(s => new { s.StructureId, s.SystemId }).ToListAsync(ct))
            structures.TryAdd(s.StructureId, s.SystemId);

        var regions = (await db.SdeSolarSystems.AsNoTracking()
                .Select(s => new { s.SolarSystemId, s.RegionId })
                .ToListAsync(ct))
            .ToDictionary(s => s.SolarSystemId, s => s.RegionId);

        return new AssetLocations(stations, structures, regions);
    }

    /// <summary>
    /// The system and region for a root as <c>ComputeRootLocations</c> typed it. An in-space
    /// root is its own system whether or not the SDE has been imported — the id came from
    /// ESI and is right; only the region needs the SDE.
    /// </summary>
    public (int? SolarSystemId, int? RegionId) Resolve(long rootId, string rootType)
    {
        int? system = rootType switch
        {
            "station"      => _systemOfStation.TryGetValue(rootId, out var st) ? st : null,
            "other"        => _systemOfStructure.TryGetValue(rootId, out var su) ? su : null,
            "solar_system" => rootId is > 0 and <= int.MaxValue ? (int)rootId : null,
            _              => null,
        };
        int? region = system is { } sys && _regionOfSystem.TryGetValue(sys, out var r) ? r : null;
        return (system, region);
    }

    /// <summary>
    /// Fills in rows written before their structure was known.
    ///
    /// <para>The asset write and the structure lookup run in that order on purpose — a hangar
    /// full of stock should not wait behind a paced walk of ESI's structure endpoint — so on the
    /// poll that first meets a structure, everything inside it lands with no system. Once the
    /// lookup has run, this catches those rows up. Cheap: one query that finds nothing unless a
    /// root is still null, and after the first poll the only nulls left are the genuinely
    /// unresolvable, which resolve to nothing again and cost one lookup each.</para>
    /// </summary>
    public static async Task BackfillAsync(AppDbContext db, long ownerId, string ownerType, CancellationToken ct)
    {
        var roots = await db.EsiAssets
            .Where(a => a.OwnerId == ownerId && a.OwnerType == ownerType
                     && a.SolarSystemId == null && a.RootLocationType == "other")
            .Select(a => a.RootLocationId)
            .Distinct()
            .ToListAsync(ct);
        if (roots.Count == 0) return;

        var places = await LoadAsync(db, ct);
        foreach (var root in roots)
        {
            var (system, region) = places.Resolve(root, "other");
            if (system is null) continue;

            await db.EsiAssets
                .Where(a => a.OwnerId == ownerId && a.OwnerType == ownerType
                         && a.RootLocationId == root && a.SolarSystemId == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.SolarSystemId, system)
                    .SetProperty(a => a.RegionId,      region), ct);
        }
    }
}
