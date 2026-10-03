using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Pi;

/// <summary>What a pin on a planet is, from its type's SDE group.</summary>
public enum PiPinKind
{
    Unknown,
    CommandCenter,
    /// <summary>An extractor control unit — what "extractor" means on a colony today.</summary>
    ExtractorControlUnit,
    /// <summary>Basic, advanced or high-tech: one group; <see cref="PiProcessorTier"/> tells them apart.</summary>
    Processor,
    Storage,
    Launchpad,
    /// <summary>The pre-ECU extractor pins (group 1026). Never placed on a colony now; kept
    /// because their dogma is where the planet-type resource list comes from.</summary>
    LegacyExtractor,
}

/// <summary>The commodity tiers. P0 is raw; P4 is the top of the chain.</summary>
public enum PiTier { P0 = 0, P1 = 1, P2 = 2, P3 = 3, P4 = 4 }

/// <summary>Which processor a <see cref="PiPinKind.Processor"/> is. All three share one SDE group,
/// so this comes from the schematics each type can run: P1 out = basic, P2 or P3 = advanced,
/// P4 = high-tech.</summary>
public enum PiProcessorTier { None, Basic, Advanced, HighTech }

public sealed record PiPinType(int TypeId, PiPinKind Kind, double Capacity, PiProcessorTier ProcessorTier)
{
    /// <summary>Holds goods a hauler can take: storage, launchpad or command center.</summary>
    public bool IsStorage => Kind is PiPinKind.Storage or PiPinKind.Launchpad or PiPinKind.CommandCenter;
}

public sealed record PiSchematicInput(int TypeId, int Quantity);

public sealed record PiSchematic(
    int SchematicId,
    string Name,
    int CycleSeconds,
    IReadOnlyList<PiSchematicInput> Inputs,
    int OutputTypeId,
    int OutputQuantity);

public sealed record PiCommodity(int TypeId, PiTier Tier, double Volume);

/// <summary>
/// SDE group ids the PI tools key on. Groups rather than type ids: CCP adds a command center or
/// a launchpad per planet type, and a type list would need maintaining where the group does not.
/// </summary>
public static class PiGroups
{
    public const int LegacyExtractor      = 1026;
    public const int CommandCenter        = 1027;
    public const int Processor            = 1028;
    public const int Storage              = 1029;
    public const int Launchpad            = 1030;
    public const int ExtractorControlUnit = 1063;

    // Commodities: categories 42 (raw) and 43 (processed).
    public const int RawSolid    = 1032;
    public const int RawLiquid   = 1033;
    public const int RawOrganic  = 1035;
    public const int TierOne     = 1042;
    public const int TierTwo     = 1034;
    public const int TierThree   = 1040;
    public const int TierFour    = 1041;

    public static readonly int[] PinGroups =
        [LegacyExtractor, CommandCenter, Processor, Storage, Launchpad, ExtractorControlUnit];

    public static readonly int[] CommodityGroups =
        [RawSolid, RawLiquid, RawOrganic, TierOne, TierTwo, TierThree, TierFour];

    public static PiPinKind KindOf(int groupId) => groupId switch
    {
        CommandCenter        => PiPinKind.CommandCenter,
        ExtractorControlUnit => PiPinKind.ExtractorControlUnit,
        Processor            => PiPinKind.Processor,
        Storage              => PiPinKind.Storage,
        Launchpad            => PiPinKind.Launchpad,
        LegacyExtractor      => PiPinKind.LegacyExtractor,
        _                    => PiPinKind.Unknown,
    };

    public static PiTier? TierOf(int groupId) => groupId switch
    {
        RawSolid or RawLiquid or RawOrganic => PiTier.P0,
        TierOne   => PiTier.P1,
        TierTwo   => PiTier.P2,
        TierThree => PiTier.P3,
        TierFour  => PiTier.P4,
        _         => null,
    };
}

/// <summary>Numbers that belong to the tier rather than to any one type.</summary>
public static class PiTiers
{
    /// <summary>
    /// The customs base cost per unit, which the owner's tax rate multiplies: export pays this ×
    /// rate, import half of that. ⚠️ Game rules, not SDE data — CCP's published PI tax table.
    /// </summary>
    public static double BaseCost(PiTier tier) => tier switch
    {
        PiTier.P0 => 5,
        PiTier.P1 => 400,
        PiTier.P2 => 7_200,
        PiTier.P3 => 60_000,
        PiTier.P4 => 1_200_000,
        _         => 0,
    };

    /// <summary>Unit volume, for a type the SDE has not been read for yet. The SDE's own figure
    /// wins wherever there is one.</summary>
    public static double FallbackVolume(PiTier tier) => tier switch
    {
        PiTier.P0 => 0.005,
        PiTier.P1 => 0.19,
        PiTier.P2 => 0.75,
        PiTier.P3 => 3,
        PiTier.P4 => 50,
        _         => 0.01,
    };

    /// <summary>The capacity a storage pin has when the SDE does not say: CCP's published figures.</summary>
    public static double FallbackCapacity(PiPinKind kind) => kind switch
    {
        PiPinKind.CommandCenter => 500,
        PiPinKind.Storage       => 12_000,
        PiPinKind.Launchpad     => 10_000,
        _                       => 0,
    };
}

/// <summary>
/// The command center's CPU and powergrid at each upgrade level.
///
/// <para>⚠️ Not in the SDE: the client computes it, and the SDE carries only each command center
/// type's level-0 (or "limited" level-1) figures. Kept here by hand from the EVE University wiki
/// (wiki.eveuniversity.org/Planetary_buildings, Command Center table, read 2026-10-01). The level-0
/// row matches the SDE's own command centers (cpuOutput 1675, powerOutput 6000), and level 1 the
/// "Limited" command centers (7057, 9000).</para>
/// </summary>
public static class PiCommandCenter
{
    public const int MaxLevel = 5;

    private static readonly (int Cpu, int Power)[] s_levels =
    [
        (1_675,  6_000),
        (7_057,  9_000),
        (12_136, 12_000),
        (17_215, 15_000),
        (21_315, 17_000),
        (25_415, 19_000),
    ];

    /// <summary>CPU (tf) and powergrid (MW) at an upgrade level, clamped to 0–5.</summary>
    public static (int Cpu, int Power) At(int level) => s_levels[Math.Clamp(level, 0, MaxLevel)];
}

/// <summary>
/// Everything the PI engine needs to know from the SDE, read once and handed to it — so the engine
/// itself never touches the database and can be run on constructed data.
/// </summary>
public sealed class PiStaticData
{
    /// <summary>CCP's published defaults for the extractor formula (dogma 1683 / 1687), used when
    /// the SDE has not been read.</summary>
    public const double DefaultDecayFactor = 0.012;
    public const double DefaultNoiseFactor = 0.8;

    public IReadOnlyDictionary<int, PiPinType>    PinTypes    { get; init; } = new Dictionary<int, PiPinType>();
    public IReadOnlyDictionary<int, PiCommodity>  Commodities { get; init; } = new Dictionary<int, PiCommodity>();
    public IReadOnlyDictionary<int, PiSchematic>  Schematics  { get; init; } = new Dictionary<int, PiSchematic>();

    /// <summary>Processor types that can run each schematic.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> PinTypesBySchematic { get; init; }
        = new Dictionary<int, IReadOnlyList<int>>();

    /// <summary>The P0 types each planet type yields, keyed on ESI's <c>planet_type</c> word.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> ResourcesByPlanetType { get; init; }
        = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);

    public double DecayFactor { get; init; } = DefaultDecayFactor;
    public double NoiseFactor { get; init; } = DefaultNoiseFactor;

    public PiPinKind KindOf(int pinTypeId) =>
        PinTypes.TryGetValue(pinTypeId, out var t) ? t.Kind : PiPinKind.Unknown;

    public PiTier? TierOf(int typeId) =>
        Commodities.TryGetValue(typeId, out var c) ? c.Tier : null;

    public double VolumeOf(int typeId) =>
        Commodities.TryGetValue(typeId, out var c)
            ? (c.Volume > 0 ? c.Volume : PiTiers.FallbackVolume(c.Tier))
            : 0.01;

    public double CapacityOf(int pinTypeId)
    {
        if (!PinTypes.TryGetValue(pinTypeId, out var t)) return 0;
        return t.Capacity > 0 ? t.Capacity : PiTiers.FallbackCapacity(t.Kind);
    }
}

/// <summary>Reads <see cref="PiStaticData"/> from the SDE tables.</summary>
public static class PiStaticDataLoader
{
    private const int HarvesterTypeAttr     = 709;   // what a legacy extractor harvests
    private const int PlanetRestrictionAttr = 1632;  // the planet type it may stand on
    private const int DecayFactorAttr       = 1683;  // ecuDecayFactor
    private const int NoiseFactorAttr       = 1687;  // ecuNoiseFactor

    public static async Task<PiStaticData> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var pinRows = await db.SdeTypes.AsNoTracking()
            .Where(t => PiGroups.PinGroups.Contains(t.GroupId))
            .Select(t => new { t.TypeId, t.GroupId, t.Capacity })
            .ToListAsync(ct);

        var commodityRows = await db.SdeTypes.AsNoTracking()
            .Where(t => PiGroups.CommodityGroups.Contains(t.GroupId))
            .Select(t => new { t.TypeId, t.GroupId, t.Volume })
            .ToListAsync(ct);

        var schematicRows = await db.SdePlanetSchematics.AsNoTracking().ToListAsync(ct);
        var schematicTypes = await db.SdePlanetSchematicTypes.AsNoTracking().ToListAsync(ct);
        var schematicPins = await db.SdePlanetSchematicPins.AsNoTracking().ToListAsync(ct);

        var commodities = commodityRows.ToDictionary(
            r => r.TypeId,
            r => new PiCommodity(r.TypeId, PiGroups.TierOf(r.GroupId) ?? PiTier.P0, r.Volume));

        var schematics = new Dictionary<int, PiSchematic>();
        foreach (var s in schematicRows)
        {
            var rows   = schematicTypes.Where(t => t.SchematicId == s.SchematicId).ToList();
            var output = rows.FirstOrDefault(t => !t.IsInput);
            if (output is null) continue;
            schematics[s.SchematicId] = new PiSchematic(
                s.SchematicId, s.Name, s.CycleTime,
                rows.Where(t => t.IsInput).Select(t => new PiSchematicInput(t.TypeId, t.Quantity)).ToList(),
                output.TypeId, output.Quantity);
        }

        var pinsBySchematic = schematicPins
            .GroupBy(p => p.SchematicId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(p => p.PinTypeId).ToList());

        // A processor's tier is the highest product of what it can run. Left None when the
        // schematic pins have not been imported yet; the engine then reads it off the schematic
        // the pin is actually running.
        var tierByPin = new Dictionary<int, PiProcessorTier>();
        foreach (var (schematicId, pinTypes) in pinsBySchematic)
        {
            if (!schematics.TryGetValue(schematicId, out var s)) continue;
            var tier = ProcessorTierFor(commodities.TryGetValue(s.OutputTypeId, out var c) ? c.Tier : null);
            foreach (var pin in pinTypes)
                if (tier > tierByPin.GetValueOrDefault(pin)) tierByPin[pin] = tier;
        }

        var pinTypesById = pinRows.ToDictionary(
            r => r.TypeId,
            r => new PiPinType(r.TypeId, PiGroups.KindOf(r.GroupId), r.Capacity, tierByPin.GetValueOrDefault(r.TypeId)));

        var resources = await db.SdePlanetTypeResources.AsNoTracking().ToListAsync(ct);
        // ⚠️ Derived on the spot when the table is empty — a database upgraded to this build has
        // the table but not yet the import that fills it, and the dogma it is built from is
        // already there.
        if (resources.Count == 0)
            resources = await DerivePlanetTypeResourcesAsync(db, ct);

        var factors = await db.SdeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == DecayFactorAttr || a.AttributeId == NoiseFactorAttr)
            .Select(a => new { a.AttributeId, a.DefaultValue })
            .ToListAsync(ct);
        var decay = factors.FirstOrDefault(f => f.AttributeId == DecayFactorAttr)?.DefaultValue;
        var noise = factors.FirstOrDefault(f => f.AttributeId == NoiseFactorAttr)?.DefaultValue;

        return new PiStaticData
        {
            PinTypes    = pinTypesById,
            Commodities = commodities,
            Schematics  = schematics,
            PinTypesBySchematic = pinsBySchematic,
            ResourcesByPlanetType = resources
                .GroupBy(r => r.PlanetType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(r => r.ResourceTypeId).Distinct().Order().ToList(),
                              StringComparer.OrdinalIgnoreCase),
            DecayFactor = decay is > 0 ? decay.Value : PiStaticData.DefaultDecayFactor,
            NoiseFactor = noise is > 0 ? noise.Value : PiStaticData.DefaultNoiseFactor,
        };
    }

    public static PiProcessorTier ProcessorTierFor(PiTier? outputTier) => outputTier switch
    {
        PiTier.P1             => PiProcessorTier.Basic,
        PiTier.P2 or PiTier.P3 => PiProcessorTier.Advanced,
        PiTier.P4             => PiProcessorTier.HighTech,
        _                     => PiProcessorTier.None,
    };

    /// <summary>
    /// Which raw resources each planet type yields.
    ///
    /// <para>⚠️ The SDE has no table for it. The legacy extractor pins (group 1026) carry it in
    /// their dogma: each pin may stand only on one planet type (1632, planetRestriction) and
    /// harvests one resource (709, harvesterType). Their union per planet type is the list the
    /// game shows in its resource scan.</para>
    ///
    /// <para>The planet type's ESI word comes from its SDE name: "Planet (Barren)" is "barren",
    /// which is what a colony's <c>planet_type</c> says.</para>
    /// </summary>
    public static async Task<List<SdePlanetTypeResource>> DerivePlanetTypeResourcesAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        var extractorIds = await db.SdeTypes.AsNoTracking()
            .Where(t => t.GroupId == PiGroups.LegacyExtractor)
            .Select(t => t.TypeId)
            .ToListAsync(ct);
        if (extractorIds.Count == 0) return [];

        var dogma = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => extractorIds.Contains(a.TypeId)
                     && (a.AttributeId == HarvesterTypeAttr || a.AttributeId == PlanetRestrictionAttr))
            .ToListAsync(ct);

        var pairs = dogma.GroupBy(a => a.TypeId)
            .Select(g => (
                Planet:   (int)(g.FirstOrDefault(a => a.AttributeId == PlanetRestrictionAttr)?.Value ?? 0),
                Resource: (int)(g.FirstOrDefault(a => a.AttributeId == HarvesterTypeAttr)?.Value ?? 0)))
            .Where(p => p.Planet > 0 && p.Resource > 0)
            .Distinct()
            .ToList();
        if (pairs.Count == 0) return [];

        var planetTypeIds = pairs.Select(p => p.Planet).Distinct().ToList();
        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => planetTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        return pairs
            .Select(p => new SdePlanetTypeResource
            {
                PlanetTypeId   = p.Planet,
                ResourceTypeId = p.Resource,
                PlanetType     = EsiPlanetTypeWord(names.GetValueOrDefault(p.Planet, "")),
            })
            .OrderBy(r => r.PlanetTypeId).ThenBy(r => r.ResourceTypeId)
            .ToList();
    }

    /// <summary>"Planet (Barren)" → "barren": the word ESI uses for the planet type.</summary>
    public static string EsiPlanetTypeWord(string sdeName)
    {
        var open  = sdeName.IndexOf('(');
        var close = sdeName.LastIndexOf(')');
        var word  = open >= 0 && close > open ? sdeName[(open + 1)..close] : sdeName;
        return word.Trim().ToLowerInvariant();
    }
}
