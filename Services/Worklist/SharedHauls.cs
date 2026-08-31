namespace EveConsole.Services.Worklist;

/// <summary>
/// One delivery that would restart more than one job.
/// </summary>
/// <param name="Jobs">Stopped jobs at that destination waiting on this item.</param>
/// <param name="Stalled">Everything stopped behind those jobs, so a trip that looks small can
/// still be the one worth making.</param>
/// <param name="Raised">Whether any haul on the list already carries it there.</param>
public sealed record SharedHaul(
    long   StationId,
    string StationName,
    int    TypeId,
    string TypeName,
    long   Units,
    double Volume,
    int    Jobs,
    int    Stalled,
    bool   Raised)
{
    public string Line =>
        $"{TypeName} to {StationName}: one delivery of {Units:N0} ({Volume:N0} m3) restarts "
      + $"{Jobs:N0} job(s)"
      + (Stalled > 0 ? $" and the {Stalled:N0} task(s) behind them" : "")
      + (Raised ? " — already raised." : " — nothing moving.");
}

/// <summary>
/// Deliveries that serve several stopped jobs at once.
///
/// <para>⚠️ The Hauling grid is one row per stopped job, which is right for "what is stuck" and
/// wrong for "what should I move". Four jobs at one station each short of the same Self-
/// Harmonizing Power Cores are four rows and one trip, and the row-per-job view cannot say that
/// — it reports the trip four times with no sign that they are the same errand.</para>
/// </summary>
public static class SharedHauls
{
    /// <summary>Deliveries wanted by more than one job, worst first.</summary>
    public static List<SharedHaul> Find(IReadOnlyList<HaulBlock> blocks)
    {
        var byDelivery = blocks
            .SelectMany(b => b.Wants.Select(w => (Block: b, Want: w)))
            .GroupBy(x => (x.Block.StationId, x.Want.TypeId))
            .Select(g => new SharedHaul(
                g.First().Block.StationId,
                g.First().Block.StationName,
                g.Key.TypeId,
                g.First().Want.TypeName,
                // ⚠️ Summed across the jobs, not maxed. Each job wants its own units, and a
                // delivery sized for the largest of them restarts one job and leaves the rest
                // exactly as stopped as they were.
                g.Sum(x => x.Want.Units),
                g.Sum(x => x.Want.Volume),
                g.Select(x => x.Block.TaskKey).Distinct().Count(),
                g.Sum(x => x.Block.StalledTasks),
                g.Any(x => x.Block.HaulTasks > 0)))
            .Where(h => h.Jobs > 1)
            .OrderByDescending(h => h.Jobs + h.Stalled)
            .ThenByDescending(h => h.Jobs)
            .ToList();

        return byDelivery;
    }
}
