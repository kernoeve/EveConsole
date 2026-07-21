using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

public sealed class OpenWindowTool : IAgentTool
{
    private readonly Action<string> _callback;

    public string Name        => "open_window";
    public string Description => "Opens a specific window in the EVE Console application. " +
                                 "Use this when the capsuleer asks to see a window, or when showing live data would be helpful.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            window = new
            {
                type = "string",
                description =
                    "Tool to open. One of: overview, characters, assets, items, industry, " +
                    "indy_parks, prod_calc, market_levels, inv_levels, trade, net_worth, " +
                    "wallet, corp_activity, killmails, eve_mail, data.",
                @enum = new[]
                {
                    "overview", "characters", "assets", "items", "industry",
                    "indy_parks", "prod_calc", "market_levels", "inv_levels", "trade",
                    "net_worth", "wallet", "corp_activity", "killmails", "eve_mail", "data",
                },
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
