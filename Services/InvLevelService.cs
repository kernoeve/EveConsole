using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public record LocationOption(long Id, string Name);

public record InvTypeResult(int TypeId, string Name);

public record InvAvailability(long Assets, long IndustryJobs, long BuyOrders)
{
    public long Total => Assets + IndustryJobs + BuyOrders;
}

public record InvTypeMeta(string Name, double Volume, double? MarketPrice, double? BuildPrice);

public class InvLevelService(IDbContextFactory<AppDbContext> dbFactory)
{
    // ── Group CRUD ────────────────────────────────────────────────────────────

    // ── Collection CRUD ───────────────────────────────────────────────────────

    public async Task<List<InvLevelCollection>> LoadCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.InvLevelCollections.OrderBy(c => c.Name).ToListAsync(ct);
    }

    public async Task<InvLevelCollection> AddCollectionAsync(string name, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var c = new InvLevelCollection { Name = name };
        db.InvLevelCollections.Add(c);
        await db.SaveChangesAsync(ct);
        return c;
    }

    public async Task RenameCollectionAsync(int id, string name, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.InvLevelCollections.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Name, name), ct);
    }

    public async Task DeleteCollectionAsync(int id, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        // Orphan groups (move to Default / no collection)
        await db.InvLevelGroups.Where(g => g.CollectionId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.CollectionId, (int?)null), ct);
        await db.InvLevelCollections.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
    }

    // ── Group CRUD ────────────────────────────────────────────────────────────

    public async Task<List<InvLevelGroup>> LoadGroupsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.InvLevelGroups.OrderBy(g => g.Name).ToListAsync(ct);
    }

    public async Task<InvLevelGroup> AddGroupAsync(InvGroupDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var g = new InvLevelGroup
        {
            Name                   = r.Name,
            CollectionId           = r.CollectionId,
            Scope                  = r.Scope,
            LocationId             = r.LocationId,
            LocationName           = r.LocationName,
            Multiplier             = r.Multiplier,
            IncludeAssets          = r.IncludeAssets,
            IncludeIndustryJobs    = r.IncludeIndustryJobs,
            IncludeMarketBuyOrders = r.IncludeMarketBuyOrders,
            IncludeContractsBuying = r.IncludeContractsBuying,
        };
        db.InvLevelGroups.Add(g);
        await db.SaveChangesAsync(ct);
        return g;
    }

    public async Task UpdateGroupAsync(int groupId, InvGroupDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var g = await db.InvLevelGroups.FindAsync([groupId], ct);
        if (g is null) return;
        g.Name                   = r.Name;
        g.CollectionId           = r.CollectionId;
        g.Scope                  = r.Scope;
        g.LocationId             = r.LocationId;
        g.LocationName           = r.LocationName;
        g.Multiplier             = r.Multiplier;
        g.IncludeAssets          = r.IncludeAssets;
        g.IncludeIndustryJobs    = r.IncludeIndustryJobs;
        g.IncludeMarketBuyOrders = r.IncludeMarketBuyOrders;
        g.IncludeContractsBuying = r.IncludeContractsBuying;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteGroupAsync(int groupId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.InvLevelItems.Where(i => i.GroupId == groupId).ExecuteDeleteAsync(ct);
        await db.InvLevelGroups.Where(g => g.Id == groupId).ExecuteDeleteAsync(ct);
    }

    // ── Item CRUD ─────────────────────────────────────────────────────────────

    public async Task<List<InvLevelItem>> LoadItemsAsync(int groupId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.InvLevelItems.Where(i => i.GroupId == groupId).ToListAsync(ct);
    }

    public async Task<InvLevelItem?> AddItemAsync(int groupId, int typeId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        if (await db.InvLevelItems.AnyAsync(i => i.GroupId == groupId && i.TypeId == typeId, ct))
            return null;
        var item = new InvLevelItem { GroupId = groupId, TypeId = typeId, TargetQuantity = 1 };
        db.InvLevelItems.Add(item);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task UpdateItemTargetAsync(int itemId, int targetQty, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.InvLevelItems.Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.TargetQuantity, targetQty), ct);
    }

    public async Task DeleteItemAsync(int itemId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        await db.InvLevelItems.Where(i => i.Id == itemId).ExecuteDeleteAsync(ct);
    }

    // ── Item type search ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<InvTypeResult>> SearchTypesAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        await using var db = dbFactory.CreateDbContext();
        return await db.SdeTypes
            .Where(t => EF.Functions.Like(t.Name, $"%{text}%") && t.Published)
            .OrderBy(t => t.Name)
            .Take(40)
            .Select(t => new InvTypeResult(t.TypeId, t.Name))
            .ToListAsync(ct);
    }

    // ── Location search ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<LocationOption>> SearchLocationsAsync(
        string scope, string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        await using var db = dbFactory.CreateDbContext();

        if (scope == "Station")
        {
            // NPC stations from SDE
            var npc = await db.SdeStations
                .Where(s => EF.Functions.Like(s.Name, $"%{text}%"))
                .OrderBy(s => s.Name).Take(40)
                .Select(s => new LocationOption(s.StationId, s.Name))
                .ToListAsync(ct);

            // Player structures from ESI name cache
            var player = await db.EsiStructureNames
                .Where(s => EF.Functions.Like(s.Name, $"%{text}%"))
                .OrderBy(s => s.Name).Take(40)
                .Select(s => new LocationOption(s.StructureId, s.Name))
                .ToListAsync(ct);

            // Corp-owned structures
            var corp = await db.EsiCorpStructures
                .Where(s => EF.Functions.Like(s.Name, $"%{text}%"))
                .OrderBy(s => s.Name).Take(40)
                .Select(s => new LocationOption(s.StructureId, s.Name))
                .ToListAsync(ct);

            return npc
                .Concat(player)
                .Concat(corp)
                .GroupBy(l => l.Id)
                .Select(g => g.First())
                .OrderBy(l => l.Name)
                .Take(50)
                .ToList();
        }

        return scope switch
        {
            "System" => await db.SdeSolarSystems
                .Where(s => EF.Functions.Like(s.Name, $"%{text}%") && !s.IsWormhole)
                .OrderBy(s => s.Name).Take(40)
                .Select(s => new LocationOption(s.SolarSystemId, s.Name))
                .ToListAsync(ct),

            "Region" => await db.SdeRegions
                .Where(r => EF.Functions.Like(r.Name, $"%{text}%") && !r.IsWormhole)
                .OrderBy(r => r.Name).Take(40)
                .Select(r => new LocationOption(r.RegionId, r.Name))
                .ToListAsync(ct),

            _ => []
        };
    }

    // ── Scope resolution ──────────────────────────────────────────────────────

    // Resolve the set of location IDs a group's scope covers — NPC stations + player/corp
    // structures, plus the solar-system id itself so items floating in space (or in a ship in
    // space, e.g. a titan a character is logged off in) are included. Null = Everywhere.
    private static async Task<HashSet<long>?> ResolveScopeFilterAsync(
        AppDbContext db, InvLevelGroup group, CancellationToken ct)
    {
        if (group.Scope == "Station" && group.LocationId.HasValue)
            return [group.LocationId.Value];

        if (group.Scope == "System" && group.LocationId.HasValue)
        {
            int sysId = (int)group.LocationId.Value;
            var ids = new HashSet<long> { sysId };
            ids.UnionWith(await db.SdeStations
                .Where(s => s.SolarSystemId == sysId).Select(s => (long)s.StationId).ToListAsync(ct));
            ids.UnionWith(await db.EsiStructureNames
                .Where(s => s.SolarSystemId == sysId).Select(s => s.StructureId).ToListAsync(ct));
            ids.UnionWith(await db.EsiCorpStructures
                .Where(s => s.SystemId == sysId).Select(s => s.StructureId).ToListAsync(ct));
            return ids;
        }

        if (group.Scope == "Region" && group.LocationId.HasValue)
        {
            int regionId = (int)group.LocationId.Value;
            var sysIds = await db.SdeSolarSystems
                .Where(s => s.RegionId == regionId).Select(s => s.SolarSystemId).ToListAsync(ct);
            var ids = new HashSet<long>(sysIds.Select(s => (long)s));
            ids.UnionWith(await db.SdeStations
                .Where(s => sysIds.Contains(s.SolarSystemId)).Select(s => (long)s.StationId).ToListAsync(ct));
            ids.UnionWith(await db.EsiStructureNames
                .Where(s => sysIds.Contains(s.SolarSystemId)).Select(s => s.StructureId).ToListAsync(ct));
            ids.UnionWith(await db.EsiCorpStructures
                .Where(s => sysIds.Contains(s.SystemId)).Select(s => s.StructureId).ToListAsync(ct));
            return ids;
        }

        return null; // Everywhere
    }

    // Earliest active-job completion (EndDate) per product type, scoped like LoadAvailableAsync's
    // "in build" — so the completion date lines up with the in-build count.
    public async Task<Dictionary<int, DateTimeOffset>> LoadEarliestJobEndAsync(
        InvLevelGroup group, IReadOnlyList<int> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return [];
        await using var db = dbFactory.CreateDbContext();
        var stationFilter = await ResolveScopeFilterAsync(db, group, ct);

        var q = db.EsiIndustryJobs
            .Where(j => (j.ActivityId == 1 || j.ActivityId == 9 || j.ActivityId == 11)
                     && j.Status == "active"
                     && j.ProductTypeId.HasValue
                     && typeIds.Contains(j.ProductTypeId!.Value));
        if (stationFilter != null)
            q = q.Where(j => stationFilter.Contains(j.FacilityId));

        var rows = await q
            .Select(j => new { Type = j.ProductTypeId!.Value, j.EndDate })
            .ToListAsync(ct);

        return rows.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Min(r => r.EndDate));
    }

    // ── Availability aggregation ──────────────────────────────────────────────

    public async Task<Dictionary<int, InvAvailability>> LoadAvailableAsync(
        InvLevelGroup group, IReadOnlyList<int> typeIds, CancellationToken ct = default,
        bool packagedOnly = false)
    {
        if (typeIds.Count == 0) return [];
        await using var db = dbFactory.CreateDbContext();

        var stationFilter = await ResolveScopeFilterAsync(db, group, ct);

        var assets  = new Dictionary<int, long>();
        var jobs    = new Dictionary<int, long>();
        var orders  = new Dictionary<int, long>();

        // Assets
        if (group.IncludeAssets)
        {
            var q = db.EsiAssets.Where(a => typeIds.Contains(a.TypeId));
            if (stationFilter != null)
                q = q.Where(a => stationFilter.Contains(a.RootLocationId));
            // Only packaged (non-singleton) items — skip assembled/fitted hulls.
            if (packagedOnly)
                q = q.Where(a => !a.IsSingleton);

            var totals = await q.GroupBy(a => a.TypeId)
                .Select(g => new { TypeId = g.Key, Total = g.Sum(a => (long)a.Quantity) })
                .ToListAsync(ct);
            foreach (var t in totals) assets[t.TypeId] = t.Total;
        }

        // Industry Jobs — active manufacturing (1) and reactions (9, plus legacy 11).
        // Count the UNITS that will be produced = Runs × output-per-run, so the total is
        // consistent with asset quantities. Reactions and multi-output blueprints (e.g.
        // capital components, ammo) produce many units per run, so counting runs alone
        // undercounts — and reactions were previously excluded entirely.
        if (group.IncludeIndustryJobs)
        {
            var q = db.EsiIndustryJobs
                .Where(j => (j.ActivityId == 1 || j.ActivityId == 9 || j.ActivityId == 11)
                         && j.Status == "active"
                         && j.ProductTypeId.HasValue
                         && typeIds.Contains(j.ProductTypeId!.Value));
            // Scope by FacilityId (the structure the job runs in), NOT OutputLocationId —
            // the latter is the delivery hangar/container sub-location, which does not
            // resolve to a structure and would drop every job from location-scoped groups.
            if (stationFilter != null)
                q = q.Where(j => stationFilter.Contains(j.FacilityId));

            var activeJobs = await q
                .Select(j => new { j.BlueprintTypeId, ProductTypeId = j.ProductTypeId!.Value, j.Runs })
                .ToListAsync(ct);

            if (activeJobs.Count > 0)
            {
                // Output units per run, keyed by (blueprint, product). Use the job's own
                // blueprint so the count matches exactly what that job will deliver.
                var bpIds = activeJobs.Select(j => j.BlueprintTypeId).Distinct().ToList();
                var qtyMap = (await db.SdeBlueprintProducts.AsNoTracking()
                        .Where(p => bpIds.Contains(p.TypeId)
                                 && (p.Activity == "manufacturing" || p.Activity == "reaction"))
                        .Select(p => new { p.TypeId, p.ProductTypeId, p.Quantity })
                        .ToListAsync(ct))
                    .GroupBy(p => (p.TypeId, p.ProductTypeId))
                    .ToDictionary(g => g.Key, g => g.First().Quantity);

                foreach (var j in activeJobs)
                {
                    long perRun = qtyMap.TryGetValue((j.BlueprintTypeId, j.ProductTypeId), out var qy)
                        ? Math.Max(1, qy) : 1;
                    long units = (long)j.Runs * perRun;
                    jobs[j.ProductTypeId] = jobs.TryGetValue(j.ProductTypeId, out var cur) ? cur + units : units;
                }
            }
        }

        // Market Buy Orders — active, not historical
        if (group.IncludeMarketBuyOrders)
        {
            var q = db.EsiMarketOrders
                .Where(o => o.IsBuyOrder && !o.IsHistory && typeIds.Contains(o.TypeId));
            if (stationFilter != null)
                q = q.Where(o => stationFilter.Contains(o.LocationId));

            var totals = await q.GroupBy(o => o.TypeId)
                .Select(g => new { TypeId = g.Key, Total = g.Sum(o => (long)o.VolumeRemain) })
                .ToListAsync(ct);
            foreach (var t in totals) orders[t.TypeId] = t.Total;
        }

        return typeIds.Distinct().ToDictionary(
            id => id,
            id => new InvAvailability(
                assets.GetValueOrDefault(id),
                jobs.GetValueOrDefault(id),
                orders.GetValueOrDefault(id)));
    }

    // ── Type metadata lookup ──────────────────────────────────────────────────

    public async Task<Dictionary<int, InvTypeMeta>> GetTypeMetaAsync(
        IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var ids = typeIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        await using var db = dbFactory.CreateDbContext();

        var types = await db.SdeTypes
            .Where(t => ids.Contains(t.TypeId))
            .Select(t => new { t.TypeId, t.Name, t.Volume })
            .ToListAsync(ct);

        var buildCosts = await db.BuildCosts
            .Where(b => ids.Contains(b.TypeId))
            .ToDictionaryAsync(b => b.TypeId, b => (double)b.TotalCost, ct);

        // Market value follows the configured Default Pricing (Asset Value) source, which
        // already gap-fills missing prices with build cost × markup. Fall back to that same
        // build-cost markup in code, then to the ESI average, so the column is never blank
        // just because ESI has no average price for an item (e.g. low-volume faction hulls).
        var defaults    = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        int?   cfgId    = defaults?.AssetValueConfigId;
        string priceKind = defaults?.AssetValuePriceType ?? MarketPriceType.Sell;
        double markup   = 1.0 + (double)(defaults?.MissingPriceMarkupPct ?? 0) / 100.0;

        var configPrices = new Dictionary<int, double>();
        if (cfgId.HasValue)
        {
            var rows = await db.MarketItemPrices.AsNoTracking()
                .Where(p => ids.Contains(p.TypeId) && p.ConfigId == cfgId.Value)
                .ToListAsync(ct);
            foreach (var p in rows)
                configPrices[p.TypeId] = priceKind switch
                {
                    MarketPriceType.Buy      => p.BuyPrice,
                    MarketPriceType.Midpoint => p.Midpoint,
                    _                        => p.SellPrice,
                };
        }

        var avgPrices = await db.EsiAdjustedPrices
            .Where(p => ids.Contains(p.TypeId))
            .ToDictionaryAsync(p => p.TypeId, p => p.AveragePrice, ct);

        double? MarketValue(int typeId)
        {
            if (configPrices.TryGetValue(typeId, out var cp) && cp > 0) return cp;
            if (buildCosts.TryGetValue(typeId, out var bc) && bc > 0)   return bc * markup;
            if (avgPrices.TryGetValue(typeId, out var ap) && ap > 0)    return ap;
            return null;
        }

        return types.ToDictionary(
            t => t.TypeId,
            t => new InvTypeMeta(
                t.Name,
                t.Volume,
                MarketValue(t.TypeId),
                buildCosts.TryGetValue(t.TypeId, out var bc) && bc > 0 ? bc : null));
    }

    public async Task<Dictionary<int, string>> GetTypeNamesAsync(
        IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var meta = await GetTypeMetaAsync(typeIds, ct);
        return meta.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
    }
}
