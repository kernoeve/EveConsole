using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Puts tabular results in a tab of their own instead of in the conversation.
///
/// <para>⚠️ The result string deliberately does NOT contain the data. It reports that the tab was
/// opened and how many rows it holds, so the model has nothing to quote back — the whole point is
/// that the rows stop being part of the chat, which is what keeps them out of the history the next
/// turn pays for and out of the text that gets read aloud.</para>
/// </summary>
public sealed class ShowTableTool : IAgentTool
{
    /// <summary>
    /// ⚠️ A ceiling, because everything here has already been through the model's context once.
    /// A table this long is a sign the query wanted narrowing, and truncating loudly is better
    /// than a tab that quietly shows part of an answer.
    /// </summary>
    private const int MaxRows = 5000;

    private readonly Func<string, string, string[], List<string[]>, string> _open;

    public string Name => "show_table";

    public string Description =>
        "Shows tabular data to the capsuleer in a new tab, instead of listing it in the chat. " +
        "PREFER THIS over writing rows into your reply whenever the answer is a list of records " +
        "with more than one field — even a short one. Rows in a tab can be sorted, selected, " +
        "copied into a spreadsheet and saved as CSV; rows in the chat can only be scrolled past, " +
        "clutter the conversation, and are read aloud when speech is on. " +
        "Each call opens a NEW tab, so earlier answers are not overwritten. " +
        "After calling this, tell the capsuleer what you found in a sentence or two and name the " +
        "tab — do NOT repeat the rows.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            title = new
            {
                type = "string",
                description = "Short tab name, ideally two to four words — e.g. \"BNI Capital Buyers\". " +
                              "This is what the capsuleer sees on the tab, so make it specific.",
            },
            caption = new
            {
                type = "string",
                description = "Optional single line shown above the grid, saying what it is and any " +
                              "qualification that matters — the date range covered, what was excluded, " +
                              "or which valuation was used.",
            },
            columns = new
            {
                type        = "array",
                items       = new { type = "string" },
                description = "Column headers, left to right.",
            },
            rows = new
            {
                type        = "array",
                items       = new { type = "array", items = new { type = "string" } },
                description = "The rows. Each row is an array of cell values in the same order as " +
                              "columns. Format numbers and dates for a reader — thousands separators, " +
                              "ISK suffixes, yyyy-MM-dd — because these are displayed as given.",
            },
        },
        required = new[] { "title", "columns", "rows" },
    };

    public ShowTableTool(Func<string, string, string[], List<string[]>, string> openCallback)
        => _open = openCallback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var title   = Text(input, "title");
        var caption = Text(input, "caption");

        if (string.IsNullOrWhiteSpace(title)) title = "Results";

        var columns = input.TryGetProperty("columns", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
            : [];

        if (columns.Length == 0)
            return Task.FromResult("No columns were supplied, so there is nothing to show. Provide a columns array.");

        var rows = new List<string[]>();
        var truncated = false;
        if (input.TryGetProperty("rows", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in r.EnumerateArray())
            {
                if (rows.Count >= MaxRows) { truncated = true; break; }
                rows.Add(row.ValueKind == JsonValueKind.Array
                    // ⚠️ Cells are read whatever their JSON type. A model that emits a bare number
                    // for a numeric column is not wrong, and GetString() on it returns null.
                    ? row.EnumerateArray().Select(Cell).ToArray()
                    : [Cell(row)]);
            }
        }

        if (rows.Count == 0)
            return Task.FromResult("No rows were supplied, so there is nothing to show. "
                                 + "If the answer really is empty, say so in your reply instead of opening a tab.");

        var opened = _open(title, caption, columns, rows);

        return Task.FromResult(truncated
            ? $"{opened} Only the first {MaxRows:N0} rows were shown — tell the capsuleer the list was cut off "
            + "and offer to narrow it. Do not list the rows in your reply."
            : $"{opened} Summarise what it shows in a sentence or two and name the tab. "
            + "Do not list the rows in your reply.");
    }

    private static string Cell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.ToString(),
    };

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
