using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Fitting;

/// <summary>What an item is to a fit. <see cref="Item"/> is everything else — fuel, ore, scripts
/// with no module, blueprints — which a fit can only carry in its hold.</summary>
public enum CatalogKind { Hull, Module, Rig, Subsystem, Charge, Drone, Fighter, Implant, Booster, Item }

/// <summary>One thing the fitting tool can put on a fit, as the finder lists it.</summary>
/// <param name="Name">The SDE's English name: what fits are stored, exported and matched by.</param>
/// <param name="GroupName">Likewise English.</param>
/// <param name="MarketGroupId">Where the market lists it — what the finder groups by when nothing is typed.</param>
public sealed record CatalogEntry(int TypeId, string Name, int GroupId, string GroupName, CatalogKind Kind, FitSlot Slot, int? MetaGroupId,
    int? MarketGroupId = null)
{
    /// <summary>The item's name in the interface language, for showing. Display only.</summary>
    public string DisplayName      => SdeNames.Type(TypeId, Name);

    /// <summary>The item's group in the interface language, for showing. Display only.</summary>
    public string DisplayGroupName => SdeNames.Group(GroupId, GroupName);

    public string SlotLabel => Kind switch
    {
        CatalogKind.Hull      => DisplayGroupName,
        CatalogKind.Module    => Slot switch
        {
            FitSlot.High => FittingText.FinderHigh, FitSlot.Mid => FittingText.FinderMid, FitSlot.Low => FittingText.FinderLow,
            FitSlot.Service => FittingText.FinderService, _ => "",
        },
        CatalogKind.Rig       => FittingText.FinderRig,
        CatalogKind.Subsystem => FittingText.FinderSubsystem,
        CatalogKind.Charge    => FittingText.FinderCharge,
        CatalogKind.Drone     => FittingText.FinderDrone,
        CatalogKind.Fighter   => FittingText.FinderFighter,
        CatalogKind.Implant   => FittingText.KindImplant,
        CatalogKind.Booster   => FittingText.KindBooster,
        _                     => DisplayGroupName,
    };

    /// <summary>Whether a word typed in a search box is in the item's name or its group's — as
    /// shown, or in English, which people paste from websites and chat.</summary>
    public bool Matches(string word) =>
        SdeNames.Matches(SdeNameKind.Type, TypeId, Name, word) || SdeNames.Matches(SdeNameKind.Group, GroupId, GroupName, word);

    public override string ToString() => DisplayName;
}

/// <summary>
/// Everything the fitting tool can search for, and the rules for what may go where: which hulls
/// a module is allowed on, which charges a module takes, whether a slot or hardpoint is free.
/// </summary>
public sealed class FittingCatalog
{
    public IReadOnlyList<CatalogEntry> Entries { get; }
    private readonly Dictionary<int, CatalogEntry> _byId;
    private readonly DogmaData _data;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Dictionary<int, IReadOnlyList<CatalogEntry>> _chargesByGroup = new();

    /// <summary>The market's groups, by id: each one's parent and English name.</summary>
    public IReadOnlyDictionary<int, (int? ParentId, string Name)> MarketGroups { get; }

    private FittingCatalog(DogmaData data, IDbContextFactory<AppDbContext> dbFactory, List<CatalogEntry> entries,
        Dictionary<int, (int? ParentId, string Name)> marketGroups)
    {
        _data        = data;
        _dbFactory   = dbFactory;
        Entries      = entries;
        _byId        = entries.ToDictionary(e => e.TypeId);
        MarketGroups = marketGroups;
    }

    public CatalogEntry? Find(int typeId) => _byId.GetValueOrDefault(typeId);

    public static async Task<FittingCatalog> LoadAsync(DogmaData data, IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await (from t in db.SdeTypes.AsNoTracking()
                          join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
                          where t.Published
                          select new { t.TypeId, t.Name, t.GroupId, GroupName = g.Name, g.CategoryId, t.MetaGroupId, t.MarketGroupId }).ToListAsync(ct);
        var marketGroups = (await db.SdeMarketGroups.AsNoTracking()
                .Select(m => new { m.MarketGroupId, m.ParentGroupId, m.Name }).ToListAsync(ct))
            .ToDictionary(m => m.MarketGroupId, m => (m.ParentGroupId, m.Name));

        // The slot a module takes is one of its effects; read them for every module at once.
        var slotEffects = new Dictionary<int, FitSlot>();
        foreach (var (name, slot) in new[] { ("hiPower", FitSlot.High), ("medPower", FitSlot.Mid), ("loPower", FitSlot.Low),
                                              ("rigSlot", FitSlot.Rig), ("subSystem", FitSlot.Subsystem), ("serviceSlot", FitSlot.Service) })
            if (data.EffectsByName.TryGetValue(name, out var fx)) slotEffects[fx.Id] = slot;
        var effectIds = slotEffects.Keys.ToList();
        var slotOf = (await db.SdeTypeDogmaEffects.AsNoTracking().Where(e => effectIds.Contains(e.EffectId))
                .Select(e => new { e.TypeId, e.EffectId }).ToListAsync(ct))
            .GroupBy(e => e.TypeId).ToDictionary(g => g.Key, g => slotEffects[g.First().EffectId]);

        var boosterness = data.Attribute("boosterness")?.Id;
        var boosters = boosterness is { } bid
            ? (await db.SdeTypeDogmaAttributes.AsNoTracking().Where(a => a.AttributeId == bid).Select(a => a.TypeId).ToListAsync(ct)).ToHashSet()
            : [];

        var entries = rows.Select(r =>
        {
            var slot = slotOf.GetValueOrDefault(r.TypeId, FitSlot.None);
            var kind = r.CategoryId switch
            {
                DogmaData.CategoryShip      => CatalogKind.Hull,
                DogmaData.CategoryCharge    => CatalogKind.Charge,
                DogmaData.CategoryDrone     => CatalogKind.Drone,
                DogmaData.CategoryFighter   => CatalogKind.Fighter,
                DogmaData.CategorySubsystem => CatalogKind.Subsystem,
                DogmaData.CategoryImplant   => boosters.Contains(r.TypeId) ? CatalogKind.Booster : CatalogKind.Implant,
                DogmaData.CategoryModule or DogmaData.CategoryStructureModule
                                            => slot == FitSlot.Rig ? CatalogKind.Rig : CatalogKind.Module,
                _                           => CatalogKind.Item,
            };
            return new CatalogEntry(r.TypeId, r.Name, r.GroupId, r.GroupName, kind, slot, r.MetaGroupId, r.MarketGroupId);
        })
        // A module with no slot cannot be fitted (fleet-only and deprecated items); it can still be carried.
        .Select(e => e.Kind is CatalogKind.Module or CatalogKind.Rig && e.Slot == FitSlot.None ? e with { Kind = CatalogKind.Item } : e);

        // In the order of the names the finder shows. Waits (once, briefly) for the interface
        // language's names, so the list is not sorted by the English it would then stop showing.
        await SdeNames.EnsureLoadedAsync(ct);
        return new FittingCatalog(data, dbFactory, entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCulture).ToList(), marketGroups);
    }

    /// <summary>Entries whose name or group contains every word of <paramref name="text"/>, as
    /// shown or in English.</summary>
    public IEnumerable<CatalogEntry> Search(string text, IReadOnlySet<CatalogKind>? kinds = null)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Entries.Where(e => (kinds is null || kinds.Contains(e.Kind)) && words.All(e.Matches));
    }

    // ── Charges ─────────────────────────────────────────────────────────────────

    private static readonly string[] ChargeGroupAttrs = ["chargeGroup1", "chargeGroup2", "chargeGroup3", "chargeGroup4", "chargeGroup5"];

    /// <summary>
    /// The charges a module can load: those in one of its charge groups, of its charge size when
    /// it has one, and small enough to fit in it.
    /// </summary>
    public async Task<IReadOnlyList<CatalogEntry>> ChargesForAsync(int moduleTypeId, CancellationToken ct = default)
    {
        await _data.LoadTypesAsync([moduleTypeId], ct);
        var module = _data.Type(moduleTypeId);
        var groups = ChargeGroupAttrs.Select(a => _data.Attribute(a)?.Id).OfType<int>()
            .Select(module.Attr).OfType<double>().Select(v => (int)v).Where(g => g > 0).Distinct().ToList();
        if (groups.Count == 0) return [];

        var candidates = new List<CatalogEntry>();
        foreach (var g in groups)
        {
            if (!_chargesByGroup.TryGetValue(g, out var inGroup))
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var ids = await db.SdeTypes.AsNoTracking().Where(t => t.GroupId == g && t.Published).Select(t => t.TypeId).ToListAsync(ct);
                inGroup = ids.Select(Find).OfType<CatalogEntry>().ToList();
                _chargesByGroup[g] = inGroup;
            }
            candidates.AddRange(inGroup);
        }
        await _data.LoadTypesAsync(candidates.Select(c => c.TypeId), ct);

        var sizeId   = _data.Attribute("chargeSize")?.Id;
        var size     = sizeId is { } s ? module.Attr(s) : null;
        var capacity = module.Attr(DogmaData.AttrCapacity) ?? 0;
        return candidates.Where(c =>
            {
                var t = _data.Type(c.TypeId);
                if (size is { } want && sizeId is { } sid && t.Attr(sid) is { } has && has != want) return false;
                return capacity <= 0 || (t.Attr(DogmaData.AttrVolume) ?? 0) <= capacity + 1e-9;
            })
            // In the order of the names a module's charge list shows.
            .DistinctBy(c => c.TypeId).OrderBy(c => c.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    // ── Fitting rules ───────────────────────────────────────────────────────────

    /// <summary>
    /// Whether <paramref name="typeId"/> may be added to the fit <paramref name="engine"/>
    /// describes, and if not, why — in a sentence for the status line. Checks the slot and
    /// hardpoints left, rig size, the hulls the item is restricted to, a subsystem's hull and
    /// position, and per-fit limits by group and type.
    /// </summary>
    public async Task<string?> WhyNotAsync(DogmaEngine engine, int typeId, CancellationToken ct = default)
    {
        await _data.LoadTypesAsync([typeId], ct);
        var type  = _data.Type(typeId);
        var ship  = engine.Ship.Type;
        var stats = new FitStats(engine);
        var slot  = DogmaEngine.SlotOf(_data, type);
        if (slot == FitSlot.None) return string.Format(FittingText.WhyNotNoSlot, SdeNames.Type(type.Id, type.Name));

        if (stats.SlotsUsed(slot) >= stats.Slots(slot))
            return NoFreeSlot(slot);

        bool Has(string effect) => type.EffectIds.Any(id => _data.Effects.TryGetValue(id, out var fx) && fx.Name == effect);
        if (Has("turretFitted") && Hardpoints(engine, "turretFitted") >= stats.TurretHardpoints)
            return FittingText.WhyNotNoTurret;
        if (Has("launcherFitted") && Hardpoints(engine, "launcherFitted") >= stats.LauncherHardpoints)
            return FittingText.WhyNotNoLauncher;

        if (slot == FitSlot.Rig && _data.Attribute("rigSize")?.Id is { } rigSize
            && type.Attr(rigSize) is { } rs && engine.Value(engine.Ship, rigSize) is var shipRs && shipRs > 0 && rs != shipRs)
            return FittingText.WhyNotRigSize;

        if (slot == FitSlot.Subsystem)
        {
            if (_data.Attribute("fitsToShipType")?.Id is { } fits && type.Attr(fits) is { } hull && (int)hull != ship.Id)
                return FittingText.WhyNotSubsystemHull;
            if (_data.Attribute("subSystemSlot")?.Id is { } pos && type.Attr(pos) is { } p
                && engine.Modules.Any(m => m.Slot == FitSlot.Subsystem && m.Type.Attr(pos) == p))
                return FittingText.WhyNotSubsystemFilled;
        }

        var groups = _data.AttributesByName.Values.Where(a => a.Name.StartsWith("canFitShipGroup")).Select(a => type.Attr(a.Id)).OfType<double>().Select(v => (int)v).ToList();
        var hulls  = _data.AttributesByName.Values.Where(a => a.Name.StartsWith("canFitShipType")).Select(a => type.Attr(a.Id)).OfType<double>().Select(v => (int)v).ToList();
        if ((groups.Count > 0 || hulls.Count > 0) && !groups.Contains(ship.GroupId) && !hulls.Contains(ship.Id))
            return string.Format(FittingText.WhyNotHull, SdeNames.Type(type.Id, type.Name), SdeNames.Type(ship.Id, ship.Name));

        if (_data.Attribute("maxGroupFitted")?.Id is { } mg && type.Attr(mg) is { } maxGroup
            && engine.Modules.Count(m => m.Type.GroupId == type.GroupId) >= maxGroup)
            return string.Format(FittingText.WhyNotMaxGroup, maxGroup);
        if (_data.Attribute("maxTypeFitted")?.Id is { } mt && type.Attr(mt) is { } maxType
            && engine.Modules.Count(m => m.Type.Id == type.Id) >= maxType)
            return string.Format(FittingText.WhyNotMaxType, maxType);

        return null;
    }

    private int Hardpoints(DogmaEngine e, string effect) => e.Modules.Count(m =>
        m.Type.EffectIds.Any(id => _data.Effects.TryGetValue(id, out var fx) && fx.Name == effect));

    /// <summary>Every slot of this kind taken — a sentence per slot, since "high" and "slot" are
    /// not words another language can put together the way English does.</summary>
    private static string NoFreeSlot(FitSlot s) => s switch
    {
        FitSlot.High      => FittingText.NoFreeHigh,
        FitSlot.Mid       => FittingText.NoFreeMid,
        FitSlot.Low       => FittingText.NoFreeLow,
        FitSlot.Rig       => FittingText.NoFreeRig,
        FitSlot.Subsystem => FittingText.NoFreeSubsystem,
        _                 => FittingText.NoFreeService,
    };
}
