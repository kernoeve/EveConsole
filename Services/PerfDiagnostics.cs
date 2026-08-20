namespace EveConsole.Services;

/// <summary>
/// The performance instrumentation built during the UI-stall investigation: the
/// <see cref="UiStallMonitor"/> heartbeat and the Overview's slow-section timings.
///
/// <para><b>Off in the shipped app.</b> ⚠️ Neither writes anywhere but the error log, and once the
/// investigation they were built for was over the two of them accounted for roughly a third of
/// every row logged — none of it an error. A log that size is one nobody reads, which costs more
/// than the measurements are worth when nothing is being chased.</para>
///
/// <para>Set this to true — from here, or at startup before the services begin — to bring both
/// back the next time something needs measuring. Everything they record still works; the switch
/// only decides whether they run and report.</para>
/// </summary>
public static class PerfDiagnostics
{
    public static bool Enabled { get; set; }
}
