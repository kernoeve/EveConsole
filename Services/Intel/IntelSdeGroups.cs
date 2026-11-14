using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Intel;

/// <summary>
/// The SDE groups intel reads meaning into, found by their NAME in the imported SDE, never by a
/// number written here.
///
/// <para>⚠️ Why names: the group numbers were remembered, not read, when this was designed, and a
/// wrong number fails silently (a smartbomb camp simply never says so). A name that is not found
/// turns its flag off, and tools/IntelCheck fails on it, so a renamed group is noticed rather than
/// guessed at.</para>
/// </summary>
public sealed class IntelSdeGroups
{
    public const string SmartBombName       = "Smart Bomb";
    public const string InterdictorName     = "Interdictor";
    public const string HeavyInterdictorName = "Heavy Interdiction Cruiser";
    public const string SphereLauncherName  = "Interdiction Sphere Launcher";
    public const string FieldGeneratorName  = "Warp Disrupt Field Generator";
    public const string CapsuleName         = "Capsule";
    public const string MobileDisruptorName = "Mobile Warp Disruptor";
    public const string CynoFieldName       = "Cynosural Field";
    public const string ShipCategoryName    = "Ship";

    /// <summary>Every group name looked for, for the check.</summary>
    public static readonly IReadOnlyList<string> Names =
    [
        SmartBombName, InterdictorName, HeavyInterdictorName, SphereLauncherName, FieldGeneratorName,
        CapsuleName, MobileDisruptorName, CynoFieldName,
    ];

    /// <summary>Group id → its category, for every group (a few thousand rows).</summary>
    public IReadOnlyDictionary<int, int> CategoryOf { get; }

    /// <summary>The ship category's id, or null when the SDE has none named so.</summary>
    public int? ShipCategory { get; }

    /// <summary>Name → group id, for the names above that the SDE has.</summary>
    public IReadOnlyDictionary<string, int> Found { get; }

    public IReadOnlyList<string> Missing => [.. Names.Where(n => !Found.ContainsKey(n))];

    public IntelSdeGroups(IEnumerable<(int GroupId, int CategoryId, string Name)> groups,
                          IEnumerable<(int CategoryId, string Name)> categories)
    {
        var list = groups.ToList();
        CategoryOf = list.GroupBy(g => g.GroupId).ToDictionary(g => g.Key, g => g.First().CategoryId);
        ShipCategory = categories.Where(c => c.Name == ShipCategoryName).Select(c => (int?)c.CategoryId).FirstOrDefault();

        // In ship modules and ships alike a name is unique per category; among the few names that
        // repeat across categories, the published ship or module group is the lowest id.
        Found = Names
            .Select(n => (Name: n, Ids: list.Where(g => g.Name == n).Select(g => g.GroupId).Order().ToList()))
            .Where(x => x.Ids.Count > 0)
            .ToDictionary(x => x.Name, x => x.Ids[0], StringComparer.Ordinal);
    }

    public static readonly IntelSdeGroups Empty = new([], []);

    public int? Group(string name) => Found.TryGetValue(name, out var id) ? id : null;

    public bool Is(int? groupId, string name) => groupId is int g && Found.TryGetValue(name, out var id) && id == g;

    /// <summary>Whether a group is a ship's (a hull a pilot can lose, a capsule included).</summary>
    public bool IsShip(int groupId) =>
        ShipCategory is int ship && CategoryOf.TryGetValue(groupId, out var cat) && cat == ship;

    public static async Task<IntelSdeGroups> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var groups = await db.SdeGroups.AsNoTracking()
            .Select(g => new { g.GroupId, g.CategoryId, g.Name }).ToListAsync(ct).ConfigureAwait(false);
        var categories = await db.SdeCategories.AsNoTracking()
            .Select(c => new { c.CategoryId, c.Name }).ToListAsync(ct).ConfigureAwait(false);
        return new IntelSdeGroups(groups.Select(g => (g.GroupId, g.CategoryId, g.Name)),
                                  categories.Select(c => (c.CategoryId, c.Name)));
    }
}
