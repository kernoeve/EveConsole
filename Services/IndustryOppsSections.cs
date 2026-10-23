using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Services;

/// <summary>One of the Industry Ops Summary tab's lists: a title, and which items belong in it.</summary>
public sealed record OppsSection(string Key, string Title, Func<IndustryRow, bool> Has);

/// <summary>
/// The Industry Ops Summary tab's lists, each a kind of item, most 30-day profit first.
///
/// <para>Items are sorted by what they are in the SDE, their inventory category and group, not
/// by market group: a market group says where an item is sold, the category what it is. Ships
/// are split by hull size as the engineering rigs class them, and by tech level by their meta
/// group; a faction or other ship is in neither tech list.</para>
/// </summary>
public static class IndustryOppsSections
{
    // Inventory categories.
    private const int Ship = 6, Module = 7, Charge = 8, Commodity = 17, Drone = 18, Deployable = 22,
                      Subsystem = 32, StructureModule = 66, Fighter = 87;

    /// <summary>The construction components, capital ones, advanced capital ones, structure
    /// components, hybrid tech components and R.A.M. tools.</summary>
    private static readonly HashSet<int> ComponentGroups = [334, 873, 913, 536, 964, 332];

    /// <summary>In the order shown: two to a row, left then right.</summary>
    public static IReadOnlyList<OppsSection> All =>
    [
        new("t1_large",   IndustryText.OppsSecT1Large,   r => Hull(r) == Size.Large     && IsT1(r)),
        new("t1_small",   IndustryText.OppsSecT1Small,   r => Hull(r) == Size.SmallMedium && IsT1(r)),
        new("t2_large",   IndustryText.OppsSecT2Large,   r => Hull(r) == Size.Large     && r.MetaGroupId == 2),
        new("t2_small",   IndustryText.OppsSecT2Small,   r => Hull(r) == Size.SmallMedium && r.MetaGroupId == 2),
        new("ammo",       IndustryText.OppsSecAmmoDrones, r => r.CategoryId is Charge or Drone or Fighter),
        new("modules",    IndustryText.OppsSecModules,   r => r.CategoryId == Module && !IsShipRig(r)),
        new("rigs",       IndustryText.OppsSecRigsSubsystems, r => IsShipRig(r) || r.CategoryId == Subsystem),
        new("deployables", IndustryText.OppsSecDeployables, r => r.CategoryId == Deployable),
        new("components", IndustryText.OppsSecComponents, r => r.CategoryId == Commodity && ComponentGroups.Contains(r.GroupId)),
        new("structure",  IndustryText.OppsSecStructureModules, r => r.CategoryId == StructureModule),
    ];

    /// <summary>A list: every item of its kind that sold at a profit in the last 30 days, most
    /// profit first. Not a top few; the list scrolls.</summary>
    public static List<IndustryRow> Rows(IEnumerable<IndustryRow> rows, OppsSection section) =>
        [.. rows.Where(r => section.Has(r) && r.HasSales30d && r.Profit30d > 0)
                .OrderByDescending(r => r.Profit30d)];

    private enum Size { None, SmallMedium, Large }

    /// <summary>Tech I, or a ship with no meta group at all.</summary>
    private static bool IsT1(IndustryRow r) => r.MetaGroupId is 0 or 1;

    /// <summary>A ship rig: a module in one of the "Rig …" groups (Rig Armor, Rig Drones…).</summary>
    private static bool IsShipRig(IndustryRow r) => r.CategoryId == Module && r.GroupName.StartsWith("Rig ", StringComparison.Ordinal);

    /// <summary>
    /// A ship's hull size, as the engineering rigs class it: frigates to battlecruisers are small
    /// and medium; battleships, freighters, industrial command ships and capitals are large.
    /// </summary>
    private static Size Hull(IndustryRow r)
    {
        if (r.CategoryId != Ship) return Size.None;
        var key = IndyRigMatching.ItemCategoryKey(r.TypeId, isReaction: false,
            new Dictionary<int, int> { [r.TypeId] = r.GroupId },
            new Dictionary<int, IndyRigMatching.GroupInfo> { [r.GroupId] = new(r.GroupId, r.CategoryId, r.GroupName) });
        return key.Replace("adv_", "") switch
        {
            "small_ships" or "medium_ships"  => Size.SmallMedium,
            "large_ships" or "capital_ships" => Size.Large,
            _                                => Size.None,
        };
    }
}
