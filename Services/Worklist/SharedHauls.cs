using EveConsole.Localization;

namespace EveConsole.Services.Worklist;

/// <summary>
/// One delivery that would restart more than one job.
/// </summary>
/// <param name="Jobs">Stopped jobs at that destination waiting on this item. ⚠️ Waiting on it —
/// not restarted by it. Most are short of other things as well.</param>
/// <param name="Unblocks">Of those, the ones this delivery would actually restart: the jobs whose
/// ONLY outstanding shortage is this item. ⚠️ The distinction is the whole point of the number.
/// A delivery wanted by six jobs of which one is short of nothing else restarts one job, and
/// reporting six is a promise the trip cannot keep.</param>
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
    int    Unblocks,
    int    Stalled,
    bool   Raised)
{
    /// <summary>
    /// The delivery in one line. A whole sentence per case for the delivery itself; the two
    /// remarks after it are clauses of their own, joined by the dash.
    /// </summary>
    public string Line
    {
        get
        {
            // The item as the screen names it; TypeName itself stays English.
            var item = SdeNames.Type(TypeId, TypeName);

            var delivery = Unblocks <= 0
                ? string.Format(WorklistText.SharedTripWanted, item, StationName, Units, Volume, Jobs)
                : Stalled > 0
                    ? string.Format(WorklistText.SharedTripRestartsBehind,
                                    item, StationName, Units, Volume, Unblocks, Stalled)
                    : string.Format(WorklistText.SharedTripRestarts, item, StationName, Units, Volume, Unblocks);

            var others = Unblocks > 0 && Jobs > Unblocks
                ? " — " + string.Format(WorklistText.SharedTripOthersShort, Jobs - Unblocks)
                : "";

            return delivery + others + " — "
                 + (Raised ? WorklistText.SharedTripRaised : WorklistText.SharedTripNothingMoving);
        }
    }
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

                // ⚠️ Only the jobs this delivery finishes the waiting for. A HaulBlock lists every
                // shortage the job has in Wants, so a job with one want is short of this and
                // nothing else -- it starts when the crate lands. A job with three wants does not,
                // however much of this you bring, and counting it here was the tool promising a
                // restart it had no way to deliver.
                g.Where(x => x.Block.Wants.Count == 1)
                 .Select(x => x.Block.TaskKey).Distinct().Count(),

                // ⚠️ Behind the jobs it actually restarts, for the same reason. Work waiting on a
                // job that stays stopped is not freed by this trip.
                g.Where(x => x.Block.Wants.Count == 1).Sum(x => x.Block.StalledTasks),
                g.Any(x => x.Block.HaulTasks > 0)))
            .Where(h => h.Jobs > 1)

            // Ranked by what the trip actually achieves, then by how much it is wanted. Ordering
            // on Jobs alone put a delivery six jobs want but none can use above one that starts
            // two immediately.
            .OrderByDescending(h => h.Unblocks + h.Stalled)
            .ThenByDescending(h => h.Unblocks)
            .ThenByDescending(h => h.Jobs)
            .ToList();

        return byDelivery;
    }
}
