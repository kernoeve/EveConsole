using System.Reflection;
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
        UiWork.Clear();
        try
        {
            return action(scheduler, state);
        }
        finally
        {
            var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (ms >= ReportAboveMs)
                log.Log(nameof(TimedMainThreadScheduler), "Slow UI-thread work",
                    $"{ms:N0} ms in {UiWork.Current ?? "(unlabelled)"} — {Describe(action, state)}");
            UiWork.Clear();
        }
    }


    /// <summary>
    /// Names the culprit by walking into Rx's own plumbing.
    ///
    /// <para>The scheduled delegate and the state object are both Rx internals — every periodic
    /// refresh in the app arrives as the same anonymous <c>ObserveOnObserverNew&lt;Int64&gt;</c>,
    /// which is why the first version of this could only report that something was slow. But the
    /// state object IS the observer, and its chain of private <c>_observer</c> fields ends at the
    /// subscriber's own delegate, whose target is the closure class of the method that
    /// subscribed. Reflecting down that chain recovers the view model by name.</para>
    ///
    /// <para>Reflection is fine here: this runs only when something has already blocked the UI
    /// thread for hundreds of milliseconds, and the walk is bounded.</para>
    /// </summary>
    private static string Describe<TState>(Delegate action, TState state)
    {
        var owner = FindAppOwner(state) ?? FindAppOwner(action.Target);
        var carried = state?.GetType().Name;
        return owner is not null
            ? $"{owner} (via {carried ?? "?"})"
            : $"unidentified — state={state?.GetType().FullName ?? "null"}";
    }

    /// <summary>Everything of ours lives under this root; anything else is framework plumbing
    /// and worth stepping through rather than reporting.</summary>
    private const string AppRoot = "EveConsole";

    /// <summary>
    /// Breadth-first hunt for the first delegate belonging to this app, following instance
    /// fields. Bounded on both node count and depth so a cyclic observer graph cannot hang the
    /// very thread it is diagnosing.
    /// </summary>
    private static string? FindAppOwner(object? root)
    {
        if (root is null) return null;

        const int MaxNodes = 400;
        var seen    = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue   = new Queue<(object Node, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (node, depth) = queue.Dequeue();
            if (depth > 8 || !seen.Add(node)) continue;
            visited++;

            if (node is Delegate d && Name(d) is { } named) return named;

            var type = node.GetType();
            if (type.IsPrimitive || node is string) continue;

            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public
                                           | BindingFlags.NonPublic))
            {
                if (f.FieldType.IsPrimitive || f.FieldType == typeof(string)) continue;

                object? value;
                try { value = f.GetValue(node); } catch { continue; }
                if (value is not null) queue.Enqueue((value, depth + 1));
            }
        }

        return null;

        // A compiler-generated closure names its method in the type ("<>c__DisplayClass12_0"),
        // so the declaring type plus the delegate's method is enough to find the call site.
        static string? Name(Delegate d)
        {
            var t = d.Target?.GetType().FullName ?? d.Method.DeclaringType?.FullName;
            if (t is null || !t.StartsWith(AppRoot, StringComparison.Ordinal)) return null;
            return $"{t}.{d.Method.Name}";
        }
    }
}
