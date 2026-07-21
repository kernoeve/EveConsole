namespace EveConsole.Models;

public class MarketLevelCollection
{
    public int    Id   { get; set; }
    public string Name { get; set; } = "";
}

public class MarketLevelGroup
{
    public int     Id              { get; set; }
    public string  Name            { get; set; } = "";
    public int?    CollectionId    { get; set; }
    public long    StationId       { get; set; }
    public string  StationName     { get; set; } = "";
    public int?    MarketSourceId  { get; set; }
    public double? MaxPriceOverPct { get; set; }
    public int     Multiplier      { get; set; } = 1;
}

public class MarketLevelItem
{
    public int Id             { get; set; }
    public int GroupId        { get; set; }
    public int TypeId         { get; set; }
    public int TargetQuantity { get; set; }
}
