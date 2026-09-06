namespace EveConsole.Models;

/// <summary>
/// One background loop's state, as the client running it reports it.
///
/// <para>The monitoring window used to read these straight off the services in this process, which
/// was right while only one client could exist. It is a lie the moment a second one opens: on a
/// client that is not the worker every loop looks stopped, so the window says "Idle" about a sweep
/// that is running perfectly well somewhere else — the most misleading thing a monitor can do,
/// because idle is also what a genuinely broken loop looks like.</para>
///
/// <para>⚠️ A table rather than a signal, and for once the reason is replay. Signals are for "act
/// now" and are lost on anyone not listening at the time; this has to answer "what is happening?"
/// to a window opened at any moment, including one opened an hour after the last thing changed.
/// The Alert action makes the same choice for the same reason.</para>
///
/// <para>Written only when a value changes, so a quiet loop costs nothing at all rather than a row
/// per tick.</para>
/// </summary>
public class WorkerActivity
{
    /// <summary>Stable identifier for the loop, e.g. <c>market.history</c>. Chosen by the
    /// publisher and matched by the reader, so the two must agree — see WorkerActivityService.</summary>
    public string Key { get; set; } = "";

    /// <summary>Whatever the loop says about itself, shown verbatim.</summary>
    public string Status { get; set; } = "";

    /// <summary>Whether it is mid-pass rather than waiting for its next one.</summary>
    public bool Running { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }

    /// <summary>
    /// ⚠️ When this row was last written, not when the loop last ran. Its age is how a reader
    /// tells a loop that is genuinely idle from a worker that died holding the pen.
    /// </summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}
