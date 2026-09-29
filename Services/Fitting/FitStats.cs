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

public enum WeaponKind { Turret, Missile, Smartbomb, Drone }

/// <summary>One weapon (or drone stack): what one volley does and how often it fires.</summary>
public sealed record WeaponDamage(DogmaItem Item, WeaponKind Kind, DamageBreakdown Volley, double CycleSeconds)
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
    public double DroneBayUsed => _e.Drones.Sum(d => _e.Value(d, DogmaData.AttrVolume) * d.Count);
    public double DroneBandwidth => Ship("droneBandwidth");
    public double CargoCapacity => _e.Value(_e.Ship, DogmaData.AttrCapacity);
    /// <summary>What the cargo list takes up, at each item's own volume.</summary>
    public double CargoUsed => _e.Cargo.Sum(c => (_e.Data.TryType(c.TypeId, out var t) ? t.Attr(DogmaData.AttrVolume) ?? 0 : 0) * c.Quantity);
    public double DroneBandwidthUsed => _e.Drones.Sum(d => _e.Value(d, "droneBandwidthUsed") * d.ActiveCount);

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
        foreach (var d in _e.Drones.Where(d => d.ActiveCount > 0))
        {
            if (CyclingEffect(d) is not { Name: "targetAttack" } fx) continue;
            list.Add(new WeaponDamage(d, WeaponKind.Drone,
                DamageOf(d) * (_e.Value(d, "damageMultiplier") * d.ActiveCount), CycleSeconds(d, fx)));
        }
        return list;
    }

    // ── Repair and regeneration ─────────────────────────────────────────────────

    /// <summary>Seconds for the shield to recharge from empty to full, as the game quotes it.</summary>
    public double ShieldRechargeSeconds => Ship("shieldRechargeRate") / 1000;

    /// <summary>
    /// Shield regeneration at its peak, in HP/s. Shields recharge along the same curve as the
    /// capacitor, fastest at 25%: 2.5 × capacity ÷ recharge time. This is the number a passive
    /// shield tank is built around.
    /// </summary>
    public double PassiveShieldRegen => ShieldRechargeSeconds > 0 ? 2.5 * Ship("shieldCapacity") / ShieldRechargeSeconds : 0;

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
        (weapons ?? Weapons()).Where(w => w.Kind != WeaponKind.Drone).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Dps);
    public DamageBreakdown DroneDps(IReadOnlyList<WeaponDamage>? weapons = null) =>
        (weapons ?? Weapons()).Where(w => w.Kind == WeaponKind.Drone).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Dps);
    public DamageBreakdown Volley(IReadOnlyList<WeaponDamage>? weapons = null) =>
        (weapons ?? Weapons()).Aggregate(DamageBreakdown.Zero, (a, w) => a + w.Volley);
}
