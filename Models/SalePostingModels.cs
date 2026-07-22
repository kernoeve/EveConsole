namespace EveConsole.Models;

// ── Sale Posting ───────────────────────────────────────────────────────────────
// Craft one or more sale postings (to paste into Slack/Discord/forums). A Posting
// owns one or more Sections, and each Section owns Items. The Posting holds the
// inventory location scope (for In Stock / In Build), the pricing rule, and which
// quantities to surface. Mirrors the Inventory Levels shape (Collection→Group→Item),
// but the rich config lives on the top-level Posting rather than the middle level.

public class SalePosting
{
    public int    Id   { get; set; }
    public string Name { get; set; } = "";

    // Inventory location scope — drives the computed In Stock (assets) and In Build
    // (industry jobs) quantities. Same vocabulary as InvLevelGroup.
    public string Scope        { get; set; } = "Everywhere"; // "Station"|"System"|"Region"|"Everywhere"
    public long?  LocationId   { get; set; }
    public string LocationName { get; set; } = "";

    // Pricing rule for the Sale Price column.
    public string  PricingBasis      { get; set; } = "Build";  // "Build" | "Contract" | "Market"
    public double  PricePercent      { get; set; } = 110;      // % of the basis value used as sale price
    // "Market" basis: current price at a specific station (one the app polls current orders for),
    // using the chosen price type.
    public long?   MarketStationId   { get; set; }
    public string  MarketStationName { get; set; } = "";
    public string  MarketPriceType   { get; set; } = "Sell";   // "Buy" | "Midpoint" | "Sell"

    // Which quantities to surface in the generated posting text.
    public bool ShowInStock  { get; set; } = true;
    public bool ShowInBuild  { get; set; } = true;
    public bool ShowReserved { get; set; } = true;

    // When set, include the earliest job completion date for an item that is out of stock
    // (In Stock = 0) but has at least one in build (In Build ≥ 1) — i.e. "none now, ready ~<date>".
    public bool IncludeCompletionDate { get; set; }

    // When set, only packaged (non-singleton) assets count toward In Stock — assembled/fitted
    // hulls are skipped, so your personal ships don't show as sale stock.
    public bool OnlyPackaged { get; set; }
}

public class SalePostingSection
{
    public int    Id        { get; set; }
    public int    PostingId { get; set; }   // FK → SalePosting (plain scalar, no nav property)
    public string Name      { get; set; } = "";

    // Output-only rendering field (not shown on the definitions grid).
    public string Prefix    { get; set; } = "";   // prefixes the section name in output (e.g. Slack :macro:)

    // Optional per-section overrides of the posting-level settings. When the Override flag is off,
    // the section inherits the posting's value.
    public bool   OverrideScope     { get; set; }
    public string Scope             { get; set; } = "Everywhere";
    public long?  LocationId        { get; set; }
    public string LocationName      { get; set; } = "";

    public bool   OverridePricing   { get; set; }
    public string PricingBasis      { get; set; } = "Build";
    public double PricePercent      { get; set; } = 110;
    public long?  MarketStationId   { get; set; }
    public string MarketStationName { get; set; } = "";
    public string MarketPriceType   { get; set; } = "Sell";

    public bool   OverrideOnlyPackaged { get; set; }
    public bool   OnlyPackaged         { get; set; }
}

// A post block within a posting. The first (Ordinal 0) is the parent; the rest are supporting
// detail (e.g. posted under a Slack thread). "Summary"/"Detail" are rendered from the posting's
// data; "Static" carries user-authored text in StaticContent.
public class SalePostingPost
{
    public int     Id            { get; set; }
    public int     PostingId     { get; set; }
    public int     Ordinal       { get; set; }
    public string  PostType      { get; set; } = "Summary"; // "Summary" | "Detail" | "Static"
    public string  Name          { get; set; } = "";
    public string? StaticContent { get; set; }              // only used by "Static"
    public string  Header        { get; set; } = "";        // Summary/Detail: text before the content
    public string  Footer        { get; set; } = "";        // Summary/Detail: text after the content
}

public class SalePostingItem
{
    public int Id        { get; set; }
    public int SectionId { get; set; }   // FK → SalePostingSection
    public int TypeId    { get; set; }   // EVE SDE type id

    // Optional display tweaks — null by default, editable in the grid.
    public string? NameOverride { get; set; }
    public string? NamePrefix   { get; set; }

    // Optional manual overrides of the computed quantities — null by default (use computed).
    public int? InStockOverride  { get; set; }
    public int? InBuildOverride  { get; set; }
    public int? ReservedOverride { get; set; }
}
