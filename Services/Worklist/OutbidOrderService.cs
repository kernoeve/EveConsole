using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Our own buy orders that have been outbid while something still needs what they are buying.
///
/// <para>An outbid order is the quietest failure the tool can report. It exists, it appears in
/// every list of open orders, its ISK is committed — and it is buying nothing, because everyone
/// selling is selling to the higher bid. Nothing else surfaces that: the order is not missing, not
/// expired, and its remaining volume still counts as material on the way.</para>
///
/// <para><b>⚠️ Only where a need remains.</b> An order for something already stocked to target is
/// not failing at anything, and raising its bid would spend ISK to acquire what is not wanted. The
/// caller decides what is needed — it holds the demand — and passes those types in.</para>
///
/// <para><b>⚠️ Orders count wherever they sit.</b> Which stations the tool searches for ASSETS is
/// the user's scope to set, but an order is a commitment to buy regardless of where it was placed:
/// one raised in a different market still fills the same need, and the material is hauled after.
/// Matching orders to the station a rule happens to buy at would report only the ones that
/// coincided with that choice.</para>
///
/// <para><b>Substitutes count too.</b> A need for a material can be filled by an order for the
/// compressed form of it, which is a different type entirely. The same relationship
/// <see cref="MaterialSubstitutionService"/> already uses to stop the tool buying what it owns in
/// another form decides which orders serve which need, so the two cannot disagree about it.</para>
/// </summary>
public class OutbidOrderService(
    IDbContextFactory<AppDbContext> dbFactory,
    MarketCompetitionService competition,
    MaterialSubstitutionService substitution)
{
    /// <summary>One of our orders, losing, for something still wanted.</summary>
    /// <param name="ForTypeId">The needed type this order serves — the order's own type, or the
    /// type it reduces to.</param>
    public sealed record Losing(
        int TypeId, string TypeName, long LocationId, string LocationName,
        decimal OurBid, decimal BestBid, long VolumeRemain, int ForTypeId,
        long CharacterId, string CharacterName);

    /// <summary>
    /// Which of our open buy orders are outbid and are serving one of <paramref name="neededTypeIds"/>.
    /// </summary>
    public async Task<List<Losing>> FindAsync(
        IReadOnlyCollection<int> neededTypeIds, CancellationToken ct = default)
    {
        if (neededTypeIds.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // A need can be met by the thing itself or by anything that reduces to it, so an order for
        // either is an order serving that need.
        var subs = await substitution.LoadAsync(ct);
        var servedBy = new Dictionary<int, int>();          // order type → needed type
        foreach (var needed in neededTypeIds)
        {
            servedBy[needed] = needed;
            foreach (var s in subs.GetValueOrDefault(needed) ?? [])
                servedBy.TryAdd(s.SourceTypeId, needed);
        }

        var wanted = servedBy.Keys.ToList();

        // ⚠️ Deduplicated by OrderId, preferring the corporation's row: a corp order placed by one
        // of our characters comes back from both endpoints under one id, and counted twice it
        // would compare against itself.
        //
        // The character's row is not discarded, though — only its volume is. It carries the one
        // thing the corp row cannot, which is who has to log in to change the price. A corp order
        // with no character row is one somebody else in the corp placed, and it stays unattributed
        // rather than being pinned on whoever happens to be assigned to that station.
        var rows = await db.EsiMarketOrders.AsNoTracking()
            .Where(o => o.IsBuyOrder && !o.IsHistory && wanted.Contains(o.TypeId))
            .Select(o => new { o.OrderId, o.OwnerId, o.OwnerType, o.TypeId, o.LocationId, o.Price, o.VolumeRemain })
            .ToListAsync(ct);

        var placedBy = rows
            .Where(o => o.OwnerType != "corporation")
            .GroupBy(o => o.OrderId)
            .ToDictionary(g => g.Key, g => g.First().OwnerId);

        var ours = rows
            .GroupBy(o => o.OrderId)
            .Select(g => g.FirstOrDefault(o => o.OwnerType == "corporation") ?? g.First())
            .ToList();

        if (ours.Count == 0) return [];

        var bids = await competition.LoadBuyAsync(
            ours.Select(o => o.TypeId).Distinct().ToList(),
            ours.Select(o => o.LocationId).Distinct().ToList(),
            ours.Select(o => o.OrderId).ToHashSet(), ct);

        var names  = await NamesAsync(db, ours.Select(o => o.TypeId).Distinct().ToList(), ct);
        var places = await PlaceNamesAsync(db, ours.Select(o => o.LocationId).Distinct().ToList(), ct);

        var charIds   = placedBy.Values.Distinct().ToList();
        var charNames = await db.Characters.AsNoTracking()
            .Where(c => charIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var losing = new List<Losing>();

        // One entry per type and station rather than per order: two orders for the same thing in
        // the same market are one visit to fix, and the worse of them is the one that decides it.
        foreach (var g in ours.GroupBy(o => (o.TypeId, o.LocationId)))
        {
            // Null means nobody else is bidding OR the station is not a market source we track.
            // Only the first is "you are winning"; the second is not knowable, and reporting it as
            // outbid would invent a rival.
            if (bids.BestBuy(g.Key.TypeId, g.Key.LocationId) is not { } rival) continue;

            var ourBest = g.Max(o => (decimal)o.Price);
            if (ourBest >= rival) continue;

            // The character on the worst-priced of the folded orders — the one the row is about.
            var worst = g.OrderBy(o => o.Price).First();
            var who   = placedBy.GetValueOrDefault(worst.OrderId);

            losing.Add(new Losing(
                g.Key.TypeId,
                names.GetValueOrDefault(g.Key.TypeId, $"Type {g.Key.TypeId}"),
                g.Key.LocationId,
                places.GetValueOrDefault(g.Key.LocationId, $"Location {g.Key.LocationId}"),
                ourBest, rival,
                g.Sum(o => (long)o.VolumeRemain),
                servedBy[g.Key.TypeId],
                who, who > 0 ? charNames.GetValueOrDefault(who, "") : ""));
        }

        return losing;
    }

    /// <summary>
    /// The task itself. Built here so every caller words it the same way and, more importantly,
    /// keys it the same way — two generators can each find the same losing order, and an identical
    /// key is what lets the service show it once.
    /// </summary>
    public static WorklistItem Task(Losing l)
    {
        var behind = l.BestBid - l.OurBid;
        var pct    = l.OurBid > 0 ? behind / l.OurBid * 100 : 0;

        // The name leads, because the task column sorts on this string and a leading verb would
        // file every one of these under the same letter.
        return new WorklistItem
        {
            Key           = $"outbid:{l.TypeId}:{l.LocationId}",
            Source        = "outbid",
            Kind          = WorklistKind.Buy,
            Title         = $"{l.TypeName} — raise bid",
            // No quantity to acquire, so nothing to merge with a purchase of the same thing. The
            // two are different actions: one changes a price, the other places an order.
            MergeKey      = null,
            Detail        = $"At {l.LocationName}: outbid at {l.OurBid:N2} ISK against {l.BestBid:N2} "
                          + $"— behind by {behind:N2} ({pct:N1}%). {l.VolumeRemain:N0} units still on "
                          + "order and buying nothing while something needs them.",
            Readiness     = WorklistReadiness.Ready,
            LocationId    = l.LocationId,
            LocationName  = l.LocationName,
            TypeId        = l.TypeId,
            TypeName      = l.TypeName,
            // Not blocked when this is empty. Every other buy task needs a character picked for
            // it, but this order was already placed by somebody — the name is a convenience, and
            // an unattributed corp order is still perfectly actionable by anyone with the role.
            CharacterId   = l.CharacterName.Length > 0 ? l.CharacterId : 0,
            CharacterName = l.CharacterName,
            Priority      = WorklistPriority.Outbid,
        };
    }

    private static async Task<Dictionary<int, string>> NamesAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

    private static async Task<Dictionary<long, string>> PlaceNamesAsync(
        AppDbContext db, List<long> ids, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        if (ids.Count == 0) return names;

        foreach (var s in await db.SdeStations.AsNoTracking()
                     .Where(s => ids.Contains(s.StationId))
                     .Select(s => new { s.StationId, s.Name }).ToListAsync(ct))
            names[s.StationId] = s.Name;

        foreach (var s in await db.Structures.AsNoTracking()
                     .Where(s => ids.Contains(s.StructureId) && s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            names[s.StructureId] = s.Name;

        return names;
    }
}
