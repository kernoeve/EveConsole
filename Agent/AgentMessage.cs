using System.Text.Json.Serialization;
using EveConsole.Localization;

namespace EveConsole.Agent;

public enum MessageRole { User, Assistant }

public sealed record AgentMessage
{
    public MessageRole    Role      { get; init; }
    public string         Content   { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }

    [JsonIgnore]
    public bool IsSummary { get; init; }

    /// <summary>
    /// Whether this message belongs on screen. False for text the app injected on the
    /// capsuleer's behalf — an alarm handing the agent something to report, say. The model
    /// still receives it, because the reply makes no sense without it; the capsuleer sees only
    /// the reply.
    ///
    /// <para>Persisted, unlike <see cref="IsSummary"/>: a reload that forgot this would put the
    /// hidden text back on screen. Defaults to true, so history written before the flag existed
    /// loads as visible.</para>
    /// </summary>
    public bool ShowInChat { get; init; } = true;

    /// <summary>
    /// What the agent's turn called to produce this reply, as <see cref="ToolUseSummary"/> words
    /// it. Null on a message written before this was recorded; empty never — a turn that called
    /// nothing says so, which is the case worth seeing.
    ///
    /// <para>⚠️ Shown under the bubble and nowhere else. It is deliberately absent from
    /// <see cref="ContentForModel"/>: fed back as text, a small model learned to write the
    /// summary in place of calling the tool.</para>
    /// </summary>
    public string? ToolsUsed { get; init; }

    /// <summary>
    /// The model that wrote this reply, as Settings names it. Null for replies from before there
    /// was a choice, and for the app's own lines. Shown under the bubble — the persona is one,
    /// but which model spoke for it, and whether that was the paid one, is the capsuleer's to see.
    /// </summary>
    public string? AnsweredBy { get; init; }

    /// <summary>
    /// Which model answered: the conversation model, or the one that reads the data. Null when one
    /// model did everything. The router reads it — a follow-up to a data answer is data too.
    /// </summary>
    public ModelRoleKind? AnsweredAs { get; init; }

    /// <summary>When it was said, in the capsuleer's own time, for the label above the bubble.</summary>
    [JsonIgnore]
    public string TimeText => Timestamp.ToLocalTime().ToString("d MMM yyyy HH:mm");

    /// <summary>
    /// The same moment as the model is told it, on EVE time — the clock every timestamp in the
    /// database is on, so "five minutes ago" and "five weeks ago" are a subtraction it can do.
    /// </summary>
    [JsonIgnore]
    public string EveTimeText => Timestamp.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + " EVE";

    /// <summary>The line above the bubble: when, and for the agent's replies, which model wrote it
    /// and what it called.</summary>
    [JsonIgnore]
    public string MetaText => string.Join("  ·  ",
        new[] { TimeText, AnsweredBy, ToolsUsedLabel(ToolsUsed) }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>
    /// <see cref="ToolsUsed"/> as the line above the bubble shows it. The app's own lines carry a
    /// marker there rather than a summary — "alarm", "voice change", "model change" — which stays
    /// in English in the saved history, because the agent panel compares it; only here is it put
    /// into words. Anything else is shown as it was saved.
    /// </summary>
    private static string? ToolsUsedLabel(string? toolsUsed) => toolsUsed switch
    {
        "alarm"        => AgentText.MetaAlarm,
        "voice change" => AgentText.MetaVoiceChange,
        "model change" => AgentText.MetaModelChange,
        _              => toolsUsed,
    };

    /// <summary>
    /// The tool the capsuleer had on screen when they wrote this — the Tool Reference's name for
    /// it. Null for what the app wrote, and for messages from before this was kept.
    ///
    /// <para>⚠️ In the message, not only in the live app state beside it. A small model asked
    /// "what about this?" answered from its own last reply rather than the app state, and went on
    /// naming the Universe Map after the capsuleer had moved to Assets. Stamped here, each message
    /// says where it was written, and the history shows the move plainly.</para>
    /// </summary>
    public string? OnScreen { get; init; }

    /// <summary>
    /// The text as every provider sends it: the capsuleer's turns carry when they were sent, and
    /// what they had on screen; the agent's own and the summary do not.
    ///
    /// <para>⚠️ One definition, used by every provider. The stamp is fixed at the moment the message
    /// was written, so a cached prefix is unchanged by it; and only the capsuleer's turns carry it,
    /// because a stamp on the model's own past replies teaches it to write one.</para>
    /// </summary>
    [JsonIgnore]
    public string ContentForModel =>
        Role == MessageRole.User && !IsSummary
            ? $"[{EveTimeText}{(string.IsNullOrWhiteSpace(OnScreen) ? "" : $", looking at {OnScreen}")}] {Content}"
            : Content;

    [JsonConstructor]
    public AgentMessage(MessageRole role, string content, DateTimeOffset timestamp)
    {
        Role = role; Content = content; Timestamp = timestamp;
    }

    public AgentMessage(MessageRole role, string content)
        : this(role, content, DateTimeOffset.UtcNow) { }

    public static AgentMessage Summary(string content) =>
        new(MessageRole.Assistant, content) { IsSummary = true };
}
