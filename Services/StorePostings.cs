using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// A store's listings, in the order it shows them.
///
/// <para>⚠️ Read through here, never off <see cref="Store.PostingId"/> or the table alone. A store
/// from before listings has no rows and one posting id, and that is a list of one Sell listing —
/// so an upgraded database needs nothing copied, and a store with rows keeps
/// <see cref="Store.PostingId"/> on its first Sell listing for whatever still reads it.</para>
/// </summary>
public static class StorePostings
{
    public static async Task<List<StorePosting>> ListAsync(AppDbContext db, Store store, CancellationToken ct = default)
        => (await ListAsync(db, [store], ct)).GetValueOrDefault(store.Id) ?? [];

    /// <summary>Every store's listings at once, by store id; a store with none has an empty list.</summary>
    public static async Task<Dictionary<int, List<StorePosting>>> ListAsync(
        AppDbContext db, IReadOnlyCollection<Store> stores, CancellationToken ct = default)
    {
        var ids  = stores.Select(s => s.Id).ToList();
        var rows = await db.StorePostings.AsNoTracking()
            .Where(p => ids.Contains(p.StoreId))
            .ToListAsync(ct);

        return stores.ToDictionary(s => s.Id, s =>
        {
            var mine = rows.Where(r => r.StoreId == s.Id && r.PostingId != 0)
                           .OrderBy(r => r.Ordinal).ThenBy(r => r.Id).ToList();
            if (mine.Count > 0) return mine;
            return s.PostingId == 0 ? [] : [new StorePosting { StoreId = s.Id, PostingId = s.PostingId, Side = StorePosting.Sell }];
        });
    }

    /// <summary>
    /// Writes a store's listings as given, in that order, and puts <see cref="Store.PostingId"/>
    /// on the first Sell listing of them.
    ///
    /// <para>One transaction: a reader between the delete and the inserts would find no rows and
    /// fall back to the old single posting, quoting a list the owner has just changed.</para>
    /// </summary>
    public static async Task ReplaceAsync(
        AppDbContext db, Store store, IReadOnlyList<StorePosting> listings, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.StorePostings.Where(p => p.StoreId == store.Id).ExecuteDeleteAsync(ct);
        for (var i = 0; i < listings.Count; i++)
            db.StorePostings.Add(new StorePosting
            {
                StoreId   = store.Id,
                PostingId = listings[i].PostingId,
                Ordinal   = i,
                Side      = listings[i].Side == StorePosting.Buy ? StorePosting.Buy : StorePosting.Sell,
                Title     = listings[i].Title.Trim(),
            });

        var firstSell = listings.FirstOrDefault(l => !l.IsBuy)?.PostingId ?? 0;
        await db.Stores.Where(s => s.Id == store.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.PostingId, firstSell), ct);
        store.PostingId = firstSell;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Stores with at least one listing, Sell or Buy: the ones with anything to serve.</summary>
    public static IQueryable<Store> WithListings(this IQueryable<Store> stores, AppDbContext db) =>
        stores.Where(s => s.PostingId != 0 || db.StorePostings.Any(p => p.StoreId == s.Id && p.PostingId != 0));
}
