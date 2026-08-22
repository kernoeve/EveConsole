using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Why a station wants something, which is also how urgent moving it is.</summary>
public enum HaulReason { Unblocking, Restock, Refine, Surplus }

/// <summary>
/// One station's need for one item, and what is asking for it.
///
/// <para>The same figures the hauling plan is built from, reported rather than acted on. Anything
/// computed separately would drift from what the tool actually does the first time either
/// changed, so this is the planner's own working shown.</para>
/// </summary>
/// <param name="OrderJobs">Materials for builds a customer order is waiting on.</param>
/// <param name="Jobs">Materials for builds nothing is waiting on — restocking production.</param>
/// <param name="InventoryLevels">The share of job demand that traces back to a stock target
/// rather than to an order.</param>
/// <param name="StationLevels">What a station level says should sit here regardless of jobs.</param>
public sealed record StationNeed(
    long   StationId,
    string StationName,
    int    TypeId,
    string TypeName,
    long   OnHand,
    long   OrderJobs,
    long   Jobs,
    long   InventoryLevels,
    long   StationLevels,
    double UnitPrice  = 0,
    double UnitVolume = 0)
{
    public long Total     => OrderJobs + Jobs + InventoryLevels + StationLevels;
    public long Shortfall => Math.Max(0, Total - OnHand);

    /// <summary>What closing the gap costs, and what it takes to carry. Priced and sized on the
    /// shortfall rather than the total, since the total is mostly stock already sitting there.</summary>
    public double ShortfallValue  => Shortfall * UnitPrice;
    public double ShortfallVolume => Shortfall * UnitVolume;
}

/// <summary>
/// Moving material to where it is needed.
///
/// <para>The other generators end at "this job cannot start, its inputs are elsewhere". That is a
/// diagnosis, not work. This turns it into the trip that fixes it.</para>
///
/// <para><b>One task per pair of stations.</b> A hauler flying from Jita to ZD1-Z2 carries
/// everything ZD1-Z2 needs from Jita, so the task lists items rather than being one task each —
/// twenty rows for one round trip would be twenty times the reading for the same flying. Volume
/// is deliberately ignored: how many trips it takes is the hauler's problem, and splitting by
/// capacity would guess at ships and rigs the tool knows nothing about.</para>
///
/// <para><b>A task is worth its best cargo.</b> If any part of a run unblocks a job, the whole
/// run carries that urgency even when the rest is routine restocking. The trip happens once.</para>
///
/// <para>Sources are ranked by distance — same system first, then gates — and a station is never
/// drawn below what it needs itself, so filling one structure cannot empty another.</para>
/// </summary>
public class LogisticsGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryBlueprintService        blueprints,
    IndustryAssignmentService       assignment,
    IndustryDemandService           demands,
    InventionService                invention,
    JumpDistanceService             jumps,
    ProductionCalculatorService     production,
    WorklistSettings                settings,
    AppErrorLogger                  errorLogger) : IWorklistGenerator
{
    public string Id          => "logistics";
    public string DisplayName => "Logistics";

    /// <param name="Priority">Inherited from whatever asked for it. A haul feeding an urgent
    /// order has to outrank one feeding a routine top-up, and the reason alone cannot say that:
    /// both are Unblocking.</param>
    /// <param name="Level">The part of Qty that is a station level rather than job demand. The
    /// deadbands apply to that part alone: a level may sit ten percent light without anyone
    /// minding, but a job needs every unit it consumes.</param>
    /// <param name="OrderJobs">Of Qty, materials for builds a customer order waits on.</param>
    /// <param name="Jobs">Of Qty, materials for builds nothing waits on.</param>
    /// <param name="RuleJobs">Of Qty, the share of job demand tracing to a stock target.</param>
    private sealed record Want(long Qty, HaulReason Reason, int Priority, long Level,
                               long OrderJobs = 0, long Jobs = 0, long RuleJobs = 0);

    /// <summary>
    /// Registers what a station wants and why. A named delegate rather than an Action of nine
    /// arguments, so the attribution can be passed by name at the call sites that care and
    /// left out entirely by the ones that do not.
    /// </summary>
    private delegate void NeedFn(long station, int typeId, long qty, HaulReason why,
                                 int priority = 0, long level = 0,
                                 long orderJobs = 0, long jobs = 0, long ruleJobs = 0);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        try
        {
            return await BuildAsync(db, parkId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(LogisticsGenerator), $"Park {parkId}", ex);
            return [];
        }
    }

    private async Task<List<WorklistItem>> BuildAsync(
        AppDbContext db, int parkId, CancellationToken ct)
    {
        var (want, stock, ctx) = await GatherAsync(db, parkId, ct);
        if (ctx is null) return [];

        var refineMoves = await RefiningMovesAsync(db, ctx, parkId, stock, ct);

        var systems = await SystemsAsync(db, ct);
        var places  = await PlaceNamesAsync(db, ct);

        var moves = new List<Move>(refineMoves);
        moves.AddRange(await AllocateAsync(want, stock, systems, ct));
        moves.AddRange(SurplusMoves(await SurplusHomesAsync(db, ct), want, stock));

        // Named from the moves themselves, not from the demand that produced most of them.
        // Surplus exists precisely where nothing is wanted, so its types are never in `want` —
        // and a run mixing restocking with surplus takes the restock label while listing the
        // surplus items as bare type ids.
        var names = await NamesAsync(db, moves.Select(m => m.TypeId).Distinct().ToList(), ct);

        return Tasks(moves, names, places);
    }

    /// <summary>
    /// What every station wants and what it already holds — the input both the hauling plan and
    /// the needs report are built from, gathered once here so neither can drift from the other.
    /// </summary>
    private async Task<(Dictionary<(long Station, int TypeId), Want> Want,
                        Dictionary<(long Station, int TypeId), long> Stock,
                        ProductionContext? Ctx)>
        GatherAsync(AppDbContext db, int parkId, CancellationToken ct)
    {
        var ctx = await production.LoadContextAsync(parkId, ct);

        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return ([], [], null);
        var corps   = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);
        var reaches = candidates
            .Select(c => WorklistIndyCharReach.Of(c, corps))
            .ToList();

        var scope   = await ScopeAsync(db, ct);
        var wrapped = await AssetExclusions.UnusableItemIdsAsync(db, ct);

        // Everything reachable, by where it is. The same rule the shortfall checks use, so the
        // two agree about what exists.
        var reachable = (await (scope is null
                    ? db.EsiAssets.AsNoTracking()
                    : db.EsiAssets.AsNoTracking().Where(a => scope.Contains(a.RootLocationId)))
                .Select(a => new { a.ItemId, a.RootLocationId, a.TypeId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId))
            // Every personal asset, whoever holds it. The scope is the player's own property, and
            // which characters are set up to run jobs says nothing about what they own — filtering
            // personal stock to the industry list hid 8,985 of 11,624 rows, including blueprints
            // bought by the trading alt and waiting in Jita to be moved.
            //
            // Corp stock is still filtered, because that genuinely is not all the player's: a main
            // in a large alliance corp exposes hangars belonging to other people.
            .Where(a => a.OwnerType != "corporation" || corps is null || corps.Contains(a.OwnerId))
            .ToList();

        var stock = reachable
            .GroupBy(a => (Station: a.RootLocationId, a.TypeId))
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        // The same view of stock the demand service nets against, so both agree on what exists.
        var inScope = new ScopeStock(
            reachable.Where(a => a.OwnerType == "corporation")
                     .GroupBy(a => (a.TypeId, a.OwnerId)).ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)),
            reachable.Where(a => a.OwnerType != "corporation")
                     .GroupBy(a => (a.TypeId, a.OwnerId))
                     .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)));

        var want = new Dictionary<(long Station, int TypeId), Want>();

        void Need(long station, int typeId, long qty, HaulReason why, int priority = 0, long level = 0,
                  long orderJobs = 0, long jobs = 0, long ruleJobs = 0)
        {
            if (station <= 0 || qty <= 0) return;
            var key = (station, typeId);
            var had = want.GetValueOrDefault(key);
            want[key] = new Want(
                had is null ? qty : had.Qty + qty,
                had is null ? why : (HaulReason)Math.Min((int)had.Reason, (int)why),
                had is null ? priority : Math.Max(had.Priority, priority),
                had is null ? level : had.Level + level,
                (had?.OrderJobs ?? 0) + orderJobs,
                (had?.Jobs      ?? 0) + jobs,
                (had?.RuleJobs  ?? 0) + ruleJobs);
        }

        var owned     = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var allPrints = await blueprints.LoadAllAsync(ct);
        var meMap     = IndustryBlueprintService.BestMeByProduct(allPrints, ctx.BlueprintByProduct, owned);

        // Prints the blueprints table does not list but assets do. Without them a copy sitting in
        // a structure that table omits is invisible here, so no move is ever raised for it — the
        // job stays blocked for want of a print the player already owns and could simply carry.
        var printsInAssets = await blueprints.OwnedInAssetsAsync(
            ctx.BlueprintByProduct.Values.Select(b => b.TypeId).Distinct().ToList(), owned, ct);

        await AddJobDemandAsync(db, ctx, meMap, allPrints, owned, printsInAssets,
                                scope, wrapped, corps, inScope, Need, ct);
        await AddStationLevelDemandAsync(db, Need, ct);

        return (want, stock, ctx);
    }

    /// <summary>
    /// Every station's demand, itemised by what is asking for it.
    ///
    /// <para>Runs the same gathering the hauling plan is built from and reports the result instead
    /// of allocating against it, so the two can never disagree about what a station needs. What
    /// it does <i>not</i> apply is the deadband: this answers "what does this station want",
    /// where the plan answers "is it far enough short to be worth a trip".</para>
    /// </summary>
    public async Task<List<StationNeed>> NeedsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        try
        {
            var (want, stock, _) = await GatherAsync(db, parkId, ct);
            if (want.Count == 0) return [];

            var places = await PlaceNamesAsync(db, ct);
            var typeIds = want.Keys.Select(k => k.TypeId).Distinct().ToList();
            var names  = await NamesAsync(db, typeIds, ct);
            var (prices, volumes) = await PriceAndVolumeAsync(db, typeIds, ct);

            return want
                .Select(kv => new StationNeed(
                    kv.Key.Station,
                    places.GetValueOrDefault(kv.Key.Station, $"Location {kv.Key.Station}"),
                    kv.Key.TypeId,
                    names.GetValueOrDefault(kv.Key.TypeId, $"Type {kv.Key.TypeId}"),
                    stock.GetValueOrDefault(kv.Key),
                    kv.Value.OrderJobs,
                    kv.Value.Jobs,
                    kv.Value.RuleJobs,
                    kv.Value.Level,
                    prices.GetValueOrDefault(kv.Key.TypeId),
                    volumes.GetValueOrDefault(kv.Key.TypeId)))
                .OrderBy(n => n.StationName).ThenBy(n => n.TypeName)
                .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(LogisticsGenerator), $"Needs for park {parkId}", ex);
            return [];
        }
    }

    /// <summary>
    /// Unit price and packaged volume for a set of types.
    ///
    /// <para>Priced at whatever the asset valuation is configured to use, so a shortfall and the
    /// hangar it is measured against are never valued two different ways. Packaged volume is the
    /// honest figure: nothing here is assembled.</para>
    /// </summary>
    private static async Task<(Dictionary<int, double> Prices, Dictionary<int, double> Volumes)>
        PriceAndVolumeAsync(AppDbContext db, List<int> typeIds, CancellationToken ct)
    {
        if (typeIds.Count == 0) return ([], []);

        var volumes = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Volume, ct);

        var settings = await db.MarketDefaultSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.AssetValueConfigId is not int configId) return ([], volumes);

        var prices = (await db.MarketItemPrices.AsNoTracking()
                .Where(p => p.ConfigId == configId && typeIds.Contains(p.TypeId))
                .ToListAsync(ct))
            .ToDictionary(p => p.TypeId, p => settings.AssetValuePriceType switch
            {
                MarketPriceType.Buy  => p.BuyPrice,
                MarketPriceType.Sell => p.SellPrice,
                _                    => p.Midpoint,
            });

        return (prices, volumes);
    }

    /// <summary>One item moving between two stations.</summary>
    private sealed record Move(long From, long To, int TypeId, long Qty, HaulReason Reason, int Priority = 0);

    // ── Demand ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the jobs planned at each park facility will consume there.
    ///
    /// <para>The whole shortfall's worth, not only the part that could start now. A job split
    /// five ways still wants all five jobs' material eventually, and hauling it in one trip beats
    /// five.</para>
    /// </summary>
    private async Task AddJobDemandAsync(
        AppDbContext db, ProductionContext ctx, Dictionary<int, int> meMap,
        IReadOnlyList<BlueprintStock> allPrints, PrintOwnership owned,
        Dictionary<int, int> printsInAssets,
        HashSet<long>? scope, HashSet<long> wrapped, HashSet<long>? corps, ScopeStock inScope,
        NeedFn need, CancellationToken ct)
    {
        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        // The same demand the job generator works from, so the two cannot disagree about what is
        // being built. Working it out separately here left materials unhauled for jobs the list
        // was suggesting: an item covered by a parent build has no shortfall of its own, so the
        // old per-rule reading of it came out as zero.
        var demand = await demands.GatherAsync(db, ctx, rules, groups, scope, wrapped, corps, inScope, ct);

        await AddInventionDemandAsync(db, ctx, demand, allPrints, owned, printsInAssets, corps, need, ct);

        foreach (var (typeId, d) in demand.OrderBy(d => d.Key))
        {
            var entry = new ProductionQueueEntry
            {
                TypeId   = typeId,
                Quantity = (int)Math.Clamp(d.Units, 1, int.MaxValue),
                MeLevel  = meMap.TryGetValue(typeId, out var me) ? me : 10,
            };

            PlanJob? root;
            try
            {
                root = production.Calculate([entry], ctx, meOverrides: meMap)
                                 .AllJobs.FirstOrDefault(j => j.OutputTypeId == typeId);
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }

            if (root?.StationId is not { } site) continue;

            // Only this job's own inputs. Its sub-assemblies are separate jobs at their own
            // facilities, and they appear in the demand list in their own right.
            //
            // Each material inherits the job's own mix of reasons, so a component built half for
            // a customer order and half to restock puts half its minerals under each. Demand a
            // parent build passes down counts as a job rather than as the order or rule at the
            // top: what this station is waiting on is the parent, and naming the distant cause
            // would make every raw mineral in the tree read as order work.
            foreach (var m in root.Materials)
            {
                var (order, rule, parent) = d.SplitOf(m.TotalQty);
                need(site, m.MaterialTypeId, m.TotalQty, HaulReason.Unblocking, d.Priority, 0,
                     orderJobs: order, jobs: parent, ruleJobs: rule);
            }

            // The print is a precondition exactly as the materials are, so it is wanted here on
            // the same terms and rides the same hauling run. It is absent from root.Materials
            // because a job consumes none of it — but a job with every input present and no
            // blueprint is just as stuck, and nothing else in the tool would ever move one.
            //
            // In practice this is quiet: a print normally already sits at the structure that
            // builds from it, so the want is met by stock and no move comes of it.
            if (ctx.BlueprintByProduct.TryGetValue(typeId, out var bpProd))
            {
                var prints = PrintsWanted(allPrints, owned, printsInAssets, bpProd,
                                 IndustryJobSplit.RunsFor(d.Units, Math.Max(1, bpProd.Quantity)));
                var (bpOrder, bpRule, bpParent) = d.SplitOf(prints);
                need(site, bpProd.TypeId, prints, HaulReason.Unblocking, d.Priority,
                     orderJobs: bpOrder, jobs: bpParent, ruleJobs: bpRule);
            }
        }
    }

    /// <summary>
    /// What the invention line will consume at the lab: datacores, decryptors, and source copies.
    ///
    /// <para>Registered here rather than in the invention generator because hauling is decided in
    /// one place. Without it the invention jobs sit permanently Blocked on datacores that nothing
    /// ever moves or buys — the tool would name the problem and never route round it, which is
    /// precisely the failure the worklist exists to prevent.</para>
    ///
    /// <para>The batch size is not worked out again here. It comes from the same
    /// <see cref="InventionService.PlanDemandAsync"/> the generator plans from, so the datacores
    /// hauled are the datacores the suggested jobs will eat.</para>
    /// </summary>
    private async Task AddInventionDemandAsync(
        AppDbContext db, ProductionContext ctx, Dictionary<int, BuildDemand> demand,
        IReadOnlyList<BlueprintStock> allPrints, PrintOwnership owned,
        Dictionary<int, int> printsInAssets, HashSet<long>? corps,
        NeedFn need, CancellationToken ct)
    {
        var candidates = (await assignment.LoadCandidatesAsync(ct))
            .Where(c => c.Runs(IndustryPool.Science)).ToList();
        if (candidates.Count == 0) return;

        var lab = await InventionService.LabAsync(
            db, await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct),
            InventionService.InventionCategory, ct);
        if (lab is null) return;

        var decryptors = await invention.DecryptorsAsync(ct);
        var printsByType = allPrints.GroupBy(p => p.TypeId).ToDictionary(g => g.Key, g => g.ToList());

        var needs = await invention.PlanDemandAsync(
            demand, ctx.BlueprintByProduct, printsByType, owned,
            typeId => InventionService.DecryptorFor(
                          typeId, ctx, decryptors, settings.ShipDecryptor, settings.OtherDecryptor),
            candidates.Select(c => (IReadOnlyDictionary<int, int>)c.Skills).ToList(), ct);

        foreach (var n in needs)
        {
            // Split per material, not once for the batch. The breakdown has to sum to the quantity
            // it describes, and apportioning the attempt count instead reported 162 datacores
            // wanted where the jobs will eat 648.
            foreach (var m in n.Plan.Materials)
            {
                var (order, rule, parent) = n.Demand.SplitOf(m.Quantity);
                need(lab.Value.Site, m.TypeId, m.Quantity, HaulReason.Unblocking, n.Demand.Priority,
                     0, orderJobs: order, jobs: parent, ruleJobs: rule);
            }

            // Source copies are wanted at the lab in their own right. One per concurrent job,
            // since a copy is locked while its invention job runs — the same rule that governs
            // manufacturing prints, and the reason a batch cannot all run at once off one copy.
            var copies = PrintsWanted(allPrints, owned, printsInAssets,
                new SdeBlueprintProduct
                {
                    TypeId        = n.Recipe.SourceBlueprintTypeId,
                    Activity      = "manufacturing",
                    ProductTypeId = n.Recipe.ProductTypeId,
                    Quantity      = 1,
                },
                n.Plan.CopyRunsNeeded);

            if (copies > 0)
            {
                var (cOrder, cRule, cParent) = n.Demand.SplitOf(copies);
                need(lab.Value.Site, n.Recipe.SourceBlueprintTypeId, copies, HaulReason.Unblocking,
                     n.Demand.Priority, 0, orderJobs: cOrder, jobs: cParent, ruleJobs: cRule);
            }
        }
    }

    /// <summary>
    /// How many prints of one blueprint the structure needs to have.
    ///
    /// <para>An original is one and done: it survives every job, so a second would be moved for
    /// nothing. Copies are consumed, and a print is locked for the duration of the job it is in,
    /// so a shortfall wanting more runs than one copy carries needs one copy per job.</para>
    ///
    /// <para>Zero when the player owns none. There is nothing to haul, and acquiring it is
    /// Material Purchases' business — asking for a move of something that does not exist would
    /// put an impossible task on the list beside the purchase that fixes it.</para>
    /// </summary>
    private static long PrintsWanted(
        IReadOnlyList<BlueprintStock> allPrints, PrintOwnership owned,
        Dictionary<int, int> printsInAssets,
        SdeBlueprintProduct bpProd, long runsNeeded)
    {
        var mine = allPrints.Where(p => p.TypeId == bpProd.TypeId && owned.Owns(p)).ToList();

        // ⚠️ Assets are the fallback, not an addition. The blueprints table does not cover every
        // structure the assets table does, so a copy there reads as owning none and no move is
        // ever raised — the job sits blocked for a print already in a hangar. Counted at one run
        // apiece because an asset row carries no run count: the conservative reading, which asks
        // for the copies rather than assuming one of them covers the batch.
        if (mine.Count == 0)
        {
            var inAssets = printsInAssets.GetValueOrDefault(bpProd.TypeId);
            return inAssets == 0 ? 0 : Math.Min(inAssets, Math.Max(1, runsNeeded));
        }

        if (mine.Any(p => p.IsOriginal)) return 1;

        // Best copies first, matching the order the job generator would reach for them in, so the
        // count here is the count it will actually use.
        long wanted = 0, covered = 0;
        foreach (var copy in mine.OrderByDescending(p => p.Me).ThenByDescending(p => p.Runs))
        {
            if (covered >= runsNeeded) break;
            covered += copy.Runs;
            wanted++;
        }
        return wanted;
    }

    /// <summary>
    /// Station levels: keep this group's stock at this station.
    ///
    /// <para>The station is the scope, whatever scope the group itself carries — the row exists
    /// to say "here", so counting stock elsewhere against it would defeat the point.</para>
    /// </summary>
    /// <para>The level itself is registered, not the shortfall against it. Everything in
    /// <c>want</c> means "the total this station should hold" — a job registers what it consumes,
    /// not what it is missing — and the spare calculation subtracts it from stock to decide what
    /// may be taken away. Registering a gap here broke that in both directions: a station already
    /// at its level registered nothing, so all of its stock read as spare and got hauled off, and
    /// one below its level registered only the difference, so part of what it did have could be
    /// taken while it was still short.</para>
    private static async Task AddStationLevelDemandAsync(
        AppDbContext db, NeedFn need, CancellationToken ct)
    {
        var levels = await db.WorklistStationLevels.AsNoTracking()
            .Where(l => l.Enabled).ToListAsync(ct);
        if (levels.Count == 0) return;

        var groupIds = levels.Select(l => l.GroupId).Distinct().ToList();

        // The multiplier is part of the target everywhere else it is read — see InvRuleShortfall —
        // so a group set to keep two of everything keeps two here too.
        var multipliers = await db.InvLevelGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => Math.Max(1, g.Multiplier), ct);

        var byGroup = (await db.InvLevelItems.AsNoTracking()
                .Where(i => groupIds.Contains(i.GroupId)).ToListAsync(ct))
            .GroupBy(i => i.GroupId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var level in levels)
        {
            if (!byGroup.TryGetValue(level.GroupId, out var items)) continue;
            var mult = multipliers.GetValueOrDefault(level.GroupId, 1);

            foreach (var i in items)
            {
                var qty = (long)i.TargetQuantity * mult;
                need(level.LocationId, i.TypeId, qty, HaulReason.Restock, level: qty);
            }
        }
    }

    /// <summary>
    /// Ore, ice and gas sitting anywhere but the facility that processes it.
    ///
    /// <para>Modelled as moves rather than as a need, because the quantity is whatever exists —
    /// a refinery does not want "200,000 Veldspar", it wants all of it. Moon ore is separated
    /// from asteroid ore by group, since the rigs are separate and so is the park assignment.</para>
    /// </summary>
    private static async Task<List<Move>> RefiningMovesAsync(
        AppDbContext db, ProductionContext ctx, int parkId,
        Dictionary<(long Station, int TypeId), long> stock, CancellationToken ct)
    {
        // ⚠️ Shared with RefiningGenerator, which raises the task once this haul has landed. Two
        // copies of the routing could disagree, and the failure would be silent: material hauled
        // to a facility the other generator never looks at, sitting there with nothing ever saying
        // to process it.
        var target = await RefiningRoutes.TargetsAsync(db, parkId, ct);
        if (target.Values.All(v => v is null)) return [];

        // Classify by SDE group so ice and moon ore separate from ordinary ore.
        var held = stock.Keys.Select(k => k.TypeId).Distinct().ToList();
        var kinds = await db.SdeTypes.AsNoTracking()
            .Where(t => held.Contains(t.TypeId))
            .Join(db.SdeGroups, t => t.GroupId, g => g.GroupId,
                  (t, g) => new { t.TypeId, g.CategoryId, g.GroupId })
            .ToListAsync(ct);

        var routeOf = kinds.ToDictionary(k => k.TypeId, k => RefiningRoutes.Route(k.CategoryId, k.GroupId));

        var moves = new List<Move>();
        foreach (var ((station, typeId), qty) in stock)
        {
            if (qty <= 0) continue;
            if (!routeOf.TryGetValue(typeId, out var key) || key is null) continue;
            if (target.GetValueOrDefault(key) is not { } to) continue;
            if (station == to) continue;   // already where it is processed

            moves.Add(new Move(station, to, typeId, qty, HaulReason.Refine));
        }

        return moves;
    }

    // Routing, and where the park does each kind of processing, live in RefiningRoutes — shared
    // with RefiningGenerator so the haul and the task that follows it cannot disagree.


    // ── Matching ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills each station's wants from the nearest station that can spare the material.
    ///
    /// <para>A source is only offered what it holds beyond its own need, so satisfying one
    /// structure never starves another. Same system first, then gate distance, then station id so
    /// two equally good sources always resolve the same way.</para>
    /// </summary>
    private async Task<List<Move>> AllocateAsync(
        Dictionary<(long Station, int TypeId), Want> want,
        Dictionary<(long Station, int TypeId), long> stock,
        Dictionary<long, int> systems, CancellationToken ct)
    {
        var moves = new List<Move>();
        if (want.Count == 0) return moves;

        // Spare stock: what a station holds beyond whatever it wants itself.
        var spare = new Dictionary<(long Station, int TypeId), long>();
        foreach (var (key, qty) in stock)
        {
            var mine = want.GetValueOrDefault(key)?.Qty ?? 0;
            var free = qty - mine;
            if (free > 0) spare[key] = free;
        }

        var jumpCache = new Dictionary<int, Dictionary<int, int>>();

        // Destinations in a fixed order so the same inputs always produce the same plan.
        var restockBand = settings.RestockBandPercent / 100.0;

        // Which sources are already carrying something to each destination this run.
        //
        // The cost the player actually pays is a trip, not a unit. Once a run from A to B exists,
        // putting the next item on it is free, while sourcing that item from C makes a second
        // trip for the same errand. Wants are walked destination by destination — the ordering
        // below guarantees it — so this only has to remember the current one.
        var suppliers = new HashSet<long>();
        long currentDest = 0;

        // Most urgent destination first, and only then by id.
        //
        // This used to walk by station id alone. That is deterministic, which was the point, but
        // it decides who gets scarce stock by which structure happens to have the smaller number:
        // 460 Capital Ship Maintenance Bays existed, two sites wanted 1,190 between them, and the
        // lower-id site took 299 to fill itself while the site building a customer's Avatar got
        // the 161 left over. Priority already encodes what matters — the Avatar's materials carry
        // the order's own rank — and it was simply not being consulted.
        //
        // Ranked per destination rather than per want, because the loop must still visit a
        // destination's wants together: the supplier-reuse rule below assumes it, and interleaving
        // destinations would turn one run into several.
        var destRank = want
            .GroupBy(k => k.Key.Station)
            .ToDictionary(g => g.Key, g => g.Max(x => x.Value.Priority));

        foreach (var ((dest, typeId), w) in want
                     .OrderByDescending(k => destRank[k.Key.Station])
                     .ThenBy(k => k.Key.Station)
                     .ThenBy(k => k.Key.TypeId))
        {
            if (dest != currentDest) { suppliers.Clear(); currentDest = dest; }

            var have = stock.GetValueOrDefault((dest, typeId));
            var gap  = w.Qty - have;
            if (gap <= 0) continue;

            // A level is allowed to run a little light before it is worth a trip, or a station
            // told to hold a thousand raises a haul for the twelve units something just consumed.
            // The band applies only to the level: whatever a job needs is needed in full, so the
            // trigger sits at the job demand plus the discounted level, and the fill still tops
            // the station all the way back up rather than leaving it parked at the trigger.
            if (w.Level > 0)
            {
                var jobPart = w.Qty - w.Level;
                if (have >= jobPart + w.Level * (1 - restockBand)) continue;
            }

            var destSystem = systems.GetValueOrDefault(dest);
            if (destSystem != 0 && !jumpCache.ContainsKey(destSystem))
                jumpCache[destSystem] = await jumps.JumpsFromAsync(destSystem, ct: ct);
            var distances = jumpCache.GetValueOrDefault(destSystem) ?? [];

            // Ordered by what it costs the player to act on, which is trips rather than jumps.
            //
            // 1. A source holding the whole amount first. Splitting a hundred units across ten
            //    stations that each hold ten is ten stops for one line of cargo, and the nearest
            //    of them being nearest changes nothing about that.
            // 2. Then one already sending something else to this destination, so the item rides
            //    a run that is happening anyway instead of starting another.
            // 3. Then distance, then indifference — between two equal sources take from the one
            //    with no use for the item rather than one holding it to a level, since the second
            //    is only spare until its own consumption catches up and the next refresh would
            //    ask for it back.
            var need = gap;
            var sources = spare
                .Where(s => s.Key.TypeId == typeId && s.Key.Station != dest && s.Value > 0)
                .OrderByDescending(s => s.Value >= need)
                .ThenByDescending(s => suppliers.Contains(s.Key.Station))
                .ThenBy(s => Distance(distances, systems, s.Key.Station))
                .ThenBy(s => want.ContainsKey((s.Key.Station, typeId)) ? 1 : 0)
                .ThenBy(s => s.Key.Station)
                .ToList();

            foreach (var s in sources)
            {
                if (gap <= 0) break;
                var take = Math.Min(gap, s.Value);
                moves.Add(new Move(s.Key.Station, dest, typeId, take, w.Reason, w.Priority));
                spare[s.Key] -= take;
                gap -= take;
                suppliers.Add(s.Key.Station);
            }
        }

        return moves;
    }

    /// <summary>Gate distance, with unreachable sources sorted last rather than dropped — a long
    /// haul is still an answer, and pretending the material is not there is not.</summary>
    private static int Distance(
        Dictionary<int, int> distances, Dictionary<long, int> systems, long station)
    {
        var sys = systems.GetValueOrDefault(station);
        if (sys == 0) return int.MaxValue - 1;
        return distances.TryGetValue(sys, out var d) ? d : int.MaxValue - 1;
    }

    /// <summary>
    /// Spare stock with nowhere to be, sent to the station that collects its group.
    ///
    /// <para>Only what no station wants. Capital parts belong at the capital shipyard once every
    /// waiting job is served, not instead of serving them.</para>
    /// </summary>
    private List<Move> SurplusMoves(
        Dictionary<int, (long Station, string Group)> homes,
        Dictionary<(long Station, int TypeId), Want> want,
        Dictionary<(long Station, int TypeId), long> stock)
    {
        var moves = new List<Move>();
        if (homes.Count == 0) return moves;

        var surplusBand = settings.SurplusBandPercent / 100.0;

        // Demand still *unmet* for each type anywhere, rather than demand at all. Every station
        // level now registers its target, so "is any of this wanted" would be true for every type
        // a level covers and surplus would never sweep again. What matters is whether somewhere is
        // actually short: once every station holds what it should, the rest is genuinely spare.
        var unmet = new Dictionary<int, long>();
        foreach (var (key, w) in want)
        {
            var short_ = w.Qty - stock.GetValueOrDefault(key);
            if (short_ > 0) unmet[key.TypeId] = unmet.GetValueOrDefault(key.TypeId) + short_;
        }

        foreach (var ((station, typeId), qty) in stock.OrderBy(s => s.Key.Station).ThenBy(s => s.Key.TypeId))
        {
            if (!homes.TryGetValue(typeId, out var home)) continue;
            if (station == home.Station) continue;
            if (unmet.GetValueOrDefault(typeId) > 0) continue;

            // Only what this station holds above its own level. Sweeping the lot would empty a
            // station that has been told to keep some of this, which is the same mistake the
            // spare calculation makes if a level is not registered.
            var mine = want.GetValueOrDefault((station, typeId));
            var free = qty - (mine?.Qty ?? 0);
            if (free <= 0) continue;

            // And not until it is over by enough to be worth the trip, for the same reason the
            // restock side has a band: a station a few units above its level should be left
            // alone rather than trickling them away one refresh at a time. Draining still goes
            // back to the level, so crossing the band once actually resolves it.
            if (mine is { Level: > 0 } && free <= mine.Level * surplusBand) continue;

            moves.Add(new Move(station, home.Station, typeId, free, HaulReason.Surplus));
        }

        return moves;
    }

    private static async Task<Dictionary<int, (long Station, string Group)>> SurplusHomesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var levels = await db.WorklistStationLevels.AsNoTracking()
            .Where(l => l.Enabled && l.AcceptsSurplus).ToListAsync(ct);
        if (levels.Count == 0) return [];

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => levels.Select(l => l.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, ct);

        var homes = new Dictionary<int, (long, string)>();

        // Lowest row id wins if two stations claim the same group's surplus, so the answer is
        // stable rather than whichever row was read first.
        foreach (var level in levels.OrderBy(l => l.Id))
        {
            var items = await db.InvLevelItems.AsNoTracking()
                .Where(i => i.GroupId == level.GroupId).Select(i => i.TypeId).ToListAsync(ct);
            foreach (var typeId in items)
                homes.TryAdd(typeId, (level.LocationId, groups.GetValueOrDefault(level.GroupId, "")));
        }

        return homes;
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private List<WorklistItem> Tasks(
        List<Move> moves, Dictionary<int, string> names, Dictionary<long, string> places)
    {
        var items = new List<WorklistItem>();

        foreach (var run in moves
                     .Where(m => m.From != m.To && m.Qty > 0)
                     .GroupBy(m => (m.From, m.To))
                     .OrderBy(g => g.Key.From).ThenBy(g => g.Key.To))
        {
            var cargo  = run.GroupBy(m => m.TypeId)
                            .Select(g => (TypeId: g.Key, Qty: g.Sum(x => x.Qty)))
                            .OrderByDescending(x => x.Qty)
                            .ToList();
            var reason = run.Min(m => m.Reason);   // the best cargo sets the worth of the run

            var from = places.GetValueOrDefault(run.Key.From, $"Location {run.Key.From}");
            var to   = places.GetValueOrDefault(run.Key.To,   $"Location {run.Key.To}");

            items.Add(new WorklistItem
            {
                // Keyed on the pair, not the cargo: it is one trip, and a key that changed as
                // items were added or removed would reset its age every refresh.
                Key          = $"haul:{run.Key.From}:{run.Key.To}",
                Source       = Id,
                Kind         = WorklistKind.Haul,
                Title        = $"{cargo.Count} item(s)",
                // The manifest lives on the row's own lines now. Repeating four of them here and
                // hiding the rest behind "and 9 more" was a worse answer to the same question.
                Detail       = Because(reason),
                Readiness    = WorklistReadiness.Ready,
                LocationId      = run.Key.From,
                LocationName    = from,
                DestinationId   = run.Key.To,
                DestinationName = to,
                Lines        = cargo
                    .Select(c => new WorklistLine(
                        c.TypeId, names.GetValueOrDefault(c.TypeId, $"Type {c.TypeId}"), c.Qty))
                    .ToList(),
                TypeId       = cargo[0].TypeId,
                TypeName     = names.GetValueOrDefault(cargo[0].TypeId, ""),
                // A run is worth its best cargo, and that now includes whose order the cargo
                // serves: a trip carrying material for the order due first outranks one carrying
                // material for the order due next month, though both are Unblocking.
                Priority     = Math.Max(
                    run.Max(m => m.Priority),
                    reason switch
                    {
                        HaulReason.Unblocking => WorklistPriority.HaulUnblocking,
                        HaulReason.Restock    => WorklistPriority.HaulRestock,
                        HaulReason.Refine     => WorklistPriority.HaulToRefine,
                        _                     => WorklistPriority.HaulSurplus,
                    }),
            });
        }

        return items;
    }

    private static string Because(HaulReason r) => r switch
    {
        HaulReason.Unblocking => "Jobs are waiting on this.",
        HaulReason.Restock    => "Tops the station up to its level.",
        HaulReason.Refine     => "For refining or decompression.",
        _                     => "Spare stock going to where its group lives.",
    };

    /// <summary>Structure names carry their system already; the title only needs the tail.</summary>
    private static string Short(string place)
    {
        var dash = place.IndexOf(" - ", StringComparison.Ordinal);
        return dash > 0 ? place[(dash + 3)..] : place;
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    private async Task<HashSet<long>?> ScopeAsync(AppDbContext db, CancellationToken ct)
    {
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking()
                .Select(s => s.LocationId).ToListAsync(ct));
        return scope;
    }

    private static async Task<Dictionary<int, string>> NamesAsync(
        AppDbContext db, List<int> typeIds, CancellationToken ct) =>
        await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

    /// <summary>Station or structure id to the system it sits in, for ranking sources.</summary>
    private static async Task<Dictionary<long, int>> SystemsAsync(AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.SolarSystemId })
                .ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.SolarSystemId);

        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.SolarSystemId != 0)
                     .Select(s => new { s.StructureId, s.SolarSystemId }).ToListAsync(ct))
            map[s.StructureId] = s.SolarSystemId;

        foreach (var s in await db.EsiCorpStructures.AsNoTracking()
                     .Select(s => new { s.StructureId, s.SystemId }).ToListAsync(ct))
            map[s.StructureId] = s.SystemId;

        // Assets in space report the solar system itself as their root, so a system is its own
        // location. Without this they rank as unreachable and sort behind every real station.
        foreach (var id in await db.SdeSolarSystems.AsNoTracking()
                     .Select(s => s.SolarSystemId).ToListAsync(ct))
            map.TryAdd(id, id);

        return map;
    }

    private static async Task<Dictionary<long, string>> PlaceNamesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => s.Name);

        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            map[s.StructureId] = s.Name;

        // Anything in space — an anchored container, a ship left on grid — roots to the system
        // rather than a station. Saying "in space" beats printing a bare id, and it tells the
        // reader why the pickup has no station name.
        foreach (var s in await db.SdeSolarSystems.AsNoTracking()
                     .Select(s => new { s.SolarSystemId, s.Name }).ToListAsync(ct))
            map.TryAdd(s.SolarSystemId, $"{s.Name} (in space)");

        return map;
    }
}
