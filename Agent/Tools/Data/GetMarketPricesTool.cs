using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveConsole.Agent.Tools.Data;

public sealed class GetMarketPricesTool : IAgentTool
{
    private readonly string _connString;

    public string Name        => "get_market_prices";
    public string Description => "Returns cached market prices for one or more EVE items by name. " +
                                 "Prices come from whichever price sources the capsuleer has configured (Jita, trade hubs, etc.). " +
                                 "Returns buy, sell, and midpoint prices where available.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            item_names = new
            {
                type  = "array",
                items = new { type = "string" },
                description = "List of item names to look up prices for.",
            },
        },
        required = new[] { "item_names" },
    };

    public GetMarketPricesTool(string connString) => _connString = connString;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        if (!input.TryGetProperty("item_names", out var namesEl) || namesEl.ValueKind != JsonValueKind.Array)
            return "No item_names provided.";

        var names = namesEl.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(20)
            .ToList();

        if (names.Count == 0) return "No item names provided.";

        const string sql = """
            SELECT st.Name,
                   pc.LocationName AS source,
                   mip.BuyPrice,
                   mip.SellPrice,
                   mip.Midpoint
            FROM   SdeTypes          st
            JOIN   MarketItemPrices  mip ON mip.TypeId   = st.TypeId
            JOIN   MarketPricingConfigs pc ON pc.Id = mip.ConfigId AND pc.IsEnabled = 1
            WHERE  st.Name LIKE @name
            ORDER  BY pc.SortOrder
            LIMIT  5
            """;

        var results = new List<object>();
        await using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync(ct);

        foreach (var name in names)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@name", name);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            if (await rdr.ReadAsync(ct))
            {
                results.Add(new
                {
                    item     = rdr.GetString(0),
                    source   = rdr.GetString(1),
                    buy      = rdr.IsDBNull(2) ? 0 : rdr.GetDouble(2),
                    sell     = rdr.IsDBNull(3) ? 0 : rdr.GetDouble(3),
                    midpoint = rdr.IsDBNull(4) ? 0 : rdr.GetDouble(4),
                });
            }
            else
            {
                results.Add(new { item = name, note = "No price data cached for this item." });
            }
        }

        return JsonSerializer.Serialize(results);
    }
}
