using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Everything asking for one item to be built, and who is asking.</summary>
/// <param name="Priority">The most urgent contributor's. A customer order sitting alongside a
/// stock top-up makes the whole build a customer order's worth of urgent, because the order is
/// not served until the last unit is.</param>
/// <param name="OrderUnits">Of the gross demand, what customer orders asked for.</param>
/// <param name="RuleUnits">Of the gross demand, what inventory levels want on the shelf.</param>
/// <param name="ParentUnits">Of the gross demand, what parent builds will eat.</param>
/// <param name="ShelfLevel">What the inventory levels want on the shelf, for measuring coverage
/// against as jobs are planned. Zero where nothing sets a level for this item.</param>
/// <param name="ShelfHave">What is on that shelf now — assets and running jobs.</param>
public sealed record BuildDemand(int TypeId, long Units, int Priority, List<string> Reasons,
                                 long OrderUnits = 0, long RuleUnits = 0, long ParentUnits = 0,
                                 long ShelfLevel = 0, long ShelfHave = 0)
{
    /// <summary>
    /// How full the shelf would be with <paramref name="planned"/> more units on it, as a
    /// percentage of what the level asks for.
    ///
    /// <para>⚠️ Takes the planned figure because the generator asks this repeatedly, between
    /// jobs. A coverage worked out once at the start is the reason six hulls' worth of a scarce
    /// component all went to whichever item happened to be emptiest before any of it was
    /// allocated — by the second job it was no longer the emptiest, and nothing noticed.</para>
    ///
    /// <para>Items with no shelf level return 100: they are not competing for shelf space, so
    /// they never win a comparison that is about how empty a shelf is.</para>
    /// </summary>
    public double CoverageWith(long planned) =>
        ShelfLevel <= 0 ? 100 : 100.0 * (ShelfHave + planned) / ShelfLevel;

    /// <summary>
    /// How many other items downstream are waiting on this one.
    ///
    /// <para>⚠️ Separates jobs that inherited the same priority. An isotropic feeding one blocked
    /// cell and an isotropic feeding the whole capital line inherit the same urgency from the
    /// order at the top, and when a reaction slot frees up the tie used to break on coverage or
    /// on type id. This is what should decide it: how much stops moving if this does not run.</para>
    /// </summary>
    public int Blocks { get; init; }

    /// <summary>
    /// How many of those dependents are things the operation actually sells or flies.
    ///
    /// <para>⚠️ A count of blocked work cannot tell a customer from a cupboard.
    /// Nanotransistors blocks eleven, and ten of them are component buffers refilling
    /// themselves — real work, whose only customer is the shelf it came from. An isotropic
    /// blocking a Neurolink cell blocks every standard capital hull. Both score eleven, and
    /// only one of them is worth a slot today.</para>
    ///
    /// <para>Which items are final is hand-set on the inventory rule, because nothing in the
    /// blueprint tree can tell: hulls for one operation, rigs or modules for another.</para>
    /// </summary>
    public int BlocksFinal { get; init; }

    /// <summary>
    /// Which items are waiting on this one, not merely how many.
    ///
    /// <para>⚠️ The count has to be RECOMPUTED as jobs are planned, and a number cannot be.
    /// Planning 50,000 oxidizers feeds the Core Temperature Regulator job that was waiting on
    /// them; that job is no longer blocked by oxidizers, so the oxidizers' claim on the next
    /// slot is smaller than it was. With a fixed count the leader keeps its score forever and
    /// takes every slot until its demand runs out, which is the one behaviour this ordering
    /// exists to prevent. The set is what makes the recount possible.</para>
    /// </summary>
    public IReadOnlyCollection<int> Dependents { get; init; } = [];

    /// <summary>This item is something the operation sells or flies. Hand-set on the
    /// inventory rule; see WorklistInvRule.IsFinalProduct.</summary>
    public bool IsFinal { get; init; }

    /// <summary>
    /// What this item's own demand justifies, before inheritance.
    ///
    /// <para>⚠️ Inherited priority is only true while something upstream still needs this.
    /// It is stamped once during the demand walk and never revisited, so an item keeps an
    /// order's urgency long after the order has been planned out. The picker falls back to
    /// this when no live dependent carries anything higher.</para>
    /// </summary>
    public int OwnPriority { get; init; }


    /// <summary>
    /// Why this is wanted — and, where it matters, how much waits behind it.
    ///
    /// <para>The blocking count is on the row rather than only in the sort, because a job that
    /// jumps the queue without saying why reads as the list being arbitrary.</para>
    /// </summary>
    public string Head => string.Join(" + ", Reasons)
                        + (Blocks > 1 ? $" [{Blocks:N0} item(s) downstream wait on this]" : "");

    /// <summary>
    /// The gross the three parts add up to, which is not <see cref="Units"/> — that is the net
    /// left after stock is taken off. Shares are taken against this so they sum to one.
    /// </summary>
    public long Gross => OrderUnits + RuleUnits + ParentUnits;

    /// <summary>
    /// How much of <paramref name="amount"/> each source is responsible for.
    ///
    /// <para>Material for a job inherits the job's own mix: a component built half for a customer
    /// order and half to restock a shelf puts half its minerals under each. Rounded down and the
    /// remainder given to the largest share, so the parts always add back to the whole.</para>
    /// </summary>
    public (long Order, long Rule, long Parent) SplitOf(long amount)
    {
        if (amount <= 0) return (0, 0, 0);
        if (Gross <= 0) return (0, amount, 0);   // no attribution recorded: call it stock-keeping

        var order  = amount * OrderUnits  / Gross;
        var rule   = amount * RuleUnits   / Gross;
        var parent = amount * ParentUnits / Gross;

        var slack = amount - order - rule - parent;
        if (slack > 0)
        {
            if (OrderUnits >= RuleUnits && OrderUnits >= ParentUnits) order += slack;
            else if (RuleUnits >= ParentUnits)                        rule  += slack;
            else                                                      parent += slack;
        }
        return (order, rule, parent);
    }
}

/// <summary>Everything in scope, wherever it sits and whoever owns it.</summary>
/// <param name="Corp">Keyed by owning corporation as well as type, because which corporation
/// holds a pile decides who can take from it.</param>
public sealed record ScopeStock(
    Dictionary<(int TypeId, long OwnerId), long> Corp,
    Dictionary<(int TypeId, long OwnerId), long> Personal)
{
    /// <summary>
    /// What one character's job could actually draw on: their own hangar, plus the hangars of the
    /// corporation they are in.
    ///
    /// <para>Derived from who the character is, not from a per-character setting. Everything here
    /// is already inside the asset scope and therefore already the player's — the only remaining
    /// question is physical: a pilot can take from their own hangar and from their own corp's,
    /// and from nobody else's, whatever the plan would prefer.</para>
    /// </summary>
    public long Reachable(int typeId, IndustryCandidate who) =>
        Corp.GetValueOrDefault((typeId, who.CorporationId))
      + Personal.GetValueOrDefault((typeId, who.Config.CharacterId));

    /// <summary>
    /// Everything in scope regardless of whose it is, for netting demand rather than deciding
    /// whether one character can start one job. A sub-assembly with no inventory rule has no
    /// group availability to fall back on, and counting only one alt's reach would ask for a
    /// second batch of something the corp already holds.
    /// </summary>
    public long Anywhere(int typeId) =>
        Corp.Where(kv => kv.Key.TypeId == typeId).Sum(kv => kv.Value)
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
    ProductionCalculatorService production,
    WorklistSettings            settings)
{
    /// <summary>Everything one item is wanted for, before anything is netted off.</summary>
    private sealed class Gross
    {
        /// <summary>What the inventory rules say should be sitting on the shelf.</summary>
        public long Level;

        /// <summary>Units customers have ordered, and units parent builds will eat.</summary>
        public long Consumed;

        // The same total again, split by who is asking. Consumed stays the figure everything
        // plans against — these only exist so a report can say why a station wants something,
        // and they are kept in step by being written beside every change to Consumed.

        /// <summary>Of <see cref="Consumed"/>, the part a customer order asked for.</summary>
        public long OrderUnits;

        /// <summary>Of <see cref="Consumed"/>, the part a parent build will eat.</summary>
        public long ParentUnits;

        /// <summary>Stock and in-flight production, counted once however many demands there are.</summary>
        public long Have;

        /// <summary>
        /// Every item downstream that waits on this one, transitively.
        ///
        /// <para><b>⚠️ Priority is inherited but never accumulated, which is what this fixes.</b> A
        /// child takes the highest priority among its parents, so an isotropic feeding one blocked
        /// cell and an isotropic feeding the whole capital line score identically — and when a
        /// reaction slot opens, the tie breaks on coverage or on type id, which is to say on
        /// nothing. How much stops moving if this does not get made is the thing that should
        /// separate them.</para>
        ///
        /// <para>A set rather than a count because the graph has diamonds: two parents can both
        /// reach the same grandchild, and adding twice would say the chain is wider than it is.</para>
        /// </summary>
        public readonly HashSet<int> Dependents = [];

        /// <summary>Hand-marked as sold or flown rather than held as an input.</summary>
        public bool IsFinal;

        /// <summary>True when a rule's threshold has tripped, or something is waiting on it. A
        /// level sitting comfortably full raises nothing; a level about to be eaten does.</summary>
        public bool Fires;

        public int          Priority = WorklistPriority.Housekeeping;

        /// <summary>What this item's OWN demand justifies, before anything is inherited
        /// from above. The floor an item falls back to once nothing upstream still needs it.</summary>
        public int          OwnPriority = WorklistPriority.Housekeeping;
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
        HashSet<long>? corps, ScopeStock inScope, CancellationToken ct)
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

                // ⚠️ The flag is on the RULE, so it covers the whole group. That is the grain
                // people set it at — "Titans" is final and "Capital Parts" is not — and it means
                // an item reachable through two rules is final if EITHER says so.
                if (rule.IsFinalProduct) At(gi.TypeId).IsFinal = true;

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
                g.Priority    = Math.Max(g.Priority, WorklistPriority.ForStock(need.Percent));
                g.OwnPriority = Math.Max(g.OwnPriority, WorklistPriority.ForStock(need.Percent));
                g.Reasons.Add($"{group.Name} · {need.StockText}.{need.FillText(rule)}");
                topLevel.Add((gi.TypeId, need.Shortfall));
            }
        }

        // ── Customer orders ───────────────────────────────────────────────────

        foreach (var (typeId, units, outstanding, count, rank) in
                 await OrderDemandAsync(db, settings.PlanCustomerOrders, scope, wrapped, corps, ct))
        {
            if (!ctx.BlueprintByProduct.ContainsKey(typeId)) continue;

            // ⚠️ OUTSTANDING, not ordered. An order already covered by stock or by a job
            // already running needs nothing built, and until now it still stamped the order
            // band on the item and added its units to demand — only the queue entry was gated.
            // One Simurgh, in build and linked to its job, therefore put priority 220 on itself;
            // a stock rule then carried that 220 down its entire tree, and fifty jobs for three
            // well-stocked reactions sat above every starving item in the list. Priority has to
            // stop when the thing that justified it is settled.
            if (outstanding <= 0) continue;

            var g = At(typeId);
            g.Consumed   += outstanding;
            g.OrderUnits += outstanding;
            g.Fires       = true;
            // Ranked rather than flat, so the order due first outranks the one due next month.
            // Children inherit this below, so the whole tree under an urgent order stays urgent.
            g.Priority    = Math.Max(g.Priority, WorklistPriority.ForOrder(rank));
            g.OwnPriority = Math.Max(g.OwnPriority, WorklistPriority.ForOrder(rank));
            g.Reasons.Add($"{count} pending order(s) for {units:N0}.");
            topLevel.Add((typeId, outstanding));
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
                child.Consumed    += qty;
                child.ParentUnits += qty;
                child.Fires        = true;
                child.Priority  = Math.Max(child.Priority, g.Priority);
                child.Reasons.Add($"{qty:N0} for {name}.");

                // What waits on this: the parent, and everything already waiting on the parent.
                // The queue reaches parents before children, so by the time a child is written the
                // parent's own set is complete — except across a cycle, where it is whatever was
                // known at the time. Under-counting a cycle is the right way to be wrong here.
                child.Dependents.Add(typeId);
                child.Dependents.UnionWith(g.Dependents);

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
                [.. Summarise(g, have)],
                g.OrderUnits, g.Level, g.ParentUnits,
                ShelfLevel: g.Level, ShelfHave: have)
            {
                Blocks = g.Dependents.Count,
            };
        }

        // ⚠️ Counted again against what actually still needs building.
        //
        // Dependents are gathered during the walk, before anything is netted against stock, so
        // the raw count includes types that turned out to be fully covered and will raise no job
        // at all. That is the difference between "how many things use this" and "how much work
        // is waiting on it", and the picker sorts on it: a base reaction feeding a long covered
        // chain outranked the component four stopped hulls were waiting for, and took the one
        // free reaction slot with it.
        foreach (var typeId in result.Keys.ToList())
            result[typeId] = result[typeId] with
            {
                Blocks      = gross[typeId].Dependents.Count(result.ContainsKey),
                BlocksFinal = gross[typeId].Dependents
                                  .Count(x => result.ContainsKey(x) && gross[x].IsFinal),
                Dependents  = [.. gross[typeId].Dependents.Where(result.ContainsKey)],
                IsFinal     = gross[typeId].IsFinal,
                OwnPriority = gross[typeId].OwnPriority,
            };

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
    /// <summary>
    /// Pending orders in the order they should be served: hand-marked ones first, then by
    /// estimated date, then by when the order was taken.
    ///
    /// <para>An order with no estimated date sorts after every dated one rather than before —
    /// a blank is "no deadline given", not "due immediately", and treating it as the latter would
    /// let an undated order shoulder past one with a real date next week.</para>
    /// </summary>
    public static List<TrackedOrder> Ranked(IEnumerable<TrackedOrder> pending) =>
        pending
            .OrderByDescending(o => o.IsPriority)
            .ThenBy(o => DateOnly.TryParse(o.EstimatedDate, out var d) ? d : DateOnly.MaxValue)
            .ThenBy(o => o.CreatedAt)
            .ThenBy(o => o.Id)
            .ToList();

    private static async Task<List<(int TypeId, long Units, long Outstanding, int Count, int Rank)>> OrderDemandAsync(
        AppDbContext db, bool enabled, HashSet<long>? scope, HashSet<long> wrapped, HashSet<long>? corps,
        CancellationToken ct)
    {
        // The Customer orders switch on the Sources tab is what says orders should be planned.
        if (!enabled) return [];

        // ⚠️ An order with a contract already made out is not work any more. The goods left the
        // hangar to get into it, so they are in no asset row and no job — and the netting below
        // sees only assets and jobs. Counting such an order as demand asks for a second hull to
        // replace one already sitting in a contract with the buyer's name on it.
        //
        // Still "pending" as an order, correctly: it is not settled until the contract is taken.
        // Pending is about the customer; this is about the shelf.
        var orders = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Status == "pending" && o.LinkedContractId == null).ToListAsync(ct);
        if (orders.Count == 0) return [];

        // Each order's place in the queue, so the work it drives can be ranked against the work
        // other orders drive rather than all of it landing on one flat "order-driven" tier.
        var rankOf = Ranked(orders)
            .Select((o, i) => (o.Id, Rank: i))
            .ToDictionary(x => x.Id, x => x.Rank);

        var wanted = orders.Select(o => o.TypeId).Distinct().ToList();

        var onHand = (await db.EsiAssets.AsNoTracking()
                .Where(a => wanted.Contains(a.TypeId))
                .Select(a => new { a.ItemId, a.TypeId, a.RootLocationId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId)
                        && (scope is null || scope.Contains(a.RootLocationId))
                        && (a.OwnerType != "corporation"
                            || corps is null || corps.Contains(a.OwnerId)))
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
                        g.Count(),
                        // Several orders can want the same item; the most urgent of them decides
                        // how urgent building it is.
                        Rank: g.Min(o => rankOf.GetValueOrDefault(o.Id, int.MaxValue)));
            })
            .ToList();
    }
}
