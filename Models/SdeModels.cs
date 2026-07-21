namespace EveConsole.Models;

// Metadata row saved at the end of each successful SDE import (always Id = 1).
public class SdeBuildInfo
{
    public int            Id          { get; set; } = 1;
    public int            BuildNumber { get; set; }
    public DateTimeOffset ReleaseDate { get; set; }
    public DateTimeOffset ImportedAt  { get; set; }
}

// -----------------------------------------------------------------------
// SDE entities — all prefixed Sde, no FK constraints between them so
// the import service can insert in any order.
// -----------------------------------------------------------------------

public class SdeCategory
{
    public int    CategoryId { get; set; }
    public string Name       { get; set; } = "";
    public bool   Published  { get; set; }
}

public class SdeGroup
{
    public int    GroupId    { get; set; }
    public int    CategoryId { get; set; }
    public string Name       { get; set; } = "";
    public bool   Published  { get; set; }
    public bool   Anchorable { get; set; }
    public bool   Anchored   { get; set; }
}

public class SdeType
{
    public int     TypeId        { get; set; }
    public int     GroupId       { get; set; }
    public string  Name          { get; set; } = "";
    public string  Description   { get; set; } = "";
    public double  Volume        { get; set; }
    public double  Mass          { get; set; }
    public double  Capacity      { get; set; }
    public int     PortionSize   { get; set; }
    public double? BasePrice     { get; set; }
    public int?    MarketGroupId { get; set; }
    public int?    IconId        { get; set; }
    public int?    GraphicId     { get; set; }
    public int?    FactionId     { get; set; }
    public int?    RaceId        { get; set; }
    public int?    MetaGroupId   { get; set; }
    public bool    Published     { get; set; }
}

public class SdeMarketGroup
{
    public int     MarketGroupId { get; set; }
    public int?    ParentGroupId { get; set; }
    public string  Name         { get; set; } = "";
    public string  Description  { get; set; } = "";
    public int?    IconId       { get; set; }
    public bool    HasTypes     { get; set; }
}

public class SdeDogmaAttributeCategory
{
    public int    CategoryId { get; set; }
    public string Name       { get; set; } = "";
}

public class SdeDogmaAttribute
{
    public int     AttributeId  { get; set; }
    public string  Name         { get; set; } = "";
    public string  DisplayName  { get; set; } = "";
    public int?    CategoryId   { get; set; }
    public double  DefaultValue { get; set; }
    public bool    HighIsGood   { get; set; }
    public bool    Stackable    { get; set; }
    public int?    UnitId       { get; set; }
    public bool    Published    { get; set; }
}

public class SdeDogmaEffect
{
    public int    EffectId     { get; set; }
    public string Name         { get; set; } = "";
    public string DisplayName  { get; set; } = "";
    public string Description  { get; set; } = "";
    public bool   IsOffensive  { get; set; }
    public bool   IsAssistance { get; set; }
    public bool   Published    { get; set; }
}

public class SdeTypeDogmaAttribute
{
    public int    TypeId      { get; set; }
    public int    AttributeId { get; set; }
    public double Value       { get; set; }
}

public class SdeTypeDogmaEffect
{
    public int  TypeId    { get; set; }
    public int  EffectId  { get; set; }
    public bool IsDefault { get; set; }
}

public class SdeBlueprint
{
    public int TypeId             { get; set; }
    public int MaxProductionLimit { get; set; }
}

public class SdeBlueprintMaterial
{
    public int    TypeId         { get; set; }  // blueprint typeID
    public string Activity       { get; set; } = "";
    public int    MaterialTypeId { get; set; }
    public int    Quantity       { get; set; }
}

public class SdeBlueprintProduct
{
    public int    TypeId        { get; set; }  // blueprint typeID
    public string Activity      { get; set; } = "";
    public int    ProductTypeId { get; set; }
    public int    Quantity      { get; set; }
    public double Probability   { get; set; }
}

public class SdeBlueprintSkill
{
    public int    TypeId      { get; set; }  // blueprint typeID
    public string Activity    { get; set; } = "";
    public int    SkillTypeId { get; set; }
    public int    Level       { get; set; }
}

public class SdeRegion
{
    public int    RegionId   { get; set; }
    public string Name       { get; set; } = "";
    public int?   FactionId  { get; set; }
    public bool   IsWormhole { get; set; }
}

public class SdeConstellation
{
    public int    ConstellationId { get; set; }
    public int    RegionId        { get; set; }
    public string Name            { get; set; } = "";
    public bool   IsWormhole      { get; set; }
}

public class SdeSolarSystem
{
    public int    SolarSystemId   { get; set; }
    public int    ConstellationId { get; set; }
    public int    RegionId        { get; set; }
    public string Name            { get; set; } = "";
    public double Security        { get; set; }
    public int?   FactionId       { get; set; }
    public bool   IsWormhole      { get; set; }
}

public class SdeStargate
{
    public int StargateId            { get; set; }
    public int SolarSystemId         { get; set; }
    public int DestinationStargateId { get; set; }
}

public class SdeStation
{
    public int    StationId              { get; set; }
    public string Name                   { get; set; } = "";
    public int    SolarSystemId          { get; set; }
    public int    ConstellationId        { get; set; }
    public int    RegionId               { get; set; }
    public int?   CorporationId         { get; set; }
    public int?   StationTypeId         { get; set; }
    public double Security               { get; set; }
    public double ReprocessingEfficiency { get; set; }
    public double ReprocessingTax        { get; set; }
}

public class SdeFaction
{
    public int    FactionId              { get; set; }
    public string Name                   { get; set; } = "";
    public string Description            { get; set; } = "";
    public int?   CorporationId         { get; set; }
    public int?   MilitiaCorporationId  { get; set; }
    public int?   SolarSystemId         { get; set; }
}

public class SdeNpcCorporation
{
    public int    CorporationId { get; set; }
    public string Name         { get; set; } = "";
    public int?   FactionId    { get; set; }
}

public class SdeRace
{
    public int    RaceId      { get; set; }
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
}

public class SdeMetaGroup
{
    public int    MetaGroupId { get; set; }
    public string Name        { get; set; } = "";
}

public class SdeCertificate
{
    public int    CertificateId { get; set; }
    public int    GroupId       { get; set; }
    public string Name          { get; set; } = "";
    public string Description   { get; set; } = "";
}

// fsd/typeMaterials.yaml — reprocessing outputs
public class SdeTypeMaterial
{
    public int TypeId         { get; set; }
    public int MaterialTypeId { get; set; }
    public int Quantity       { get; set; }
}

// fsd/planetSchematics.yaml — PI production chains
public class SdePlanetSchematic
{
    public int    SchematicId { get; set; }
    public string Name        { get; set; } = "";
    public int    CycleTime   { get; set; }
}

public class SdePlanetSchematicType
{
    public int  SchematicId { get; set; }
    public int  TypeId      { get; set; }
    public bool IsInput     { get; set; }
    public int  Quantity    { get; set; }
}

// dogmaUnits.yaml
public class SdeDogmaUnit
{
    public int    UnitId      { get; set; }
    public string Name        { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

// icons.yaml
public class SdeIcon
{
    public int    IconId   { get; set; }
    public string IconFile { get; set; } = "";
}

// graphics.yaml
public class SdeGraphic
{
    public int     GraphicId   { get; set; }
    public string? GraphicFile { get; set; }
}

// skins.yaml
public class SdeSkin
{
    public int    SkinId            { get; set; }
    public string InternalName      { get; set; } = "";
    public int?   SkinMaterialId    { get; set; }
    public bool   VisibleTranquility { get; set; }
}

public class SdeSkinType
{
    public int SkinId { get; set; }
    public int TypeId { get; set; }
}

// skinLicenses.yaml
public class SdeSkinLicense
{
    public int LicenseTypeId { get; set; }
    public int SkinId        { get; set; }
    public int Duration      { get; set; }
}
