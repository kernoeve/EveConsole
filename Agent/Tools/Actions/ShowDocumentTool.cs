using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Writes a formatted report into a tab of its own instead of into the conversation.
///
/// <para>The prose counterpart to <see cref="ShowTableTool"/>. Same reasoning: a long answer in
/// the chat panel is scrolled past, carried in the history of every later turn, and read aloud a
/// paragraph at a time. A report belongs somewhere it can be read, kept and saved.</para>
/// </summary>
public sealed class ShowDocumentTool : IAgentTool
{
    /// <summary>Generous — a long report is the point — but not unbounded.</summary>
    private const int MaxChars = 100_000;

    private readonly Func<string, string, string> _open;

    public string Name => "show_document";

    public string Description =>
        "Shows a formatted document to the capsuleer in a new tab, instead of writing it into the " +
        "chat. Use this for anything that is a REPORT rather than a reply: a summary with sections, " +
        "an analysis, a plan, a comparison, a briefing — anything the capsuleer might want to keep, " +
        "re-read or save. Content is Markdown: headings, bold and italic, bullet and numbered lists, " +
        "tables, block quotes, code blocks and horizontal rules all render. " +
        "Each call opens a NEW tab, so earlier documents are not overwritten. " +
        "After calling this, give the capsuleer the headline finding in a sentence or two and name " +
        "the tab — do NOT repeat the document.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            title = new
            {
                type = "string",
                description = "Short tab name, ideally two to four words — e.g. \"Q3 Production Review\". " +
                              "This is what the capsuleer sees on the tab, so make it specific.",
            },
            markdown = new
            {
                type = "string",
                description = "The document, in Markdown. Open with a heading. Use tables for tabular " +
                              "sections. Write it as something the capsuleer would be content to read " +
                              "a week later — say what the numbers are, where they came from, and what " +
                              "they mean, rather than only listing them.",
            },
        },
        required = new[] { "title", "markdown" },
    };

    public ShowDocumentTool(Func<string, string, string> openCallback) => _open = openCallback;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var title    = Text(input, "title");
        var markdown = Text(input, "markdown");

        if (string.IsNullOrWhiteSpace(markdown))
            return Task.FromResult("No content was supplied, so there is nothing to show. "
                                 + "Provide the document in the markdown field.");

        if (string.IsNullOrWhiteSpace(title)) title = "Report";

        var truncated = markdown.Length > MaxChars;
        if (truncated) markdown = markdown[..MaxChars] + "\n\n*(truncated)*";

        var opened = _open(title, markdown);

        return Task.FromResult(truncated
            ? $"{opened} It was too long and has been cut off — tell the capsuleer. "
            + "Do not repeat the document in your reply."
            : $"{opened} Give the headline finding in a sentence or two and name the tab. "
            + "Do not repeat the document in your reply.");
    }

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
