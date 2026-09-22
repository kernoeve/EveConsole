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

public record InvAvailability(long Assets, long IndustryJobs, long BuyOrders, long Contracts = 0)
{
    public long Total => Assets + IndustryJobs + BuyOrders + Contracts;
}

/// <param name="IsBlueprint">⚠️ Which image the icon comes from. EVE's image server serves a
/// blueprint under /bp and everything else under /icon, and asking for the wrong one gets a
/// blank rather than a fallback.</param>
public record InvTypeMeta(
    string Name, double Volume, double? MarketPrice, double? BuildPrice, bool IsBlueprint = false);

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

    /// <summary>
    /// Everything the dialog decides, copied onto a group.
    ///
    /// <para>⚠️ ONE mapping, deliberately. There were three — add, update, and a literal in the
    /// view model that rebuilt the row after an edit — and a field missing from the third silently
    /// reverted the other two. The row kept the stale value, and the Multiplier setter then
    /// re-saved the whole group FROM THE ROW, writing it back over what had just been stored.
    /// Packaged-only never saved for exactly that reason, and scope had the same bug before it.
    /// A new flag added here reaches all three.</para>
    /// </summary>
    public static void ApplyTo(InvLevelGroup g, InvGroupDialogResult r)
    {
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
        g.PackagedOnly           = r.PackagedOnly;
    }

    public async Task<InvLevelGroup> AddGroupAsync(InvGroupDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var g = new InvLevelGroup();
        ApplyTo(g, r);
        db.InvLevelGroups.Add(g);
        await db.SaveChangesAsync(ct);
        return g;
    }

    public async Task UpdateGroupAsync(int groupId, InvGroupDialogResult r, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var g = await db.InvLevelGroups.FindAsync([groupId], ct);
        if (g is null) return;
        ApplyTo(g, r);
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

    /// <summary>
    /// Adds an item to a group at the target the caller asked for.
    ///
    /// <para>The target used to be hardcoded to 1, so the quantity typed into the add dialog was
    /// collected, passed along and then discarded — every item landed at 1 whatever was entered.
    /// Callers with no opinion still get 1 from the default.</para>
    /// </summary>
    public async Task<InvLevelItem?> AddItemAsync(
        int groupId, int typeId, int targetQty = 1, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        if (await db.InvLevelItems.AnyAsync(i => i.GroupId == groupId && i.TypeId == typeId, ct))
            return null;
        var item = new InvLevelItem
        {
            GroupId = groupId, TypeId = typeId, TargetQuantity = Math.Max(1, targetQty),
        };
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
    private static Task<HashSet<long>?> ResolveScopeFilterAsync(
        AppDbContext db, InvLevelGroup group, CancellationToken ct)
        => ResolveScopeFilterAsync(db, group.Scope, group.LocationId, ct);

    /// <summary>
    /// Every location id that counts as inside a scope, or null for Everywhere.
    ///
    /// <para>Shared rather than duplicated: "which structures are in this region" is the kind of
    /// question two callers can easily answer differently — one remembering player structures and
    /// the other not — and then disagree about whether the same pile of material exists.</para>
    /// </summary>
    public static async Task<HashSet<long>?> ResolveScopeFilterAsync(
        AppDbContext db, string scope, long? locationId, CancellationToken ct)
    {
        // A region resolves through five queries, and inside a worklist build the same scope is
        // resolved by five or six generators. Once per build, per (scope, location) — and handed
        // out as a COPY, because callers union their own extra stations onto the set they get.
        var resolved = await Worklist.BuildCache.GetOrAddAsync(
            $"ScopeFilter:{scope}:{locationId}", () => ResolveScopeFilterUncachedAsync(db, scope, locationId, ct));
        return resolved is null ? null : new HashSet<long>(resolved);
    }

    private static async Task<HashSet<long>?> ResolveScopeFilterUncachedAsync(
        AppDbContext db, string scope, long? locationId, CancellationToken ct)
    {
        if (scope == "Station" && locationId.HasValue)
            return [locationId.Value];

        if (scope == "System" && locationId.HasValue)
        {
            int sysId = (int)locationId.Value;
            var ids = new HashSet<long> { sysId };
            ids.UnionWith(await db.SdeStations
                .Where(s => s.SolarSystemId == sysId).Select(s => (long)s.StationId).ToListAsync(ct));
            // ⚠️ The same three structure sources AssetLocations resolves an asset's system from,
            // or a scope and the assets in it disagree about where a structure is: one the
            // Structure Browser described by hand was in no scope at all.
            ids.UnionWith(await db.Structures
                .Where(s => s.SolarSystemId == sysId).Select(s => s.StructureId).ToListAsync(ct));
            ids.UnionWith(await db.EsiStructureNames
                .Where(s => s.SolarSystemId == sysId).Select(s => s.StructureId).ToListAsync(ct));
            ids.UnionWith(await db.EsiCorpStructures
                .Where(s => s.SystemId == sysId).Select(s => s.StructureId).ToListAsync(ct));
            return ids;
        }

        if (scope == "Region" && locationId.HasValue)
        {
            int regionId = (int)locationId.Value;
            var sysIds = await db.SdeSolarSystems
                .Where(s => s.RegionId == regionId).Select(s => s.SolarSystemId).ToListAsync(ct);
            var ids = new HashSet<long>(sysIds.Select(s => (long)s));
            ids.UnionWith(await db.SdeStations
                .Where(s => sysIds.Contains(s.SolarSystemId)).Select(s => (long)s.StationId).ToListAsync(ct));
            ids.UnionWith(await db.Structures
                .Where(s => sysIds.Contains(s.SolarSystemId)).Select(s => s.StructureId).ToListAsync(ct));
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

    /// <summary>
    /// The owners this tool speaks for: characters we hold a token for, and the corporations the
    /// user has marked personal on the Characters tool.
    ///
    /// <para>⚠️ Required, not a refinement. A director's token pulls the WHOLE corporation's
    /// assets, jobs and orders, and a member corp's hangars are not stock the user can build
    /// from or sell. Measured here: of the fuel blocks on hand, 849,225 units belonged to a corp
    /// the user is merely a member of against 397,025 that were actually theirs — so a stockpile
    /// read as comfortably supplied was in fact three times smaller than it looked.</para>
    ///
    /// <para>Same rule the Order Tracker's fulfilment matching and the Worklist's industry
    /// assignment already use, so the tools agree about what "we have" means.</para>
    /// </summary>
    private static Task<HashSet<long>> OwnedIdsAsync(AppDbContext db, CancellationToken ct)
        => Worklist.BuildCache.GetOrAddAsync("InvLevel.OwnedIds", () => OwnedIdsUncachedAsync(db, ct));

    private static async Task<HashSet<long>> OwnedIdsUncachedAsync(AppDbContext db, CancellationToken ct)
    {
        var ids = await db.Characters.AsNoTracking()
            .Where(c => c.RefreshToken != "").Select(c => c.Id).ToListAsync(ct);

        // Corporation ids are int on the entity and long on every table that references one.
        ids.AddRange(await db.Corporations.AsNoTracking()
            .Where(c => c.IsPersonal).Select(c => (long)c.Id).ToListAsync(ct));

        return ids.ToHashSet();
    }

    /// <summary>
    /// Which of these types are blueprints.
    ///
    /// <para>Asked as "does anything have a blueprint activity" rather than by category, because
    /// that is the same table every other blueprint decision in the app is made from.</para>
    /// </summary>
    private static async Task<HashSet<int>> BlueprintTypeIdsAsync(
        AppDbContext db, IReadOnlyList<int> typeIds, CancellationToken ct) =>
        [.. await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => typeIds.Contains(p.TypeId))
            .Select(p => p.TypeId)
            .Distinct()
            .ToListAsync(ct)];

    public async Task<Dictionary<int, InvAvailability>> LoadAvailableAsync(
        InvLevelGroup group, IReadOnlyList<int> typeIds, CancellationToken ct = default,
        bool packagedOnly = false)
    {
        if (typeIds.Count == 0) return [];
        var all = await LoadAvailableAsync([(group, typeIds)], ct, packagedOnly);
        return all.GetValueOrDefault(group.Id) ?? [];
    }

    /// <summary>
    /// Availability for several groups at once, keyed by group id.
    /// </summary>
    /// <remarks>
    /// ⚠️ This is the shape the worklist must call. The single-group form above was called once
    /// per inventory rule from inside the demand gatherer, and each call is ten or so queries —
    /// owner ids, scope, assets, runs, delivery lag, jobs, orders, contracts — of which only the
    /// scope depends on the group. With twenty-eight rules that was close to three hundred round
    /// trips per generator, and three generators gather demand: measured at 180 ms a round trip
    /// over a remote link, minutes of the worklist's load were this method being asked the same
    /// questions twenty-eight times.
    ///
    /// <para>Every row is now loaded once for the union of every group's types, and each group's
    /// filters — its types, its scope, its Include flags, its packaging — are applied in memory,
    /// exactly as the per-group queries applied them. The single-group form is this with one
    /// request, so there is one implementation and the two cannot drift.</para>
    ///
    /// <para>The scope is resolved once per distinct (scope, location) rather than per group,
    /// since rules routinely share one.</para>
    /// </remarks>
    public Task<Dictionary<int, Dictionary<int, InvAvailability>>> LoadAvailableAsync(
        IReadOnlyList<(InvLevelGroup Group, IReadOnlyList<int> TypeIds)> requests,
        CancellationToken ct = default, bool packagedOnly = false)
    {
        // Three generators gather the same demand from the same rules, so they ask this with
        // the same groups and the same types. Keyed by exactly that, so a different request —
        // the inventory tool asking for one group — is its own load. Results are records the
        // callers only read.
        var key = "InvLevel.Available:" + (packagedOnly ? "P:" : "A:")
                + string.Join("|", requests.OrderBy(r => r.Group.Id)
                      .Select(r => $"{r.Group.Id}=" + string.Join(",", r.TypeIds.Distinct().OrderBy(t => t))));
        return Worklist.BuildCache.GetOrAddAsync(key, () => LoadAvailableUncachedAsync(requests, ct, packagedOnly));
    }

    private async Task<Dictionary<int, Dictionary<int, InvAvailability>>> LoadAvailableUncachedAsync(
        IReadOnlyList<(InvLevelGroup Group, IReadOnlyList<int> TypeIds)> requests,
        CancellationToken ct, bool packagedOnly)
    {
        var result = new Dictionary<int, Dictionary<int, InvAvailability>>();
        requests = requests.Where(r => r.TypeIds.Count > 0).ToList();
        if (requests.Count == 0) return result;

        await using var db = dbFactory.CreateDbContext();

        var allTypes    = requests.SelectMany(r => r.TypeIds).Distinct().ToList();
        var ownerFilter = await OwnedIdsAsync(db, ct);

        var anyAssets    = requests.Any(r => r.Group.IncludeAssets);
        var anyJobs      = requests.Any(r => r.Group.IncludeIndustryJobs);
        var anyOrders    = requests.Any(r => r.Group.IncludeMarketBuyOrders);
        var anyContracts = requests.Any(r => r.Group.IncludeContractsBuying);

        // ── Scope, once per distinct scope rather than per group ────────────
        var scopeFilters = new Dictionary<(string, long?), HashSet<long>?>();
        foreach (var (group, _) in requests)
        {
            var key = (group.Scope, group.LocationId);
            if (!scopeFilters.ContainsKey(key))
                scopeFilters[key] = await ResolveScopeFilterAsync(db, group, ct);
        }

        // ── Assets: every row for the union, filtered per group below ───────
        var bpTypeIds = anyAssets ? await BlueprintTypeIdsAsync(db, allTypes, ct) : [];

        List<(long ItemId, int TypeId, int Quantity, long RootLocationId, bool IsSingleton)> assetRows = [];
        Dictionary<long, long> runsByItem = [];
        List<DeliveredOutput> deliveredItems  = [];
        List<DeliveredPrint>  deliveredPrints = [];
        if (anyAssets)
        {
            assetRows = (await db.EsiAssets
                .Where(a => allTypes.Contains(a.TypeId) && ownerFilter.Contains(a.OwnerId))
                .Select(a => new { a.ItemId, a.TypeId, a.Quantity, a.RootLocationId, a.IsSingleton })
                .ToListAsync(ct))
                .Select(a => (a.ItemId, a.TypeId, a.Quantity, a.RootLocationId, a.IsSingleton))
                .ToList();

            // ⚠️ A blueprint level is written in RUNS, not copies, and the assets table cannot
            // answer in runs: a copy is one row of quantity 1 whether it carries two runs or
            // twenty. Counted that way, four Ark copies holding twenty runs between them read as
            // "4" against a target of five and asked for a fifth that was not needed.
            //
            // ⚠️ Copies ONLY. An original is unlimited runs and satisfies nothing here, which
            // looks wrong until you ask what a blueprint level is kept for: invention runs off a
            // copy and cannot touch the original, however many runs the original is good for. A
            // group holding a BPO and no copies is genuinely empty, and cutting copies is the
            // answer — the same reason the purchase pass counts only copies against a shelf.
            runsByItem = bpTypeIds.Count == 0
                ? []
                : await db.EsiBlueprints.AsNoTracking()
                    .Where(b => bpTypeIds.Contains(b.TypeId) && b.Runs > 0)
                    .ToDictionaryAsync(b => b.ItemId, b => (long)b.Runs, ct);

            // ⚠️ Plus what delivered jobs have put in hangars that the asset and blueprint polls
            // have not seen yet. A delivered job stops counting under Industry Jobs within
            // minutes, and for up to an hour its output is in no asset row either — so a level
            // that was comfortably covered by a running job read as short the moment the job was
            // collected, and the worklist raised the same job again. Items in units; blueprint
            // types in runs, as the level is written. See DeliveryLag.
            deliveredItems = await DeliveryLag.ItemsAsync(db, ct, allTypes);
            if (bpTypeIds.Count > 0)
                deliveredPrints = await DeliveryLag.PrintsAsync(db, ct, bpTypeIds.ToList());
        }

        // ── Industry jobs: active manufacturing (1) and reactions (9, plus legacy 11) ──
        // Count the UNITS that will be produced = Runs × output-per-run, so the total is
        // consistent with asset quantities. Reactions and multi-output blueprints (e.g.
        // capital components, ammo) produce many units per run, so counting runs alone
        // undercounts — and reactions were previously excluded entirely.
        List<(int BlueprintTypeId, int ProductTypeId, int Runs, long FacilityId)> jobRows = [];
        Dictionary<(int, int), int> qtyMap = [];
        if (anyJobs)
        {
            jobRows = (await db.EsiIndustryJobs
                .Where(j => (j.ActivityId == 1 || j.ActivityId == 9 || j.ActivityId == 11)
                         && j.Status == "active"
                         && j.ProductTypeId.HasValue
                         && allTypes.Contains(j.ProductTypeId!.Value)
                         && ownerFilter.Contains(j.OwnerId))
                .Select(j => new { j.BlueprintTypeId, ProductTypeId = j.ProductTypeId!.Value, j.Runs, j.FacilityId })
                .ToListAsync(ct))
                .Select(j => (j.BlueprintTypeId, j.ProductTypeId, j.Runs, j.FacilityId))
                .ToList();

            if (jobRows.Count > 0)
            {
                // Output units per run, keyed by (blueprint, product). Use the job's own
                // blueprint so the count matches exactly what that job will deliver.
                var bpIds = jobRows.Select(j => j.BlueprintTypeId).Distinct().ToList();
                qtyMap = (await db.SdeBlueprintProducts.AsNoTracking()
                        .Where(p => bpIds.Contains(p.TypeId)
                                 && (p.Activity == "manufacturing" || p.Activity == "reaction"))
                        .Select(p => new { p.TypeId, p.ProductTypeId, p.Quantity })
                        .ToListAsync(ct))
                    .GroupBy(p => (p.TypeId, p.ProductTypeId))
                    .ToDictionary(g => g.Key, g => g.First().Quantity);
            }
        }

        // ── Market buy orders — active, not historical ───────────────────────
        List<(long OrderId, string OwnerType, int TypeId, int VolumeRemain, long LocationId)> orderRows = [];
        if (anyOrders)
            orderRows = (await db.EsiMarketOrders
                .Where(o => o.IsBuyOrder && !o.IsHistory && allTypes.Contains(o.TypeId)
                         && ownerFilter.Contains(o.OwnerId))
                .Select(o => new { o.OrderId, o.OwnerType, o.TypeId, o.VolumeRemain, o.LocationId })
                .ToListAsync(ct))
                .Select(o => (o.OrderId, o.OwnerType, o.TypeId, o.VolumeRemain, o.LocationId))
                .ToList();

        // ── Contracts we are buying through ──────────────────────────────────
        // Outstanding item exchanges of ours that ASK for the item, so accepting them brings it in.
        //
        // ⚠️ Requested, not offered. IsIncluded true means the issuer is handing the item over;
        // false means they want it delivered to them. This counts the false rows on contracts our
        // own characters and personal corporations issued, which is the contract-window equivalent
        // of a market buy order and the only shape that adds stock we do not already have.
        //
        // ⚠️ Ours only, by the same owner filter every block here uses — a contract sitting in a
        // corporation the player merely belongs to is somebody else's supply.
        //
        // ⚠️ Not scoped by station. A contract's end location is where it is collected, and the
        // item lands wherever the acceptor is told to put it; a location-scoped group would
        // otherwise silently drop every contract whose pickup happens to sit elsewhere.
        List<(int TypeId, long Quantity)> contractLines = [];
        if (anyContracts)
        {
            var mine = await db.EsiContracts.AsNoTracking()
                .Where(c => c.Status == "outstanding"
                         && c.Type == "item_exchange"
                         && ownerFilter.Contains(c.OwnerId)
                         && ownerFilter.Contains(c.IssuerId))
                .Select(c => c.ContractId)
                .Distinct()
                .ToListAsync(ct);

            if (mine.Count > 0)
                contractLines = (await db.EsiContractItems.AsNoTracking()
                    .Where(i => mine.Contains(i.ContractId)
                             && !i.IsIncluded
                             && allTypes.Contains(i.TypeId))
                    .Select(i => new { i.TypeId, i.Quantity })
                    .ToListAsync(ct))
                    .Select(i => (i.TypeId, i.Quantity))
                    .ToList();
        }

        // ── Per group: the same filters the per-group queries applied, in memory ──
        foreach (var (group, typeIds) in requests)
        {
            var wanted        = typeIds.ToHashSet();
            var stationFilter = scopeFilters[(group.Scope, group.LocationId)];

            var assets    = new Dictionary<int, long>();
            var jobs      = new Dictionary<int, long>();
            var orders    = new Dictionary<int, long>();
            var contracts = new Dictionary<int, long>();

            if (group.IncludeAssets)
            {
                var rows = assetRows.Where(a => wanted.Contains(a.TypeId));
                if (stationFilter != null)
                    rows = rows.Where(a => stationFilter.Contains(a.RootLocationId));

                // Packaged only: skip assembled and fitted hulls. The group setting is the usual
                // source; the parameter is how the sale posting tool overrides it per posting.
                //
                // ⚠️ Blueprints are exempt, and that is not a nicety. Singleton on a blueprint does
                // not mean "assembled" — it means the item does not stack, which is true of every copy
                // and every researched original. Filtering on it would empty a blueprint group
                // outright, and a group of T2 copies is exactly where somebody would think to tick a
                // box about packaging.
                if (packagedOnly || group.PackagedOnly)
                    rows = rows.Where(a => !a.IsSingleton || bpTypeIds.Contains(a.TypeId));

                foreach (var g in rows.GroupBy(r => r.TypeId))
                {
                    if (!bpTypeIds.Contains(g.Key))
                    {
                        assets[g.Key] = g.Sum(r => (long)r.Quantity);
                        continue;
                    }

                    // ⚠️ A row the blueprints endpoint never returned counts for nothing rather
                    // than for one. Runs are the unit here, that row's run count is unknown, and
                    // guessing at it would report stock the group may not have — where a plain
                    // count at least could not be wrong about what it was counting.
                    assets[g.Key] = g.Sum(r => runsByItem.GetValueOrDefault(r.ItemId));
                }

                foreach (var d in deliveredItems)
                {
                    if (!wanted.Contains(d.TypeId)) continue;
                    if (!ownerFilter.Contains(d.OwnerId)) continue;
                    if (stationFilter != null && !stationFilter.Contains(d.Site)) continue;
                    assets[d.TypeId] = assets.GetValueOrDefault(d.TypeId) + d.Units;
                }
                foreach (var p in deliveredPrints)
                {
                    if (!wanted.Contains(p.TypeId)) continue;
                    if (!ownerFilter.Contains(p.OwnerId)) continue;
                    if (stationFilter != null && !stationFilter.Contains(p.Site)) continue;
                    assets[p.TypeId] = assets.GetValueOrDefault(p.TypeId) + (long)p.Copies * p.RunsEach;
                }
            }

            if (group.IncludeIndustryJobs)
            {
                // Scope by FacilityId (the structure the job runs in), NOT OutputLocationId —
                // the latter is the delivery hangar/container sub-location, which does not
                // resolve to a structure and would drop every job from location-scoped groups.
                foreach (var j in jobRows)
                {
                    if (!wanted.Contains(j.ProductTypeId)) continue;
                    if (stationFilter != null && !stationFilter.Contains(j.FacilityId)) continue;

                    long perRun = qtyMap.TryGetValue((j.BlueprintTypeId, j.ProductTypeId), out var qy)
                        ? Math.Max(1, qy) : 1;
                    long units = (long)j.Runs * perRun;
                    jobs[j.ProductTypeId] = jobs.TryGetValue(j.ProductTypeId, out var cur) ? cur + units : units;
                }
            }

            if (group.IncludeMarketBuyOrders)
            {
                var rows = orderRows.Where(o => wanted.Contains(o.TypeId));
                if (stationFilter != null)
                    rows = rows.Where(o => stationFilter.Contains(o.LocationId));

                // ⚠️ One order, two rows. A corporation order placed by a character comes back
                // from both the character endpoint and the corporation one, and both are stored —
                // 25 open buy orders here are duplicated that way. Summing VolumeRemain straight
                // off the table therefore counts the same order twice: 12,886 units of
                // Fullerite-C32 on order read as 25,772, and a rule saw stock arriving that does
                // not exist.
                //
                // Deduplicated by OrderId, preferring the corporation's row — the same rule
                // MaterialPurchaseGenerator and InventoryLevelGenerator already apply.
                foreach (var g in rows
                             .GroupBy(o => o.OrderId)
                             .Select(g => g.Any(o => o.OwnerType == "corporation") ? g.First(o => o.OwnerType == "corporation") : g.First())
                             .GroupBy(o => o.TypeId))
                    orders[g.Key] = g.Sum(o => (long)o.VolumeRemain);
            }

            if (group.IncludeContractsBuying)
                foreach (var g in contractLines.Where(i => wanted.Contains(i.TypeId)).GroupBy(i => i.TypeId))
                    contracts[g.Key] = g.Sum(i => i.Quantity);

            result[group.Id] = typeIds.Distinct().ToDictionary(
                id => id,
                id => new InvAvailability(
                    assets.GetValueOrDefault(id),
                    jobs.GetValueOrDefault(id),
                    orders.GetValueOrDefault(id),
                    contracts.GetValueOrDefault(id)));
        }

        return result;
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
            .Select(t => new { t.TypeId, t.Name, Volume = t.PackagedVolume > 0 ? t.PackagedVolume : t.Volume })
            .ToListAsync(ct);

        var blueprints = await BlueprintTypeIdsAsync(db, ids, ct);

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
                buildCosts.TryGetValue(t.TypeId, out var bc) && bc > 0 ? bc : null,
                blueprints.Contains(t.TypeId)));
    }

    public async Task<Dictionary<int, string>> GetTypeNamesAsync(
        IEnumerable<int> typeIds, CancellationToken ct = default)
    {
        var meta = await GetTypeMetaAsync(typeIds, ct);
        return meta.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
    }
}
