using System.Text.Json;

namespace EveConsole.Agent.Tools.Data;

/// <summary>
/// Columns for named tables, so the agent can look the schema up instead of guessing it.
///
/// <para>The table index in the system prompt says what EXISTS; this says what is in one. Split
/// that way because the full column list is roughly 8k tokens — worth fetching for the three
/// tables a question needs, not worth carrying in every prompt for the two hundred it does not.</para>
///
/// <para>⚠️ A wrong name answers with the closest real ones rather than an empty result. That is
/// the whole point: on SQLite an unknown double-quoted identifier is read as a string literal
/// instead of failing, so a guess produces confident nonsense. Turning a miss into a correction is
/// what stops the agent building on it.</para>
/// </summary>
public sealed class DescribeTablesTool(AgentSchema schema) : IAgentTool
{
    public string Name => "describe_tables";

    public string Description =>
        """
        Get the exact columns of one or more database tables: name, type, whether it is part of the
        primary key, and whether it can be null.

        Call this BEFORE writing SQL against any table you have not already described in this
        conversation. The table index in your instructions lists every table that exists but none
        of their columns.

        You can also pass a partial or misremembered name — the answer names the closest real
        tables, which is the quickest way to find where something lives.
        """;

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            tables = new
            {
                type        = "array",
                items       = new { type = "string" },
                description = "Table names, exactly as listed in the table index. Several at once is fine.",
            },
        },
        required = new[] { "tables" },
    };

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        if (!input.TryGetProperty("tables", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Task.FromResult("Pass 'tables' as an array of table names.");

        var names = arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToList();

        if (names.Count == 0)
            return Task.FromResult("No table names given.");

        // ⚠️ Capped. Asking for everything would defeat the point of not carrying the whole
        // schema in the prompt, and an agent that does it once will do it every turn.
        const int Max = 12;
        if (names.Count > Max)
            return Task.FromResult(
                $"Too many tables at once ({names.Count}). Ask for at most {Max} — the ones you " +
                "actually intend to query — rather than describing the schema wholesale.");

        return Task.FromResult(schema.Describe(names));
    }
}
