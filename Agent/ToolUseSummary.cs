using System.Text.Json;

namespace EveConsole.Agent;

/// <summary>
/// "3 tool calls: query_database ×2, show_table" — one wording for the chat label and the usage
/// view, so the count a capsuleer reads under a reply is the count the telemetry recorded.
///
/// <para>⚠️ For the capsuleer's eyes only. It must never reach the model: a small local model
/// given its own past tool use as text learned to WRITE the marker instead of calling the tool.
/// The chat shows it; <see cref="AgentMessage.ContentForModel"/> does not carry it.</para>
///
/// <para>The point of it is the zero. A reply that names ships, ISK and stations after calling
/// nothing did not read the database, however plausible it sounds — and a model that has drifted
/// into answering from memory is indistinguishable from one that looked, except here.</para>
/// </summary>
public static class ToolUseSummary
{
    public static string Describe(IEnumerable<KeyValuePair<string, int>> counts)
    {
        var parts = counts.Where(c => c.Value > 0)
            .Select(c => c.Value > 1 ? $"{c.Key} ×{c.Value}" : c.Key)
            .ToList();
        var total = counts.Sum(c => c.Value);
        if (total == 0) return "no tool calls";
        return $"{total} tool call{(total == 1 ? "" : "s")}: {string.Join(", ", parts)}";
    }

    /// <summary>From the telemetry's own record, <c>{"query_database":2,"show_table":1}</c>.</summary>
    public static string Describe(string toolsUsedJson)
    {
        if (string.IsNullOrWhiteSpace(toolsUsedJson)) return Describe([]);
        try
        {
            using var doc = JsonDocument.Parse(toolsUsedJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Describe([]);
            return Describe(doc.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, int>(p.Name, p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt32() : 0))
                .ToList());
        }
        catch { return Describe([]); }
    }
}
