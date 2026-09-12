using System.Text.Json.Serialization;

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

    /// <summary>What fired, in a line — the event's summary, for anything that shows it.</summary>
    public string Summary { get; set; } = "";

    public string? SoundKey    { get; set; }
    public int     SoundVolume { get; set; } = 100;

    public string? DialogTitle { get; set; }
    public string? DialogBody  { get; set; }

    /// <summary>The dialog's button, when it is not merely "Dismiss" — "I'm awake".</summary>
    public string? DialogButton { get; set; }

    // ── A staged firing ──
    //
    // Set together, for a firing that can be acknowledged: which alarm's situation it is
    // (ScopeKey — a character), which occurrence of it (Episode), and how long an
    // acknowledgement keeps it quiet. Absent on an ordinary firing.
    public string? ScopeKey      { get; set; }
    public string? Episode       { get; set; }
    public int     SnoozeMinutes { get; set; }
    /// <summary>The condition's type key, so a client can ask it whether the situation still holds.</summary>
    public string? ConditionType { get; set; }

    /// <summary>Repeat the sound until the situation is acknowledged or ends of itself.</summary>
    public bool    SoundLoop     { get; set; }

    /// <summary>Any reply to the agent's message acknowledges the situation.</summary>
    public bool    ReplyAcknowledges { get; set; }

    /// <summary>The finished prompt for the agent, matches and standing instruction included.</summary>
    public string? AgentText   { get; set; }

    /// <summary>
    /// Text the agent is to say exactly as written, with no model in the way — composed by the
    /// condition, which knows the order its facts matter in. Set instead of
    /// <see cref="AgentText"/> for an alarm whose value is in the next few seconds.
    /// </summary>
    public string? SpeakText   { get; set; }

    /// <summary>
    /// The TTS Direct action's text: spoken and shown as the application's own line, whether
    /// or not the agent is on. Distinct from <see cref="SpeakText"/>, which is an Agent Notify
    /// that happened to need no model and so still asks whether the agent can speak.
    /// </summary>
    public string? DirectText  { get; set; }

    /// <summary>True when this alarm's only instruction was to tell the agent. Lets a client that
    /// cannot speak decide whether silence loses the warning entirely.</summary>
    public bool    AgentOnly   { get; set; }

    /// <summary>Fallback wording if nothing can speak it — resolved by the worker, like the rest.</summary>
    public string? FallbackTitle { get; set; }
    public string? FallbackBody  { get; set; }

    /// <summary>The acknowledgeable situation this signal is about, or null.</summary>
    [JsonIgnore]
    public AlarmAck? Ack =>
        ScopeKey is { Length: > 0 } scope
            ? new AlarmAck(AlarmId, scope, Episode ?? "", SnoozeMinutes)
            : null;
}

/// <summary>
/// One stage of a staged firing, as the service hands it to the runner: which stage, whose
/// situation, which occurrence, and how long an acknowledgement quiets it.
/// </summary>
public sealed record AlarmStageInfo(int Stage, string ScopeKey, string Episode, int SnoozeMinutes, string ConditionType);

/// <summary>What an acknowledgement names: this alarm's situation for this scope and episode.</summary>
public sealed record AlarmAck(long AlarmId, string ScopeKey, string Episode, int SnoozeMinutes);
