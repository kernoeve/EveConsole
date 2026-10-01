namespace EveConsole.Services.Fitting;

public enum ModuleState { Offline = -1, Online = 0, Active = 1, Overheated = 2 }

public enum FitSlot { High, Mid, Low, Rig, Subsystem, Service, None }

public sealed record FitModule(int TypeId, ModuleState State, int? ChargeTypeId = null);

/// <summary>
/// A drone stack, or a fighter squadron. For drones, <paramref name="Active"/> is how many are
/// launched. For a squadron, <paramref name="Count"/> is its size and any <paramref name="Active"/>
/// above zero means it is in a launch tube; <paramref name="Abilities"/> are the effect ids of the
/// abilities switched on, or null for the defaults (see <see cref="FighterAbilities"/>).
/// </summary>
public sealed record FitDrone(int TypeId, int Count, int Active, IReadOnlyList<int>? Abilities = null);

/// <summary>What a fit is made of, by type id. Everything the engine needs and nothing it computes.</summary>
public sealed class FitDefinition
{
    public int    ShipTypeId { get; set; }
    public string Name       { get; set; } = "";
    public List<FitModule> Modules  { get; } = [];
    public List<FitDrone>  Drones   { get; } = [];
    public List<int>       Implants { get; } = [];
    public List<int>       Boosters { get; } = [];
    public List<(int TypeId, int Quantity)> Cargo { get; } = [];
    /// <summary>A tactical destroyer's mode (a "Ship Modifiers" type such as Svipul Defense Mode),
    /// for a hull that has them; null for any other.</summary>
    public int? ModeTypeId { get; set; }

    public IEnumerable<int> AllTypeIds() =>
        new[] { ShipTypeId }
            .Concat(ModeTypeId is { } mode ? [mode] : Array.Empty<int>())
            .Concat(Modules.Select(m => m.TypeId))
            .Concat(Modules.Where(m => m.ChargeTypeId is not null).Select(m => m.ChargeTypeId!.Value))
            .Concat(Drones.Select(d => d.TypeId))
            .Concat(Implants).Concat(Boosters)
            .Concat(Cargo.Select(c => c.TypeId));
}

/// <summary>The pilot: a level per skill. Skills not listed are untrained.</summary>
public sealed class SkillSet
{
    private readonly Dictionary<int, int> _levels;
    public string Name { get; }

    public SkillSet(string name, IDictionary<int, int> levels)
    {
        Name    = name;
        _levels = new Dictionary<int, int>(levels);
    }

    public int Level(int skillTypeId) => _levels.GetValueOrDefault(skillTypeId);
    public IReadOnlyDictionary<int, int> Levels => _levels;

    /// <summary>Every published skill at <paramref name="level"/> — the usual "All V" pilot.</summary>
    public static SkillSet AllAt(DogmaData data, int level) =>
        new($"All {level}", data.SkillTypeIds.ToDictionary(id => id, _ => level));
}
