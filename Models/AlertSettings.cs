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
}
