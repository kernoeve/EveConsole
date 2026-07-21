using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveConsole.Services;

public class ReprocessingValueService(
    IServiceScopeFactory scopeFactory,
    AppErrorLogger       errorLogger)
{
    private const int OreIceCategoryId = 25;   // Asteroid (ore, ice, moon ore)

    // Max efficiency: Tatara + T2 rig + nullsec + Reprocessing/Efficiency/Ore V + RX-804 implant
    private const double OreIceYield  = 0.9063;

    // NPC base 50% × Scrapmetal Processing V (×1.10)
    private const double GenItemYield = 0.55;

    public async Task RecalculateAllAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var settings = await db.MarketDefaultSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == 1, ct);
            if (settings?.AssetValueConfigId is not int configId) return;

            var priceType = settings.AssetValuePriceType;
            var rawPrices = await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == configId)
                .ToListAsync(ct);
            var priceMap = rawPrices.ToDictionary(p => p.TypeId, p => priceType switch
            {
                MarketPriceType.Buy  => p.BuyPrice,
                MarketPriceType.Sell => p.SellPrice,
                _                   => p.Midpoint,
            });

            var allMaterials = await db.SdeTypeMaterials.AsNoTracking().ToListAsync(ct);
            var inputTypeIds = allMaterials.Select(m => m.TypeId).Distinct().ToList();

            var typeData = await db.SdeTypes.AsNoTracking()
                .Where(t => inputTypeIds.Contains(t.TypeId))
                .Select(t => new { t.TypeId, t.PortionSize, t.GroupId })
                .ToListAsync(ct);

            var portionSizes = typeData.ToDictionary(t => t.TypeId, t => (double)t.PortionSize);
            var groupIds     = typeData.ToDictionary(t => t.TypeId, t => t.GroupId);

            var allGroupIds   = groupIds.Values.Distinct().ToList();
            var groupCategory = await db.SdeGroups.AsNoTracking()
                .Where(g => allGroupIds.Contains(g.GroupId))
                .ToDictionaryAsync(g => g.GroupId, g => g.CategoryId, ct);

            var newValues = new List<ReprocessingItemValue>();
            foreach (var group in allMaterials.GroupBy(m => m.TypeId))
            {
                var typeId    = group.Key;
                var portionSz = portionSizes.TryGetValue(typeId, out var ps) ? ps : 1.0;

                var grpId  = groupIds.TryGetValue(typeId, out var gid) ? gid : 0;
                var catId  = groupCategory.TryGetValue(grpId, out var cid) ? cid : 0;
                double yield = catId == OreIceCategoryId ? OreIceYield : GenItemYield;

                double perUnit = 0;
                foreach (var mat in group)
                {
                    if (!priceMap.TryGetValue(mat.MaterialTypeId, out var p) || p <= 0) continue;
                    perUnit += (mat.Quantity / portionSz) * p * yield;
                }
                if (perUnit > 0)
                    newValues.Add(new ReprocessingItemValue { TypeId = typeId, Value = perUnit });
            }

            await db.ReprocessingItemValues.ExecuteDeleteAsync(ct);
            db.ReprocessingItemValues.AddRange(newValues);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { errorLogger.Log("ReprocessingValueService", "RecalculateAllAsync", ex); }
    }
}
