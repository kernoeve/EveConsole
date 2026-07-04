using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveCortex.Agent.Tools.Data;

public sealed class GetAssetsTool : IAgentTool
{
    private readonly string _connString;

    public string Name        => "get_assets";
    public string Description => "Returns the capsuleer's assets stored in the local Eve Cortex database. " +
                                 "Can filter by character, item name, or location. " +
                                 "Groups identical items at the same location and returns estimated value where market prices are available.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            character_name = new { type = "string", description = "Filter by character or corporation name (partial match). Omit for all owners." },
            item_name      = new { type = "string", description = "Filter by item name (partial match)." },
            location_name  = new { type = "string", description = "Filter by station or structure name (partial match)." },
            limit          = new { type = "integer", description = "Max rows to return (default 50, max 200)." },
        },
    };

    public GetAssetsTool(string connString) => _connString = connString;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var charFilter  = input.TryGetProperty("character_name", out var cn) ? cn.GetString() : null;
        var itemFilter  = input.TryGetProperty("item_name",      out var it) ? it.GetString() : null;
        var locFilter   = input.TryGetProperty("location_name",  out var ln) ? ln.GetString() : null;
        var limit       = input.TryGetProperty("limit",          out var li) && li.TryGetInt32(out var lv) ? Math.Clamp(lv, 1, 200) : 50;

        const string sql = """
            SELECT
                st.Name                                                     AS item,
                SUM(a.Quantity)                                             AS quantity,
                COALESCE(sn.Name, ss.Name, CAST(a.RootLocationId AS TEXT)) AS location,
                COALESCE(c.Name, corp.Name, CAST(a.OwnerId AS TEXT))       AS owner,
                COALESCE(
                    ROUND(mip.Midpoint * SUM(a.Quantity), 2),
                    0
                )                                                           AS estimated_value
            FROM EsiAssets a
            JOIN SdeTypes  st   ON st.TypeId   = a.TypeId
            LEFT JOIN Characters    c    ON c.Id    = a.OwnerId  AND a.OwnerType = 'character'
            LEFT JOIN Corporations  corp ON corp.Id = a.OwnerId  AND a.OwnerType = 'corp'
            LEFT JOIN SdeStations        ss ON ss.StationId     = a.RootLocationId
            LEFT JOIN EsiStructureNames  sn ON sn.StructureId   = a.RootLocationId
            LEFT JOIN (
                SELECT mip2.TypeId, mip2.Midpoint
                FROM   MarketItemPrices     mip2
                JOIN   MarketPricingConfigs mpc  ON mpc.Id = mip2.ConfigId AND mpc.IsEnabled = 1
                ORDER  BY mpc.SortOrder
            ) mip ON mip.TypeId = a.TypeId
            WHERE  (@char IS NULL OR c.Name LIKE @char OR corp.Name LIKE @char)
              AND  (@item IS NULL OR st.Name LIKE @item)
              AND  (@loc  IS NULL OR sn.Name LIKE @loc OR ss.Name LIKE @loc)
            GROUP BY a.TypeId, a.RootLocationId, a.OwnerId, a.OwnerType
            ORDER BY estimated_value DESC, quantity DESC
            LIMIT @limit
            """;

        var rows = new List<object>();
        await using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@char",  charFilter is null ? (object)DBNull.Value : $"%{charFilter}%");
        cmd.Parameters.AddWithValue("@item",  itemFilter is null ? (object)DBNull.Value : $"%{itemFilter}%");
        cmd.Parameters.AddWithValue("@loc",   locFilter  is null ? (object)DBNull.Value : $"%{locFilter}%");
        cmd.Parameters.AddWithValue("@limit", limit);

        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            rows.Add(new
            {
                item            = rdr.GetString(0),
                quantity        = rdr.GetInt64(1),
                location        = rdr.IsDBNull(2) ? "Unknown" : rdr.GetString(2),
                owner           = rdr.IsDBNull(3) ? "Unknown" : rdr.GetString(3),
                estimated_value = rdr.GetDouble(4),
            });
        }

        return rows.Count == 0
            ? "No assets found matching the specified filters."
            : JsonSerializer.Serialize(rows);
    }
}
