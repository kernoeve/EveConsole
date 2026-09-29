using EveConsole.Data;
using Microsoft.EntityFrameworkCore;
using EveConsole.Localization;

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
    /// <summary>
    /// What a standing order needs doing.
    ///
    /// <para>⚠️ A key rather than the words shown. The routing below has to tell placing an order
    /// apart from changing one already placed, and that must not depend on how the title reads in
    /// the interface's language — the title is looked up from this, never compared.</para>
    /// </summary>
    private enum Fix { RaiseBid, PlaceOrder, TopUp, RePlace }

    public string Id          => "standing_buy";
    public string DisplayName => WorklistText.SourceStandingBuyOrders;

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
            var (fix, detail, priority) = Diagnose(r, settings);
            if (fix is not { } action) continue;

            altMap.TryGetValue(r.LocationId, out var alt);

            // ── Who has to log in ─────────────────────────────────────────────
            //
            // ⚠️ An order that already exists is changed by the character who placed it, and
            // by nobody else. Routing these to the market alt assigned to the station sent
            // "raise the bid" to a character with no such order — somebody else's order, and
            // the named one cannot touch its price.
            //
            // PlacedById, not OwnerId: a corp order is held by the corporation, so its owner is
            // a wallet rather than a person, but it was still placed by one of our characters
            // and that is who has to log in.
            //
            // Only for the verbs that act on an order that exists. "Place order" is the opposite
            // case: nothing has been placed, so the station's alt is exactly who should.
            var placedBy  = action == Fix.PlaceOrder ? 0 : r.PlacedById;
            var ownerName = placedBy > 0 ? r.PlacedByName : "";
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
                Title         = TitleOf(action, r.TypeName),
                Detail        = detail,
                Readiness     = blocked ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                BlockedBy     = blocked ? WorklistText.BlockedNoCharacterAtLocation : "",
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

    /// <summary>The item, then what to do about its order — name first, so the column sorts by
    /// item. One whole title per fix, since other languages may place the verb differently.</summary>
    private static string TitleOf(Fix fix, string typeName) => string.Format(fix switch
    {
        Fix.RaiseBid   => WorklistText.TitleRaiseBid,
        Fix.PlaceOrder => WorklistText.TitlePlaceOrder,
        Fix.TopUp      => WorklistText.TitleTopUpOrder,
        _              => WorklistText.TitleReplaceOrder,
    }, typeName);

    /// <summary>
    /// The most severe thing wrong with one standing order, and how to say it. Returns a null
    /// fix when nothing is wrong, which is the common case.
    ///
    /// Ordered by how quietly each one fails. An outbid order looks healthy in every list —
    /// it exists, it has volume, it has time left — and buys nothing at all, so it goes first.
    /// A missing order at least announces itself by being absent.
    /// </summary>
    private static (Fix? Fix, string Detail, int Priority) Diagnose(
        StandingBuyOrderRow r, WorklistSettings settings)
    {
        if (r.IsOutbid && settings.RaiseOutbid)
            return (Fix.RaiseBid,
                    r.OutbidBy is { } b
                        ? string.Format(WorklistText.StandingOutbidBy, b, r.CompetingBidText, r.PriceText)
                        : string.Format(WorklistText.StandingOutbid, r.CompetingBidText, r.PriceText),
                    WorklistPriority.Outbid);

        if (r.MatchStatus == "missing" && settings.RaiseMissing)
        {
            // A station with no market source configured cannot be checked for competition, so
            // say so rather than letting silence read as "nobody else is bidding".
            var missing = string.Format(WorklistText.StandingNoOrderAt, r.LocationName);
            return (Fix.PlaceOrder,
                    r.IsLocationTracked ? missing : missing + " " + WorklistText.CompetingBidsUnknown,
                    WorklistPriority.Missing);
        }

        if (r.IsLow && settings.RaiseLow)
            return (Fix.TopUp,
                    string.Format(WorklistText.StandingRunningLow, r.RemainingText, r.RemainingPercentText),
                    WorklistPriority.ForStock(r.RemainingPercentValue));

        if (r.IsExpiringSoon && settings.RaiseExpiring)
            return (Fix.RePlace, string.Format(WorklistText.StandingExpires, r.ExpiryText),
                    WorklistPriority.Housekeeping);

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
            // ⚠️ Distinct values, newest picked here, not ORDER BY in SQL: a DateTimeOffset in
            // ORDER BY throws on SQLite, and the catch below turned that into "no stamp" on
            // every SQLite install. One value per fetch batch, so the list is short.
            var stamps = await db.MarketRawOrders.AsNoTracking()
                .Select(o => o.FetchedAt)
                .Distinct()
                .ToListAsync(ct);
            return stamps.Count > 0 ? stamps.Max() : null;
        }
        catch { return null; }
    }
}
