using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// What outstanding customer orders need bought.
///
/// The build-to-order counterpart to the inventory-level rules: nothing is held on the shelf, so
/// the order itself drives acquisition. An order says how many of what — no target has to be
/// inferred — and the chain runs backwards from there: subtract what is already built, subtract
/// what is already in the ovens, plan the remainder against a park, and report the raw materials
/// that plan cannot cover from assets.
///
/// The planning is <see cref="ProductionCalculatorService"/>'s, unchanged. Quantities depend on
/// facility rigs and ME, and a second implementation of that arithmetic would quietly disagree
/// with the Production Calculator about what an order actually costs to fill.
/// </summary>
public class TrackedOrderGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    ProductionCalculatorService     production,
    WorklistMarketAltService        marketAlts,
    AppErrorLogger                  errorLogger) : IWorklistGenerator
{
    public string Id          => "tracked_orders";
    public string DisplayName => "Customer Orders";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rules = await db.WorklistOrderRules.AsNoTracking().Where(r => r.Enabled).ToListAsync(ct);
        if (rules.Count == 0) return [];

        var orders = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Status == "pending")
            .ToListAsync(ct);
        if (orders.Count == 0) return [];

        // What the orders ask for, before anything is netted off.
        var demand = orders.GroupBy(o => o.TypeId)
            .ToDictionary(g => g.Key, g => (long)g.Sum(o => o.Units));

        var wantedTypes = demand.Keys.ToList();

        // Already built and sitting somewhere.
        var onHand = await db.EsiAssets.AsNoTracking()
            .Where(a => wantedTypes.Contains(a.TypeId))
            .GroupBy(a => a.TypeId)
            .Select(g => new { TypeId = g.Key, Qty = g.Sum(a => (long)a.Quantity) })
            .ToDictionaryAsync(x => x.TypeId, x => x.Qty, ct);

        // Already in the ovens. ApplyAvailabilityAsync only knows about assets, so without this
        // an order whose build is halfway done would be planned and bought for a second time.
        //
        // The same three statuses the poller treats as live. "ready" matters most here: the job
        // has finished and consumed its materials, but the product is not in assets until it is
        // collected — counting only "active" would miss exactly the units most likely to be
        // sitting there, and buy them again. "paused" will still produce eventually. "delivered"
        // is excluded because those units are in assets already and counted as on hand.
        var inBuild = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => (j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                            && j.ProductTypeId != null
                            && wantedTypes.Contains(j.ProductTypeId!.Value))
                .Select(j => new { j.ProductTypeId, j.Runs })
                .ToListAsync(ct))
            .GroupBy(j => j.ProductTypeId!.Value)
            .ToDictionary(g => g.Key, g => (long)g.Sum(j => j.Runs));

        var queue = new List<ProductionQueueEntry>();
        var names = await TypeNamesAsync(db, wantedTypes, ct);

        foreach (var (typeId, qty) in demand)
        {
            var outstanding = qty
                            - onHand.GetValueOrDefault(typeId)
                            - inBuild.GetValueOrDefault(typeId);
            if (outstanding <= 0) continue;

            queue.Add(new ProductionQueueEntry
            {
                TypeId   = typeId,
                TypeName = names.GetValueOrDefault(typeId, $"Type {typeId}"),
                Quantity = (int)Math.Min(int.MaxValue, outstanding),
                MeLevel  = await production.GetDefaultMeAsync(typeId, ct),
            });
        }

        if (queue.Count == 0) return [];

        var items = new List<WorklistItem>();

        foreach (var rule in rules)
        {
            List<PlanRawMaterial> shortfalls;
            try
            {
                var plan = await production.CalculateAsync(queue, rule.ParkId, ct: ct);

                // Assets mode, not Station: this asks "do I own the materials anywhere", which
                // is the right question when the answer decides whether to buy. Where they need
                // to end up is a hauling problem, and a separate piece of work.
                await production.ApplyAvailabilityAsync(plan, ProductionCalculatorService.MissingMode.Assets, ct);

                shortfalls = plan.RawMaterials.Where(r => r.Missing > 0).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A park that cannot be planned is worth reporting once, not per material.
                errorLogger.Log("TrackedOrderGenerator", $"Park {rule.ParkId}", ex);
                continue;
            }

            var altMap = await marketAlts.GetByLocationAsync(ct);
            altMap.TryGetValue(rule.LocationId, out var alt);
            var blocked = alt is null;

            var orderCount = queue.Count;

            foreach (var raw in shortfalls)
            {
                items.Add(new WorklistItem
                {
                    // Rule id, so two rules buying for the same park at different stations stay
                    // separate pieces of work with independent snoozes.
                    Key           = $"tracked_order:{rule.Id}:{raw.TypeId}",
                    Source        = Id,
                    Title         = $"Buy — {raw.TypeName}",
                    Detail        = $"{orderCount} pending order(s) need {raw.Quantity:N0}; "
                                  + $"{raw.Available:N0} on hand — short {raw.Missing:N0}.",
                    Readiness     = blocked ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                    BlockedBy     = blocked ? "No market alt assigned to this location" : "",
                    CharacterId   = alt?.CharacterId   ?? 0,
                    CharacterName = alt?.CharacterName ?? "",
                    LocationId    = rule.LocationId,
                    LocationName  = rule.LocationName,
                    TypeId        = raw.TypeId,
                    TypeName      = raw.TypeName,
                    Priority      = 60,
                });
            }
        }

        return items;
    }

    private static async Task<Dictionary<int, string>> TypeNamesAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
}
