using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>Goods a contract has taken out of a hangar, or put into one, that the asset snapshot
/// does not show yet.</summary>
/// <param name="Site">The station or structure: where the contract was made, or where a courier
/// contract took the goods.</param>
/// <param name="OwnerType">Whose hangar — the corporation's for a contract made or accepted on its
/// behalf, the character's otherwise.</param>
/// <param name="Units">Negative for goods that have left the hangar, positive for goods that have
/// come into it.</param>
/// <param name="IsSingleton">An assembled ship or a blueprint rather than packaged goods, for the
/// counts that only take packaged ones.</param>
public sealed record ContractMove(
    int ContractId, long Site, string OwnerType, long OwnerId, int TypeId, long Units, bool IsSingleton,
    DateTimeOffset At);

/// <summary>
/// What contracts have moved in and out of hangars that the asset polls have not caught up with.
///
/// <para>⚠️ Assets are polled hourly, contracts every five minutes. Making a contract takes its
/// goods out of the hangar at once, and for up to an hour the snapshot still shows them: the Order
/// Tracker found the hull just contracted to one buyer still on the shelf, called the next buyer's
/// order in stock, and mailed them to say so. Deleting a contract puts the goods back, accepting
/// one hands them over — and takes whatever it asked for in return — and a courier contract lands
/// them at the far end, each with the same gap. The contract-side twin of DeliveryLag, settled
/// the same way: measured against the asset clock of the owner whose hangar the goods left or
/// reached.</para>
///
/// <para>ESI dates the making, the accepting and the delivering. ⚠️ It does not date a deletion:
/// a deleted contract carries no date at all. The contracts poll records instead when it last saw
/// the contract as it was (<see cref="Models.ContractRecord.StatusChangedAfter"/>), and the goods
/// are credited back only when that was after the asset poll — deleted after it for certain. A
/// deletion the snapshot may already show is left to the snapshot: counting it twice would
/// promise stock that is not there, which is the fault this exists to stop.</para>
///
/// <para>Every move is signed, so a caller adds it to its pile as it adds delivered output — and
/// ⚠️ stops at zero: goods the snapshot holds somewhere the caller does not count, a wrapped
/// container or a hull it leaves out, can make the subtraction larger than the pile. The moves
/// come back with the arrivals first, so a pile that stops at zero on each one ends where it
/// would have if it had been summed first.</para>
/// </summary>
public static class ContractLag
{
    /// <summary>How old a copy ESI may answer a contracts call with — the cache on both routes.</summary>
    internal static readonly TimeSpan ContractsCache = TimeSpan.FromMinutes(5);

    /// <summary>
    /// An owner the asset poll has not reached for this long is not being polled at all — its
    /// token has gone, or its corporation will not show us its hangars. Its snapshot is not the
    /// state of anything any more, and leaving it out keeps the contract query to recent rows:
    /// one corporation refused for two months had the query reading every contract since.
    /// </summary>
    private static readonly TimeSpan NotPolled = TimeSpan.FromDays(7);

    /// <summary>Goods moved by contracts since the asset poll of the owner whose hangar they
    /// left or reached, arrivals first.</summary>
    /// <param name="typeIds">Only these types, where the caller has a list; null for every type.</param>
    public static async Task<List<ContractMove>> MovesAsync(
        AppDbContext db, CancellationToken ct, IReadOnlyCollection<int>? typeIds = null)
    {
        // Once per worklist build and sliced per caller, as DeliveryLag does.
        if (!Worklist.BuildCache.IsActive) return await MovesUncachedAsync(db, ct, typeIds);

        var all = await Worklist.BuildCache.GetOrAddAsync("ContractLag.Moves", () => MovesUncachedAsync(db, ct, null));
        if (typeIds is null) return all;
        if (typeIds.Count == 0) return [];
        var set = typeIds as IReadOnlySet<int> ?? typeIds.ToHashSet();
        return all.Where(m => set.Contains(m.TypeId)).ToList();
    }

    private static async Task<List<ContractMove>> MovesUncachedAsync(
        AppDbContext db, CancellationToken ct, IReadOnlyCollection<int>? typeIds)
    {
        if (typeIds is { Count: 0 }) return [];

        // Each owner's asset clock, and whether that is a character's hangar or a corporation's.
        var now = DateTimeOffset.UtcNow;
        var snapshot = (await db.EsiCallRecords.AsNoTracking()
                .Where(r => r.Endpoint == "char.assets" || r.Endpoint == "corp.assets")
                .Select(r => new { r.OwnerId, r.Endpoint, r.LastCalledAt })
                .ToListAsync(ct))
            .GroupBy(r => r.OwnerId)
            .Select(g => (Owner: g.Key,
                          Type: g.Any(r => r.Endpoint == "corp.assets") ? "corporation" : "character",
                          At: g.Max(r => r.LastCalledAt)))
            .Where(s => s.At > now - NotPolled)
            .ToDictionary(s => s.Owner, s => (s.Type, s.At));
        if (snapshot.Count == 0) return [];

        // ⚠️ Raw SQL for the dates: a DateTimeOffset in a LINQ Where does not translate on SQLite
        // (see OrderFulfilmentService.CandidateIdsAsync, which filters the same way). Only rows
        // polled for these owners — the key leads with the owner, where the dates have no index
        // and half a million public contracts share the table. Ids are our own longs, embedded.
        var floor = snapshot.Values.Min(s => s.At).ToUniversalTime();
        var sql = $$"""
            SELECT DISTINCT c."ContractId" AS "Value"
            FROM "EsiContracts" c
            WHERE c."OwnerId" IN ({{string.Join(",", snapshot.Keys)}})
              AND c."OwnerType" IN ('character', 'corporation')
              AND c."Type" IN ('item_exchange', 'auction', 'courier')
              AND (c."DateIssued" > {0} OR c."DateAccepted" > {1} OR c."DateCompleted" > {2}
                   OR c."StatusChangedAfter" > {3})
            """;
        var ids = await db.Database.SqlQueryRaw<int>(sql, floor, floor, floor, floor).ToListAsync(ct);
        if (ids.Count == 0) return [];

        // ⚠️ Every owner row of each, read together. A contract is a row per owner that can see
        // it, each as fresh as that owner's last poll; the most settled speaks for the contract,
        // as it does for the Order Tracker.
        var rows = await db.EsiContracts.AsNoTracking()
            .Where(c => ids.Contains(c.ContractId) && c.OwnerType != "public")
            .Select(c => new
            {
                c.ContractId, c.Type, c.Status, c.ForCorporation, c.IssuerId, c.IssuerCorporationId,
                c.AcceptorId, c.StartLocationId, c.EndLocationId, c.DateIssued, c.DateAccepted,
                c.DateCompleted, c.StatusChangedAfter,
            })
            .ToListAsync(ct);

        var lineQuery = db.EsiContractItems.AsNoTracking().Where(i => ids.Contains(i.ContractId));
        if (typeIds is not null)
        {
            var wanted = typeIds.ToList();
            lineQuery = lineQuery.Where(i => wanted.Contains(i.TypeId));
        }
        var lines = (await lineQuery
                .Select(i => new { i.ContractId, i.TypeId, i.Quantity, i.IsIncluded, i.IsSingleton })
                .ToListAsync(ct))
            .ToLookup(i => i.ContractId);

        var moves = new List<ContractMove>();

        // The lines one side of a contract hands over — offered (included) or asked for — moved in
        // or out of one owner's hangar.
        void Move(int contractId, long? site, (string Type, long Id) owner, bool offered, int sign, DateTimeOffset at)
        {
            if (site is not long place || place == 0) return;
            foreach (var l in lines[contractId])
                if (l.IsIncluded == offered)
                    moves.Add(new ContractMove(contractId, place, owner.Type, owner.Id, l.TypeId,
                                               sign * l.Quantity, l.IsSingleton, at));
        }

        // Whose hangar an id is and when its snapshot was taken, when it has one.
        bool Clock(long id, out (string Type, long Id) owner, out DateTimeOffset taken)
        {
            if (snapshot.TryGetValue(id, out var s)) { owner = (s.Type, id); taken = s.At; return true; }
            owner = default;
            taken = default;
            return false;
        }

        foreach (var g in rows.GroupBy(r => r.ContractId))
        {
            if (!lines.Contains(g.Key)) continue;

            var c      = g.First();
            var status = g.OrderBy(r => OrderFulfilmentService.Settledness(r.Status)).First().Status;

            // The goods came out of the corporation's hangar for a contract made on its behalf,
            // the issuer's own otherwise.
            var from      = g.Any(r => r.ForCorporation) ? c.IssuerCorporationId : c.IssuerId;
            var polled    = Clock(from, out var issuer, out var issuerTaken);
            var madeSince = polled && c.DateIssued > issuerTaken;

            // ── Made since the snapshot: the goods left with it ────────────────────────
            // Whatever it has come to since — outstanding, accepted, turned down — except deleted,
            // which put them back: the snapshot has them, and so does the hangar.
            if (madeSince && status != "deleted")
                Move(g.Key, c.StartLocationId, issuer, offered: true, -1, c.DateIssued);

            // ── Made before it, deleted since: the goods are back ──────────────────────
            // Only when the last sight of it still standing came after the snapshot. Earlier than
            // that, the snapshot may already hold them — see the class notes.
            var seenStanding = g.Where(r => r.Status == status).Max(r => r.StatusChangedAfter);
            if (polled && !madeSince && status == "deleted" && seenStanding > issuerTaken)
                Move(g.Key, c.StartLocationId, issuer, offered: true, +1, seenStanding.Value);

            if (status != "finished") continue;

            if (c.Type == "item_exchange" && g.Max(r => r.DateAccepted) is { } accepted)
            {
                // ── Accepted since: the offer reached the acceptor, and what it asked for went
                // the other way ─────────────────────────────────────────────────────────────
                // ⚠️ The acceptor is the corporation when it was accepted on the corporation's
                // behalf — 108 of the 213 accepted by us, measured — and the goods are then in
                // the corporation's hangar.
                if (g.Max(r => r.AcceptorId) is long by and not 0
                    && Clock(by, out var acceptor, out var acceptorTaken) && accepted > acceptorTaken)
                {
                    Move(g.Key, c.StartLocationId, acceptor, offered: true,  +1, accepted);
                    Move(g.Key, c.StartLocationId, acceptor, offered: false, -1, accepted);
                }
                if (polled && accepted > issuerTaken)
                    Move(g.Key, c.StartLocationId, issuer, offered: false, +1, accepted);
            }

            // ── A courier delivered since: the goods are the issuer's at the far end ───────
            if (c.Type == "courier" && polled && g.Max(r => r.DateCompleted) is { } delivered
                && delivered > issuerTaken)
                Move(g.Key, c.EndLocationId, issuer, offered: true, +1, delivered);
        }

        return moves.OrderByDescending(m => m.Units).ToList();
    }

    /// <summary>
    /// Up to when an owner's contracts were last seen as they stood: its previous successful
    /// contracts call, less the age of the copy that call could have been given. Recorded against
    /// a status change the moment a poll sees one — see
    /// <see cref="Models.ContractRecord.StatusChangedAfter"/>.
    ///
    /// <para>⚠️ Read before the poll stamps its own call, which is after the handler returns. And
    /// null when that previous call failed: a failed call is stamped as well, and a contract was
    /// not seen at all by it.</para>
    /// </summary>
    internal static async Task<DateTimeOffset?> LastSeenAsync(
        AppDbContext db, long ownerId, string ownerType, string endpoint, CancellationToken ct)
    {
        var last = await db.EsiCallRecords.AsNoTracking()
            .Where(r => r.OwnerId == ownerId && r.OwnerType == ownerType && r.Endpoint == endpoint)
            .Select(r => new { r.LastCalledAt, r.LastStatusCode })
            .FirstOrDefaultAsync(ct);
        return last is { LastStatusCode: >= 200 and < 300 } ? last.LastCalledAt - ContractsCache : null;
    }
}
