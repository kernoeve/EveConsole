using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Jobs to start, and who should start them.
///
/// Demand comes from inventory rules marked Build: a group short of target whose items are made
/// rather than bought. Each shortfall becomes one or more jobs, assigned to the least capable
/// character who can actually run them — the alt who can build titans should not be filled with
/// work anyone could do, because that capacity is the one thing that cannot be substituted.
///
/// <para><b>A shortfall is not a job.</b> Twenty-five thousand units is a job of 25,000 runs only
/// if a print will carry that many and the player is content to wait a week for any of it. So the
/// shortfall is split: capped by the configured maximum job length, by the blueprint's own run
/// limit, and by the licensed runs left on a copy — then handed out one print per job, since a
/// print is locked for the duration of the job it is in. The pieces can land on different
/// characters and different slots, which is the point.</para>
///
/// <para>Readiness earns its keep here. A job whose materials are not at the structure is
/// Blocked and says what is missing; one that is ready to go but has no free slot is Waiting;
/// only a job that could be started right now is Ready. Because materials are drawn down job by
/// job, a shortfall with enough on hand for two of its five jobs reports exactly that — two
/// Ready, three waiting on materials — rather than one blocked lump. That distinction is the
/// whole reason the tool exists: logging in to start a job and finding the inputs elsewhere is
/// the cost being paid today.</para>
/// </summary>
public class IndustryJobGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService       assignment,
    IndustryBlueprintService        blueprints,
    IndustryTimeService             times,
    InvLevelService                 invLevels,
    ProductionCalculatorService     production,
    WorklistSettings                settings) : IWorklistGenerator
{
    public string Id          => "industry_jobs";
    public string DisplayName => "Industry Jobs";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = settings.IndustryParkId;
        if (parkId <= 0) return [];

        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);
        if (rules.Count == 0) return [];

        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];

        // Loaded once per run and passed down. Calculate() is pure given a context, so each item
        // costs only arithmetic — and a field would race, since generators run in parallel and
        // two refreshes can overlap.
        var ctx     = await production.LoadContextAsync(parkId, ct);
        var timeCtx = await times.LoadAsync(parkId, ct);

        // Structures by the real facility they are linked to, so a planned job's station can be
        // turned back into the park row whose rigs and security class decide its run time. Only
        // linked structures can be checked for materials either — an unlinked one models rigs
        // but points at no real place, so nothing can be counted there.
        var linked = await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == parkId && s.RealStructureId != null)
            .ToListAsync(ct);
        var structureBySite = linked
            .GroupBy(s => s.RealStructureId!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var siteIds = structureBySite.Keys.ToList();

        // Everything sitting in the park's real facilities, in one read. A split turns a handful
        // of items into hundreds of jobs, and a per-job asset query made the generator take
        // seconds; the park's own stock is a few thousand rows, so it is cheaper to hold all of
        // it than to ask repeatedly.
        var siteAssets = await db.EsiAssets.AsNoTracking()
            .Where(a => siteIds.Contains(a.RootLocationId))
            .Select(a => new { a.RootLocationId, a.TypeId, a.OwnerId, a.OwnerType, a.Quantity })
            .ToListAsync(ct);

        // Split by whose it is, because the two are looked up differently: corp stock at a
        // facility serves any alt whose config includes it, whoever the holding corp is, while
        // personal stock only serves the character it belongs to.
        var stock = new SiteStock(
            siteAssets.Where(a => a.OwnerType == "corporation")
                      .GroupBy(a => (a.RootLocationId, a.TypeId))
                      .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)),
            siteAssets.Where(a => a.OwnerType != "corporation")
                      .GroupBy(a => (a.RootLocationId, a.TypeId, a.OwnerId))
                      .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)));

        // Rig bonuses key off the item's category, which needs the SDE group tree.
        var typeToGroup = ctx.TypeGroupMap.ToDictionary(kv => kv.Key, kv => kv.Value.GroupId);
        var groupInfo   = ctx.GroupCatMap.ToDictionary(
            kv => kv.Key,
            kv => new IndyRigMatching.GroupInfo(kv.Value.GroupId, kv.Value.CategoryId, kv.Value.Name));

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var items     = new List<WorklistItem>();
        var slotsLeft = candidates.ToDictionary(
            c => c.Config.CharacterId,
            c => new Dictionary<IndustryPool, int>(c.FreeSlots));

        // Materials already spoken for by jobs planned earlier in this pass, keyed by site and
        // type. Without it every job in a split would compare against the same untouched pile
        // and all five would report Ready off one job's worth of stock.
        var committed = new Dictionary<(long Site, int TypeId), long>();

        // Planning is pure given the context, so the same item at the same size and ME always
        // costs the same. A split is mostly equal-sized jobs off one print, so this collapses a
        // nine-job reaction into two plans instead of nine.
        var planCache = new Dictionary<(int TypeId, long Qty, int Me), Dictionary<int, long>>();

        // Rules in a fixed order, and items within them too, so the greedy allocation below
        // walks demand identically on every run and therefore assigns identically.
        foreach (var rule in rules.OrderByDescending(r => r.ThresholdPercent).ThenBy(r => r.Id))
        {
            if (!groups.TryGetValue(rule.GroupId, out var group)) continue;

            var groupItems = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == group.Id)
                .ToListAsync(ct);
            if (groupItems.Count == 0) continue;

            var typeIds = groupItems.Select(i => i.TypeId).Distinct().ToList();
            var avail   = await invLevels.LoadAvailableAsync(group, typeIds, ct);
            var names   = await invLevels.GetTypeNamesAsync(typeIds, ct);

            // Blueprints that make these items, what each needs by way of skills, and how many
            // runs one print will carry.
            var products = await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => typeIds.Contains(p.ProductTypeId))
                .ToListAsync(ct);
            var bpIds = products.Select(p => p.TypeId).Distinct().ToList();
            var bpSkills = (await db.SdeBlueprintSkills.AsNoTracking()
                    .Where(s => bpIds.Contains(s.TypeId))
                    .ToListAsync(ct))
                .GroupBy(s => (s.TypeId, s.Activity))
                .ToDictionary(g => g.Key, g => (IReadOnlyList<SdeBlueprintSkill>)g.ToList());
            var runLimits = await db.SdeBlueprints.AsNoTracking()
                .Where(b => bpIds.Contains(b.TypeId))
                .ToDictionaryAsync(b => b.TypeId, b => b.MaxProductionLimit, ct);

            var printsByType = await blueprints.LoadAsync(bpIds, ct);

            foreach (var gi in groupItems.OrderBy(i => i.TypeId))
            {
                avail.TryGetValue(gi.TypeId, out var av);
                var need = InvRuleShortfall.For(rule, group, gi, av);
                if (need is null || need.Shortfall <= 0) continue;

                var product = products
                    .Where(p => p.ProductTypeId == gi.TypeId)
                    .OrderBy(p => p.TypeId)
                    .FirstOrDefault();
                if (product is null) continue;   // nothing makes it — a Buy rule's job, not this

                var name       = names.GetValueOrDefault(gi.TypeId, $"Type {gi.TypeId}");
                var isReaction = product.Activity == "reaction";
                var pool       = isReaction ? IndustryPool.Reaction : IndustryPool.Manufacturing;
                var required   = bpSkills.GetValueOrDefault((product.TypeId, product.Activity), []);
                var eligible   = IndustryAssignmentService.EligibleFor(candidates, pool, required);

                // An inventory level is more urgent the emptier it is, so the priority carries
                // how far below target it has fallen rather than treating every shortfall alike.
                var priority = WorklistPriority.ForStock(need.Percent);

                var head = $"{group.Name} · {need.StockText}.{need.FillText(rule)}";

                if (eligible.Count == 0)
                {
                    items.Add(Unstartable(rule, gi.TypeId, name, priority,
                        $"{head} Build {need.Shortfall:N0}.",
                        required.Count > 0
                            ? "No enabled character has the skills for this job"
                            : "No enabled character runs this activity"));
                    continue;
                }

                // Where the park sends this job. Facility assignment is by category, not by
                // quantity, so one probe at the full shortfall settles it for every split.
                var probe    = await PlanRootJobAsync(ctx, gi.TypeId, need.Shortfall, ct: ct);
                var siteId   = probe?.StationId;
                var siteName = probe?.StationName.Length > 0 ? probe.StationName
                             : probe?.StructureName ?? "";

                if (siteId is null)
                {
                    items.Add(Unstartable(rule, gi.TypeId, name, priority,
                        $"{head} Build {need.Shortfall:N0}.",
                        siteName.Length > 0
                            ? $"{siteName} is not linked to a real structure, so materials cannot be checked"
                            : "No linked structure for this job, so materials cannot be checked"));
                    continue;
                }

                structureBySite.TryGetValue(siteId.Value, out var structure);
                var catKey = IndyRigMatching.ItemCategoryKey(
                                 gi.TypeId, isReaction, typeToGroup, groupInfo);

                var reaches = eligible
                    .Select(c => new WorklistIndyCharReach(
                        c.Config.CharacterId, c.Config.IncludeCorpAssets, c.Config.IncludePersonalAssets))
                    .ToList();

                var prints = IndustryBlueprintService.UsableAt(
                    printsByType.GetValueOrDefault(product.TypeId, []), siteId.Value, reaches);

                var runsNeeded = IndustryJobSplit.RunsFor(need.Shortfall, Math.Max(1, product.Quantity));

                if (prints.Count == 0)
                {
                    items.Add(Unstartable(rule, gi.TypeId, name, priority,
                        $"{head} Build {need.Shortfall:N0} ({runsNeeded:N0} run(s)) at {siteName}.",
                        $"No usable blueprint at {siteName} — every print is elsewhere, "
                        + "out of reach, or locked in a running job",
                        siteId.Value, siteName));
                    continue;
                }

                var split = IndustryJobSplit.Plan(
                    runsNeeded,
                    print => IndustryTimeService.PerRunSeconds(
                                 timeCtx, product.TypeId, isReaction, print.Te,
                                 structure, catKey, eligible[0].Skills),
                    settings.MaxJobDaysFor(pool),
                    runLimits.GetValueOrDefault(product.TypeId),
                    prints);

                for (var i = 0; i < split.Jobs.Count; i++)
                {
                    var job = split.Jobs[i];

                    // Whoever can reach this print, can run this activity, and has a slot free.
                    // Reach is checked per print because a copy in a personal hangar is usable
                    // by exactly one alt, whatever the corp hangar holds.
                    var owner = eligible.FirstOrDefault(c =>
                        new WorklistIndyCharReach(c.Config.CharacterId,
                            c.Config.IncludeCorpAssets, c.Config.IncludePersonalAssets).CanUse(job.Print)
                        && slotsLeft[c.Config.CharacterId].GetValueOrDefault(pool) > 0);

                    var busy = owner is null;
                    owner ??= eligible.FirstOrDefault(c =>
                        new WorklistIndyCharReach(c.Config.CharacterId,
                            c.Config.IncludeCorpAssets, c.Config.IncludePersonalAssets).CanUse(job.Print))
                        ?? eligible[0];

                    var readiness = WorklistReadiness.Ready;
                    var blockedBy = "";

                    // Materials for this job at this print's ME, against what is left at the site
                    // after the jobs already planned in this pass.
                    var wanted   = job.Runs * (long)Math.Max(1, product.Quantity);
                    var cacheKey = (gi.TypeId, wanted, job.Print.Me);
                    if (!planCache.TryGetValue(cacheKey, out var needed))
                        planCache[cacheKey] = needed =
                            await MaterialsForAsync(ctx, gi.TypeId, wanted, job.Print.Me, ct);

                    var missing = MissingAtSite(
                        stock, ctx, owner, needed, siteId.Value, committed);

                    if (missing.Count > 0)
                    {
                        readiness = WorklistReadiness.Blocked;
                        blockedBy = $"Materials not at {siteName}: "
                                  + string.Join(", ", missing.Take(4))
                                  + (missing.Count > 4 ? $", and {missing.Count - 4} more" : "");
                    }
                    else if (busy)
                    {
                        // Everyone able to do it is busy — real information, and different from
                        // being unable to do it at all.
                        readiness = WorklistReadiness.Waiting;
                        blockedBy = "Every character who can run this has all slots busy";
                    }
                    else
                    {
                        // Only a job that can actually start consumes a slot and its materials.
                        slotsLeft[owner.Config.CharacterId][pool] -= 1;
                        foreach (var (typeId, qty) in needed)
                            committed[(siteId.Value, typeId)] =
                                committed.GetValueOrDefault((siteId.Value, typeId)) + qty;
                    }

                    var runsText = product.Quantity > 1
                        ? $"{job.Runs:N0} run(s) → {wanted:N0} × {name}"
                        : $"{job.Runs:N0} × {name}";
                    var ofText   = split.Jobs.Count > 1 ? $" (job {job.Index} of {job.Of})" : "";
                    var duration = IndustryJobSplit.Duration(job.Seconds);
                    var durText  = duration.Length > 0 ? $" ~{duration}." : "";
                    var leftover = i == split.Jobs.Count - 1 && split.RunsUnassigned > 0
                        ? $" {split.RunsUnassigned:N0} further run(s) need a print — none free."
                        : "";

                    items.Add(new WorklistItem
                    {
                        // No character in the key. Assignment can legitimately move between
                        // refreshes as slots free up, and a key that moved with it would reset
                        // the item's age and silently drop its snooze. The index keeps the
                        // pieces of one split independently snoozable.
                        Key           = $"industry_job:{rule.Id}:{gi.TypeId}:{job.Index}",
                        Source        = Id,
                        Title         = $"Run job — {runsText}",
                        Detail        = $"{head} Short {need.Shortfall:N0}{ofText}. "
                                      + $"{job.Print.Describe()} at {siteName}.{durText}{leftover}",
                        Readiness     = readiness,
                        BlockedBy     = blockedBy,
                        CharacterId   = owner.Config.CharacterId,
                        CharacterName = owner.Config.CharacterName,
                        LocationId    = siteId.Value,
                        LocationName  = siteName,
                        TypeId        = gi.TypeId,
                        TypeName      = name,
                        Priority      = priority,
                    });
                }
            }
        }

        return items;
    }

    /// <summary>A shortfall that cannot become a job at all, reported once with the reason.</summary>
    private WorklistItem Unstartable(
        WorklistInvRule rule, int typeId, string name, int priority,
        string detail, string blockedBy, long locationId = 0, string locationName = "") =>
        new()
        {
            Key          = $"industry_job:{rule.Id}:{typeId}:0",
            Source       = Id,
            Title        = $"Start job — {name}",
            Detail       = detail,
            Readiness    = WorklistReadiness.Blocked,
            BlockedBy    = blockedBy,
            LocationId   = locationId,
            LocationName = locationName,
            TypeId       = typeId,
            TypeName     = name,
            Priority     = priority,
        };

    /// <summary>
    /// Exactly what one job consumes, at the ME and rig bonuses that will actually apply.
    ///
    /// Planned through <see cref="ProductionCalculatorService"/> against the configured park, so
    /// the figures match what the Production Calculator would quote for the same build. Base SDE
    /// quantities were tried first and were wrong in the direction that matters: over-stating a
    /// requirement produces a job reported as blocked for materials that are sitting in the
    /// station. On an expensive, rarely-run build the difference is not a rounding error, and
    /// waiting on a job that could have started is the exact cost this tool exists to remove.
    ///
    /// <para>The ME is the chosen print's own, not a default. Picking a TE0 ME7 copy and then
    /// checking materials at ME10 asks for less than the job will actually eat.</para>
    /// </summary>
    private async Task<Dictionary<int, long>> MaterialsForAsync(
        ProductionContext ctx, int productTypeId, long quantity, int me, CancellationToken ct)
    {
        var root = await PlanRootJobAsync(ctx, productTypeId, quantity, me, ct);
        return root is null
            ? []
            : root.Materials
                  .GroupBy(m => m.MaterialTypeId)
                  .ToDictionary(g => g.Key, g => (long)g.Sum(m => m.TotalQty));
    }

    /// <summary>
    /// The root job of a plan for one item: its own inputs, and the facility the park assigns it
    /// to. Sub-components the plan would build are separate jobs with their own worklist items.
    /// </summary>
    private async Task<PlanJob?> PlanRootJobAsync(
        ProductionContext ctx, int productTypeId, long quantity, int? me = null,
        CancellationToken ct = default)
    {
        var entry = new ProductionQueueEntry
        {
            TypeId   = productTypeId,
            Quantity = (int)Math.Clamp(quantity, 1, int.MaxValue),
            MeLevel  = me ?? await production.GetDefaultMeAsync(productTypeId, ct),
        };

        return production.Calculate([entry], ctx)
                         .AllJobs.FirstOrDefault(j => j.OutputTypeId == productTypeId);
    }

    /// <summary>
    /// Which of the needed materials are not within the chosen character's reach at the build
    /// site, named rather than counted — "short Tritanium" tells you what to move.
    ///
    /// Reach is deliberately per character. Materials pooled in a corp hangar serve every alt in
    /// that corp, while a player who keeps stock in personal hangars needs the personal side
    /// counted instead; assuming either would suggest jobs that cannot start.
    ///
    /// <para><paramref name="committed"/> holds what jobs already planned in this pass will
    /// consume. Two jobs off one pile are not both startable on one job's worth of material, and
    /// drawing the pile down as it is spent is what lets a five-job split report the first two
    /// Ready and the rest waiting.</para>
    /// </summary>
    private static List<string> MissingAtSite(
        SiteStock stock, ProductionContext ctx, IndustryCandidate who,
        Dictionary<int, long> needed, long siteId, Dictionary<(long, int), long> committed)
    {
        if (needed.Count == 0) return [];

        var shortIds = new List<int>();

        foreach (var (typeId, wanted) in needed)
        {
            var reachable = stock.Reachable(siteId, typeId, who.Config);
            if (reachable - committed.GetValueOrDefault((siteId, typeId)) < wanted)
                shortIds.Add(typeId);
        }

        return shortIds
            .OrderBy(id => id)
            .Select(id => ctx.TypeNames.GetValueOrDefault(id, $"Type {id}"))
            .ToList();
    }

    /// <summary>What is on hand at the park's facilities, indexed for the two ways it is asked
    /// about. Materials pooled in a corp hangar serve every alt whose config includes them;
    /// personal stock serves only its owner.</summary>
    private sealed record SiteStock(
        Dictionary<(long Site, int TypeId), long>               Corp,
        Dictionary<(long Site, int TypeId, long OwnerId), long> Personal)
    {
        public long Reachable(long siteId, int typeId, WorklistIndyChar cfg)
        {
            long total = 0;
            if (cfg.IncludeCorpAssets)     total += Corp.GetValueOrDefault((siteId, typeId));
            if (cfg.IncludePersonalAssets) total += Personal.GetValueOrDefault(
                                                        (siteId, typeId, cfg.CharacterId));
            return total;
        }
    }
}
