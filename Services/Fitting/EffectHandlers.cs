namespace EveConsole.Services.Fitting;

/// <summary>
/// Effects the engine computes in code instead of from their modifierInfo: those the SDE lists
/// with no modifiers although they change attributes in game.
/// </summary>
/// <remarks>
/// <para>Handled: propulsion modules, T3 subsystem slots and hardpoints, the missile, drone and
/// cloaking skills whose bonus the SDE scales by level but applies to nothing, a Reactive Armor
/// Hardener's adaptation (<see cref="AdaptiveArmor"/>), and what one-off and area modules do to
/// their own ship while running: a micro jump drive's or field generator's signature bloom, a
/// warp disruption field generator's, an Emergency Hull Energizer's hull resistance, and a
/// doomsday's hold on the ship that fires it.</para>
///
/// <para>Not handled yet, by decision, and contributing nothing until they are: command bursts
/// and phenomena generators, which need a fleet-boost system of their own.</para>
/// </remarks>
internal static class EffectHandlers
{
    /// <summary>What a skill bonus lands on: charges (missiles), modules (launchers) or drones —
    /// in each case only those that require the skill.</summary>
    private enum SkillTarget { Charges, Modules, Drones }

    /// <summary>
    /// Skill effects the SDE carries without modifiers. Each names the skill's bonus attribute
    /// (which the skill's own data has already multiplied by its level) and the attribute it
    /// raises, as a percentage, on the items that require the skill.
    /// </summary>
    private static readonly Dictionary<string, (SkillTarget Target, string Bonus, string Attribute)> SkillBonuses = new()
    {
        ["missileEMDmgBonus"]        = (SkillTarget.Charges, "damageMultiplierBonus", "emDamage"),
        ["missileThermalDmgBonus"]   = (SkillTarget.Charges, "damageMultiplierBonus", "thermalDamage"),
        ["missileKineticDmgBonus2"]  = (SkillTarget.Charges, "damageMultiplierBonus", "kineticDamage"),
        ["missileExplosiveDmgBonus"] = (SkillTarget.Charges, "damageMultiplierBonus", "explosiveDamage"),
        ["selfRof"]                  = (SkillTarget.Modules, "rofBonus",              "speed"),
        ["droneDmgBonus"]            = (SkillTarget.Drones,  "damageMultiplierBonus", "damageMultiplier"),
        // Cloaking: shorter targeting delay after decloaking, for cloaks.
        ["cloakingTargetingDelayBonusPostPercentCloakingTargetingDelayBonusForShipModulesRequiringCloaking"]
                                     = (SkillTarget.Modules, "cloakingTargetingDelayBonus", "cloakingTargetingDelay"),
    };

    /// <summary>The hull's resistances, and the attributes an Emergency Hull Energizer carries for them.</summary>
    private static readonly (string Ship, string Module)[] HullResonances =
    [
        ("emDamageResonance",        "hullEmDamageResonance"),
        ("thermalDamageResonance",   "hullThermalDamageResonance"),
        ("kineticDamageResonance",   "hullKineticDamageResonance"),
        ("explosiveDamageResonance", "hullExplosiveDamageResonance"),
    ];

    /// <summary>A subsystem's additions to the hull: slots, then hardpoints.</summary>
    private static readonly (string Effect, string Hull, string Subsystem)[] SubsystemAdditions =
    [
        ("slotModifier",            "hiSlots",           "hiSlotModifier"),
        ("slotModifier",            "medSlots",          "medSlotModifier"),
        ("slotModifier",            "lowSlots",          "lowSlotModifier"),
        ("hardPointModifierEffect", "turretSlotsLeft",   "turretHardPointModifier"),
        ("hardPointModifierEffect", "launcherSlotsLeft", "launcherHardPointModifier"),
    ];

    /// <summary>Registers <paramref name="effect"/> on <paramref name="item"/> in code and returns
    /// true, or returns false to let the engine apply its modifierInfo.</summary>
    public static bool TryRegister(DogmaEngine engine, DogmaItem item, DogmaEffectInfo effect)
    {
        if (SkillBonuses.TryGetValue(effect.Name, out var bonus))
        {
            SkillBonus(engine, item, bonus.Target, bonus.Bonus, bonus.Attribute);
            return true;
        }

        var additions = SubsystemAdditions.Where(a => a.Effect == effect.Name).ToList();
        if (additions.Count > 0)
        {
            foreach (var (_, hull, sub) in additions)
                engine.AddModification(engine.Ship, engine.Data.AttrId(hull),
                    new Modification(item, DogmaEngine.OpModAdd, engine.Data.AttrId(sub), null));
            return true;
        }

        switch (effect.Name)
        {
            case "moduleBonusAfterburner":    Propulsion(engine, item, blooms: false); return true;
            case "moduleBonusMicrowarpdrive": Propulsion(engine, item, blooms: true);  return true;
            case "adaptiveArmorHardener":     engine.AddAdaptive(item, AdaptiveArmor.Register(engine, item)); return true;
            case "microJumpDrive" or "microJumpPortalDrive" or "microJumpPortalDriveCapital":
                Bloom(engine, item, "signatureRadiusBonusPercent"); return true;
            case "warpDisruptSphere":         WarpDisruptSphere(engine, item); return true;
            case "emergencyHullEnergizer":    HullEnergizer(engine, item); return true;
            // Cuts the damage breacher pods do while it runs. The fit's figures take no breacher
            // pod damage, so it changes none of them; the module's row says what it does.
            case "moduleBonusBreacherPodDamageControl": return true;
            case "debuffLance":               Doomsday(engine, item); return true;
        }
        // Doomsdays and burst projectors (doomsday…), and the older titan doomsdays (superWeapon…), targeted.
        if ((effect.Name.StartsWith("doomsday", StringComparison.Ordinal) || effect.Name.StartsWith("superWeapon", StringComparison.Ordinal))
            && effect.Category is 1 or 2)
        {
            Doomsday(engine, item);
            return true;
        }
        return false;
    }

    /// <summary>A modification of <paramref name="attribute"/> by <paramref name="source"/>'s
    /// <paramref name="by"/>, or none when the SDE has no such attribute. <paramref name="chain"/>
    /// names a stacking chain of its own, when it does not share the operation's.</summary>
    private static void Add(DogmaEngine e, DogmaItem target, string attribute, DogmaItem source, int operation, string by,
        string? chain = null)
    {
        if (e.Data.Attribute(attribute)?.Id is { } a && e.Data.Attribute(by)?.Id is { } b)
            e.AddModification(target, a, new Modification(source, operation, b, null, PenaltyGroup: chain));
    }

    /// <summary>A running micro jump drive or field generator: the ship's signature radius grows by
    /// the module's percentage while it spools up — in full, alongside a microwarpdrive's bloom
    /// and anything else: a stacking chain of its own (only one runs at a time).</summary>
    private static void Bloom(DogmaEngine e, DogmaItem module, string bonus) =>
        Add(e, e.Ship, "signatureRadius", module, DogmaEngine.OpPostPercent, bonus, chain: "microJumpBloom");

    /// <summary>
    /// A running warp disruption field generator, without the script that focuses it into a
    /// point: the ship's signature grows by <c>signatureRadiusBonus</c>, its mass and top speed
    /// change by <c>massBonusPercentage</c> and <c>maxVelocityMultiplier</c>, and its propulsion
    /// modules' speed and thrust by <c>speedFactorBonus</c> and <c>speedBoostFactorBonus</c>.
    /// Today's generators carry only the signature bonus; the others are neutral. The focused
    /// script cancels the signature bonus through its own modifiers.
    /// </summary>
    private static void WarpDisruptSphere(DogmaEngine e, DogmaItem module)
    {
        Add(e, e.Ship, "signatureRadius", module, DogmaEngine.OpPostPercent, "signatureRadiusBonus", chain: "warpDisruptSphere");
        Add(e, e.Ship, "mass",            module, DogmaEngine.OpPostPercent, "massBonusPercentage");
        Add(e, e.Ship, "maxVelocity",     module, DogmaEngine.OpPostMul,     "maxVelocityMultiplier");
        foreach (var prop in e.Modules.Where(m => m.Type.EffectIds.Any(id => e.Data.Effects.TryGetValue(id, out var fx)
                     && fx.Name is "moduleBonusAfterburner" or "moduleBonusMicrowarpdrive")))
        {
            Add(e, prop, "speedFactor",      module, DogmaEngine.OpPostPercent, "speedFactorBonus");
            Add(e, prop, "speedBoostFactor", module, DogmaEngine.OpPostPercent, "speedBoostFactorBonus");
        }
    }

    /// <summary>A running Emergency Hull Energizer: the hull takes its resistances, multiplied in
    /// before other modules' percentages as a Damage Control's are, but in full alongside it — a
    /// stacking chain of its own. (The rule that penalizes a Damage Control with a Reactive Armor
    /// Hardener or Bastion does not name it.)</summary>
    private static void HullEnergizer(DogmaEngine e, DogmaItem module)
    {
        foreach (var (ship, own) in HullResonances)
            Add(e, e.Ship, ship, module, DogmaEngine.OpPreMul, own, chain: "emergencyHullEnergizer");
    }

    /// <summary>
    /// A doomsday or burst projector firing, as it holds the ship that fires it: one that
    /// immobilises (<c>doomsdayImmobilityDuration</c>) stops the ship — its <c>speedFactor</c>, −100%,
    /// on top speed — and one with <c>siegeModeWarpStatus</c> keeps it from warping. Its effect on
    /// targets is not part of a fit.
    /// </summary>
    private static void Doomsday(DogmaEngine e, DogmaItem module)
    {
        if (e.Data.Attribute("doomsdayImmobilityDuration")?.Id is { } still && e.Value(module, still) > 0
            && e.Data.Attribute("speedFactor")?.Id is { } factor && e.Data.Attribute("maxVelocity")?.Id is { } speed)
            e.AddModification(e.Ship, speed, new Modification(module, DogmaEngine.OpPostPercent, factor, null, PenalizedOverride: false));
        if (e.Data.Attribute("siegeModeWarpStatus")?.Id is { } status && module.Type.Attributes.ContainsKey(status))
            Add(e, e.Ship, "warpScrambleStatus", module, DogmaEngine.OpModAdd, "siegeModeWarpStatus");
    }

    private static void SkillBonus(DogmaEngine e, DogmaItem skill, SkillTarget target, string bonusAttr, string attr)
    {
        IEnumerable<DogmaItem> items = target switch
        {
            SkillTarget.Charges => e.Charges,
            SkillTarget.Modules => e.Modules,
            _                   => e.Drones,
        };
        var bonusId = e.Data.AttrId(bonusAttr);
        var attrId  = e.Data.AttrId(attr);
        foreach (var item in items.Where(i => i.Type.Requires(skill.Type.Id)))
            e.AddModification(item, attrId, new Modification(skill, DogmaEngine.OpPostPercent, bonusId, null));
    }

    /// <summary>
    /// An active afterburner or microwarpdrive. It adds its <c>massAddition</c> to the ship, then
    /// multiplies top speed by 1 + speedFactor% × thrust ÷ the ship's mass — mass that includes
    /// the addition, which is why plates and a prop module slow each other down. A
    /// microwarpdrive also blooms the signature radius by its <c>signatureRadiusBonus</c>.
    /// </summary>
    private static void Propulsion(DogmaEngine e, DogmaItem module, bool blooms)
    {
        var ship = e.Ship;
        e.AddModification(ship, DogmaData.AttrMass,
            new Modification(module, DogmaEngine.OpModAdd, e.Data.AttrId("massAddition"), null));

        int speedFactor = e.Data.AttrId("speedFactor"), thrust = e.Data.AttrId("speedBoostFactor");
        e.AddModification(ship, e.Data.AttrId("maxVelocity"), new Modification(module, DogmaEngine.OpPostMul, 0,
            () => 1 + e.Value(module, speedFactor) / 100 * e.Value(module, thrust) / e.Value(ship, DogmaData.AttrMass)));

        if (blooms)
            e.AddModification(ship, e.Data.AttrId("signatureRadius"),
                new Modification(module, DogmaEngine.OpPostPercent, e.Data.AttrId("signatureRadiusBonus"), null));
    }
}
