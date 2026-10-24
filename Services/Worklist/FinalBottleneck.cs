namespace EveConsole.Services.Worklist;

/// <summary>
/// Which items final products are actually waiting ON: what their blocked jobs must have BUILT
/// before they can start.
///
/// <para>Counted on the jobs, not on the recipes. A final whose job is planned and blocked names
/// what it is short of; the items it cannot get any other way — not owned, or owned but needed
/// where they stand — are what holds it up, and so are their own inputs while those are short.
/// A part that no planned final job is short of holds nothing up today, however low its own
/// stock: it may be wanted a month from now, but more of it starts no hull.</para>
///
/// <para>⚠️ Not the item's own coverage. The rule this replaced credited each waiting final to its
/// most-starved input, measured on every input's coverage of its whole demand. Core Temperature
/// Regulators at a fraction of their need beat Auto-Integrity Preservation Seals at half of
/// theirs, so every batch of Reinforced Carbon Fiber went into regulators — 500 a unit, made into
/// Capital Core Temperature Regulators already in stock — while every capital hull job sat blocked
/// for want of seals at its yard (10 fiber makes 3), and the U-C, R-O, S-R and G-O conduits went
/// unbuilt the same way. A low share is not a stopped job.</para>
///
/// <para>⚠️ Before that, every input was credited with every final it fed, and breadth beat
/// depth: a third-stocked Reinforced Carbon Fiber feeding fifteen finals took the reaction slots
/// from an empty Tungsten Carbide that four Arks were stopped for. Counted on blocked jobs, the
/// fiber is credited only with the Arks short of something made from it.</para>
///
/// <para>⚠️ And an input is credited through its consumer only while it is at least as short as
/// that consumer. Rolled Tungsten Alloy, part-stocked, would otherwise take every Ark the empty
/// Tungsten Carbide was stopping and outrank it — the carbide can start on the alloy on hand,
/// so the carbide is what they wait on. Reinforced Carbon Fiber, emptier than the seals a hull is
/// short of, does take that hull: the seals cannot be made without it.</para>
///
/// <para>Used by <see cref="IndustryJobGenerator"/> for its finals-held-up sort key, and checked by
/// tools/WorklistRankCheck.</para>
/// </summary>
public static class FinalBottleneck
{
    /// <summary>Two shares this close are the same shortage.</summary>
    public const double Tie = 1e-9;

    /// <summary>
    /// Whether an item holds up a blocked final job that must have <paramref name="needs"/> built:
    /// it is one of them, or one of them is made from it (it is in the item's dependents) and it
    /// is at least as short as that one.
    /// </summary>
    public static bool HoldsUp(int typeId, double starving, IReadOnlySet<int> dependents,
                               IReadOnlySet<int> needs, Func<int, double> starvingOf)
    {
        if (needs.Contains(typeId)) return true;
        foreach (var m in needs)
            if (dependents.Contains(m) && starving >= starvingOf(m) - Tie) return true;
        return false;
    }

    /// <summary>
    /// How many blocked final jobs this item is holding up, scaled by how much of its own need is
    /// unmet — a share, compared as it stands, so an empty item outranks its part-stocked input
    /// on the same jobs.
    ///
    /// <para>A final product counts itself: nothing consumes a hull, so counting only what
    /// consumes an item would score every finished product zero on the key meant to protect
    /// it.</para>
    ///
    /// <para>Scaled, so the claim falls with every job planned rather than in one step at the end;
    /// see IndustryJobGenerator.BlockedNow for why a sort key has to move as the list grows.</para>
    /// </summary>
    /// <param name="typeId">The item.</param>
    /// <param name="isFinal">Whether the item is itself a final product.</param>
    /// <param name="starving">How much of the item's need is unmet, 0..1.</param>
    /// <param name="dependents">Everything that consumes the item, directly or further up.</param>
    /// <param name="blockedFinalNeeds">For each blocked final job planned so far, what it must have
    /// built: its shortages that cannot be hauled to it.</param>
    /// <param name="starvingOf">How short any item is, by type, measured as <paramref name="starving"/> is.</param>
    public static double FinalsHeldUp(
        int                                  typeId,
        bool                                 isFinal,
        double                               starving,
        IReadOnlySet<int>                    dependents,
        IReadOnlyList<IReadOnlySet<int>>     blockedFinalNeeds,
        Func<int, double>                    starvingOf)
    {
        if (starving <= 0) return 0;

        var n = isFinal ? 1 : 0;
        foreach (var needs in blockedFinalNeeds)
            if (HoldsUp(typeId, starving, dependents, needs, starvingOf))
                n++;

        return n * starving;
    }
}
