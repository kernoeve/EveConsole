namespace EveCortex.Models;

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
    public DateTime UpdatedAt    { get; set; }
}

public class ReprocessingItemValue
{
    public int    TypeId { get; set; }
    public double Value  { get; set; }
}
