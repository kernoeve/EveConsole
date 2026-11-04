using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>
/// The JSON files the Scheduler and Alarms export and import: every task or alarm, with its own
/// settings written out as JSON objects rather than strings, so the file can be read and edited.
///
/// <para>⚠️ No secret goes in. A task names where it posts by kind and row id, never a webhook
/// URL; the label beside it is only for finding the same place on another machine.</para>
/// </summary>
public static class AlarmTaskTransfer
{
    public const string TasksFormat  = "eveconsole.scheduled-tasks";
    public const string AlarmsFormat = "eveconsole.alarms";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented          = true,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters             = { new JsonStringEnumConverter() },
    };

    // ── Scheduled tasks ──────────────────────────────────────────────────────

    public sealed record TasksFile(string Format, int Version, DateTimeOffset ExportedAt, List<TaskEntry> Tasks);

    /// <param name="DestinationLabel">What the destination was called where it was exported, for
    /// matching a webhook by name where its row id means something else.</param>
    public sealed record TaskEntry(
        string Name, bool Enabled, string Kind, int IntervalMinutes, int DaysOfWeek, int TimeOfDayMinutes,
        int DayOfMonth, int MonthOfYear, bool SkipIfMissed, string TaskType, JsonNode? Config,
        string? DestinationLabel);

    public static async Task WriteTasksAsync(Stream stream, IEnumerable<ScheduledTask> tasks, Func<string, string, string?> labelOf)
    {
        var entries = tasks.Select(t =>
        {
            var cfg = ScheduledTaskConfig.FromJson(t.Config);
            return new TaskEntry(t.Name, t.Enabled, t.Kind, t.IntervalMinutes, t.DaysOfWeek, t.TimeOfDayMinutes,
                                 t.DayOfMonth, t.MonthOfYear, t.SkipIfMissed, t.TaskType, Node(cfg.ToJson()),
                                 cfg.DestinationKind.Length > 0 ? labelOf(cfg.DestinationKind, cfg.DestinationId) : null);
        }).ToList();
        await JsonSerializer.SerializeAsync(stream, new TasksFile(TasksFormat, 1, DateTimeOffset.UtcNow, entries), Opts);
    }

    /// <summary>The tasks in an export, or null when the file is not one.</summary>
    public static async Task<List<TaskEntry>?> ReadTasksAsync(Stream stream)
    {
        try
        {
            var file = await JsonSerializer.DeserializeAsync<TasksFile>(stream, Opts);
            return file?.Format == TasksFormat ? file.Tasks ?? [] : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The entry as a task, with its config as stored; the caller decides the destination.</summary>
    public static ScheduledTask ToTask(TaskEntry e, ScheduledTaskConfig cfg) => new()
    {
        Name             = e.Name,
        Enabled          = e.Enabled,
        Kind             = e.Kind,
        IntervalMinutes  = e.IntervalMinutes,
        DaysOfWeek       = e.DaysOfWeek,
        TimeOfDayMinutes = e.TimeOfDayMinutes,
        DayOfMonth       = e.DayOfMonth,
        MonthOfYear      = e.MonthOfYear,
        SkipIfMissed     = e.SkipIfMissed,
        TaskType         = e.TaskType,
        Config           = cfg.ToJson(),
    };

    // ── Alarms ───────────────────────────────────────────────────────────────

    public sealed record AlarmsFile(string Format, int Version, DateTimeOffset ExportedAt, List<AlarmEntry> Alarms);

    public sealed record AlarmEntry(
        string Name, bool Enabled, string ConditionType, JsonNode? Condition, AlarmRepeat Repeat,
        int PollSeconds, int CooldownSeconds, string? ActiveFrom, string? ActiveThru, List<ActionEntry> Actions);

    public sealed record ActionEntry(AlarmActionKind Kind, JsonNode? Config);

    public static async Task WriteAlarmsAsync(Stream stream, IEnumerable<(Alarm Alarm, IEnumerable<AlarmAction> Actions)> alarms)
    {
        var entries = alarms.Select(x => new AlarmEntry(
            x.Alarm.Name, x.Alarm.Enabled, x.Alarm.ConditionType, Node(x.Alarm.ConditionJson), x.Alarm.Repeat,
            x.Alarm.PollSeconds, x.Alarm.CooldownSeconds, x.Alarm.ActiveFrom, x.Alarm.ActiveThru,
            [.. x.Actions.OrderBy(a => a.Ordinal).Select(a => new ActionEntry(a.Kind, Node(a.ConfigJson)))])).ToList();
        await JsonSerializer.SerializeAsync(stream, new AlarmsFile(AlarmsFormat, 1, DateTimeOffset.UtcNow, entries), Opts);
    }

    /// <summary>The alarms in an export, or null when the file is not one.</summary>
    public static async Task<List<AlarmEntry>?> ReadAlarmsAsync(Stream stream)
    {
        try
        {
            var file = await JsonSerializer.DeserializeAsync<AlarmsFile>(stream, Opts);
            return file?.Format == AlarmsFormat ? file.Alarms ?? [] : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>A JSON object back to the compact text it is stored as.</summary>
    public static string Text(JsonNode? node) => node switch
    {
        null                                                      => "{}",
        JsonValue v when v.TryGetValue<string>(out var raw)       => raw,   // kept as text by Node
        _                                                         => node.ToJsonString(),
    };

    /// <summary>
    /// Stored JSON as a node, so the file shows it as an object. Text that is not JSON is kept
    /// as a string rather than dropped.
    /// </summary>
    private static JsonNode? Node(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return JsonValue.Create(json); }
    }
}
