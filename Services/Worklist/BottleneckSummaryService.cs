namespace EveConsole.Services.Worklist;

/// <summary>
/// One thing worth doing something about, in prose.
/// </summary>
/// <param name="Rank">Lower sorts first. Ranked by how much work the finding is holding up,
/// not by which tab it came from.</param>
/// <param name="Points">Supporting lines. Kept apart from the body so the reader can skim the
/// claim and drop into the arithmetic only if they doubt it.</param>
public sealed record Observation(
    string Kind,
    int    Rank,
    string Headline,
    string Body,
    IReadOnlyList<string> Points)
{
    /// <summary>Whether this is the finding to act on rather than one to be aware of.</summary>
    public bool IsPrimary { get; init; }
}

/// <summary>
/// The Bottlenecks summary: what the other four tabs add up to.
///
/// <para>The tabs each answer one question well and none of them answers "so what should I do".
/// A reader who has to open four grids and hold them in their head to find that reactions are
/// the constraint has been handed the raw material of an answer rather than an answer.</para>
///
/// <para>⚠️ Prose, and ranked across ALL FOUR sources together. The pipeline has one binding
/// constraint at a time; four separate top-tens invite fixing the third-worst thing because it
/// happened to be at the top of the list you were looking at.</para>
/// </summary>
public class BottleneckSummaryService
{
    /// <summary>Below this, a pool with a queue is noise rather than a constraint.</summary>
    private const int QueueFloor = 5;

    public List<Observation> Summarise(
        IReadOnlyList<WorklistItem>  items,
        IReadOnlyList<SlotPressure>  slots,
        IReadOnlyList<ItemBandwidth> prints,
        IReadOnlyList<ItemShortage>  shortages,
        IReadOnlyList<HaulBlock>     hauls)
    {
        var found = new List<Observation>();
        var index = TaskChain.Index(items);

        found.AddRange(SlotObservations(items, slots, index));
        found.AddRange(PrintObservations(prints));
        found.AddRange(MaterialObservations(shortages));
        found.AddRange(HaulObservations(hauls));

        var ordered = found.OrderByDescending(o => o.Rank).ToList();

        // ⚠️ One primary, chosen across every source. Naming a winner is the whole point: the
        // reader wants to know which lever moves the pipeline, not which four are available.
        return ordered.Count == 0
            ? ordered
            : [ordered[0] with { IsPrimary = true }, .. ordered.Skip(1)];
    }

    // ── Slots ─────────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> SlotObservations(
        IReadOnlyList<WorklistItem> items,
        IReadOnlyList<SlotPressure> slots,
        Dictionary<int, List<(WorklistItem Item, WorklistShortage Short)>> index)
    {
        foreach (var s in slots.Where(s => s.Waiting >= QueueFloor)
                               .OrderByDescending(s => s.Waiting))
        {
            var pool = Pool(s.Pool);

            // ⚠️ What the queue costs elsewhere. A reaction queue is not a reaction problem: the
            // manufacturing jobs waiting on those reactions are stopped by the slot shortage just
            // as surely, and counting only the queue itself understates the pool by the whole
            // chain hanging off it.
            var downstream = Downstream(items, index, s.Pool);

            var body =
                $"{s.Capacity:N0} {pool} slot(s), {s.InUse:N0} in use and {s.Free:N0} free. "
              + $"{s.Waiting:N0} job(s) could start today if there were somewhere to run them"
              + (s.Blocked > 0 ? $", with another {s.Blocked:N0} behind them" : "")
              + ". "
              + (downstream > 0
                  ? $"{downstream:N0} job(s) in other pools are stopped for want of what those "
                  + "would have made, so the queue is holding up more than itself. "
                  : "")
              + (double.IsInfinity(s.ClearDays) || s.ClearDays <= 0
                  ? ""
                  : $"At the bandwidth on hand the queue takes {s.ClearDays:N0} day(s) to clear.");

            var points = new List<string>();

            foreach (var r in s.Remedies.Where(r => r.Slots > 0))
                points.Add(
                    $"{r.Action}: +{r.Slots:N0} slot(s), {r.PercentGain:N0}% more capacity"
                  + (r.Effect.Length > 0 ? $" — {r.Effect}." : ".")
                  + $" {r.Detail}");

            // The one that is not worth doing is worth saying, so the reader stops considering it.
            foreach (var r in s.Remedies.Where(r => r.Slots <= 0))
                points.Add($"{r.Action}: nothing to gain. {r.Detail}");

            // ⚠️ Ranked on the work held up, not on the queue. A pool with 200 waiting jobs that
            // block nothing downstream matters less than one with 40 that stop a hundred others.
            yield return new Observation(
                "slots",
                s.Waiting + downstream,
                $"{Cap(pool)} slots are the constraint",
                body,
                points);
        }
    }

    /// <summary>
    /// Jobs in other pools stopped for want of what this pool's queued jobs would have made.
    ///
    /// <para>Walked over the chain rather than counted directly: a manufacturing job may be two
    /// steps above the reaction that is waiting, and the step between them is stopped too.</para>
    /// </summary>
    private static int Downstream(
        IReadOnlyList<WorklistItem> items,
        Dictionary<int, List<(WorklistItem Item, WorklistShortage Short)>> index,
        IndustryPool pool)
    {
        var stuck = items
            .Where(i => i.Pool == pool
                     && i.TypeId > 0
                     && i.Readiness != WorklistReadiness.Ready)
            .Select(i => i.TypeId)
            .Distinct();

        var reached = new HashSet<string>();
        foreach (var typeId in stuck)
            foreach (var t in TaskChain.Stalled(index, typeId))
                reached.Add(t.Title + "|" + t.Why);

        return reached.Count;
    }

    // ── Blueprints ────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> PrintObservations(IReadOnlyList<ItemBandwidth> prints)
    {
        var blocking = prints.Where(p => p.BlockedNow > 0 || p.StalledTasks > 0).ToList();
        if (blocking.Count == 0) yield break;

        var worst = blocking
            .OrderByDescending(p => p.StalledTasks)
            .ThenByDescending(p => p.ContentionPercent)
            .Take(4)
            .ToList();

        var held = blocking.Sum(p => p.BlockedNow);

        yield return new Observation(
            "prints",
            blocking.Sum(p => p.StalledTasks) + held,
            $"{blocking.Count:N0} blueprint(s) are the ceiling, not the slots",
            $"{blocking.Count:N0} blueprint(s) or formula(s) have work queued behind what they "
          + $"make{(held > 0 ? $", and {held:N0} job(s) cannot start for want of a free original" : "")}. "
          + "Buying a second original doubles a product's rate outright, which no amount of slots does.",
            [.. worst.Select(p =>
                $"{p.ProductName}: {p.Prints:N0} {p.Nouns} owned, every one busy "
              + $"{p.ContentionPercent:N0}% of the last {p.WindowDays} days"
              + (p.StalledTasks > 0 ? $", {p.StalledTasks:N0} task(s) waiting on its output" : "")
              + $". One more takes the ceiling to {p.CeilingWithOneMore:N2}/day from {p.CapacityPerDay:N2}.")]);
    }

    // ── Materials ─────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> MaterialObservations(IReadOnlyList<ItemShortage> shortages)
    {
        var real = shortages.Where(s => s.BlockedTasks > 0).ToList();
        if (real.Count == 0) yield break;

        var worst = real.OrderByDescending(s => s.StalledTasks).Take(4).ToList();

        // ⚠️ Separated because the answers are nothing alike: an item with no level set has no
        // cushion by construction, and an item nothing makes cannot be fixed by a bigger one.
        var noLevel = real.Count(s => s.Level <= 0);
        var noMaker = real.Count(s => !s.Buildable);

        yield return new Observation(
            "materials",
            real.Sum(s => s.StalledTasks),
            $"{real.Count:N0} material(s) are stopping work outright",
            $"{real.Sum(s => s.BlockedTasks):N0} job(s) are short of something nobody owns enough "
          + $"of, holding up {real.Sum(s => s.StalledTasks):N0} task(s) in all. "
          + (noLevel > 0
              ? $"{noLevel:N0} of these have no inventory level set at all, so there is no cushion "
              + "by construction — every unit has to arrive exactly when it is wanted. "
              : "")
          + (noMaker > 0
              ? $"{noMaker:N0} cannot be made here and can only be bought."
              : ""),
            [.. worst.Select(s =>
                $"{s.Name}: {s.BlockedTasks:N0} job(s) short, {s.StalledTasks:N0} stopped in all. "
              + $"{s.Verdict}. Used {s.UsedPerDay:N1}/day against {s.MadePerDay:N1}/day made"
              + (s.Level > 0 ? $", level {s.Level:N0} = {s.DaysOfCover:N0} day(s) of cover." : ", no level set."))]);
    }

    // ── Hauling ───────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> HaulObservations(IReadOnlyList<HaulBlock> hauls)
    {
        if (hauls.Count == 0) yield break;

        var idle = hauls.Where(h => h.HaulTasks == 0).ToList();
        var behind = hauls.Sum(h => h.StalledTasks);

        yield return new Observation(
            "hauling",
            hauls.Count + behind,
            $"{hauls.Count:N0} job(s) are waiting on material you already own",
            $"{hauls.Count:N0} job(s) are stopped for want of nothing but a delivery — "
          + $"{hauls.Sum(h => h.Volume) / 1000:N0}k m3 in all, holding up {behind:N0} task(s) "
          + "behind them. "
          + (idle.Count > 0
              ? $"{idle.Count:N0} of them have no haul raised at all, so those will not resolve "
              + "on their own."
              : "Every one of them has a haul already raised, so this clears itself."),
            [.. hauls.OrderByDescending(h => h.StalledTasks).ThenByDescending(h => h.Volume).Take(4)
                     .Select(h =>
                $"{h.Title} at {h.StationName}: {h.Volume:N0} m3 from "
              + (h.Sources == 1 ? "one place" : $"{h.Sources:N0} places")
              + (h.HaulTasks > 0 ? $", {h.HaulTasks:N0} haul(s) raised." : ", nothing moving."))]);
    }

    private static string Pool(IndustryPool p) => p switch
    {
        IndustryPool.Manufacturing => "manufacturing",
        IndustryPool.Reaction      => "reaction",
        _                          => "science",
    };

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
