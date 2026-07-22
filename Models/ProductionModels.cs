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
    public string  StructureDisplay => StructureName.Length > 0 && SystemName.Length > 0
        ? $"{StructureName} @ {SystemName}"
        : StructureName.Length > 0 ? StructureName : SystemName;
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
}

public class PlanRawMaterial
{
    public int     TypeId    { get; set; }
    public string  TypeName  { get; set; } = "";
    public int     Quantity  { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalCost { get; set; }
}

public class PlanIntermediate
{
    public int     TypeId           { get; set; }
    public string  TypeName         { get; set; } = "";
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
    public decimal TotalRawMaterialCost { get; set; }
    public decimal TotalJobCost         { get; set; }
    public decimal TotalLeftoverValue   { get; set; }
    public decimal NetCost              { get; set; }
}
