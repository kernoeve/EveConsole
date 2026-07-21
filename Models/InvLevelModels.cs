namespace EveConsole.Models;

// ── Persistence models ────────────────────────────────────────────────────────

public class InvLevelCollection
{
    public int    Id   { get; set; }
    public string Name { get; set; } = "";
}

public class InvLevelGroup
{
    public int    Id                     { get; set; }
    public string Name                   { get; set; } = "";
    public int    Multiplier             { get; set; } = 1;
    public int?   CollectionId           { get; set; }

    // Scope: "Station" | "System" | "Region" | "Everywhere"
    public string Scope                  { get; set; } = "Everywhere";
    public long?  LocationId             { get; set; }
    public string LocationName           { get; set; } = "";

    // Include flags: which data sources to sum for "Available"
    public bool   IncludeAssets          { get; set; } = true;
    public bool   IncludeIndustryJobs    { get; set; } = true;
    public bool   IncludeMarketBuyOrders { get; set; } = true;
    public bool   IncludeContractsBuying { get; set; }
}

public class InvLevelItem
{
    public int Id             { get; set; }
    public int GroupId        { get; set; }
    public int TypeId         { get; set; }
    public int TargetQuantity { get; set; } = 1;
}
