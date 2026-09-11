using System.Text.Json.Serialization;

namespace EveConsole.Agent;

public enum MessageRole { User, Assistant }

public sealed record AgentMessage
{
    public MessageRole    Role      { get; init; }
    public string         Content   { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }

    [JsonIgnore]
    public bool IsSummary { get; init; }

    /// <summary>
    /// Whether this message belongs on screen. False for text the app injected on the
    /// capsuleer's behalf — an alarm handing the agent something to report, say. The model
    /// still receives it, because the reply makes no sense without it; the capsuleer sees only
    /// the reply.
    ///
    /// <para>Persisted, unlike <see cref="IsSummary"/>: a reload that forgot this would put the
    /// hidden text back on screen. Defaults to true, so history written before the flag existed
    /// loads as visible.</para>
    /// </summary>
    public bool ShowInChat { get; init; } = true;

    /// <summary>When it was said, in the capsuleer's own time, for the label above the bubble.</summary>
    [JsonIgnore]
    public string TimeText => Timestamp.ToLocalTime().ToString("d MMM yyyy HH:mm");

    /// <summary>
    /// The same moment as the model is told it, on EVE time — the clock every timestamp in the
    /// database is on, so "five minutes ago" and "five weeks ago" are a subtraction it can do.
    /// </summary>
    [JsonIgnore]
    public string EveTimeText => Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " EVE";

    [JsonConstructor]
    public AgentMessage(MessageRole role, string content, DateTimeOffset timestamp)
    {
        Role = role; Content = content; Timestamp = timestamp;
    }

    public AgentMessage(MessageRole role, string content)
        : this(role, content, DateTimeOffset.UtcNow) { }

    public static AgentMessage Summary(string content) =>
        new(MessageRole.Assistant, content) { IsSummary = true };
}
