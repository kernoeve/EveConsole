namespace EveCortex.Models;

public static class MarketMethod
{
    public const string Fuzzwork        = "Fuzzwork";
    public const string EsiRegion       = "Region";
    public const string PlayerStructure = "Player Structure";
}

public static class MarketPriceType
{
    public const string Midpoint = "Midpoint";
    public const string Buy      = "Buy";
    public const string Sell     = "Sell";
}

public class MarketPricingConfig
{
    public int     Id                  { get; set; }
    public string  Method              { get; set; } = MarketMethod.Fuzzwork;
    public string  LocationName        { get; set; } = "";
    public long    LocationId          { get; set; }
    public string  PriceType          { get; set; } = MarketPriceType.Midpoint; // kept for DB compat; unused
    public long?   AuthCharId          { get; set; }
    public bool    IsEnabled           { get; set; } = true;
    public int     SortOrder           { get; set; }
    public DateTimeOffset? LastRefreshed { get; set; }
    public string  LastStatus          { get; set; } = "";
    public long?   StationFilter       { get; set; }
    public bool    UsePercentileFilter { get; set; } = true;
    public double  PercentilePercent   { get; set; } = 5.0;
}

public class MarketItemPrice
{
    public int    ConfigId       { get; set; }
    public int    TypeId         { get; set; }
    public double BuyPrice       { get; set; }
    public double SellPrice      { get; set; }
    public double Midpoint       { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    // True = row came from real market orders; false = gap-filled from build cost.
    // Only false rows are overwritten when build costs change (Step 3 of FillPriceGapsAsync).
    public bool   FromMarketData { get; set; }
}

public class MarketDefaultSettings
{
    public int     Id                      { get; set; } = 1; // singleton row
    public int?    AssetValueConfigId      { get; set; }
    public string  AssetValuePriceType     { get; set; } = MarketPriceType.Midpoint;
    public int?    ManufacturingConfigId   { get; set; }
    public string  ManufacturingPriceType  { get; set; } = MarketPriceType.Sell;
    public decimal MissingPriceMarkupPct         { get; set; } = 15m;
    public bool    FilterLowballBuyOrders         { get; set; } = true;
    public decimal LowballBuyOrderThresholdPct    { get; set; } = 25m;
}

public class MarketRawOrder
{
    public int    ConfigId     { get; set; }
    public long   OrderId      { get; set; }
    public int    TypeId       { get; set; }
    public bool   IsBuyOrder   { get; set; }
    public double Price        { get; set; }
    public int    VolumeRemain { get; set; }
    public int    VolumeTotal  { get; set; }
    public int    MinVolume    { get; set; }
    public long   LocationId   { get; set; }
    public int    SystemId     { get; set; }
    public string Range        { get; set; } = "";
    public DateTimeOffset Issued    { get; set; }
    public int    Duration     { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

// ── Market price history (on-demand cache from ESI /markets/{region}/history/) ─

public class MarketTypeHistory
{
    public int    RegionId   { get; set; }
    public int    TypeId     { get; set; }
    public string Date       { get; set; } = ""; // YYYY-MM-DD
    public double Average    { get; set; }
    public double Highest    { get; set; }
    public double Lowest     { get; set; }
    public long   Volume     { get; set; }
    public int    OrderCount { get; set; }
}

public class MarketHistoryFetch
{
    public int            RegionId  { get; set; }
    public int            TypeId    { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    // Whether the last fetch returned any history. Items that returned nothing (marketable
    // but never traded in this region) are re-checked far less often to bound wasted calls.
    public bool           HadData   { get; set; } = true;
}

public class PriceHistoryRegion
{
    public int    RegionId   { get; set; }
    public string RegionName { get; set; } = "";
}
