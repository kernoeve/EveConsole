using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveCortex.Agent.Tools.Data;

public sealed class SearchItemsTool : IAgentTool
{
    private readonly string _connString;

    public string Name        => "search_items";
    public string Description => "Searches the EVE static data for items by name. " +
                                 "Returns type ID, name, group, and category. Useful for looking up what an item is before querying prices or assets.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Partial or full item name to search for." },
            limit = new { type = "integer", description = "Max results to return (default 20, max 50)." },
        },
        required = new[] { "query" },
    };

    public SearchItemsTool(string connString) => _connString = connString;

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var query = input.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var limit = input.TryGetProperty("limit",  out var l) && l.TryGetInt32(out var li) ? Math.Clamp(li, 1, 50) : 20;

        if (string.IsNullOrWhiteSpace(query)) return "No search query provided.";

        const string sql = """
            SELECT st.TypeId, st.Name, sg.Name as GroupName, sc.Name as CategoryName
            FROM SdeTypes     st
            JOIN SdeGroups    sg ON sg.GroupId    = st.GroupId
            JOIN SdeCategories sc ON sc.CategoryId = sg.CategoryId
            WHERE st.Name LIKE @q AND st.Published = 1
            ORDER BY LENGTH(st.Name), st.Name
            LIMIT @limit
            """;

        var rows = new List<object>();
        await using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@q",     $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            rows.Add(new { type_id = rdr.GetInt32(0), name = rdr.GetString(1), group = rdr.GetString(2), category = rdr.GetString(3) });

        return rows.Count == 0
            ? $"No published items found matching '{query}'."
            : JsonSerializer.Serialize(rows);
    }
}
