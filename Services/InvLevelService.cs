using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

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

    // ── Availability aggregation ──────────────────────────────────────────────

    public async Task<Dictionary<int, InvAvailability>> LoadAvailableAsync(
        InvLevelGroup group, IReadOnlyList<int> typeIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0) return [];
        await using var db = dbFactory.CreateDbContext();

        // Resolve all location IDs (NPC stations + player structures) for the scope.
        HashSet<long>? stationFilter = null;
        if (group.Scope == "Station" && group.LocationId.HasValue)
        {
            stationFilter = [group.LocationId.Value];
        }
        else if (group.Scope == "System" && group.LocationId.HasValue)
        {
            int sysId = (int)group.LocationId.Value;
            var ids = new HashSet<long>();

            ids.UnionWith(await db.SdeStations
                .Where(s => s.SolarSystemId == sysId)
                .Select(s => (long)s.StationId)
                .ToListAsync(ct));
            ids.UnionWith(await db.EsiStructureNames
                .Where(s => s.SolarSystemId == sysId)
                .Select(s => s.StructureId)
                .ToListAsync(ct));
            ids.UnionWith(await db.EsiCorpStructures
                .Where(s => s.SystemId == sysId)
                .Select(s => s.StructureId)
                .ToListAsync(ct));

            stationFilter = ids;
        }
        else if (group.Scope == "Region" && group.LocationId.HasValue)
        {
            int regionId = (int)group.LocationId.Value;
            var sysIds = await db.SdeSolarSystems
                .Where(s => s.RegionId == regionId)
                .Select(s => s.SolarSystemId)
                .ToListAsync(ct);

            var ids = new HashSet<long>();
            ids.UnionWith(await db.SdeStations
                .Where(s => sysIds.Contains(s.SolarSystemId))
                .Select(s => (long)s.StationId)
                .ToListAsync(ct));
            ids.UnionWith(await db.EsiStructureNames
                .Where(s => sysIds.Contains(s.SolarSystemId))
                .Select(s => s.StructureId)
                .ToListAsync(ct));
            ids.UnionWith(await db.EsiCorpStructures
                .Where(s => sysIds.Contains(s.SystemId))
                .Select(s => s.StructureId)
                .ToListAsync(ct));

            stationFilter = ids;
        }

        var assets  = new Dictionary<int, long>();
        var jobs    = new Dictionary<int, long>();
        var orders  = new Dictionary<int, long>();

        // Assets
        if (group.IncludeAssets)
        {
            var q = db.EsiAssets.Where(a => typeIds.Contains(a.TypeId));
            if (stationFilter != null)
                q = q.Where(a => stationFilter.Contains(a.RootLocationId));

            var totals = await q.GroupBy(a => a.TypeId)
                .Select(g => new { TypeId = g.Key, Total = g.Sum(a => (long)a.Quantity) })
                .ToListAsync(ct);
            foreach (var t in totals) assets[t.TypeId] = t.Total;
        }

        // Industry Jobs — active manufacturing (ActivityId = 1)
        if (group.IncludeIndustryJobs)
        {
            var q = db.EsiIndustryJobs
                .Where(j => j.ActivityId == 1
                         && j.Status == "active"
                         && j.ProductTypeId.HasValue
                         && typeIds.Contains(j.ProductTypeId!.Value));
            if (stationFilter != null)
                q = q.Where(j => stationFilter.Contains(j.OutputLocationId));

            var totals = await q.GroupBy(j => j.ProductTypeId!.Value)
                .Select(g => new { TypeId = g.Key, Total = g.Sum(j => (long)j.Runs) })
                .ToListAsync(ct);
            foreach (var t in totals) jobs[t.TypeId] = t.Total;
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

        var prices = await db.EsiAdjustedPrices
            .Where(p => ids.Contains(p.TypeId))
            .ToDictionaryAsync(p => p.TypeId, p => p.AveragePrice, ct);

        var buildCosts = await db.BuildCosts
            .Where(b => ids.Contains(b.TypeId))
            .ToDictionaryAsync(b => b.TypeId, b => (double)b.TotalCost, ct);

        return types.ToDictionary(
            t => t.TypeId,
            t => new InvTypeMeta(
                t.Name,
                t.Volume,
                prices.TryGetValue(t.TypeId, out var p) && p > 0 ? p : null,
                buildCosts.TryGetValue(t.TypeId, out var bc) && bc > 0 ? bc : null));
    }

    public async Task<Dictionary<int, string>> GetTypeNamesAsync(
        IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var meta = await GetTypeMetaAsync(typeIds, ct);
        return meta.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
    }
}
