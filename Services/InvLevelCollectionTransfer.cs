using System.Text.Json;
using System.Text.Json.Serialization;
using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

// ── Wire shape ────────────────────────────────────────────────────────────────
//
// Deliberately not the entity types. Ids are database-local and meaningless in another install,
// so none are carried; items are named as well as typed so a file stays readable and a missing
// type can be reported by name rather than as a number.

public sealed record InvCollectionFile
{
    public int    Version { get; init; } = 1;
    public string Name    { get; init; } = "";
    public List<InvGroupFile> Groups { get; init; } = [];
}

public sealed record InvGroupFile
{
    public string Name       { get; init; } = "";
    public int    Multiplier { get; init; } = 1;
    public string Scope      { get; init; } = "Everywhere";

    /// <summary>
    /// Carried because a station id means the same thing everywhere, unlike a group id. It may
    /// still name a structure the importer cannot see, so the name travels with it.
    /// </summary>
    public long?  LocationId   { get; init; }
    public string LocationName { get; init; } = "";

    public bool IncludeAssets          { get; init; } = true;
    public bool IncludeIndustryJobs    { get; init; } = true;
    public bool IncludeMarketBuyOrders { get; init; } = true;
    public bool IncludeContractsBuying { get; init; }

    public List<InvItemFile> Items { get; init; } = [];
}

public sealed record InvItemFile
{
    public int    TypeId   { get; init; }
    public string TypeName { get; init; } = "";
    public int    Target   { get; init; } = 1;
}

/// <summary>
/// Moving a whole collection of inventory groups between installs, or keeping a copy of one.
///
/// <para>A collection is a lot of hand-entered work — group scopes, include flags, and a target
/// per item across dozens of items — and until now the only way to reproduce it elsewhere was to
/// retype it.</para>
/// </summary>
public class InvLevelCollectionTransfer(IDbContextFactory<AppDbContext> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task ExportAsync(int collectionId, Stream output, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var collection = await db.InvLevelCollections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == collectionId, ct)
            ?? throw new InvalidOperationException("That collection no longer exists.");

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => g.CollectionId == collectionId)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

        var groupIds = groups.Select(g => g.Id).ToList();
        var items = await db.InvLevelItems.AsNoTracking()
            .Where(i => groupIds.Contains(i.GroupId))
            .ToListAsync(ct);

        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => items.Select(i => i.TypeId).Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var file = new InvCollectionFile
        {
            Name   = collection.Name,
            Groups = groups.Select(g => new InvGroupFile
            {
                Name                   = g.Name,
                Multiplier             = g.Multiplier,
                Scope                  = g.Scope,
                LocationId             = g.LocationId,
                LocationName           = g.LocationName,
                IncludeAssets          = g.IncludeAssets,
                IncludeIndustryJobs    = g.IncludeIndustryJobs,
                IncludeMarketBuyOrders = g.IncludeMarketBuyOrders,
                IncludeContractsBuying = g.IncludeContractsBuying,
                Items = items.Where(i => i.GroupId == g.Id)
                             .OrderBy(i => names.GetValueOrDefault(i.TypeId, ""))
                             .Select(i => new InvItemFile
                             {
                                 TypeId   = i.TypeId,
                                 TypeName = names.GetValueOrDefault(i.TypeId, $"Type {i.TypeId}"),
                                 Target   = i.TargetQuantity,
                             })
                             .ToList(),
            }).ToList(),
        };

        await JsonSerializer.SerializeAsync(output, file, Json, ct);
    }

    /// <summary>What an import did, so the caller can say so rather than just claiming success.</summary>
    public sealed record ImportResult(string CollectionName, int Groups, int Items, int UnknownTypes);

    /// <summary>
    /// Brings a collection in under a fresh name, never merging into an existing one.
    ///
    /// <para>Importing onto a collection already in use would have to guess at every collision —
    /// same group name, different scope; same item, different target — and guessing wrong
    /// silently rewrites work the player did by hand. A new collection beside the old one leaves
    /// both intact and the comparison to the player.</para>
    /// </summary>
    public async Task<ImportResult> ImportAsync(Stream input, CancellationToken ct = default)
    {
        var file = await JsonSerializer.DeserializeAsync<InvCollectionFile>(input, Json, ct)
                   ?? throw new InvalidOperationException("That file is not a collection export.");
        if (file.Groups.Count == 0)
            throw new InvalidOperationException("That file contains no groups.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var taken = (await db.InvLevelCollections.AsNoTracking()
            .Select(c => c.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = string.IsNullOrWhiteSpace(file.Name) ? "Imported collection" : file.Name;
        var unique = name;
        for (var n = 2; taken.Contains(unique); n++) unique = $"{name} ({n})";

        var collection = new InvLevelCollection { Name = unique };
        db.InvLevelCollections.Add(collection);
        await db.SaveChangesAsync(ct);

        // Types the importing install has never heard of are dropped rather than added blind: an
        // item with no SDE entry shows as a bare number everywhere and can never be valued.
        var wanted = file.Groups.SelectMany(g => g.Items).Select(i => i.TypeId).Distinct().ToList();
        var known = (await db.SdeTypes.AsNoTracking()
            .Where(t => wanted.Contains(t.TypeId))
            .Select(t => t.TypeId).ToListAsync(ct)).ToHashSet();

        var items = 0;
        foreach (var g in file.Groups)
        {
            var group = new InvLevelGroup
            {
                Name                   = g.Name,
                Multiplier             = Math.Max(1, g.Multiplier),
                CollectionId           = collection.Id,
                Scope                  = g.Scope,
                LocationId             = g.LocationId,
                LocationName           = g.LocationName,
                IncludeAssets          = g.IncludeAssets,
                IncludeIndustryJobs    = g.IncludeIndustryJobs,
                IncludeMarketBuyOrders = g.IncludeMarketBuyOrders,
                IncludeContractsBuying = g.IncludeContractsBuying,
            };
            db.InvLevelGroups.Add(group);
            await db.SaveChangesAsync(ct);

            foreach (var i in g.Items.Where(i => known.Contains(i.TypeId))
                               .GroupBy(i => i.TypeId).Select(x => x.First()))
            {
                db.InvLevelItems.Add(new InvLevelItem
                {
                    GroupId        = group.Id,
                    TypeId         = i.TypeId,
                    TargetQuantity = Math.Max(1, i.Target),
                });
                items++;
            }
            await db.SaveChangesAsync(ct);
        }

        return new ImportResult(unique, file.Groups.Count, items, wanted.Count - known.Count);
    }
}
