using System.Text.Json;

namespace EveConsole.Agent.Tools;

public interface IAgentTool
{
    string Name        { get; }
    string Description { get; }
    object InputSchema { get; }   // anonymous object → serialized as JSON Schema by Claude provider

    Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default);

    // Default: wrap the string result. Override to return image data.
    virtual async Task<AgentToolResult> ExecuteWithResultAsync(JsonElement input, CancellationToken ct = default)
    {
        var text = await ExecuteAsync(input, ct);
        return text;
    }
}
