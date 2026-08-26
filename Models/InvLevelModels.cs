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

    /// <summary>
    /// Something sold or flown, rather than an input held so the next thing can be built.
    ///
    /// <para>⚠️ A count of blocked work cannot tell the two apart, and the difference is the
    /// whole value of the job. Nanotransistors blocks eleven tasks and ten of them are component
    /// buffers refilling themselves — real work, but work whose only customer is the shelf it
    /// came from. The isotropic blocking a Neurolink cell blocks every standard capital hull.
    /// Both count as "blocking eleven".</para>
    ///
    /// <para>Off by default and set by hand: what counts as final is a business decision, not a
    /// property of the item. For one operation it is hulls, for another it is rigs or modules.</para>
    /// </summary>
    public bool IsFinalProduct { get; set; }
}
