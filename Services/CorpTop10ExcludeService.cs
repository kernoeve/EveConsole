using System.Collections.Concurrent;
using EveConsole.Api;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

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

    /// <summary>
    /// Pilots or corporations whose name contains <paramref name="nameFragment"/>, for the
    /// exclusion picker: our own first, then every name the app has met, then ESI's search.
    ///
    /// <para>⚠️ Not only our own. The corporation search read nothing but the corporations
    /// signed in to the app, and the ones worth excluding from a Top 10 are other people's — an
    /// alliance mate's alt corp, a holding corp — so the picker listed nothing and no corporation
    /// could be added at all. The pilot search had an ESI fallback, but only when none of our own
    /// characters matched. Both now go through the entity browser's search, which reads the name
    /// cache and asks ESI for what it does not know.</para>
    /// </summary>
    public async Task<List<CorpTop10Exclude>> SearchAsync(
        string nameFragment, string entityType, CancellationToken ct = default)
    {
        const int Max = 20;
        using var db = factory.CreateDbContext();
        var lower = nameFragment.ToLower();
        var isCorp = entityType == "corporation";

        var ours = isCorp
            ? await db.Corporations
                .Where(c => c.Name.ToLower().Contains(lower))
                .OrderBy(c => c.Name)
                .Take(Max)
                .Select(c => new { Id = (long)c.Id, c.Name })
                .ToListAsync(ct)
            : await db.Characters
                .Where(c => c.Name.ToLower().Contains(lower))
                .OrderBy(c => c.Name)
                .Take(Max)
                .Select(c => new { Id = (long)c.Id, c.Name })
                .ToListAsync(ct);

        var found = await new EntityBrowserService(factory, esi).SearchWithEsiAsync(
            isCorp ? EntityKind.PlayerCorp : EntityKind.Pilot, nameFragment, ct);

        return ours.Select(o => (o.Id, o.Name))
            .Concat(found.Select(m => (m.Id, m.Name)))
            .DistinctBy(e => e.Item1)
            .Take(Max)
            .Select(e => new CorpTop10Exclude { EntityId = e.Item1, EntityType = entityType, EntityName = e.Item2 })
            .ToList();
    }
}
