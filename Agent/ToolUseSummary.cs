using System.Text.Json;
using EveConsole.Localization;

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
///
/// <para>In the interface language. The chat history keeps the words a reply was saved with, so
/// replies from before a language change keep the old language under them.</para>
/// </summary>
public static class ToolUseSummary
{
    public static string Describe(IEnumerable<KeyValuePair<string, int>> counts)
    {
        var parts = counts.Where(c => c.Value > 0)
            .Select(c => c.Value > 1 ? $"{c.Key} ×{c.Value}" : c.Key)
            .ToList();
        var total = counts.Sum(c => c.Value);
        if (total == 0) return AgentText.ToolCallsNone;
        return Plurals.Format(AgentText.ResourceManager, nameof(AgentText.ToolCallsOther), total, string.Join(", ", parts));
    }

    /// <summary>The counts as the chat history and the telemetry keep them,
    /// <c>{"query_database":2,"show_table":1}</c>; <c>{}</c> for a turn that called nothing.</summary>
    public static string Encode(IReadOnlyDictionary<string, int> counts) => JsonSerializer.Serialize(counts);

    /// <summary>
    /// What a reply saved before the counts were kept: the English words, "no tool calls" or
    /// "3 tool calls: query_database ×2, show_table". Read back into counts, so they are worded in
    /// the language of the day too; null for anything else.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, int>>? ReadEnglish(string text)
    {
        if (text == "no tool calls") return [];
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^\d+ tool calls?: (.+)$");
        if (!m.Success) return null;
        var counts = new List<KeyValuePair<string, int>>();
        foreach (var part in m.Groups[1].Value.Split(", "))
        {
            var x = part.LastIndexOf(" ×", StringComparison.Ordinal);
            if (x < 0) counts.Add(new(part, 1));
            else if (int.TryParse(part[(x + 2)..], out var n)) counts.Add(new(part[..x], n));
            else return null;
        }
        return counts;
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
