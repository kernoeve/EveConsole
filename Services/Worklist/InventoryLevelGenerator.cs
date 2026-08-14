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
    MarketCompetitionService        competition) : IWorklistGenerator
{
    public string Id          => "inventory_levels";
    public string DisplayName => "Inventory Levels";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled)
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

        var items = new List<WorklistItem>();

        foreach (var ruleGroup in rules.GroupBy(r => r.GroupId))
        {
            if (!groups.TryGetValue(ruleGroup.Key, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id)
                .ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();
            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);
            var names   = await invLevels.GetTypeNamesAsync(typeIds, ct);

            var locIds = ruleGroup.Select(r => r.LocationId).Distinct().ToList();
            var bids   = await competition.LoadBuyAsync(
                             typeIds, locIds, ourOrders.Select(o => o.OrderId).ToHashSet(), ct);

            foreach (var rule in ruleGroup)
            {
                altMap.TryGetValue(rule.LocationId, out var alt);

                foreach (var gi in groupItems)
                {
                    var target = (long)gi.TargetQuantity * Math.Max(1, group.Multiplier);
                    if (target <= 0) continue;

                    // Stock is what exists: on hand, plus in production. Buy orders are excluded
                    // even when the group counts them — the group's include flags say what the
                    // Inventory Levels tool should display, and this is a different question.
                    // Here an order is not stock; it is the thing that may make the shortfall
                    // unnecessary, and it is subtracted as such immediately below. Reading the
                    // group's flag would also make the answer depend on how the station sits
                    // relative to the group's scope, which has nothing to do with the question.
                    var a    = avail.TryGetValue(gi.TypeId, out var av) ? av : null;
                    var have = (a?.Assets ?? 0) + (a?.IndustryJobs ?? 0);
                    if (have >= target * (rule.ThresholdPercent / 100.0)) continue;

                    var mine = ourOrders
                        .Where(o => o.TypeId == gi.TypeId && o.LocationId == rule.LocationId)
                        .ToList();
                    var onOrder = mine.Sum(o => (long)o.VolumeRemain);

                    var wanted    = (long)Math.Ceiling(target * (rule.FillTargetPercent / 100.0));
                    var shortfall = wanted - have - onOrder;

                    var name = names.GetValueOrDefault(gi.TypeId, $"Type {gi.TypeId}");
                    var pct  = target > 0 ? have * 100.0 / target : 0;

                    // Being outbid is worth saying even when the shortfall is covered: the order
                    // exists, looks healthy, and is buying nothing.
                    var rival = bids.BestBuy(gi.TypeId, rule.LocationId);
                    var best  = mine.Count > 0 ? mine.Max(o => o.Price) : (decimal?)null;
                    var outbid = best is { } b && rival is { } r && b < r;

                    string verb, detail;
                    int priority;

                    // Stock and target alone do not explain the shortfall once a fill target
                    // above or below 100% is involved — "5,607 of 10,000, short 5,393" reads as
                    // an arithmetic error until the 110% is on the row too. So say what is being
                    // filled to whenever it is not simply the target.
                    var stock = $"stock {have:N0} of {target:N0} ({pct:0.#}%)";
                    var order = onOrder > 0 ? $", {onOrder:N0} on order here" : "";
                    var fill  = Math.Abs(rule.FillTargetPercent - 100) < 0.05
                        ? ""
                        : $" Filling to {rule.FillTargetPercent:0.#}% ({wanted:N0}).";

                    if (outbid)
                    {
                        verb     = "Raise bid";
                        detail   = $"{stock}{order}. Outbid — best bid {rival:N2} ISK, "
                                 + $"yours {best:N2} ISK.";
                        priority = 100;
                    }
                    else if (shortfall > 0)
                    {
                        verb     = onOrder > 0 ? "Increase buy order" : "Place buy order";
                        detail   = $"{stock}{order}.{fill} Short {shortfall:N0}."
                                 + (bids.IsTracked(rule.LocationId)
                                      ? ""
                                      : " Competing bids unknown — this location is not a configured market source.");
                        priority = 80;
                    }
                    else continue;   // below threshold, but existing orders already cover it

                    var blocked = alt is null;

                    items.Add(new WorklistItem
                    {
                        // Rule id, not group id: two rules on one group are two separate pieces
                        // of work at two stations, and they must snooze independently.
                        Key           = $"inv_level:{rule.Id}:{gi.TypeId}",
                        Source        = Id,
                        Title         = $"{verb} — {name}",
                        Detail        = $"{group.Name} · below {rule.ThresholdPercent:0.#}% · {detail}",
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

        return items;
    }
}
