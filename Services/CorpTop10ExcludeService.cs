using System.Collections.Concurrent;
using EveCortex.Api;
using EveCortex.Data;
using EveCortex.Models;
using Microsoft.EntityFrameworkCore;

namespace EveCortex.Services;

public class CorpTop10ExcludeService(IDbContextFactory<AppDbContext> factory, EsiClient esi)
{
    private readonly ConcurrentDictionary<(long, string), CorpTop10Exclude> _cache = new();

    public async Task LoadAsync(CancellationToken ct = default)
    {
        using var db = factory.CreateDbContext();
        var rows = await db.CorpTop10Excludes.AsNoTracking().ToListAsync(ct);
        _cache.Clear();
        foreach (var r in rows)
            _cache[(r.EntityId, r.EntityType)] = r;
    }

    public List<CorpTop10Exclude> GetAll() => _cache.Values.OrderBy(e => e.EntityName).ToList();

    public HashSet<long> GetExcludeIds() => _cache.Values.Select(e => e.EntityId).ToHashSet();

    public async Task AddAsync(long entityId, string entityType, string entityName,
        CancellationToken ct = default)
    {
        var entry = new CorpTop10Exclude
        { EntityId = entityId, EntityType = entityType, EntityName = entityName };

        using var db = factory.CreateDbContext();
        var existing = await db.CorpTop10Excludes
            .FindAsync(new object[] { entityId, entityType }, ct);
        if (existing is null)
        {
            db.CorpTop10Excludes.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        _cache[(entityId, entityType)] = entry;
    }

    public async Task RemoveAsync(long entityId, string entityType,
        CancellationToken ct = default)
    {
        using var db = factory.CreateDbContext();
        var existing = await db.CorpTop10Excludes
            .FindAsync(new object[] { entityId, entityType }, ct);
        if (existing is not null)
        {
            db.CorpTop10Excludes.Remove(existing);
            await db.SaveChangesAsync(ct);
        }
        _cache.TryRemove((entityId, entityType), out _);
    }

    public async Task<List<CorpTop10Exclude>> SearchAsync(
        string nameFragment, string entityType, CancellationToken ct = default)
    {
        using var db = factory.CreateDbContext();
        var lower = nameFragment.ToLower();

        if (entityType == "character")
        {
            var chars = await db.Characters
                .Where(c => c.Name.ToLower().Contains(lower))
                .OrderBy(c => c.Name)
                .Take(20)
                .ToListAsync(ct);

            if (chars.Count > 0)
                return chars.Select(c => new CorpTop10Exclude
                    { EntityId = c.Id, EntityType = "character", EntityName = c.Name }).ToList();

            // Fall back to ESI authenticated character search using the first available character.
            var firstChar = await db.Characters.OrderBy(c => c.Id).Select(c => c.Id).FirstOrDefaultAsync(ct);
            if (firstChar == 0) return [];
            var ids = await esi.SearchCharacterIdsAsync(firstChar, nameFragment, ct);
            if (ids.Count == 0) return [];
            var names = await esi.GetNamesAsync(ids.Take(20).ToList(), ct);
            return names.Select(n => new CorpTop10Exclude
                { EntityId = (long)n.Id, EntityType = "character", EntityName = n.Name }).ToList();
        }
        else
        {
            var corps = await db.Corporations
                .Where(c => c.Name.ToLower().Contains(lower))
                .OrderBy(c => c.Name)
                .Take(20)
                .ToListAsync(ct);
            return corps.Select(c => new CorpTop10Exclude
                { EntityId = (long)c.Id, EntityType = "corporation", EntityName = c.Name }).ToList();
        }
    }
}
