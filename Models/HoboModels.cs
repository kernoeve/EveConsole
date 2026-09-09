namespace EveConsole.Models;

// Single-row metadata saved after each successful Hoboleaks import.
public class HoboBuildInfo
{
    public int            Id         { get; set; } = 1;
    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>
    /// Which Hoboleaks publication this data came from, so the app can say whether there is a
    /// newer one.
    /// </summary>
    /// <remarks>
    /// ⚠️ Zero on every database written before this existed, and on any import that predates it.
    /// Treated as "unknown" rather than "revision zero": an update check must not claim you are
    /// behind, or up to date, on the strength of a number nobody ever wrote.
    /// </remarks>
    public long Revision { get; set; }
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
