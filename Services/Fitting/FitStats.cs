namespace EveConsole.Services.Fitting;

public sealed record DamageProfile(double Em, double Thermal, double Kinetic, double Explosive)
{
    public static readonly DamageProfile Uniform = new(25, 25, 25, 25);
    public double Total => Em + Thermal + Kinetic + Explosive;
}

public sealed record LayerStats(double Hp, double EmResonance, double ThermalResonance, double KineticResonance, double ExplosiveResonance)
{
    /// <summary>HP divided by the share of incoming damage the layer does not resist.</summary>
    public double Ehp(DamageProfile p)
    {
        var taken = (p.Em * EmResonance + p.Thermal * ThermalResonance + p.Kinetic * KineticResonance
                   + p.Explosive * ExplosiveResonance) / p.Total;
        return taken <= 0 ? 0 : Hp / taken;
    }
}

public sealed record DamageBreakdown(double Em, double Thermal, double Kinetic, double Explosive)
{
    public static readonly DamageBreakdown Zero = new(0, 0, 0, 0);
    public double Total => Em + Thermal + Kinetic + Explosive;
    public static DamageBreakdown operator +(DamageBreakdown a, DamageBreakdown b) =>
        new(a.Em + b.Em, a.Thermal + b.Thermal, a.Kinetic + b.Kinetic, a.Explosive + b.Explosive);
    public static DamageBreakdown operator *(DamageBreakdown a, double k) =>
        new(a.Em * k, a.Thermal * k, a.Kinetic * k, a.Explosive * k);
}

public enum WeaponKind { Turret, Missile, Smartbomb, Drone, Fighter }

/// <summary>One weapon, drone stack or fighter ability: what one volley does and how often it
/// fires. A fighter squadron has one per damaging ability switched on, named by <paramref name="Label"/>;
/// a one-off strike (kamikaze) has no cycle, and counts in the volley but not the DPS.</summary>
public sealed record WeaponDamage(DogmaItem Item, WeaponKind Kind, DamageBreakdown Volley, double CycleSeconds, string? Label = null,
    FighterAbility? Ability = null)
{
    public DamageBreakdown Dps => CycleSeconds > 0 ? Volley * (1 / CycleSeconds) : DamageBreakdown.Zero;
}

public enum TankLayer { Shield, Armor, Hull }

/// <summary>One repair module: what one cycle restores and how long a cycle takes.</summary>
public sealed record RepairModule(DogmaItem Item, TankLayer Layer, double Amount, double CycleSeconds)
{
    public double PerSecond => CycleSeconds > 0 ? Amount / CycleSeconds : 0;
}

/// <summary>Raw HP/s: shield regeneration at its peak, and what active modules repair per layer.</summary>
public sealed record TankRates(double PassiveShield, double ShieldBoost, double ArmorRepair, double HullRepair);

/// <summary>What a fit adds up to — the numbers a fitting window shows.</summary>
public sealed class FitStats
{
    private readonly DogmaEngine _e;
    public FitStats(DogmaEngine engine) => _e = engine;

    private double Ship(string attr) => _e.Value(_e.Ship, attr);

    // ── Resources ───────────────────────────────────────────────────────────────

    private IEnumerable<DogmaItem> OnlineModules =>
        _e.Modules.Where(m => m.Kind == DogmaItemKind.Module && m.State >= ModuleState.Online);

    public double CpuOutput  => Ship("cpuOutput");
    public double PowerOutput => Ship("powerOutput");
    public double Calibration => Ship("upgradeCapacity");
    public double CpuUsed    => Math.Round(OnlineModules.Sum(m => _e.Value(m, "cpu")), 2);
    public double PowerUsed  => Math.Round(OnlineModules.Sum(m => _e.Value(m, "power")), 2);
    public double CalibrationUsed => _e.Modules.Where(m => m.Kind == DogmaItemKind.Rig).Sum(m => _e.Value(m, "upgradeCost"));
    public double DroneBay   => Ship("droneCapacity");
    public double DroneBayUsed => _e.DroneStacks.Sum(d => _e.Value(d, DogmaData.AttrVolume) * d.Count);
    public double DroneBandwidth => Ship("droneBandwidth");
    public double CargoCapacity => _e.Value(_e.Ship, DogmaData.AttrCapacity);
    /// <summary>What the cargo list takes up, at each item's own volume.</summary>
    public double CargoUsed => _e.Cargo.Sum(c => (_e.Data.TryType(c.TypeId, out var t) ? t.Attr(DogmaData.AttrVolume) ?? 0 : 0) * c.Quantity);
    public double DroneBandwidthUsed => _e.DroneStacks.Sum(d => _e.Value(d, "droneBandwidthUsed") * d.ActiveCount);

    /// <summary>The fighter bay: every squadron counts, in a tube or not.</summary>
    public double FighterBay     => Ship("fighterCapacity");
    public double FighterBayUsed => _e.Fighters.Sum(f => _e.Value(f, DogmaData.AttrVolume) * f.Count);
    public int FighterTubes      => (int)Ship("fighterTubes");
    public int FighterTubesUsed  => _e.Fighters.Count(f => f.ActiveCount > 0);
    /// <summary>Launch slots for squadrons of <paramref name="c"/>, and how many are in tubes.</summary>
    public int FighterSlots(FighterClass c) => (int)Ship(FighterAbilities.SlotAttribute(c));
    public int FighterSlotsUsed(FighterClass c) =>
        _e.Fighters.Count(f => f.ActiveCount > 0 && FighterAbilities.ClassOf(_e.Data, f.Type) == c);

    public int Slots(FitSlot slot) => (int)Ship(slot switch
    {
        FitSlot.High      => "hiSlots",
        FitSlot.Mid       => "medSlots",
        FitSlot.Low       => "lowSlots",
        FitSlot.Rig       => "rigSlots",
        FitSlot.Subsystem => "maxSubSystems",
        FitSlot.Service   => "serviceSlots",
        _                 => "",
    });
    public int SlotsUsed(FitSlot slot) => _e.Modules.Count(m => m.Slot == slot);
    public int TurretHardpoints   => (int)Ship("turretSlotsLeft");
    public int LauncherHardpoints => (int)Ship("launcherSlotsLeft");

    // ── Tank ────────────────────────────────────────────────────────────────────

    public LayerStats Shield => new(Ship("shieldCapacity"),
        Ship("shieldEmDamageResonance"), Ship("shieldThermalDamageResonance"),
        Ship("shieldKineticDamageResonance"), Ship("shieldExplosiveDamageResonance"));
    public LayerStats Armor => new(Ship("armorHP"),
        Ship("armorEmDamageResonance"), Ship("armorThermalDamageResonance"),
        Ship("armorKineticDamageResonance"), Ship("armorExplosiveDamageResonance"));
    public LayerStats Hull => new(Ship("hp"),
        Ship("emDamageResonance"), Ship("thermalDamageResonance"),
        Ship("kineticDamageResonance"), Ship("explosiveDamageResonance"));

    public double Ehp(DamageProfile? p = null)
    {
        p ??= DamageProfile.Uniform;
        return Shield.Ehp(p) + Armor.Ehp(p) + Hull.Ehp(p);
    }

    // ── Navigation and targeting ────────────────────────────────────────────────

    public double MaxVelocity => Ship("maxVelocity");
    public double Mass        => _e.Value(_e.Ship, DogmaData.AttrMass);
    public double Agility     => Ship("agility");
    /// <summary>Seconds to reach 75% of top speed, the point a ship can enter warp.</summary>
    public double AlignTime   => -Math.Log(0.25) * Agility * Mass / 1_000_000;
    public double Signature   => Ship("signatureRadius");
    public double WarpSpeed   => Ship("warpSpeedMultiplier") * Ship("baseWarpSpeed");
    /// <summary>Whether the ship can warp: nothing on it holding it (a doomsday firing) beyond what
    /// its warp core stabilizers outweigh.</summary>
    public bool   CanWarp     => Ship("warpScrambleStatus") <= 0;
    public double TargetRange => Ship("maxTargetRange");
    public double ScanResolution => Ship("scanResolution");
    /// <summary>The ship's limit and the pilot's, whichever is lower.</summary>
    public double MaxLockedTargets => Math.Min(Ship("maxLockedTargets"), _e.Value(_e.Character, "maxLockedTargets"));

    // ── Cycling modules ─────────────────────────────────────────────────────────

    /// <summary>A module's cycling effect — the one it runs when switched on — if it has one.</summary>
    private DogmaEffectInfo? CyclingEffect(DogmaItem item) =>
        item.Type.DefaultEffectId is { } id && _e.Data.Effects.TryGetValue(id, out var fx) ? fx : null;

    /// <summary>Seconds per cycle: the effect's duration attribute, plus any reactivation delay.</summary>
    private double CycleSeconds(DogmaItem item, DogmaEffectInfo fx) =>
        fx.DurationAttributeId is { } d
            ? (_e.Value(item, d) + _e.Value(item, "moduleReactivationDelay")) / 1000
            : 0;

    private IEnumerable<DogmaItem> ActiveModules =>
        _e.Modules.Where(m => m.Kind == DogmaItemKind.Module && m.State >= ModuleState.Active);

    // ── Capacitor ───────────────────────────────────────────────────────────────

    public double CapacitorCapacity => Ship("capacitorCapacity");

    /// <summary>Every active module's draw on the capacitor per cycle; a cap booster's is negative.</summary>
    public IReadOnlyList<CapacitorDrain> CapacitorDrains()
    {
        var drains = new List<CapacitorDrain>();
        foreach (var m in ActiveModules)
        {
            if (CyclingEffect(m) is not { } fx) continue;
            var cycle = CycleSeconds(m, fx);
            if (fx.Name == "powerBooster")
            {
                if (m.Charge is not null)
                    drains.Add(new CapacitorDrain(m.Type.Name, -_e.Value(m.Charge, "capacitorBonus"), cycle));
                continue;
            }
            // The effect names the attribute holding its cap cost; a few (compressors, jump portal
            // generators) do not, and the module's own capacitorNeed is the cost.
            var need = fx.DischargeAttributeId ?? _e.Data.AttrId("capacitorNeed");
            if (_e.Value(m, need) is var amount and not 0)
                drains.Add(new CapacitorDrain(m.Type.Name, amount, cycle));
        }
        return drains;
    }

    public CapacitorResult Capacitor() =>
        CapacitorSim.Run(CapacitorCapacity, Ship("rechargeRate"), CapacitorDrains());

    // ── Damage ──────────────────────────────────────────────────────────────────

    private DamageBreakdown DamageOf(DogmaItem item) => new(
        _e.Value(item, "emDamage"), _e.Value(item, "thermalDamage"),
        _e.Value(item, "kineticDamage"), _e.Value(item, "explosiveDamage"));

    /// <summary>
    /// Every weapon that is switched on and every launched drone stack. A turret's volley is its
    /// charge's damage times its damage multiplier; a launcher's, its missile's damage times the
    /// pilot's missile damage multiplier (where ballistic control systems act); a smartbomb's,
    /// its own damage; a drone stack's, one drone's damage times its multiplier times the number
    /// launched. Reloads are not counted.
    /// </summary>
    public IReadOnlyList<WeaponDamage> Weapons()
    {
        var list = new List<WeaponDamage>();
        foreach (var m in ActiveModules)
        {
            if (CyclingEffect(m) is not { } fx) continue;
            var cycle = CycleSeconds(m, fx);
            switch (fx.Name)
            {
                case "useMissiles" when m.Charge is not null:
                    list.Add(new WeaponDamage(m, WeaponKind.Missile,
                        DamageOf(m.Charge) * _e.Value(_e.Character, "missileDamageMultiplier"), cycle));
                    break;
                case "targetAttack" or "projectileFired" or "targetDisintegratorAttack" or "ChainLightning" when m.Charge is not null:
                    list.Add(new WeaponDamage(m, WeaponKind.Turret,
                        DamageOf(m.Charge) * _e.Value(m, "damageMultiplier"), cycle));
                    break;
                case "empWave":
                    list.Add(new WeaponDamage(m, WeaponKind.Smartbomb, DamageOf(m), cycle));
                    break;
            }
        }
        foreach (var d in _e.DroneStacks.Where(d => d.ActiveCount > 0))
        {
            if (CyclingEffect(d) is not { Name: "targetAttack" } fx) continue;
            list.Add(new WeaponDamage(d, WeaponKind.Drone,
                DamageOf(d) * (_e.Value(d, "damageMultiplier") * d.ActiveCount), CycleSeconds(d, fx)));
        }
        foreach (var f in _e.Fighters.Where(f => f.ActiveCount > 0))
            list.AddRange(FighterDamage(f));
        return list;
    }

    /// <summary>
    /// A squadron in a tube, one entry per damaging ability switched on. Every fighter in the
    /// squadron fires: an ability's volley is one fighter's damage, times its damage multiplier
    /// (where fighter skills, hull bonuses and drone damage amplifiers act), times the squadron's
    /// size. The standing attack and the secondary missiles keep their own damage attributes and
    /// durations; a bomb does its bomb type's damage; a kamikaze strike is a single blow.
    /// </summary>
    private IEnumerable<WeaponDamage> FighterDamage(DogmaItem f)
    {
        DamageBreakdown Named(string prefix) => new(
            _e.Value(f, prefix + "DamageEM"), _e.Value(f, prefix + "DamageTherm"),
            _e.Value(f, prefix + "DamageKin"), _e.Value(f, prefix + "DamageExp"));

        foreach (var a in FighterAbilities.Of(_e.Data, f.Type))
        {
            if (!a.DealsDamage || !f.Abilities.Contains(a.EffectId)) continue;
            var fx    = _e.Data.Effects[a.EffectId];
            var cycle = fx.DurationAttributeId is { } d ? _e.Value(f, d) / 1000 : 0;
            var one = a.Kind switch
            {
                FighterAbilityKind.Attack   => Named("fighterAbilityAttackMissile") * _e.Value(f, "fighterAbilityAttackMissileDamageMultiplier"),
                FighterAbilityKind.Missiles => Named("fighterAbilityMissiles") * _e.Value(f, "fighterAbilityMissilesDamageMultiplier"),
                FighterAbilityKind.Bomb     => BombDamage(f),
                _                           => Named("fighterAbilityKamikaze"),
            };
            if (a.Kind == FighterAbilityKind.Kamikaze) cycle = 0;
            yield return new WeaponDamage(f, WeaponKind.Fighter, one * f.ActiveCount, cycle, a.Label, a);
        }
    }

    private DamageBreakdown BombDamage(DogmaItem f)
    {
        var id = (int)_e.Value(f, "fighterAbilityLaunchBombType");
        if (id <= 0 || !_e.Data.TryType(id, out var bomb)) return DamageBreakdown.Zero;
        double Of(string attr) => bomb.Attr(_e.Data.AttrId(attr)) ?? 0;
        return new(Of("emDamage"), Of("thermalDamage"), Of("kineticDamage"), Of("explosiveDamage"));
    }

    // ── Repair and regeneration ─────────────────────────────────────────────────

    /// <summary>Seconds for the shield to recharge from empty to full, as the game quotes it.</summary>
    public double ShieldRechargeSeconds => Ship("shieldRechargeRate") / 1000;

    /// <summary>A recharge time this long is the SDE's way of saying "never": Upwell structures carry
    /// 999,999,999,999 ms. Anything past a year is treated as no passive recharge at all.</summary>
    private const double NeverRechargesSeconds = 365 * 24 * 3600;
    public bool HasPassiveShieldRecharge => ShieldRechargeSeconds is > 0 and < NeverRechargesSeconds;

    /// <summary>An Upwell structure rather than a ship: it does not move, and its holds have no capacity in the SDE.</summary>
    public bool IsStructure => _e.Ship.Type.CategoryId == DogmaData.CategoryStructure;

    /// <summary>
    /// Shield regeneration at its peak, in HP/s. Shields recharge along the same curve as the
    /// capacitor, fastest at 25%: 2.5 × capacity ÷ recharge time. This is the number a passive
    /// shield tank is built around.
    /// </summary>
    public double PassiveShieldRegen => HasPassiveShieldRecharge ? 2.5 * Ship("shieldCapacity") / ShieldRechargeSeconds : 0;

    /// <summary>
    /// Every active repair module: shield boosters, armor and hull repairers, and their ancillary
    /// forms. HP per cycle and cycle time; an ancillary armor repairer with nanite paste loaded
    /// repairs its charged multiple.
    /// </summary>
    public IReadOnlyList<RepairModule> Repairs()
    {
        var list = new List<RepairModule>();
        foreach (var m in ActiveModules)
        {
            if (CyclingEffect(m) is not { } fx) continue;
            var cycle = CycleSeconds(m, fx);
            switch (fx.Name)
            {
                case "shieldBoosting" or "fueledShieldBoosting":
                    list.Add(new RepairModule(m, TankLayer.Shield, _e.Value(m, "shieldBonus"), cycle));
                    break;
                case "armorRepair":
                    list.Add(new RepairModule(m, TankLayer.Armor, _e.Value(m, "armorDamageAmount"), cycle));
                    break;
                case "fueledArmorRepair":
                    var paste = m.Charge is not null ? _e.Value(m, "chargedArmorDamageMultiplier") : 1;
                    list.Add(new RepairModule(m, TankLayer.Armor, _e.Value(m, "armorDamageAmount") * (paste > 0 ? paste : 1), cycle));
                    break;
                case "structureRepair":
                    list.Add(new RepairModule(m, TankLayer.Hull, _e.Value(m, "structureDamageAmount"), cycle));
                    break;
            }
        }
        return list;
    }

    /// <summary>Raw HP/s repaired per layer by <paramref name="repairs"/>, and shield regeneration at its peak.</summary>
    public TankRates Tank(IReadOnlyList<RepairModule>? repairs = null)
    {
        repairs ??= Repairs();
        double Sum(TankLayer l) => repairs.Where(r => r.Layer == l).Sum(r => r.PerSecond);
        return new TankRates(PassiveShieldRegen, Sum(TankLayer.Shield), Sum(TankLayer.Armor), Sum(TankLayer.Hull));
    }

    public DamageBreakdown WeaponDps(IReadOnlyList<WeaponDamage>? weapons = null) =>
        (weapons ?? Weapons()).Where(w => w.Kind is not (WeaponKind.Drone or WeaponKind.Fighter)).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Dps);
    /// <summary>
    /// Fighter damage per second over a whole sortie, rearming included. A squadron fights until
    /// the charges of the abilities it has switched on are spent — its standing attack firing all
    /// the while — then returns to its tube, rearms every charge it spent and refuels, and goes
    /// again. Damage over the fight divided by fight plus turnaround. The flight out and back is
    /// not counted, so this is the most a squadron can keep up, not what a fight at range sees.
    /// A squadron with no charged ability switched on never needs to come back: its figure is its DPS.
    /// </summary>
    public double FighterSustainedDps(IReadOnlyList<WeaponDamage>? weapons = null)
    {
        var total = 0.0;
        foreach (var squadron in (weapons ?? Weapons()).Where(w => w.Kind == WeaponKind.Fighter && w.CycleSeconds > 0).GroupBy(w => w.Item))
        {
            var charged = squadron.Where(w => w.Ability?.Charges is not null).ToList();
            if (charged.Count == 0) { total += squadron.Sum(w => w.Dps.Total); continue; }
            var fight  = charged.Max(w => w.Ability!.Charges!.Value * w.CycleSeconds);
            var damage = squadron.Sum(w => w.Ability?.Charges is { } n ? w.Volley.Total * n : w.Dps.Total * fight);
            var rearm  = charged.Max(w => w.Ability!.Charges!.Value * (w.Ability.Game?.RearmSeconds ?? 0));
            var refuel = _e.Value(squadron.Key, "fighterRefuelingTime") / 1000;
            total += damage / (fight + rearm + refuel);
        }
        return total;
    }

    public DamageBreakdown FighterDps(IReadOnlyList<WeaponDamage>? weapons = null) =>
        (weapons ?? Weapons()).Where(w => w.Kind == WeaponKind.Fighter).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Dps);
    public DamageBreakdown DroneDps(IReadOnlyList<WeaponDamage>? weapons = null) =>
        (weapons ?? Weapons()).Where(w => w.Kind == WeaponKind.Drone).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Dps);
    public DamageBreakdown Volley(IReadOnlyList<WeaponDamage>? weapons = null) =>
        (weapons ?? Weapons()).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Volley);
}
