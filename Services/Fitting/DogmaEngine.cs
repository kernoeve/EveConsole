namespace EveConsole.Services.Fitting;

public enum DogmaItemKind { Character, Skill, Ship, Module, Rig, Subsystem, Charge, Drone, Fighter, Implant, Booster }

/// <summary>One item taking part in a calculation: the ship, a module, a skill, a drone stack…</summary>
public sealed class DogmaItem
{
    public required DogmaItemKind Kind  { get; init; }
    public required DogmaTypeInfo Type  { get; init; }
    public ModuleState State { get; set; } = ModuleState.Online;
    public FitSlot     Slot  { get; init; } = FitSlot.None;
    /// <summary>For a module: its loaded charge. For a charge: null.</summary>
    public DogmaItem?  Charge { get; set; }
    /// <summary>For a charge: the module it is loaded in.</summary>
    public DogmaItem?  Holder { get; init; }
    /// <summary>Drone stack size, and how many of them are launched. For a fighter squadron: its
    /// size, and the same again when it is in a launch tube.</summary>
    public int Count       { get; init; } = 1;
    public int ActiveCount { get; init; }
    /// <summary>For a fighter squadron: the effect ids of the abilities switched on.</summary>
    public IReadOnlySet<int> Abilities { get; init; } = EmptyAbilities;
    internal static readonly IReadOnlySet<int> EmptyAbilities = new HashSet<int>();
    /// <summary>Position in the fit's module or drone list, for reporting.</summary>
    public int Index       { get; init; }

    /// <summary>Base values that replace the type's own — a skill's level.</summary>
    internal Dictionary<int, double>? Forced { get; init; }

    public override string ToString() => $"{Kind} {Type.Name}";
}

/// <summary>
/// One modification waiting to be applied: "attribute <c>ModifyingAttributeId</c> of
/// <c>Source</c>, by <c>Operation</c>". An <c>Amount</c> function stands in for effects that
/// have no modifierInfo and are computed in code instead (see <see cref="EffectHandlers"/>).
/// </summary>
internal sealed record Modification(DogmaItem Source, int Operation, int ModifyingAttributeId, Func<double>? Amount,
    bool? PenalizedOverride = null, string? PenaltyGroup = null);

/// <summary>
/// The fitting engine: every item in a fit, every modifier their running effects register, and
/// every attribute evaluated on demand from them.
/// </summary>
/// <remarks>
/// <para>Built from the SDE's modifierInfo rather than from a handler per effect: 3,205 of the
/// 3,422 effects describe themselves that way. What modifierInfo leaves out is the game's general
/// rules, which live here: the order operations apply in, the stacking penalty, and which effect
/// categories run in which module state. The few effects that change attributes with no
/// modifiers at all go through <see cref="EffectHandlers"/>.</para>
///
/// <para>An attribute is computed the first time it is asked for and remembered: its base
/// value, then every modification aimed at it, each of which reads an attribute of its source —
/// which is itself computed the same way. A skill multiplies its bonus by its level, a hull
/// multiplies its bonus by a skill, and the module sees the result, all through this one path.
/// </para>
/// </remarks>
public sealed class DogmaEngine
{
    // Operations, as the SDE numbers them.
    internal const int OpPreAssign   = -1;
    internal const int OpPreMul      = 0;
    internal const int OpPreDiv      = 1;
    internal const int OpModAdd      = 2;
    internal const int OpModSub      = 3;
    internal const int OpPostMul     = 4;
    internal const int OpPostDiv     = 5;
    internal const int OpPostPercent = 6;
    internal const int OpPostAssign  = 7;

    // The stacking penalty: the i-th penalized multiplier (0-based, strongest first) keeps
    // e^-(i²/7.1289) of its effect — 100%, 86.9%, 57.1%, 28.3%, 10.6%, …
    private const double PenaltyDenominator = 7.1289;

    /// <summary>skillEffect, on every skill: derives the level from skill points (operation 9).
    /// Levels are given, not derived, so it never runs.</summary>
    private const int SkillEffectId = 132;

    private static readonly HashSet<int> PenaltyExemptCategories =
    [
        DogmaData.CategoryShip, DogmaData.CategoryCharge, DogmaData.CategorySkill,
        DogmaData.CategoryImplant, DogmaData.CategorySubsystem,
    ];

    private static readonly HashSet<string> TwoDecimalAttributes = ["cpu", "power", "cpuOutput", "powerOutput"];

    public DogmaData Data { get; }
    /// <summary>The damage the fit is assumed to be taking — what a Reactive Armor Hardener
    /// adapts to. Uniform unless the caller says otherwise.</summary>
    public DamageProfile DamageProfile { get; }
    public DogmaItem Character { get; }
    public DogmaItem Ship      { get; }
    public IReadOnlyList<DogmaItem> Skills   { get; }
    public IReadOnlyList<DogmaItem> Modules  { get; }   // incl. rigs and subsystems, fit order
    public IReadOnlyList<DogmaItem> Charges  { get; }
    /// <summary>Drone stacks and fighter squadrons: what the pilot flies, where drone and fighter
    /// skills and hull bonuses land.</summary>
    public IReadOnlyList<DogmaItem> Drones   { get; }
    public IEnumerable<DogmaItem> DroneStacks => Drones.Where(d => d.Kind == DogmaItemKind.Drone);
    public IEnumerable<DogmaItem> Fighters    => Drones.Where(d => d.Kind == DogmaItemKind.Fighter);
    public IReadOnlyList<DogmaItem> Implants { get; }
    public IReadOnlyList<DogmaItem> Boosters { get; }
    /// <summary>What the fit carries in its hold. Cargo takes no part in the calculation; it is here so
    /// the fit is whole — volume, value, and what is written back to the game.</summary>
    public IReadOnlyList<(int TypeId, int Quantity)> Cargo { get; }
    public SkillSet SkillSet { get; }

    private readonly Dictionary<(DogmaItem, int), List<Modification>> _mods = new();
    private readonly Dictionary<(DogmaItem, int), double> _cache = new();
    private readonly HashSet<(DogmaItem, int)> _evaluating = [];

    private DogmaEngine(DogmaData data, FitDefinition fit, SkillSet skills, DamageProfile? profile)
    {
        Data     = data;
        SkillSet = skills;
        DamageProfile = profile ?? DamageProfile.Uniform;

        Character = new DogmaItem { Kind = DogmaItemKind.Character, Type = data.Type(CharacterTypeId) };
        Skills = skills.Levels.Where(kv => kv.Value > 0 && data.TryType(kv.Key, out _))
            .Select(kv => new DogmaItem
            {
                Kind = DogmaItemKind.Skill, Type = data.Type(kv.Key),
                Forced = new() { [DogmaData.AttrSkillLevel] = kv.Value },
            }).ToList();

        Ship = new DogmaItem { Kind = DogmaItemKind.Ship, Type = data.Type(fit.ShipTypeId) };

        var modules = new List<DogmaItem>();
        var charges = new List<DogmaItem>();
        for (var i = 0; i < fit.Modules.Count; i++)
        {
            var m    = fit.Modules[i];
            var type = data.Type(m.TypeId);
            var slot = SlotOf(data, type);
            var kind = slot switch
            {
                FitSlot.Rig       => DogmaItemKind.Rig,
                FitSlot.Subsystem => DogmaItemKind.Subsystem,
                _                 => DogmaItemKind.Module,
            };
            var item = new DogmaItem { Kind = kind, Type = type, Slot = slot, Index = i,
                State = kind == DogmaItemKind.Module ? m.State : ModuleState.Online };
            if (m.ChargeTypeId is { } chargeId && data.TryType(chargeId, out var chargeType))
            {
                var charge = new DogmaItem { Kind = DogmaItemKind.Charge, Type = chargeType, Holder = item, Index = i };
                item.Charge = charge;
                charges.Add(charge);
            }
            modules.Add(item);
        }
        Modules = modules;
        Charges = charges;

        Drones = fit.Drones.Select((d, i) =>
        {
            var type = data.Type(d.TypeId);
            var fighter = type.CategoryId == DogmaData.CategoryFighter;
            return new DogmaItem
            {
                Kind = fighter ? DogmaItemKind.Fighter : DogmaItemKind.Drone, Type = type, Index = i,
                Count = d.Count,
                ActiveCount = fighter ? (d.Active > 0 ? d.Count : 0) : Math.Min(d.Active, d.Count),
                State = d.Active > 0 ? ModuleState.Active : ModuleState.Offline,
                Abilities = fighter ? (d.Abilities ?? FighterAbilities.Defaults(data, type)).ToHashSet() : DogmaItem.EmptyAbilities,
            };
        }).ToList();
        Implants = fit.Implants.Select(id => new DogmaItem { Kind = DogmaItemKind.Implant, Type = data.Type(id) }).ToList();
        Boosters = fit.Boosters.Select(id => new DogmaItem { Kind = DogmaItemKind.Booster, Type = data.Type(id) }).ToList();
        Cargo    = fit.Cargo.ToList();

        foreach (var item in AllItems())
            RegisterEffects(item);
    }

    /// <summary>The generic character type (Amarr). Its dogma attributes are the pilot's base
    /// values — max locked targets, drone control, and the rest skills add to.</summary>
    public const int CharacterTypeId = 1373;

    /// <summary>Loads what the fit needs and computes it.</summary>
    public static async Task<DogmaEngine> CreateAsync(DogmaData data, FitDefinition fit, SkillSet skills,
        DamageProfile? profile = null, CancellationToken ct = default)
    {
        await data.LoadTypesAsync(fit.AllTypeIds().Append(CharacterTypeId).Concat(skills.Levels.Keys), ct);
        // A heavy fighter's bomb is a type of its own, whose damage the bomb ability delivers.
        if (data.Attribute("fighterAbilityLaunchBombType")?.Id is { } bombAttr)
        {
            var bombs = fit.Drones.Select(d => data.TryType(d.TypeId, out var t) ? t.Attr(bombAttr) : null)
                .OfType<double>().Where(b => b > 0).Select(b => (int)b).ToList();
            if (bombs.Count > 0) await data.LoadTypesAsync(bombs, ct);
        }
        return new DogmaEngine(data, fit, skills, profile);
    }

    public IEnumerable<DogmaItem> AllItems()
    {
        yield return Character;
        foreach (var s in Skills)   yield return s;
        yield return Ship;
        foreach (var m in Modules)  yield return m;
        foreach (var c in Charges)  yield return c;
        foreach (var d in Drones)   yield return d;
        foreach (var i in Implants) yield return i;
        foreach (var b in Boosters) yield return b;
    }

    public static FitSlot SlotOf(DogmaData data, DogmaTypeInfo type)
    {
        foreach (var id in type.EffectIds)
        {
            if (!data.Effects.TryGetValue(id, out var e)) continue;
            switch (e.Name)
            {
                case "hiPower":     return FitSlot.High;
                case "medPower":    return FitSlot.Mid;
                case "loPower":     return FitSlot.Low;
                case "rigSlot":     return FitSlot.Rig;
                case "subSystem":   return FitSlot.Subsystem;
                case "serviceSlot": return FitSlot.Service;
            }
        }
        return FitSlot.None;
    }

    // ── Values ──────────────────────────────────────────────────────────────────

    public double Value(DogmaItem item, string attributeName) =>
        Data.AttributesByName.TryGetValue(attributeName, out var a) ? Value(item, a.Id) : 0;

    public double Value(DogmaItem item, int attributeId)
    {
        var key = (item, attributeId);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        // A modifier that (through some chain) reads the attribute it modifies. The data has
        // none that matter; a cycle gets the unmodified value rather than a stack overflow.
        if (!_evaluating.Add(key)) return BaseValue(item, attributeId);
        try
        {
            var value = Compute(item, attributeId);
            _cache[key] = value;
            return value;
        }
        finally { _evaluating.Remove(key); }
    }

    /// <summary>The value before any modifier: forced, else the type's own, else the attribute's default.</summary>
    public double BaseValue(DogmaItem item, int attributeId)
    {
        if (item.Forced is not null && item.Forced.TryGetValue(attributeId, out var forced)) return forced;
        if (item.Type.Attributes.TryGetValue(attributeId, out var own)) return own;
        return Data.Attributes.TryGetValue(attributeId, out var info) ? info.DefaultValue : 0;
    }

    /// <summary>
    /// The attribute as it would be without <paramref name="excluded"/>'s modifications — what an
    /// effect that reacts to the ship's state reads, so it does not react to itself. Not cached.
    /// </summary>
    public double ValueExcluding(DogmaItem item, int attributeId, DogmaItem excluded) =>
        Compute(item, attributeId, excluded);

    private double Compute(DogmaItem item, int attributeId, DogmaItem? excluded = null)
    {
        var info  = Data.Attributes.GetValueOrDefault(attributeId);
        var value = BaseValue(item, attributeId);

        if (_mods.TryGetValue((item, attributeId), out var all))
        {
            var mods = excluded is null ? all : all.Where(m => m.Source != excluded).ToList();
            // Resolve every amount first; each is itself an attribute of its source.
            var resolved = mods.Select(m => (m, amount: m.Amount?.Invoke() ?? Value(m.Source, m.ModifyingAttributeId))).ToList();

            // PreAssign: the last one wins, as it does in the client.
            foreach (var (_, amount) in resolved.Where(r => r.m.Operation == OpPreAssign))
                value = amount;

            // Then additions, then plain multipliers, then each penalty group's chain.
            foreach (var (m, amount) in resolved)
                if (m.Operation == OpModAdd) value += amount;
                else if (m.Operation == OpModSub) value -= amount;

            // Penalized multipliers are penalized within their group only: multipliers applied by
            // different operations on the same attribute do not reduce each other.
            var penalized = new Dictionary<string, List<double>>();
            foreach (var (m, amount) in resolved)
            {
                var factor = m.Operation switch
                {
                    OpPreMul or OpPostMul => amount,
                    OpPreDiv or OpPostDiv => amount == 0 ? 1 : 1 / amount,
                    OpPostPercent         => 1 + amount / 100,
                    _                     => double.NaN,
                };
                if (double.IsNaN(factor)) continue;

                if (IsPenalized(m, info))
                {
                    var group = m.PenaltyGroup ?? DefaultPenaltyGroup(m.Operation);
                    if (!penalized.TryGetValue(group, out var list)) penalized[group] = list = [];
                    list.Add(factor);
                }
                else value *= factor;
            }
            foreach (var chain in penalized.Values)
                value *= PenaltyMultiplier(chain);

            foreach (var (_, amount) in resolved.Where(r => r.m.Operation == OpPostAssign))
                value = amount;
        }

        if (info is not null)
        {
            if (info.MaxAttributeId is { } maxId && maxId != attributeId) value = Math.Min(value, Value(item, maxId));
            if (info.MinAttributeId is { } minId && minId != attributeId) value = Math.Max(value, Value(item, minId));
            if (TwoDecimalAttributes.Contains(info.Name)) value = Math.Round(value, 2);
        }
        return value;
    }

    /// <summary>The stacking group a penalized multiplier joins when its modification does not
    /// name one: its operation. Stacking penalties apply among modifiers of the same operation
    /// on an attribute — two percentage bonuses reduce each other, a percentage bonus and a
    /// plain multiplier do not.</summary>
    internal static string DefaultPenaltyGroup(int operation) => operation.ToString();

    private static bool IsPenalized(Modification m, DogmaAttributeInfo? target)
    {
        if (m.PenalizedOverride is { } forced) return forced;
        if (target is null || target.Stackable) return false;
        if (m.Source.Kind is DogmaItemKind.Character or DogmaItemKind.Skill) return false;
        return !PenaltyExemptCategories.Contains(m.Source.Type.CategoryId);
    }

    /// <summary>Bonuses and penalties are penalized apart, strongest first; the first of each is
    /// free and the i-th keeps e^-(i²/7.1289) of its effect.</summary>
    internal static double PenaltyMultiplier(IReadOnlyList<double> factors)
    {
        var result = 1.0;
        foreach (var chain in new[] { factors.Where(f => f > 1), factors.Where(f => f < 1) })
        {
            var i = 0;
            foreach (var f in chain.OrderByDescending(f => Math.Abs(f - 1)))
            {
                result *= 1 + (f - 1) * Math.Exp(-(i * i) / PenaltyDenominator);
                i++;
            }
        }
        return result;
    }

    // ── Registration ────────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="item"/>'s effect of <paramref name="category"/> runs.
    /// A module's passive effects run whatever its state, its online effects when
    /// it is online, its active ones when active, its overload ones when overheated; a charge
    /// follows its module; everything else is passive and always on.</summary>
    public static bool Runs(DogmaItem item, int category)
    {
        const int passive = 0, active = 1, online = 4, overload = 5;
        return item.Kind switch
        {
            DogmaItemKind.Module => category switch
            {
                passive  => true,
                online   => item.State >= ModuleState.Online,
                active   => item.State >= ModuleState.Active,
                overload => item.State >= ModuleState.Overheated,
                _        => false,
            },
            DogmaItemKind.Charge => category switch
            {
                passive or online => item.Holder is { State: >= ModuleState.Online },
                active            => item.Holder is { State: >= ModuleState.Active },
                _                 => false,
            },
            DogmaItemKind.Drone => category is passive or online,
            _                   => category is passive or online,
        };
    }

    private void RegisterEffects(DogmaItem item)
    {
        foreach (var effectId in item.Type.EffectIds)
        {
            if (effectId == SkillEffectId) continue;
            if (!Data.Effects.TryGetValue(effectId, out var effect)) continue;
            if (!Runs(item, effect.Category)) continue;

            if (EffectHandlers.TryRegister(this, item, effect)) continue;

            foreach (var mod in effect.Modifiers)
            {
                var domain = ResolveDomain(item, mod.Domain);
                if (domain is null) continue;
                foreach (var target in Targets(item, domain, mod))
                    AddModification(target, mod.ModifiedAttributeId,
                        new Modification(item, mod.Operation, mod.ModifyingAttributeId, null));
            }
        }
    }

    internal void AddModification(DogmaItem target, int attributeId, Modification m)
    {
        var key = (target, attributeId);
        if (!_mods.TryGetValue(key, out var list)) _mods[key] = list = [];
        list.Add(m);
    }

    private DogmaItem? ResolveDomain(DogmaItem source, string domain) => domain switch
    {
        "itemID"                  => source,
        "shipID" or "structureID" => Ship,
        "charID"                  => Character,
        "otherID"                 => source.Kind == DogmaItemKind.Charge ? source.Holder : source.Charge,
        _                         => null,   // target, targetID: projected, not part of a local fit
    };

    private IEnumerable<DogmaItem> Targets(DogmaItem source, DogmaItem domain, DogmaModifierInfo mod)
    {
        int? skill = mod.SkillTypeId == -1 ? source.Type.Id : mod.SkillTypeId;
        return mod.Func switch
        {
            "ItemModifier"                  => [domain],
            "LocationModifier"              => LocatedIn(domain),
            "LocationGroupModifier"         => LocatedIn(domain).Where(i => i.Type.GroupId == mod.GroupId),
            "LocationRequiredSkillModifier" => LocatedIn(domain).Where(i => skill is { } s && i.Type.Requires(s)),
            "OwnerRequiredSkillModifier"    => OwnedBy(domain).Where(i => skill is { } s && i.Type.Requires(s)),
            _                               => [],
        };
    }

    /// <summary>What sits "in" a location: the ship holds what is fitted to it and the charges
    /// loaded there; the character holds its implants and boosters.</summary>
    private IEnumerable<DogmaItem> LocatedIn(DogmaItem location)
    {
        if (location == Ship) return Modules.Concat(Charges);
        if (location == Character) return Implants.Concat(Boosters);
        return [];
    }

    /// <summary>What the pilot owns in space: drones and charges (missiles, bombs), which is
    /// where drone and missile skills and hull bonuses land.</summary>
    private IEnumerable<DogmaItem> OwnedBy(DogmaItem owner) =>
        owner == Character ? Drones.Concat(Charges) : [];
}
