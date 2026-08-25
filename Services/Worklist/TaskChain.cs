namespace EveConsole.Services.Worklist;

/// <summary>
/// How far a shortage reaches, over the tasks on the list.
///
/// <para>Shared because two tabs ask the same question of the same data and must not answer it
/// differently: Item Contention asks what a missing material holds up, and BPO / Formula asks
/// what a blueprint's output holds up. Both are "which tasks are stopped, directly or behind
/// something that is", and one walk keeps the two counts commensurable.</para>
///
/// <para>⚠️ Over blocked TASKS, never over the recipe tree. Walking the tree reports a Leviathan
/// as downstream of a material whether or not anyone is building one — true about EVE, useless
/// about the queue.</para>
/// </summary>
public static class TaskChain
{
    /// <summary>
    /// Tasks indexed by the thing each one is short of.
    ///
    /// <para>⚠️ EVERY shortage, not only the ones a purchase would fix. A task stopped because
    /// the material is at another station is still stopped, and what it would have made is still
    /// missing from whatever waited on it — walking only the purchases cuts the chain at the
    /// first haulable link and drops everything above it.</para>
    /// </summary>
    public static Dictionary<int, List<(WorklistItem Item, WorklistShortage Short)>> Index(
        IReadOnlyList<WorklistItem> items) =>
        items
            .SelectMany(i => i.Shortages.Select(sh => (Item: i, Short: sh)))
            .GroupBy(x => x.Short.TypeId)
            .ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>
    /// Every task held up by a shortage of <paramref name="typeId"/>, nearest first.
    ///
    /// <para>A task stopped for want of this cannot produce its own output, so whatever is
    /// stopped for want of THAT is stopped by this too. The walk carries on until it reaches
    /// nothing new, which is also what stops it looping through a cycle.</para>
    /// </summary>
    public static List<ShortageTask> Stalled(
        Dictionary<int, List<(WorklistItem Item, WorklistShortage Short)>> index, int typeId)
    {
        var found = new List<ShortageTask>();
        var tasks = new HashSet<string>();
        var types = new HashSet<int> { typeId };
        var queue = new Queue<(int Type, int Hop)>();
        queue.Enqueue((typeId, 0));

        while (queue.Count > 0)
        {
            var (type, hop) = queue.Dequeue();
            if (!index.TryGetValue(type, out var stopped)) continue;

            foreach (var (task, sh) in stopped)
            {
                if (!tasks.Add(task.Key)) continue;

                found.Add(new ShortageTask(
                    "Stopped", hop, task.TypeName, task.Title,
                    task.Readiness.ToString(),
                    $"short {sh.Short:N0} of {sh.Wanted:N0} {sh.TypeName}"
                  + (sh.MustBuy ? "" : " (owned, but not where the job is)")));

                if (task.TypeId > 0 && types.Add(task.TypeId)) queue.Enqueue((task.TypeId, hop + 1));
            }
        }

        return [.. found.OrderBy(t => t.Hop)];
    }
}
