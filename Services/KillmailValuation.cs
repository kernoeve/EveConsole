using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// What a killmail was worth, in one place.
///
/// <para>⚠️ Extracted because it was in two places and they disagreed. The Killmail Browser had
/// the correct version; the Overview and Corp Activity kill lists had their own SQL that priced
/// every item straight off <c>MarketItemPrices</c>. Two differences fell out of that, pulling in
/// opposite directions, which is why the gap was never a clean multiple:</para>
/// <list type="bullet">
/// <item>Blueprint copies were valued as ORIGINALS. <c>MarketItemPrices</c> carries the BPO
/// price, and for a capital or T2 blueprint that is enormous — one kill read 268.35B against a
/// true 106.75B.</item>
/// <item>The victim's own hull was left out entirely.</item>
/// </list>
///
/// <para>That copy had already been patched once, for an unrelated duplicate-config join, under a
/// comment claiming it now matched the browser. It matched one of the three things the browser
/// did. Hence one implementation rather than a third correction.</para>
/// </summary>
public static class KillmailValuation
{
    /// <summary>
    /// Total ISK per killmail: every item destroyed or dropped, plus the victim's hull.
    ///
    /// <para>The hull comes in as a map rather than being queried here because every caller has
    /// already selected it — and a kill with no priced hull (a bare pod) should still total its
    /// implants rather than drop out.</para>
    /// </summary>
    /// <param name="hullByKill">Killmail id → the victim's ship type id.</param>
    public static async Task<Dictionary<int, double>> ValueKillsAsync(
        AppDbContext db,
        IReadOnlyDictionary<int, int> hullByKill,
        CancellationToken ct = default)
    {
        var result = new Dictionary<int, double>();
        if (hullByKill.Count == 0) return result;

        var settings = await db.MarketDefaultSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings?.AssetValueConfigId is not int configId) return result;
        var priceType = settings.AssetValuePriceType;

        var killIds = hullByKill.Keys.ToList();

        var items = await db.KillMailItems.AsNoTracking()
            .Where(i => killIds.Contains(i.KillMailId))
            .Select(i => new { i.KillMailId, i.ItemTypeId, i.QuantityDestroyed, i.QuantityDropped, i.Singleton })
            .ToListAsync(ct);

        var itemTypeIds  = items.Select(i => i.ItemTypeId).Distinct().ToList();
        var blueprintIds = await BlueprintTypeIdsAsync(db, itemTypeIds, ct);

        // Singleton == 2 is ESI's marker for a blueprint copy.
        var bpcTypeIds = items.Where(i => i.Singleton == 2 && blueprintIds.Contains(i.ItemTypeId))
            .Select(i => i.ItemTypeId).Distinct().ToList();
        var bpcPerRun  = await CheapestBpcPerRunAsync(db, bpcTypeIds, ct);

        var priceTypeIds = itemTypeIds.Concat(hullByKill.Values).Distinct().ToList();
        var marketPrices = await db.MarketItemPrices.AsNoTracking()
            .Where(p => p.ConfigId == configId && priceTypeIds.Contains(p.TypeId))
            .ToDictionaryAsync(p => p.TypeId, p => priceType switch
            {
                "Buy"  => p.BuyPrice,
                "Sell" => p.SellPrice,
                _      => p.Midpoint,
            }, ct);

        // ⚠️ Per item, not per type. The same blueprint type can appear on one kill as both an
        // original and a copy, so deciding once for the type would misprice one of them.
        foreach (var item in items)
        {
            var qty = (item.QuantityDestroyed ?? 0) + (item.QuantityDropped ?? 0);
            var unitPrice = item.Singleton == 2 && blueprintIds.Contains(item.ItemTypeId)
                ? bpcPerRun.GetValueOrDefault(item.ItemTypeId)
                : marketPrices.GetValueOrDefault(item.ItemTypeId);
            result[item.KillMailId] = result.GetValueOrDefault(item.KillMailId) + qty * unitPrice;
        }

        foreach (var (killId, hullTypeId) in hullByKill)
            result[killId] = result.GetValueOrDefault(killId) + marketPrices.GetValueOrDefault(hullTypeId);

        return result;
    }

    /// <summary>Which of <paramref name="typeIds"/> are blueprint types at all (BPO or BPC) — a
    /// killmail item is only a Blueprint Copy when it is both this AND Singleton == 2; the same
    /// underlying EVE item_type_id is used for a type's BPO and BPC alike, so singleton is what
    /// disambiguates a given line, not the type id.
    ///
    /// <para>SdeBlueprintProducts also carries "invention" rows where the TypeId key is a consumed
    /// INPUT MATERIAL, not a real blueprint you would hold — confirmed via the SDE for T3
    /// reverse-engineering relics ("Wrecked Armor Nanobot", "Intact Hull Section", etc.): every row
    /// for those is Activity=invention and nothing else, which is exactly how they were wrongly
    /// getting BPO icon and grouping treatment. A genuine blueprint always either has a
    /// non-invention (manufacturing/reaction) row too, or — for a handful of invention-only
    /// structure-rig blueprints — its name still ends in "Blueprint" even with no other activity
    /// row. Both conditions together correctly include real blueprints and reaction formulas and
    /// exclude relic materials.</para>
    /// </summary>
    public static async Task<HashSet<int>> BlueprintTypeIdsAsync(
        AppDbContext db, IReadOnlyCollection<int> typeIds, CancellationToken ct)
    {
        if (typeIds.Count == 0) return [];

        var candidates = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => typeIds.Contains(p.TypeId))
            .Select(p => new { p.TypeId, p.Activity })
            .Distinct()
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var candidateIds = candidates.Select(c => c.TypeId).Distinct().ToList();
        var namesById = await db.SdeTypes.AsNoTracking()
            .Where(t => candidateIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name })
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        return candidates
            .GroupBy(c => c.TypeId)
            .Where(g => g.Any(c => c.Activity != "invention")
                     || (namesById.TryGetValue(g.Key, out var name) && name.EndsWith("Blueprint", StringComparison.Ordinal)))
            .Select(g => g.Key)
            .ToHashSet();
    }

    /// <summary>Cheapest known per-run BPC contract price for each blueprint type, using the same
    /// best-vs-30-day-average rule (ContractPricing.EffectivePerRun) BuildCostService applies —
    /// the lowest across all ME rows, since a killmail item gives no runs or ME to pick a specific
    /// one. A type with no contract price on file is simply absent; callers must treat that as
    /// unpriced, never fall back to the BPO market price.</summary>
    public static async Task<Dictionary<int, double>> CheapestBpcPerRunAsync(
        AppDbContext db, IReadOnlyCollection<int> blueprintTypeIds, CancellationToken ct)
    {
        if (blueprintTypeIds.Count == 0) return [];

        var rows = await db.ContractBpcPrices.AsNoTracking()
            .Where(p => blueprintTypeIds.Contains(p.TypeId))
            .ToListAsync(ct);

        var result = new Dictionary<int, double>();
        foreach (var row in rows)
        {
            if (ContractPricing.EffectivePerRun(row) is not { } effective) continue;
            var value = (double)effective;
            if (!result.TryGetValue(row.TypeId, out var existing) || value < existing)
                result[row.TypeId] = value;
        }
        return result;
    }
}
