using System.Text.Json;

namespace EveCortex.Agent.Tools.Actions;

public sealed class OpenWindowTool : IAgentTool
{
    private readonly Action<string> _callback;

    public string Name        => "open_window";
    public string Description => "Opens a specific window in the Eve Cortex application. " +
                                 "Use this when the capsuleer asks to see a window, or when showing live data would be helpful.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new
            {
                type = "string",
                description = "Window to open. One of: assets, industry, characters, items, data.",
                @enum = new[] { "assets", "industry", "characters", "items", "data" },
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
