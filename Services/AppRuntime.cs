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

    /// <summary>
    /// True when running under the Windows service control manager.
    ///
    /// <para>⚠️ Narrower than <see cref="IsHeadless"/>, which it implies. A service runs as
    /// LocalSystem: a different account, a different profile, and therefore a different
    /// %LOCALAPPDATA% — so it cannot read the settings the desktop app saved, and could not
    /// decrypt the password in them if it could. That is why this exists as its own fact rather
    /// than being folded into headless: it is what makes the app look somewhere else entirely for
    /// its database. See <see cref="MachineConfig"/>.</para>
    /// </summary>
    public static bool IsService { get; private set; }

    public static void MarkService()
    {
        IsService  = true;
        IsHeadless = true;   // a service has nowhere to draw either
    }
}
