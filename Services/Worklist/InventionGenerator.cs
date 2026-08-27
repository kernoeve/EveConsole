using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// The science half of T2 production: the copy jobs and invention jobs that have to happen before
/// a T2 build has a blueprint to run from.
///
/// <para>Before this, a T2 shortfall reported "No BPO or BPC owned on any character — one has to be
/// acquired" and stopped there, which is true and useless. T2 blueprints are not acquired, they are
/// made, and the making is three linked jobs deep: copy a T1 original, invent from the copies, then
/// build. This raises the first two.</para>
///
/// <para><b>Invention runs are consumed one per attempt, not one per copy.</b> A twenty-run T1 copy
/// feeds twenty attempts and comes back with whatever is left, so the copy job is usually a single
/// run and the thing that actually gets sized is the datacore order. Getting this backwards — as
/// the obvious reading of "the blueprint is consumed" suggests — would raise twenty copy jobs where
/// one is needed.</para>
///
/// <para><b>Runs, not blueprints, are the unit throughout.</b> Three successes at four runs each is
/// twelve runs of T2 production against a demand of ten, and it is the twelve that has to be netted
/// off next refresh — counting blueprints would call three copies enough for three hulls.</para>
///
/// <para>Kept out of <see cref="IndustryJobGenerator"/> because almost every quantity means
/// something different here. Its "runs" produce items; these produce chances at a blueprint.</para>
/// </summary>
public class InventionGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService       assignment,
    IndustryBlueprintService        blueprints,
    IndustryTimeService             times,
    IndustryDemandService           demands,
    InventionService                invention,
    InvLevelService                 invLevels,
    ProductionCalculatorService     production,
    WorklistSettings                settings) : IWorklistGenerator
{
    public string Id          => "invention";
    public string DisplayName => "Invention & Copying";

    private const int ShipCategoryId = 6;

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        var candidates = await assignment.LoadCandidatesAsync(ct);
        var scientists = candidates.Where(c => c.Runs(IndustryPool.Science)).ToList();
        if (scientists.Count == 0) return [];

        var rules = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.Action == "Build")
            .ToListAsync(ct);

        var ctx     = await production.LoadContextAsync(parkId, ct);
        var timeCtx = await times.LoadAsync(parkId, ct);

        var corps    = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);
        var owner    = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var wrapped  = await AssetExclusions.UnusableItemIdsAsync(db, ct);

        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);

        var groups = await db.InvLevelGroups.AsNoTracking()
            .Where(g => rules.Select(r => r.GroupId).Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, ct);

        var inScope = await ScopeStockAsync(db, scope, wrapped, corps, ct);
        var demand  = await demands.GatherAsync(db, ctx, rules, groups, scope, wrapped, corps, inScope, ct);
        if (demand.Count == 0) return [];

        // Only the invented ones. Everything else is the job generator's business.
        var recipes = await invention.LoadAsync(demand.Keys.ToList(), ct);
        if (recipes.Count == 0) return [];

        var decryptors = await invention.DecryptorsAsync(ct);

        // Where the park says these activities happen. Both are Indy Parks categories already, so
        // a park that has assigned Blueprint Copying and Blueprint Invention needs nothing more
        // said here — and one that has not gets no jobs rather than a guess.
        var inventionLab = await InventionService.LabAsync(db, parkId, InventionService.InventionCategory, ct);
        var copyLab      = await InventionService.LabAsync(db, parkId, InventionService.CopyingCategory, ct);
        if (inventionLab is null || copyLab is null) return [];

        // Products, datacores and decryptors together — a decryptor is a material of the job like
        // any other, and leaving it out of the lookup printed a bare type id in the manifest.
        var names = await invLevels.GetTypeNamesAsync(
            recipes.Keys
                   .Concat(recipes.Values.SelectMany(r => r.Datacores.Select(d => d.TypeId)))
                   .Concat(decryptors.Select(d => d.TypeId))
                   .Distinct().ToList(), ct);

        // Both ends of the chain: the invented T2 blueprints (whose runs offset demand) and the
        // T1 originals and copies the attempts are made from.
        var printsByType = await blueprints.LoadAsync(
            recipes.Values.Select(r => r.InventedBlueprintTypeId)
                   .Concat(recipes.Values.Select(r => r.SourceBlueprintTypeId))
                   .Distinct().ToList(), ct);

        var siteStock = await SiteStockAsync(db, [inventionLab.Value.Site, copyLab.Value.Site], wrapped, corps, ct);

        var reaches   = scientists.Select(c => WorklistIndyCharReach.Of(c, corps)).ToList();
        var slotsLeft = scientists.ToDictionary(
            c => c.Config.CharacterId, c => c.FreeSlots.GetValueOrDefault(IndustryPool.Science));

        // Datacores already spoken for by jobs planned earlier in this pass. Without it every job
        // in a split compares against the same untouched pile and all twenty report Ready off one
        // job's worth of stock.
        var committed = new Dictionary<(long Site, int TypeId), long>();

        var items = new List<WorklistItem>();

        var needs = await invention.PlanDemandAsync(
            demand, ctx.BlueprintByProduct, printsByType, owner,
            typeId => DecryptorFor(typeId, ctx, decryptors),
            scientists.Select(c => (IReadOnlyDictionary<int, int>)c.Skills).ToList(), ct);

        foreach (var (d, recipe, plan, shortRuns) in needs)
        {
            var name = names.GetValueOrDefault(d.TypeId, $"Type {d.TypeId}");

            // Whoever gives the best odds. Invention chance is the one thing here that genuinely
            // differs by character, and a worse pilot costs datacores on every attempt.
            var best = scientists
                .OrderByDescending(c => InventionService.Chance(recipe, plan.Decryptor, c.Skills))
                .ThenByDescending(c => slotsLeft.GetValueOrDefault(c.Config.CharacterId))
                .ThenBy(c => c.Config.CharacterId)
                .First();

            var head = $"{name}: short {shortRuns:N0} run(s) of T2 production.";

            items.AddRange(CopyTasks(recipe, plan, copyLab.Value, printsByType, owner, reaches,
                                     timeCtx, best, siteStock, d.Priority, name, head));

            items.AddRange(InventionTasks(recipe, plan, inventionLab.Value, timeCtx, best, scientists,
                                          reaches, printsByType, siteStock, committed, slotsLeft,
                                          d.Priority, name, head, names));
        }

        return items;
    }

    // ── Invention ─────────────────────────────────────────────────────────────

    private List<WorklistItem> InventionTasks(
        InventionRecipe recipe, InventionPlan plan, InventionService.Lab lab,
        IndustryTimeService.TimeContext timeCtx, IndustryCandidate best,
        List<IndustryCandidate> scientists, List<WorklistIndyCharReach> reaches,
        Dictionary<int, List<BlueprintStock>> printsByType,
        Dictionary<(long Site, int TypeId), long> stock,
        Dictionary<(long Site, int TypeId), long> committed,
        Dictionary<long, int> slotsLeft,
        int priority, string name, string head, Dictionary<int, string> names)
    {
        // The source copies standing at the lab. Nothing else can carry an attempt: the copy is
        // installed in the job and locked for its duration, so concurrent invention jobs need a
        // copy each exactly as concurrent builds need a print each.
        var atLab = printsByType.GetValueOrDefault(recipe.SourceBlueprintTypeId, [])
            .Where(b => !b.LockedInJob && !b.IsOriginal
                     && b.LocationId == lab.Site && reaches.Any(r => r.CanUse(b)))
            .OrderByDescending(b => b.Runs)
            .ThenBy(b => b.ItemId)
            .ToList();

        // Reusing the manufacturing splitter rather than a second one. The shape is identical —
        // runs to place, a per-run cost, a length limit, and one print per job whose own runs bound
        // it — and the one difference, that an original cannot be inverted from, is handled by
        // excluding originals above rather than by forking the algorithm.
        var split = IndustryJobSplit.Plan(
            plan.Attempts,
            // A laboratory rig is keyed by the activity it speeds up, not by what is being made —
            // unlike a manufacturing rig, which is keyed by the item's category. Passing the item
            // key here (or, as this first did, an empty one) matches no rig at all, so a rigged
            // lab modelled as an unrigged one and every batch came out roughly twice as long.
            print => IndustryTimeService.PerScienceUnitSeconds(
                         timeCtx, recipe.SourceBlueprintTypeId,
                         IndustryTimeService.InventionActivity, lab.Structure,
                         InventionService.InventionCategory, best.Skills),
            settings.MaxJobDaysScience,
            atLab);

        var items = new List<WorklistItem>();

        foreach (var job in split.Jobs)
        {
            // Per attempt, so a run count can be costed. Datacores scale exactly with attempts —
            // no efficiency rounding applies to invention — so unlike manufacturing, how many
            // attempts the stock covers is a division rather than a search.
            var perAttempt = plan.Recipe.Datacores
                .Select(d => (d.TypeId, Qty: (long)d.Quantity))
                .Concat(plan.Decryptor.IsNone ? [] : new[] { (plan.Decryptor.TypeId, Qty: 1L) })
                .ToList();

            List<(int TypeId, long Qty)> MatsFor(int runs) =>
                perAttempt.Select(m => (m.TypeId, Qty: m.Qty * runs)).ToList();

            // Per job, not per batch. Each job consumes only its own runs' worth, and charging the
            // whole line's datacores against the first job would block every one of them on a
            // shortfall none of them individually has.
            var mats = MatsFor(job.Runs);

            List<string> Short(IReadOnlyList<(int TypeId, long Qty)> want) =>
                want.Where(m => Available(m.TypeId) < m.Qty)
                    .Select(m => $"{names.GetValueOrDefault(m.TypeId, $"Type {m.TypeId}")} " +
                                 $"({Available(m.TypeId):N0} of {m.Qty:N0})")
                    .ToList();

            var missing = Short(mats);

            // ⚠️ How many attempts the datacores actually cover. Waiting for a full batch means
            // inventing nothing at all until every datacore has arrived, and the copies that
            // batch would produce are what everything downstream is waiting on. Whatever can be
            // started is worth starting.
            var runnable = missing.Count == 0
                ? job.Runs
                : (int)perAttempt
                    .Select(m => m.Qty <= 0 ? job.Runs : Math.Min(job.Runs, Available(m.TypeId) / m.Qty))
                    .DefaultIfEmpty(job.Runs)
                    .Min();

            var free = scientists.FirstOrDefault(c => slotsLeft.GetValueOrDefault(c.Config.CharacterId) > 0);
            var who  = free ?? best;

            if (runnable > 0)
            {
                var runMats = runnable == job.Runs ? mats : MatsFor(runnable);

                var readiness = free is null ? WorklistReadiness.Waiting : WorklistReadiness.Ready;
                var blockedBy = free is null ? "Every character who runs science has all slots busy" : "";

                if (free is not null)
                {
                    // Only a job that could start now claims a slot and its share of the datacores.
                    slotsLeft[who.Config.CharacterId] -= 1;
                    foreach (var m in runMats)
                        committed[(lab.Site, m.TypeId)] = committed.GetValueOrDefault((lab.Site, m.TypeId)) + m.Qty;
                }

                Emit(runnable, runMats, readiness, blockedBy, "",
                     runnable < job.Runs
                         ? $" Cut to what the datacores on hand cover — {job.Runs - runnable:N0} "
                         + "more attempt(s) are on a separate row."
                         : "");
            }

            if (runnable < job.Runs)
            {
                // ⚠️ Measured after the startable half has claimed its share — Available() reads
                // through `committed`, which the branch above has already added to. Naming a
                // datacore the first row just consumed would send someone hunting for a shortage
                // the plan itself created.
                var restRuns = job.Runs - runnable;
                var restMats = MatsFor(restRuns);

                // ⚠️ Only when the startable half did NOT claim. It claims only if a slot was
                // free; with every scientist busy nothing was committed, and measuring the
                // remainder against the full stock would report no shortage at all — printing
                // "Not at Lab: " with nothing after it.
                var unclaimed = runnable > 0 && free is null
                    ? MatsFor(runnable).ToDictionary(m => m.TypeId, m => m.Qty)
                    : [];

                var shortNames = restMats
                    .Where(m => Available(m.TypeId) - unclaimed.GetValueOrDefault(m.TypeId) < m.Qty)
                    .Select(m =>
                    {
                        var have = Available(m.TypeId) - unclaimed.GetValueOrDefault(m.TypeId);
                        return $"{names.GetValueOrDefault(m.TypeId, $"Type {m.TypeId}")} " +
                               $"({have:N0} of {m.Qty:N0})";
                    })
                    .ToList();

                Emit(restRuns, restMats, WorklistReadiness.Blocked,
                     $"Not at {lab.Name}: {string.Join(", ", shortNames)}",
                     runnable > 0 ? ":short" : "",
                     runnable > 0 ? " The rest of this batch, waiting on datacores." : "");
            }

            void Emit(int runs, IReadOnlyList<(int TypeId, long Qty)> lineMats,
                      WorklistReadiness readiness, string blockedBy,
                      string keySuffix, string extraDetail)
            {
                // Scaled: job.Seconds covers the whole planned batch, and a row for part of it
                // that quoted the whole duration would read as the longer job it is not.
                var seconds  = job.Runs > 0 ? job.Seconds * runs / job.Runs : 0;
                var duration = IndustryJobSplit.Duration(seconds);
                var durText  = duration.Length > 0 ? $" ~{duration}." : "";
                var ofText   = split.Jobs.Count > 1 ? $" (job {job.Index} of {job.Of})" : "";
                var capText  = split.Jobs.Count == 1 && split.RunsUnassigned == 0 ? "" : job.Cap switch
                {
                    SplitCap.GameLimit => " Capped by EVE's 30-day limit on a single job.",
                    SplitCap.CopyRuns  => " Capped by the runs left on the source copy.",
                    SplitCap.JobLength => $" Capped by the {settings.MaxJobDaysScience:0.#}-day job length.",
                    _                  => "",
                };
                var shortText = job.Index == split.Jobs.Count && split.RunsUnassigned > 0
                    ? $" {split.RunsUnassigned:N0} further attempt(s) need a source copy — none free."
                    : "";

                items.Add(new WorklistItem
                {
                    Key           = $"invention:{recipe.ProductTypeId}:{job.Index}{keySuffix}",
                    Source        = Id,
                    Kind          = WorklistKind.Job,
                    Pool          = IndustryPool.Science,
                    Title         = $"{name} — invent {runs:N0} run(s)",
                    Quantity      = runs,
                    Detail        = $"{head}{ofText} {plan.Chance:P1} a run "
                                  + $"({recipe.BaseChance:P0} base, {DecryptorText(plan.Decryptor)}) "
                                  + $"→ {plan.SuccessesNeeded:N0} BPC(s) of {plan.RunsPerBpc} run(s) "
                                  + $"at ME{plan.InventedMe}/TE{plan.InventedTe} over {plan.Attempts:N0} "
                                  + $"attempt(s). {recipe.SourceBlueprintName} "
                                  + $"{job.Print.Describe()} at {lab.Name}.{durText}{capText}{extraDetail}{shortText}",
                    Readiness     = readiness,
                    BlockedBy     = blockedBy,
                    CharacterId   = who.Config.CharacterId,
                    CharacterName = who.Config.CharacterName,
                    LocationId    = lab.Site,
                    LocationName  = lab.Name,
                    TypeId        = recipe.ProductTypeId,
                    TypeName      = name,
                    Priority      = priority,
                    Lines         = lineMats
                        .Select(m => new WorklistLine(
                            m.TypeId, names.GetValueOrDefault(m.TypeId, $"Type {m.TypeId}"), m.Qty))
                        .ToList(),
                });
            }
        }

        // No copy at the lab at all: the split placed nothing, but the work is still real and the
        // reader needs to know why it is not listed.
        if (split.Jobs.Count == 0)
            items.Add(new WorklistItem
            {
                Key       = $"invention:{recipe.ProductTypeId}:0",
                Source    = Id,
                Kind      = WorklistKind.Job,
                Pool      = IndustryPool.Science,
                Title     = $"{name} — invent {plan.Attempts:N0} run(s)",
                Quantity  = plan.Attempts,
                Detail    = $"{head} {plan.Chance:P1} a run → {plan.SuccessesNeeded:N0} BPC(s) "
                          + $"of {plan.RunsPerBpc} run(s).",
                Readiness = WorklistReadiness.Blocked,
                BlockedBy      = $"No {recipe.SourceBlueprintName} copy at {lab.Name} to invent from",
                BlockedByPrint = true,
                LocationId   = lab.Site,
                LocationName = lab.Name,
                TypeId    = recipe.ProductTypeId,
                TypeName  = name,
                Priority  = priority,
            });

        return items;

        long Available(int typeId) =>
            stock.GetValueOrDefault((lab.Site, typeId))
            - committed.GetValueOrDefault((lab.Site, typeId));
    }

    // ── Copying ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The copy job that feeds invention, raised only when the copies on hand cannot cover the
    /// attempts. Usually one run: a copy carries up to its blueprint's licensed maximum, and
    /// twenty runs is twenty attempts.
    /// </summary>
    private List<WorklistItem> CopyTasks(
        InventionRecipe recipe, InventionPlan plan, InventionService.Lab lab,
        Dictionary<int, List<BlueprintStock>> printsByType, PrintOwnership owner,
        List<WorklistIndyCharReach> reaches, IndustryTimeService.TimeContext timeCtx,
        IndustryCandidate best, Dictionary<(long Site, int TypeId), long> stock,
        int priority, string name, string head)
    {
        var all = printsByType.GetValueOrDefault(recipe.SourceBlueprintTypeId, []);

        // Copy runs the player can already reach, wherever they sit. A copy in the wrong structure
        // is a haul, not a reason to cut another one.
        var ownedCopyRuns = all.Where(b => owner.Owns(b) && !b.IsOriginal).Sum(b => (long)b.Runs);
        var shortRuns     = plan.CopyRunsNeeded - ownedCopyRuns;
        if (shortRuns <= 0) return [];

        var copies   = IndustryJobSplit.RunsFor(shortRuns, recipe.MaxCopyRuns);
        var perCopy  = Math.Min(shortRuns, recipe.MaxCopyRuns);
        var original = all.FirstOrDefault(b => owner.Owns(b) && b.IsOriginal);

        if (original is null)
            return [new WorklistItem
            {
                Key       = $"invention_copy:{recipe.ProductTypeId}",
                Source    = Id,
                Kind      = WorklistKind.Job,
                Pool      = IndustryPool.Science,
                Title     = $"{recipe.SourceBlueprintName} — no original to copy from",
                Detail    = $"{head} Invention needs {plan.CopyRunsNeeded:N0} copy run(s) and "
                          + $"{ownedCopyRuns:N0} are owned.",
                Readiness = WorklistReadiness.Blocked,
                BlockedBy      = "No BPO owned on any character — one has to be acquired, "
                               + "or the copies bought outright",
                BlockedByPrint = true,
                TypeId    = recipe.SourceBlueprintTypeId,
                TypeName  = recipe.SourceBlueprintName,
                Priority  = priority,
            }];

        var atCopyLab = original.LocationId == lab.Site;

        var perUnit = IndustryTimeService.PerScienceUnitSeconds(
            timeCtx, recipe.SourceBlueprintTypeId, IndustryTimeService.CopyingActivity,
            lab.Structure, InventionService.CopyingCategory, best.Skills);

        // Copying is charged per run per copy, so a two-copy job of thirty runs costs sixty units.
        var duration = IndustryJobSplit.Duration((perUnit ?? 0) * copies * perCopy);
        var durText  = duration.Length > 0 ? $" ~{duration}." : "";

        return [new WorklistItem
        {
            Key           = $"invention_copy:{recipe.ProductTypeId}",
            Source        = Id,
            Kind          = WorklistKind.Job,
            Pool          = IndustryPool.Science,
            Title         = $"{recipe.SourceBlueprintName} — copy {copies:N0} × {perCopy:N0} run(s)",
            Quantity      = copies,
            Detail        = $"{head} Feeds {plan.Attempts:N0} invention attempt(s); "
                          + $"{ownedCopyRuns:N0} copy run(s) already owned. "
                          + $"{original.Describe()} at {lab.Name}.{durText}",
            Readiness     = atCopyLab ? WorklistReadiness.Ready : WorklistReadiness.Blocked,
            BlockedBy     = atCopyLab ? ""
                          : $"The original is not at {lab.Name} — it has to be moved there first",
            CharacterId   = best.Config.CharacterId,
            CharacterName = best.Config.CharacterName,
            LocationId    = lab.Site,
            LocationName  = lab.Name,
            TypeId        = recipe.SourceBlueprintTypeId,
            TypeName      = recipe.SourceBlueprintName,
            Priority      = priority,
        }];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DecryptorText(Decryptor d) =>
        d.IsNone ? "no decryptor" : $"{d.Name} ×{d.ChanceMultiplier:0.0#}";

    private Decryptor DecryptorFor(int productTypeId, ProductionContext ctx, List<Decryptor> all) =>
        InventionService.DecryptorFor(
            productTypeId, ctx, all, settings.ShipDecryptor, settings.OtherDecryptor);

    private static async Task<Dictionary<(long, int), long>> SiteStockAsync(
        AppDbContext db, long[] sites, HashSet<long> wrapped, HashSet<long>? corps,
        CancellationToken ct) =>
        (await db.EsiAssets.AsNoTracking()
                .Where(a => sites.Contains(a.RootLocationId))
                .Select(a => new { a.ItemId, a.RootLocationId, a.TypeId, a.OwnerId, a.OwnerType, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId)
                     && (a.OwnerType != "corporation" || corps is null || corps.Contains(a.OwnerId)))
            .GroupBy(a => (a.RootLocationId, a.TypeId))
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

    private static async Task<ScopeStock> ScopeStockAsync(
        AppDbContext db, HashSet<long>? scope, HashSet<long> wrapped, HashSet<long>? corps,
        CancellationToken ct)
    {
        var rows = (await (scope is null
                    ? db.EsiAssets.AsNoTracking()
                    : db.EsiAssets.AsNoTracking().Where(a => scope.Contains(a.RootLocationId)))
                .Select(a => new { a.ItemId, a.TypeId, a.OwnerType, a.OwnerId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => !wrapped.Contains(a.ItemId)
                     && (a.OwnerType != "corporation" || corps is null || corps.Contains(a.OwnerId)))
            .ToList();

        return new ScopeStock(
            rows.Where(a => a.OwnerType == "corporation")
                .GroupBy(a => (a.TypeId, a.OwnerId)).ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)),
            rows.Where(a => a.OwnerType != "corporation")
                .GroupBy(a => (a.TypeId, a.OwnerId)).ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity)));
    }
}
