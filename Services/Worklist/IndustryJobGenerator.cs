using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Jobs to start, and who should start them.
///
/// Demand comes from two places and is pooled per item, because they are additive but the
/// production is not: inventory rules marked Build, and pending customer orders. An order for an
/// Avatar beside a standing target of one means building two, and planning each separately would
/// make two one-run jobs where the split logic should decide the shape. Whoever is waiting only
/// changes the urgency and the wording.
///
/// Each item's total becomes one or more jobs, assigned to the least capable character who can
/// actually run them — the alt who can build titans should not be filled with work anyone could
/// do, because that capacity is the one thing that cannot be substituted.
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
    IndustryDemandService           demands,
    InvLevelService                 invLevels,
    ProductionCalculatorService     production,
    WorklistSettings                settings) : IWorklistGenerator
{
    public string Id          => "industry_jobs";
    public string DisplayName => "Industry Jobs";

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        // No early exit on an empty rule set: customer orders are demand in their own right, so
        // a player who tracks orders and keeps no inventory targets still gets jobs.
        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);

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
        // Asset safety is excluded everywhere below. A wrap has to be unpacked wherever the game
        // put it and hauled back, so its contents cannot fill a job — and the flag sits only on
        // the wrap itself, so the whole container chain beneath it has to be found.
        var wrapped = await AssetExclusions.UnusableItemIdsAsync(db, ct);

        // Corp stock only counts for corporations the enabled characters are actually in. Being
        // able to see a corporation's hangars is not the same as being able to build from them:
        // a main in a large alliance corp exposes tens of thousands of rows that are other
        // people's, and treating them as material makes every shortfall look filled.
        var corps = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);

        // Wider than the candidates above on purpose: this only answers "does the player have one
        // of these at all", which decides whether a blocked job reads as "move it" or "buy one".
        var printOwner = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);

        bool Ours(string ownerType, long ownerId) =>
            ownerType != "corporation" || corps is null || corps.Contains(ownerId);

        var siteAssets = (await db.EsiAssets.AsNoTracking()
                .Where(a => siteIds.Contains(a.RootLocationId))
                .Select(a => new { a.ItemId, a.RootLocationId, a.TypeId, a.OwnerId, a.OwnerType, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId) && Ours(a.OwnerType, a.OwnerId))
            .ToList();

        // How far to look before calling something missing. Stock outside this counts as absent,
        // which is the point: material in another region is not material this job can use, and
        // treating it as present would suppress a purchase that genuinely needs making.
        //
        // The extra stations sit on top, because a trade hub is deliberately outside the home
        // region and stock waiting there is still stock in hand.
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
        {
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking()
                .Select(s => s.LocationId).ToListAsync(ct));
            // The park's own facilities are always in scope. A job's inputs sitting in the
            // structure it runs in cannot sensibly be called out of reach.
            scope.UnionWith(siteIds);
        }

        // Split by whose it is, because the two are looked up differently: corp stock at a
        // facility serves any alt whose config includes it, whoever the holding corp is, while
        // personal stock only serves the character it belongs to.
        var stock = new SiteStock(
            siteAssets.Where(a => a.OwnerType == "corporation")
                      .GroupBy(a => (a.RootLocationId, a.TypeId, a.OwnerId))
                      .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)),
            siteAssets.Where(a => a.OwnerType != "corporation")
                      .GroupBy(a => (a.RootLocationId, a.TypeId, a.OwnerId))
                      .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)));

        // Everything in scope, wherever it sits. Separate from the per-site view above and asking
        // a different question: the site view decides whether a job can start now, this one
        // decides whether the material exists at all — a haul or a purchase.
        var scopeRows = (await (scope is null
                    ? db.EsiAssets.AsNoTracking()
                    : db.EsiAssets.AsNoTracking().Where(a => scope.Contains(a.RootLocationId)))
                .Select(a => new { a.ItemId, a.TypeId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId) && Ours(a.OwnerType, a.OwnerId))
            .GroupBy(a => (a.TypeId, a.OwnerType, a.OwnerId))
            .Select(g => new { g.Key.TypeId, g.Key.OwnerType, g.Key.OwnerId,
                               Qty = g.Sum(a => (long)a.Quantity) })
            .ToList();

        var inScope = new ScopeStock(
            scopeRows.Where(a => a.OwnerType == "corporation")
                     .GroupBy(a => (a.TypeId, a.OwnerId)).ToDictionary(g => g.Key, g => g.Sum(a => a.Qty)),
            scopeRows.Where(a => a.OwnerType != "corporation")
                     .GroupBy(a => (a.TypeId, a.OwnerId)).ToDictionary(g => g.Key, g => g.Sum(a => a.Qty)));

        // Rig bonuses key off the item's category, which needs the SDE group tree.
        var typeToGroup = ctx.TypeGroupMap.ToDictionary(kv => kv.Key, kv => kv.Value.GroupId);
        var groupInfo   = ctx.GroupCatMap.ToDictionary(
            kv => kv.Key,
            kv => new IndyRigMatching.GroupInfo(kv.Value.GroupId, kv.Value.CategoryId, kv.Value.Name));

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var demand = await demands.GatherAsync(db, ctx, rules, groups, scope, wrapped, corps, inScope, ct);
        if (demand.Count == 0) return [];

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


        // Which blueprint makes each item is the calculator's choice, not a fresh one. Some
        // products have an unpublished "Test Reaction Blueprint" alongside the real formula with
        // a tiny output quantity — Tungsten Carbide's yields 20 a run against the real 10,000 —
        // and picking differently from the calculator would plan materials off one blueprint
        // while counting runs off another. It filters those out; reusing its index makes the two
        // agree by construction rather than by both remembering to.
        var demanded = demand.Keys.ToList();
        var bpIds = demanded
            .Select(id => ctx.BlueprintByProduct.GetValueOrDefault(id))
            .OfType<SdeBlueprintProduct>()
            .Select(p => p.TypeId).Distinct().ToList();

        var bpSkills = (await db.SdeBlueprintSkills.AsNoTracking()
                .Where(s => bpIds.Contains(s.TypeId))
                .ToListAsync(ct))
            .GroupBy(s => (s.TypeId, s.Activity))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SdeBlueprintSkill>)g.ToList());
        var names = await invLevels.GetTypeNamesAsync(demanded, ct);

        var printsByType = await blueprints.LoadAsync(bpIds, ct);

        // Most urgent first, then by type id, so the greedy slot and material allocation below
        // walks demand identically on every run and therefore assigns identically — and the work
        // someone is waiting on claims a slot before routine stock-keeping does.
        foreach (var d in demand.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.TypeId))
        {
                var product = ctx.BlueprintByProduct.GetValueOrDefault(d.TypeId);
                if (product is null) continue;   // nothing makes it — a Buy rule's job, not this

                var name       = names.GetValueOrDefault(d.TypeId, $"Type {d.TypeId}");
                var isReaction = product.Activity == "reaction";
                var pool       = isReaction ? IndustryPool.Reaction : IndustryPool.Manufacturing;
                var required   = bpSkills.GetValueOrDefault((product.TypeId, product.Activity), []);
                var eligible   = IndustryAssignmentService.EligibleFor(candidates, pool, required);

                // An inventory level is more urgent the emptier it is, so the priority carries
                // how far below target it has fallen rather than treating every shortfall alike.
                var priority = d.Priority;

                var head = d.Head;

                if (eligible.Count == 0)
                {
                    items.Add(Unstartable(d.TypeId, name, priority, pool, d.Units,
                        $"{head} Build {d.Units:N0}.",
                        required.Count > 0
                            ? "No enabled character has the skills for this job"
                            : "No enabled character runs this activity"));
                    continue;
                }

                // Where the park sends this job. Facility assignment is by category, not by
                // quantity, so one probe at the full shortfall settles it for every split.
                var probe    = await PlanRootJobAsync(ctx, d.TypeId, d.Units, ct: ct);
                var siteId   = probe?.StationId;
                var siteName = probe?.StationName.Length > 0 ? probe.StationName
                             : probe?.StructureName ?? "";

                if (siteId is null)
                {
                    items.Add(Unstartable(d.TypeId, name, priority, pool, d.Units,
                        $"{head} Build {d.Units:N0}.",
                        siteName.Length > 0
                            ? $"{siteName} is not linked to a real structure, so materials cannot be checked"
                            : "No linked structure for this job, so materials cannot be checked"));
                    continue;
                }

                structureBySite.TryGetValue(siteId.Value, out var structure);
                var catKey = IndyRigMatching.ItemCategoryKey(
                                 d.TypeId, isReaction, typeToGroup, groupInfo);

                var reaches = eligible
                    .Select(c => WorklistIndyCharReach.Of(c, corps))
                    .ToList();

                var prints = IndustryBlueprintService.UsableAt(
                    printsByType.GetValueOrDefault(product.TypeId, []), siteId.Value, reaches);

                var runsNeeded = IndustryJobSplit.RunsFor(d.Units, Math.Max(1, product.Quantity));

                if (prints.Count == 0)
                {
                    // Owning one somewhere and owning none at all are different problems. The
                    // first is a move; the second is a purchase, and saying "buy" when the print
                    // is one structure away would be the more expensive mistake of the two.
                    // Acquiring the print is Material Purchases' business — it is one purchase
                    // however many rules and orders are waiting on it. This row only reports why
                    // the job cannot run.
                    var allPrints = printsByType.GetValueOrDefault(product.TypeId, []);
                    var owned     = IndustryBlueprintService.OwnedAnywhere(allPrints, printOwner);

                    items.Add(Unstartable(d.TypeId, name, priority, pool, d.Units,
                        $"{head} Build {d.Units:N0} ({runsNeeded:N0} run(s)) at {siteName}.",
                        owned
                            ? $"No blueprint at {siteName} — one is owned but elsewhere, held by "
                              + "another character, or locked in a running job"
                            // No scope in this wording any more: ownership is now checked across
                            // every character, so "none owned" means none at all rather than
                            // none within the material scope.
                            : "No BPO or BPC owned on any character — one has to be acquired",
                        siteId.Value, siteName));
                    continue;
                }

                var split = IndustryJobSplit.Plan(
                    runsNeeded,
                    print => IndustryTimeService.PerRunSeconds(
                                 timeCtx, product.TypeId, isReaction, print.Te,
                                 structure, catKey, eligible[0].Skills),
                    settings.MaxJobDaysFor(pool),
                    prints);

                for (var i = 0; i < split.Jobs.Count; i++)
                {
                    var job = split.Jobs[i];

                    // Whoever can reach this print, can run this activity, and has a slot free.
                    // Reach is checked per print because a copy in a personal hangar is usable
                    // by exactly one alt, whatever the corp hangar holds.
                    var owner = eligible.FirstOrDefault(c =>
                        WorklistIndyCharReach.Of(c, corps).CanUse(job.Print)
                        && slotsLeft[c.Config.CharacterId].GetValueOrDefault(pool) > 0);

                    var busy = owner is null;
                    owner ??= eligible.FirstOrDefault(c =>
                        WorklistIndyCharReach.Of(c, corps).CanUse(job.Print))
                        ?? eligible[0];

                    var readiness = WorklistReadiness.Ready;
                    var blockedBy = "";

                    // Materials for this job at this print's ME, against what is left at the site
                    // after the jobs already planned in this pass.
                    var wanted   = job.Runs * (long)Math.Max(1, product.Quantity);
                    var cacheKey = (d.TypeId, wanted, job.Print.Me);
                    if (!planCache.TryGetValue(cacheKey, out var needed))
                        planCache[cacheKey] = needed =
                            await MaterialsForAsync(ctx, d.TypeId, wanted, job.Print.Me, ct);

                    var missing = MissingAtSite(
                        stock, inScope, ctx, owner, needed, siteId.Value, committed);

                    if (missing.Count > 0)
                    {
                        // Named by what would fix it. "Not here" and "not owned" both stop the
                        // job, but one is answered by a hauler and the other by a wallet.
                        var haul = missing.Where(m => !m.MustBuy).Select(m => m.Name).ToList();
                        var buy  = missing.Where(m =>  m.MustBuy).Select(m => m.Name).ToList();

                        readiness = WorklistReadiness.Blocked;
                        blockedBy = buy.Count == 0
                            ? $"Materials not at {siteName}: " + Names(haul)
                            : haul.Count == 0
                                ? $"Not owned{settings.IndustryScopeSuffix}: " + Names(buy)
                                : $"Not owned{settings.IndustryScopeSuffix}: {Names(buy)}; "
                                  + $"elsewhere: {Names(haul)}";
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

                    // Name first. The column sorts on this string, and a leading run count sorts
                    // by digit — scattering the several jobs of one split across the whole list.
                    var runsText = product.Quantity > 1
                        ? $"{name} — {job.Runs:N0} run(s) → {wanted:N0}"
                        : $"{name} — {job.Runs:N0} run(s)";
                    var ofText   = split.Jobs.Count > 1 ? $" (job {job.Index} of {job.Of})" : "";
                    var duration = IndustryJobSplit.Duration(job.Seconds);
                    var durText  = duration.Length > 0 ? $" ~{duration}." : "";
                    var leftover = i == split.Jobs.Count - 1 && split.RunsUnassigned > 0
                        ? $" {split.RunsUnassigned:N0} further run(s) need a print — none free."
                        : "";

                    // Named only on a real split. A job well under the configured length looks
                    // like a miscalculation unless it says what stopped it, and the usual answer
                    // is the blueprint's own run cap rather than the clock.
                    var capText = split.Jobs.Count == 1 && split.RunsUnassigned == 0 ? "" : job.Cap switch
                    {
                        SplitCap.GameLimit => " Capped by EVE's 30-day limit on a single job.",
                        SplitCap.CopyRuns  => " Capped by the runs left on the copy.",
                        SplitCap.JobLength => $" Capped by the {settings.MaxJobDaysFor(pool):0.#}-day job length.",
                        _                  => "",
                    };

                    items.Add(new WorklistItem
                    {
                        // No character in the key. Assignment can legitimately move between
                        // refreshes as slots free up, and a key that moved with it would reset
                        // the item's age and silently drop its snooze. The index keeps the
                        // pieces of one split independently snoozable.
                        Key           = $"industry_job:{d.TypeId}:{job.Index}",
                        Pool          = pool,
                        Source        = Id,
                        Kind          = WorklistKind.Job,
                        Title         = runsText,
                        Quantity      = wanted,
                        Detail        = $"{head} Short {d.Units:N0}{ofText}. "
                                      + $"{job.Print.Describe()} at {siteName}.{durText}{capText}{leftover}",
                        Readiness     = readiness,
                        BlockedBy     = blockedBy,
                        // ⚠️ Only a job that can start names a character. An owner is still picked
                        // above — material reach is per character, so the check needs one — but
                        // reporting it on a job that cannot run reads as an instruction, and the
                        // fallback owner is whoever comes first in the eligible list. Every
                        // blocked and waiting job in the run landed on that same alt, a queue
                        // they could never work through and which says nothing about who will
                        // actually take the job once it frees up.
                        CharacterId   = readiness == WorklistReadiness.Ready ? owner.Config.CharacterId   : 0,
                        CharacterName = readiness == WorklistReadiness.Ready ? owner.Config.CharacterName : "",
                        LocationId    = siteId.Value,
                        LocationName  = siteName,
                        TypeId        = d.TypeId,
                        TypeName      = name,
                        Priority      = priority,
                    });
                }
        }

        return items;
    }



    /// <summary>A shortfall that cannot become a job at all, reported once with the reason.</summary>
    /// <param name="pool">Carried so a blocked job still counts under its own slot type in the
    /// summary. A job that cannot start is still a manufacturing job.</param>
    /// <param name="units">What the job would produce. ⚠️ Carried purely so the row can be priced
    /// and measured: <c>WorklistService.ApplyVolumeAsync</c> skips anything with no quantity, so
    /// these rows used to sit with a blank value and volume column while every startable job
    /// beside them had both. What is blocked is worth knowing the size of — that is most of why
    /// it is worth unblocking.</param>
    private WorklistItem Unstartable(
        int typeId, string name, int priority, IndustryPool pool, long units,
        string detail, string blockedBy, long locationId = 0, string locationName = "") =>
        new()
        {
            Key          = $"industry_job:{typeId}:0",
            Source       = Id,
            Kind         = WorklistKind.Job,
            Title        = name,
            Quantity     = units,
            Detail       = detail,
            Readiness    = WorklistReadiness.Blocked,
            BlockedBy    = blockedBy,
            LocationId   = locationId,
            LocationName = locationName,
            TypeId       = typeId,
            TypeName     = name,
            Priority     = priority,
            Pool         = pool,
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
    private static List<MissingMaterial> MissingAtSite(
        SiteStock stock, ScopeStock inScope, ProductionContext ctx, IndustryCandidate who,
        Dictionary<int, long> needed, long siteId, Dictionary<(long, int), long> committed)
    {
        if (needed.Count == 0) return [];

        var missing = new List<MissingMaterial>();

        foreach (var (typeId, wanted) in needed.OrderBy(n => n.Key))
        {
            var here = stock.Reachable(siteId, typeId, who)
                     - committed.GetValueOrDefault((siteId, typeId));
            if (here >= wanted) continue;

            missing.Add(new MissingMaterial(
                ctx.TypeNames.GetValueOrDefault(typeId, $"Type {typeId}"),
                typeId,
                // Owned in scope but not here is a hauling problem. Not owned in scope at all is
                // a buying one, and only the second should raise a purchase.
                MustBuy: inScope.Reachable(typeId, who) < wanted));
        }

        return missing;
    }

    private sealed record MissingMaterial(string Name, int TypeId, bool MustBuy);


    /// <summary>Names for a message, capped so a job short of thirty things stays readable.</summary>
    private static string Names(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(4))
        + (names.Count > 4 ? $", and {names.Count - 4} more" : "");

    /// <summary>What is on hand at the park's facilities, indexed by who can reach it. Materials
    /// in a corp hangar serve every alt in that corporation; personal stock serves only its
    /// owner.</summary>
    private sealed record SiteStock(
        Dictionary<(long Site, int TypeId, long OwnerId), long> Corp,
        Dictionary<(long Site, int TypeId, long OwnerId), long> Personal)
    {
        /// <summary>
        /// Everything at this site the given character could actually put into a job: their own
        /// hangar and their corporation's. A fact about who they are, not a setting — the scope
        /// has already decided what is the player's, and this only asks who can physically
        /// reach it.
        /// </summary>
        public long Reachable(long siteId, int typeId, IndustryCandidate who) =>
            Corp.GetValueOrDefault((siteId, typeId, who.CorporationId))
          + Personal.GetValueOrDefault((siteId, typeId, who.Config.CharacterId));
    }
}
