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

    /// <summary>
    /// What a whole job consumes of one material: the base quantity per run, the ME/rig/role
    /// multiplier, and the run count, as EVE computes it —
    /// <c>max(runs, ceil(round(baseQty × runs × factor, 2)))</c>.
    ///
    /// <para>⚠️ The rounding is on the JOB's total, not on the per-run amount. Rounding
    /// <c>baseQty × factor</c> to two places first and then multiplying quietly changes the
    /// answer: 22 Fermionic Condensates at 0.8461057 is 18.6143 a run, which becomes 18.61, and
    /// over 275 runs that is 5,118 against the game's 5,119. The error is bounded by
    /// 0.005 × runs and falls the same way every time for a given material, so every job of
    /// that item reserves slightly less than it will actually eat.</para>
    ///
    /// <para>The two-place round is only there to kill floating-point noise before the ceiling.
    /// Without it a total the game calls exactly 5,064 can arrive as 5,064.0000000001 and
    /// ceiling to 5,065.</para>
    ///
    /// <para>⚠️ One definition, because the build costs and the plan have to agree about what
    /// a job eats. This was two identical private copies — one here, one in BuildCostService
    /// — each carrying a comment telling the reader to keep it in step with the other.</para>
    /// </summary>
    public static long JobMaterialTotal(int baseQty, double factor, long runs)
    {
        double total = Math.Round(baseQty * (double)runs * factor, 2);
        return Math.Max(runs, (long)Math.Ceiling(total));
    }
}
