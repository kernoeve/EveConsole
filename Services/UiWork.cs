using System.Reactive.Linq;
using ReactiveUI;

namespace EveConsole.Services;

/// <summary>
/// Names whatever is currently running on the UI thread, so a stall report can say which
/// subscription caused it.
///
/// <para><see cref="TimedMainThreadScheduler"/> can time a slow piece of UI work but not
/// identify it: everything it sees is Rx plumbing, and every periodic refresh in the app
/// arrives as the same anonymous <c>ObserveOnObserverNew&lt;Int64&gt;</c>. The label is set by
/// the subscription itself, one line at the point where the tick crosses onto the UI
/// thread, and read back by the scheduler when the work finishes.</para>
/// </summary>
public static class UiWork
{
    [ThreadStatic] private static string? _label;

    /// <summary>What the UI thread is currently doing, if it said.</summary>
    public static string? Current => _label;

    /// <summary>
    /// The last label seen, and when — deliberately NOT cleared with <see cref="Clear"/>.
    ///
    /// <para><see cref="UiStallMonitor"/> reports after the stall is over, by which time
    /// <see cref="Current"/> has been cleared by whatever finished; reading it there would say
    /// "nothing named" every single time, including when a named subscription was the whole
    /// cause. What that report can use is the last thing that named itself and how long ago,
    /// which separates "an Rx tick was running when this began" from "the UI thread has not run
    /// any named work in minutes, so the blockage is somewhere Rx never sees".</para>
    ///
    /// <para>Not thread-static: the monitor's callback and the work it is describing both run on
    /// the UI thread, but keeping one shared value means a stall on any thread can still name
    /// what the UI thread last did.</para>
    /// </summary>
    private static volatile string? _last;
    private static long _lastAt;

    public static string? Last => _last;

    /// <summary>Milliseconds since the last named work began, or null if nothing ever named itself.</summary>
    public static int? MsSinceLast =>
        _last is null ? null : (int)System.Diagnostics.Stopwatch.GetElapsedTime(_lastAt).TotalMilliseconds;

    public static void Mark(string label)
    {
        _label  = label;
        _last   = label;
        _lastAt = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public static void Clear() => _label = null;
}

public static class UiWorkObservableExtensions
{
    /// <summary>
    /// <c>ObserveOn(RxApp.MainThreadScheduler)</c> that names itself. Use this rather than the
    /// bare call for anything periodic — an unnamed timer that blocks the UI thread cannot be
    /// told from the other five in the error log.
    /// </summary>
    public static IObservable<T> ObserveOnUi<T>(this IObservable<T> source, string label)
        => source.ObserveOn(RxApp.MainThreadScheduler).Do(_ => UiWork.Mark(label));
}
