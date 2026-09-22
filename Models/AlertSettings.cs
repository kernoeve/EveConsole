namespace EveConsole.Models;

public class AlertSettings
{
    public int  Id                    { get; set; } = 1; // singleton row
    public bool SkillQueueEmpty       { get; set; } = true;
    public bool SkillQueuePaused      { get; set; } = true;
    public bool SkillQueueEmptyInDays { get; set; } = true;
    public int  SkillQueueEmptyDays   { get; set; } = 30;
    public bool AssetSafety                { get; set; } = true;
    public bool InactiveStandingProjects   { get; set; } = true;
    public bool StandingBuyOrdersAttention { get; set; } = true;
    public bool UnriggedIndustryJobs       { get; set; } = true;

    /// <summary>"You have N industry jobs ready to deliver" — finished, output waiting, slot held.</summary>
    public bool IndustryJobsReady          { get; set; } = true;

    /// <summary>Active contracts assigned to a character or personal corporation.</summary>
    public bool OutstandingContracts       { get; set; } = true;
    /// <summary>Active contracts from or to them in the last 15% of their life.</summary>
    public bool ExpiringContracts          { get; set; } = true;
}
