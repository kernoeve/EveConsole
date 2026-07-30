namespace EveConsole.Models;

public class EsiAdjustedPrice
{
    public int    TypeId        { get; set; }
    public double AdjustedPrice { get; set; }
    public double AveragePrice  { get; set; }
}

public class IndustryCostIndex
{
    public int    SolarSystemId { get; set; }
    public string Activity      { get; set; } = "";
    public double CostIndex     { get; set; }
}

public class BuildCost
{
    public int      TypeId       { get; set; }
    public string   TypeName     { get; set; } = "";
    public decimal  TotalCost    { get; set; }
    public decimal  MaterialCost { get; set; }
    public decimal  JobCost      { get; set; }
    // Seconds to manufacture ONE unit in the default park (blueprint TE + skills +
    // structure role/rig time bonuses applied). 0 if the item is not manufacturable.
    public double   BuildSeconds { get; set; }
    // True when buying the finished item is cheaper than building it — TotalCost is then the buy
    // price with no job. The Production Calculator reads this to buy (not build) the component.
    public bool     Bought       { get; set; }
    public DateTime UpdatedAt    { get; set; }
}

public class ReprocessingItemValue
{
    public int    TypeId { get; set; }
    public double Value  { get; set; }
}
