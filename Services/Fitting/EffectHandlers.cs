namespace EveConsole.Services.Fitting;

/// <summary>
/// Effects the SDE does not describe with modifierInfo, or describes in a way the client does not
/// actually apply, written out in code — each one after Pyfa's handler of the same name (Pyfa,
/// GPLv3, eos/effects.py), which is where the formulas come from.
/// </summary>
/// <remarks>
/// Effects whose work is not an attribute change at all — a repairer's cycle, a weapon's damage,
/// a capacitor booster's charge — are not here. They are read off the module by
/// <see cref="FitStats"/>, which is how Pyfa treats them too.
/// </remarks>
internal static class EffectHandlers
{
    /// <summary>Registers <paramref name="effect"/> on <paramref name="item"/> in code and returns
    /// true, or returns false to let the engine apply its modifierInfo.</summary>
    public static bool TryRegister(DogmaEngine engine, DogmaItem item, DogmaEffectInfo effect)
    {
        switch (effect.Name)
        {
            case "moduleBonusAfterburner":
                Propulsion(engine, item, signatureBloom: false);
                return true;
            case "moduleBonusMicrowarpdrive":
                Propulsion(engine, item, signatureBloom: true);
                return true;
            case "slotModifier":
                AddFromItem(engine, item, ("hiSlots", "hiSlotModifier"), ("medSlots", "medSlotModifier"), ("lowSlots", "lowSlotModifier"));
                return true;
            case "hardPointModifierEffect":
                AddFromItem(engine, item, ("turretSlotsLeft", "turretHardPointModifier"), ("launcherSlotsLeft", "launcherHardPointModifier"));
                return true;
            case "adaptiveArmorHardener":
                ReactiveArmorHardener(engine, item);
                return true;
        }
        return false;
    }

    /// <summary>Adds attributes of <paramref name="item"/> to the ship's: a subsystem's slots and hardpoints.</summary>
    private static void AddFromItem(DogmaEngine e, DogmaItem item, params (string Ship, string Item)[] pairs)
    {
        foreach (var (shipAttr, itemAttr) in pairs)
            e.AddModification(e.Ship, e.Data.AttrId(shipAttr),
                new Modification(item, DogmaEngine.OpModAdd, e.Data.AttrId(itemAttr), null));
    }

    private static readonly string[] ArmorResonances =
        ["armorEmDamageResonance", "armorThermalDamageResonance", "armorKineticDamageResonance", "armorExplosiveDamageResonance"];

    /// <summary>
    /// A Reactive Armor Hardener's resists after it has adapted to the fit's damage profile.
    /// Pyfa's simulation, step for step: each cycle the two types doing the most damage (after
    /// the ship's other armor resists) take resistanceShiftAmount from the other two, until the
    /// profile repeats; the loop is averaged and rounded to three places. Applied to the ship as
    /// penalized pre-multipliers.
    /// </summary>
    private static void ReactiveArmorHardener(DogmaEngine e, DogmaItem rah)
    {
        var ids = ArmorResonances.Select(e.Data.AttrId).ToArray();
        var result = new Lazy<double[]>(() =>
        {
            var p = e.DamageProfile;
            double[] incoming = [p.Em, p.Thermal, p.Kinetic, p.Explosive];
            // The ship's armor resists without this hardener, as they are before it runs.
            var baseTaken = ids.Select((id, i) => incoming[i] * e.ValueExcluding(e.Ship, id, rah)).ToArray();
            var shift = e.Value(rah, "resistanceShiftAmount") / 100;
            var res   = ids.Select(id => e.Value(rah, id)).ToArray();

            var cycles = new List<double[]>();
            var loopStart = -20;
            for (var n = 0; n < 50; n++)
            {
                // EM, explosive, kinetic, thermal: the client's order for equal damage.
                var order = new[] { 0, 3, 2, 1 }
                    .Select(i => (Index: i, Damage: baseTaken[i] * res[i], Res: res[i]))
                    .OrderBy(t => t.Damage).ToArray();   // stable, like Python's sorted
                double c0, c1, c2, c3;
                if (order[2].Damage == 0)
                {
                    c0 = 1 - order[0].Res; c1 = 1 - order[1].Res; c2 = 1 - order[2].Res;
                    c3 = -(c0 + c1 + c2);
                }
                else if (order[1].Damage == 0)
                {
                    c0 = 1 - order[0].Res; c1 = 1 - order[1].Res;
                    c2 = c3 = -(c0 + c1) / 2;
                }
                else
                {
                    c0 = Math.Min(shift, 1 - order[0].Res); c1 = Math.Min(shift, 1 - order[1].Res);
                    c2 = c3 = -(c0 + c1) / 2;
                }
                res[order[0].Index] = order[0].Res + c0;
                res[order[1].Index] = order[1].Res + c1;
                res[order[2].Index] = order[2].Res + c2;
                res[order[3].Index] = order[3].Res + c3;

                for (var i = 0; i < cycles.Count; i++)
                    if (Enumerable.Range(0, 4).All(k => Math.Abs(res[k] - cycles[i][k]) <= 1e-6)) { loopStart = i; break; }
                if (loopStart >= 0) break;
                cycles.Add((double[])res.Clone());
            }

            var loop = cycles.Skip(loopStart >= 0 ? loopStart : Math.Max(0, cycles.Count + loopStart)).ToList();
            return Enumerable.Range(0, 4)
                .Select(k => Math.Round(loop.Average(c => c[k]), 3, MidpointRounding.ToEven)).ToArray();
        });

        for (var i = 0; i < 4; i++)
        {
            var k = i;
            e.AddModification(e.Ship, ids[k], new Modification(rah, DogmaEngine.OpPreMul, 0,
                () => result.Value[k], PenalizedOverride: true, PenaltyGroup: "preMul"));
        }
    }

    /// <summary>
    /// Adds the module's mass to the ship, then speeds it up by speedFactor × thrust ÷ the ship's
    /// mass <em>including</em> that addition — the reason a plated ship is slow under an MWD. A
    /// post-multiplication in the client, so penalized in that group; an MWD also blooms the
    /// signature.
    /// </summary>
    private static void Propulsion(DogmaEngine e, DogmaItem module, bool signatureBloom)
    {
        var ship = e.Ship;
        e.AddModification(ship, DogmaData.AttrMass,
            new Modification(module, DogmaEngine.OpModAdd, e.Data.AttrId("massAddition"), null));

        int speedFactor = e.Data.AttrId("speedFactor"), thrust = e.Data.AttrId("speedBoostFactor");
        e.AddModification(ship, e.Data.AttrId("maxVelocity"), new Modification(module, DogmaEngine.OpPostPercent, 0,
            () => e.Value(module, speedFactor) * e.Value(module, thrust) / e.Value(ship, DogmaData.AttrMass),
            PenalizedOverride: true, PenaltyGroup: "postMul"));

        if (signatureBloom)
            e.AddModification(ship, e.Data.AttrId("signatureRadius"), new Modification(module, DogmaEngine.OpPostPercent,
                e.Data.AttrId("signatureRadiusBonus"), null, PenalizedOverride: true));
    }
}
