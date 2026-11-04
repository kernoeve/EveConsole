namespace EveConsole.Services.Worklist;

/// <summary>
/// What a load would do for the jobs stopped for want of it, once it is down.
///
/// <para>⚠️ In runs, not whole jobs. A job the materials cannot cover is cut where they run out, one
/// row to start and one for the rest, the moment the materials are there. Judged whole, a 91-run
/// job with enough for 17 read as not starting at all, under the very trip that would start those
/// 17, and the last jobs of a long queue, which nobody will reach for weeks, held up the first.</para>
///
/// <para>Worked through in plan order against one pile per item, so a job ahead takes its share
/// first and the same units are not promised twice. Only a job with its slot and print takes
/// anything: one without would not start, so it leaves the pile for those that will.</para>
/// </summary>
internal static class ArrivalPlan
{
    /// <summary>What a load does for one item's jobs, in units of that item.</summary>
    /// <param name="Starts">Units that start once it is down.</param>
    /// <param name="Waits">Units it covers that would then wait on a slot.</param>
    /// <param name="WaitsForPrint">Units it covers that would then wait on a print.</param>
    /// <param name="Rest">Units still short of something.</param>
    /// <param name="Missing">What those units still lack, once this load is down.</param>
    /// <param name="Jobs">How many of the jobs start, each being a row of its own by then.</param>
    public sealed record Outcome(
        WorklistItem First, long Starts, long Waits, long WaitsForPrint, long Rest,
        IReadOnlyList<WorklistLine> Missing, int Jobs, int Priority);

    /// <param name="jobs">Every job on the list. Those stopped for materials and short of something
    /// in the load are the ones it speaks for.</param>
    /// <param name="load">What lands, by station and item.</param>
    public static List<Outcome> Run(
        IEnumerable<WorklistItem> jobs, IReadOnlyDictionary<long, Dictionary<int, long>> load)
    {
        var rows = jobs
            .Where(j => j.Stop == WorklistStop.Materials && j.Runs > 0 && j.Bill.Count > 0
                     && load.TryGetValue(j.LocationId, out var here)
                     && j.Shortages.Any(s => here.ContainsKey(s.TypeId)))
            .OrderBy(j => j.PlanSequence)
            .ToList();
        if (rows.Count == 0) return [];

        // ⚠️ The lowest any row saw. Only jobs that start reserve anything, so what a row saw only
        // ever falls down the plan, and the last figure is what is really there after them.
        var pile = new Dictionary<(long Site, int TypeId), double>();
        foreach (var j in rows)
            foreach (var b in j.Bill)
                pile[(j.LocationId, b.TypeId)] = pile.TryGetValue((j.LocationId, b.TypeId), out var seen)
                    ? Math.Min(seen, b.Here) : b.Here;
        foreach (var key in pile.Keys.ToList())
            pile[key] += load[key.Site].GetValueOrDefault(key.TypeId);

        var results = rows.Select(j =>
        {
            var possible = j.Bill.Min(b =>
                (int)Math.Min(j.Runs, Math.Floor(pile[(j.LocationId, b.TypeId)] / PerRun(j, b) + 1e-9)));
            var starts = j.HasSlot ? possible : 0;
            foreach (var b in j.Bill)
                pile[(j.LocationId, b.TypeId)] -= PerRun(j, b) * starts;
            return (Job: j, Starts: starts, Waits: j.HasSlot ? 0 : possible);
        }).ToList();

        return results
            .GroupBy(r => r.Job.TypeId)
            .Select(g =>
            {
                long Units(int runs, WorklistItem j) => j.Quantity * runs / j.Runs;

                // Everything the rest of these jobs want, less whatever is still in the pile: the
                // pile is one, however many jobs each look at it.
                var missing = g
                    .SelectMany(r => r.Job.Bill.Select(b => (r.Job, Bill: b,
                                     Wanted: PerRun(r.Job, b) * (r.Job.Runs - r.Starts - r.Waits))))
                    .GroupBy(x => (x.Job.LocationId, x.Bill.TypeId))
                    .Select(x => (x.First().Bill,
                                  Short: (long)Math.Ceiling(x.Sum(y => y.Wanted) - Math.Max(0, pile[x.Key]))))
                    .Where(x => x.Short > 0)
                    .Select(x => new WorklistLine(x.Bill.TypeId, x.Bill.TypeName, x.Short) { Print = x.Bill.Print })
                    .ToList();

                return new Outcome(
                    g.First().Job,
                    g.Sum(r => Units(r.Starts, r.Job)),
                    g.Where(r => !r.Job.ThenNeedsPrint).Sum(r => Units(r.Waits, r.Job)),
                    g.Where(r =>  r.Job.ThenNeedsPrint).Sum(r => Units(r.Waits, r.Job)),
                    g.Sum(r => Units(r.Job.Runs - r.Starts - r.Waits, r.Job)),
                    missing,
                    g.Count(r => r.Starts > 0),
                    g.Max(r => r.Job.Priority));
            })
            .ToList();
    }

    private static double PerRun(WorklistItem job, WorklistBillLine line) => (double)line.Wanted / job.Runs;
}
