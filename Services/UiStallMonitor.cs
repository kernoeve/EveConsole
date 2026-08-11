using System.Diagnostics;
using Avalonia.Threading;

namespace EveConsole.Services;

/// <summary>
/// Records how long the UI thread goes unresponsive, so a reported "it freezes" can be turned
/// into a time and a duration instead of a guess.
///
/// <para>The method is a heartbeat: a background timer stamps the clock and posts a callback to
/// the dispatcher. The dispatcher runs it only once it has finished whatever it was doing, so the
/// gap between posting and running IS the stall — no profiler, no instrumentation of the code
/// being blamed, and it cannot miss a stall caused by something we never suspected.</para>
///
/// <para>Deliberately not a diagnostic left running for its own sake: it costs one queued
/// callback every half second, and it writes to the error log only when a stall exceeds the
/// threshold, so a healthy session logs nothing at all.</para>
/// </summary>
public sealed class UiStallMonitor(AppErrorLogger errorLogger)
{
    /// <summary>How often to take the UI thread's pulse.</summary>
    private static readonly TimeSpan PingEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>Below this a late callback is ordinary scheduling noise, not a freeze. A stall
    /// this long is already visible as a hitch.</summary>
    private const int ReportAboveMs = 750;

    private System.Timers.Timer? _timer;

    /// <summary>Longest stall seen this session, for the status/diagnostics surface.</summary>
    public int WorstStallMs { get; private set; }

    /// <summary>How many stalls over the threshold have been seen this session.</summary>
    public int StallCount { get; private set; }

    /// <summary>Set while a ping is outstanding, so a long stall produces one report rather than
    /// one per tick — otherwise a five second freeze would log ten times and hide its own size.</summary>
    private volatile bool _pending;

    public void Start()
    {
        if (_timer is not null) return;

        _timer = new System.Timers.Timer(PingEvery.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) =>
        {
            if (_pending) return;

            _pending = true;
            var sent = Stopwatch.GetTimestamp();

            // Background priority on purpose: it must queue behind real UI work, so what it
            // measures is the thread being genuinely busy rather than this jumping the queue.
            Dispatcher.UIThread.Post(() =>
            {
                _pending = false;

                var waited = (int)Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                if (waited < ReportAboveMs) return;

                StallCount++;
                if (waited > WorstStallMs) WorstStallMs = waited;

                errorLogger.Log(nameof(UiStallMonitor), "UI thread stalled",
                    $"The UI thread was busy for {waited:N0} ms " +
                    $"(stall {StallCount} this session, worst {WorstStallMs:N0} ms).");
            }, DispatcherPriority.Background);
        };

        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
    }
}
