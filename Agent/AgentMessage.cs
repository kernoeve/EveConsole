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
