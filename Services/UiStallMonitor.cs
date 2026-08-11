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

            // Snapshot the two things that stall a UI thread without it running any code of
            // ours, so the log can tell the three causes apart rather than leaving it to
            // inference:
            //   • a blocking GC suspends every thread, including this one — if the pause time
            //     grew by about the stall, the UI thread was not busy, it was frozen;
            //   • thread pool starvation delays the dispatcher's own continuations — if work
            //     items are queued with no free workers, the blockage is elsewhere and the UI
            //     is only its most visible victim.
            // If neither moved, the UI thread genuinely ran our code for that long.
            var gcPause = GC.GetTotalPauseDuration();
            var gen2    = GC.CollectionCount(2);
            ThreadPool.GetAvailableThreads(out var freeWorkersBefore, out _);

            // Background priority on purpose: it must queue behind real UI work, so what it
            // measures is the thread being genuinely busy rather than this jumping the queue.
            Dispatcher.UIThread.Post(() =>
            {
                _pending = false;

                var waited = (int)Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                if (waited < ReportAboveMs) return;

                StallCount++;
                if (waited > WorstStallMs) WorstStallMs = waited;

                var gcMs      = (int)(GC.GetTotalPauseDuration() - gcPause).TotalMilliseconds;
                var gen2Runs  = GC.CollectionCount(2) - gen2;
                ThreadPool.GetAvailableThreads(out var freeWorkers, out _);
                var queued    = ThreadPool.PendingWorkItemCount;

                // Name the cause in the message itself, so the log is readable without having
                // to reason about the numbers every time.
                var cause =
                    gcMs > waited / 2         ? "GC pause"
                  : freeWorkers == 0          ? "thread pool starved"
                  : queued > 50               ? "thread pool backlog"
                  :                             "UI thread ran code";

                errorLogger.Log(nameof(UiStallMonitor), $"UI stalled — {cause}",
                    $"Blocked {waited:N0} ms. GC paused {gcMs:N0} ms ({gen2Runs} gen2). " +
                    $"Pool: {freeWorkers} free workers (was {freeWorkersBefore}), " +
                    $"{queued:N0} queued. " +
                    $"Stall {StallCount} this session, worst {WorstStallMs:N0} ms.");
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
