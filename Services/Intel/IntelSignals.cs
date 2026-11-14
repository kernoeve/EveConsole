using System.Text.Json;

namespace EveConsole.Services.Intel;

/// <summary>
/// What PCs sharing one database tell each other about intel, over <see cref="ClientSignals"/>.
///
/// <para>⚠️ Only the PC holding the worker lease parses intel. Every PC still reads its own chat logs
/// (they are files on that machine), so the others store their lines and then say so; the worker parses
/// them. Two PCs parsing the same lines at once would each write the same reports, and the second would
/// fail on the unique chat-message index halfway through its batch.</para>
///
/// <list type="bullet">
/// <item><see cref="Chat"/>: "I have stored new chat lines": the worker runs a pass.</item>
/// <item><see cref="Changed"/>: "intel was written": every PC refreshes what it shows, and the worker
/// evaluates the intel alarms.</item>
/// <item><see cref="Reparse"/>: "re-parse the history", from the Settings button on a PC that is not
/// the worker. The worker's progress comes back through the activity board.</item>
/// </list>
///
/// <para>No replay, like every signal: a PC that is not listening misses it. That costs nothing here.
/// A worker that missed <see cref="Chat"/> parses the same lines on its next pass, because its watermark
/// says where it stopped, not what it was told.</para>
/// </summary>
public static class IntelSignals
{
    public const string Chat    = "intel.chat";
    public const string Changed = "intel.changed";
    public const string Reparse = "intel.reparse";

    /// <summary>The wire form: <c>{"Kind":"intel.chat"}</c>, the same discriminator every signal carries.</summary>
    public static string Payload(string kind) => JsonSerializer.Serialize(new Wire { Kind = kind });

    /// <summary>The intel kind a payload carries, or null when it is something else.</summary>
    public static string? KindOf(string payload)
    {
        // Every signal on the channel passes through here, and nearly all are something else.
        if (!payload.Contains("\"intel.", StringComparison.Ordinal)) return null;

        try
        {
            var kind = JsonSerializer.Deserialize<Wire>(payload)?.Kind;
            return kind is Chat or Changed or Reparse ? kind : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed class Wire
    {
        public string Kind { get; set; } = "";
    }
}
