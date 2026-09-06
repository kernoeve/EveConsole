namespace EveConsole.Services;

/// <summary>
/// How this process was started, for the parts of the app that have to care.
///
/// <para>Static because it is settled in <c>Program.Main</c> from the command line, before the
/// container exists and long before anything that reads it — there is nothing to inject it from,
/// and it cannot change afterwards.</para>
/// </summary>
public static class AppRuntime
{
    /// <summary>
    /// True when started with <c>--headless</c>: no window, no Avalonia, background work only.
    ///
    /// <para>⚠️ Not the same as "is the worker". A headless process still has to win the lease
    /// like anything else, and a desktop client that wins it is just as much the worker. This says
    /// only that there is nobody to show anything to.</para>
    /// </summary>
    public static bool IsHeadless { get; private set; }

    /// <summary>Set once, from the command line. Ignored afterwards.</summary>
    public static void MarkHeadless() => IsHeadless = true;
}
