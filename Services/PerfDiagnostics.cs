namespace EveConsole.Services;

/// <summary>
/// The performance instrumentation built during the UI-stall investigation. Two switches, not
/// one, because the two measurements turned out to be worth very different amounts.
///
/// <para>⚠️ Neither writes anywhere but the error log, so a switch left on costs log volume in
/// every session and buys nothing when nothing is being chased. That is the whole reason these
/// exist as switches rather than as code that always runs.</para>
/// </summary>
public static class PerfDiagnostics
{
    /// <summary>
    /// The <see cref="UiStallMonitor"/> heartbeat: how long the UI thread was actually blocked.
    ///
    /// <para><b>On.</b> This is the one that says whether the window froze, and it is the only
    /// instrumentation that can — <see cref="TimedMainThreadScheduler"/> is always on but sees
    /// only work dispatched through ReactiveUI's scheduler, which misses everything after the
    /// first await in an async handler. A healthy session logs nothing at all, so the cost of
    /// leaving it on is only what it finds.</para>
    /// </summary>
    public static bool UiStalls { get; set; } = true;

    /// <summary>
    /// The Overview's per-section wall-clock timings.
    ///
    /// <para><b>Off.</b> ⚠️ Wall clock including background queries, so a slow section does not
    /// mean anything froze — and in practice it named the same handful of sections in every
    /// session without one of them being a fault. Noisy on its own: it was only ever useful
    /// cross-referenced against a stall that had already been reported, and by then the stall
    /// report says what is needed.</para>
    /// </summary>
    public static bool OverviewSections { get; set; }
}
