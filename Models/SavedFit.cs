namespace EveConsole.Models;

/// <summary>A fit saved in the fitting tool, kept as EFT text — the format the client, and every
/// other fitting tool, reads and writes, so a saved fit is always one copy away from the game.</summary>
public class SavedFit
{
    public long           Id         { get; set; }
    public string         Name       { get; set; } = "";
    public int            ShipTypeId { get; set; }
    public string         Eft        { get; set; } = "";
    /// <summary>What EFT leaves out — module states, launched drones, squadrons in tubes — as
    /// <see cref="Services.Fitting.FitState"/> writes it; "" for a fit saved before it was kept.</summary>
    public string         State      { get; set; } = "";
    public DateTimeOffset UpdatedAt  { get; set; }
}
