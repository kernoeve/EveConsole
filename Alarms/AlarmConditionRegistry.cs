using EveConsole.Alarms.Conditions;
using EveConsole.Services;

namespace EveConsole.Alarms;

/// <summary>
/// The set of checks an alarm can use. Adding a condition means writing one class and adding
/// one line here — the editor dropdown, the agent's tool schema and the evaluator all read
/// from this list.
/// </summary>
public sealed class AlarmConditionRegistry
{
    private readonly Dictionary<string, IAlarmCondition> _byKey;

    public AlarmConditionRegistry(IEnumerable<IAlarmCondition> conditions)
    {
        All   = conditions.ToList();
        _byKey = All.ToDictionary(c => c.TypeKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The built-in set, in the order they should appear in the editor.</summary>
    public static AlarmConditionRegistry CreateDefault(SystemGraph graph) => new(
    [
        new TimerCondition(),
        new IntelCondition(graph),
        new MarketContractCondition(),
        new SqlCondition(),
    ]);

    public IReadOnlyList<IAlarmCondition> All { get; }

    public IAlarmCondition? Find(string typeKey) =>
        _byKey.TryGetValue(typeKey ?? "", out var c) ? c : null;
}
