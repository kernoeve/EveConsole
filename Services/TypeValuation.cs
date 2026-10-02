using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// What a unit of each type is worth, the way the asset valuation prices it: the market at the
/// asset-value source and price type, contracts where the market has nothing, and a blueprint as a
/// copy. Shared by the worklist and the PI tools so a task and a colony value the same item alike.
/// </summary>
public static class TypeValuation
{
    public static async Task<Dictionary<int, double>> PricesAsync(AppDbContext db, IReadOnlyCollection<int> typeIds,
                                                                  CancellationToken ct = default)
    {
        var prices = new Dictionary<int, double>();
        if (typeIds.Count == 0) return prices;
        var ids = typeIds.Distinct().ToList();

        var market = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (market?.AssetValueConfigId is int configId)
            prices = (await db.MarketItemPrices.AsNoTracking()
                    .Where(p => p.ConfigId == configId && ids.Contains(p.TypeId))
                    .ToListAsync(ct))
                .ToDictionary(p => p.TypeId, p => market.AssetValuePriceType switch
                {
                    MarketPriceType.Buy  => p.BuyPrice,
                    MarketPriceType.Sell => p.SellPrice,
                    _                    => p.Midpoint,
                });

        // ⚠️ Contracts fill the gaps the market cannot. A blueprint has no market price at all —
        // it is bought and sold on contracts — so a buy task for one valued at nothing, and the
        // ISK column on those rows sat blank while the task itself was worth billions. Applied
        // only where the market had no price rather than in preference to it: the market is the
        // better number when it exists, and mixing the two per type would make the total depend on
        // which source happened to answer.
        var unpriced = ids.Where(id => !prices.ContainsKey(id) || prices[id] <= 0).ToList();
        if (unpriced.Count > 0)
            foreach (var cp in await db.ContractPrices.AsNoTracking()
                         .Where(c => unpriced.Contains(c.TypeId)).ToListAsync(ct))
                if (ContractPricing.EffectivePrice(cp) is { } effective && effective > 0)
                    prices[cp.TypeId] = (double)effective;

        // ⚠️ A blueprint is priced as a copy, and this overrides both sources above rather than
        // filling a gap they left. ContractPrices holds the whole-item price, which for a
        // blueprint type is the original — and an Avatar BPO is tens of billions against a copy
        // at a small fraction of that. Nobody with a task to acquire a titan print is buying the
        // original; they are buying a copy, which is what the task's own note already quotes.
        // Valuing the row off the BPO put a number on the list that no part of the plan matched.
        //
        // A type with no BPC contract price keeps whatever it had. That is deliberate: the
        // fallback would be the BPO price, which is the figure being corrected here.
        var bpTypeIds = await KillmailValuation.BlueprintTypeIdsAsync(db, ids, ct);
        if (bpTypeIds.Count > 0)
            foreach (var (typeId, perRun) in
                     await KillmailValuation.CheapestBpcPerRunAsync(db, bpTypeIds, ct))
                if (perRun > 0) prices[typeId] = perRun;

        return prices;
    }
}
