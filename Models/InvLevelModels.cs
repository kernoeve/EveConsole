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

    /// <summary>Units still listed on our own open market SELL orders. Off by default: listed stock
    /// is on its way out, and counted as on hand a level would not restock until it sold.</summary>
    public bool   IncludeMarketSellOrders { get; set; }

    /// <summary>Items our own outstanding contracts hand over — contracts we are selling through.
    /// Off by default, for the same reason as sell orders.</summary>
    public bool   IncludeContractsSelling { get; set; }

    /// <summary>
    /// Count only PACKAGED items, skipping assembled and fitted hulls.
    ///
    /// <para>Off by default, because most groups are stocking material and a rule that quietly
    /// ignored half a hangar would be worse than one that counted a flown ship.</para>
    ///
    /// <para>⚠️ Never applied to blueprints. Singleton on a blueprint does not mean "assembled",
    /// it means the item does not stack — which is true of every copy and every researched
    /// original. Filtering on it would empty a blueprint group outright.</para>
    /// </summary>
    public bool   PackagedOnly           { get; set; }
}

public class InvLevelItem
{
    public int Id             { get; set; }
    public int GroupId        { get; set; }
    public int TypeId         { get; set; }
    public int TargetQuantity { get; set; } = 1;
}
