namespace EveConsole.Models;

public class IndyPark
{
    public int    Id        { get; set; }
    public string Name      { get; set; } = "New Park";
    public bool   IsDefault { get; set; }
}

public class IndyStructure
{
    public int     Id               { get; set; }
    public int     ParkId           { get; set; }
    public string  DisplayName      { get; set; } = "";
    public string  StructureTypeKey { get; set; } = "raitaru";
    public string  SystemName       { get; set; } = "";
    public string  SecurityClass    { get; set; } = "nullsec";
    public decimal FacilityTax      { get; set; } = 1m; // percent (1 = 1%, stored as-is, divide by 100 when calculating)

    /// <summary>
    /// Optional link to the real in-game structure this models.
    ///
    /// Parks are otherwise hypothetical — a planned loadout used to cost jobs. Linking
    /// one to an actual facility lets a running industry job be checked against the
    /// rigs configured here, which is the only route to knowing a structure's rigs at
    /// all: ESI exposes no structure-fitting endpoint.
    ///
    /// Null means unlinked, and a job at an unlinked facility is reported as unknown
    /// rather than unrigged.
    /// </summary>
    public long?   RealStructureId   { get; set; }
    public string  RealStructureName { get; set; } = "";
}

public class IndyStructureRig
{
    public int Id          { get; set; }
    public int StructureId { get; set; }
    public int SlotIndex   { get; set; }
    public int RigTypeId   { get; set; }
}

public class IndyCategoryAssignment
{
    public int    Id          { get; set; }
    public int    ParkId      { get; set; }
    public string CategoryKey { get; set; } = "";
    public int?   StructureId { get; set; }
}

public class IndyItemException
{
    public int    Id          { get; set; }
    public int    ParkId      { get; set; }
    public int    TypeId      { get; set; }
    public string TypeName    { get; set; } = "";
    public int?   StructureId { get; set; }
}
