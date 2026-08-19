namespace EveConsole.Models;

public class ProductionQueueEntry
{
    public int    TypeId   { get; set; }
    public string TypeName { get; set; } = "";
    public int    Quantity { get; set; } = 1;
    public int    MeLevel  { get; set; } = 10;
}

public class PlanJob
{
    public int     OutputTypeId    { get; set; }
    public string  OutputTypeName  { get; set; } = "";
    public bool    IsReaction      { get; set; }
    public int     MeLevel         { get; set; }
    public int     QuantityNeeded  { get; set; }
    public int     QuantityPerRun  { get; set; }
    public int     Runs            { get; set; }
    public int     QuantityProduced => Runs * QuantityPerRun;
    public int     Leftover         => QuantityProduced - QuantityNeeded;
    public string  StructureName   { get; set; } = "";
    public string  SystemName      { get; set; } = "";

    /// <summary>Real in-game facility this job's park structure is linked to, if the
    /// user has set one (Indy Parks → Actual facility). Null means stock here cannot
    /// be counted, which is reported as unknown rather than as everything missing.</summary>
    public long?   StationId       { get; set; }
    public string  StationName     { get; set; } = "";
    /// <summary>The system the facility sits in, so its name can open the map. Zero when the park
    /// structure names a system the SDE does not know — possible for a hand-typed entry.</summary>
    public int     SolarSystemId   { get; set; }
    public string  StructureDisplay => StructureName.Length > 0 && SystemName.Length > 0
        ? $"{StructureName} @ {SystemName}"
        : StructureName.Length > 0 ? StructureName : SystemName;

    // ── Links on the Jobs tab ─────────────────────────────────────────────────
    //
    // The structure line reads "Name @ System". Both halves point somewhere: the facility at the
    // Structure Browser, the system at the map. Split here rather than in the view so the display
    // string and the two links cannot drift apart.
    public bool HasStationLink => StationId is > 0 && StructureName.Length > 0;
    public bool HasSystemLink  => SolarSystemId > 0 && SystemName.Length   > 0;
    public bool ShowStationPlain => StructureName.Length > 0 && !HasStationLink;
    public bool ShowSystemPlain  => SystemName.Length    > 0 && !HasSystemLink;
    /// <summary>Shown between the two only when both are present, so a job with one of them does
    /// not read as "Name @" or "@ System".</summary>
    public bool ShowAtSeparator => StructureName.Length > 0 && SystemName.Length > 0;

    public void OpenStation() => EveConsole.Services.EntityNavigator.Instance.Structure(StationId ?? 0);
    public void OpenSystem()  => EveConsole.Services.EntityNavigator.Instance.System(SolarSystemId);
    public List<PlanJobMaterial> Materials      { get; set; } = [];
    public List<int>             ChildTypeIds   { get; set; } = [];
    public List<int>             ParentTypeIds  { get; set; } = [];
    public bool    IsFinalProduct  { get; set; }
    public decimal MaterialCost    { get; set; }
    public decimal JobCost         { get; set; }

    // Material efficiency modifiers applied to this job (for UI debugging/verification)
    public double MeReductionPct { get; set; }   // e.g. 10.0 for ME10
    public double RigBonusPct    { get; set; }   // e.g. 4.18 for T1 ME rig in highsec
    public double RoleBonusPct   { get; set; }   // e.g. 1.0 for engineering complex
    public double CombinedFactor { get; set; }   // final multiplier = (1-me%)×(1-rig%)×(1-role%)
    public string ModifierDisplay =>
        $"ME -{MeReductionPct:F0}%  Rig -{RigBonusPct:F2}%  Structure -{RoleBonusPct:F1}%  → ×{CombinedFactor:F4}";
}

public class PlanJobMaterial
{
    public int     MaterialTypeId { get; set; }
    public string  TypeName       { get; set; } = "";
    public int     BaseQtyPerRun  { get; set; }
    public int     EffQtyPerRun   { get; set; }
    public int     TotalQty       { get; set; }
    public bool    IsBought       { get; set; }
    public decimal UnitPrice      { get; set; }
    public decimal TotalCost      => IsBought ? TotalQty * UnitPrice : 0;
    public string  Source         => IsBought ? "Buy" : "Build";
    // Full formula string for UI debugging, e.g. "ceil(2,631 × 0.8536) = 2,247"
    public string  FormulaDisplay { get; set; } = "";

    // ── Stock at the job's station ───────────────────────────────────────────
    // Per job and independent of other jobs: two jobs in the same structure both
    // compare against the full pile, because the question here is "can I start this
    // one". The Raw Materials tab is where competing demand is summed.

    /// <summary>False when the job's park structure has no linked facility, so there
    /// is no station whose stock could be counted.</summary>
    public bool AvailabilityKnown { get; set; }

    /// <summary>Units of this material already at the job's station.</summary>
    public int  Available         { get; set; }

    public int  Missing => AvailabilityKnown ? Math.Max(0, TotalQty - Available) : 0;

    /// <summary>Em dash when unknown — a blank cell reads as "none missing", which is
    /// the opposite of what an unlinked structure means.</summary>
    public string MissingDisplay => AvailabilityKnown ? Missing.ToString("N0") : "—";

    /// <summary>Orange only for a real shortfall. Zero and unknown stay muted so the
    /// colour means "act on this" rather than "this column exists".</summary>
    public string MissingColor => AvailabilityKnown && Missing > 0 ? "#e0902e" : "#555566";
}

/// <summary>
/// Raw material row. Notifies on the availability fields alone: the missing mode toggle
/// recomputes them in place on a plan that is already on screen, and reassigning the plan
/// to force a rebind would collapse the Jobs tree the user had expanded.
/// </summary>
public class PlanRawMaterial : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public int     TypeId    { get; set; }
    public string  TypeName  { get; set; } = "";

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public int     Quantity  { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalCost { get; set; }

    // ── Availability ─────────────────────────────────────────────────────────
    // Unlike a job row, this is summed demand: every job needing this material
    // competes for the same stock. In station mode the sum is taken per structure —
    // ten jobs in one Raitaru draw on one pile — and the shortfalls are then added
    // up. In asset mode it is compared against everything owned, anywhere.

    private bool _availabilityKnown;
    private int  _available;
    private int  _missing;

    public bool AvailabilityKnown
    {
        get => _availabilityKnown;
        set
        {
            _availabilityKnown = value;
            Raise(nameof(AvailabilityKnown)); Raise(nameof(MissingDisplay)); Raise(nameof(MissingColor));
        }
    }

    public int Available
    {
        get => _available;
        set { _available = value; Raise(nameof(Available)); }
    }

    public int Missing
    {
        get => _missing;
        set { _missing = value; Raise(nameof(Missing)); Raise(nameof(MissingDisplay)); Raise(nameof(MissingColor)); }
    }

    public string MissingDisplay => AvailabilityKnown ? Missing.ToString("N0") : "—";
    public string MissingColor   => AvailabilityKnown && Missing > 0 ? "#e0902e" : "#555566";
}

public class PlanIntermediate
{
    public int     TypeId           { get; set; }
    public string  TypeName         { get; set; } = "";

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public int     QuantityNeeded   { get; set; }
    public int     QuantityProduced { get; set; }
    public int     Leftover         { get; set; }
    public decimal MarketUnitPrice  { get; set; }
    public decimal LeftoverValue    { get; set; }
}

public class PlanFinalProduct
{
    public int     TypeId            { get; set; }
    public string  TypeName          { get; set; } = "";

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public int     QuantityRequested { get; set; }
    public int     QuantityProduced  { get; set; }
    public int     MeLevel           { get; set; }
    public decimal TotalMaterialCost { get; set; }
    public decimal TotalJobCost      { get; set; }
    public decimal TotalCost         { get; set; }
    public decimal UnitCost          { get; set; }
    public decimal MarketUnitPrice   { get; set; }
    public decimal MarketTotalValue  { get; set; }
    public decimal Profit            => MarketTotalValue - TotalCost;
    public decimal ProfitMargin      => MarketTotalValue > 0 ? Profit / MarketTotalValue * 100m : 0m;
}

public class PlanLeftoverItem
{
    public int     TypeId    { get; set; }
    public string  TypeName  { get; set; } = "";

    public bool HasItemLink => TypeId > 0 && TypeName.Length > 0;
    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public int     Quantity  { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalValue { get; set; }
    public string  Source    { get; set; } = "";
}

public class ProductionPlan
{
    public List<PlanJob>          AllJobs       { get; set; } = [];
    public List<int>              RootTypeIds   { get; set; } = [];
    public List<PlanRawMaterial>  RawMaterials  { get; set; } = [];
    public List<PlanIntermediate> Intermediates { get; set; } = [];
    public List<PlanFinalProduct> FinalProducts { get; set; } = [];
    public List<PlanLeftoverItem> Leftovers     { get; set; } = [];
    /// <summary>
    /// Items no category assignment covered. They were planned against the park's catch-all
    /// facility with no rig bonus rather than aborting the calculation, so the plan is
    /// complete but these figures carry no rig benefit they might be entitled to.
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    public decimal TotalRawMaterialCost { get; set; }
    public decimal TotalJobCost         { get; set; }
    public decimal TotalLeftoverValue   { get; set; }
    public decimal NetCost              { get; set; }
}
