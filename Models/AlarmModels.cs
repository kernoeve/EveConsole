namespace EveConsole.Models;

/// <summary>What happens after an alarm has fired once.</summary>
public enum AlarmRepeat
{
    /// <summary>Fire once, then disable the alarm.</summary>
    OneShot = 0,
    /// <summary>Stay armed until the user disables it.</summary>
    Continuous = 1,
}

public enum AlarmActionKind
{
    Sound       = 0,
    AgentNotify = 1,
    Alert       = 2,
    Dialog      = 3,
}

/// <summary>
/// A user- or agent-defined alarm. The condition is stored as a registry key plus a JSON
/// parameter blob so new condition types can be added without a schema change.
/// </summary>
public class Alarm
{
    public long   Id      { get; set; }
    public string Name    { get; set; } = "";
    public bool   Enabled { get; set; } = true;

    public string ConditionType { get; set; } = "";     // IAlarmCondition.TypeKey
    public string ConditionJson { get; set; } = "{}";

    public AlarmRepeat Repeat { get; set; } = AlarmRepeat.Continuous;

    /// <summary>How often to evaluate. The service applies its own floor.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>Minimum gap between firings, on top of the new-match rule. 0 = no extra damping.</summary>
    public int CooldownSeconds { get; set; }

    /// <summary>
    /// False until the first evaluation has banked the matches that already existed when the
    /// alarm was created. Without this, a killmail alarm would fire on every kill in history
    /// the moment it is switched on.
    /// </summary>
    public bool Primed { get; set; }

    public string CreatedBy { get; set; } = "user";     // "user" | "agent"

    public DateTimeOffset  CreatedAt     { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastFiredAt   { get; set; }
    public int             FireCount     { get; set; }
    public string?         LastError     { get; set; }
}

public class AlarmAction
{
    public long            Id         { get; set; }
    public long            AlarmId    { get; set; }
    public AlarmActionKind Kind       { get; set; }
    public string          ConfigJson { get; set; } = "{}";
    public int             Ordinal    { get; set; }
}

/// <summary>
/// The identity ledger that keeps an alarm from re-announcing something it has already
/// announced. A check reports what it matched, not merely that it matched; a key that has
/// been seen before is not news.
/// </summary>
public class AlarmSeenKey
{
    public long           AlarmId     { get; set; }
    public string         MatchKey    { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; }
}

/// <summary>One firing, with the matches that caused it. Shown as history in the Alarms tool.</summary>
public class AlarmEvent
{
    public long           Id         { get; set; }
    public long           AlarmId    { get; set; }
    public DateTimeOffset FiredAt    { get; set; }
    public string         Summary    { get; set; } = "";
    public string?        DetailJson { get; set; }
    public int            MatchCount { get; set; }
}

/// <summary>A dismissible alert raised by the Alert action, persisted until the user clears it.</summary>
public class AlarmAlert
{
    public long            Id           { get; set; }
    public long            AlarmId      { get; set; }
    public long            AlarmEventId { get; set; }
    public DateTimeOffset  CreatedAt    { get; set; }
    public string          Title        { get; set; } = "";
    public string?         Body         { get; set; }
    public bool            Dismissed    { get; set; }
    public DateTimeOffset? DismissedAt  { get; set; }
}
