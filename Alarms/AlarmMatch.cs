namespace EveConsole.Alarms;

/// <summary>
/// One thing a check matched — not merely that it matched.
///
/// <para><see cref="Key"/> is the whole anti-spam mechanism. It must identify the *occurrence*
/// stably across evaluations: a hostile sitting in a system for an hour is one key and so is
/// announced once, while a second hostile arriving is a new key and so is news. Choose the
/// narrowest durable identifier the source offers — <c>intel:412</c>, <c>km:118…</c>,
/// <c>job:5501…</c> — never something that varies run to run, or the alarm will fire forever.</para>
/// </summary>
public sealed record AlarmMatch(string Key, string Summary)
{
    /// <summary>Structured payload stored on the event and offered to the agent.</summary>
    public IReadOnlyDictionary<string, object?>? Detail { get; init; }
}
