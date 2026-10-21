namespace EveConsole.Services.Worklist;

/// <summary>
/// Which input a waiting final product is actually waiting ON.
///
/// <para>A hull needs every one of its parts. While one of them is at zero, more of the others
/// builds nothing, so a waiting final counts towards the item that is HOLDING IT UP — its
/// most-starved input on this pass — and not towards every item it happens to use.</para>
///
/// <para>⚠️ Counting every input let breadth beat depth. Tungsten Carbide sat at zero with nothing
/// running and the Amarr capitals stopped for want of it, but it feeds three final types, so it
/// scored three; Reinforced Carbon Fiber, a third covered, feeds fifteen, the Arks among them,
/// and could not bring one of those Arks a step closer to being built. A broad intermediate had
/// to be about 80% fed before an empty composite was offered a single reaction slot, so all 131
/// went to items with stock on hand and every racial composite read Waiting, weeks behind. Its
/// own inputs outranked it the same way: Rolled Tungsten Alloy and Sylramic Fibers sit upstream
/// of everything carbide feeds, so they counted every one of its finals and more.</para>
///
/// <para>Used by <see cref="IndustryJobGenerator"/> for its finals-blocked sort key, and checked by
/// tools/WorklistRankCheck.</para>
/// </summary>
public static class FinalBottleneck
{
    /// <summary>Two shares this close are the same shortage: inputs short together share the
    /// final rather than one of them taking it on the order the walk happened to use.</summary>
    public const double Tie = 1e-9;

    /// <summary>
    /// For every final, how short its most-starved input is. <paramref name="inputsOfFinal"/>
    /// maps a final's type to the items it waits on; <paramref name="into"/> is cleared and
    /// refilled, so a caller recomputing it every pass does not allocate every pass.
    /// </summary>
    public static void Binding<TItem>(
        IReadOnlyDictionary<int, List<TItem>> inputsOfFinal,
        Func<TItem, double>                   starving,
        Dictionary<int, double>               into)
    {
        into.Clear();
        foreach (var (final, inputs) in inputsOfFinal)
            into[final] = inputs.Count == 0 ? 0 : inputs.Max(starving);
    }

    /// <summary>Whether an input this starved is what holds up a final whose most-starved input is
    /// <paramref name="binding"/>.</summary>
    public static bool HoldsUp(double starving, double binding) => starving >= binding - Tie;

    /// <summary>
    /// How many waiting finals this item is holding up, scaled by how much of its need is unmet.
    ///
    /// <para>A final product counts itself: nothing consumes a hull, so counting only what
    /// consumes an item would score every finished product zero on the key meant to protect
    /// it.</para>
    ///
    /// <para>Scaled, so the count falls with every job planned rather than in one step at the end;
    /// see IndustryJobGenerator.BlockedNow for why a sort key has to move as the list grows.</para>
    /// </summary>
    /// <param name="isFinal">Whether the item is itself a final product.</param>
    /// <param name="starving">How much of the item's need is unmet, 0..1.</param>
    /// <param name="dependents">Everything that consumes the item, directly or further up.</param>
    /// <param name="isWaitingFinal">Whether a dependent is a final product with work still waiting.</param>
    /// <param name="binding">This pass's <see cref="Binding{TItem}"/>.</param>
    public static int FinalsHeldUp(
        bool                             isFinal,
        double                           starving,
        IEnumerable<int>                 dependents,
        Func<int, bool>                  isWaitingFinal,
        IReadOnlyDictionary<int, double> binding)
    {
        if (starving <= 0) return 0;

        var n = isFinal ? 1 : 0;
        foreach (var t in dependents)
            if (isWaitingFinal(t) && HoldsUp(starving, binding.GetValueOrDefault(t)))
                n++;

        return (int)Math.Ceiling(n * starving);
    }
}
