using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Everything asking for one item to be built, and who is asking.</summary>
/// <param name="Priority">The most urgent contributor's. A customer order sitting alongside a
/// stock top-up makes the whole build a customer order's worth of urgent, because the order is
/// not served until the last unit is.</param>
public sealed record BuildDemand(int TypeId, long Units, int Priority, List<string> Reasons)
{
    public string Head => string.Join(" + ", Reasons);
}

/// <summary>Everything in scope, wherever it sits and whoever owns it.</summary>
public sealed record ScopeStock(
    Dictionary<int, long>               Corp,
    Dictionary<(int TypeId, long OwnerId), long> Personal)
{
    public long Reachable(int typeId, WorklistIndyChar cfg)
    {
        long total = 0;
        if (cfg.IncludeCorpAssets)     total += Corp.GetValueOrDefault(typeId);
        if (cfg.IncludePersonalAssets) total += Personal.GetValueOrDefault((typeId, cfg.CharacterId));
        return total;
    }

    /// <summary>
    /// Everything in scope regardless of whose it is, for netting demand rather than deciding
    /// whether one character can start one job. A sub-assembly with no inventory rule has no
    /// group availability to fall back on, and counting only one alt's reach would ask for a
    /// second batch of something the corp already holds.
    /// </summary>
    public long Anywhere(int typeId) =>
        Corp.GetValueOrDefault(typeId)
        + Personal.Where(kv => kv.Key.TypeId == typeId).Sum(kv => kv.Value);
}

/// What industry has to build, from every demand at once.
///
/// <para>Shared, because two tools have to agree about it. The job generator turns this into jobs
/// and the logistics generator moves the materials those jobs will eat — and when each worked it
/// out separately they disagreed the moment either changed, leaving jobs suggested with no
/// materials hauled for them.</para>
/// </summary>
public class IndustryDemandService(
    InvLevelService             invLevels,
    ProductionCalculatorService production)
{
    /// <summary>Everything one item is wanted for, before anything is netted off.</summary>
    private sealed class Gross
    {
        /// <summary>What the inventory rules say should be sitting on the shelf.</summary>
        public long Level;

        /// <summary>Units customers have ordered, and units parent builds will eat.</summary>
        public long Consumed;

        /// <summary>Stock and in-flight production, counted once however many demands there are.</summary>
        public long Have;

        /// <summary>True when a rule's threshold has tripped, or something is waiting on it. A
        /// level sitting comfortably full raises nothing; a level about to be eaten does.</summary>
        public bool Fires;

        public int          Priority = WorklistPriority.Housekeeping;
        public List<string> Reasons  = [];
    }

    /// <summary>
    /// What has to be built, from every demand at once.
    ///
    /// <para><b>Gross demand pooled, supply netted once.</b> The alternative — each source
    /// netting stock against its own figure — double-counts the shelf. Radar-FTL Interlink
    /// Communicators sat at 268 with a standing level of 50 and two Avatars needing 446 of them:
    /// against the level alone there was no shortfall, against the Avatars alone the 268 covered
    /// most of it, and either way the tool asked for far too few. Held to 50 and consumed 446,
    /// the real answer is 496 less 268, so build 228.</para>
    ///
    /// <para><b>Demand cascades.</b> A parent build's requirement for a sub-assembly is demand
    /// for that sub-assembly. Without this the tool planned an Avatar, bought its raw materials,
    /// and never mentioned the fifty-odd component jobs in between — every one of which is real
    /// work someone has to queue.</para>
    /// </summary>
    public async Task<Dictionary<int, BuildDemand>> GatherAsync(
        AppDbContext db, ProductionContext ctx, List<WorklistInvRule> rules,
        Dictionary<int, InvLevelGroup> groups, HashSet<long>? scope, HashSet<long> wrapped,
        ScopeStock inScope, CancellationToken ct)
    {
        var gross = new Dictionary<int, Gross>();

        Gross At(int typeId)
        {
            if (!gross.TryGetValue(typeId, out var g)) gross[typeId] = g = new Gross();
            return g;
        }

        // ── Inventory levels ──────────────────────────────────────────────────
        //
        // The level is the amount to have on the shelf, so it counts gross whether or not the
        // rule has tripped. What the threshold still governs is whether a level on its own is
        // worth raising: without it a group at 99% would churn out a job every refresh.

        var topLevel = new List<(int TypeId, long Units)>();

        foreach (var rule in rules.OrderByDescending(r => r.ThresholdPercent).ThenBy(r => r.Id))
        {
            if (!groups.TryGetValue(rule.GroupId, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id).ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();
            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);

            foreach (var gi in groupItems.OrderBy(i => i.TypeId))
            {
                if (!ctx.BlueprintByProduct.ContainsKey(gi.TypeId)) continue;  // bought, not built

                avail.TryGetValue(gi.TypeId, out var av);
                var have   = (av?.Assets ?? 0) + (av?.IndustryJobs ?? 0);
                var target = (long)gi.TargetQuantity * Math.Max(1, group.Multiplier);
                if (target <= 0) continue;

                var wanted = (long)Math.Ceiling(target * (rule.FillTargetPercent / 100.0));

                var g = At(gi.TypeId);
                // Two rules on one item are two statements of the same shelf, not two shelves.
                g.Level = Math.Max(g.Level, wanted);
                g.Have  = Math.Max(g.Have, have);

                var need = InvRuleShortfall.For(rule, group, gi, av);
                if (need is null) continue;   // comfortably full: contributes its level, asks for nothing

                g.Fires    = true;
                g.Priority = Math.Max(g.Priority, WorklistPriority.ForStock(need.Percent));
                g.Reasons.Add($"{group.Name} · {need.StockText}.{need.FillText(rule)}");
                topLevel.Add((gi.TypeId, need.Shortfall));
            }
        }

        // ── Customer orders ───────────────────────────────────────────────────

        foreach (var (typeId, units, outstanding, count) in await OrderDemandAsync(db, scope, wrapped, ct))
        {
            if (!ctx.BlueprintByProduct.ContainsKey(typeId)) continue;

            var g = At(typeId);
            g.Consumed += units;
            g.Fires     = true;
            g.Priority  = Math.Max(g.Priority, WorklistPriority.OrderDriven);
            g.Reasons.Add($"{count} pending order(s) for {units:N0}.");
            if (outstanding > 0) topLevel.Add((typeId, outstanding));
        }

        // ── What those builds will consume ────────────────────────────────────
        //
        // Exploded one level at a time, netting stock at every level before descending. Taking
        // the whole tree from a single plan would ask each level for the parent's gross: two
        // Avatars want 451 Radar-FTL, but 268 are already on the shelf, so only 235 get built —
        // and only those 235 consume anything further down. Planning the subtree for 451 would
        // over-state every level beneath it.
        //
        // Only the root job's own materials are taken from each plan; its deeper levels arrive on
        // their own turn, already netted.

        var pending  = new Queue<int>(topLevel.Select(t => t.TypeId).Distinct());
        var expanded = new Dictionary<int, long>();   // what each type was last exploded for

        // Default ME is a database lookup and the explosion revisits the same types repeatedly.
        var meCache = new Dictionary<int, int>();

        // Demand only ever grows, so this settles. The cap is a backstop against a blueprint
        // cycle in bad data rather than an expected limit.
        for (var pass = 0; pass < 5000 && pending.Count > 0; pass++)
        {
            var typeId = pending.Dequeue();
            if (!ctx.BlueprintByProduct.ContainsKey(typeId)) continue;

            var g    = At(typeId);
            var have = g.Have > 0 ? g.Have : inScope.Anywhere(typeId);
            var net  = g.Level + g.Consumed - have;
            if (net <= 0) continue;

            // Already exploded for at least this much; only the increase needs pushing down.
            var already = expanded.GetValueOrDefault(typeId);
            if (net <= already) continue;
            expanded[typeId] = net;

            if (!meCache.TryGetValue(typeId, out var me))
                meCache[typeId] = me = await production.GetDefaultMeAsync(typeId, ct);

            var entry = new ProductionQueueEntry
            {
                TypeId   = typeId,
                Quantity = (int)Math.Clamp(net, 1, int.MaxValue),
                MeLevel  = me,
            };

            PlanJob? root;
            try
            {
                root = production.Calculate([entry], ctx)
                                 .AllJobs.FirstOrDefault(j => j.OutputTypeId == typeId);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }   // an unplannable item is reported by its own row, not here

            if (root is null) continue;

            var name    = ctx.TypeNames.GetValueOrDefault(typeId, $"Type {typeId}");
            var portion = already > 0 ? (double)(net - already) / net : 1.0;

            foreach (var m in root.Materials)
            {
                // Bought materials are Material Purchases' business, not a job.
                if (m.IsBought || !ctx.BlueprintByProduct.ContainsKey(m.MaterialTypeId)) continue;

                var qty = (long)Math.Ceiling(m.TotalQty * portion);
                if (qty <= 0) continue;

                var child = At(m.MaterialTypeId);
                child.Consumed += qty;
                child.Fires     = true;
                child.Priority  = Math.Max(child.Priority, g.Priority);
                child.Reasons.Add($"{qty:N0} for {name}.");

                pending.Enqueue(m.MaterialTypeId);
            }
        }

        // ── Net once ──────────────────────────────────────────────────────────

        var result = new Dictionary<int, BuildDemand>();

        foreach (var (typeId, g) in gross)
        {
            if (!g.Fires) continue;

            // Sub-assemblies with no inventory rule have no group availability to draw on, so
            // their stock comes from the configured scope instead.
            var have = g.Have > 0 ? g.Have : inScope.Anywhere(typeId);

            var units = g.Level + g.Consumed - have;
            if (units <= 0) continue;

            result[typeId] = new BuildDemand(
                typeId, units, g.Priority,
                [.. Summarise(g, have)]);
        }

        return result;
    }

    /// <summary>
    /// The row's opening line. Says what the total is for, since a pooled figure that only
    /// showed the shortfall would leave the reader unable to check it.
    /// </summary>
    private static IEnumerable<string> Summarise(Gross g, long have)
    {
        // Contributors first, in the order they were added, then the arithmetic.
        foreach (var r in g.Reasons.Take(3)) yield return r;
        if (g.Reasons.Count > 3) yield return $"and {g.Reasons.Count - 3} more.";

        if (g.Consumed > 0)
            yield return $"Keeping {g.Level:N0} and consuming {g.Consumed:N0}, "
                       + $"against {have:N0} on hand.";
    }

    /// <summary>Pending orders: gross units, what is still outstanding, and how many orders.</summary>
    private static async Task<List<(int TypeId, long Units, long Outstanding, int Count)>> OrderDemandAsync(
        AppDbContext db, HashSet<long>? scope, HashSet<long> wrapped, CancellationToken ct)
    {
        if (!await db.WorklistOrderRules.AsNoTracking().AnyAsync(r => r.Enabled, ct)) return [];

        var orders = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Status == "pending").ToListAsync(ct);
        if (orders.Count == 0) return [];

        var wanted = orders.Select(o => o.TypeId).Distinct().ToList();

        var onHand = (await db.EsiAssets.AsNoTracking()
                .Where(a => wanted.Contains(a.TypeId))
                .Select(a => new { a.ItemId, a.TypeId, a.RootLocationId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId)
                        && (scope is null || scope.Contains(a.RootLocationId)))
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        var inBuild = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => (j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                            && j.ProductTypeId != null && wanted.Contains(j.ProductTypeId!.Value))
                .Select(j => new { j.ProductTypeId, j.Runs, j.FacilityId })
                .ToListAsync(ct))
            .Where(j => scope is null || scope.Contains(j.FacilityId))
            .GroupBy(j => j.ProductTypeId!.Value)
            .ToDictionary(g => g.Key, g => (long)g.Sum(j => j.Runs));

        return orders.GroupBy(o => o.TypeId).OrderBy(g => g.Key)
            .Select(g =>
            {
                var units = g.Sum(o => (long)o.Units);
                return (g.Key, units,
                        Math.Max(0, units - onHand.GetValueOrDefault(g.Key)
                                          - inBuild.GetValueOrDefault(g.Key)),
                        g.Count());
            })
            .ToList();
    }
}
