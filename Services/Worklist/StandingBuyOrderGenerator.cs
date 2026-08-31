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
    /// <summary>The one verb that is about an order not existing yet. Named because the routing
    /// above has to tell it apart from the verbs that change an order already placed.</summary>
    private const string PlaceOrderVerb = "Place order";

    public string Id          => "standing_buy";
    public string DisplayName => "Standing Buy Orders";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        var rows     = await standing.BuildGridRowsAsync(ct);
        var altMap  = await marketAlts.GetByLocationAsync(ct);
        var asOf     = await MarketDataAsOfAsync(ct);

        // Names for the characters holding these orders. Only personal orders: a corp order is
        // not one character's to change, and OwnerId is already zero where several owners back
        // one declaration.
        var ownerIds = rows.Where(r => r.OwnerId > 0 && r.OwnerType != "corporation")
                           .Select(r => r.OwnerId)
                           .Distinct()
                           .ToList();

        var ownerNames = new Dictionary<long, string>();
        if (ownerIds.Count > 0)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            ownerNames = await db.Characters.AsNoTracking()
                .Where(c => ownerIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        }

        var items = new List<WorklistItem>();

        foreach (var r in rows)
        {
            // One item per standing order, not one per symptom. An order can be low and
            // expiring and outbid at once, but it is still a single trip to the market window.
            var (verb, detail, priority) = Diagnose(r, settings);
            if (verb is null) continue;

            altMap.TryGetValue(r.LocationId, out var alt);

            // ── Who has to log in ─────────────────────────────────────────────
            //
            // ⚠️ An order that already exists is changed by the character who placed it, not
            // by whoever is assigned to that station. Naming the station's alt sent "raise the
            // bid on Compressed Kylixium IV-Grade" to a character with no such order — the
            // order was another alt's, and the named one cannot touch its price.
            //
            // Only for the verbs that act on an existing order. "Place order" is the opposite
            // case: nothing has been placed, so the station's alt is exactly who should.
            //
            // Corp orders keep the alt too. OwnerId is zero for them here, and the tool already
            // treats a corp order as actionable by anyone holding the role — see
            // OutbidOrderService.Task, which reaches the same conclusion from the other side.
            var placedBy  = verb == PlaceOrderVerb ? 0 : r.OwnerId;
            var ownerName = placedBy > 0 ? ownerNames.GetValueOrDefault(placedBy, "") : "";
            var named     = ownerName.Length > 0;

            // No character at all is a real blocker rather than a detail: the point of the list
            // is knowing which character to log in, and an item that cannot say is unfinished
            // work.
            var blocked = !named && alt is null;

            items.Add(new WorklistItem
            {
                Key           = $"standing_buy:{r.TypeId}:{r.LocationId}",
                Source        = Id,
                Kind          = WorklistKind.Buy,
                // Name first so the column sorts by item. These carry no quantity — they are
                // about the state of a standing order, not an amount to acquire — so the verb
                // stays, trailing, rather than being replaced by a count there is none of.
                Title         = $"{r.TypeName} — {verb.ToLowerInvariant()}",
                Detail        = detail,
                Readiness     = blocked ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                BlockedBy     = blocked ? "No character assigned to this location" : "",
                CharacterId   = named ? placedBy  : alt?.CharacterId   ?? 0,
                CharacterName = named ? ownerName : alt?.CharacterName ?? "",
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
            return (PlaceOrderVerb, $"No order found at {r.LocationName}.{caveat}", WorklistPriority.Missing);
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
