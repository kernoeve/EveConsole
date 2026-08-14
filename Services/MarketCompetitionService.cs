using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// The best competing bid per (type, location), and which locations we can actually see.
///
/// The distinction between "nobody else is bidding" and "we cannot see this station" is the
/// whole reason this is a type rather than a dictionary: the second silently reads as the first,
/// and the first is good news.
/// </summary>
public sealed class CompetingBids
{
    private readonly Dictionary<(int TypeId, long LocationId), decimal> _best;
    private readonly HashSet<long> _tracked;

    internal CompetingBids(Dictionary<(int, long), decimal> best, HashSet<long> tracked)
    {
        _best    = best;
        _tracked = tracked;
    }

    /// <summary>Whether the public book covers this station at all. False means unknown, not clear.</summary>
    public bool IsTracked(long locationId) => _tracked.Contains(locationId);

    /// <summary>Highest bid from someone other than us. Null when nobody else is bidding, or
    /// when the location is untracked — check <see cref="IsTracked"/> to tell those apart.</summary>
    public decimal? BestBuy(int typeId, long locationId) =>
        _best.TryGetValue((typeId, locationId), out var p) ? p : null;
}

/// <summary>
/// Reads the public order book for competition checks.
///
/// Shared because more than one feature needs the same answer — the Standing Buy Orders tool and
/// the worklist's inventory-level rules both ask "am I being outbid here" — and two
/// implementations of that would eventually disagree about the two things it is easy to get
/// wrong: excluding our own orders, and treating an untracked station as uncontested.
/// </summary>
public class MarketCompetitionService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <param name="ourOrderIds">Our own order ids. They appear in the public book too, so
    /// without excluding them every order compares against itself and never looks outbid.</param>
    public async Task<CompetingBids> LoadBuyAsync(
        IReadOnlyList<int> typeIds, IReadOnlyList<long> locationIds,
        IReadOnlyCollection<long> ourOrderIds, CancellationToken ct = default)
    {
        if (typeIds.Count == 0 || locationIds.Count == 0)
            return new CompetingBids([], []);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bookRows = await db.MarketRawOrders.AsNoTracking()
            .Where(m => m.IsBuyOrder && typeIds.Contains(m.TypeId) && locationIds.Contains(m.LocationId))
            .Select(m => new { m.OrderId, m.TypeId, m.LocationId, m.Price })
            .ToListAsync(ct);

        var ours = ourOrderIds as HashSet<long> ?? ourOrderIds.ToHashSet();

        var best = bookRows
            .Where(m => !ours.Contains(m.OrderId))
            .GroupBy(m => (m.TypeId, m.LocationId))
            .ToDictionary(g => g.Key, g => (decimal)g.Max(m => m.Price));

        // A location with any rows at all is being tracked, even when this particular item has
        // no other bidder — otherwise "no rows for this type" and "no data for this station"
        // would be indistinguishable.
        var tracked = (await db.MarketRawOrders.AsNoTracking()
            .Where(m => locationIds.Contains(m.LocationId))
            .Select(m => m.LocationId)
            .Distinct()
            .ToListAsync(ct)).ToHashSet();

        return new CompetingBids(best, tracked);
    }
}
