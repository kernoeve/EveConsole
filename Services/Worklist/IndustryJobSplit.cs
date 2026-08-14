namespace EveConsole.Services.Worklist;

/// <summary>One installable job: how many runs, on which print, and how long it will take.</summary>
/// <param name="Seconds">Total run time, or zero when the blueprint's base time is unknown.</param>
public sealed record SplitJob(
    BlueprintStock Print, int Runs, double Seconds, int Index, int Of);

/// <summary>
/// The result of turning a shortfall into jobs that could actually be installed.
/// </summary>
/// <param name="Jobs">In the order they should be started.</param>
/// <param name="RunsUnassigned">Runs the demand still calls for that no free print can carry.
/// Reported rather than dropped: "start this, and 20,000 runs still need a print" is the true
/// state, and silently planning less than the shortfall would read as the shortfall being met.</param>
public sealed record JobSplit(IReadOnlyList<SplitJob> Jobs, long RunsUnassigned);

/// <summary>
/// Turns "we are short 25,000 of this" into the jobs that would produce it.
///
/// <para>Three separate ceilings apply, and the smallest wins for any given job:</para>
/// <list type="bullet">
/// <item><b>Job length.</b> The player's configured maximum, converted to runs at this job's real
/// per-run time. This is the reason the split exists — a build whose output arrives in one lump
/// a week from now is a week of the shortfall going unmet, and one slot parked for the
/// duration.</item>
/// <item><b>The blueprint's maximum runs.</b> A hard game limit per print, and often the tighter
/// one: capital components cap at 40 runs whatever the clock says.</item>
/// <item><b>Licensed runs on a copy.</b> A 40-run copy cannot carry a 100-run job.</item>
/// </list>
///
/// <para>And a fourth ceiling on the count rather than the size: <b>one print per job</b>. A
/// print is locked while its job runs, so five concurrent jobs need five prints. Planning more
/// jobs than there are prints would produce work that cannot be started — the precise failure
/// this tool exists to avoid — so the surplus is reported as runs still needing a print.</para>
///
/// <para>The length ceiling is evaluated per print rather than once, because time efficiency is
/// a property of the print. A TE0 copy runs a fifth slower than a TE20 original, so under the
/// same day limit it earns a correspondingly shorter job.</para>
/// </summary>
public static class IndustryJobSplit
{
    /// <param name="runsNeeded">Total runs the shortfall calls for.</param>
    /// <param name="perRunSeconds">Real seconds per run on a given print, or null when the
    /// blueprint has no base time on record — in which case the clock cannot bound anything and
    /// only the blueprint limits apply.</param>
    /// <param name="maxDays">Configured job length ceiling; zero or less means no limit.</param>
    /// <param name="maxProductionLimit">The blueprint's own per-job run cap; zero means none.</param>
    /// <param name="prints">Usable prints, best first.</param>
    public static JobSplit Plan(
        long                            runsNeeded,
        Func<BlueprintStock, double?>   perRunSeconds,
        double                          maxDays,
        int                             maxProductionLimit,
        IReadOnlyList<BlueprintStock>   prints)
    {
        if (runsNeeded <= 0) return new JobSplit([], 0);

        var jobs      = new List<SplitJob>();
        var remaining = runsNeeded;

        foreach (var print in prints)
        {
            if (remaining <= 0) break;

            var secs = perRunSeconds(print);
            var cap  = long.MaxValue;

            if (maxDays > 0 && secs is > 0)
            {
                // Floored at one run: when a single run already exceeds the configured length
                // there is no smaller job to suggest, and proposing zero runs would be no
                // suggestion at all. The duration on the item makes the overrun evident.
                cap = Math.Max(1, (long)(maxDays * 86400.0 / secs.Value));
            }

            if (maxProductionLimit > 0) cap = Math.Min(cap, maxProductionLimit);
            cap = Math.Min(cap, print.Runs);   // originals carry int.MaxValue
            if (cap <= 0) continue;

            var runs = (int)Math.Min(remaining, cap);
            jobs.Add(new SplitJob(print, runs, (secs ?? 0) * runs, jobs.Count + 1, 0));
            remaining -= runs;
        }

        // Of is only knowable once the walk finishes, so it is stamped here rather than guessed.
        var total = jobs.Count;
        return new JobSplit(jobs.Select(j => j with { Of = total }).ToList(), Math.Max(0, remaining));
    }

    /// <summary>Runs needed to produce a quantity, given what one run yields.</summary>
    public static long RunsFor(long units, int quantityPerRun) =>
        quantityPerRun <= 1 ? units : (units + quantityPerRun - 1) / quantityPerRun;

    /// <summary>A duration in the terms an industrialist reads it in.</summary>
    public static string Duration(double seconds) => seconds switch
    {
        <= 0    => "",
        < 3600  => $"{seconds / 60:0}m",
        < 86400 => $"{seconds / 3600:0.#}h",
        _       => $"{seconds / 86400:0.#}d",
    };
}
