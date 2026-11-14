namespace EveConsole.Services.Intel;

/// <summary>
/// One run as a single reading of the snapshot sees it: a system's run of unnamed counts, or of one
/// flag, from its oldest report still in the window to its newest.
/// </summary>
/// <param name="Kind"><see cref="IntelEpisodeRegister.Counts"/>, or the flag's bit.</param>
/// <param name="Peak">For counts: the most counted at once in the reports read.</param>
public readonly record struct IntelEpisodeRun(int SystemId, int Kind, DateTimeOffset Start, DateTimeOffset Last, int Peak);

/// <summary>
/// What keeps a run of counts, or of one flag, in a system the same run while it goes on: when it began,
/// its newest report, and (for counts) the most it counted at once. In memory, per process, like
/// <see cref="IntelCampRegister"/>.
///
/// <para>Needed because the snapshot reads back only its window: bubbles or a count called every quarter
/// hour for two hours lose their first reports from every reading, and a run that began at its oldest
/// report read back would begin again each time one left the window. The intel alarm keys a count or a
/// flag by its run's start, so each such move would be said again as news.</para>
///
/// <para>A reading's run continues the remembered one when it reaches back to the remembered run's
/// newest report: readings come every few seconds and the window is at least an hour, so that report is
/// still read back while the run goes on. When it does not, something broke the run (a clear, a gap
/// longer than the lifetime, a report with nobody counted) and this is a new one. A run keeps the
/// highest count it reached, so a count that falls is not news, even once its highest report has left
/// the window.</para>
///
/// <para>A run no reading shows is kept while its newest report is still in the window (a count said to
/// have moved on can be called again), and forgotten after that, when nothing can continue it.</para>
///
/// <para>After a restart or a move to another PC the register starts empty, so a run older than the
/// window may be taken for a new one once: the same accepted risk as the camp register.</para>
/// </summary>
public sealed class IntelEpisodeRegister
{
    /// <summary>The kind of a run of unnamed counts (a flag's run is its bit, never 0).</summary>
    public const int Counts = 0;

    private sealed class Episode
    {
        public DateTimeOffset Start;
        public DateTimeOffset Last;
        public int Peak;
    }

    private readonly Dictionary<(int System, int Kind), Episode> _runs = [];
    private readonly object _lock = new();

    public int Count { get { lock (_lock) return _runs.Count; } }

    /// <summary>
    /// Every run of one reading, each given back with its start and peak carried from the run it
    /// continues; runs nothing can continue any more (newest report before <paramref name="from"/>,
    /// the window's start) are forgotten.
    /// </summary>
    public Dictionary<(int System, int Kind), (DateTimeOffset Start, int Peak)> Apply(
        IReadOnlyList<IntelEpisodeRun> runs, DateTimeOffset from)
    {
        var result = new Dictionary<(int, int), (DateTimeOffset, int)>(runs.Count);
        lock (_lock)
        {
            foreach (var run in runs)
            {
                var key = (run.SystemId, run.Kind);
                if (_runs.TryGetValue(key, out var e) && run.Start <= e.Last && run.Last >= e.Start)
                {
                    if (run.Start < e.Start) e.Start = run.Start;
                    if (run.Last > e.Last)   e.Last  = run.Last;
                    if (run.Peak > e.Peak)   e.Peak  = run.Peak;
                }
                else
                {
                    _runs[key] = e = new Episode { Start = run.Start, Last = run.Last, Peak = run.Peak };
                }
                result[key] = (e.Start, e.Peak);
            }

            foreach (var (key, e) in _runs.ToList())
                if (e.Last < from && !result.ContainsKey(key)) _runs.Remove(key);
        }
        return result;
    }
}
