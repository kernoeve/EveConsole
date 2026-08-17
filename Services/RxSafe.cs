namespace EveConsole.Services;

/// <summary>
/// Subscribing with an async handler, without letting a failure take the process down.
///
/// <c>Subscribe(async _ => await SaveAsync())</c> reads harmlessly and is not: the lambda is
/// effectively async void, so nothing awaits it and nothing can catch it. A throw becomes an
/// unhandled exception on a thread-pool thread, and the client dies.
///
/// That is a poor trade for the kind of failure these handlers actually see. A throttled save
/// racing another writer gets SQLITE_BUSY — transient, already retried for 30 seconds by
/// busy_timeout, and fixed by trying again. Losing an edit is a nuisance; losing the application
/// mid-session is not the same order of problem.
/// </summary>
public static class RxSafe
{
    /// <summary>
    /// Runs an async handler per notification, logging anything it throws instead of letting it
    /// escape. <paramref name="context"/> names the handler in the error log, since a stack trace
    /// through Rx says little about which subscription failed.
    /// </summary>
    public static IDisposable SubscribeAsyncSafe<T>(
        this IObservable<T> source,
        Func<T, Task> handler,
        AppErrorLogger? errorLogger,
        string context)
        => source.Subscribe(async value =>
        {
            try
            {
                await handler(value);
            }
            catch (OperationCanceledException)
            {
                // Routine on teardown or a superseded refresh.
            }
            catch (Exception ex)
            {
                errorLogger?.Log("Rx", context, ex);
            }
        });
}
