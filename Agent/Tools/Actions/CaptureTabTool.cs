using System.Text.Json;
using EveCortex.Agent;

namespace EveCortex.Agent.Tools.Actions;

/// <summary>
/// Renders a named tab (or the currently active view) to a PNG image and returns it
/// to the model as a vision-capable tool result. Only works with providers that
/// support image content in tool results (Claude claude-sonnet-4-6 and above).
/// </summary>
public sealed class CaptureTabTool : IAgentTool
{
    // Callback set by MainWindow. Returns (imageBytes, description) for the named tab,
    // or null if the tab doesn't exist or can't be rendered.
    private readonly Func<string, Task<(byte[]? image, string description)>>? _callback;

    public string Name        => "capture_tab";
    public string Description =>
        "Capture a screenshot of a tab or panel in the Eve Cortex application so you can " +
        "see exactly what the user is looking at. " +
        "tab_name can be: 'current' (whatever is active), 'assets', 'industry', " +
        "'characters', 'items', 'data', or 'overview'. " +
        "Use this when the user asks about something you can see on screen, or when " +
        "you need to understand the current UI state to answer a question.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            tab_name = new
            {
                type = "string",
                description = "Name of the tab to capture: 'current', 'assets', 'industry', 'characters', 'items', 'data', 'overview'.",
            },
        },
        required = new[] { "tab_name" },
    };

    public CaptureTabTool(Func<string, Task<(byte[]? image, string description)>>? callback)
        => _callback = callback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
        => Task.FromResult("Use ExecuteWithResultAsync for image results.");

    public async Task<AgentToolResult> ExecuteWithResultAsync(JsonElement input, CancellationToken ct = default)
    {
        if (_callback is null)
            return "Screenshot capture is not available.";

        var tabName = input.TryGetProperty("tab_name", out var t) ? (t.GetString() ?? "current") : "current";

        var (imageBytes, description) = await _callback(tabName.ToLowerInvariant());

        if (imageBytes is null || imageBytes.Length == 0)
            return $"Could not capture tab '{tabName}'. It may not be open or visible.";

        return new AgentToolResult
        {
            Text        = description,
            ImageBase64 = Convert.ToBase64String(imageBytes),
            ImageMediaType = "image/png",
        };
    }
}
