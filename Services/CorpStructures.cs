using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>Whether a row is an Upwell structure, a starbase (POS) control tower or a skyhook.</summary>
public enum CorpStructureKind { Upwell, Tower, Skyhook }

/// <summary>A reagent a skyhook holds, secured and unsecured, as of its last cycle.</summary>
public sealed record CorpReagentInfo(int TypeId, string Name, long Secured, long Unsecured, DateTimeOffset LastCycle);

/// <summary>One of the user's characters' mercenary dens on a skyhook: what its owner can see.</summary>
public sealed record CorpDenOnSkyhook(long DenId, string Owner, string State, long Infomorphs,
    int DevelopmentLevel, long DevelopmentAmount, int AnarchyLevel, long AnarchyAmount, DateTimeOffset? ReinforcedUntil);

/// <summary>One service a structure offers, and whether it is running.</summary>
public sealed record CorpStructureServiceInfo(string Name, string State)
{
    public bool IsOnline => State == "online";
}

/// <summary>
/// One fuel in a bay: what was there at the last reading, how fast it goes and when it runs out.
///
/// <para>Two burns, either of which may be missing. Expected is worked out — from the game data
/// for an Upwell structure's service modules, from the tower's size and kind for a starbase.
/// Measured is what the bay actually lost since it was last topped up (<see cref="CorpFuelBays"/>).
/// The figures below use the expected burn when there is one: it is right from the first reading,
/// where the measured one needs hours to settle.</para>
/// </summary>
public sealed record CorpFuelInfo(
    int TypeId, string Name, long Quantity, DateTimeOffset ReadAt,
    double? ExpectedPerHour, double? MeasuredPerHour, TimeSpan MeasuredOver,
    DateTimeOffset? EsiRunsOut)
{
    public double? PerHour => ExpectedPerHour ?? MeasuredPerHour;

    /// <summary>The burn ESI's fuel expiry implies for this reading — the bay over the hours left
    /// at the time it was read. Only for an Upwell structure's fuel blocks; a check on the
    /// expected figure, which leans on telling which services are running.</summary>
    public double? ImpliedPerHour => EsiRunsOut is { } end && end > ReadAt && Quantity > 0
        ? Quantity / (end - ReadAt).TotalHours : null;

    /// <summary>The bay now, taking off what has burned since the reading.</summary>
    public long OnHandNow(DateTimeOffset now) => PerHour is { } h && now > ReadAt
        ? Math.Max(0, Quantity - (long)Math.Floor(h * (now - ReadAt).TotalHours))
        : Quantity;

    /// <summary>When it runs out: ESI's own fuel expiry where it gives one (the fuel blocks of an
    /// Upwell structure), else the bay's last reading at its burn. Null when nothing burns it.</summary>
    public DateTimeOffset? RunsOut => EsiRunsOut
        ?? (PerHour is > 0 and var h ? ReadAt + TimeSpan.FromHours(Quantity / h) : null);
}

/// <summary>One item fitted to, or held in a working bay of, a structure.</summary>
public sealed record CorpFitInfo(string Band, int Order, int TypeId, string Name, long Quantity, bool ByHand);

/// <summary>One type in a structure's bay: how many, the room they take and what they are worth
/// at the asset valuation's prices (null where there is no price).</summary>
public sealed record CorpBayItem(int TypeId, string Name, long Quantity, double Volume, double? Value);

/// <summary>Modules of one type in a fit band: short of the fit, or fitted in a short one's place.</summary>
public sealed record CorpFitShortfall(string Band, int TypeId, string Name, int Count);

/// <summary>A charge or fighter the structure holds less of than its fit carries.</summary>
public sealed record CorpStockShortfall(int TypeId, string Name, long Have, long Want);

/// <summary>
/// How a structure measures up to the fit set for its type on Corp Structures → Config.
///
/// <para>Only what the fit names is required, wherever it sits: a module in another slot of the
/// same band is the module, and a module or rig the fit does not name is no problem in itself. A
/// fit module the structure lacks is <see cref="Missing"/> while the band has a slot free for it,
/// and <see cref="Wrong"/> once the band is full — something else is where it should be, listed
/// in <see cref="Instead"/>.</para>
/// </summary>
public sealed class CorpFitCheck
{
    public string FitName   { get; init; } = "";
    /// <summary>Where the fit is kept: EVE Console, or the character whose fitting it is.</summary>
    public string FitSource { get; init; } = "";
    /// <summary>The fit could no longer be found; the check used the copy taken <see cref="CopiedAt"/>.</summary>
    public bool   FromCopy  { get; init; }
    public DateTimeOffset CopiedAt { get; init; }
    /// <summary>False when the corporation's assets are not read: nothing could be compared.</summary>
    public bool   Known     { get; init; }
    public int    AmmoPct     { get; init; }
    public int    FightersPct { get; init; }
    public IReadOnlyList<CorpFitShortfall>  Missing     { get; init; } = [];
    public IReadOnlyList<CorpFitShortfall>  Wrong       { get; init; } = [];
    public IReadOnlyList<CorpFitShortfall>  Instead     { get; init; } = [];
    public IReadOnlyList<CorpStockShortfall> AmmoLow     { get; init; } = [];
    public IReadOnlyList<CorpStockShortfall> FightersLow { get; init; } = [];

    public bool HasProblems => Missing.Count + Wrong.Count + AmmoLow.Count + FightersLow.Count > 0;
}

/// <summary>Everything Corp Structures shows about one structure or tower.</summary>
public sealed class CorpStructureInfo
{
    public CorpStructureKind Kind { get; init; }
    public long   Id             { get; init; }
    public string Name           { get; init; } = "";
    public int    TypeId         { get; init; }
    public string TypeName       { get; init; } = "";
    public string GroupName      { get; init; } = "";
    public int    SystemId       { get; init; }
    public string SystemName     { get; init; } = "";
    public double? Security      { get; init; }
    public string RegionName     { get; init; } = "";
    public string Place          { get; init; } = "";   // nearest celestial, or the tower's moon
    public string State          { get; init; } = "";

    public DateTimeOffset? StateTimerStart    { get; init; }
    public DateTimeOffset? StateTimerEnd      { get; init; }
    public DateTimeOffset? UnanchorsAt        { get; init; }
    public DateTimeOffset? FuelExpires        { get; init; }
    public DateTimeOffset? NextReinforceApply { get; init; }
    public int?   ReinforceHour      { get; init; }
    public int?   NextReinforceHour  { get; init; }
    public int?   ProfileId          { get; init; }

    // Towers.
    public DateTimeOffset? OnlinedSince    { get; init; }
    public DateTimeOffset? ReinforcedUntil { get; init; }
    public CorpStarbaseDetail? Settings    { get; init; }
    /// <summary>Hours of reinforcement the tower's strontium holds, at its size's rate.</summary>
    public double? StrontiumHours          { get; init; }

    // Skyhooks.
    /// <summary>Producing workforce, power or reagents.</summary>
    public bool    SkyhookActive      { get; init; }
    /// <summary>The skyhook's workforce now: lowered by a mercenary den attached to it.</summary>
    public long?   Workforce          { get; init; }
    public DateTimeOffset? TheftStart { get; init; }
    public DateTimeOffset? TheftEnd   { get; init; }
    public IReadOnlyList<CorpReagentInfo>  Reagents { get; init; } = [];
    /// <summary>The user's characters' dens on this skyhook. A den of anyone else's is not told.</summary>
    public IReadOnlyList<CorpDenOnSkyhook> Dens     { get; init; } = [];
    /// <summary>When the skyhook's detail was last read; null when it never has been.</summary>
    public DateTimeOffset? ReadAt     { get; init; }
    /// <summary>The anarchy level the den alert is raised at (Settings → Alerts), to mark a den by.</summary>
    public int AnarchyAlertLevel      { get; init; } = 3;

    // Moon drilling.
    public CorpMiningExtraction? Extraction { get; init; }

    public IReadOnlyList<CorpStructureServiceInfo> Services { get; init; } = [];
    public IReadOnlyList<CorpFuelInfo> Fuel  { get; init; } = [];
    public IReadOnlyList<CorpFitInfo>  Fit   { get; init; } = [];
    /// <summary>What a Metenox has drilled and holds: ore by type.</summary>
    public IReadOnlyList<CorpBayItem>  MoonBay { get; init; } = [];
    /// <summary>The moon material bay's size in m³, from the structure type; null for one without.</summary>
    public double? MoonBayCapacity { get; init; }
    /// <summary>True when the corp's assets reach this structure, so an empty fit means empty.</summary>
    public bool FitKnown { get; init; }
    /// <summary>The structure against the fit set for its type; null when none is set.</summary>
    public CorpFitCheck? FitCheck { get; init; }

    /// <summary>Fuel blocks an hour across the bay — what the list's burn column shows.</summary>
    public double? BlocksPerHour { get; init; }

    /// <summary>What the fuel it burns costs a day at the asset valuation's prices: the blocks at
    /// <see cref="BlocksPerHour"/>, and any other fuel with a burn (a Metenox's gas, a tower's
    /// charters, strontium while reinforced). Null where the blocks a day are not known.</summary>
    public double? FuelIskPerDay { get; init; }

    /// <summary>When the structure runs out of whatever it runs out of first. For an Upwell
    /// structure that is ESI's fuel expiry, or a Metenox's gas if that goes sooner; for a tower,
    /// the first of its blocks and charters.</summary>
    public DateTimeOffset? RunsOut { get; init; }

    /// <summary>An online tower or an Upwell structure with fuel — not one in low power or offline.</summary>
    public bool IsPowered { get; init; }

    /// <summary>Has services to run, so no fuel is a problem. An Upwell structure with none fitted
    /// sits in low power by design.</summary>
    public bool NeedsFuel { get; init; }
}

/// <summary>Reads what Corp Structures shows, for one corporation.</summary>
public static class CorpStructures
{
    // Dogma.
    private const int AttrServiceFuelPerHour  = 2109;   // serviceModuleFuelAmount — blocks an hour
    private const int AttrControlTowerSize    = 1031;   // 1 small, 2 medium, 3 large
    private const int AttrMoonMaterialBayCapacity = 5693;   // outputMoonMaterialBayCapacity, m³ (Metenox 500,000)
    private const int GroupFuelBlock          = 1136;
    private const int GroupControlTower       = 365;
    private const int TypeOrbitalSkyhook      = 81080;
    private const int TypeStrontium           = 16275;
    private const int CategoryStarbase        = 23;
    private const int CategoryCharge          = 8;
    private const int CategoryFighter         = 87;
    private const string FuncLocationGroup    = "LocationGroupModifier";

    /// <summary>The slots a structure type has in each band, by dogma attribute.</summary>
    private static readonly (string Band, int Attribute)[] SlotAttributes =
        [("HiSlot", 14), ("MedSlot", 13), ("LoSlot", 12), ("RigSlot", 1137), ("ServiceSlot", 2056)];

    /// <summary>
    /// A tower's fuel blocks an hour, by size: 10, 20, 40. The game data does not carry these —
    /// they live in the SDE's control tower resources, which the import does not read.
    ///
    /// <para>⚠️ Faction towers burn less: 10% less for Angel, Blood, Guristas, Sansha and
    /// Serpentis; 20% less for Dark Blood, Domination, Dread Guristas, Shadow and True Sansha.
    /// Told apart by name, since both tiers share a meta group. Any tower's measured burn shows
    /// beside this, so a rate that has changed would show as the two disagreeing.</para>
    /// </summary>
    internal static double TowerBlocksPerHour(int size, string englishName)
    {
        double full = size switch { 1 => 10, 2 => 20, _ => 40 };
        string[] eliteFactions = ["Dark Blood", "Domination", "Dread Guristas", "Shadow", "True Sansha"];
        string[] factions      = ["Angel", "Blood", "Guristas", "Sansha", "Serpentis"];
        if (eliteFactions.Any(f => englishName.StartsWith(f + " ", StringComparison.Ordinal))) return full * 0.8;
        if (factions.Any(f => englishName.StartsWith(f + " ", StringComparison.Ordinal)))      return full * 0.9;
        return full;
    }

    /// <summary>A tower's strontium an hour while reinforced: 100, 200, 400 by size.</summary>
    internal static double TowerStrontiumPerHour(int size) => size switch { 1 => 100, 2 => 200, _ => 400 };

    /// <summary>
    /// Which of a structure's services a service module provides, by keywords in the service's
    /// name. Used only to tell whether a fitted module is running: a module whose services were
    /// all found offline burns nothing. A module none of whose words match any service is taken
    /// as running while the structure has fuel — the safe side for a fuel estimate.
    /// </summary>
    private static readonly (string Module, string[] Service)[] ServiceWords =
    [
        ("Supercapital Shipyard", ["super"]),
        ("Capital Shipyard",      ["capital"]),
        ("Manufacturing Plant",   ["standard"]),
        ("Invention Lab",         ["invention"]),
        ("Research Lab",          ["research", "copying"]),
        ("Cloning Center",        ["clone"]),
        ("Market Hub",            ["market"]),
        ("Reprocessing Facility", ["reprocessing"]),
        ("Metenox",               ["drill"]),
        ("Moon Drill",            ["drill"]),
        ("Composite Reactor",     ["composite"]),
        ("Hybrid Reactor",        ["hybrid"]),
        ("Biochemical Reactor",   ["biochemical"]),
        ("Conduit Generator",     ["jump", "bridge", "conduit"]),
        ("Cynosural System Jammer", ["jammer"]),
        ("Cynosural Field Generator", ["beacon", "cynosural field"]),
    ];

    private static bool ModuleRunning(string englishModuleName, IReadOnlyList<CorpStructureServiceInfo> services)
    {
        var words = ServiceWords.FirstOrDefault(w =>
            englishModuleName.Contains(w.Module, StringComparison.OrdinalIgnoreCase)).Service;
        if (words is null) return true;

        // "capital" also matches "Super Capitals": a capital shipyard is judged on the services
        // that name capitals and not super capitals.
        var mine = services.Where(s =>
                words.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))
                && !(words.Contains("capital") && s.Name.Contains("super", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return mine.Count == 0 || mine.Any(s => s.IsOnline);
    }

    public static async Task<List<CorpStructureInfo>> LoadAsync(
        IDbContextFactory<AppDbContext> dbFactory, long corpId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var structures = await db.EsiCorpStructures.AsNoTracking()
            .Where(s => s.CorporationId == corpId).ToListAsync(ct);
        var towers = await db.EsiCorpStarbases.AsNoTracking()
            .Where(s => s.CorporationId == corpId).ToListAsync(ct);

        var structureIds = structures.Select(s => s.StructureId).ToList();
        var towerIds     = towers.Select(t => t.StarbaseId).ToList();
        var allIds       = structureIds.Concat(towerIds).ToList();

        var services = (await db.EsiCorpStructureServices.AsNoTracking()
                .Where(s => s.CorporationId == corpId).ToListAsync(ct))
            .GroupBy(s => s.StructureId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CorpStructureServiceInfo>)
                g.OrderBy(s => s.Name).Select(s => new CorpStructureServiceInfo(s.Name, s.State)).ToList());

        var bays = (await db.EsiCorpFuelBays.AsNoTracking()
                .Where(b => b.CorporationId == corpId).ToListAsync(ct))
            .GroupBy(b => b.LocationId).ToDictionary(g => g.Key, g => g.ToList());

        var details = await db.EsiCorpStarbaseDetails.AsNoTracking()
            .Where(d => d.CorporationId == corpId).ToDictionaryAsync(d => d.StarbaseId, ct);

        // Everything sitting directly in the structures: fit, core, fighters, the moon bay.
        var contents = (await db.EsiAssets.AsNoTracking()
                .Where(a => a.OwnerId == corpId && a.OwnerType == "corporation"
                         && structureIds.Contains(a.LocationId))
                .Select(a => new { a.LocationId, a.LocationFlag, a.TypeId, a.Quantity })
                .ToListAsync(ct))
            .GroupBy(a => a.LocationId).ToDictionary(g => g.Key, g => g.ToList());
        var corpHasAssets = await db.EsiAssets.AsNoTracking()
            .AnyAsync(a => a.OwnerId == corpId && a.OwnerType == "corporation", ct);

        var byHand = (await db.StructureFittings.AsNoTracking()
                .Where(f => structureIds.Contains(f.StructureId)).ToListAsync(ct))
            .GroupBy(f => f.StructureId).ToDictionary(g => g.Key, g => g.ToList());

        // A tower's modules are anchored in space beside it, listed in the corp's assets under
        // the solar system. They can be put to a tower only where it is the corp's one tower in
        // that system: assets give no position to tell two neighbours' modules apart.
        var towerSystems = towers.Select(t => t.SystemId).Distinct().ToList();
        var anchored = (await db.EsiAssets.AsNoTracking()
                .Where(a => a.OwnerId == corpId && a.OwnerType == "corporation"
                         && a.LocationType == "solar_system"
                         && towerSystems.Contains((int)a.LocationId))
                .Join(db.SdeTypes, a => a.TypeId, t => t.TypeId, (a, t) => new { a.ItemId, a.LocationId, a.TypeId, a.Quantity, t.GroupId })
                .Join(db.SdeGroups, a => a.GroupId, g => g.GroupId, (a, g) => new { a.ItemId, a.LocationId, a.TypeId, a.Quantity, a.GroupId, g.CategoryId })
                .Where(a => a.CategoryId == CategoryStarbase && a.GroupId != GroupControlTower)
                .ToListAsync(ct))
            .GroupBy(a => a.LocationId).ToDictionary(g => g.Key, g => g.ToList());

        var extractions = (await db.EsiCorpMiningExtractions.AsNoTracking()
                .Where(e => e.CorporationId == corpId).ToListAsync(ct))
            .GroupBy(e => e.StructureId).ToDictionary(g => g.Key, g => g.OrderBy(e => e.ChunkArrivalTime).Last());

        var places = await db.EsiStructureNames.AsNoTracking()
            .Where(n => structureIds.Contains(n.StructureId))
            .ToDictionaryAsync(n => n.StructureId, n => n.NearestCelestial, ct);
        var moonIds = towers.Select(t => t.MoonId).Distinct().ToList();
        var moons = await db.SdeCelestials.AsNoTracking()
            .Where(c => moonIds.Contains(c.ItemId))
            .ToDictionaryAsync(c => c.ItemId, c => c.Name, ct);

        // Skyhooks: their planets, reagents, and the user's characters' dens on them.
        var skyhooks = await db.EsiCorpSkyhooks.AsNoTracking()
            .Where(s => s.CorporationId == corpId).ToListAsync(ct);
        var skyhookIds = skyhooks.Select(s => s.SkyhookId).ToList();
        var reagents = (await db.EsiCorpSkyhookReagents.AsNoTracking()
                .Where(x => x.CorporationId == corpId).ToListAsync(ct))
            .GroupBy(x => x.SkyhookId).ToDictionary(g => g.Key, g => g.ToList());
        var planetIds = skyhooks.Select(s => (long)s.PlanetId).Distinct().ToList();
        var planets = await db.SdeCelestials.AsNoTracking()
            .Where(c => planetIds.Contains(c.ItemId))
            .Select(c => new { c.ItemId, c.Name, c.SolarSystemId })
            .ToDictionaryAsync(c => c.ItemId, ct);
        var densOn = (await db.EsiMercenaryDens.AsNoTracking()
                .Where(d => skyhookIds.Contains(d.SkyhookId)).ToListAsync(ct))
            .GroupBy(d => d.SkyhookId).ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<long, string> owners = densOn.Count == 0 ? [] : await db.Characters.AsNoTracking()
            .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        // The fit each structure type should carry, and how far below it ammo and fighters may run.
        var required = await CorpStructureFits.RequiredAsync(dbFactory, corpId, ct);
        var alertSettings = await db.AlertSettings.AsNoTracking().FirstOrDefaultAsync(a => a.Id == 1, ct);
        var ammoPct     = alertSettings?.StructureAmmoPct ?? 50;
        var fightersPct = alertSettings?.StructureFightersPct ?? 50;

        // Names of every type that shows up.
        var typeIds = structures.Select(s => s.TypeId)
            .Concat(towers.Select(t => t.TypeId))
            .Concat(contents.Values.SelectMany(l => l.Select(a => a.TypeId)))
            .Concat(bays.Values.SelectMany(l => l.Select(b => b.TypeId)))
            .Concat(byHand.Values.SelectMany(l => l.Select(f => f.TypeId)))
            .Concat(anchored.Values.SelectMany(l => l.Select(a => a.TypeId)))
            .Concat(required.Values.SelectMany(f => f.Items.Select(i => i.TypeId)))
            .Concat(reagents.Values.SelectMany(l => l.Select(x => x.TypeId)))
            .Append(TypeOrbitalSkyhook)
            .Distinct().ToList();
        var types = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name, t.GroupId, t.Volume })
            .ToDictionaryAsync(t => t.TypeId, ct);
        var groupIds = types.Values.Select(t => t.GroupId).Distinct().ToList();
        var groups = await db.SdeGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.GroupId))
            .Select(g => new { g.GroupId, g.Name, g.CategoryId })
            .ToDictionaryAsync(g => g.GroupId, ct);

        string TypeName(int id) => types.TryGetValue(id, out var t) ? SdeNames.Type(id, t.Name) : $"#{id}";
        string English(int id)  => types.TryGetValue(id, out var t) ? t.Name : "";
        int GroupOf(int id)     => types.TryGetValue(id, out var t) ? t.GroupId : 0;
        int CategoryOf(int id)  => groups.TryGetValue(GroupOf(id), out var g) ? g.CategoryId : 0;
        string GroupName(int id) => types.TryGetValue(id, out var t) && groups.TryGetValue(t.GroupId, out var g)
            ? SdeNames.Group(t.GroupId, g.Name) : "";

        // What a day's fuel costs, at the prices the assets are valued at. Blocks are priced as the
        // kinds in the bay, by how many of each; with none there, as the average of the four kinds —
        // any of them runs a structure, and they trade within a few percent of each other.
        var blockTypeIds = await db.SdeTypes.AsNoTracking()
            .Where(t => t.GroupId == GroupFuelBlock).Select(t => t.TypeId).ToListAsync(ct);
        var prices = await TypeValuation.PricesAsync(db,
            [.. blockTypeIds, .. bays.Values.SelectMany(l => l.Select(b => b.TypeId)),
             .. contents.Values.SelectMany(l => l).Where(a => a.LocationFlag == "MoonMaterialBay").Select(a => a.TypeId)], ct);
        var anchoredTypeIds = structures.Select(s => s.TypeId).Distinct().ToList();
        var moonBaySizes = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrMoonMaterialBayCapacity && anchoredTypeIds.Contains(a.TypeId))
            .ToDictionaryAsync(a => a.TypeId, a => a.Value, ct);
        var blockPrices = blockTypeIds.Where(id => prices.GetValueOrDefault(id) > 0).Select(id => prices[id]).ToList();
        double? anyBlock = blockPrices.Count > 0 ? blockPrices.Average() : null;

        CorpBayItem BayItem(int typeId, long quantity) => new(typeId, TypeName(typeId), quantity,
            quantity * (types.TryGetValue(typeId, out var t) ? t.Volume : 0),
            prices.GetValueOrDefault(typeId) is > 0 and var p ? quantity * p : null);

        double? IskPerDay(double? blocksPerHour, IReadOnlyList<CorpFuelInfo> fuel)
        {
            if (blocksPerHour is not { } bph) return null;
            var inBay = fuel.Where(f => GroupOf(f.TypeId) == GroupFuelBlock && f.Quantity > 0
                                        && prices.GetValueOrDefault(f.TypeId) > 0).ToList();
            var blockPrice = inBay.Count > 0
                ? inBay.Sum(f => f.Quantity * prices[f.TypeId]) / inBay.Sum(f => f.Quantity)
                : anyBlock;
            if (blockPrice is not { } bp) return null;
            return bph * 24 * bp + fuel
                .Where(f => GroupOf(f.TypeId) != GroupFuelBlock && f.PerHour > 0)
                .Sum(f => f.PerHour!.Value * 24 * prices.GetValueOrDefault(f.TypeId));
        }

        // Fuel per hour of each service module, and each structure type's percent off by the
        // module group it bonuses — read from the type's own effects, not a table here.
        var moduleTypeIds = contents.Values.SelectMany(l => l)
            .Where(a => a.LocationFlag.StartsWith("ServiceSlot")).Select(a => a.TypeId)
            .Concat(byHand.Values.SelectMany(l => l).Where(f => f.Band == "Service").Select(f => f.TypeId))
            .Distinct().ToList();
        var fuelPerHour = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrServiceFuelPerHour && moduleTypeIds.Contains(a.TypeId))
            .ToDictionaryAsync(a => a.TypeId, a => a.Value, ct);

        var structureTypeIds = structures.Select(s => s.TypeId).Distinct().ToList();
        var bonuses = await (
                from te in db.SdeTypeDogmaEffects
                join m  in db.SdeDogmaEffectModifiers on te.EffectId equals m.EffectId
                join a  in db.SdeTypeDogmaAttributes  on new { te.TypeId, AttributeId = m.ModifyingAttributeId ?? 0 }
                                                      equals new { a.TypeId, a.AttributeId }
                where structureTypeIds.Contains(te.TypeId)
                   && m.ModifiedAttributeId == AttrServiceFuelPerHour
                   && m.Func == FuncLocationGroup && m.GroupId != null
                select new { te.TypeId, GroupId = m.GroupId!.Value, a.Value })
            .AsNoTracking().ToListAsync(ct);

        var slotAttributeIds = SlotAttributes.Select(s => s.Attribute).ToList();
        var slotCounts = (await db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => structureTypeIds.Contains(a.TypeId) && slotAttributeIds.Contains(a.AttributeId))
                .Select(a => new { a.TypeId, a.AttributeId, a.Value })
                .ToListAsync(ct))
            .ToDictionary(a => (a.TypeId, a.AttributeId), a => (int)a.Value);

        var towerSizes = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrControlTowerSize && towers.Select(t => t.TypeId).Contains(a.TypeId))
            .ToDictionaryAsync(a => a.TypeId, a => (int)a.Value, ct);

        var systemIds = structures.Select(s => s.SystemId).Concat(towers.Select(t => t.SystemId))
            .Concat(planets.Values.Select(p => p.SolarSystemId)).Distinct().ToList();
        var systems = await db.SdeSolarSystems.AsNoTracking()
            .Where(s => systemIds.Contains(s.SolarSystemId))
            .Select(s => new { s.SolarSystemId, s.Name, s.Security, s.RegionId })
            .ToDictionaryAsync(s => s.SolarSystemId, ct);
        var regionIds = systems.Values.Select(s => s.RegionId).Distinct().ToList();
        var regions = await db.SdeRegions.AsNoTracking()
            .Where(r => regionIds.Contains(r.RegionId))
            .ToDictionaryAsync(r => r.RegionId, r => r.Name, ct);

        (string Name, double? Sec, string Region) System(int id) =>
            systems.TryGetValue(id, out var s)
                ? (SdeNames.SolarSystem(id, s.Name), s.Security,
                   regions.TryGetValue(s.RegionId, out var r) ? SdeNames.Region(s.RegionId, r) : "")
                : ($"#{id}", null, "");

        CorpFuelInfo Fuel(CorpFuelBay b, double? expected, DateTimeOffset? esiRunsOut) => new(
            b.TypeId, TypeName(b.TypeId), b.Quantity, b.SeenAt,
            expected, CorpFuelBays.MeasuredPerHour(b), b.SeenAt - b.BaselineAt, esiRunsOut);

        var result = new List<CorpStructureInfo>();

        // ── Upwell structures ────────────────────────────────────────────────
        foreach (var s in structures)
        {
            var svc   = services.GetValueOrDefault(s.StructureId) ?? [];
            var items = contents.GetValueOrDefault(s.StructureId) ?? [];
            var hand  = byHand.GetValueOrDefault(s.StructureId) ?? [];
            var (sysName, sec, region) = System(s.SystemId);

            var fit = new List<CorpFitInfo>();
            foreach (var a in items)
            {
                var (band, order) = BandOf(a.LocationFlag);
                if (band is null) continue;
                fit.Add(new CorpFitInfo(band, order, a.TypeId, TypeName(a.TypeId), a.Quantity, false));
            }
            // Hand-entered modules (Structure Browser) fill only slots the assets leave empty.
            var taken = fit.Select(f => (f.Band, f.Order)).ToHashSet();
            foreach (var f in hand)
            {
                var band = f.Band switch { "High" => "HiSlot", "Mid" => "MedSlot", "Low" => "LoSlot",
                                           "Rig" => "RigSlot", "Service" => "ServiceSlot", _ => null };
                if (band is null || f.TypeId <= 0 || !taken.Add((band, f.SlotIndex))) continue;
                fit.Add(new CorpFitInfo(band, f.SlotIndex, f.TypeId, TypeName(f.TypeId), 1, true));
            }
            // The ammo hold — the assets call it the structure's cargo — by type: missiles, bombs and
            // scripts not loaded. Loaded ones sit in their module's slot, listed with it above.
            fit.AddRange(items.Where(a => a.LocationFlag == "Cargo")
                .GroupBy(a => a.TypeId)
                .Select(g => new CorpFitInfo("Cargo", 0, g.Key, TypeName(g.Key), g.Sum(a => (long)a.Quantity), false)));

            // Fuel blocks an hour: each running service module's rate, less the structure's
            // bonus for that module's group.
            var powered = s.FuelExpires is { } fe && fe > now;
            double? expectedBlocks = null;
            var serviceModules = fit.Where(f => f.Band == "ServiceSlot").ToList();
            if (serviceModules.Count > 0)
            {
                double sum = 0;
                foreach (var m in serviceModules)
                {
                    if (!fuelPerHour.TryGetValue(m.TypeId, out var rate)) continue;
                    if (!ModuleRunning(English(m.TypeId), svc)) continue;
                    foreach (var b in bonuses.Where(b => b.TypeId == s.TypeId && b.GroupId == GroupOf(m.TypeId)))
                        rate *= 1 + b.Value / 100.0;
                    sum += rate;
                }
                // ⚠️ Whole blocks, the TOTAL rounded down — not each module. A clone bay (10 an
                // hour) in a citadel (25% off) is 7.5, and the game's fuel expiry gave exactly 7.0
                // on two Fortizars. A Tatara's three reactors and reprocessing plant are 11.25 ×
                // 3 + 7.5 = 41.25: the expiry on two of them gave 41.0, where rounding each
                // module down gave 40. Measured against every structure of one corporation,
                // 2026-10-28: all fifty agree to the hour.
                expectedBlocks = powered ? Math.Floor(sum + 1e-9) : 0;
            }

            var fuel = (bays.GetValueOrDefault(s.StructureId) ?? [])
                .Select(b => GroupOf(b.TypeId) == GroupFuelBlock
                    ? Fuel(b, expectedBlocks, s.FuelExpires)
                    : Fuel(b, null, null))
                .OrderBy(f => GroupOf(f.TypeId) == GroupFuelBlock ? 0 : 1).ThenBy(f => f.Name)
                .ToList();

            CorpFitCheck? check = null;
            if (required.TryGetValue(s.TypeId, out var req))
            {
                var structureTypeId = s.TypeId;
                check = Check(req, items.Select(a => (a.LocationFlag, a.TypeId, (long)a.Quantity)), corpHasAssets,
                    CategoryOf, TypeName,
                    band => SlotAttributes.Where(x => x.Band == band)
                        .Select(x => slotCounts.TryGetValue((structureTypeId, x.Attribute), out var n) ? n : (int?)null)
                        .FirstOrDefault(),
                    ammoPct, fightersPct);
            }

            var bayBlocks = fuel.Where(f => GroupOf(f.TypeId) == GroupFuelBlock).Sum(f => f.PerHour ?? 0);
            var blocksPerHour = fuel.Any(f => GroupOf(f.TypeId) == GroupFuelBlock) || expectedBlocks is not null
                ? (bayBlocks > 0 ? bayBlocks : expectedBlocks) : null;
            var firstOut = new DateTimeOffset?[] { s.FuelExpires }
                .Concat(fuel.Where(f => GroupOf(f.TypeId) != GroupFuelBlock && f.PerHour > 0).Select(f => f.RunsOut))
                .Where(d => d is not null).Min();

            result.Add(new CorpStructureInfo
            {
                Kind               = CorpStructureKind.Upwell,
                Id                 = s.StructureId,
                Name               = s.Name.Length > 0 ? s.Name : TypeName(s.TypeId),
                TypeId             = s.TypeId,
                TypeName           = TypeName(s.TypeId),
                GroupName          = GroupName(s.TypeId),
                SystemId           = s.SystemId,
                SystemName         = sysName,
                Security           = sec,
                RegionName         = region,
                Place              = places.GetValueOrDefault(s.StructureId) ?? "",
                State              = s.State,
                StateTimerStart    = s.StateTimerStart,
                StateTimerEnd      = s.StateTimerEnd,
                UnanchorsAt        = s.UnanchorsAt,
                FuelExpires        = s.FuelExpires,
                NextReinforceApply = s.NextReinforceApply,
                ReinforceHour      = s.ReinforceHour,
                NextReinforceHour  = s.NextReinforceHour,
                ProfileId          = s.ProfileId,
                Extraction         = extractions.GetValueOrDefault(s.StructureId),
                Services           = svc,
                Fuel               = fuel,
                Fit                = [.. fit.OrderBy(f => BandRank(f.Band)).ThenBy(f => f.Order).ThenBy(f => f.Name)],
                MoonBay            = [.. items.Where(a => a.LocationFlag == "MoonMaterialBay")
                                         .GroupBy(a => a.TypeId)
                                         .Select(g => BayItem(g.Key, g.Sum(a => (long)a.Quantity)))
                                         .OrderByDescending(f => f.Quantity)],
                MoonBayCapacity    = moonBaySizes.TryGetValue(s.TypeId, out var bay) ? bay : null,
                FitKnown           = corpHasAssets,
                FitCheck           = check,
                BlocksPerHour      = blocksPerHour,
                FuelIskPerDay      = IskPerDay(blocksPerHour, fuel),
                RunsOut            = firstOut,
                IsPowered          = powered,
                NeedsFuel          = svc.Count > 0 || serviceModules.Count > 0,
            });
        }

        // ── Starbases ────────────────────────────────────────────────────────
        var towersBySystem = towers.GroupBy(t => t.SystemId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var t in towers)
        {
            var (sysName, sec, region) = System(t.SystemId);
            var size     = towerSizes.GetValueOrDefault(t.TypeId, 3);
            var online   = t.State is "online" or "reinforced" or "onlining";
            var expected = online ? TowerBlocksPerHour(size, English(t.TypeId)) : 0;

            var fuel = new List<CorpFuelInfo>();
            double? strontiumHours = null;
            foreach (var b in bays.GetValueOrDefault(t.StarbaseId) ?? [])
            {
                if (b.TypeId == TypeStrontium)
                {
                    var perHour = TowerStrontiumPerHour(size);
                    strontiumHours = b.Quantity / perHour;
                    fuel.Add(Fuel(b, t.State == "reinforced" ? perHour : 0, null));
                }
                else if (GroupOf(b.TypeId) == GroupFuelBlock) fuel.Add(Fuel(b, expected, null));
                else fuel.Add(Fuel(b, null, null));   // charters: one an hour in high sec, measured
            }
            fuel = [.. fuel.OrderBy(f => GroupOf(f.TypeId) == GroupFuelBlock ? 0 : f.TypeId == TypeStrontium ? 2 : 1)];

            var fit = towersBySystem[t.SystemId] == 1
                ? (anchored.GetValueOrDefault(t.SystemId) ?? [])
                    .GroupBy(a => a.TypeId)
                    .Select(g => new CorpFitInfo(GroupName(g.Key), 0, g.Key, TypeName(g.Key), g.Sum(a => a.Quantity), false))
                    .OrderBy(f => f.Band).ThenBy(f => f.Name).ToList()
                : [];

            var runsOut = fuel.Where(f => f.TypeId != TypeStrontium && f.PerHour > 0)
                .Select(f => f.RunsOut).Where(d => d is not null).Min();
            var blocksPerHour = fuel.Where(f => GroupOf(f.TypeId) == GroupFuelBlock).Select(f => f.PerHour).FirstOrDefault()
                                ?? (online ? expected : null);

            result.Add(new CorpStructureInfo
            {
                Kind            = CorpStructureKind.Tower,
                Id              = t.StarbaseId,
                Name            = moons.TryGetValue(t.MoonId, out var moon) ? moon : TypeName(t.TypeId),
                TypeId          = t.TypeId,
                TypeName        = TypeName(t.TypeId),
                GroupName       = GroupName(t.TypeId),
                SystemId        = t.SystemId,
                SystemName      = sysName,
                Security        = sec,
                RegionName      = region,
                Place           = moons.GetValueOrDefault(t.MoonId) ?? "",
                State           = t.State,
                UnanchorsAt     = t.UnanchorAt,
                OnlinedSince    = t.OnlinedSince,
                ReinforcedUntil = t.ReinforcedUntil,
                Settings        = details.GetValueOrDefault(t.StarbaseId),
                StrontiumHours  = strontiumHours,
                Fuel            = fuel,
                Fit             = fit,
                FitKnown        = corpHasAssets && towersBySystem[t.SystemId] == 1,
                BlocksPerHour   = blocksPerHour,
                FuelIskPerDay   = IskPerDay(blocksPerHour, fuel),
                RunsOut         = runsOut,
                IsPowered       = online,
                NeedsFuel       = online,
            });
        }

        // ── Skyhooks ─────────────────────────────────────────────────────────
        foreach (var s in skyhooks)
        {
            var planet = planets.GetValueOrDefault(s.PlanetId);
            var (sysName, sec, region) = planet is null ? ("", (double?)null, "") : System(planet.SolarSystemId);
            result.Add(new CorpStructureInfo
            {
                Kind            = CorpStructureKind.Skyhook,
                Id              = s.SkyhookId,
                Name            = planet?.Name ?? $"#{s.PlanetId}",
                TypeId          = TypeOrbitalSkyhook,
                TypeName        = TypeName(TypeOrbitalSkyhook),
                GroupName       = GroupName(TypeOrbitalSkyhook),
                SystemId        = planet?.SolarSystemId ?? 0,
                SystemName      = sysName,
                Security        = sec,
                RegionName      = region,
                Place           = planet?.Name ?? "",
                State           = SkyhookState(s.State),
                StateTimerEnd   = s.ReinforcedUntil,
                SkyhookActive   = s.IsActive,
                Workforce       = s.EffectiveWorkforce,
                TheftStart      = s.TheftStart,
                TheftEnd        = s.TheftEnd,
                ReadAt          = s.PulledAt,
                AnarchyAlertLevel = alertSettings?.DenAnarchyLevel ?? 3,
                IsPowered       = s.IsActive,
                Reagents        = [.. (reagents.GetValueOrDefault(s.SkyhookId) ?? [])
                                      .Select(x => new CorpReagentInfo(x.TypeId, TypeName(x.TypeId), x.SecuredStock, x.UnsecuredStock, x.LastCycle))
                                      .OrderBy(x => x.Name)],
                Dens            = [.. (densOn.GetValueOrDefault(s.SkyhookId) ?? [])
                                      .Select(d => new CorpDenOnSkyhook(d.DenId, owners.GetValueOrDefault(d.CharacterId) ?? $"#{d.CharacterId}",
                                          d.State, d.Infomorphs, d.DevelopmentLevel, d.DevelopmentAmount, d.AnarchyLevel, d.AnarchyAmount,
                                          d.ReinforcedUntil))
                                      .OrderByDescending(d => d.AnarchyAmount)],
            });
        }

        return result;
    }

    /// <summary>
    /// A structure's contents against the fit set for its type: see <see cref="CorpFitCheck"/>.
    /// Modules are compared by type, band by band; charges and fighters by how many of each the
    /// structure holds anywhere in it (loaded, in the cargo, in a tube or the bay) against the
    /// fit's count.
    /// </summary>
    /// <param name="items">Everything sitting directly in the structure: flag, type, quantity.</param>
    /// <param name="slotsIn">The structure type's slots in a band, or null when not known — then
    /// every module short of the fit counts as missing.</param>
    internal static CorpFitCheck Check(
        CorpRequiredFit fit, IEnumerable<(string Flag, int TypeId, long Quantity)> items, bool known,
        Func<int, int> categoryOf, Func<int, string> nameOf, Func<string, int?> slotsIn, int ammoPct, int fightersPct)
    {
        var source = fit.Source == CorpFitChoice.App ? CorpText.CsFitInApp : fit.OwnerName;
        if (!known)
            return new CorpFitCheck { FitName = fit.Name, FitSource = source, FromCopy = fit.FromCopy, CopiedAt = fit.CopiedAt,
                                      AmmoPct = ammoPct, FightersPct = fightersPct };

        static string? Band(string flag) => BandOf(flag).Band is "HiSlot" or "MedSlot" or "LoSlot" or "RigSlot" or "ServiceSlot"
            ? BandOf(flag).Band : null;

        var have = items.ToList();
        var needModules = fit.Items
            .Where(i => Band(i.Flag) is not null && categoryOf(i.TypeId) != CategoryCharge)
            .GroupBy(i => (Band: Band(i.Flag)!, i.TypeId))
            .ToDictionary(g => g.Key, g => g.Sum(i => Math.Max(1, i.Quantity)));
        var haveModules = have
            .Where(i => Band(i.Flag) is not null && categoryOf(i.TypeId) != CategoryCharge)
            .GroupBy(i => (Band: Band(i.Flag)!, i.TypeId))
            .ToDictionary(g => g.Key, g => (int)g.Sum(i => Math.Max(1, i.Quantity)));

        List<CorpFitShortfall> missing = [], wrong = [], instead = [];
        foreach (var band in SlotAttributes.Select(s => s.Band))
        {
            var shorts = needModules
                .Where(n => n.Key.Band == band && haveModules.GetValueOrDefault(n.Key) < n.Value)
                .Select(n => (n.Key.TypeId, Count: n.Value - haveModules.GetValueOrDefault(n.Key)))
                .OrderBy(n => nameOf(n.TypeId), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (shorts.Count == 0) continue;

            // As many as the band has free slots for are missing; the rest have something else
            // where they should be.
            var fitted = haveModules.Where(h => h.Key.Band == band).Sum(h => h.Value);
            var free   = slotsIn(band) is { } slots ? Math.Max(0, slots - fitted) : int.MaxValue;
            foreach (var (typeId, count) in shorts)
            {
                var m = Math.Min(count, free);
                free -= m;
                if (m > 0)         missing.Add(new CorpFitShortfall(band, typeId, nameOf(typeId), m));
                if (count - m > 0) wrong.Add(new CorpFitShortfall(band, typeId, nameOf(typeId), count - m));
            }
            if (wrong.Any(w => w.Band == band))
                instead.AddRange(haveModules
                    .Where(h => h.Key.Band == band && h.Value > needModules.GetValueOrDefault(h.Key))
                    .Select(h => new CorpFitShortfall(band, h.Key.TypeId, nameOf(h.Key.TypeId), h.Value - needModules.GetValueOrDefault(h.Key)))
                    .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase));
        }

        List<CorpStockShortfall> Low(int category, Func<string, bool> where, int pct) =>
        [
            .. fit.Items.Where(i => categoryOf(i.TypeId) == category)
                .GroupBy(i => i.TypeId)
                .Select(g => (TypeId: g.Key, Want: (long)g.Sum(i => i.Quantity),
                              Have: have.Where(h => h.TypeId == g.Key && where(h.Flag)).Sum(h => h.Quantity)))
                .Where(x => x.Want > 0 && x.Have * 100 < x.Want * pct)
                .Select(x => new CorpStockShortfall(x.TypeId, nameOf(x.TypeId), x.Have, x.Want))
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
        ];

        return new CorpFitCheck
        {
            FitName = fit.Name, FitSource = source, FromCopy = fit.FromCopy, CopiedAt = fit.CopiedAt, Known = true,
            AmmoPct = ammoPct, FightersPct = fightersPct,
            Missing = missing, Wrong = wrong, Instead = instead,
            AmmoLow     = Low(CategoryCharge, _ => true, ammoPct),
            FightersLow = Low(CategoryFighter, f => f == "FighterBay" || f.StartsWith("FighterTube", StringComparison.Ordinal), fightersPct),
        };
    }

    /// <summary>The band a location flag belongs to, and the slot's index within it. Null for a
    /// flag that is not part of what Corp Structures calls the fit (hangars, cargo, offices).</summary>
    internal static (string? Band, int Order) BandOf(string flag)
    {
        foreach (var prefix in new[] { "HiSlot", "MedSlot", "LoSlot", "RigSlot", "ServiceSlot", "FighterTube" })
            if (flag.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(flag.AsSpan(prefix.Length), out var i))
                return (prefix, i);
        return flag switch
        {
            "QuantumCoreRoom" => ("QuantumCoreRoom", 0),
            "FighterBay"      => ("FighterBay", 0),
            _                 => (null, 0),
        };
    }

    /// <summary>A skyhook's state as the structure list spells an Upwell structure's, so both read
    /// the same: "ShieldVulnerable" is "shield_vulnerable". Empty until the detail is read.</summary>
    internal static string SkyhookState(string state) => state switch
    {
        "ShieldVulnerable" => "shield_vulnerable",
        "ArmorReinforced"  => "armor_reinforce",
        "ArmorVulnerable"  => "armor_vulnerable",
        "HullReinforced"   => "hull_reinforce",
        "HullVulnerable"   => "hull_vulnerable",
        _                  => state,
    };

    internal static int BandRank(string band) => band switch
    {
        "HiSlot"          => 0,
        "MedSlot"         => 1,
        "LoSlot"          => 2,
        "RigSlot"         => 3,
        "ServiceSlot"     => 4,
        "QuantumCoreRoom" => 5,
        "FighterTube"     => 6,
        "FighterBay"      => 7,
        "Cargo"           => 8,
        _                 => 9,
    };
}
