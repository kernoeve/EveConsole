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

    // ── Planetary Industry ── only the characters that do PI. The lead times these judge by are
    // on Settings → Industry (PiSettings), shared with the PI worklist tasks.

    /// <summary>Extractors stopped, or stopping within the lead time (exact).</summary>
    public bool PiExtractors { get; set; } = true;
    /// <summary>Storage, a launchpad or the command center full within the lead time (estimated).</summary>
    public bool PiStorage    { get; set; } = true;
    /// <summary>A factory planet's brought-in input running out within the lead time (estimated).</summary>
    public bool PiInputs     { get; set; } = true;
    /// <summary>A PI character with a colony slot it is not using.</summary>
    public bool PiFreeSlots  { get; set; } = true;
    /// <summary>A colony not opened in game for longer than the staleness setting.</summary>
    public bool PiStaleData  { get; set; } = true;
}
