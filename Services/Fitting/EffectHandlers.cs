namespace EveConsole.Services.Fitting;

/// <summary>
/// Effects the engine computes in code instead of from their modifierInfo: those the SDE lists
/// with no modifiers although they change attributes in game.
/// </summary>
/// <remarks>
/// <para>Handled: propulsion modules, T3 subsystem slots and hardpoints, the missile and drone
/// skills whose bonus the SDE scales by level but applies to nothing, and a Reactive Armor
/// Hardener's adaptation (<see cref="AdaptiveArmor"/>).</para>
///
/// <para>Not handled yet, by decision, and contributing nothing until they are: command bursts, doomsdays, bubble generators, MJD signature bloom and
/// the other active effects of that kind.</para>
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
    };

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
        }
        return false;
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
