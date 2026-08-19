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

    public static void Mark(string label) => _label = label;
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
