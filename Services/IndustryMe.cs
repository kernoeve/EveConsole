namespace EveConsole.Services;

// Default material-efficiency assumptions, shared by BuildCostService and
// ProductionCalculatorService so the two agree on how well-researched a blueprint is:
//   • ME10 for most manufacturable items (well-researched T1 BPO)
//   • ME3  for T2 items (typical invention/research level)
//   • ME0  for BPC-only items (faction/loot BPCs can't be researched)
//   • ME9  for titans, supers, Keepstars and Fortizars (ME10 is impractically slow; ME9 is real)
//   • ME0  for reactions (no material research exists)
public static class IndustryMe
{
    public const int TitanGroupId   = 30;     // SDE group "Titan"
    public const int SuperGroupId   = 659;    // SDE group "Supercarrier"
    public const int KeepstarTypeId = 35834;  // the Keepstar (other Citadels stay at the default)
    public const int FortizarTypeId = 35833;  // the Fortizar — same story as the Keepstar

    public static int DefaultMe(bool isReaction, bool bpcOnly, bool isT2, bool isTitanKeepstarFortizar)
    {
        if (isReaction)        return 0;
        if (bpcOnly)           return 0;   // faction / loot BPC — not researchable
        if (isTitanKeepstarFortizar) return 9;
        if (isT2)              return 3;
        return 10;
    }

    // Convenience: the material multiplier for a given ME (0.90 at ME10, 1.0 at ME0).
    public static double Factor(int me) => (100.0 - me) / 100.0;
}
