using System.Text.RegularExpressions;

namespace EveConsole.Agent;

/// <summary>
/// Decides, before a turn, which model answers it: the conversation model, or the one that reads
/// the capsuleer's data.
///
/// <para>⚠️ The app asks, rather than leaving the conversation model to decide mid-answer by
/// calling a tool. One word — DATA or CHAT — about one message is the kind of task a small model
/// gets right; deciding to call a tool is exactly what a 7B model failed at, answering data
/// questions from nothing instead. The hand-off tool stays as the backup for what this lets
/// through.</para>
///
/// <para>The router sees the last exchange as well as the new message, so a follow-up to a data
/// answer — "and for my alt?" — goes back to the data model, which is where it has to go.</para>
/// </summary>
public static class ModelRouter
{
    public const string SystemPrompt =
        "You direct messages for an EVE Online companion that has two parts. CHAT talks, explains, " +
        "and works the application's screens. DATA reads the capsuleer's own database and ESI. " +
        "Reply with one word — DATA or CHAT — and nothing else.";

    private const int QuoteLimit = 500;

    /// <param name="handOffWhen">The capsuleer's own words, finishing "Send a message to DATA when…".</param>
    /// <param name="lastUser">The capsuleer's previous message, if any.</param>
    /// <param name="lastReply">The companion's last reply, if any.</param>
    /// <param name="lastReplyFromData">Whether that reply came from DATA; null when not known.</param>
    /// <param name="local">
    /// The router is a model of the capsuleer's own. Qwen3-family models think aloud at length
    /// before a one-word answer unless told not to; "/no_think" is their switch, and a line of
    /// text any other model passes over.
    /// </param>
    public static string Prompt(string handOffWhen, string? lastUser, string? lastReply,
                                bool? lastReplyFromData, string message, bool local)
    {
        var when = string.IsNullOrWhiteSpace(handOffWhen) ? AgentSettings.DefaultHandOffWhen : handOffWhen.Trim();

        var exchange = lastUser is null && lastReply is null
            ? "(none — this is the first message)"
            : $"Capsuleer: {Quote(lastUser)}\n" +
              $"Companion{(lastReplyFromData is { } fromData ? fromData ? " (answered from DATA)" : " (answered from CHAT)" : "")}: {Quote(lastReply)}";

        return
            $"Send a message to DATA when {when}\n" +
            "Everything else goes to CHAT: conversation, general EVE Online knowledge, how the application works, " +
            "opening or arranging its tools, alarms, destinations, and remembering instructions.\n" +
            // ⚠️ "I'm on Assets now" went to DATA: the word matched "assets and ships" above. Naming
            // a tool is not asking what the records hold.
            "A message about the application itself — what a tool does, which tool is on screen, where to find " +
            "something — is CHAT even when it names a tool such as Assets or Market Orders. It is DATA only when " +
            "it asks what the capsuleer's own records hold.\n" +
            "A follow-up to an answer that came from DATA — \"and for my alt?\", \"what about last week?\", " +
            "\"sort that by value\" — is DATA too.\n\n" +
            $"The last exchange:\n{exchange}\n\n" +
            $"New message from the capsuleer:\n{message}\n\n" +
            "DATA or CHAT?" +
            (local ? "\n/no_think" : "");
    }

    private static string Quote(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(nothing)";
        var t = text.Trim().Replace('\n', ' ');
        return t.Length <= QuoteLimit ? t : t[..QuoteLimit] + "…";
    }

    /// <summary>
    /// The router's answer, or null when it gave none. What a model thought aloud first is set
    /// aside; an answer that starts with its word is taken at that, and one that explains itself
    /// is taken at its LAST word, which is where a reasoned answer ends up.
    /// </summary>
    public static ModelRoleKind? Parse(string? reply)
    {
        var text = Regex.Replace(reply ?? "", @"<think>[\s\S]*?(</think>|$)", " ", RegexOptions.IgnoreCase).Trim();
        var words = Regex.Matches(text, @"\b(DATA|CHAT)\b", RegexOptions.IgnoreCase);
        if (words.Count == 0) return null;

        var pick = Regex.IsMatch(text, @"^\W*(DATA|CHAT)\b", RegexOptions.IgnoreCase) ? words[0] : words[^1];
        return pick.Value.Equals("DATA", StringComparison.OrdinalIgnoreCase)
            ? ModelRoleKind.Analyst
            : ModelRoleKind.Conversation;
    }
}
