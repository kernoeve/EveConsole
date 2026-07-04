using System.Text.Json;

namespace EveCortex.Agent.Tools.Actions;

public sealed class SelectCharacterTool : IAgentTool
{
    private readonly Action<string>? _callback;

    public string Name        => "select_character";
    public string Description => "Opens the Characters tab and selects a specific character in the Character Viewer. " +
                                 "Use this to switch the active character being viewed. " +
                                 "The character must already be configured in Eve Cortex.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            character_name = new { type = "string", description = "Name of the character to select (partial match accepted)." },
        },
        required = new[] { "character_name" },
    };

    public SelectCharacterTool(Action<string>? callback) => _callback = callback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var name = input.TryGetProperty("character_name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult("No character name provided.");

        _callback?.Invoke(name);
        return Task.FromResult($"Switching Characters tab to '{name}'.");
    }
}
