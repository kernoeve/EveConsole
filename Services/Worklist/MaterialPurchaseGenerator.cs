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
/// right answer, and it can only be done in one place.</para>
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
    InvLevelService                 invLevels,
    MaterialSubstitutionService     substitution,
    ProductionCalculatorService     production,
    WorklistMarketAltService        marketAlts,
    WorklistSettings                settings,
    OutbidOrderService              outbidOrders,
    AppErrorLogger                  errorLogger) : IWorklistGenerator
{
    public string Id          => "material_purchases";
    public string DisplayName => "Material Purchases";

    /// <summary>Where a slice of demand came from, so a row can say what it is serving.</summary>
    private sealed record DemandSource(string What, long Units);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];

        var ctx     = await production.LoadContextAsync(parkId, ct);
        var corps   = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);
        var reaches = candidates
            .Select(c => WorklistIndyCharReach.Of(c, corps))
            .ToList();

        var scope = await ScopeAsync(db, ct);
        var reach = new ProductionCalculatorService.AssetReach(
            scope, await AssetExclusions.UnusableItemIdsAsync(db, ct),
            corps);

        var allPrints = await blueprints.LoadAllAsync(ct);
        var owned     = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var meMap     = IndustryBlueprintService.BestMeByProduct(
                            allPrints, ctx.BlueprintByProduct, owned);

        // ── What has to be produced, from both demands ────────────────────────

        // Collected first and turned into a queue after, so the two demands can be gathered
        // without either needing to know how an efficiency is resolved.
        var serving = new Dictionary<int, List<DemandSource>>();

        void Want(int typeId, long units, string what)
        {
            if (units <= 0) return;
            serving.TryAdd(typeId, []);
            serving[typeId].Add(new DemandSource(what, units));
        }

        // ⚠️ Orders first, and what they claim is passed to the stock rules — they are the first
        // call on stock that already exists, and what they carry away cannot also be the thing
        // that keeps the shelf full.
        //
        // Both sides used to net the same supply independently: an Avatar ordered with one in
        // build read as "the order is covered" AND "the shelf is full", so neither asked for
        // anything and not one material was purchased for the replacement. The class comment
        // below describes this exact failure for materials, where it was fixed by pooling; the
        // finished item it is all for was still being counted twice.
        var claimed = await AddOrderDemandAsync(
            db, settings.PlanCustomerOrders, ctx, reach, scope, Want, ct);
        await AddStockDemandAsync(db, ctx, Want, claimed, ct);

        if (serving.Count == 0) return [];

        var queue = new List<ProductionQueueEntry>();
        foreach (var (typeId, sources) in serving.OrderBy(s => s.Key))
        {
            queue.Add(new ProductionQueueEntry
            {
                TypeId   = typeId,
                TypeName = ctx.TypeNames.GetValueOrDefault(typeId, $"Type {typeId}"),
                Quantity = (int)Math.Clamp(sources.Sum(s => s.Units), 1, int.MaxValue),
                MeLevel  = meMap.TryGetValue(typeId, out var me)
                             ? me
                             : await production.GetDefaultMeAsync(typeId, ct),
            });
        }

        // ── Plan it, once ─────────────────────────────────────────────────────

        ProductionPlan plan;
        List<PlanRawMaterial> shortfalls;
        try
        {
            plan = production.Calculate(queue, ctx, meOverrides: meMap);
            await production.ApplyAvailabilityAsync(
                plan, ProductionCalculatorService.MissingMode.Assets, ct, reach);
            shortfalls = plan.RawMaterials.Where(r => r.Missing > 0).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(MaterialPurchaseGenerator), $"Park {parkId}", ex);
            return [];
        }

        // Anything a Build rule covers is made, not bought — unless nothing can make it, in which
        // case buying is the only way it ever arrives.
        var buildManaged = await BuildManagedTypesAsync(db, ctx, ct);

        // Blueprints the plan itself asks to buy — BPC-only items carry their copies as a raw
        // material with a real quantity. Those are better handled there than by the
        // nothing-owned check below, which cannot say "you have two copies and need four".
        var bpShortfalls = shortfalls.Where(r => ctx.BpTypeIds.Contains(r.TypeId))
                                     .Select(r => r.TypeId).ToHashSet();

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
        items.AddRange(PrintTasks(ctx, queue, allPrints, owned, bpShortfalls, inAssets, shelfWant,
                                  buyAt, buyName, alt));

        var onOrder = await OnOrderAsync(db, shortfalls.Select(s => s.TypeId).ToList(), ct);

        // Ore, ice and gas already held count toward what they turn into, so a shortfall covered
        // by unrefined stock does not become a purchase.
        var subs      = await substitution.LoadAsync(ct);
        var subsStock = await SubstituteStockAsync(db, subs, shortfalls, reach, ct);

        // Production already running that will yield the material. Assets alone under-count what
        // is coming: a reaction three days from delivery is material as surely bought as one on
        // an open order, and buying against it orders the same units twice.
        var inFlight = await InFlightOutputAsync(
            db, ctx, shortfalls.Select(s => s.TypeId).ToList(), scope, ct);

        // Materials the plan would still be missing if none of our orders existed — the ones an
        // order is actually needed for. See the note where these are reported.
        var needed = new HashSet<int>();

        foreach (var raw in shortfalls.OrderBy(r => r.TypeName))
        {
            if (buildManaged.Contains(raw.TypeId)) continue;

            var ordered  = onOrder.GetValueOrDefault(raw.TypeId);
            var building = inFlight.GetValueOrDefault(raw.TypeId);
            var held     = subsStock.GetValueOrDefault(raw.TypeId);
            var short_   = raw.Missing - ordered - building - held.Units;

            // ⚠️ Everything except the orders. An order covering the last of a shortfall is the
            // reason that shortfall is not a task, which makes it the one order whose failing
            // matters most — and subtracting it here would hide exactly that case.
            if (raw.Missing - building - held.Units > 0) needed.Add(raw.TypeId);

            if (short_ <= 0) continue;

            // A blueprint is acquired, not market-ordered, so it is titled the way the print
            // tasks are — either a BPO or a copy will do, and which is the player's call.
            var isPrint = ctx.BpTypeIds.Contains(raw.TypeId);

            items.Add(new WorklistItem
            {
                Key           = $"industry_buy:{raw.TypeId}",
                Source        = Id,
                // The amount belongs in the title — "buy this" without a number is not yet an
                // instruction — but the name leads, because the column sorts on this string and
                // a leading count sorts by digit, scattering an item's rows across the list.
                Kind          = WorklistKind.Buy,
                // "BPO/BPC" trails the name for the same reason, while still saying the purchase
                // is a contract rather than a market order, which the kind column cannot.
                Title         = isPrint ? $"{raw.TypeName} — BPO/BPC × {short_:N0}"
                                        : $"{raw.TypeName} × {short_:N0}",
                Quantity      = short_,
                TitleTag      = isPrint ? "BPO/BPC" : null,
                // Prints merge too. A job needing copies and a stocking rule wanting some on the
                // shelf are one trip to the contract window, exactly as two demands for the same
                // mineral are one order — the contract-versus-market distinction only matters
                // against a market row for the same type, and a blueprint never has one.
                MergeKey      = WorklistItem.BuyMergeKey(buyAt, raw.TypeId),
                // Both halves of the subtraction, so a merge with an inventory rule wanting the
                // same material nets the shared stock once rather than once per demand.
                // raw.Missing already has Available taken off it, so the gross figure is the
                // requirement itself and Available belongs with the supply.
                GrossDemand    = raw.Quantity,
                SupplyCredited = (long)raw.Available + ordered + building + held.Units,
                Detail        = $"{WantedBy(plan, raw.TypeId)}: need {raw.Quantity:N0}; "
                              + $"{raw.Available:N0} on hand{settings.IndustryScopeSuffix}"
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
                TypeId        = raw.TypeId,
                TypeName      = raw.TypeName,
                Priority      = WorklistPriority.OrderDriven,
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
    /// The jobs that actually consume this material, so a row can say why it is wanted.
    ///
    /// <para>Taken from the plan's own jobs rather than from the top of the queue. Naming the
    /// first few things asked for would put "2 Avatar" against every line, including materials no
    /// Avatar touches — a plausible sentence that happens to be false, which is worse than no
    /// explanation at all.</para>
    /// </summary>
    private static string WantedBy(ProductionPlan plan, int materialTypeId)
    {
        var users = plan.AllJobs
            .Where(j => j.Materials.Any(m => m.MaterialTypeId == materialTypeId))
            .OrderByDescending(j => j.Materials.Where(m => m.MaterialTypeId == materialTypeId)
                                               .Sum(m => (long)m.TotalQty))
            .ToList();
        if (users.Count == 0) return "Planned builds";

        var named = users.Take(2)
            .Select(j => $"{j.OutputTypeName} ({j.Runs:N0} run(s))")
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

    private async Task AddStockDemandAsync(
        AppDbContext db, ProductionContext ctx,
        Action<int, long, string> want, IReadOnlyDictionary<int, long> claimed,
        CancellationToken ct)
    {
        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);
        if (rules.Count == 0) return;

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        foreach (var rule in rules.OrderBy(r => r.Id))
        {
            if (!groups.TryGetValue(rule.GroupId, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id).ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();
            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);

            foreach (var gi in groupItems.OrderBy(i => i.TypeId))
            {
                avail.TryGetValue(gi.TypeId, out var av);
                var need = InvRuleShortfall.For(
                    rule, group, gi, av, claimed.GetValueOrDefault(gi.TypeId));
                if (need is null || need.Shortfall <= 0) continue;
                if (!ctx.BlueprintByProduct.ContainsKey(gi.TypeId)) continue;

                want(gi.TypeId, need.Shortfall, "stock target");
            }
        }
    }

    /// <summary>
    /// Outstanding customer orders, less what is built or building.
    ///
    /// <para>Returns how much existing stock the orders have spoken for, per type, so the stock
    /// rules can be told what is genuinely still on the shelf. Recorded even for items nothing
    /// can build — an order carries the hull away whether or not we could have made another.</para>
    /// </summary>
    private static async Task<Dictionary<int, long>> AddOrderDemandAsync(
        AppDbContext db, bool enabled, ProductionContext ctx, ProductionCalculatorService.AssetReach reach,
        HashSet<long>? scope, Action<int, long, string> want, CancellationToken ct)
    {
        // The Customer orders switch on the Sources tab is what says orders should be planned.
        if (!enabled) return [];

        var orders = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Status == "pending").ToListAsync(ct);
        if (orders.Count == 0) return [];

        var wanted = orders.Select(o => o.TypeId).Distinct().ToList();

        var onHand = (await db.EsiAssets.AsNoTracking()
                .Where(a => wanted.Contains(a.TypeId))
                .Select(a => new { a.ItemId, a.TypeId, a.RootLocationId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => reach.Counts(a.ItemId, a.RootLocationId, a.OwnerType, a.OwnerId))
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        // "ready" counts as in build: the job has finished and eaten its materials, but the
        // product is not in assets until collected, so ignoring it would buy for it twice.
        var inBuild = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => (j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                            && j.ProductTypeId != null && wanted.Contains(j.ProductTypeId!.Value))
                .Select(j => new { j.ProductTypeId, j.Runs, j.FacilityId })
                .ToListAsync(ct))
            .Where(j => scope is null || scope.Contains(j.FacilityId))
            .GroupBy(j => j.ProductTypeId!.Value)
            .ToDictionary(g => g.Key, g => (long)g.Sum(j => j.Runs));

        var claimed = new Dictionary<int, long>();

        foreach (var g in orders.GroupBy(o => o.TypeId).OrderBy(g => g.Key))
        {
            var gross  = g.Sum(o => (long)o.Units);
            var supply = onHand.GetValueOrDefault(g.Key) + inBuild.GetValueOrDefault(g.Key);

            // Recorded before the buildable test: an order takes the hull whether or not we
            // could have built another one, and the shelf is just as empty either way.
            claimed[g.Key] = Math.Min(gross, supply);

            var outstanding = gross - supply;
            if (outstanding <= 0) continue;
            if (!ctx.BlueprintByProduct.ContainsKey(g.Key)) continue;

            want(g.Key, outstanding, $"{g.Count()} order(s)");
        }

        return claimed;
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
        HashSet<int> alreadyCounted, Dictionary<int, int> ownedInAssets,
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

            // Supply from both tables. The blueprints table does not cover every structure the
            // assets table does — this corporation has 5,518 blueprint rows and none at UALX-3,
            // where assets list two Avatar copies — and "absent from that table" is not the same
            // fact as "not owned". Assets contribute a count only: no runs, ME or TE on those
            // rows, so they cannot be planned against, merely counted.
            var held = allPrints.Count(p => p.TypeId == bpTypeId && owned.Owns(p));
            if (held == 0) held = ownedInAssets.GetValueOrDefault(bpTypeId);

            var jobs  = jobNeed.GetValueOrDefault(bpTypeId);
            var shelf = shelfWant.GetValueOrDefault(bpTypeId);

            // An original is never spent by the job it runs, so one covers every run — but it does
            // not fill a shelf target, which asks for a print to be there.
            var anyOriginal = allPrints.Any(p => p.TypeId == bpTypeId && p.IsOriginal && owned.Owns(p));
            var demand      = (anyOriginal ? 0 : jobs) + shelf;

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
            var haveText = held > 0 ? $", {held:N0} owned" : ", none owned";

            items.Add(new WorklistItem
            {
                Key           = $"industry_print:{bpTypeId}",
                Source        = "material_purchases",
                Kind          = WorklistKind.Buy,
                Title         = $"{bpName} — BPO/BPC × {stillNeeded:N0}",
                TitleTag      = "BPO/BPC",
                Quantity      = stillNeeded,
                MergeKey      = WorklistItem.BuyMergeKey(buyAt, bpTypeId),
                Detail        = $"{string.Join(" + ", parts)}{haveText} — short {stillNeeded:N0}.{price}",
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = alt?.CharacterId   ?? 0,
                CharacterName = alt?.CharacterName ?? "",
                LocationId    = buyAt,
                LocationName  = buyName,
                TypeId        = bpTypeId,
                TypeName      = bpName,
                Priority      = WorklistPriority.OrderDriven,
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
        List<PlanRawMaterial> shortfalls, ProductionCalculatorService.AssetReach reach,
        CancellationToken ct)
    {
        var wanted = shortfalls.Select(s => s.TypeId).Where(subs.ContainsKey).ToList();
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

    private async Task<HashSet<long>?> ScopeAsync(AppDbContext db, CancellationToken ct)
    {
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking()
                .Select(s => s.LocationId).ToListAsync(ct));
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
