using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Buy orders the player has declared they intend to keep up, that are not currently doing
/// their job — missing, outbid, running low or about to expire.
///
/// Deliberately thin. <see cref="StandingBuyOrderService.BuildGridRowsAsync"/> already decides
/// what counts as needing attention, including the awkward part of excluding our own bids from
/// the competing-bid comparison. Re-deriving any of that here would give two answers to one
/// question, so this maps its rows and adds only what the worklist needs on top: routing to a
/// character, and an idea of how stale the numbers are.
/// </summary>
public class StandingBuyOrderGenerator(
    StandingBuyOrderService              standing,
    WorklistMarketAltService                  marketAlts,
    WorklistSettings                     settings,
    IDbContextFactory<AppDbContext>      dbFactory) : IWorklistGenerator
{
    public string Id          => "standing_buy";
    public string DisplayName => "Standing Buy Orders";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        var rows     = await standing.BuildGridRowsAsync(ct);
        var altMap  = await marketAlts.GetByLocationAsync(ct);
        var asOf     = await MarketDataAsOfAsync(ct);

        var items = new List<WorklistItem>();

        foreach (var r in rows)
        {
            // One item per standing order, not one per symptom. An order can be low and
            // expiring and outbid at once, but it is still a single trip to the market window.
            var (verb, detail, priority) = Diagnose(r, settings);
            if (verb is null) continue;

            altMap.TryGetValue(r.LocationId, out var alt);

            // No alt is a real blocker rather than a detail: the point of the list is knowing
            // which character to log in, and an item that cannot say is unfinished work.
            var blocked = alt is null;

            items.Add(new WorklistItem
            {
                Key           = $"standing_buy:{r.TypeId}:{r.LocationId}",
                Source        = Id,
                Kind          = WorklistKind.Buy,
                Title         = $"{verb} — {r.TypeName}",
                Detail        = detail,
                Readiness     = blocked ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                BlockedBy     = blocked ? "No character assigned to this location" : "",
                CharacterId   = alt?.CharacterId   ?? 0,
                CharacterName = alt?.CharacterName ?? "",
                LocationId    = r.LocationId,
                LocationName  = r.LocationName,
                TypeId        = r.TypeId,
                TypeName      = r.TypeName,
                Priority      = priority,
                DataAsOf      = asOf,
            });
        }

        return items;
    }

    /// <summary>
    /// The most severe thing wrong with one standing order, and how to say it. Returns a null
    /// verb when nothing is wrong, which is the common case.
    ///
    /// Ordered by how quietly each one fails. An outbid order looks healthy in every list —
    /// it exists, it has volume, it has time left — and buys nothing at all, so it goes first.
    /// A missing order at least announces itself by being absent.
    /// </summary>
    private static (string? Verb, string Detail, int Priority) Diagnose(
        StandingBuyOrderRow r, WorklistSettings settings)
    {
        if (r.IsOutbid && settings.RaiseOutbid)
        {
            var by = r.OutbidBy is { } b ? $" by {b:N2} ISK" : "";
            return ("Raise bid", $"Outbid{by} — best competing bid {r.CompetingBidText}. "
                               + $"Yours: {r.PriceText}.", WorklistPriority.Outbid);
        }

        if (r.MatchStatus == "missing" && settings.RaiseMissing)
        {
            // A station with no market source configured cannot be checked for competition, so
            // say so rather than letting silence read as "nobody else is bidding".
            var caveat = r.IsLocationTracked
                ? ""
                : " Competing bids unknown — this location is not a configured market source.";
            return ("Place order", $"No order found at {r.LocationName}.{caveat}", WorklistPriority.Missing);
        }

        if (r.IsLow && settings.RaiseLow)
            return ("Top up order",
                    $"{r.RemainingText} left ({r.RemainingPercentText} of the original volume).",
                    WorklistPriority.ForStock(r.RemainingPercentValue));

        if (r.IsExpiringSoon && settings.RaiseExpiring)
            return ("Re-place order", $"Expires {r.ExpiryText}.", WorklistPriority.Housekeeping);

        return (null, "", 0);
    }

    /// <summary>
    /// When the public order book behind the outbid check was last pulled. Approximate on
    /// purpose — one timestamp for the run rather than per location — but enough to stop the
    /// player acting on a number that is an hour old without knowing it.
    /// </summary>
    private async Task<DateTimeOffset?> MarketDataAsOfAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rows = await db.MarketRawOrders.AsNoTracking()
                .OrderByDescending(o => o.FetchedAt)
                .Select(o => o.FetchedAt)
                .Take(1)
                .ToListAsync(ct);
            return rows.Count > 0 ? rows[0] : null;
        }
        catch { return null; }
    }
}
