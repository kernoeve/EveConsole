using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Everything industry has to acquire, from every demand it serves, counted once.
///
/// <para><b>Why one source and not two.</b> Customer orders and inventory targets are additive —
/// an order for an Avatar and a standing target of one Avatar means building two — but the stock
/// that fills them is not. When each side reported its own shortfall, both subtracted the same
/// twelve Nano Regulation Gates from their own demand, and neither ever asked for the twenty-three
/// actually needed. Netting supply against pooled demand is the only arrangement that gives the
/// right answer, and it can only be done in one place — the demand service the job generator
/// plans from, so what is bought is the material of what will actually be built.</para>
///
/// <para><b>Netted at every level, running jobs included.</b> A component on the shelf, or in a
/// machine, needs none of its materials bought. This generator used to plan one tree from the
/// top-level shortfall and net only the raw materials at the bottom against assets, so a
/// component's inputs were asked for whether or not the component existed — and starting one of
/// its own recommended jobs made the purchases GROW: the inputs left the hangar, the output was
/// not yet in any asset row, and the next refresh bought the inputs over again. 80 billion ISK of
/// orders became 155 billion over an afternoon of starting exactly what the list said to.</para>
///
/// <para><b>Sized against the prints on hand.</b> Materials are planned at the efficiency of the
/// blueprint that would really be used, at every level of the tree rather than only the top. The
/// same Avatar planned at default efficiency asks for 17 gates where the ME8 original actually
/// owned needs 18, and buying 17 leaves the job unable to start.</para>
///
/// <para>Items covered by a Build rule are never bought. That rule is a standing decision to make
/// the thing, so its shortfall is answered by a job; a build job and a buy order for one pile of
/// material are contradictory instructions.</para>
/// </summary>
public class MaterialPurchaseGenerator(

    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService       assignment,
    IndustryBlueprintService        blueprints,
    IndustryDemandService           demands,
    MaterialSubstitutionService     substitution,
    ProductionCalculatorService     production,
    WorklistMarketAltService        marketAlts,
    WorklistSettings                settings,
    OutbidOrderService              outbidOrders,
    AppErrorLogger                  errorLogger) : IWorklistGenerator
{
    public string Id          => "material_purchases";
    public string DisplayName => "Material Purchases";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];

        var ctx     = await production.LoadContextAsync(parkId, ct);
        var corps   = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);

        // ── Prints that are made rather than bought ───────────────────────────
        //
        // ⚠️ An inventable blueprint is NOT a purchase task. A missing Ark Blueprint was raised
        // here as "BPO/BPC × 2" alongside the invention and copy jobs the invention generator
        // raises for the same shortfall — two plans for one gap, and the wrong one first: a T2
        // print is invented off its T1 original, not shopped for.
        //
        // ⚠️ Only stood down on when invention can actually run. With no scientist assigned or a
        // park that has not said where copying and invention happen, that generator produces
        // nothing at all — and standing down into silence would leave a blocked T2 job with no
        // task against it whatsoever. Then buying really is the only way to get one, and the
        // purchase row is the honest answer.
        var canInvent = await InventionService.CanPlanAsync(
            db, parkId, candidates.Any(c => c.Runs(IndustryPool.Science)), ct);

        var inventable = canInvent
            ? await InventionService.InventedBlueprintIdsAsync(db, ct)
            : [];

        var scope   = await ScopeAsync(db, parkId, ct);
        var wrapped = await AssetExclusions.UnusableItemIdsAsync(db, ct);
        var reach   = new ProductionCalculatorService.AssetReach(scope, wrapped, corps);

        var allPrints = await blueprints.LoadAllAsync(ct);
        var owned     = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var meMap     = IndustryBlueprintService.BestMeByProduct(
                            allPrints, ctx.BlueprintByProduct, owned);

        // ── What has to be produced, from both demands ────────────────────────
        //
        // The same demand the job generator turns into jobs, so the two agree about what is
        // being built: pooled across orders and inventory rules, cascaded down the tree, and
        // netted at EVERY level against what is on hand and what running jobs will deliver.
        // Planned at the efficiency of the prints actually owned, since that is what the jobs
        // will take.
        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);
        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var inScope = await ScopeStock.LoadAsync(db, scope, wrapped, corps, ct);
        var demand  = await demands.GatherAsync(
            db, ctx, rules, groups, scope, wrapped, corps, inScope, ct, meMap);
        if (demand.Count == 0) return [];

        // What those builds consume that is bought: each item's own inputs, for the units the
        // cascade left to build. Its sub-assemblies are rows of the demand in their own right.
        Dictionary<int, IndustryDemandService.RawNeed> raw;
        try
        {
            raw = await demands.RawMaterialsAsync(ctx, demand.Values, meMap, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(MaterialPurchaseGenerator), $"Park {parkId}", ex);
            return [];
        }

        // Things wanted in their own right — by an order or a tripped rule — are what prints are
        // acquired for here. A sub-assembly's missing print is raised by the job generator on
        // the job that cannot start without it.
        var queue = new List<ProductionQueueEntry>();
        foreach (var d in demand.Values.Where(d => d.IsRoot).OrderBy(d => d.TypeId))
            queue.Add(new ProductionQueueEntry
            {
                TypeId   = d.TypeId,
                TypeName = ctx.TypeNames.GetValueOrDefault(d.TypeId, $"Type {d.TypeId}"),
                Quantity = Math.Clamp(d.Units, 1, int.MaxValue),
                MeLevel  = meMap.TryGetValue(d.TypeId, out var me)
                             ? me
                             : await production.GetDefaultMeAsync(d.TypeId, ct),
            });

        // ── Supply against those needs ────────────────────────────────────────

        var rawIds = raw.Keys.ToList();

        // On hand, anywhere in reach. RootLocationId is the terminal station reached by walking
        // the container chain, so this counts materials sitting in cans and ship holds too.
        var onHand = (await db.EsiAssets.AsNoTracking()
                .Where(a => rawIds.Contains(a.TypeId))
                .Select(a => new { a.ItemId, a.TypeId, a.RootLocationId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => reach.Counts(a.ItemId, a.RootLocationId, a.OwnerType, a.OwnerId))
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        // Less what jobs started since the last asset poll have already taken out of it. The
        // job generator makes the same correction for the same reason; here it is the mirror
        // image — inputs still counted as on hand cover a shortfall they no longer can, and for
        // up to an hour a purchase that is needed goes unraised.
        var eaten = (await demands.AlreadyConsumedAsync(db, ctx, scope, ct))
            .GroupBy(kv => kv.Key.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

        long Missing(int typeId) =>
            Math.Max(0, raw[typeId].Units
                        - Math.Max(0, onHand.GetValueOrDefault(typeId) - eaten.GetValueOrDefault(typeId)));

        // Anything a Build rule covers is made, not bought — unless nothing can make it, in which
        // case buying is the only way it ever arrives.
        var buildManaged = await BuildManagedTypesAsync(db, ctx, ct);

        // Blueprints the builds themselves ask to buy — BPC-only items carry their copies as a
        // material with a real quantity, at every level of the tree. Those are better handled
        // there than by the nothing-owned check below, which cannot say "you have two copies and
        // need four".
        var bpShortfalls = rawIds.Where(t => ctx.BpTypeIds.Contains(t) && Missing(t) > 0).ToHashSet();

        var buyAt   = settings.IndustryBuyLocationId;
        var buyName = settings.IndustryBuyLocationName;
        var alt     = buyAt > 0 ? (await marketAlts.GetByLocationAsync(ct)).GetValueOrDefault(buyAt) : null;

        // Prints the blueprints table does not know about but the assets table does. Without this
        // a copy sitting in a structure the blueprints feed omits reads as no copy at all, and the
        // tool asks you to buy one you already own.
        var queueBpIds = queue
            .Select(q => ctx.BlueprintByProduct.TryGetValue(q.TypeId, out var b) ? b.TypeId : 0)
            .Where(id => id > 0).Distinct().ToList();
        var inAssets = await blueprints.OwnedInAssetsAsync(queueBpIds, owned, ct);

        var shelfWant = await BlueprintShelfWantAsync(db, ctx, ct);

        var items = new List<WorklistItem>();
        items.AddRange(PrintTasks(ctx, queue, allPrints, owned, bpShortfalls, inventable, inAssets, shelfWant,
                                  buyAt, buyName, alt));

        var shortIds = rawIds.Where(t => Missing(t) > 0).ToList();

        var onOrder = await OnOrderAsync(db, shortIds, ct);

        // Ore, ice and gas already held count toward what they turn into, so a shortfall covered
        // by unrefined stock does not become a purchase.
        var subs      = await substitution.LoadAsync(ct);
        var subsStock = await SubstituteStockAsync(db, subs, shortIds, reach, ct);

        // Production already running that will yield the material itself — a bought intermediate
        // somebody is building anyway. Assets alone under-count what is coming: a reaction three
        // days from delivery is material as surely bought as one on an open order, and buying
        // against it orders the same units twice.
        var inFlight = await InFlightOutputAsync(db, ctx, shortIds, scope, ct);

        // Materials the plan would still be missing if none of our orders existed — the ones an
        // order is actually needed for. See the note where these are reported.
        var needed = new HashSet<int>();

        foreach (var typeId in shortIds.OrderBy(t => ctx.TypeNames.GetValueOrDefault(t, "")))
        {
            if (buildManaged.Contains(typeId)) continue;

            var need     = raw[typeId];
            var have     = onHand.GetValueOrDefault(typeId);
            var taken    = Math.Min(have, eaten.GetValueOrDefault(typeId));
            var missing  = Missing(typeId);
            var ordered  = onOrder.GetValueOrDefault(typeId);
            var building = inFlight.GetValueOrDefault(typeId);
            var held     = subsStock.GetValueOrDefault(typeId);
            var short_   = missing - ordered - building - held.Units;

            // ⚠️ Everything except the orders. An order covering the last of a shortfall is the
            // reason that shortfall is not a task, which makes it the one order whose failing
            // matters most — and subtracting it here would hide exactly that case.
            if (missing - building - held.Units > 0) needed.Add(typeId);

            if (short_ <= 0) continue;

            var typeName = ctx.TypeNames.GetValueOrDefault(typeId, $"Type {typeId}");

            // A blueprint is acquired, not market-ordered, so it is titled the way the print
            // tasks are — either a BPO or a copy will do, and which is the player's call.
            var isPrint = ctx.BpTypeIds.Contains(typeId);

            // Invention raises this one, and raising it here as well would be two plans for one gap.
            if (isPrint && inventable.Contains(typeId)) continue;

            items.Add(new WorklistItem
            {
                Key           = $"industry_buy:{typeId}",
                Source        = Id,
                // The amount belongs in the title — "buy this" without a number is not yet an
                // instruction — but the name leads, because the column sorts on this string and
                // a leading count sorts by digit, scattering an item's rows across the list.
                Kind          = WorklistKind.Buy,
                // "BPO/BPC" trails the name for the same reason, while still saying the purchase
                // is a contract rather than a market order, which the kind column cannot.
                Title         = isPrint ? $"{typeName} — BPO/BPC × {short_:N0}"
                                        : $"{typeName} × {short_:N0}",
                Quantity      = short_,
                TitleTag      = isPrint ? "BPO/BPC" : null,
                // Prints merge too. A job needing copies and a stocking rule wanting some on the
                // shelf are one trip to the contract window, exactly as two demands for the same
                // mineral are one order — the contract-versus-market distinction only matters
                // against a market row for the same type, and a blueprint never has one.
                MergeKey      = WorklistItem.BuyMergeKey(buyAt, typeId),
                // Both halves of the subtraction, so a merge with an inventory rule wanting the
                // same material nets the shared stock once rather than once per demand. What the
                // started jobs already took is not supply, so it is left out of the credit.
                GrossDemand    = need.Units,
                SupplyCredited = (have - taken) + ordered + building + held.Units,
                Detail        = $"{WantedBy(need)}: need {need.Units:N0}; "
                              + $"{have:N0} on hand{settings.IndustryScopeSuffix}"
                              + (taken    > 0 ? $" of which {taken:N0} already went into jobs the asset poll has not seen" : "")
                              + (ordered  > 0 ? $", {ordered:N0} on order" : "")
                              + (building > 0 ? $", {building:N0} in production" : "")
                              + held.Note
                              + $" — short {short_:N0}.",
                Readiness     = alt is null ? WorklistReadiness.Blocked : WorklistReadiness.Ready,
                BlockedBy     = alt is null
                    ? (buyAt > 0 ? $"No market alt assigned to {buyName}"
                                 : "No buy location set on the Industry tab")
                    : "",
                CharacterId   = alt?.CharacterId   ?? 0,
                CharacterName = alt?.CharacterName ?? "",
                LocationId    = buyAt,
                LocationName  = buyName,
                TypeId        = typeId,
                TypeName      = typeName,
                Priority      = WorklistPriority.ServesOther,
            });
        }

        // Orders failing to buy what the plan still needs. Reported alongside the purchase tasks
        // rather than instead of them: an order can be losing while it is the only thing covering
        // the shortfall, and a shortfall can want a second order placed while the first is also
        // underbid. Keyed by type and station, so an order the inventory rules also depend on is
        // one task, not one per reason it is wanted.
        items.AddRange((await outbidOrders.FindAsync(needed, ct)).Select(OutbidOrderService.Task));

        return items;
    }

    /// <summary>
    /// The builds that actually consume this material, so a row can say why it is wanted.
    ///
    /// <para>Taken from the builds that asked for it rather than from the top of the demand.
    /// Naming the first few things wanted would put "2 Avatar" against every line, including
    /// materials no Avatar touches — a plausible sentence that happens to be false, which is
    /// worse than no explanation at all.</para>
    /// </summary>
    private static string WantedBy(IndustryDemandService.RawNeed need)
    {
        var users = need.Consumers
            .GroupBy(c => c.TypeId)
            .Select(g => (g.First().Name, Runs: g.Sum(c => c.Runs), Units: g.Sum(c => c.Units)))
            .OrderByDescending(u => u.Units)
            .ToList();
        if (users.Count == 0) return "Planned builds";

        var named = users.Take(2)
            .Select(u => $"{u.Name} ({u.Runs:N0} run(s))")
            .ToList();
        var more = users.Count - named.Count;

        return "For " + string.Join(" and ", named) + (more > 0 ? $" and {more} more" : "");
    }

    /// <summary>Shortfalls against inventory targets, for groups whose rule says to build.</summary>
    /// <summary>
    /// What the stocking rules want of each blueprint, <b>before</b> any stock is deducted.
    ///
    /// <para>⚠️ Gross on purpose. <see cref="InvRuleShortfall"/> subtracts what is on hand and
    /// returns nothing at all once stock covers the target — correct for a rule read on its own,
    /// wrong here, because the same copies are also about to be spent by jobs. The subtraction has
    /// to happen once, against both demands together, so this hands over the raw target and lets
    /// <see cref="PrintTasks"/> do the arithmetic.</para>
    ///
    /// <para>Rules at different stations take the larger target rather than the sum: a print is
    /// bought once and moved, so two stations each wanting one is one print to acquire, not two.</para>
    /// </summary>
    private async Task<Dictionary<int, long>> BlueprintShelfWantAsync(
        AppDbContext db, ProductionContext ctx, CancellationToken ct)
    {
        var want = new Dictionary<int, long>();

        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action != "Build")
            .ToListAsync(ct);
        if (rules.Count == 0) return want;

        var groupIds = rules.Select(r => r.GroupId).Distinct().ToList();
        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
        var items = await db.InvLevelItems.AsNoTracking()
            .Where(i => groupIds.Contains(i.GroupId)).ToListAsync(ct);

        foreach (var rule in rules)
        {
            if (!groups.TryGetValue(rule.GroupId, out var group)) continue;

            foreach (var gi in items.Where(i => i.GroupId == group.Id))
            {
                if (!ctx.BpTypeIds.Contains(gi.TypeId)) continue;

                var target = (long)gi.TargetQuantity * Math.Max(1, group.Multiplier);
                if (target <= 0) continue;

                var wanted = (long)Math.Ceiling(target * (rule.FillTargetPercent / 100.0));
                if (wanted > want.GetValueOrDefault(gi.TypeId)) want[gi.TypeId] = wanted;
            }
        }

        return want;
    }

    /// <summary>
    /// Every blueprint that has to be acquired: what the jobs need, plus what the shelf wants,
    /// less what is already owned — worked out once, here.
    ///
    /// <para>⚠️ This is the only place blueprint demand is totalled, and
    /// <see cref="InventoryLevelGenerator"/> deliberately skips blueprint types so it stays that
    /// way. Two Avatar copies, two builds queued and a standing target of one used to produce
    /// nothing at all: the job side subtracted the two copies from its two, the stocking side
    /// subtracted the same two copies from its one, and both fell to zero. One pile of supply
    /// cannot be spent twice. Summed and subtracted once it is 2 + 1 − 2 = 1, which is the print
    /// actually missing.</para>
    ///
    /// <para>Jobs take from stock first and the shelf gets the remainder, which is why the sum is
    /// taken before the subtraction rather than after: the copies are consumed by the builds, and
    /// it is the standing target that ends up short.</para>
    /// </summary>
    private static List<WorklistItem> PrintTasks(
        ProductionContext ctx, List<ProductionQueueEntry> queue,
        List<BlueprintStock> allPrints, PrintOwnership owned,
        HashSet<int> alreadyCounted, HashSet<int> inventable, Dictionary<int, int> ownedInAssets,
        Dictionary<int, long> shelfWant,
        long buyAt, string buyName, WorklistMarketAlt? alt)
    {
        var items = new List<WorklistItem>();

        // What the queued builds need, one print per run — a copy is spent by the job that uses
        // it, so two builds need two.
        var jobNeed = new Dictionary<int, long>();
        var forWhat = new Dictionary<int, string>();
        foreach (var entry in queue.OrderBy(q => q.TypeId))
        {
            if (!ctx.BlueprintByProduct.TryGetValue(entry.TypeId, out var bp)) continue;
            jobNeed[bp.TypeId] = jobNeed.GetValueOrDefault(bp.TypeId)
                               + IndustryJobSplit.RunsFor(entry.Quantity, Math.Max(1, bp.Quantity));
            forWhat.TryAdd(bp.TypeId, entry.TypeName);
        }

        // The union, so a blueprint that is only stocked — nothing queued to build with it — is
        // still acquired. Iterating the queue alone would lose it the moment this became the one
        // place the demand is totalled.
        foreach (var bpTypeId in jobNeed.Keys.Concat(shelfWant.Keys).Distinct().OrderBy(id => id))
        {
            if (alreadyCounted.Contains(bpTypeId)) continue;   // the plan is already buying it
            if (inventable.Contains(bpTypeId))     continue;   // and this one is invented, not bought

            // Supply from both tables. The blueprints table does not cover every structure the
            // assets table does — this corporation has 5,518 blueprint rows and none at the staging structure,
            // where assets list two Avatar copies — and "absent from that table" is not the same
            // fact as "not owned".
            var mine = allPrints.Where(p => p.TypeId == bpTypeId && owned.Owns(p)).ToList();

            var anyOriginal = mine.Any(p => p.IsOriginal);

            // ⚠️ RUNS, not copies, on both sides of the subtraction. A copy is not one blueprint's
            // worth of anything: it carries runs, and a run is what a job spends and what a
            // stocking rule on a blueprint asks for. Two Ark copies of two and three runs are five
            // runs of production; counted as "2 owned" against a demand of four they asked the
            // contract window for two more prints that were not needed.
            var held = mine.Where(p => !p.IsOriginal).Sum(p => (long)p.Runs);

            // ⚠️ The assets fallback carries no runs, ME or TE — those rows can be counted and not
            // planned against. One run apiece is a floor, and deliberately the same floor the old
            // count-only arithmetic assumed, so nothing gets worse where the blueprints table is
            // the one with the gap.
            if (mine.Count == 0) held = ownedInAssets.GetValueOrDefault(bpTypeId);

            var jobs  = jobNeed.GetValueOrDefault(bpTypeId);    // runs the queued builds spend
            var shelf = shelfWant.GetValueOrDefault(bpTypeId);  // runs the stocking rule wants kept

            // An original is never spent by the job it runs, so one covers every run there will
            // ever be — but it does not fill a shelf target, which asks for copies to be there.
            // Invention is the reason that distinction matters: it runs off a copy and cannot
            // touch the original, however many runs the original is good for.
            var demand = (anyOriginal ? 0 : jobs) + shelf;

            var stillNeeded = Math.Max(0, demand - held);
            if (stillNeeded <= 0) continue;

            var bpName = ctx.TypeNames.GetValueOrDefault(bpTypeId, $"Blueprint {bpTypeId}");
            var price  = ctx.BpcPerRun.TryGetValue(bpTypeId, out var opts) && opts.Count > 0
                ? $" Copies have been seen on contract from {opts.Min(o => o.PerRun):N0} ISK a run."
                : "";

            // Spelled out, because the number is a subtraction the reader cannot see.
            var parts = new List<string>(3);
            if (jobs  > 0) parts.Add($"{jobs:N0} for {forWhat.GetValueOrDefault(bpTypeId, "queued builds")}");
            if (shelf > 0) parts.Add($"{shelf:N0} to stock");

            var haveText = anyOriginal ? ", original owned"
                         : held > 0    ? $", {held:N0} owned"
                                       : ", none owned";

            items.Add(new WorklistItem
            {
                Key           = $"industry_print:{bpTypeId}",
                Source        = "material_purchases",
                Kind          = WorklistKind.Buy,
                Title         = $"{bpName} — BPO/BPC × {stillNeeded:N0} run(s)",
                TitleTag      = "BPO/BPC",
                Quantity      = stillNeeded,
                MergeKey      = WorklistItem.BuyMergeKey(buyAt, bpTypeId),
                Detail        = $"{string.Join(" + ", parts)}{haveText} — short {stillNeeded:N0} run(s).{price}",
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = alt?.CharacterId   ?? 0,
                CharacterName = alt?.CharacterName ?? "",
                LocationId    = buyAt,
                LocationName  = buyName,
                TypeId        = bpTypeId,
                TypeName      = bpName,
                Priority      = WorklistPriority.ServesOther,
            });
        }

        return items;
    }

    /// <summary>
    /// Units of each material that live jobs will deliver.
    ///
    /// <para>Counted in units rather than runs, because a reaction run yields thousands and a
    /// component run yields one; comparing runs against a material shortfall would be comparing
    /// different things. "ready" counts too — the job is finished and the units exist, they are
    /// just not collected.</para>
    /// </summary>
    private static async Task<Dictionary<int, long>> InFlightOutputAsync(
        AppDbContext db, ProductionContext ctx, List<int> typeIds,
        HashSet<long>? scope, CancellationToken ct)
    {
        if (typeIds.Count == 0) return [];

        var jobs = await db.EsiIndustryJobs.AsNoTracking()
            .Where(j => (j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                        && j.ProductTypeId != null && typeIds.Contains(j.ProductTypeId!.Value))
            .Select(j => new { j.ProductTypeId, j.Runs, j.FacilityId })
            .ToListAsync(ct);

        return jobs
            .Where(j => scope is null || scope.Contains(j.FacilityId))
            .GroupBy(j => j.ProductTypeId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Sum(j => (long)j.Runs
                                * Math.Max(1, ctx.BlueprintByProduct.TryGetValue(g.Key, out var bp)
                                                  ? bp.Quantity : 1)));
    }

    /// <summary>
    /// How much of each shortfall is already covered in an unrefined form, and by what.
    ///
    /// <para>Counts what is on order as well as what is held. An open buy order for compressed
    /// gas is as surely incoming as one for the gas itself, and ignoring it raised a second
    /// purchase for material already bought — the same double-buy the direct on-order check
    /// exists to prevent, one conversion removed.</para>
    /// </summary>
    private static async Task<Dictionary<int, (long Units, string Note)>> SubstituteStockAsync(
        AppDbContext db, Dictionary<int, List<Substitute>> subs,
        List<int> shortTypeIds, ProductionCalculatorService.AssetReach reach,
        CancellationToken ct)
    {
        var wanted = shortTypeIds.Where(subs.ContainsKey).ToList();
        if (wanted.Count == 0) return [];

        var sourceIds = wanted.SelectMany(w => subs[w]).Select(s => s.SourceTypeId).Distinct().ToList();

        var held = (await db.EsiAssets.AsNoTracking()
                .Where(a => sourceIds.Contains(a.TypeId))
                .Select(a => new { a.ItemId, a.TypeId, a.RootLocationId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => reach.Counts(a.ItemId, a.RootLocationId, a.OwnerType, a.OwnerId))
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        var ordered = await OnOrderAsync(db, sourceIds, ct);

        var result = new Dictionary<int, (long, string)>();

        foreach (var typeId in wanted)
        {
            long total = 0;
            var  from  = new List<string>();

            // Each source is counted in full against every product it yields. One batch of ice
            // gives all of its outputs at once, so there is nothing to apportion.
            foreach (var s in subs[typeId].OrderBy(s => s.SourceName))
            {
                var have  = held.GetValueOrDefault(s.SourceTypeId);
                var due   = ordered.GetValueOrDefault(s.SourceTypeId);
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

    private async Task<HashSet<long>?> ScopeAsync(AppDbContext db, int parkId, CancellationToken ct)
    {
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
        {
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking()
                .Select(s => s.LocationId).ToListAsync(ct));

            // The park's own facilities are always in scope, as the job generator has them: a
            // job's inputs sitting in the structure it runs in cannot sensibly be called out of
            // reach, and the jobs running there are the ones whose output has to count.
            scope.UnionWith(await db.IndyStructures.AsNoTracking()
                .Where(s => s.ParkId == parkId && s.RealStructureId != null)
                .Select(s => s.RealStructureId!.Value).ToListAsync(ct));
        }
        return scope;
    }

    /// <summary>
    /// Types a Build rule has taken responsibility for, so this generator leaves them alone.
    ///
    /// <para>⚠️ Only the ones something can actually make. A group is a list of items and a rule
    /// applies to all of them, so a Build rule routinely covers something with no blueprint at
    /// all — a planetary product like Self-Harmonizing Power Core sitting in a group beside
    /// components that are built. Claiming those here dropped them from the worklist entirely:
    /// nothing bought them because a Build rule covered them, and no job could be made because
    /// nothing manufactures them. No row, no warning, and the job generator's own comment on
    /// skipping them says "a Buy rule's job, not this" — the rule that had just been switched
    /// away.</para>
    ///
    /// <para>Tested against the same index the job generator uses, so the two agree by
    /// construction rather than by both remembering to. This does not second-guess a rule: a
    /// buildable item under a Buy rule is still bought, which is a legitimate choice, and a
    /// buildable item under a Build rule is still left to the job side. Only the impossible case
    /// falls back.</para>
    /// </summary>
    private static async Task<HashSet<int>> BuildManagedTypesAsync(
        AppDbContext db, ProductionContext ctx, CancellationToken ct) =>
        (await db.InvLevelItems.AsNoTracking()
            .Where(i => db.WorklistInvRules.Any(r => r.Enabled && r.Action == "Build"
                                                  && r.GroupId == i.GroupId))
            .Select(i => i.TypeId)
            .ToListAsync(ct))
        .Where(ctx.BlueprintByProduct.ContainsKey)
        .ToHashSet();

    /// <summary>Open buy orders, deduped the way the rest of the tool does: a corp order placed
    /// by one of our characters comes back from both endpoints under the same id.</summary>
    private static async Task<Dictionary<int, long>> OnOrderAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        (await db.EsiMarketOrders.AsNoTracking()
                .Where(o => o.IsBuyOrder && !o.IsHistory && typeIds.Contains(o.TypeId))
                .Select(o => new { o.OrderId, o.OwnerType, o.TypeId, o.VolumeRemain })
                .ToListAsync(ct))
            .GroupBy(o => o.OrderId)
            .Select(g => g.FirstOrDefault(o => o.OwnerType == "corporation") ?? g.First())
            .GroupBy(o => o.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(o => (long)o.VolumeRemain));
}
