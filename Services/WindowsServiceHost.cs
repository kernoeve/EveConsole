using System.Runtime.Versioning;
using System.ServiceProcess;
using Avalonia.Threading;

namespace EveConsole.Services;

/// <summary>
/// Runs the background worker under the Windows service control manager.
///
/// <para>The work itself is identical to <c>--headless</c>; what differs is who starts and stops
/// it, and that the SCM expects an answer promptly. So the worker gets a thread of its own and
/// OnStart returns immediately — a service that does its startup inline is reported as failing to
/// start while it is still perfectly busy starting.</para>
///
/// <para>⚠️ A service cannot show anything. Session 0 has no interactive desktop, which is why the
/// tray icon has to be a separate process in the user's own session rather than something this
/// could put on screen.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceHost : ServiceBase
{
    /// <summary>What the SCM knows it as. Shared with the installer, so the two cannot drift.</summary>
    public const string ServiceName = "EveConsoleWorker";
    public const string DisplayName = "EVE Console background worker";

    private readonly Func<CancellationToken, int> _run;
    private readonly CancellationTokenSource      _stopping = new();
    private Thread?                               _worker;

    /// <param name="run">
    /// Sets Avalonia up and runs the dispatcher loop until the token is cancelled. Passed in rather
    /// than called directly so this class owns the service lifecycle and nothing else.
    /// </param>
    public WindowsServiceHost(Func<CancellationToken, int> run)
    {
        _run                       = run;
        base.ServiceName           = ServiceName;
        CanShutdown                = true;
        CanStop                    = true;
        AutoLog                    = true;   // start/stop land in the Windows event log for free
    }

    protected override void OnStart(string[] args)
    {
        // ⚠️ STA, like Program.Main. Avalonia asks for it on Windows, and a service's own thread is
        // MTA by default — so a worker started on the SCM's thread would be initialising a UI
        // framework in the wrong apartment.
        _worker = new Thread(() =>
        {
            try { _run(_stopping.Token); }
            catch (Exception ex)
            {
                // Nowhere to draw and possibly no console. The event log is what a service is
                // expected to use and what anybody debugging one will look at first.
                try { EventLog.WriteEntry($"EVE Console worker stopped: {ex.Message}",
                                          System.Diagnostics.EventLogEntryType.Error); }
                catch { /* even that can fail; the app's own error log still has it */ }

                // Tell the SCM this was a failure so a configured recovery action can act on it.
                ExitCode = 1;
                Stop();
            }
        })
        {
            IsBackground = false,
            Name         = "EveConsole worker",
        };

        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    protected override void OnStop()
    {
        _stopping.Cancel();

        // ⚠️ Waited for, not abandoned. The shutdown releases the lease so another client picks the
        // work up on its next tick rather than after the server times the socket out — and that
        // only happens if the process is still alive long enough to do it. Windows allows a service
        // roughly 30 seconds before it stops being patient; this stays well inside that.
        _worker?.Join(TimeSpan.FromSeconds(20));
    }

    protected override void OnShutdown() => OnStop();   // machine going down is a stop with less time
}
