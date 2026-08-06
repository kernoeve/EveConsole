using EveConsole.Models;

namespace EveConsole.Services;

/// <summary>
/// Everything a production plan needs that does not depend on what is being built — the
/// blueprint index, the park's structures and rigs, cost indices, market prices.
///
/// <para>Loading this is the expensive part of a plan: several full-table reads, around 360ms,
/// and identical for every item. Splitting it out lets a caller that needs thousands of plans —
/// <see cref="BuildCostService"/> re-costing every buildable item — pay for it once instead of
/// once per item, which is the difference between seconds and twenty minutes.</para>
///
/// <para>Treat it as read-only. Anything a plan mutates (unit prices and BPC prices, which price
/// overrides rewrite per queue) is copied by the caller before use.</para>
/// </summary>
public sealed class ProductionContext
{
    public required int ParkId { get; init; }

    // ── Blueprint index ──────────────────────────────────────────────────────
    public required List<SdeBlueprintProduct>            BpProducts         { get; init; }
    public required List<int>                            BpTypeIds          { get; init; }
    public required Dictionary<int, SdeBlueprintProduct> BlueprintByProduct { get; init; }
    public required Dictionary<int, List<SdeBlueprintMaterial>> MaterialsByBp { get; init; }
    public required HashSet<int>                         MarketBlueprints   { get; init; }
    public required HashSet<int>                         InventedFromMarket { get; init; }

    // ── Type names, groups, categories ───────────────────────────────────────
    public required Dictionary<int, string> TypeNames        { get; init; }
    public required Dictionary<int, TypeGroup> TypeGroupMap  { get; init; }
    public required Dictionary<int, GroupCat> GroupCatMap    { get; init; }
    public required HashSet<int>            T2TypeIds        { get; init; }
    public required HashSet<int>            TitanKeepstarIds { get; init; }

    // ── Park, structures, rigs ───────────────────────────────────────────────
    public required List<IndyStructure>                  Structures       { get; init; }
    public required List<IndyStructureRig>               Rigs             { get; init; }
    public required List<IndyCategoryAssignment>         Assignments      { get; init; }
    public required Dictionary<int, IndyStructure?>      ItemOverrides    { get; init; }
    public required Dictionary<string, IndyStructure?>   StructByCategory { get; init; }

    public required Dictionary<int, double> MfgRigBonusAttr    { get; init; }
    public required Dictionary<int, double> RxnRigBonusAttr    { get; init; }
    public required Dictionary<int, double> RigLowsecMultAttr  { get; init; }
    public required Dictionary<int, double> RigNullsecMultAttr { get; init; }
    public required Dictionary<int, string> RigCategoryKeys    { get; init; }

    // ── Cost indices ─────────────────────────────────────────────────────────
    public required Dictionary<string, int> SystemIds { get; init; }
    public required Dictionary<int, Dictionary<string, double>> CiLookup { get; init; }

    // ── Prices ───────────────────────────────────────────────────────────────
    public required double                          MarkupFactor    { get; init; }
    public required Dictionary<int, decimal>        UnitCosts       { get; init; }
    public required Dictionary<int, List<(int Me, decimal PerRun)>> BpcPerRun { get; init; }
    public required HashSet<int>                    BoughtSet       { get; init; }
    public required Dictionary<int, PriceOverride>  Overrides       { get; init; }
    public required Dictionary<int, double>         AdjPrices       { get; init; }
    public required Dictionary<int, decimal>        BuildCostLookup { get; init; }

    /// <summary>Type id → its SDE group. Named rather than anonymous so it can cross a method.</summary>
    public readonly record struct TypeGroup(int TypeId, int GroupId);

    /// <summary>Group id → its category and name.</summary>
    public readonly record struct GroupCat(int GroupId, int CategoryId, string Name);
}
