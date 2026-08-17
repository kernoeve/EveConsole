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
    AppErrorLogger                  errorLogger) : IWorklistGenerator
{
    public string Id          => "material_purchases";
    public string DisplayName => "Material Purchases";

    /// <summary>Where a slice of demand came from, so a row can say what it is serving.</summary>
    private sealed record DemandSource(string What, long Units);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = settings.IndustryParkId;
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

        await AddStockDemandAsync(db, ctx, Want, ct);
        await AddOrderDemandAsync(db, ctx, reach, scope, Want, ct);

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

        // Anything a Build rule covers is made, not bought.
        var buildManaged = await BuildManagedTypesAsync(db, ct);

        // Blueprints the plan itself asks to buy — BPC-only items carry their copies as a raw
        // material with a real quantity. Those are better handled there than by the
        // nothing-owned check below, which cannot say "you have two copies and need four".
        var bpShortfalls = shortfalls.Where(r => ctx.BpTypeIds.Contains(r.TypeId))
                                     .Select(r => r.TypeId).ToHashSet();

        var buyAt   = settings.IndustryBuyLocationId;
        var buyName = settings.IndustryBuyLocationName;
        var alt     = buyAt > 0 ? (await marketAlts.GetByLocationAsync(ct)).GetValueOrDefault(buyAt) : null;

        var items = new List<WorklistItem>();
        items.AddRange(PrintTasks(ctx, queue, allPrints, owned, bpShortfalls,
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

        foreach (var raw in shortfalls.OrderBy(r => r.TypeName))
        {
            if (buildManaged.Contains(raw.TypeId)) continue;

            var ordered  = onOrder.GetValueOrDefault(raw.TypeId);
            var building = inFlight.GetValueOrDefault(raw.TypeId);
            var held     = subsStock.GetValueOrDefault(raw.TypeId);
            var short_   = raw.Missing - ordered - building - held.Units;
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
    private async Task AddStockDemandAsync(
        AppDbContext db, ProductionContext ctx,
        Action<int, long, string> want, CancellationToken ct)
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
                var need = InvRuleShortfall.For(rule, group, gi, av);
                if (need is null || need.Shortfall <= 0) continue;
                if (!ctx.BlueprintByProduct.ContainsKey(gi.TypeId)) continue;

                want(gi.TypeId, need.Shortfall, "stock target");
            }
        }
    }

    /// <summary>Outstanding customer orders, less what is built or building.</summary>
    private static async Task AddOrderDemandAsync(
        AppDbContext db, ProductionContext ctx, ProductionCalculatorService.AssetReach reach,
        HashSet<long>? scope, Action<int, long, string> want, CancellationToken ct)
    {
        // The Order Rules tab is what says orders should be planned at all.
        if (!await db.WorklistOrderRules.AsNoTracking().AnyAsync(r => r.Enabled, ct)) return;

        var orders = await db.TrackedOrders.AsNoTracking()
            .Where(o => o.Status == "pending").ToListAsync(ct);
        if (orders.Count == 0) return;

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

        foreach (var g in orders.GroupBy(o => o.TypeId).OrderBy(g => g.Key))
        {
            var outstanding = g.Sum(o => (long)o.Units)
                            - onHand.GetValueOrDefault(g.Key)
                            - inBuild.GetValueOrDefault(g.Key);
            if (outstanding <= 0) continue;
            if (!ctx.BlueprintByProduct.ContainsKey(g.Key)) continue;

            want(g.Key, outstanding, $"{g.Count()} order(s)");
        }
    }

    /// <summary>Blueprints nothing in the queue can be built without, because none is owned.</summary>
    private static List<WorklistItem> PrintTasks(
        ProductionContext ctx, List<ProductionQueueEntry> queue,
        List<BlueprintStock> allPrints, PrintOwnership owned,
        HashSet<int> alreadyCounted,
        long buyAt, string buyName, WorklistMarketAlt? alt)
    {
        var items = new List<WorklistItem>();

        foreach (var entry in queue.OrderBy(q => q.TypeId))
        {
            if (!ctx.BlueprintByProduct.TryGetValue(entry.TypeId, out var bp)) continue;
            if (alreadyCounted.Contains(bp.TypeId)) continue;   // the plan is already buying it

            var mine = allPrints.Where(p => p.TypeId == bp.TypeId).ToList();
            if (IndustryBlueprintService.OwnedAnywhere(mine, owned)) continue;

            var bpName = ctx.TypeNames.GetValueOrDefault(bp.TypeId, $"Blueprint {bp.TypeId}");
            var price  = ctx.BpcPerRun.TryGetValue(bp.TypeId, out var opts) && opts.Count > 0
                ? $" Copies have been seen on contract from {opts.Min(o => o.PerRun):N0} ISK a run."
                : "";

            // One print per run. These are bought as copies, and a copy is spent by the job that
            // uses it, so two builds need two. The count carried no number at all until now, on
            // the reasoning that a single original would cover every run — true of an original,
            // but not of what actually gets bought, and it made this demand contribute nothing
            // when it merged with a stocking rule for the same print.
            var printsNeeded = IndustryJobSplit.RunsFor(entry.Quantity, Math.Max(1, bp.Quantity));

            items.Add(new WorklistItem
            {
                Key           = $"industry_print:{bp.TypeId}",
                Source        = "material_purchases",
                Kind          = WorklistKind.Buy,
                Title         = $"{bpName} — BPO/BPC × {printsNeeded:N0}",
                TitleTag      = "BPO/BPC",
                Quantity      = printsNeeded,
                MergeKey      = WorklistItem.BuyMergeKey(buyAt, bp.TypeId),
                Detail        = $"No blueprint owned, so {entry.TypeName} cannot be built at all. "
                              + $"{entry.Quantity:N0} wanted.{price}",
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = alt?.CharacterId   ?? 0,
                CharacterName = alt?.CharacterName ?? "",
                LocationId    = buyAt,
                LocationName  = buyName,
                TypeId        = bp.TypeId,
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

    private static async Task<HashSet<int>> BuildManagedTypesAsync(AppDbContext db, CancellationToken ct) =>
        (await db.InvLevelItems.AsNoTracking()
            .Where(i => db.WorklistInvRules.Any(r => r.Enabled && r.Action == "Build"
                                                  && r.GroupId == i.GroupId))
            .Select(i => i.TypeId)
            .ToListAsync(ct))
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
