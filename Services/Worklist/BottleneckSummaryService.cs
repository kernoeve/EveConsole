namespace EveConsole.Services.Worklist;

/// <summary>
/// One thing worth doing something about, in prose.
/// </summary>
/// <param name="Rank">Lower sorts last. Ranked by how much work the finding is holding up, not
/// by which tab it came from.</param>
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
/// <para>⚠️ Prose, and ranked across ALL sources together. The pipeline has one binding
/// constraint at a time; several separate top-tens invite fixing the third-worst thing because
/// it happened to be at the top of the list that was open.</para>
/// </summary>
public class BottleneckSummaryService
{
    /// <summary>Below this, a pool with a queue is noise rather than a constraint.</summary>
    private const int QueueFloor = 5;

    /// <summary>
    /// How many items any one finding names.
    ///
    /// <para>⚠️ A summary that lists everything is the grid again. Ten is enough to see the
    /// shape and short enough to read; anything past it belongs on the tab it came from, which
    /// is why each finding says how many it left out.</para>
    /// </summary>
    private const int MaxNamed = 6;

    /// <summary>
    /// How many DISTINCT tasks a set of findings holds up.
    ///
    /// <para>⚠️ Summing each finding's own count double-counts, and the findings are ranked
    /// against each other on exactly this number. A job short of three materials is stopped
    /// once, not three times — forty-three materials summed to 316 stopped tasks out of a
    /// list of 338, and beat a slot pool holding up more work than any of them, because the
    /// slot figure counts distinct jobs and the material figure counted overlaps.</para>
    /// </summary>
    private static int DistinctStopped(IEnumerable<IEnumerable<ShortageTask>> sets)
    {
        var seen = new HashSet<string>();
        foreach (var set in sets)
            foreach (var t in set)
                if (t.Role == "Stopped") seen.Add(t.Title + "|" + t.TypeName);
        return seen.Count;
    }

    /// <summary>Days of cover a suggested level is sized for.</summary>
    private const int TargetCoverDays = 30;

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
        found.AddRange(PrintObservations(prints, reactions: false));
        found.AddRange(PrintObservations(prints, reactions: true));
        found.AddRange(MaterialObservations(shortages));
        found.AddRange(UnorderedObservations(items, shortages));
        found.AddRange(LevelObservations(shortages));
        found.AddRange(HaulObservations(hauls));

        var ordered = found.OrderByDescending(o => o.Rank).ToList();

        // ⚠️ One primary, chosen across every source. Naming a winner is the whole point: the
        // reader wants to know which lever moves the pipeline, not which six are available.
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

    // ── Blueprints and formulas ───────────────────────────────────────────────

    /// <summary>
    /// ⚠️ One measurement, two decisions. A BPO is bought off the market for a known price; a
    /// formula is a different market and often a different character, so folding them into one
    /// line hides which of the two the reader is being asked to spend on.
    /// </summary>
    private static IEnumerable<Observation> PrintObservations(
        IReadOnlyList<ItemBandwidth> prints, bool reactions)
    {
        // ⚠️ Contention above zero is the entry test, not queued work. A print that has never
        // once had every copy busy is not what is holding its product up — the queue behind it
        // is inherited from further up the chain, and buying another copy of something that has
        // sat idle for ninety days changes nothing at all.
        var blocking = prints
            .Where(p => p.IsReaction == reactions
                     && p.ContentionPercent > 0
                     && (p.BlockedNow > 0 || p.StalledTasks > 0))
            .OrderByDescending(p => p.ContentionPercent)
            .ThenByDescending(p => p.StalledTasks)
            .ToList();
        if (blocking.Count == 0) yield break;

        var one   = blocking.Count == 1;
        var thing = reactions ? (one ? "formula" : "formulas") : (one ? "blueprint" : "blueprints");
        var held  = blocking.Sum(p => p.BlockedNow);
        var named = blocking.Take(MaxNamed).ToList();

        yield return new Observation(
            reactions ? "formulas" : "prints",
            DistinctStopped(blocking.Select(p => p.Tasks)) + held,
            $"{blocking.Count:N0} {thing} cap their own output",
            // ⚠️ No claim about slots. Saying "the ceiling, not the slots" is false where every
            // slot is full as well: buying the copy would not start the job either.
            $"{blocking.Count:N0} {thing} have spent real time with every copy busy at once"
          + (held > 0 ? $", and {held:N0} job(s) cannot start for want of a free one" : "")
          + ". A second copy doubles that product's rate outright."
          + (blocking.Count > MaxNamed
              ? $" The worst {MaxNamed} are below; the rest are on the BPO / Formula tab."
              : ""),
            [.. named.Select(p =>
                $"{p.ProductName}: {p.Prints:N0} owned, every one busy "
              + $"{p.ContentionPercent:N0}% of the last {p.WindowDays} days"
              + (p.StalledTasks > 0 ? $", {p.StalledTasks:N0} task(s) waiting on its output" : "")
              + $". One more takes the ceiling to {p.CeilingWithOneMore:N2}/day from "
              + $"{p.CapacityPerDay:N2}.")]);
    }

    // ── Materials ─────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> MaterialObservations(IReadOnlyList<ItemShortage> shortages)
    {
        var real = shortages.Where(s => s.BlockedTasks > 0).ToList();
        if (real.Count == 0) yield break;

        var worst = real.OrderByDescending(s => s.StalledTasks).Take(MaxNamed / 2).ToList();

        // ⚠️ Separated because the answers are nothing alike: an item with no level set has no
        // cushion by construction, and an item nothing makes cannot be fixed by a bigger one.
        var noLevel = real.Count(s => s.Level <= 0);
        var noMaker = real.Count(s => !s.Buildable);

        var stopped = DistinctStopped(real.Select(s => s.Tasks ?? []));

        yield return new Observation(
            "materials",
            stopped,
            $"{real.Count:N0} item(s) are stopping work outright",
            // ⚠️ Not "nobody owns any". The test is whether the industry scope can cover what a
            // job asks for, AFTER the material earlier jobs already claimed — so an item can be
            // owned in quantity and still fail it. Saying nobody owns enough sent the reader
            // looking for an empty hangar when 65,696 Prometium were sitting in one.
            $"{real.Sum(s => s.BlockedTasks):N0} job(s) are short of something the industry scope "
          + $"cannot cover once earlier jobs have taken their share, holding up {stopped:N0} "
          + "task(s) between them. "
          + (noLevel > 0
              ? $"{noLevel:N0} of these have no inventory level set at all, so there is no cushion "
              + "by construction. "
              : "")
          + (noMaker > 0
              ? $"{noMaker:N0} cannot be made here and can only be bought."
              : ""),
            [.. worst.Select(s =>
                $"{s.Name}: {s.BlockedTasks:N0} job(s) short, {s.StalledTasks:N0} stopped in all. "
              + $"{s.Verdict}. Used {s.UsedPerDay:N1}/day against {s.MadePerDay:N1}/day made"
              + (s.Level > 0
                  ? $", level {s.Level:N0} = {s.DaysOfCover:N0} day(s) of cover."
                  : ", no level set."))]);
    }

    // ── Buying ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Material that is stopping work and has nothing on order.
    ///
    /// <para>⚠️ A shortage with a purchase raised against it is in hand; the same shortage with
    /// nothing raised will still be there next week. That difference is the whole finding, and
    /// it is invisible on Item Contention, which reports the shortage either way.</para>
    /// </summary>
    private static IEnumerable<Observation> UnorderedObservations(
        IReadOnlyList<WorklistItem> items, IReadOnlyList<ItemShortage> shortages)
    {
        // Everything the list already proposes buying, single-item tasks and manifests alike.
        var onOrder = items
            .Where(i => i.Kind == WorklistKind.Buy)
            .SelectMany(i => i.Lines.Count > 0
                ? i.Lines.Select(l => l.TypeId)
                : new[] { i.TypeId })
            .Where(t => t > 0)
            .ToHashSet();

        // ⚠️ Only what is actually bought. Anything the pipeline can make raises a JOB when it
        // runs short, not a purchase, so listing buildables here told the reader to go shopping
        // for something the plan already intends to build. Item Contention draws the same line
        // when it says "Buy now" only where nothing here makes it.
        var missing = shortages
            .Where(s => s.BlockedTasks > 0 && !s.Buildable && !onOrder.Contains(s.TypeId))
            .OrderByDescending(s => s.StalledTasks)
            .ThenByDescending(s => s.BlockedTasks)
            .ToList();
        if (missing.Count == 0) yield break;

        yield return new Observation(
            "buying",
            DistinctStopped(missing.Select(s => s.Tasks ?? [])),
            $"{missing.Count:N0} bought item(s) are stopping work with nothing on order",
            $"{missing.Sum(s => s.BlockedTasks):N0} job(s) are short of these. Nothing here makes "
          + "them, so a purchase is the only thing that starts those jobs, and no buy task has "
          + $"been raised for any of them. They are holding up "
          + $"{missing.Sum(s => s.StalledTasks):N0} task(s) in all."
          + (missing.Count > MaxNamed ? $" The worst {MaxNamed} are below." : ""),
            [.. missing.Take(MaxNamed).Select(s =>
                $"{s.Name}: {s.BlockedTasks:N0} job(s) short, drawn on {s.UsedPerDay:N1}/day"
              + (s.Level > 0 ? $" against a level of {s.Level:N0}." : " with no level set."))]);
    }

    // ── Buffers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Levels worth setting or raising.
    ///
    /// <para>⚠️ Two findings, deliberately in one place and distinguished in the text. No level
    /// at all means no cushion by construction: every unit has to arrive exactly when it is
    /// wanted. A level that is merely too small was sized for ordinary draw and met a build
    /// wave, which is a number to change rather than a habit.</para>
    /// </summary>
    private static IEnumerable<Observation> LevelObservations(IReadOnlyList<ItemShortage> shortages)
    {
        var unset = shortages
            .Where(s => s.BlockedTasks > 0 && s.Level <= 0 && s.UsedPerDay > 0)
            .OrderByDescending(s => s.UsedPerDay)
            .ToList();

        // ⚠️ The test is whether the LEVEL is too small, not whether today is short. Titanium
        // Carbide at 20,000,000 already carries 35 days and was short by a tenth of its level
        // during a wave — a buffer doing its job, and it headed the list because the shortfall
        // was large in absolute units. Ranked by how far short the level is of the draw it has
        // to cover, so a level under half of what it needs beats one that is a tenth light.
        var thin = shortages
            .Where(s => s.BlockedTasks > 0
                     && s.Level > 0
                     && s.UsedPerDay > 0
                     && s.UsedPerDay * TargetCoverDays > s.Level)
            .OrderByDescending(s => s.UsedPerDay * TargetCoverDays / s.Level)
            .ToList();

        if (unset.Count == 0 && thin.Count == 0) yield break;

        var points = new List<string>();

        foreach (var s in unset.Take(MaxNamed))
            points.Add(
                $"SET a level for {s.Name}: drawn on {s.UsedPerDay:N1}/day with nothing set "
              + $"aside, {s.BlockedTasks:N0} job(s) stopped. "
              + $"{Suggest(s)} would carry {TargetCoverDays} days at the current rate.");

        foreach (var s in thin.Take(MaxNamed))
            points.Add(
                $"RAISE the level on {s.Name}: {s.Level:N0} carries only {s.DaysOfCover:N0} day(s) "
              + $"at {s.UsedPerDay:N1}/day, and {s.BlockedTasks:N0} job(s) are stopped on it. "
              + $"{Suggest(s)} would carry {TargetCoverDays} days — "
              + $"{s.UsedPerDay * TargetCoverDays / s.Level:N1}x the level set now.");

        yield return new Observation(
            "levels",
            DistinctStopped(unset.Concat(thin).Select(s => s.Tasks ?? [])),
            $"{unset.Count + thin.Count:N0} inventory level(s) are worth setting or raising",
            (unset.Count > 0
                ? $"{unset.Count:N0} material(s) that stopped work have no level set at all, so "
                + "there is no cushion by construction. "
                : "")
          + (thin.Count > 0
                ? $"{thin.Count:N0} have a level smaller than the draw it has to cover, so it "
                + "empties faster than it can be refilled. "
                : "")
          + "A buffer is a length of time, not a quantity, so the figures below are what "
          + $"{TargetCoverDays} days of the current draw would take.",
            points);
    }

    /// <summary>⚠️ Sized on the draw, not on the shortfall. A level set to today's gap is a level
    /// that empties again the moment the next wave arrives.</summary>
    private static string Suggest(ItemShortage s) =>
        Math.Ceiling(s.UsedPerDay * TargetCoverDays).ToString("N0");

    // ── Hauling ───────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> HaulObservations(IReadOnlyList<HaulBlock> hauls)
    {
        if (hauls.Count == 0) yield break;

        var idle   = hauls.Where(h => h.HaulTasks == 0).ToList();
        var behind = hauls.Sum(h => h.StalledTasks);
        var shared = SharedHauls.Find(hauls);

        yield return new Observation(
            "hauling",
            hauls.Count + DistinctStopped(hauls.Select(h => h.Tasks)),
            $"{hauls.Count:N0} job(s) are waiting on material you already own",
            $"{hauls.Count:N0} job(s) are stopped for want of nothing but a delivery — "
          + $"{hauls.Sum(h => h.Volume) / 1000:N0}k m3 in all, holding up {behind:N0} task(s) "
          + "behind them. "
          + (idle.Count > 0
              ? $"{idle.Count:N0} of them have no haul raised at all, so those will not resolve "
              + "on their own. "
              : "Every one of them has a haul already raised, so this clears itself. ")
          + (shared.Count > 0
              ? $"{shared.Count:N0} single deliveries would each restart more than one job."
              : ""),
            // ⚠️ Trips only. The single stopped jobs were listed underneath and read as more
            // hauls, when they are the Hauling grid restated — this finding exists to say which
            // ONE delivery is worth making, and a list of jobs is not that.
            [.. shared.Take(MaxNamed).Select(h => h.Line)]);
    }

    private static string Pool(IndustryPool p) => p switch
    {
        IndustryPool.Manufacturing => "manufacturing",
        IndustryPool.Reaction      => "reaction",
        _                          => "science",
    };

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
