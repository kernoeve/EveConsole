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
    /// <summary>Retired 2026-10-23: there is no stale PI data — every layout is read every ten
    /// minutes. The column stays so no schema changes; nothing reads it.</summary>
    public bool PiStaleData  { get; set; } = true;

    /// <summary>Unread EVE mail on any of the user's characters.</summary>
    public bool UnreadMail   { get; set; } = true;

    // ── Structures ── only corporations with Corporation.StructureAlerts on. The fit each structure
    // type should carry is set on Corp Structures → Config.

    /// <summary>A structure or online tower runs out of fuel within StructureFuelDays.</summary>
    public bool StructureFuel       { get; set; } = true;
    public int  StructureFuelDays   { get; set; } = 3;
    /// <summary>A structure or tower attacked, from the game's notifications: dismissible.</summary>
    public bool StructureAttacked   { get; set; } = true;
    /// <summary>A module of the structure's fit not fitted, with a slot free for it.</summary>
    public bool StructureFitMissing { get; set; } = true;
    /// <summary>A module of the fit not fitted and its slots full: something else is in its place.</summary>
    public bool StructureFitWrong   { get; set; } = true;
    /// <summary>A charge of the fit below StructureAmmoPct percent of the fit's amount.</summary>
    public bool StructureAmmoLow    { get; set; } = true;
    public int  StructureAmmoPct    { get; set; } = 50;
    /// <summary>A fighter of the fit below StructureFightersPct percent of the fit's amount.</summary>
    public bool StructureFightersLow { get; set; } = true;
    public int  StructureFightersPct { get; set; } = 50;

    // ── Mercenary dens ── the user's characters' own dens (Mercenary Dens tool). ESI tells a den's
    // anarchy only to its owner, so a den on someone else's skyhook is not seen here.

    /// <summary>A den's anarchy at DenAnarchyLevel (0–4) or above.</summary>
    public bool DenAnarchy      { get; set; } = true;
    public int  DenAnarchyLevel { get; set; } = 3;
    /// <summary>A den attacked or reinforced, from the game's notifications: dismissible.</summary>
    public bool DenAttacked     { get; set; } = true;
    /// <summary>A new mercenary tactical operation at a den, from the notifications: dismissible.</summary>
    public bool DenNewMto       { get; set; } = true;
}
