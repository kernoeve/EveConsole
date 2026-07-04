using System.Text.Json;

namespace EveCortex.Agent.Tools.Actions;

/// <summary>
/// Adjusts the Item Browser view for whatever item is currently loaded there:
/// which detail tab is shown, and the market-orders source / price-history region.
/// Call navigate_to_item first to load the item.
/// </summary>
public sealed class ConfigureItemBrowserTool : IAgentTool
{
    private readonly Func<string?, string?, string?, string> _callback;

    public string Name => "configure_item_browser";
    public string Description =>
        "Adjust the Item Browser for the item currently loaded there: switch its detail tab, " +
        "and/or choose the market-orders source or price-history region. " +
        "Call navigate_to_item first to load the item. " +
        "Example: to show Jita market orders for an item, call navigate_to_item, then this with " +
        "tab='market_orders' and market_source='Jita'.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            tab = new
            {
                type = "string",
                description = "Detail tab to show.",
                @enum = new[]
                {
                    "description", "attributes", "requirements", "required_for",
                    "industry", "market_orders", "price_history",
                },
            },
            market_source = new
            {
                type = "string",
                description = "Partial name of the market-orders source to select on the Market Orders tab (e.g. 'Jita').",
            },
            history_region = new
            {
                type = "string",
                description = "Partial name of the region to select on the Price History tab (e.g. 'The Forge').",
            },
        },
    };

    public ConfigureItemBrowserTool(Func<string?, string?, string?, string> callback) => _callback = callback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        string? tab = input.TryGetProperty("tab", out var t)            ? t.GetString() : null;
        string? src = input.TryGetProperty("market_source", out var s)  ? s.GetString() : null;
        string? reg = input.TryGetProperty("history_region", out var r) ? r.GetString() : null;

        if (string.IsNullOrWhiteSpace(tab) && string.IsNullOrWhiteSpace(src) && string.IsNullOrWhiteSpace(reg))
            return Task.FromResult("Specify at least one of: tab, market_source, history_region.");

        return Task.FromResult(_callback(tab, src, reg));
    }
}
