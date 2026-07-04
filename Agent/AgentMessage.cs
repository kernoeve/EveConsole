using System.Text.Json.Serialization;

namespace EveCortex.Agent;

public enum MessageRole { User, Assistant }

public sealed record AgentMessage
{
    public MessageRole    Role      { get; init; }
    public string         Content   { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }

    [JsonIgnore]
    public bool IsSummary { get; init; }

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
