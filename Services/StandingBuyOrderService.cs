using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One standing buy order paired with whatever live order currently matches it.</summary>
public sealed record StandingBuyOrderRow(
    long   DbId,
    int    TypeId,
    string TypeName,
    long   LocationId,
    string LocationName,
    string MatchStatus,            // "matched" | "missing"
    string OwnerDisplay,
    string OwnerTooltip,           // per-order breakdown; empty when there is only one
    int    OrderCount,
    string PriceText,
    long   VolumeRemain,
    long   VolumeTotal,
    string RemainingText,
    string RemainingPercentText,
    double RemainingPercentValue,  // percent of the original volume still on the order; -1 when unmatched
    bool   IsLow,                  // remaining volume has fallen below the top-up threshold
    DateTimeOffset? ExpiresAt,     // earliest expiry across the matching orders
    string ExpiryText,
    double TimeRemainingPercentValue, // percent of the order's duration still to run; -1 when unmatched
    bool   IsExpiringSoon,
    decimal? OurBestPrice,         // highest of our own bids here
    decimal? CompetingBestBid,     // highest OTHER buy order at this station; null when nobody else is bidding
    bool   IsLocationTracked,      // false when this station isn't a configured market source
    string CompetingBidText,
    bool   IsOutbid,
    decimal? OutbidBy);

/// <summary>
/// Standing buy orders: the user declares a buy order they intend to keep up at a
/// station or structure, and this reports whether it is actually there.
///
/// Deliberately mirrors CorpActivityService's standing-projects handling — define
/// what should exist, match it against live data, report matched / missing plus how
/// much is left to run.
///
/// The live side is EsiMarketOrders, which only ever holds the user's own tracked
/// character and corp orders (it is populated from characters/{id}/orders/ and the
/// corp equivalent), so matching needs no owner filter — every row is theirs.
/// </summary>
public class StandingBuyOrderService(IDbContextFactory<AppDbContext> dbFactory,
                                     MarketCompetitionService competition)
{
    /// <summary>An order this far below its original volume wants topping up.
    /// Matches the threshold used for standing projects.</summary>
    public const double LowRemainingThresholdPercent = 10.0;

    /// <summary>An order with this little of its duration left wants renewing.
    /// Measured against the order's own duration, so a 90-day order gets 18 days'
    /// warning while a 7-day order gets a day and a half — the warning scales with
    /// how long the order was meant to run.</summary>
    public const double LowTimeThresholdPercent = 20.0;

    // ── CRUD ─────────────────────────────────────────────────────────────────

    public async Task<List<StandingBuyOrder>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.StandingBuyOrders
            .AsNoTracking()
            .OrderBy(o => o.TypeName).ThenBy(o => o.LocationName)
            .ToListAsync(ct);
    }

    /// <summary>Returns false when an entry for the same type and location already exists.</summary>
    public async Task<bool> AddAsync(StandingBuyOrder order, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var clash = await db.StandingBuyOrders.AnyAsync(
            o => o.TypeId == order.TypeId && o.LocationId == order.LocationId, ct);
        if (clash) return false;

        order.CreatedAt = DateTimeOffset.UtcNow;
        db.StandingBuyOrders.Add(order);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateAsync(StandingBuyOrder order, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.StandingBuyOrders.Update(order);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.StandingBuyOrders.Where(o => o.Id == id).ExecuteDeleteAsync(ct);
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Pair every standing definition with the live buy orders sitting at the same
    /// type and location.
    ///
    /// Multiple live orders can satisfy one definition — two characters both buying
    /// the same thing in the same station, for instance. Those are aggregated rather
    /// than duplicated into separate rows, since the question being asked is "is this
    /// order being maintained", not "who is maintaining it".
    /// </summary>
    public async Task<List<StandingBuyOrderRow>> BuildGridRowsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var standing = await db.StandingBuyOrders
            .AsNoTracking()
            .OrderBy(o => o.TypeName).ThenBy(o => o.LocationName)
            .ToListAsync(ct);

        if (standing.Count == 0) return [];

        // Live buy orders only. IsHistory rows are expired/filled orders kept for the
        // Sales Tracker and would otherwise resurrect a long-dead order as a match.
        var live = await db.EsiMarketOrders
            .AsNoTracking()
            .Where(o => o.IsBuyOrder && !o.IsHistory)
            .Select(o => new { o.OrderId, o.OwnerId, o.OwnerType, o.TypeId, o.LocationId,
                               o.Price, o.VolumeRemain, o.VolumeTotal,
                               o.Issued, o.Duration })
            .ToListAsync(ct);

        // ⚠ One ESI order can be stored twice. EsiMarketOrders is keyed on
        // (OwnerId, OwnerType, OrderId, IsHistory), and a corp order placed by one of
        // the user's characters comes back from BOTH characters/{id}/orders/ and the
        // corp endpoint — same OrderId, same price, same volume, same issue time.
        // Summing both would double every quantity, so dedupe on OrderId first.
        //
        // The corporation row wins where both exist: it is a corp order, and that is
        // the entity whose wallet holds the escrow.
        var deduped = live
            .GroupBy(o => o.OrderId)
            .Select(g => g.FirstOrDefault(o => o.OwnerType == "corporation") ?? g.First())
            .ToList();

        var byKey = deduped
            .GroupBy(o => (o.TypeId, o.LocationId))
            .ToDictionary(g => g.Key, g => g.ToList());

        // ── Competing bids from the public order book ────────────────────────
        //
        // Being outbid is invisible from our own orders alone: the order is present,
        // full and in date, and still gets no fills because someone is paying more.
        //
        // MarketRawOrders is the public book, and only covers locations set up as
        // market sources (Settings → Market). Somewhere untracked has to read as
        // "unknown", not "nobody is bidding" — the second would be a lie that reads
        // as good news.
        //
        // Our own orders are in the public book too, so they must be excluded or
        // every row would compare against itself and never look outbid.
        var typeIds = standing.Select(s => s.TypeId).Distinct().ToList();
        var locIds  = standing.Select(s => s.LocationId).Distinct().ToList();
        var ourOrderIds = live.Select(o => o.OrderId).ToHashSet();

        // Read through MarketCompetitionService: the worklist's inventory-level rules ask the
        // same question, and two implementations would eventually disagree about exactly the
        // two things above that are easy to get wrong.
        var bids = await competition.LoadBuyAsync(typeIds, locIds, ourOrderIds, ct);

        var charNames = await db.Characters.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        // Corporation.Id is int while MarketOrder.OwnerId is long, so widen the key
        // rather than casting at every lookup.
        var corpNames = await db.Corporations.AsNoTracking()
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name, ct);

        // The poller writes "corporation", not "corp" — getting this wrong silently
        // renders every corp order's owner as an unresolved id.
        string OwnerName(long id, string type) => type == "corporation"
            ? corpNames.GetValueOrDefault(id, $"Corp {id}")
            : charNames.GetValueOrDefault(id, $"#{id}");

        var rows = new List<StandingBuyOrderRow>(standing.Count);

        foreach (var sbo in standing)
        {
            var tracked  = bids.IsTracked(sbo.LocationId);
            var rivalBid = bids.BestBuy(sbo.TypeId, sbo.LocationId);

            // Shown even when our order is missing: knowing what the station is paying
            // is exactly what you need in order to place one.
            var rivalText = !tracked
                ? "—"
                : rivalBid is { } v ? $"{v:N2}" : "no other bids";

            if (!byKey.TryGetValue((sbo.TypeId, sbo.LocationId), out var matches) || matches.Count == 0)
            {
                rows.Add(new StandingBuyOrderRow(
                    DbId                 : sbo.Id,
                    TypeId               : sbo.TypeId,
                    TypeName             : sbo.TypeName,
                    LocationId           : sbo.LocationId,
                    LocationName         : sbo.LocationName,
                    MatchStatus          : "missing",
                    OwnerDisplay         : "",
                    OwnerTooltip         : "",
                    OrderCount           : 0,
                    PriceText            : "",
                    VolumeRemain         : 0,
                    VolumeTotal          : 0,
                    RemainingText        : "",
                    RemainingPercentText : "",
                    RemainingPercentValue: -1,
                    IsLow                : false,
                    ExpiresAt            : null,
                    ExpiryText           : "",
                    TimeRemainingPercentValue: -1,
                    IsExpiringSoon       : false,
                    OurBestPrice         : null,
                    CompetingBestBid     : rivalBid,
                    IsLocationTracked    : tracked,
                    CompetingBidText     : rivalText,
                    IsOutbid             : false,
                    OutbidBy             : null));
                continue;
            }

            long remain = matches.Sum(m => (long)m.VolumeRemain);
            long total  = matches.Sum(m => (long)m.VolumeTotal);
            var  pct    = total > 0 ? (double)remain / total * 100.0 : -1.0;

            // With several orders the prices usually differ, so show the best (highest)
            // bid — that is the one actually setting the buy price at that location.
            var bestPrice = matches.Max(m => m.Price);
            var priceText = matches.Count == 1
                ? $"{bestPrice:N2}"
                : $"{bestPrice:N2} (max of {matches.Count})";

            // Count distinct owners, not orders — one owner can hold several orders for
            // the same item at the same station, and reporting that as "2 owners" would
            // be wrong.
            var distinctOwners = matches
                .Select(m => (m.OwnerId, m.OwnerType))
                .Distinct()
                .ToList();

            var owner = distinctOwners.Count == 1
                ? OwnerName(distinctOwners[0].OwnerId, distinctOwners[0].OwnerType)
                  + (matches.Count > 1 ? $" ({matches.Count} orders)" : "")
                : $"{distinctOwners.Count} owners";

            // Per-order breakdown, so an aggregated row can be unpacked without
            // leaving the grid.
            var tooltip = matches.Count <= 1
                ? ""
                : string.Join("\n", matches
                    .OrderByDescending(m => m.Price)
                    .Select(m =>
                        $"{OwnerName(m.OwnerId, m.OwnerType)} — {m.Price:N2} ISK, "
                      + $"{m.VolumeRemain:N0}/{m.VolumeTotal:N0}, "
                      + $"expires {m.Issued.AddDays(m.Duration).ToLocalTime():yyyy-MM-dd}"));

            // Expiry. Where several orders back one declaration, the earliest is what
            // matters — it is the one that lapses first and leaves a gap.
            var now = DateTimeOffset.UtcNow;
            var soonest = matches
                .Select(m => new
                {
                    Expires  = m.Issued.AddDays(m.Duration),
                    Duration = m.Duration,
                })
                .OrderBy(x => x.Expires)
                .First();

            var expiresAt = soonest.Expires;
            var timePct   = soonest.Duration > 0
                ? Math.Max(0.0, (expiresAt - now).TotalDays / soonest.Duration * 100.0)
                : -1.0;

            var expiryText = FormatExpiry(expiresAt, now);
            if (matches.Count > 1) expiryText += " (first)";

            rows.Add(new StandingBuyOrderRow(
                DbId                 : sbo.Id,
                TypeId               : sbo.TypeId,
                TypeName             : sbo.TypeName,
                LocationId           : sbo.LocationId,
                LocationName         : sbo.LocationName,
                MatchStatus          : "matched",
                OwnerDisplay         : owner,
                OwnerTooltip         : tooltip,
                OrderCount           : matches.Count,
                PriceText            : priceText,
                VolumeRemain         : remain,
                VolumeTotal          : total,
                RemainingText        : $"{remain:N0} / {total:N0}",
                RemainingPercentText : pct >= 0 ? $"{pct:N1}%" : "",
                RemainingPercentValue: pct,
                IsLow                : pct >= 0 && pct < LowRemainingThresholdPercent,
                ExpiresAt            : expiresAt,
                ExpiryText           : expiryText,
                TimeRemainingPercentValue: timePct,
                IsExpiringSoon       : timePct >= 0 && timePct < LowTimeThresholdPercent,
                OurBestPrice         : bestPrice,
                CompetingBestBid     : rivalBid,
                IsLocationTracked    : tracked,
                CompetingBidText     : rivalText,
                IsOutbid             : rivalBid is { } rival && bestPrice < rival,
                OutbidBy             : rivalBid is { } r2 && bestPrice < r2 ? r2 - bestPrice : null));
        }

        return rows;
    }

    /// <summary>Absolute date plus how long is left, since "2026-08-14" alone doesn't
    /// say whether that is urgent.</summary>
    private static string FormatExpiry(DateTimeOffset expires, DateTimeOffset now)
    {
        var left = expires - now;
        if (left <= TimeSpan.Zero) return $"{expires.ToLocalTime():yyyy-MM-dd}  (expired)";

        var span = left.TotalDays >= 1
            ? $"{(int)left.TotalDays}d"
            : $"{(int)left.TotalHours}h";

        return $"{expires.ToLocalTime():yyyy-MM-dd}  ({span} left)";
    }

    /// <summary>Standing orders that are missing, nearly exhausted, nearly expired or
    /// outbid — the count worth surfacing elsewhere, as CountInactiveStandingProjectsAsync
    /// does for projects.</summary>
    public async Task<int> CountNeedingAttentionAsync(CancellationToken ct = default)
    {
        var rows = await BuildGridRowsAsync(ct);
        return rows.Count(r => r.MatchStatus == "missing" || r.IsLow || r.IsExpiringSoon || r.IsOutbid);
    }
}
