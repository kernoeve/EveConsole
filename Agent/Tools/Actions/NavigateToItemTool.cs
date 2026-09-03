using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveConsole.Agent.Tools.Actions;

public sealed class NavigateToItemTool : IAgentTool
{
    private readonly string _connString;
    private readonly Action<int, string>? _callback;

    public string Name        => "navigate_to_item";
    public string Description => "Opens the Items tab and loads a specific EVE item in the Item Browser. " +
                                 "Use this when the capsuleer asks to 'pull up', 'show', or 'look at' a particular item. " +
                                 "Performs a partial name match — prefer the most specific name you know.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            item_name = new { type = "string", description = "Name of the item to navigate to (partial match accepted)." },
        },
        required = new[] { "item_name" },
    };

    public NavigateToItemTool(string connString, Action<int, string>? callback)
    {
        _connString = connString;
        _callback   = callback;
    }

    public async Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var query = input.TryGetProperty("item_name", out var n) ? n.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query)) return "No item name provided.";

        const string sql = """
            SELECT st."TypeId", st."Name"
            FROM   "SdeTypes" st
            WHERE  st."Name" LIKE @name AND st."Published" = 1
            ORDER  BY LENGTH(st."Name"), st."Name"
            LIMIT  5
            """;

        var matches = new List<(int TypeId, string Name)>();
        await using var conn = new SqliteConnection(_connString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@name", $"%{query}%");
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            matches.Add((rdr.GetInt32(0), rdr.GetString(1)));

        if (matches.Count == 0)
            return $"No published item matching '{query}' found in the SDE.";

        var (typeId, name) = matches[0];
        _callback?.Invoke(typeId, name);

        if (matches.Count == 1)
            return $"Opening Item Browser for '{name}'.";

        var others = string.Join(", ", matches.Skip(1).Select(m => m.Name));
        return $"Opening Item Browser for '{name}'. Other matches: {others}.";
    }
}
