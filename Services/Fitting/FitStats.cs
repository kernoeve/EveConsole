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
}
