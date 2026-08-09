using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One standing buy order paired with whatever live order currently matches it.</summary>
public sealed record StandingBuyOrderRow(
    long   DbId,
    int    TypeId,
    string TypeName,
    string LocationName,
    string MatchStatus,            // "matched" | "missing"
    string OwnerDisplay,
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
    bool   IsExpiringSoon);

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
public class StandingBuyOrderService(IDbContextFactory<AppDbContext> dbFactory)
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
            .Select(o => new { o.OwnerId, o.OwnerType, o.TypeId, o.LocationId,
                               o.Price, o.VolumeRemain, o.VolumeTotal,
                               o.Issued, o.Duration })
            .ToListAsync(ct);

        var byKey = live
            .GroupBy(o => (o.TypeId, o.LocationId))
            .ToDictionary(g => g.Key, g => g.ToList());

        var charNames = await db.Characters.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        // Corporation.Id is int while MarketOrder.OwnerId is long, so widen the key
        // rather than casting at every lookup.
        var corpNames = await db.Corporations.AsNoTracking()
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name, ct);

        string OwnerName(long id, string type) => type == "corp"
            ? corpNames.GetValueOrDefault(id, $"Corp {id}")
            : charNames.GetValueOrDefault(id, $"#{id}");

        var rows = new List<StandingBuyOrderRow>(standing.Count);

        foreach (var sbo in standing)
        {
            if (!byKey.TryGetValue((sbo.TypeId, sbo.LocationId), out var matches) || matches.Count == 0)
            {
                rows.Add(new StandingBuyOrderRow(
                    DbId                 : sbo.Id,
                    TypeId               : sbo.TypeId,
                    TypeName             : sbo.TypeName,
                    LocationName         : sbo.LocationName,
                    MatchStatus          : "missing",
                    OwnerDisplay         : "",
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
                    IsExpiringSoon       : false));
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

            var owner = matches.Count == 1
                ? OwnerName(matches[0].OwnerId, matches[0].OwnerType)
                : $"{matches.Select(m => m.OwnerId).Distinct().Count()} owners";

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
                LocationName         : sbo.LocationName,
                MatchStatus          : "matched",
                OwnerDisplay         : owner,
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
                IsExpiringSoon       : timePct >= 0 && timePct < LowTimeThresholdPercent));
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

    /// <summary>Standing orders that are missing, nearly exhausted or nearly expired —
    /// the count worth surfacing elsewhere, as CountInactiveStandingProjectsAsync does
    /// for projects.</summary>
    public async Task<int> CountNeedingAttentionAsync(CancellationToken ct = default)
    {
        var rows = await BuildGridRowsAsync(ct);
        return rows.Count(r => r.MatchStatus == "missing" || r.IsLow || r.IsExpiringSoon);
    }
}
