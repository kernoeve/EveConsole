using System.Diagnostics;
using System.Reactive.Concurrency;

namespace EveConsole.Services;

/// <summary>
/// Wraps ReactiveUI's main-thread scheduler and reports any single piece of work that occupies
/// the UI thread for too long, naming the type responsible.
///
/// <para>Companion to <see cref="UiStallMonitor"/>, which can say the UI thread was blocked but
/// not by what. Nearly all of this app's UI updates arrive through
/// <c>ObserveOn(RxApp.MainThreadScheduler)</c> or a ReactiveCommand, so decorating that one
/// scheduler covers most of the surface without touching a single call site.</para>
///
/// <para>⚠️ Only work scheduled AFTER this is installed is measured — ObserveOn captures the
/// scheduler when the subscription is created, so anything subscribed earlier keeps the
/// original. Silence therefore narrows the search rather than clearing ReactiveUI entirely.</para>
/// </summary>
public sealed class TimedMainThreadScheduler(IScheduler inner, AppErrorLogger log) : IScheduler
{
    /// <summary>Below this it cannot be what a person notices as a freeze.</summary>
    private const int ReportAboveMs = 400;

    public DateTimeOffset Now => inner.Now;

    public IDisposable Schedule<TState>(
        TState state, Func<IScheduler, TState, IDisposable> action)
        => inner.Schedule(state, (s, st) => Timed(action, s, st));

    public IDisposable Schedule<TState>(
        TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action)
        => inner.Schedule(state, dueTime, (s, st) => Timed(action, s, st));

    public IDisposable Schedule<TState>(
        TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action)
        => inner.Schedule(state, dueTime, (s, st) => Timed(action, s, st));

    private IDisposable Timed<TState>(
        Func<IScheduler, TState, IDisposable> action, IScheduler scheduler, TState state)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return action(scheduler, state);
        }
        finally
        {
            var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= ReportAboveMs)
                log.Log(nameof(TimedMainThreadScheduler), "Slow UI-thread work",
                    $"{ms:N0} ms in {Describe(action, state)}");
        }
    }

    /// <summary>
    /// Best effort at naming the culprit. The scheduled delegate is usually Rx plumbing, so the
    /// state object — which carries the subscriber's own closure — is normally the more telling
    /// of the two. Both are reported rather than picking one and being wrong.
    /// </summary>
    private static string Describe<TState>(Delegate action, TState state)
    {
        var target = action.Target?.GetType().FullName ?? action.Method.DeclaringType?.FullName;
        var carried = state?.GetType().FullName;
        return $"state={carried ?? "null"}, target={target ?? "unknown"}";
    }
}
