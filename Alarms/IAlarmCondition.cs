using System.Text.Json;
using EveConsole.Data;
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
}

/// <summary>
/// A kind of thing an alarm can watch for. Implementations are stateless: everything they need
/// comes from the JSON config and the context, and whether a match is *new* is decided by the
/// service against the seen-key ledger. That keeps adding a condition to a single file.
/// </summary>
public interface IAlarmCondition
{
    /// <summary>Stored in <see cref="Alarm.ConditionType"/>. Never change once shipped.</summary>
    string TypeKey { get; }

    string DisplayName { get; }

    /// <summary>Shown in the editor and handed to the agent so it can pick the right check.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the config blob. Drives both the agent tool and validation.</summary>
    object ParameterSchema { get; }

    /// <summary>One-line human summary of a configured instance, for the alarm list.</summary>
    string Describe(JsonElement config);

    /// <summary>
    /// Everything currently matching. Return the full current match set, not a delta — the
    /// service diffs it against what this alarm has already seen.
    /// </summary>
    Task<IReadOnlyList<AlarmMatch>> EvaluateAsync(
        JsonElement config, AlarmEvaluationContext ctx, CancellationToken ct = default);
}
