using System.Text.Json;

namespace EveConsole.Agent.Tools.Actions;

/// <summary>
/// Lets the agent write to the capsuleer's standing instructions — the same text that sits in
/// Settings → AI Agent → Personalisation and is given to the agent with every message.
///
/// <para>This is how "when I say home, I mean the Keepstar in UALX-3" becomes permanent without
/// anyone opening Settings: the capsuleer says it, the agent records it, and from the next message
/// on the prompt carries it. Without this the same sentence would have to be repeated in every
/// conversation, and it would be lost the moment the history was summarised.</para>
///
/// <para>⚠️ Append and remove, not replace. A tool that took the whole text as its input would
/// have the model rewrite every line to change one, and models drop lines when they do that. A
/// line is added, or lines matching a phrase are removed; the full text comes back after either,
/// so the model can see what it has.</para>
/// </summary>
public sealed class UpdateGuidanceTool : IAgentTool
{
    /// <summary>
    /// The guidance lives in the cached prompt prefix, which every round of every turn reads.
    /// This is roughly a thousand tokens — generous for standing instructions, and a bound on a
    /// model that decides to record the whole conversation.
    /// </summary>
    private const int MaxChars = 4_000;

    private readonly AgentService _service;

    public string Name => "update_guidance";

    public string Description =>
        "Records a standing instruction from the capsuleer so it holds in every future " +
        "conversation — what they mean by a word (\"when I say home I mean the Keepstar in " +
        "UALX-3\", \"Seafood is the system C-FD0D\"), who someone is (\"my main is Kerno\"), or " +
        "how they want you to behave (\"never read out contract ids\"). Use it when the capsuleer " +
        "TELLS you something like that — \"from now on\", \"remember that\", \"when I say\" — and " +
        "then confirm in a few words. Do NOT use it for things you looked up, for one-off requests, " +
        "or for anything the capsuleer did not ask you to keep. Each instruction is one line. To " +
        "change one, remove it and add the new wording. The full list comes back after every change.";

    public object InputSchema => new
    {
        type = "object",
        properties = new
        {
            add = new
            {
                type        = "string",
                description = "One instruction to add, as a single line in the capsuleer's own terms — " +
                              "e.g. \"When I say home, I mean the Keepstar in UALX-3.\"",
            },
            remove = new
            {
                type        = "string",
                description = "A phrase; every existing line containing it (case-insensitive) is removed. " +
                              "Use with add to change an instruction.",
            },
        },
    };

    public UpdateGuidanceTool(AgentService service) => _service = service;

    public Task<string> ExecuteAsync(JsonElement input, CancellationToken ct = default)
    {
        var (text, reply, changed) = Apply(_service.Settings.UserGuidance, Text(input, "add"), Text(input, "remove"));
        if (changed) _service.UpdateGuidance(text);
        return Task.FromResult(reply);
    }

    /// <summary>
    /// The edit itself, with nothing persisted: what the instructions become, what to tell the
    /// model, and whether anything actually changed. Pure, so it can be checked without a
    /// settings file — the one the service writes is the capsuleer's own.
    /// </summary>
    internal static (string Text, string Reply, bool Changed) Apply(string current, string add, string remove)
    {
        add    = add.Trim();
        remove = remove.Trim();

        if (add.Length == 0 && remove.Length == 0)
            return (current, "Nothing to do — give add, remove, or both.", false);

        // Lines, kept in order, blank ones dropped.
        var lines = (current ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var removed = 0;
        if (remove.Length > 0)
            removed = lines.RemoveAll(l => l.Contains(remove, StringComparison.OrdinalIgnoreCase));

        var added = false;
        if (add.Length > 0)
        {
            // One line, however it was written; and not twice.
            add = add.ReplaceLineEndings(" ").Trim();
            if (!lines.Any(l => l.Equals(add, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(add);
                added = true;
            }
        }

        var text = string.Join("\n", lines);
        if (text.Length > MaxChars)
            return (current, $"Not saved: the standing instructions would be {text.Length:N0} characters, over the "
                           + $"{MaxChars:N0} limit. Remove some first, or word this one more briefly.", false);

        var what = (added, removed) switch
        {
            (true,  0) => "Added.",
            (true,  _) => $"Replaced {removed} line(s).",
            (false, 0) => remove.Length > 0 ? $"Nothing matched '{remove}'." : "Already recorded.",
            (false, _) => $"Removed {removed} line(s).",
        };

        return (text,
                $"{what} It applies from the next message. The standing instructions are now:\n"
                + (text.Length == 0 ? "(none)" : text),
                added || removed > 0);
    }

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
