namespace EveConsole.Services;

/// <summary>
/// The performance instrumentation built during the UI-stall investigation: the
/// <see cref="UiStallMonitor"/> heartbeat and the Overview's slow-section timings.
///
/// <para><b>Currently ON — hangs are being chased.</b> Turned back on 2026-08-22 after freezes
/// that the always-on <see cref="TimedMainThreadScheduler"/> could not account for: it reports
/// only work that goes through ReactiveUI's scheduler, and the stalls in question produced no
/// entry from it at all, which means whatever blocked the UI thread never went through Rx.</para>
///
/// <para><b>Why it defaults off.</b> ⚠️ Neither writes anywhere but the error log, and once the
/// investigation they were built for was over the two of them accounted for roughly a third of
/// every row logged — none of it an error. A log that size is one nobody reads, which costs more
/// than the measurements are worth when nothing is being chased. Set this back to false when the
/// current hunt is done; everything they record keeps working either way.</para>
/// </summary>
public static class PerfDiagnostics
{
    public static bool Enabled { get; set; } = true;
}
