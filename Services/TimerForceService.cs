using System.Collections.Concurrent;

namespace EveConsole.Services;

/// <summary>
/// Runs the work behind a timer key on demand, for the Force Now buttons on the Timers tab.
///
/// <para><b>⚠️ Why this is needed at all.</b> Force Now called
/// <see cref="EsiPollingService.ResetCallTime"/> for every row, which clears the in-memory
/// schedule the polling loop consults — correct for the character and corporation endpoints, and
/// completely inert for everything under "Other". Those are not polled by that loop: the market
/// refresh, the price history sweep, the contract sweeps and the LP store sweep each run on their
/// own service's own timer, and none of them look at those dictionaries. The button cleared
/// entries nothing reads and reported success by doing nothing at all.</para>
///
/// <para>So a service that owns its own schedule registers here how to run itself now, and the
/// button asks this first. Anything not registered falls back to the reset, which remains right
/// for the endpoints the polling loop does drive.</para>
/// </summary>
public class TimerForceService(AppErrorLogger errorLogger)
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _actions = new();
    private readonly ConcurrentDictionary<string, byte>                          _running = new();

    /// <summary>Called by a service that runs on its own timer, naming the key its row uses.</summary>
    public void Register(string timerKey, Func<CancellationToken, Task> runNow)
        => _actions[timerKey] = runNow;

    public bool CanForce(string timerKey) => _actions.ContainsKey(timerKey);

    /// <summary>Whether this key is running right now, so the button can say so.</summary>
    public bool IsRunning(string timerKey) => _running.ContainsKey(timerKey);

    /// <summary>
    /// Starts the work and returns immediately — a market refresh takes minutes and the button
    /// must not freeze the settings window waiting for it.
    ///
    /// <para>Refuses a second run of the same key while the first is going: the timer that owns it
    /// may fire mid-force, and these sweeps are not written to run twice at once.</para>
    /// </summary>
    public bool TryForce(string timerKey)
    {
        if (!_actions.TryGetValue(timerKey, out var run)) return false;
        if (!_running.TryAdd(timerKey, 0)) return true;   // already going, which is what was asked for

        _ = Task.Run(async () =>
        {
            try { await run(CancellationToken.None); }
            catch (Exception ex) { errorLogger.Log(nameof(TimerForceService), $"force {timerKey}", ex); }
            finally { _running.TryRemove(timerKey, out _); }
        });

        return true;
    }
}
