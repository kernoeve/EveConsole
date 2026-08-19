using Avalonia.Threading;

namespace EveConsole.Services;

/// <summary>
/// Runs an expensive probe off the UI thread, no more often than a set interval, and posts the
/// result back for display.
///
/// <para>Written for the log-importer settings panels, whose two-second progress poll also
/// re-checked every configured log directory with <see cref="Directory.Exists"/> — on the UI
/// thread. Locally that is free; against a UNC path to a machine that is asleep, each call blocks
/// on SMB for seconds, and the window froze once the negative cache expired. The paths change
/// perhaps twice in a session, so probing them at the same rate as a progress bar bought nothing
/// and cost the whole UI thread.</para>
///
/// <para>Throttled from the moment the probe <em>finishes</em>, not when it starts: a probe that
/// takes five seconds must not be re-run the instant it lands.</para>
/// </summary>
public sealed class ThrottledUiProbe(TimeSpan interval, Func<string> probe, Action<string> apply)
{
    private bool     _running;
    private DateTime _lastFinishedUtc = DateTime.MinValue;

    /// <summary>Run the probe if it is due. Cheap and safe to call from a fast timer — that is
    /// the point of it.</summary>
    public void Poke()
    {
        if (_running || DateTime.UtcNow - _lastFinishedUtc < interval) return;
        Run();
    }

    /// <summary>Run now regardless of the interval — for when the user has just changed
    /// something the probe reports on, and waiting would read as the change not taking.</summary>
    public void Force()
    {
        if (_running) return;
        Run();
    }

    private void Run()
    {
        _running = true;
        _ = Task.Run(() =>
        {
            string result;
            try { result = probe(); }
            catch (Exception ex) { result = $"Could not check — {ex.Message}"; }

            Dispatcher.UIThread.Post(() =>
            {
                apply(result);
                _lastFinishedUtc = DateTime.UtcNow;
                _running         = false;
            });
        });
    }
}
