using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Alarms;

/// <summary>What a check is handed when it runs.</summary>
public sealed class AlarmEvaluationContext
{
    public required IDbContextFactory<AppDbContext> DbFactory        { get; init; }
    public required string                          ConnectionString { get; init; }
    public required Alarm                           Alarm            { get; init; }
    public required DateTimeOffset                  Now              { get; init; }

    /// <summary>
    /// The keys this alarm has already announced or banked. Most checks never look: the service
    /// does the diff. A check that folds several facts into one announcement needs to know
    /// whether the thing is new to say it as new, and that is the one question this answers.
    /// </summary>
    public IReadOnlySet<string> Seen { get; init; } = new HashSet<string>();
}

/// <summary>
/// The Alarms editor's words for one parameter of a check, in the interface language. A part
/// left null shows the schema's own English.
/// </summary>
public sealed record AlarmFieldText(string? Label = null, string? Description = null, string? Suffix = null);

/// <summary>
/// A kind of thing an alarm can watch for. Implementations are stateless: everything they need
/// comes from the JSON config and the context, and whether a match is *new* is decided by the
/// service against the seen-key ledger. That keeps adding a condition to a single file.
/// </summary>
public interface IAlarmCondition
{
    /// <summary>Stored in <see cref="Alarm.ConditionType"/>. Never change once shipped.</summary>
    string TypeKey { get; }

    /// <summary>The check's name in the agent's tool description. English, whatever the interface
    /// language; the editor shows <see cref="ScreenName"/>.</summary>
    string DisplayName { get; }

    /// <summary>Handed to the agent so it can pick the right check. English, word for word; the
    /// editor shows <see cref="ScreenDescription"/>.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the config blob. Drives both the agent tool and validation, and the
    /// editor's fields — whose words come from <see cref="ScreenField"/> and
    /// <see cref="ScreenOption"/>. Its enum values are what the config stores.</summary>
    object ParameterSchema { get; }

    // ── What the Alarms editor shows ──────────────────────────────────────────
    //
    // ⚠️ Two readers. DisplayName, Description and ParameterSchema are the AI agent's: its tool
    // description and schema are built from them word for word, and they stay English whatever
    // language the screen is in. The editor asks these instead, in the interface language — and a
    // check that says nothing here shows the agent's English, so a new check still gets a working
    // editor.

    /// <summary>The check's name in the editor's Check list.</summary>
    string ScreenName => DisplayName;

    /// <summary>What the editor says the check does, under the Check list.</summary>
    string ScreenDescription => Description;

    /// <summary>The editor's words for one parameter, by its property name in the schema; null
    /// leaves the schema's English.</summary>
    AlarmFieldText? ScreenField(string property) => null;

    /// <summary>The editor's word for one of a parameter's enum values — the value itself is what
    /// the config stores and never changes. Null shows the value as it is.</summary>
    string? ScreenOption(string property, string value) => null;

    /// <summary>
    /// One-line summary of a configured instance, for the alarm list. ⚠️ The agent reads it too —
    /// manage_alarms lists and confirms alarms with it — so it stays English.
    /// </summary>
    string Describe(JsonElement config);

    /// <summary>
    /// Title and body for an Alert or Dialog when the user has not written their own. Each
    /// check knows what is worth saying about its own matches, so the wording follows the
    /// check rather than being one generic sentence for all of them.
    ///
    /// <para>In the interface language: a person reads it. The matches' summaries under the
    /// title stay English — the agent reads them back from the event.</para>
    /// </summary>
    (string Title, string Body) DefaultText(
        string alarmName, JsonElement config, IReadOnlyList<AlarmMatch> matches)
        => (alarmName, JoinSummaries(matches));

    /// <summary>
    /// What the agent is to SAY when the alarm fires, word for word — or null, the default, to
    /// let the agent put the matches into its own words.
    ///
    /// <para>For an alarm whose whole value is in the next few seconds, the model's own words are
    /// the wrong tool: they arrive after a round trip, in whatever order the model chose, with
    /// whatever it thought worth adding. A condition that knows the priority of its own facts
    /// composes the sentence itself, and it is spoken as written, at once.</para>
    ///
    /// <para>In the interface language, since it is said to the person: where the agent is asked
    /// to say it, it is the text to say, inside instructions that stay English.</para>
    /// </summary>
    string? Announcement(JsonElement config, IReadOnlyList<AlarmMatch> matches) => null;

    /// <summary>
    /// How many stages a firing of this check can progress through, or 0 for an ordinary check.
    ///
    /// <para>A staged check emits matches that each carry a <c>stage</c>, a <c>scope_key</c>
    /// (whose situation it is — a character), an <c>episode</c> (which occurrence of it) and a
    /// <c>snooze_minutes</c> in their detail. The service fires each scope's stage separately,
    /// with only the actions tied to that stage, and repeat and cooldown do not apply — the
    /// stages are the cadence. An acknowledgement quiets the scope's episode for the snooze.</para>
    /// </summary>
    int Stages => 0;

    /// <summary>
    /// The whole of what the agent is told, for a check that needs more than "say this": a
    /// wake-up call asks a question and must let the reply come. Null, the default, leaves it
    /// to the runner's generic prompt. The capsuleer's standing instruction, if any, is appended
    /// by the runner either way.
    /// </summary>
    string? AgentPrompt(JsonElement config, IReadOnlyList<AlarmMatch> matches) => null;

    /// <summary>
    /// For a staged check: whether the situation a firing was about is still going on and still
    /// unacknowledged. Asked by a client between plays of a repeating sound, so it must read the
    /// database rather than remember anything. False, the default, stops the sound.
    /// </summary>
    Task<bool> StillHoldsAsync(
        long alarmId, string scopeKey, string episode,
        IDbContextFactory<AppDbContext> dbFactory, CancellationToken ct)
        => Task.FromResult(false);

    /// <summary>The stage a match belongs to, for a staged check; 0 otherwise.</summary>
    static int StageOf(AlarmMatch m)
        => m.Detail is { } d && d.TryGetValue("stage", out var s) && s is int stage ? stage : 0;

    /// <summary>Shared body-building for the default text: the matches, one per line, capped.</summary>
    protected static string JoinSummaries(IReadOnlyList<AlarmMatch> matches, int max = 6)
    {
        if (matches.Count == 0) return "";

        var body = string.Join("\n", matches.Take(max).Select(m => "• " + m.Summary));
        return matches.Count > max
            ? body + "\n• " + string.Format(AlarmsText.MatchesAndMore, matches.Count - max)
            : body;
    }

    /// <summary>
    /// When true, a key that stops appearing is forgotten, so the same key becomes news again
    /// if it comes back.
    ///
    /// <para>Off by default, because for an append-only source — an intel report, a killmail —
    /// a key that has been announced should stay announced forever. It is switched on for
    /// checks over mutable state, where a row leaving the result set and returning is a real
    /// event, and it is what makes "alert me when there are NO rows" able to re-arm at all.</para>
    /// </summary>
    bool ForgetsUnseenKeys => false;

    /// <summary>
    /// Everything currently matching. Return the full current match set, not a delta — the
    /// service diffs it against what this alarm has already seen.
    /// </summary>
    Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default);
}

/// <summary>
/// Pieces the checks share when they title or speak a firing, in the interface language.
/// </summary>
internal static class AlarmWords
{
    /// <summary>
    /// A hull with its article — "a Rifter", "an Abaddon", "a ship" when there is no name. An
    /// entry per case rather than "a " or "an " put in front in code: the choice is English's,
    /// and a language without articles makes both entries the bare name.
    /// </summary>
    public static string Hull(string hull) =>
        hull.Length == 0                                  ? AlarmsText.HullShip
        : "aeiou".Contains(char.ToLowerInvariant(hull[0])) ? string.Format(AlarmsText.HullAn, hull)
        :                                                   string.Format(AlarmsText.HullA, hull);

    /// <summary>Names as a spoken list: "A, B and C", and past three "A, B, C and more".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0    => "",
        1    => items[0],
        <= 3 => string.Format(AlarmsText.ListAnd, string.Join(", ", items.Take(items.Count - 1)), items[^1]),
        _    => string.Format(AlarmsText.ListAndMore, string.Join(", ", items.Take(3))),
    };
}
