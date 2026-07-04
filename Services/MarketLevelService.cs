using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

public sealed record MarketLevelRowData(
    int     TypeId,
    string  TypeName,
    int     TargetQuantity,
    int     AvailableUnits,
    double? MarketPrice,
    double? StationMin,
    double? StationAvg,
    double? StationMax,
    double? BuildPrice = null,
    double  Volume     = 0
);

public sealed record MarketLevelGroupResult(
    IReadOnlyList<MarketLevelRowData> Rows,
    DateTimeOffset?                   DataFetchedAt
);

public sealed record MarketLevelStation(long Id, string Name, string Kind);

public class MarketLevelService(IDbContextFactory<AppDbContext> dbFactory)
{
    // ── Collection CRUD ───────────────────────────────────────────────────────

    public async Task<List<MarketLevelCollection>> GetCollectionsAsync()
    {
        using var db = dbFactory.CreateDbContext();
        return await db.MarketLevelCollections.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<MarketLevelCollection> AddCollectionAsync(string name)
    {
        using var db = dbFactory.CreateDbContext();
        var c = new MarketLevelCollection { Name = name };
        db.MarketLevelCollections.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    public async Task RenameCollectionAsync(int id, string name)
    {
        using var db = dbFactory.CreateDbContext();
        await db.MarketLevelCollections.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, name));
    }

    public async Task DeleteCollectionAsync(int id)
    {
        using var db = dbFactory.CreateDbContext();
        await db.MarketLevelGroups.Where(g => g.CollectionId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.CollectionId, (int?)null));
        await db.MarketLevelCollections.Where(c => c.Id == id).ExecuteDeleteAsync();
    }

    // ── Group CRUD ─────────────────────────────────────────────────────────────

    public async Task<List<MarketLevelGroup>> GetGroupsAsync()
    {
        using var db = dbFactory.CreateDbContext();
        return await db.MarketLevelGroups.OrderBy(g => g.Name).ToListAsync();
    }

    public async Task<MarketLevelGroup> SaveGroupAsync(MarketLevelGroup group)
    {
        using var db = dbFactory.CreateDbContext();
        if (group.Id == 0) db.MarketLevelGroups.Add(group);
        else                db.MarketLevelGroups.Update(group);
        await db.SaveChangesAsync();
        return group;
    }

    public async Task DeleteGroupAsync(int groupId)
    {
        using var db = dbFactory.CreateDbContext();
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "MarketLevelItems"  WHERE "GroupId" = {groupId}""");
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "MarketLevelGroups" WHERE "Id"      = {groupId}""");
    }

    public async Task<List<MarketLevelItem>> GetItemsAsync(int groupId)
    {
        using var db = dbFactory.CreateDbContext();
        return await db.MarketLevelItems.Where(i => i.GroupId == groupId).ToListAsync();
    }

    public async Task<MarketLevelItem> SaveItemAsync(MarketLevelItem item)
    {
        using var db = dbFactory.CreateDbContext();
        if (item.Id == 0) db.MarketLevelItems.Add(item);
        else               db.MarketLevelItems.Update(item);
        await db.SaveChangesAsync();
        return item;
    }

    public async Task DeleteItemAsync(int itemId)
    {
        using var db = dbFactory.CreateDbContext();
        await db.Database.ExecuteSqlAsync(
            $"""DELETE FROM "MarketLevelItems" WHERE "Id" = {itemId}""");
    }

    // ── Station picker — only stations with cached sell orders ────────────────

    public async Task<List<MarketLevelStation>> GetAvailableStationsAsync(CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var locationIds = await db.MarketRawOrders
            .Where(o => !o.IsBuyOrder)
            .Select(o => o.LocationId)
            .Distinct()
            .ToListAsync(ct);

        if (locationIds.Count == 0) return [];

        var result = new List<MarketLevelStation>();

        // NPC stations (< 1 billion): names from SDE
        var npcIds = locationIds.Where(id => id < 1_000_000_000L).Select(id => (int)id).ToList();
        if (npcIds.Count > 0)
        {
            var stations = await db.SdeStations
                .Where(s => npcIds.Contains(s.StationId))
                .OrderBy(s => s.Name)
                .ToListAsync(ct);
            result.AddRange(stations.Select(s => new MarketLevelStation(s.StationId, s.Name, "Station")));
        }

        // Player structures (≥ 1 billion): names from EsiStructureNames cache,
        // with fallback to MarketPricingConfig.LocationName
        var structIds = locationIds.Where(id => id >= 1_000_000_000L).ToList();
        if (structIds.Count > 0)
        {
            var structNames = await db.EsiStructureNames
                .Where(s => structIds.Contains(s.StructureId))
                .ToDictionaryAsync(s => s.StructureId, s => s.Name, ct);

            var configNames = await db.MarketPricingConfigs
                .Where(c => structIds.Contains(c.LocationId))
                .ToDictionaryAsync(c => c.LocationId, c => c.LocationName, ct);

            foreach (var sid in structIds.OrderBy(id => id))
            {
                string name = structNames.TryGetValue(sid, out var sn) ? sn
                            : configNames.TryGetValue(sid, out var cn) ? cn
                            : $"Structure {sid}";
                result.Add(new MarketLevelStation(sid, name, "Structure"));
            }
        }

        return result.OrderBy(s => s.Name).ToList();
    }

    // ── Data load from cached orders ──────────────────────────────────────────

    public async Task<MarketLevelGroupResult> LoadGroupDataAsync(
        MarketLevelGroup group, CancellationToken ct = default)
    {
        using var db = dbFactory.CreateDbContext();

        var items = await db.MarketLevelItems
            .Where(i => i.GroupId == group.Id)
            .ToListAsync(ct);

        if (items.Count == 0)
            return new MarketLevelGroupResult([], null);

        var typeIds = items.Select(i => i.TypeId).ToHashSet();

        // Resolve type names and volumes
        var typeData = await db.SdeTypes
            .Where(t => typeIds.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name, t.Volume })
            .ToListAsync(ct);
        var typeNames   = typeData.ToDictionary(t => t.TypeId, t => t.Name);
        var typeVolumes = typeData.ToDictionary(t => t.TypeId, t => t.Volume);

        // Resolve market prices from cache
        var marketPrices = await ResolveMarketPricesAsync(db, group.MarketSourceId, typeIds, ct);

        // Pull cached sell orders at this station for these types
        var allOrders = await db.MarketRawOrders
            .Where(o => o.LocationId == group.StationId
                     && !o.IsBuyOrder
                     && typeIds.Contains(o.TypeId))
            .ToListAsync(ct);

        // Data freshness: most recent FetchedAt across all orders
        DateTimeOffset? fetchedAt = allOrders.Count > 0
            ? allOrders.Max(o => o.FetchedAt)
            : null;

        // Group orders by TypeId
        var ordersByType = allOrders.GroupBy(o => o.TypeId)
                                    .ToDictionary(g => g.Key, g => g.ToList());

        // Build costs (material cost per unit) — keyed by TypeId
        var buildCosts = await db.BuildCosts
            .Where(b => typeIds.Contains(b.TypeId))
            .ToDictionaryAsync(b => b.TypeId, b => (double)b.TotalCost, ct);

        var rows = new List<MarketLevelRowData>();
        foreach (var item in items)
        {
            var orders = ordersByType.GetValueOrDefault(item.TypeId, []);
            marketPrices.TryGetValue(item.TypeId, out var mktPrice);

            // Max-price filter
            if (group.MaxPriceOverPct.HasValue && mktPrice > 0)
            {
                double ceiling = mktPrice * (1.0 + group.MaxPriceOverPct.Value / 100.0);
                orders = [.. orders.Where(o => o.Price <= ceiling)];
            }

            int    available = orders.Sum(o => o.VolumeRemain);
            double? min      = orders.Count > 0 ? orders.Min(o => o.Price) : null;
            double? max      = orders.Count > 0 ? orders.Max(o => o.Price) : null;
            double? avg      = null;
            if (orders.Count > 0)
            {
                double totalValue = orders.Sum(o => (double)o.VolumeRemain * o.Price);
                double totalVol   = orders.Sum(o => o.VolumeRemain);
                if (totalVol > 0) avg = totalValue / totalVol;
            }

            double? buildPrice = buildCosts.TryGetValue(item.TypeId, out var bp) && bp > 0 ? bp : null;
            double  volume     = typeVolumes.GetValueOrDefault(item.TypeId);

            rows.Add(new MarketLevelRowData(
                item.TypeId,
                typeNames.GetValueOrDefault(item.TypeId, $"TypeId {item.TypeId}"),
                item.TargetQuantity,
                available,
                mktPrice > 0 ? mktPrice : null,
                min, avg, max, buildPrice, volume));
        }

        return new MarketLevelGroupResult(rows, fetchedAt);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<Dictionary<int, double>> ResolveMarketPricesAsync(
        AppDbContext db, int? marketSourceId, HashSet<int> typeIds, CancellationToken ct)
    {
        int? sourceId = marketSourceId;
        if (sourceId == null)
        {
            var def = await db.MarketDefaultSettings.FindAsync([1], ct);
            sourceId = def?.AssetValueConfigId;
        }
        if (sourceId == null) return [];

        var config = await db.MarketPricingConfigs.FindAsync([sourceId.Value], ct);
        if (config == null) return [];

        var priceRows = await db.MarketItemPrices
            .Where(p => p.ConfigId == sourceId.Value && typeIds.Contains(p.TypeId))
            .ToListAsync(ct);

        return priceRows.ToDictionary(
            p => p.TypeId,
            p => PickPrice(p, config.PriceType));
    }

    private static double PickPrice(MarketItemPrice p, string priceType) => priceType switch
    {
        MarketPriceType.Buy  => p.BuyPrice  > 0 ? p.BuyPrice  : p.Midpoint,
        MarketPriceType.Sell => p.SellPrice > 0 ? p.SellPrice : p.Midpoint,
        _                    => p.Midpoint  > 0 ? p.Midpoint  : p.SellPrice,
    };
}
