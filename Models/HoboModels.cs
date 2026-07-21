namespace EveConsole.Models;

// Single-row metadata saved after each successful Hoboleaks import.
public class HoboBuildInfo
{
    public int            Id         { get; set; } = 1;
    public DateTimeOffset ImportedAt { get; set; }
}

// hoboleaks blueprints.json — mirrors SdeBlueprint* but sourced from Hoboleaks
// and updated faster (includes new ships before the official SDE catches up).
public class HoboBlueprint
{
    public int TypeId             { get; set; }
    public int MaxProductionLimit { get; set; }
}

public class HoboBlueprintActivity
{
    public int    TypeId   { get; set; }
    public string Activity { get; set; } = "";
    public int    Time     { get; set; }
}

public class HoboBlueprintMaterial
{
    public int    TypeId         { get; set; }
    public string Activity       { get; set; } = "";
    public int    MaterialTypeId { get; set; }
    public int    Quantity       { get; set; }
}

public class HoboBlueprintProduct
{
    public int    TypeId        { get; set; }
    public string Activity      { get; set; } = "";
    public int    ProductTypeId { get; set; }
    public int    Quantity      { get; set; }
    public double Probability   { get; set; }
}

public class HoboBlueprintSkill
{
    public int    TypeId      { get; set; }
    public string Activity    { get; set; } = "";
    public int    SkillTypeId { get; set; }
    public int    Level       { get; set; }
}

// hoboleaks typematerials.json — reprocessing yields
public class HoboTypeMaterial
{
    public int TypeId         { get; set; }
    public int MaterialTypeId { get; set; }
    public int Quantity       { get; set; }
}

// hoboleaks repackagedvolumes.json — typeId → repackaged volume
public class HoboRepackagedVolume
{
    public int    TypeId { get; set; }
    public double Volume { get; set; }
}

// hoboleaks compressibletypes.json — sourceTypeId → compressed typeId
public class HoboCompressibleType
{
    public int SourceTypeId     { get; set; }
    public int CompressedTypeId { get; set; }
}
