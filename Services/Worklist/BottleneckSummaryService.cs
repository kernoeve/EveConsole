namespace EveConsole.Services.Worklist;

/// <summary>
/// One thing worth doing something about.
/// </summary>
/// <param name="Rank">Higher sorts first: how much work the finding is holding up, counted in
/// distinct stopped tasks.</param>
/// <param name="Points">The actions themselves, one per line.</param>
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
/// The Bottlenecks summary: what to DO, in the order worth doing it.
///
/// <para>⚠️ Actions, not explanations. The first version stated the case for every finding — the
/// rates, the days of cover, the share of the window a print had been busy — and it was slower to
/// act on than the grids it summarised, which is the one thing a summary may not be. A reader who
/// can answer their question faster on a detail tab has been handed prose where they wanted a
/// list. Everything supporting is one tab away and optional; this page says which buttons to
/// press.</para>
///
/// <para>⚠️ Ranked across ALL sources together. The pipeline has one binding constraint at a
/// time; several separate top-tens invite fixing the third-worst thing because it happened to be
/// at the top of the list that was open.</para>
/// </summary>
public class BottleneckSummaryService
{
    /// <summary>Below this, a pool with a queue is noise rather than a constraint.</summary>
    private const int QueueFloor = 5;

    /// <summary>How many actions any one finding lists before deferring to its tab.</summary>
    private const int MaxNamed = 6;

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
        found.AddRange(BuyObservations(items, index));
        found.AddRange(LevelObservations(shortages));
        found.AddRange(HaulObservations(hauls));

        var ordered = found.OrderByDescending(o => o.Rank).ToList();

        return ordered.Count == 0
            ? ordered
            : [ordered[0] with { IsPrimary = true }, .. ordered.Skip(1)];
    }

    /// <summary>
    /// How many DISTINCT tasks a set of findings holds up.
    ///
    /// <para>⚠️ Summing each finding's own count double-counts, and findings are ranked against
    /// each other on exactly this number. A job short of three materials is stopped once, not
    /// three times.</para>
    /// </summary>
    private static int DistinctStopped(IEnumerable<IEnumerable<ShortageTask>> sets)
    {
        var seen = new HashSet<string>();
        foreach (var set in sets)
            foreach (var t in set)
                if (t.Role == "Stopped") seen.Add(t.Title + "|" + t.TypeName);
        return seen.Count;
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
            var pool       = Pool(s.Pool);
            var downstream = Downstream(items, index, s.Pool);

            // ⚠️ Only remedies worth taking. A remedy worth nothing used to be spelled out so
            // the reader would stop considering it — which is an explanation, not an action.
            var points = s.Remedies
                .Where(r => r.Slots > 0)
                .Select(r => $"{r.Action} — +{r.Slots:N0} slots ({r.PercentGain:N0}%)")
                .ToList();

            if (points.Count == 0) continue;

            yield return new Observation(
                "slots",
                s.Waiting + downstream,
                $"Add {pool} slots",
                $"{s.Waiting:N0} job(s) waiting on one"
              + (downstream > 0 ? $", {downstream:N0} more stopped behind them" : "")
              + ".",
                points);
        }
    }

    /// <summary>Jobs in other pools stopped for want of what this pool's queued jobs would make.</summary>
    private static int Downstream(
        IReadOnlyList<WorklistItem> items,
        Dictionary<int, List<(WorklistItem Item, WorklistShortage Short)>> index,
        IndustryPool pool)
    {
        var stuck = items
            .Where(i => i.Pool == pool && i.TypeId > 0 && i.Readiness != WorklistReadiness.Ready)
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
    /// ⚠️ One measurement, two decisions. A BPO is bought off the market; a formula is a
    /// different market and often a different character, so one line would hide which of the two
    /// the reader is being asked to spend on.
    /// </summary>
    private static IEnumerable<Observation> PrintObservations(
        IReadOnlyList<ItemBandwidth> prints, bool reactions)
    {
        var blocking = prints
            .Where(p => p.IsReaction == reactions
                     && p.ContentionPercent > 0
                     && (p.BlockedNow > 0 || p.StalledTasks > 0))
            .OrderByDescending(p => p.ContentionPercent)
            .ThenByDescending(p => p.StalledTasks)
            .ToList();
        if (blocking.Count == 0) yield break;

        var thing = reactions ? "formula" : "blueprint";

        yield return new Observation(
            reactions ? "formulas" : "prints",
            DistinctStopped(blocking.Select(p => p.Tasks)) + blocking.Sum(p => p.BlockedNow),
            $"Buy {blocking.Count:N0} {thing}(s)",
            More(blocking.Count),
            // ⚠️ The gain, not the evidence. How often every copy was busy is what identified the
            // row and belongs on the tab that shows it; what a reader needs here is how much
            // throughput one purchase buys.
            [.. blocking.Take(MaxNamed).Select(p =>
                $"{p.ProductName} — buy 1 of {p.Prints:N0} owned "
              + $"(+{(p.Prints > 0 ? 100.0 / p.Prints : 100):N0}% output)")]);
    }

    // ── Buying ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What to buy, taken from the worklist's own purchase tasks.
    ///
    /// <para>⚠️ Derived from the Buy tasks rather than from Item Contention's shortages, because
    /// the two answer different questions and the summary must not contradict the list it
    /// summarises. Contention's MustBuy is a per-job, sequential test — each planned job reserves
    /// its materials and a later one that cannot get its share is marked short — so it fires
    /// when the plan's cumulative appetite exceeds stock, not when a shelf is empty. It told the
    /// reader to buy 40,698 Tungsten while 226,037 sat on hand against a level of 200,100, and
    /// the worklist raised no task for it, correctly.</para>
    ///
    /// <para>MaterialPurchaseGenerator is the component that decides purchases: one pooled plan,
    /// netted against all supply, with orders already placed and material in production taken
    /// off. Anything it did not raise is not a purchase.</para>
    /// </summary>
    private static IEnumerable<Observation> BuyObservations(
        IReadOnlyList<WorklistItem> items,
        Dictionary<int, List<(WorklistItem Item, WorklistShortage Short)>> index)
    {
        var buys = items
            .Where(i => i.Kind == WorklistKind.Buy && i.Title.Length > 0)
            .Select(i => (Item: i, Stalled: i.TypeId > 0 ? TaskChain.Stalled(index, i.TypeId) : []))
            .OrderByDescending(x => x.Stalled.Count)
            .ThenByDescending(x => x.Item.Priority)
            .ToList();
        if (buys.Count == 0) yield break;

        yield return new Observation(
            "buying",
            DistinctStopped(buys.Select(x => (IEnumerable<ShortageTask>)x.Stalled)),
            $"Place {buys.Count:N0} buy order(s)",
            More(buys.Count),
            [.. buys.Take(MaxNamed).Select(x =>
                x.Item.Title
              + (x.Stalled.Count > 0 ? $" — unblocks {x.Stalled.Count:N0} task(s)" : ""))]);
    }

    // ── Buffers ───────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> LevelObservations(IReadOnlyList<ItemShortage> shortages)
    {
        var unset = shortages
            .Where(s => s.BlockedTasks > 0 && s.Level <= 0 && s.UsedPerDay > 0)
            .OrderByDescending(s => s.UsedPerDay)
            .ToList();

        // ⚠️ Only where the LEVEL is too small, not where today is short. A level carrying
        // 35 days that was raided during a build wave is a buffer doing its job.
        var thin = shortages
            .Where(s => s.BlockedTasks > 0 && s.Level > 0 && s.UsedPerDay > 0
                     && s.UsedPerDay * TargetCoverDays > s.Level)
            .OrderByDescending(s => s.UsedPerDay * TargetCoverDays / s.Level)
            .ToList();

        if (unset.Count == 0 && thin.Count == 0) yield break;

        var points = new List<string>();

        foreach (var s in unset.Take(MaxNamed / 2))
            points.Add($"{s.Name} — set level to {Suggest(s)}");

        foreach (var s in thin.Take(MaxNamed))
            points.Add($"{s.Name} — raise level {s.Level:N0} to {Suggest(s)}");

        yield return new Observation(
            "levels",
            DistinctStopped(unset.Concat(thin).Select(s => s.Tasks ?? [])),
            $"Change {unset.Count + thin.Count:N0} inventory level(s)",
            $"Sized for {TargetCoverDays} days at the current draw."
          + More(unset.Count + thin.Count),
            points);
    }

    /// <summary>⚠️ Sized on the draw, not on the shortfall. A level set to today's gap empties
    /// again on the next wave.</summary>
    private static string Suggest(ItemShortage s) =>
        Math.Ceiling(s.UsedPerDay * TargetCoverDays).ToString("N0");

    // ── Hauling ───────────────────────────────────────────────────────────────

    private static IEnumerable<Observation> HaulObservations(IReadOnlyList<HaulBlock> hauls)
    {
        if (hauls.Count == 0) yield break;

        // Only what nobody has raised: a trip already on the list needs no decision.
        var idle = hauls.Where(h => h.HaulTasks == 0).ToList();
        if (idle.Count == 0) yield break;

        // ⚠️ Trips serving several stopped jobs first. One row per job is the grid's business;
        // what belongs here is the delivery worth making.
        var shared = SharedHauls.Find(idle).Where(h => !h.Raised).ToList();

        var points = shared.Count > 0
            ? [.. shared.Take(MaxNamed).Select(h =>
                  $"{h.TypeName} to {h.StationName} — {h.Units:N0} ({h.Volume:N0} m3), "
                + $"restarts {h.Jobs:N0} jobs")]
            : idle.OrderByDescending(h => h.StalledTasks).Take(MaxNamed)
                  .Select(h => $"{h.Title} at {h.StationName} — {h.Volume:N0} m3")
                  .ToList();

        yield return new Observation(
            "hauling",
            idle.Count + DistinctStopped(idle.Select(h => h.Tasks)),
            $"Raise {(shared.Count > 0 ? shared.Count : idle.Count):N0} haul(s)",
            $"{idle.Count:N0} job(s) waiting on material already owned, with nothing moving.",
            points);
    }

    /// <summary>Says only that a list was cut, and where the rest is.</summary>
    private static string More(int total) =>
        total > MaxNamed ? $" Worst {MaxNamed} shown; the rest are on the tab." : "";

    private static string Pool(IndustryPool p) => p switch
    {
        IndustryPool.Manufacturing => "manufacturing",
        IndustryPool.Reaction      => "reaction",
        _                          => "science",
    };
}
