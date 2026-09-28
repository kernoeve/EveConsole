using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

public sealed class OpenWindowTool : IAgentTool
{
    private readonly Action<string> _callback;

    public string Name        => "open_window";
    public string Description => "Opens a specific window in the EVE Console application. " +
                                 "Use this when the capsuleer asks to see a window, or when showing live data would be helpful.";

    // ⚠️ Every tool, from the one catalogue. This listed 17 of the 41, so the agent could not open
    // the Jump Planner, the Worklist or the Structure Browser — and "background" was listed here
    // but not handled by the window, so asking for it silently did nothing.
    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new
            {
                type = "string",
                description = "Tool to open, by id: " +
                              string.Join(", ", AppKnowledge.Tools.Select(t => $"{t.Id} ({t.Name})")) + ".",
                @enum = AppKnowledge.Tools.Select(t => t.Id).ToArray(),
            },
        },
        required = new[] { "window" },
    };

    public OpenWindowTool(Action<string> openWindowCallback) => _callback = openWindowCallback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var window = input.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(window))
            return Task.FromResult("No window name provided.");

        _callback(window);
        return Task.FromResult($"Opened the {window} window.");
    }
}
