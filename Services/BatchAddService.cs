using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

public record BlueprintSearchResult(int BlueprintTypeId, int ProductTypeId, string ProductName);

public class BatchAddService(IDbContextFactory<AppDbContext> dbFactory)
{
    // ── Market group subtree items ────────────────────────────────────────────

    public async Task<List<(int TypeId, string Name)>> GetItemsInGroupTreeAsync(
        int marketGroupId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();

        // Load all market groups and build children map
        var allGroups = await db.SdeMarketGroups.AsNoTracking().ToListAsync(ct);
        var childMap  = allGroups.GroupBy(g => g.ParentGroupId ?? 0)
            .ToDictionary(g => g.Key, g => g.Select(x => x.MarketGroupId).ToList());

        // Collect all descendant group IDs (inclusive)
        var groupIds = new HashSet<int>();
        Collect(marketGroupId, groupIds, childMap);

        // Find all published types in those groups
        var rows = await db.SdeTypes.AsNoTracking()
            .Where(t => t.Published && t.MarketGroupId.HasValue && groupIds.Contains(t.MarketGroupId!.Value))
            .OrderBy(t => t.Name)
            .Select(t => new { t.TypeId, t.Name })
            .ToListAsync(ct);
        return rows.Select(x => (x.TypeId, x.Name)).ToList();
    }

    private static void Collect(int id, HashSet<int> result, Dictionary<int, List<int>> childMap)
    {
        result.Add(id);
        if (!childMap.TryGetValue(id, out var children)) return;
        foreach (var c in children) Collect(c, result, childMap);
    }

    // Returns the given market group's ID plus every descendant group ID (inclusive).
    public async Task<HashSet<int>> GetDescendantGroupIdsAsync(
        int marketGroupId, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var allGroups = await db.SdeMarketGroups.AsNoTracking().ToListAsync(ct);
        var childMap  = allGroups.GroupBy(g => g.ParentGroupId ?? 0)
            .ToDictionary(g => g.Key, g => g.Select(x => x.MarketGroupId).ToList());

        var groupIds = new HashSet<int>();
        Collect(marketGroupId, groupIds, childMap);
        return groupIds;
    }

    // ── Blueprint search ──────────────────────────────────────────────────────

    public async Task<List<BlueprintSearchResult>> SearchBlueprintsAsync(
        string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        await using var db = dbFactory.CreateDbContext();

        // Join blueprint products → product SdeType by product name
        var productMatches = await db.SdeBlueprintProducts
            .Where(bp => bp.Activity == "manufacturing")
            .Join(db.SdeTypes,
                  bp => bp.ProductTypeId, t => t.TypeId,
                  (bp, t) => new { bp.TypeId, bp.ProductTypeId, t.Name, t.Published })
            .Where(x => x.Published && EF.Functions.Like(x.Name, $"%{text}%"))
            .OrderBy(x => x.Name)
            .Take(40)
            .Select(x => new BlueprintSearchResult(x.TypeId, x.ProductTypeId, x.Name))
            .ToListAsync(ct);

        return productMatches;
    }

    // ── Indy parks ────────────────────────────────────────────────────────────

    public async Task<List<IndyPark>> LoadParksAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.IndyParks.OrderBy(p => p.Name).ToListAsync(ct);
    }

    // ── Market group tree (for picker UI) ─────────────────────────────────────

    public async Task<List<SdeMarketGroup>> LoadAllMarketGroupsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        return await db.SdeMarketGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
    }

    public async Task<HashSet<int>> GetGroupIdsWithItemsAsync(CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        // Leaf group IDs that have at least one published type
        var leafIds = await db.SdeTypes.AsNoTracking()
            .Where(t => t.Published && t.MarketGroupId.HasValue)
            .Select(t => t.MarketGroupId!.Value)
            .Distinct()
            .ToListAsync(ct);
        return new HashSet<int>(leafIds);
    }
}
