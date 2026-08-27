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

    /// <summary>
    /// Time efficiency assumed for runs that have no print at all — a fully researched original,
    /// which is what would be bought. Optimistic, and said so on the row: an assumed duration is
    /// better than a run that never appears in the list.
    /// </summary>
    private const int FullyResearchedTe = 20;

    /// <summary>
    /// How many rows the runs with no free print may occupy.
    ///
    /// <para>⚠️ A guard against a pathological demand filling the list with hundreds of identical
    /// blocked rows, not a limit on what is reported. The last allowed row absorbs everything
    /// still remaining and says that it does, so the jobs always sum to the demand — which is the
    /// property these rows exist to preserve.</para>
    /// </summary>
    private const int MaxUnprintedRows = 12;

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

        // ── Who gets the next job ─────────────────────────────────────────────
        //
        // ⚠️ Re-picked after EVERY job, not sorted once. Sorting once and satisfying each item in
        // full let whichever shelf happened to be emptiest claim a whole scarce component run:
        // six Neurolink cells all went to Phoenix Navy Issues while a Standard Phoenix, a
        // Thanatos and a Moros Navy sat blocked. By the second job the Navy Issue was no longer
        // the emptiest shelf, and nothing was looking.
        //
        // ⚠️ One JOB at a time, not one run. A job is the unit of work — a capital component job
        // may carry a hundred and forty runs, and splitting it to balance shelves to the unit
        // would trade a real efficiency for a tidier number. Plan a whole job, then look again.
        //
        // Orders always outrank shelf-keeping, and priority orders outrank other orders: that is
        // what Priority already encodes, so it stays the first comparison. Coverage only decides
        // between things of equal standing — which in practice is the stock-keeping tier, where
        // the question "who is emptiest" is the whole point.
        var queue = demand.Values
            .Select(x => new PlanState(x))
            .ToList();

        // Every item by type, so a candidate can ask how its dependents are doing right now.
        var byType = queue.ToDictionary(s => s.Demand.TypeId);

        // Of what the queued work actually eats from this item, how much cannot be met.
        //
        // ⚠️ Against the NEED, never the shelf level. Pressurized Oxidizers holds 8 against a
        // level of 950,000, so a level-deficit stays near 1.0 for nineteen jobs and out-ranks
        // everything for all of them, long after the work in front of it has been fed — the rest
        // of that level is shelf, and refilling a shelf stops nobody. Reinforced Carbon Fiber is
        // the same error from the other end: 55% of its level, 795,615 units on hand, nothing
        // stopped for want of it.
        //
        // ⚠️ ShelfHave already includes the output of jobs already running in game, so work in
        // flight counts as arriving rather than as still missing.
        static double Starving(PlanState s)
        {
            var wanted = s.Demand.OrderUnits + s.Demand.ParentUnits;
            if (wanted <= 0) return 0;

            var have = s.Demand.ShelfHave + s.Planned;
            return Math.Clamp(1.0 - (double)have / wanted, 0.0, 1.0);
        }

        // How many jobs are stopped for want of this item, AS THINGS STAND THIS PASS.
        //
        // ⚠️ Recomputed, not read off the demand record. Each job planned feeds the need, and
        // once enough is planned to cover it nothing is waiting on this item any more — so its
        // claim on the next slot has to fall to zero even though its shelf is still far from
        // full. A fixed count is what let one item take twenty consecutive slots.
        int BlockedNow(PlanState s)
        {
            var starving = Starving(s);
            if (starving <= 0) return 0;

            var n = 0;
            foreach (var t in s.Demand.Dependents)
                if (byType.TryGetValue(t, out var dep) && dep.Remaining > 0) n++;

            // ⚠️ Scaled by how much of the need is still unmet, so the count falls with EVERY
            // job rather than in one step at the end. Twenty-three dependents share one shortfall:
            // planning a quarter of it feeds roughly a quarter of them, and the item's claim on
            // the next slot should shrink accordingly. Counting them all until the last unit is
            // planned, then dropping to nothing, is what let one item hold the top of the list
            // for seven consecutive passes with an unchanging 23 beside it.
            return (int)Math.Ceiling(n * starving);
        }

        // How much finished output stands behind this item — what the operation sells or flies,
        // rather than every task of any kind.
        int BlockedFinalNow(PlanState s)
        {
            var starving = Starving(s);
            if (starving <= 0) return 0;

            // ⚠️ A final product counts ITSELF. Dependents are the things that CONSUME an item,
            // and nothing consumes a hull, so counting only dependents scored every finished
            // product zero on the one key meant to protect it. Its components each scored one
            // for blocking it — so the parts outranked the ship, and the moment they were made
            // the hull dropped level with any intermediate being built to top up a shelf. The
            // flag exists precisely to stop a titan queueing behind stock work.
            var n = s.Demand.IsFinal ? 1 : 0;

            foreach (var t in s.Demand.Dependents)
                if (byType.TryGetValue(t, out var dep) && dep.Remaining > 0 && dep.Demand.IsFinal)
                    n++;
            return (int)Math.Ceiling(n * starving);
        }

        // What this item is worth to the most urgent thing STILL waiting on it.
        //
        // ⚠️ Re-evaluated every pass, like coverage and the blocked counts. Priority is stamped
        // once during the demand walk and inherited down the whole tree, so without this an item
        // keeps an order's urgency after that order's chain has been planned out — and holds the
        // top of the list on the strength of work that is no longer outstanding. Falling back to
        // what the item's own demand justifies is what lets the urgency expire.
        int LivePriority(PlanState s)
        {
            // ⚠️ An item's demand is a MIXTURE and its priority is one number, which is the
            // whole fault. Reinforced Carbon Fiber holds 795,615 units: the Ravens and
            // Apocalypses waiting on it are already fed, and every further unit is shelf — its
            // level asks for 1,450,000. Both halves shared the 220 the orders conferred, so
            // twenty jobs of pure shelf-filling sat at order priority ahead of everything else.
            //
            // Inherited urgency lasts exactly as long as the order-driven portion is short. Once
            // enough is on hand or planned to feed the work above, what remains is a stocking
            // job and falls back to what the item's own demand justifies.
            // Against the ORDER-DRIVEN units only. What the shelf also wants is a stocking job
            // and has never been what the order was waiting for.
            var forOrders = s.Demand.OrderDriven;
            if (forOrders <= 0 || s.Demand.ShelfHave + s.Planned >= forOrders)
                return s.Demand.OwnPriority;

            var best = s.Demand.OwnPriority;

            foreach (var t in s.Demand.Dependents)
                if (byType.TryGetValue(t, out var dep) && dep.Remaining > 0 &&
                    dep.Demand.Priority > best)
                    best = dep.Demand.Priority;

            return best;
        }

        // How full this item is against everything asked of it — the work AND the shelf.
        //
        // ⚠️ A share, never a unit count. Units are not comparable between items: fifty thousand
        // oxidizers and one Neurolink Protection Cell are not two numbers to sort.
        static double Coverage(PlanState s)
        {
            var total = s.Demand.ShelfLevel + s.Demand.OrderUnits + s.Demand.ParentUnits;
            if (total <= 0) return 1.0;

            return (double)(s.Demand.ShelfHave + s.Planned) / total;
        }

        // Prints already planned against in this pass. See where it is applied for why the real
        // LockedInJob flag is not enough on its own.
        var printsCommitted = new HashSet<long>();

        // Counts the passes, so a row can say where the plan reached it. See
        // WorklistItem.PlanSequence for why the value has to travel with the item.
        var planSeq = 0;

        while (true)
        {
            var state = queue
                .Where(s => !s.Done)
                // ⚠️ Priority first, and every key below it re-evaluated on every pass. An
                // order outranks work nobody is waiting for, and that is deliberate; what is not
                // deliberate is an item keeping an order's urgency after the order is settled.
                // All four keys move as jobs are planned — see LivePriority, BlockedNow and
                // Coverage. A key that is stamped once and never revisited hands the top of the
                // list to whichever item won the first pass.
                .OrderByDescending(LivePriority)
                .ThenByDescending(BlockedFinalNow)
                .ThenByDescending(BlockedNow)
                .ThenBy(Coverage)
                .ThenBy(s => s.Demand.TypeId)
                .FirstOrDefault();

            if (state is null) break;

            // ⚠️ Finished unless this visit makes progress. The body below leaves by a dozen
            // routes — no print, no site, nothing affordable — and any one of them returning to
            // the picker with the item still outstanding would choose it again forever. Opting IN
            // to another visit is the only version of this that cannot spin.
            state.Done = true;
            var seq = ++planSeq;

            // TEMPORARY: captured before this pass changes anything, so a row shows the
            // numbers that actually chose it.
            var dbgPrio  = LivePriority(state);
            var dbgFinal = BlockedFinalNow(state);
            var dbgBlock = BlockedNow(state);
            var dbgCover = Coverage(state);

            // The body sees a demand for what is still outstanding, so a second visit plans the
            // rest rather than the whole thing again.
            var d = state.Demand with { Units = state.Remaining };
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

                var runsNeeded = IndustryJobSplit.RunsFor(d.Units, Math.Max(1, product.Quantity));

                // ⚠️ Minus the prints this pass has already committed. UsableAt filters on
                // LockedInJob, which is what EVE says right now — it knows nothing about a job
                // this run has just planned. That was harmless while an item was visited once and
                // the split handed each print one job; with the item revisited between jobs the
                // same free BPO was offered again, and a second Chimera was recommended against
                // the one original that exists.
                var usable = IndustryBlueprintService.UsableAt(
                    printsByType.GetValueOrDefault(product.TypeId, []), siteId.Value, reaches);

                var prints = usable.Where(p => !printsCommitted.Contains(p.ItemId)).ToList();

                // ⚠️ A print free in EVE but already spoken for by a job further up this same list
                // is a different answer from no print at all, and saying "none at this station"
                // would be a lie the user can see through — they are looking at the job holding it.
                if (prints.Count == 0 && usable.Count > 0)
                {
                    items.Add(Unstartable(d.TypeId, name, priority, pool, d.Units,
                        $"{head} Build {d.Units:N0} ({runsNeeded:N0} run(s)) at {siteName}.",
                        usable.Count == 1
                            ? "The only usable blueprint is already committed to a job above — "
                            + "another would have to be acquired to run these in parallel"
                            : $"All {usable.Count:N0} usable blueprints are committed to jobs "
                            + "above — another would have to be acquired to run these in parallel",
                        siteId.Value, siteName, onPrint: true));
                    continue;
                }


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
                        siteId.Value, siteName, onPrint: true));
                    continue;
                }

                var split = IndustryJobSplit.Plan(
                    runsNeeded,
                    print => IndustryTimeService.PerRunSeconds(
                                 timeCtx, product.TypeId, isReaction, print.Te,
                                 structure, catKey, eligible[0].Skills),
                    settings.MaxJobDaysFor(pool),
                    prints);

                // ⚠️ One job, then back to the picker. The split still plans the whole remaining
                // shortfall — that is what sizes this job and what the "of N" wording counts —
                // but only the first is taken, because by the time it is installed this item may
                // no longer be the emptiest shelf in the yard.
                for (var i = 0; i < split.Jobs.Count && i < 1; i++)
                {
                    var job = split.Jobs[i];

                    // ⚠️ Spoken for, whatever becomes of the row below. One print runs one job at
                    // a time, so once this pass has planned against it — startable, waiting on a
                    // slot, or blocked for material — it is not available to plan against twice.
                    printsCommitted.Add(job.Print.ItemId);

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

                    var perRun = (long)Math.Max(1, product.Quantity);

                    // Materials for this job at this print's ME, against what is left at the site
                    // after the jobs already planned in this pass.
                    var needed  = await MaterialsFor(job.Runs);
                    var missing = MissingAtSite(
                        stock, inScope, ctx, owner, needed, siteId.Value, committed);

                    // ── How much of this job can actually start ───────────────────────
                    //
                    // ⚠️ The whole point of this branch. Sizing a job to the entire shortfall and
                    // then refusing to start it means nothing is built until every last unit of
                    // material is on hand — and with orders still arriving, that moment may never
                    // come. Production stalls behind a job that was never startable, and the
                    // components waiting on it stall behind that.
                    //
                    // So a job the materials cannot cover is cut where the materials run out:
                    // one row that can be installed now, and one for the rest that names what is
                    // missing. Nothing is lost — the second row is the same work, still counted.
                    var runnable = missing.Count == 0
                        ? job.Runs
                        : await AffordableRunsAsync(job.Runs);

                    // What the startable half takes, whether or not it can start today. Used only
                    // to measure the remainder honestly: those materials are spoken for by the
                    // first row, so counting them as available to the second would understate the
                    // shortage — and when every slot happens to be busy, would report no shortage
                    // at all and print an empty list of missing things.
                    var claimed = committed;

                    if (runnable > 0)
                    {
                        var mats = runnable == job.Runs ? needed : await MaterialsFor(runnable);

                        if (runnable < job.Runs)
                        {
                            claimed = new Dictionary<(long, int), long>(committed);
                            foreach (var (typeId, qty) in mats)
                                claimed[(siteId.Value, typeId)] =
                                    claimed.GetValueOrDefault((siteId.Value, typeId)) + qty;
                        }

                        // Waiting rather than Ready when every slot is busy: real information,
                        // and different from being unable to do it at all.
                        var readiness = busy ? WorklistReadiness.Waiting : WorklistReadiness.Ready;
                        var blockedBy = busy ? "Every character who can run this has all slots busy" : "";

                        // A slot is only taken by a job that can actually start.
                        if (!busy) slotsLeft[owner.Config.CharacterId][pool] -= 1;

                        // ⚠️ Materials are reserved by ANY job that is planned, including one
                        // waiting on a slot. They are two different resources and only the slot
                        // is free again next pass: a waiting job still eats its materials the
                        // moment it starts, so leaving them unreserved lets the next pass see
                        // the same stock and plan the same units over again.
                        //
                        // That is why the same thirteen runs appeared four times. The reactor
                        // holds 2,563,078 Tritanium against 10,000 a run and every reaction slot
                        // is full, so every job after the first was slot-blocked, reserved
                        // nothing, and the pass after it measured against untouched stock and
                        // cut an identical job. The shelf was credited each time — coverage rose
                        // — while the material it would consume was promised away repeatedly.
                        //
                        // What the reader should see instead is one waiting job for what the
                        // material covers, and the rest reported as blocked for want of it.
                        foreach (var (typeId, qty) in mats)
                            committed[(siteId.Value, typeId)] =
                                committed.GetValueOrDefault((siteId.Value, typeId)) + qty;

                        Emit(runnable, readiness, blockedBy, "",
                             runnable < job.Runs
                                 ? $" Cut to what the materials on hand cover — {job.Runs - runnable:N0} "
                                 + "more run(s) are on a separate row."
                                 : "");
                    }

                    // ── The rest, if the materials did not reach ──────────────────────
                    if (runnable < job.Runs)
                    {
                        // Named by what would fix it. "Not here" and "not owned" both stop the
                        // job, but one is answered by a hauler and the other by a wallet.
                        //
                        // ⚠️ Measured against the remainder, not the whole job. Once the runnable
                        // half has claimed its share, what is short is what the rest still needs
                        // — and naming a material the first row just consumed all of would send
                        // someone hunting for a shortage that the plan itself created.
                        var restRuns = job.Runs - runnable;
                        var restMats = await MaterialsFor(restRuns);
                        var short_   = MissingAtSite(
                            stock, inScope, ctx, owner, restMats, siteId.Value, claimed);

                        var haul = short_.Where(m => !m.MustBuy).Select(m => m.Name).ToList();
                        var buy  = short_.Where(m =>  m.MustBuy).Select(m => m.Name).ToList();

                        var why = buy.Count == 0
                            ? $"Materials not at {siteName}: " + Names(haul)
                            : haul.Count == 0
                                ? $"Not owned{settings.IndustryScopeSuffix}: " + Names(buy)
                                : $"Not owned{settings.IndustryScopeSuffix}: {Names(buy)}; "
                                  + $"elsewhere: {Names(haul)}";

                        // ⚠️ A distinct key, so this and the startable half snooze and age
                        // separately. The startable half keeps the original key: it is the row
                        // that was already there, and moving it would reset its age and silently
                        // drop a snooze the moment materials ran short.
                        Emit(restRuns, WorklistReadiness.Blocked, why,
                             runnable > 0 ? ":short" : "",
                             runnable > 0 ? " The rest of this job, waiting on materials." : "",
                             // ⚠️ The same list the sentence above was built from, kept whole.
                             // What is short and whether it is owned at all is the input to every
                             // material bottleneck question, and it was being thrown away here.
                             short_.Select(m => new WorklistShortage(
                                        m.TypeId, m.Name, m.Short, m.Wanted, m.MustBuy))
                                   .ToList());
                    }

                    // Builds one row for part or all of this planned job.
                    void Emit(int runs, WorklistReadiness readiness, string blockedBy,
                              string keySuffix, string extraDetail,
                              IReadOnlyList<WorklistShortage>? shortages = null)
                    {
                        var produced = runs * perRun;

                        // Name first. The column sorts on this string, and a leading run count
                        // sorts by digit — scattering the several jobs of one split across the
                        // whole list.
                        var runsText = product.Quantity > 1
                            ? $"{name} — {runs:N0} run(s) → {produced:N0}"
                            : $"{name} — {runs:N0} run(s)";

                        var ofText   = split.Jobs.Count > 1 ? $" (job {job.Index} of {job.Of})" : "";

                        // Scaled: job.Seconds covers the whole planned job, and a row for part of
                        // it that quoted the whole duration would be wrong in the direction that
                        // matters — it would look like the longer job someone was trying to avoid.
                        var seconds  = job.Runs > 0 ? job.Seconds * runs / job.Runs : 0;
                        var duration = IndustryJobSplit.Duration(seconds);
                        var durText  = duration.Length > 0 ? $" ~{duration}." : "";

                        // Points at the rows rather than replacing them. This used to be the ONLY
                        // mention of those runs, which is how a need for 3,375 showed up as one
                        // 40-run task; they are their own blocked rows now, and this says so.
                        var leftover = i == split.Jobs.Count - 1 && split.RunsUnassigned > 0
                            ? $" {split.RunsUnassigned:N0} further run(s) have no free print — listed separately."
                            : "";

                        // Named only on a real split. A job well under the configured length looks
                        // like a miscalculation unless it says what stopped it, and the usual
                        // answer is the blueprint's own run cap rather than the clock.
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
                            Key           = $"industry_job:{d.TypeId}:{job.Index}{keySuffix}",
                            Pool          = pool,
                            // The print the materials above were planned against, so the row can say
                            // what its quantities and its duration were worked out from.
                            BlueprintMe   = job.Print.Me,
                            BlueprintTe   = job.Print.Te,
                            Source        = Id,
                            Kind          = WorklistKind.Job,
                            Title         = runsText,
                            Quantity      = produced,
                            Detail        = $"{head} Short {d.Units:N0}{ofText}. "
                                          + $"{job.Print.Describe()} at {siteName}.{durText}{capText}{extraDetail}{leftover}",
                            Readiness     = readiness,
                            BlockedBy     = blockedBy,
                            Shortages     = shortages ?? [],
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
                            Blocks        = d.Blocks,
                            PlanSequence  = seq,
                            SortPriority     = dbgPrio,
                            SortBlockedFinal = dbgFinal,
                            SortBlocked      = dbgBlock,
                            SortCoverage     = dbgCover,
                        });

                        // ⚠️ Progress recorded here, at the one place a job is actually planned,
                        // and only for work that will actually happen. The shelf is that much
                        // fuller, which is what sends the next job somewhere else — and asking
                        // for another visit only when something was planned is what stops the
                        // picker returning to the same item forever.
                        //
                        // ⚠️ Blocked runs excluded. A row saying "these 40 runs have no material"
                        // fills no shelf and consumes nothing; counting it as progress would
                        // credit an item for work nobody can do, and send the next real job to
                        // whoever looks emptiest AFTER that fiction.
                        if (readiness != WorklistReadiness.Blocked)
                        {
                            var madeUnits = (long)runs * Math.Max(1, product.Quantity);
                            state.Planned     += madeUnits;
                            state.Remaining   -= madeUnits;
                            if (state.Remaining > 0) state.Done = false;
                        }
                    }

                    // Materials for a given run count on this print, cached across the search.
                    async Task<Dictionary<int, long>> MaterialsFor(int runs)
                    {
                        var qty = runs * perRun;
                        var key = (d.TypeId, qty, job.Print.Me);
                        if (!planCache.TryGetValue(key, out var mats))
                            planCache[key] = mats =
                                await MaterialsForAsync(ctx, d.TypeId, qty, job.Print.Me, ct);
                        return mats;
                    }

                    /// <summary>
                    /// The largest run count below <paramref name="ceiling"/> whose materials are
                    /// all on hand, or zero if not even one run is covered.
                    ///
                    /// <para>⚠️ Searched rather than divided. Material efficiency rounds per JOB,
                    /// not per run, so the amount a job consumes is not proportional to its runs
                    /// and "available ÷ per-run" is wrong in both directions. It is monotonic
                    /// though — more runs never need less of anything — which is what makes a
                    /// bisection correct, at a dozen or so probes against a plan already in
                    /// memory.</para>
                    /// </summary>
                    async Task<int> AffordableRunsAsync(int ceiling)
                    {
                        var lo = 0;         // known covered
                        var hi = ceiling;   // known short — the caller established this

                        while (hi - lo > 1)
                        {
                            var mid  = lo + (hi - lo) / 2;
                            var mats = await MaterialsFor(mid);
                            if (MissingAtSite(stock, inScope, ctx, owner, mats,
                                              siteId.Value, committed).Count == 0)
                                lo = mid;
                            else
                                hi = mid;
                        }

                        return lo;
                    }
                }

                // ── Runs that no print could carry ────────────────────────────────
                //
                // ⚠️ These are tasks, not a footnote. They used to be one sentence appended to the
                // last row — "3,335 further run(s) need a print" — which meant the list showed 40
                // units of work against a need for 3,375 and nothing said where the rest went. The
                // jobs in this list have to add up to what is being asked for, or the list cannot
                // be used to plan anything.
                //
                // Blocked, obviously: there is no free blueprint. But blocked and absent are very
                // different things — the first is work waiting on one purchase or one job
                // finishing, and the second is invisible.
                if (split.RunsUnassigned > 0)
                {
                    // The print these runs would use if one were free: the best owned, wherever it
                    // is and whatever it is doing. Its real ME and TE, rather than an assumption —
                    // the BPO sitting in a job right now is the one that will run them.
                    var reference = printsByType.GetValueOrDefault(product.TypeId, [])
                        .OrderByDescending(b => b.IsOriginal)
                        .ThenByDescending(b => b.Me)
                        .ThenByDescending(b => b.Te)
                        .ThenBy(b => b.ItemId)
                        .FirstOrDefault();

                    // Nothing owned: assume the print that would be bought, fully researched.
                    // Optimistic on purpose — an assumed figure that flattered a blueprint nobody
                    // has is better than no row at all, and the row says the assumption out loud.
                    var refTe = reference?.Te ?? FullyResearchedTe;

                    var refSecs = IndustryTimeService.PerRunSeconds(
                        timeCtx, product.TypeId, isReaction, refTe, structure, catKey,
                        eligible[0].Skills);

                    // The same two ceilings a real job gets. Without them one row would claim a
                    // job EVE would refuse to install.
                    var refCap = long.MaxValue;
                    if (refSecs is > 0)
                    {
                        refCap = Math.Max(1, (long)Math.Ceiling(
                            IndustryJobSplit.GameMaxJobSeconds / refSecs.Value));
                        var days = settings.MaxJobDaysFor(pool);
                        if (days > 0)
                        {
                            var byClock = Math.Max(1, (long)(days * 86400.0 / refSecs.Value));
                            if (byClock < refCap) refCap = byClock;
                        }
                    }

                    var printWhy = reference is null
                        ? "No blueprint owned — one has to be acquired"
                        : reference.LockedInJob
                            ? $"Blueprint busy in a running job ({reference.Describe()})"
                            : $"No blueprint free at {siteName} ({reference.Describe()})";

                    var assumed = reference is null
                        ? $" Sized against a fully researched print (TE{FullyResearchedTe}) — none is owned."
                        : $" Sized against {reference.Describe()}.";

                    var left  = split.RunsUnassigned;
                    var piece = 0;

                    while (left > 0)
                    {
                        piece++;

                        // ⚠️ The last allowed row takes everything still left, however much that
                        // is. Truncating instead would break the one property this whole block
                        // exists for: the jobs have to sum to the demand. A row larger than a
                        // single job can be says so in its own text rather than silently going
                        // missing.
                        var last = piece >= MaxUnprintedRows;
                        var runs = last ? left : Math.Min(left, refCap);
                        var over = last && runs > refCap;

                        items.Add(new WorklistItem
                        {
                            Key           = $"industry_job:{d.TypeId}:np{piece}",
                            Pool          = pool,
                            BlueprintMe   = reference?.Me,
                            BlueprintTe   = reference?.Te,
                            Source        = Id,
                            Kind          = WorklistKind.Job,
                            Title         = product.Quantity > 1
                                ? $"{name} — {runs:N0} run(s) → {runs * (long)Math.Max(1, product.Quantity):N0}"
                                : $"{name} — {runs:N0} run(s)",
                            Quantity      = runs * (long)Math.Max(1, product.Quantity),
                            Detail        = $"{head} Short {d.Units:N0}. Needs a blueprint at "
                                          + $"{siteName}.{assumed}"
                                          + (over
                                              ? $" Covers every remaining run; more than one job can "
                                              + "hold, so it will take several once a print is free."
                                              : ""),
                            Readiness      = WorklistReadiness.Blocked,
                            BlockedBy      = printWhy,
                            BlockedByPrint = true,
                            LocationId    = siteId.Value,
                            LocationName  = siteName,
                            TypeId        = d.TypeId,
                            TypeName      = name,
                            Priority      = priority,
                            Blocks        = d.Blocks,
                            PlanSequence  = seq,
                            SortPriority     = dbgPrio,
                            SortBlockedFinal = dbgFinal,
                            SortBlocked      = dbgBlock,
                            SortCoverage     = dbgCover,
                        });

                        left -= runs;
                    }
                }
        }
        }

        return items;
    }



    /// <summary>
    /// One item's progress through the planner: what is left to plan, and what has been.
    ///
    /// <para>Exists because demand is no longer walked once in a fixed order. Each visit plans a
    /// single job and hands the floor back, so an item has to remember where it got to — and the
    /// picker has to know how full its shelf is NOW rather than how full it was before anything
    /// was allocated.</para>
    /// </summary>
    private sealed class PlanState(BuildDemand demand)
    {
        public BuildDemand Demand { get; } = demand;

        /// <summary>Units still to plan. Starts at the whole shortfall.</summary>
        public long Remaining { get; set; } = demand.Units;

        /// <summary>Units planned so far, which is what raises this item's coverage.</summary>
        public long Planned { get; set; }

        /// <summary>No further visit is useful — finished, or unable to go further.</summary>
        public bool Done { get; set; }
    }

    /// <summary>A shortfall that cannot become a job at all, reported once with the reason.</summary>
    /// <param name="pool">Carried so a blocked job still counts under its own slot type in the
    /// summary. A job that cannot start is still a manufacturing job.</param>
    /// <param name="units">What the job would produce. ⚠️ Carried purely so the row can be priced
    /// and measured: <c>WorklistService.ApplyVolumeAsync</c> skips anything with no quantity, so
    /// these rows used to sit with a blank value and volume column while every startable job
    /// beside them had both. What is blocked is worth knowing the size of — that is most of why
    /// it is worth unblocking.</param>
    /// <param name="onPrint">⚠️ True where the blueprint is what is missing. Not inferable from
    /// the wording: the reason is prose written for a person, and the Bottlenecks tab counts
    /// print shortages off this flag. Left false and a genuine print block is invisible there —
    /// which is how "every copy is installed, so nothing can start" came to report nothing
    /// blocked by a print at all.</param>
    private WorklistItem Unstartable(
        int typeId, string name, int priority, IndustryPool pool, long units,
        string detail, string blockedBy, long locationId = 0, string locationName = "",
        bool onPrint = false) =>
        new()
        {
            BlockedByPrint = onPrint,
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

        // ⚠️ What earlier jobs in this pass already claimed, scope-wide.
        //
        // The site figure netted this off and the scope figure did not, so a material with a
        // small pool spread over many one-unit jobs could never read as a purchase: every hull
        // wants one Neurolink Protection Cell, three exist, and each job in turn compared its
        // one against the same untouched three and was told to go and haul it. The tenth job
        // was as haulable as the first. Nothing owns what has already been promised away.
        var spoken = new Dictionary<int, long>();
        foreach (var ((_, t), q) in committed)
            spoken[t] = spoken.GetValueOrDefault(t) + q;

        foreach (var (typeId, wanted) in needed.OrderBy(n => n.Key))
        {
            var here = stock.Reachable(siteId, typeId, who)
                     - committed.GetValueOrDefault((siteId, typeId));
            if (here >= wanted) continue;

            missing.Add(new MissingMaterial(
                ctx.TypeNames.GetValueOrDefault(typeId, $"Type {typeId}"),
                typeId,
                // ⚠️ How much short, not merely that it is. Without the amount, "blocked on this"
                // reads the same whether the job wanted two more than it had or ten thousand more
                // than exists — and those call for entirely different answers.
                Short: Math.Max(0, wanted - here),
                Wanted: wanted,
                // Owned in scope but not here is a hauling problem. Not owned in scope at all is
                // a buying one, and only the second should raise a purchase.
                MustBuy: inScope.Reachable(typeId, who)
                       - spoken.GetValueOrDefault(typeId) < wanted));
        }

        return missing;
    }

    private sealed record MissingMaterial(
        string Name, int TypeId, long Short, long Wanted, bool MustBuy);


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
