namespace EveCortex.Agent;

/// <summary>Tool result that optionally carries an image payload alongside text.</summary>
public sealed record AgentToolResult
{
    public string  Text           { get; init; } = "";
    public string? ImageBase64    { get; init; }
    public string  ImageMediaType { get; init; } = "image/png";

    public static implicit operator AgentToolResult(string text) => new() { Text = text };
}
