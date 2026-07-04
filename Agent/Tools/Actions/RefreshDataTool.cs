using System.Text.Json;

namespace EveCortex.Agent.Tools.Actions;

public sealed class RefreshDataTool : IAgentTool
{
    private readonly Action _callback;

    public string Name        => "refresh_data";
    public string Description => "Triggers a full ESI data refresh for the configured characters and corporations. " +
                                 "Use this when the capsuleer wants to see the latest data from the EVE server.";

    public object InputSchema => new
    {
        type       = "object",
        properties = new { },
    };

    public RefreshDataTool(Action refreshCallback) => _callback = refreshCallback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        _callback();
        return Task.FromResult("Data refresh triggered. Updated data will be available shortly.");
    }
}
