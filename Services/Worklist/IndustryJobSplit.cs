namespace EveConsole.Services.Worklist;

/// <summary>Why a job is the size it is — the ceiling that actually bound it.</summary>
public enum SplitCap
{
    /// <summary>Nothing bound it: this job is the whole remaining shortfall.</summary>
    Demand,
    /// <summary>The configured maximum job length.</summary>
    JobLength,
    /// <summary>EVE's own thirty-day ceiling on a single job.</summary>
    GameLimit,
    /// <summary>Runs left on the copy.</summary>
    CopyRuns,
}

/// <summary>One installable job: how many runs, on which print, and how long it will take.</summary>
/// <param name="Seconds">Total run time, or zero when the blueprint's base time is unknown.</param>
/// <param name="Cap">What limited the run count. Carried so the item can say why a job is short of
/// the configured length instead of leaving the reader to guess that it is a bug.</param>
public sealed record SplitJob(
    BlueprintStock Print, int Runs, double Seconds, int Index, int Of, SplitCap Cap = SplitCap.Demand);

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
/// <item><b>EVE's thirty-day job limit.</b> The only run cap the game actually imposes on an
/// original, and the backstop when the player has configured no limit of their own.</item>
/// <item><b>Runs left on a copy.</b> A 40-run copy cannot carry a 100-run job.</item>
/// </list>
///
/// <para><b>Not</b> the blueprint's <c>maxProductionLimit</c>. That field reads like a per-job run
/// cap and is not one — originals are unlimited, bounded only by the thirty days. Trusting it put
/// capital components on 40-run jobs when the player installs 110 to 115, and it is contradicted
/// by their own history: 482 of 1,347 past jobs, and 1,428 of 4,357 owned copies, exceed it.</para>
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
    /// <summary>
    /// EVE's hard ceiling on how long one industry job may run. Verified against the industry
    /// window: a Capital Armor Plates original at TE16 in a rigged Raitaru offers a maximum of
    /// 576 runs, reported as a duration of 30d 00:54:00 — thirty days plus the final run.
    /// </summary>
    public const double GameMaxJobSeconds = 30 * 86400.0;


    /// <param name="runsNeeded">Total runs the shortfall calls for.</param>
    /// <param name="perRunSeconds">Real seconds per run on a given print, or null when the
    /// blueprint has no base time on record — in which case the clock cannot bound anything and
    /// only the blueprint limits apply.</param>
    /// <param name="maxDays">Configured job length ceiling; zero or less means no limit.</param>
    /// <param name="prints">Usable prints, best first.</param>
    public static JobSplit Plan(
        long                            runsNeeded,
        Func<BlueprintStock, double?>   perRunSeconds,
        double                          maxDays,
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
            var why  = SplitCap.Demand;

            if (secs is > 0)
            {
                // The game's own ceiling, matched to how the industry window computes it: the run
                // that crosses thirty days is still allowed, so this is a ceiling rather than a
                // floor. Capital Armor Plates at 4,505.6s a run offers 576, not 575.
                cap = Math.Max(1, (long)Math.Ceiling(GameMaxJobSeconds / secs.Value));
                why = SplitCap.GameLimit;
            }

            if (maxDays > 0 && secs is > 0)
            {
                // Floored, because the configured value is a maximum the player chose and a job
                // that overran it would not be honouring it — and floored at one run, since when
                // a single run already exceeds the length there is no smaller job to suggest.
                var byClock = Math.Max(1, (long)(maxDays * 86400.0 / secs.Value));
                if (byClock < cap) { cap = byClock; why = SplitCap.JobLength; }
            }

            // Only a strictly tighter ceiling claims the reason, so the one named is the one that
            // actually cost runs rather than whichever happened to be tested last.
            if (print.Runs < cap)
            {
                cap = print.Runs;              // originals carry int.MaxValue
                why = SplitCap.CopyRuns;
            }
            if (cap <= 0) continue;

            var runs = (int)Math.Min(remaining, cap);
            if (remaining <= cap) why = SplitCap.Demand;
            jobs.Add(new SplitJob(print, runs, (secs ?? 0) * runs, jobs.Count + 1, 0, why));
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
