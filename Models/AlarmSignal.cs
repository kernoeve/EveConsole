namespace EveConsole.Models;

/// <summary>
/// An alarm firing, as the worker hands it to the clients.
///
/// <para>⚠️ Everything here is already resolved. The worker composes each string — expanding the
/// user's placeholders, building the agent's prompt out of the matches — and the clients only
/// perform. That is not tidiness: the matches live in memory on the side that evaluated the
/// condition, and making every client reconstruct them would mean a database read on the one path
/// where the whole design is about not spending milliseconds.</para>
///
/// <para>A null field means the alarm has no action of that kind. There is no separate list of
/// which actions to run, because "has a sound" and "SoundKey is set" would then be two facts that
/// could disagree.</para>
///
/// <para>⚠️ No Alert field. The Alert action writes a row, the worker does it directly, and every
/// client reads it from the database like any other row — it is the one action that survives a
/// client being disconnected, muted, or closed, which is exactly why it is not carried here.</para>
/// </summary>
public sealed class AlarmSignal
{
    /// <summary>
    /// Discriminator, present from the first version so a second kind of signal can be added
    /// without the existing clients having to guess what they are looking at.
    /// </summary>
    public const string SignalKind = "alarm";

    public string Kind    { get; set; } = SignalKind;

    public long   AlarmId { get; set; }
    public string Name    { get; set; } = "";

    public string? SoundKey    { get; set; }
    public int     SoundVolume { get; set; } = 100;

    public string? DialogTitle { get; set; }
    public string? DialogBody  { get; set; }

    /// <summary>The finished prompt for the agent, matches and standing instruction included.</summary>
    public string? AgentText   { get; set; }

    /// <summary>
    /// Text the agent is to say exactly as written, with no model in the way — composed by the
    /// condition, which knows the order its facts matter in. Set instead of
    /// <see cref="AgentText"/> for an alarm whose value is in the next few seconds.
    /// </summary>
    public string? SpeakText   { get; set; }

    /// <summary>True when this alarm's only instruction was to tell the agent. Lets a client that
    /// cannot speak decide whether silence loses the warning entirely.</summary>
    public bool    AgentOnly   { get; set; }

    /// <summary>Fallback wording if nothing can speak it — resolved by the worker, like the rest.</summary>
    public string? FallbackTitle { get; set; }
    public string? FallbackBody  { get; set; }
}
