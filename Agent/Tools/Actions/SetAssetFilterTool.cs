using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

public sealed class SetAssetFilterTool : IAgentTool
{
    private readonly Action<string?, string?, string?>? _callback;

    public string Name        => "set_asset_filter";
    public string Description => "Opens the Assets tab and filters the asset grid. " +
                                 "Provide any combination of location, owner, and item name filters. " +
                                 "All filters use partial (contains) matching. " +
                                 "Omit a parameter to leave that filter unset. " +
                                 "Call with no parameters to clear all filters.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            location  = new { type = "string", description = "Filter by location name (station, structure, solar system, or region — partial match)." },
            character = new { type = "string", description = "Filter by character or corporation name (partial match)." },
            item_name = new { type = "string", description = "Filter by item/type name (partial match)." },
        },
    };

    public SetAssetFilterTool(Action<string?, string?, string?>? callback) => _callback = callback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var location  = input.TryGetProperty("location",  out var l) ? l.GetString() : null;
        var character = input.TryGetProperty("character", out var c) ? c.GetString() : null;
        var item      = input.TryGetProperty("item_name", out var i) ? i.GetString() : null;

        _callback?.Invoke(location, character, item);

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(location))  parts.Add($"location contains '{location}'");
        if (!string.IsNullOrEmpty(character)) parts.Add($"owner contains '{character}'");
        if (!string.IsNullOrEmpty(item))      parts.Add($"item contains '{item}'");

        return Task.FromResult(parts.Count == 0
            ? "Asset filter cleared."
            : $"Asset browser filtered by: {string.Join(", ", parts)}.");
    }
}
