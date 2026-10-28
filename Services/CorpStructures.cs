using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>Whether a row is an Upwell structure or a starbase (POS) control tower.</summary>
public enum CorpStructureKind { Upwell, Tower }

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

    // Moon drilling.
    public CorpMiningExtraction? Extraction { get; init; }

    public IReadOnlyList<CorpStructureServiceInfo> Services { get; init; } = [];
    public IReadOnlyList<CorpFuelInfo> Fuel  { get; init; } = [];
    public IReadOnlyList<CorpFitInfo>  Fit   { get; init; } = [];
    /// <summary>What a Metenox has drilled and holds: ore by type.</summary>
    public IReadOnlyList<CorpFitInfo>  MoonBay { get; init; } = [];
    /// <summary>True when the corp's assets reach this structure, so an empty fit means empty.</summary>
    public bool FitKnown { get; init; }

    /// <summary>Fuel blocks an hour across the bay — what the list's burn column shows.</summary>
    public double? BlocksPerHour { get; init; }

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
    private const int GroupFuelBlock          = 1136;
    private const int GroupControlTower       = 365;
    private const int TypeStrontium           = 16275;
    private const int CategoryStarbase        = 23;
    private const string FuncLocationGroup    = "LocationGroupModifier";

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

        // Names of every type that shows up.
        var typeIds = structures.Select(s => s.TypeId)
            .Concat(towers.Select(t => t.TypeId))
            .Concat(contents.Values.SelectMany(l => l.Select(a => a.TypeId)))
            .Concat(bays.Values.SelectMany(l => l.Select(b => b.TypeId)))
            .Concat(byHand.Values.SelectMany(l => l.Select(f => f.TypeId)))
            .Concat(anchored.Values.SelectMany(l => l.Select(a => a.TypeId)))
            .Distinct().ToList();
        var types = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name, t.GroupId })
            .ToDictionaryAsync(t => t.TypeId, ct);
        var groupIds = types.Values.Select(t => t.GroupId).Distinct().ToList();
        var groups = await db.SdeGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.GroupId))
            .ToDictionaryAsync(g => g.GroupId, g => g.Name, ct);

        string TypeName(int id) => types.TryGetValue(id, out var t) ? SdeNames.Type(id, t.Name) : $"#{id}";
        string English(int id)  => types.TryGetValue(id, out var t) ? t.Name : "";
        int GroupOf(int id)     => types.TryGetValue(id, out var t) ? t.GroupId : 0;
        string GroupName(int id) => types.TryGetValue(id, out var t) && groups.TryGetValue(t.GroupId, out var g)
            ? SdeNames.Group(t.GroupId, g) : "";

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

        var towerSizes = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrControlTowerSize && towers.Select(t => t.TypeId).Contains(a.TypeId))
            .ToDictionaryAsync(a => a.TypeId, a => (int)a.Value, ct);

        var systemIds = structures.Select(s => s.SystemId).Concat(towers.Select(t => t.SystemId)).Distinct().ToList();
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
                    // ⚠️ Whole blocks, rounded down. A clone bay (10 an hour) in a citadel (25%
                    // off) works out at 7.5, but the game's own fuel expiry on two of them gave
                    // exactly 7.0 an hour against the blocks in their bays.
                    sum += Math.Floor(rate + 1e-9);
                }
                expectedBlocks = powered ? sum : 0;
            }

            var fuel = (bays.GetValueOrDefault(s.StructureId) ?? [])
                .Select(b => GroupOf(b.TypeId) == GroupFuelBlock
                    ? Fuel(b, expectedBlocks, s.FuelExpires)
                    : Fuel(b, null, null))
                .OrderBy(f => GroupOf(f.TypeId) == GroupFuelBlock ? 0 : 1).ThenBy(f => f.Name)
                .ToList();

            var blocksPerHour = fuel.Where(f => GroupOf(f.TypeId) == GroupFuelBlock).Sum(f => f.PerHour ?? 0);
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
                                         .Select(g => new CorpFitInfo("MoonMaterialBay", 0, g.Key, TypeName(g.Key), g.Sum(a => a.Quantity), false))
                                         .OrderByDescending(f => f.Quantity)],
                FitKnown           = corpHasAssets,
                BlocksPerHour      = fuel.Any(f => GroupOf(f.TypeId) == GroupFuelBlock) || expectedBlocks is not null
                                         ? (blocksPerHour > 0 ? blocksPerHour : expectedBlocks) : null,
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
                BlocksPerHour   = fuel.Where(f => GroupOf(f.TypeId) == GroupFuelBlock).Select(f => f.PerHour).FirstOrDefault()
                                  ?? (online ? expected : null),
                RunsOut         = runsOut,
                IsPowered       = online,
                NeedsFuel       = online,
            });
        }

        return result;
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
        _                 => 8,
    };
}
