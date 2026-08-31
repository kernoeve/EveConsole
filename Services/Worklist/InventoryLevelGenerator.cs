using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Buy orders to place because a stockpile has run down.
///
/// This is the stockpile trader's side of acquisition: hold materials so production never waits
/// on the market, and top them up before they run out. A rule says "when this group falls below
/// X% of target, there should be a buy order at this station"; several rules can point at one
/// group, so falling further can add a second order at a trade hub without cancelling the first.
///
/// Availability comes from <see cref="InvLevelService.LoadAvailableAsync"/>, the same call the
/// Inventory Levels tool makes, so assets and job output are counted identically in both.
///
/// Its buy-order component is deliberately ignored, though. A group's include flags describe what
/// that tool should display; this asks a different question — "is there an order in place, and
/// does it cover the gap" — and the answer must not change because a display setting was toggled.
/// So stock means on hand plus in production, and orders are checked separately.
/// </summary>
public class InventoryLevelGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    InvLevelService                 invLevels,
    WorklistMarketAltService             marketAlts,
    MaterialSubstitutionService     substitution,
    MarketCompetitionService        competition,
    OutbidOrderService              outbidOrders) : IWorklistGenerator
{
    public string Id          => "inventory_levels";
    public string DisplayName => "Inventory Levels";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Build rules belong to the industry generator. Without this a component group would be
        // told to buy and to manufacture the same shortfall.
        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action != "Build")
            .ToListAsync(ct);
        if (rules.Count == 0) return [];

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var altMap = await marketAlts.GetByLocationAsync(ct);

        // Our own live buy orders, deduped the way StandingBuyOrderService does: a corp order
        // placed by one of our characters comes back from both the character and corp endpoints
        // with the same OrderId, and summing both would double every volume.
        var ourOrders = (await db.EsiMarketOrders.AsNoTracking()
                .Where(o => o.IsBuyOrder && !o.IsHistory)
                .Select(o => new { o.OrderId, o.OwnerType, o.TypeId, o.LocationId, o.Price, o.VolumeRemain })
                .ToListAsync(ct))
            .GroupBy(o => o.OrderId)
            .Select(g => g.FirstOrDefault(o => o.OwnerType == "corporation") ?? g.First())
            .ToList();

        // ⚠️ Every open order for the type, wherever it sits — not just orders at the rule's own
        // station. An order is material already bought, and where it fills is a hauling question,
        // not a reason to spend the ISK a second time. A rule that buys at a trade hub and
        // measures its stock somewhere else saw none of its own orders under a station match.
        var onOrderAnywhere = ourOrders
            .GroupBy(o => o.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(o => (long)o.VolumeRemain));

        // Material already held in a form that becomes this one. Same reasoning as
        // MaterialPurchaseGenerator, which has always done it: 200,000 Compressed Fullerite-C28
        // sitting in the rule's own region is 190,000 units of gas the rule was asking to buy
        // again, and the two generators merge into one row, so only one of them saw it.
        var subs = await substitution.LoadAsync(ct);

        var items = new List<WorklistItem>();

        // Types a rule would still be short of on stock alone. An order buying one of these is
        // buying something wanted, so its being outbid is a failure worth a task; an order for
        // anything else is not this list's business, however it is priced. Collected across the
        // rules and reported once at the end — two rules on one group both reach the same
        // conclusion, and deciding it inside the loop would say it twice.
        var needed = new HashSet<int>();

        foreach (var ruleGroup in rules.GroupBy(r => r.GroupId))
        {
            if (!groups.TryGetValue(ruleGroup.Key, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id)
                .ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();

            // ⚠️ Blueprints are not raised here. MaterialPurchaseGenerator totals a print's stock
            // target together with what queued jobs need and subtracts what is owned once, because
            // one pile of copies cannot be spent twice — see PrintTasks. Raising the stock half
            // here as well would put the same target on the list a second time, and subtract the
            // same copies a second time to hide it.
            var bpTypeIds = await KillmailValuation.BlueprintTypeIdsAsync(db, typeIds, ct);

            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);
            var names   = await invLevels.GetTypeNamesAsync(typeIds, ct);
            var subHeld = await SubstituteStockAsync(db, subs, typeIds, onOrderAnywhere, ct);

            var locIds = ruleGroup.Select(r => r.LocationId).Distinct().ToList();
            var bids   = await competition.LoadBuyAsync(
                             typeIds, locIds, ourOrders.Select(o => o.OrderId).ToHashSet(), ct);

            foreach (var rule in ruleGroup)
            {
                altMap.TryGetValue(rule.LocationId, out var alt);

                foreach (var gi in groupItems)
                {
                    if (bpTypeIds.Contains(gi.TypeId)) continue;   // totalled with job demand instead

                    avail.TryGetValue(gi.TypeId, out var av);
                    var need = InvRuleShortfall.For(rule, group, gi, av);
                    if (need is null) continue;

                    var mine = ourOrders
                        .Where(o => o.TypeId == gi.TypeId && o.LocationId == rule.LocationId)
                        .ToList();
                    var onOrder = mine.Sum(o => (long)o.VolumeRemain);

                    // The shared figure less everything already incoming: orders anywhere, and
                    // stock held in a form that becomes this one. An order or a hold that covers
                    // the gap means no task — the material is bought, it is just not here yet.
                    var ordered = onOrderAnywhere.GetValueOrDefault(gi.TypeId);
                    var held    = subHeld.GetValueOrDefault(gi.TypeId);
                    var shortfall = need.Shortfall - ordered - held.Units;

                    var name = names.GetValueOrDefault(gi.TypeId, $"Type {gi.TypeId}");

                    // ⚠️ Everything except the orders. What is left is whether this rule would
                    // still be short if no order existed — which is exactly the question of
                    // whether its orders are buying anything wanted. Subtracting them too would
                    // ask a healthy order to justify itself and a failing one to prove it had
                    // already failed.
                    if (need.Shortfall - held.Units > 0) needed.Add(gi.TypeId);

                    // Below threshold, but what is already bought covers the gap. The order that
                    // covers it may still be losing, which is now a separate task rather than a
                    // replacement for this one — a bid to raise and a shortfall to order are two
                    // different actions, and one is not evidence against the other.
                    if (shortfall <= 0) continue;

                    var stock = need.StockText;
                    // "Here" and "elsewhere" kept apart so the detail says where the incoming
                    // material actually is; both count the same against the shortfall.
                    var away  = ordered - onOrder;
                    var order = (onOrder > 0 ? $", {onOrder:N0} on order here" : "")
                              + (away    > 0 ? $", {away:N0} on order elsewhere" : "")
                              + held.Note;
                    var fill  = need.FillText(rule);

                    // The name leads. The column sorts on this string, so a leading verb sorted
                    // every shortfall in the list under "P" for "Place order".
                    var title    = $"{name} × {shortfall:N0}";
                    var detail   = $"{stock}{order}.{fill} Short {shortfall:N0}."
                                 + (bids.IsTracked(rule.LocationId)
                                      ? ""
                                      : " Competing bids unknown — this location is not a configured market source.");
                    var priority = WorklistPriority.ForStock(need.Percent);
                    // Only a shortfall is an amount to acquire, and only an amount can be added
                    // to what another source wants of the same thing at the same station.
                    var mergeKey = WorklistItem.BuyMergeKey(rule.LocationId, gi.TypeId);

                    var blocked = alt is null;

                    items.Add(new WorklistItem
                    {
                        // Rule id, not group id: two rules on one group are two separate pieces
                        // of work at two stations, and they must snooze independently.
                        Key           = $"inv_level:{rule.Id}:{gi.TypeId}",
                        Source        = Id,
                        Kind          = WorklistKind.Buy,
                        Title         = title,
                        Detail        = $"{group.Name} · below {rule.ThresholdPercent:0.#}% · {detail}",
                        Quantity      = shortfall,
                        MergeKey      = mergeKey,
                        // Both halves of the subtraction, so merging with a job's demand for the
                        // same material nets the shared stock once instead of once per demand.
                        GrossDemand    = need.Wanted,
                        SupplyCredited = need.Have + ordered + held.Units,
                        Readiness     = blocked ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                        BlockedBy     = blocked ? "No character assigned to this location" : "",
                        CharacterId   = alt?.CharacterId   ?? 0,
                        CharacterName = alt?.CharacterName ?? "",
                        LocationId    = rule.LocationId,
                        LocationName  = rule.LocationName,
                        TypeId        = gi.TypeId,
                        TypeName      = name,
                        Priority      = priority,
                    });
                }
            }
        }

        // Orders failing to buy what these rules still want. Independent of the tasks above: an
        // order can be losing while its shortfall is covered, and a shortfall can need a second
        // order placed while the first is also underbid.
        items.AddRange((await outbidOrders.FindAsync(needed, ct)).Select(OutbidOrderService.Task));

        return items;
    }

    /// <summary>
    /// How much of each stocking target is already covered in an unrefined form, and by what.
    ///
    /// <para>The mirror of <c>MaterialPurchaseGenerator.SubstituteStockAsync</c>, and deliberately
    /// so: the two generators raise buys for the same type at the same station and merge into one
    /// row, so a supply only one of them can see makes the merged total wrong. Compressed gas was
    /// the case that showed it — the materials half credited it, the stocking half did not, and
    /// the row asked for both answers added together.</para>
    ///
    /// <para>⚠️ Unscoped on purpose, orders included. Material held or bought in another form is
    /// material that does not need buying again, wherever it currently sits; moving it is the
    /// hauling generator's job. This is the same line <see cref="MaterialSubstitutionService"/>
    /// draws — it stops a second purchase, it does not claim the stock is in the right place.</para>
    /// </summary>
    private static async Task<Dictionary<int, (long Units, string Note)>> SubstituteStockAsync(
        AppDbContext db, Dictionary<int, List<Substitute>> subs, List<int> typeIds,
        Dictionary<int, long> onOrder, CancellationToken ct)
    {
        var wanted = typeIds.Where(subs.ContainsKey).ToList();
        if (wanted.Count == 0) return [];

        var sourceIds = wanted.SelectMany(w => subs[w]).Select(s => s.SourceTypeId).Distinct().ToList();

        var held = await db.EsiAssets.AsNoTracking()
            .Where(a => sourceIds.Contains(a.TypeId))
            .GroupBy(a => a.TypeId)
            .Select(g => new { TypeId = g.Key, Qty = g.Sum(a => (long)a.Quantity) })
            .ToDictionaryAsync(x => x.TypeId, x => x.Qty, ct);

        var result = new Dictionary<int, (long, string)>();

        foreach (var typeId in wanted)
        {
            long total = 0;
            var  from  = new List<string>();

            // Each source counts in full against every product it yields. One batch of ice gives
            // all of its outputs at once, so there is nothing to apportion between them.
            foreach (var s in subs[typeId].OrderBy(s => s.SourceName))
            {
                var have  = held.GetValueOrDefault(s.SourceTypeId);
                var due   = onOrder.GetValueOrDefault(s.SourceTypeId);
                var units = have + due;
                if (units <= 0) continue;

                var gives = s.From(units);
                if (gives <= 0) continue;

                total += gives;
                from.Add(due > 0
                    ? $"{have:N0} {s.SourceName} and {due:N0} on order"
                    : $"{units:N0} {s.SourceName}");
            }

            if (total <= 0) continue;

            result[typeId] = (total,
                $", {total:N0} recoverable from " + string.Join(", ", from.Take(3))
                + (from.Count > 3 ? $" and {from.Count - 3} more" : ""));
        }

        return result;
    }
}
