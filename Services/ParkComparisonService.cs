using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>What a job-list line does: build the item (manufacturing or reaction, the whole
/// chain), or a science job on a blueprint.</summary>
public enum CompareOp { Build, Copy, Invention, MeResearch, TeResearch }

/// <summary>
/// One line of the comparison's job list. <paramref name="TypeId"/> is the item to build, or a
/// blueprint: building a blueprint builds what it makes, and the science operations work on it.
/// </summary>
/// <param name="Quantity">Build: units. Copy: runs on each copy. Invention: runs of T2
/// production wanted. Research: unused.</param>
/// <param name="MeLevel">Build: the item's blueprint ME.</param>
/// <param name="Copies">Copy: how many copies.</param>
/// <param name="TargetLevel">Research: the level researched to from 0, 1–10 (TE level n is TE 2n%).</param>
/// <param name="DecryptorTypeId">Invention: the decryptor, 0 for none.</param>
/// <param name="Line">The line's place in the list, which tells two lines on the same item apart.</param>
public sealed record ParkCompareJob(int TypeId, string TypeName, long Quantity, int MeLevel,
    CompareOp Op = CompareOp.Build, int Copies = 1, int TargetLevel = 10, int DecryptorTypeId = 0, int Line = 0);

/// <summary>
/// The share of a plan's job cost that one job's system cost index sets:
/// <c>Round(Weight × index[SystemId, Activity])</c>, which was <see cref="Gross"/> at today's index.
/// </summary>
public sealed record IndexTerm(int SystemId, string Activity, double Weight, decimal Gross);

/// <summary>
/// One job-list line done in one park: every job in the structure the park gives that kind of
/// job.
/// </summary>
/// <param name="Slot">The park's place in the list of every park, which picks its colour on the
/// chart: unticking another park does not repaint this one.</param>
/// <param name="Buildable">False when the park cannot do the line: the item is bought, or the
/// blueprint has no recipe or no time for that operation.</param>
/// <param name="Produced">What comes out: units built, copy runs, T2 runs invented, or one
/// researched blueprint.</param>
/// <param name="Seconds">Job time: every job the line needs, end to end in slots (a build's whole
/// chain; an invention's copying and attempts). Null when a time is not known.</param>
/// <param name="Detail">What the line came to, in words: attempts and chance, copies, levels.</param>
public sealed record ParkCost(
    int ParkId, string ParkName, int Slot, ParkCompareJob Job, bool Buildable, long Produced,
    decimal Materials, decimal JobCost, decimal Total, IReadOnlyList<IndexTerm> Terms,
    double? Seconds = null, string Detail = "")
{
    public decimal PerUnit => Produced > 0 ? Total / Produced : 0;

    /// <summary>What the components made beyond need are worth: kept, not consumed, so the total
    /// is net of them, as the Production Calculator's Net Cost is. None for a science job.</summary>
    public decimal Leftovers => Materials + JobCost - Total;
}

/// <summary>
/// Does the same job list in several Indy Parks and sets the costs and times side by side, and
/// works the cost out again for each past day's system cost indices.
///
/// <para>⚠️ Building is the Production Calculator's own costing, untouched: one context per park,
/// one plan per item, its durations. Only the cost index moves over time. Each job records the
/// part of its cost the index sets (<see cref="PlanJob.IndexWeight"/>), so a past day is that part
/// again at that day's index — materials, taxes and the plan stay as calculated today.</para>
///
/// <para>Science jobs are costed here, by the game's rules, in the same shape: the job's
/// estimated item value is 2% of the manufacturing value (ME 0 materials at adjusted prices) of
/// what the blueprint makes — per copy run, per invention attempt (of the invented blueprint's
/// product), or × the research level factor / 105. That value × system index × the hull's role
/// cost bonus × (1 − the lab rigs' cost bonus), plus facility tax and the 4% SCC surcharge on the
/// value itself. Skills are all V. Invention plans the attempts its chance needs for the runs
/// asked for, as the Worklist does, and the copying of the source runs those attempts use.</para>
/// </summary>
public sealed class ParkComparisonService(ProductionCalculatorService calc, IDbContextFactory<AppDbContext> dbFactory)
{
    private const double SciencePerRun  = 0.02;
    private const double SccSurcharge   = 0.04;
    private const int    AttrRigCost    = 2595;   // a lab rig's cost bonus, percent, negative
    private const int    AttrHullCost   = 2601;   // an engineering complex's job cost multiplier

    private readonly IndustryTimeService _time      = new(dbFactory);
    private readonly InventionService    _invention = new(dbFactory);

    /// <summary>Every park, in the Indy Parks list's order (the default first, then by name).</summary>
    public async Task<List<(int Id, string Name)>> ParksAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.IndyParks.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
                    .Select(p => new { p.Id, p.Name }).ToListAsync(ct))
               .Select(p => (p.Id, p.Name)).ToList();
    }

    /// <summary>The decryptors, for the job list's invention lines.</summary>
    public Task<List<Decryptor>> DecryptorsAsync(CancellationToken ct = default) => _invention.DecryptorsAsync(ct);

    /// <summary>
    /// What a blueprint is good for: what it makes (0 for none) and whether it can be invented
    /// from. Empty for a type that is not a blueprint.
    /// </summary>
    public async Task<Dictionary<int, (int Product, bool Inventable)>> BlueprintsAsync(
        IReadOnlyCollection<int> typeIds, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var bps = await db.SdeBlueprints.AsNoTracking().Where(b => typeIds.Contains(b.TypeId)).Select(b => b.TypeId).ToListAsync(ct);
        var products = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => bps.Contains(p.TypeId))
            .Select(p => new { p.TypeId, p.Activity, p.ProductTypeId }).ToListAsync(ct);
        return bps.ToDictionary(id => id, id => (
            products.FirstOrDefault(p => p.TypeId == id && (p.Activity == "manufacturing" || p.Activity == "reaction"))?.ProductTypeId ?? 0,
            products.Any(p => p.TypeId == id && p.Activity == "invention")));
    }

    /// <summary>Every job-list line in every park given, park by park.</summary>
    public async Task<List<ParkCost>> CompareAsync(
        IReadOnlyList<ParkCompareJob> jobs, IReadOnlyList<(int Id, string Name, int Slot)> parks,
        CancellationToken ct = default)
    {
        // One cache for the run, so each park's context and timings are read once, not per line.
        using var scope = BuildCache.Begin();
        var facts = await ScienceFactsAsync(jobs, ct);

        var result = new List<ParkCost>();
        foreach (var park in parks)
        {
            ct.ThrowIfCancellationRequested();
            var ctx  = await calc.LoadContextAsync(park.Id, ct);
            var time = await _time.LoadAsync(park.Id, ct);
            foreach (var job in jobs)
                result.Add(job.Op == CompareOp.Build
                    ? await BuildAsync(park, job, ctx, facts, ct)
                    : Science(park, job, ctx, time, facts));
        }
        return result;
    }

    // ── Building ─────────────────────────────────────────────────────────────

    private async Task<ParkCost> BuildAsync((int Id, string Name, int Slot) park, ParkCompareJob job,
        ProductionContext ctx, ScienceFacts facts, CancellationToken ct)
    {
        // A blueprint on a build line builds what it makes.
        var typeId = facts.Blueprints.TryGetValue(job.TypeId, out var bp) && bp.Product > 0 ? bp.Product : job.TypeId;
        var plan = calc.Calculate(
            [new ProductionQueueEntry { TypeId = typeId, TypeName = job.TypeName, Quantity = job.Quantity, MeLevel = job.MeLevel }], ctx);
        var fp = plan.FinalProducts.FirstOrDefault(p => p.TypeId == typeId);
        if (fp is null || !plan.AllJobs.Any(j => j.OutputTypeId == typeId))
            return new ParkCost(park.Id, park.Name, park.Slot, job, false, 0, 0, 0, 0, []);

        await calc.ApplyDurationsAsync(plan, park.Id, ct);
        var terms = plan.AllJobs
            .Where(j => j.IndexWeight > 0 && j.CostIndexSystemId > 0)
            .Select(j => new IndexTerm(j.CostIndexSystemId, j.CostIndexActivity, j.IndexWeight, j.JobGross))
            .ToList();
        return new ParkCost(park.Id, park.Name, park.Slot, job, true, fp.QuantityProduced,
                            fp.TotalMaterialCost, fp.TotalJobCost, fp.TotalCost, terms,
                            plan.AllJobs.Any(j => j.DurationSeconds is null) ? null : plan.TotalJobSeconds);
    }

    // ── Science ──────────────────────────────────────────────────────────────

    private sealed record ScienceFacts(
        Dictionary<int, (int Product, bool Inventable)> Blueprints,
        Dictionary<int, InventionRecipe> Recipes,        // by source (T1) blueprint
        List<Decryptor> Decryptors,
        Dictionary<int, double> RigCost,                 // rig type → cost reduction, a fraction
        Dictionary<string, double> HullCost);            // structure type key → job cost multiplier

    private async Task<ScienceFacts> ScienceFactsAsync(IReadOnlyList<ParkCompareJob> jobs, CancellationToken ct)
    {
        var ids = jobs.Select(j => j.TypeId).Distinct().ToList();
        var blueprints = await BlueprintsAsync(ids, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // What each invention line's blueprint invents into: the likeliest, where there are several.
        var sources = jobs.Where(j => j.Op == CompareOp.Invention).Select(j => j.TypeId).Distinct().ToList();
        var invented = (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => sources.Contains(p.TypeId) && p.Activity == "invention")
                .Select(p => new { p.TypeId, p.ProductTypeId, p.Probability }).ToListAsync(ct))
            .GroupBy(p => p.TypeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Probability).First().ProductTypeId);
        var byInvented = invented.Count > 0 ? await _invention.LoadAsync([.. invented.Values], ct) : [];
        var recipes = invented.Where(kv => byInvented.ContainsKey(kv.Value)).ToDictionary(kv => kv.Key, kv => byInvented[kv.Value]);

        var decryptors = sources.Count > 0 ? await _invention.DecryptorsAsync(ct) : [];

        var rigIds = await db.IndyStructureRigs.AsNoTracking().Select(r => r.RigTypeId).Distinct().ToListAsync(ct);
        var rigCost = (await db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => rigIds.Contains(a.TypeId) && a.AttributeId == AttrRigCost)
                .Select(a => new { a.TypeId, a.Value }).ToListAsync(ct))
            .ToDictionary(a => a.TypeId, a => Math.Abs(a.Value) / 100.0);
        var hullCost = await (from a in db.SdeTypeDogmaAttributes.AsNoTracking()
                              join t in db.SdeTypes.AsNoTracking() on a.TypeId equals t.TypeId
                              where a.AttributeId == AttrHullCost
                              select new { t.Name, a.Value }).ToListAsync(ct);

        return new ScienceFacts(blueprints, recipes, decryptors, rigCost,
            hullCost.GroupBy(h => h.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Value));
    }

    /// <summary>All-V skills, the science ones included, as the calculator's durations assume.</summary>
    private static Dictionary<int, int> SkillsAtFive(InventionRecipe? recipe)
    {
        var skills = new Dictionary<int, int> { [3380] = 5, [3388] = 5, [45746] = 5, [3402] = 5, [3403] = 5, [3409] = 5 };
        if (recipe is not null)
        {
            skills[recipe.EncryptionSkillId] = 5;
            foreach (var id in recipe.ScienceSkillIds) skills[id] = 5;
        }
        return skills;
    }

    private static ParkCost Science((int Id, string Name, int Slot) park, ParkCompareJob job,
        ProductionContext ctx, IndustryTimeService.TimeContext time, ScienceFacts facts)
    {
        ParkCost No() => new(park.Id, park.Name, park.Slot, job, false, 0, 0, 0, 0, []);
        if (!facts.Blueprints.ContainsKey(job.TypeId)) return No();
        var bp = job.TypeId;

        switch (job.Op)
        {
            case CompareOp.Copy:
            {
                var runs   = Math.Max(1, job.Quantity);
                var copies = Math.Max(1, job.Copies);
                var copy   = ScienceJob(ctx, time, facts, bp, "bp_copying", "copying", IndustryTimeService.CopyingActivity,
                                        Eiv(ctx, bp) * SciencePerRun * runs * copies, SkillsAtFive(null), runs * copies, 0);
                if (copy is null) return No();
                return new ParkCost(park.Id, park.Name, park.Slot, job, true, runs * copies, 0, copy.Value.Cost, copy.Value.Cost,
                                    [copy.Value.Term], copy.Value.Seconds,
                                    string.Format(IndustryText.CompareDetailCopy, copies, runs));
            }
            case CompareOp.Invention:
            {
                if (!facts.Recipes.TryGetValue(bp, out var recipe)) return No();
                var decryptor = facts.Decryptors.FirstOrDefault(d => d.TypeId == job.DecryptorTypeId) ?? Decryptor.None;
                var skills = SkillsAtFive(recipe);
                var plan   = InventionService.Plan(recipe, decryptor, Math.Max(1, job.Quantity), skills);

                // The source copies the attempts use up, then the attempts themselves.
                var copy = ScienceJob(ctx, time, facts, bp, "bp_copying", "copying", IndustryTimeService.CopyingActivity,
                                      Eiv(ctx, bp) * SciencePerRun * plan.CopyRunsNeeded, skills, plan.CopyRunsNeeded, 0);
                var invent = ScienceJob(ctx, time, facts, bp, "bp_invention", "invention", IndustryTimeService.InventionActivity,
                                        Eiv(ctx, recipe.InventedBlueprintTypeId) * SciencePerRun * plan.Attempts, skills, plan.Attempts, 0);
                if (copy is null || invent is null) return No();

                var materials = plan.Materials.Sum(m => m.Quantity * PriceOf(ctx, m.TypeId));
                var jobCost   = copy.Value.Cost + invent.Value.Cost;
                return new ParkCost(park.Id, park.Name, park.Slot, job, true, plan.SuccessesNeeded * plan.RunsPerBpc,
                                    materials, jobCost, materials + jobCost, [copy.Value.Term, invent.Value.Term],
                                    copy.Value.Seconds + invent.Value.Seconds,
                                    string.Format(IndustryText.CompareDetailInvention, plan.Attempts, plan.Chance,
                                                  plan.SuccessesNeeded, plan.RunsPerBpc, plan.InventedMe, plan.InventedTe,
                                                  plan.CopyRunsNeeded, decryptor.IsNone ? IndustryText.CompareNoDecryptor : SdeNames.Type(decryptor.TypeId, decryptor.Name)));
            }
            case CompareOp.MeResearch:
            case CompareOp.TeResearch:
            {
                var me    = job.Op == CompareOp.MeResearch;
                var level = Math.Clamp(job.TargetLevel, 1, 10);
                var job2  = ScienceJob(ctx, time, facts, bp, "bp_research",
                                       me ? "researching_material_efficiency" : "researching_time_efficiency",
                                       me ? IndustryTimeService.MeResearchActivity : IndustryTimeService.TeResearchActivity,
                                       Eiv(ctx, bp) * SciencePerRun * IndustryTimeService.ResearchLevelFactor[level] / 105.0,
                                       SkillsAtFive(null), 1, level);
                if (job2 is null) return No();
                return new ParkCost(park.Id, park.Name, park.Slot, job, true, 1, 0, job2.Value.Cost, job2.Value.Cost,
                                    [job2.Value.Term], job2.Value.Seconds,
                                    me ? string.Format(IndustryText.CompareDetailMe, level)
                                       : string.Format(IndustryText.CompareDetailTe, level * 2));
            }
            default:
                return No();
        }
    }

    /// <summary>
    /// One science job: its install cost, the part of that the index sets, and its time. Null
    /// when the blueprint has no time on record for the activity.
    /// </summary>
    /// <param name="indexActivity">The activity as ESI names its cost index.</param>
    /// <param name="timeActivity">The activity as the blueprint's times name it.</param>
    /// <param name="units">Copy runs or invention attempts, which the time is per; research: 1.</param>
    private static (decimal Cost, IndexTerm Term, double Seconds)? ScienceJob(
        ProductionContext ctx, IndustryTimeService.TimeContext time, ScienceFacts facts,
        int blueprintTypeId, string categoryKey, string indexActivity, string timeActivity,
        double jobEiv, IReadOnlyDictionary<int, int> skills, long units, int researchLevel)
    {
        var structure = StructureFor(ctx, categoryKey);
        double? seconds = timeActivity is IndustryTimeService.MeResearchActivity or IndustryTimeService.TeResearchActivity
            ? IndustryTimeService.PerResearchSeconds(time, blueprintTypeId, timeActivity, researchLevel, structure, categoryKey, skills)
            : IndustryTimeService.PerScienceUnitSeconds(time, blueprintTypeId, timeActivity, structure, categoryKey, skills) * units;
        if (seconds is null) return null;

        var systemId = structure is not null && ctx.SystemIds.TryGetValue(structure.SystemName, out var sys) ? sys : 0;
        var index    = systemId > 0 && ctx.CiLookup.TryGetValue(systemId, out var byAct) && byAct.TryGetValue(indexActivity, out var ci) ? ci : 0;
        var role     = structure is not null && facts.HullCost.TryGetValue(structure.StructureTypeKey.ToLowerInvariant(), out var r) ? r : 1.0;
        var rigs     = RigCost(ctx, facts, structure, categoryKey);
        var weight   = jobEiv * role * Math.Max(0, 1 - rigs);
        var gross    = Math.Round((decimal)(weight * index), 0);
        var taxes    = Math.Round((decimal)(jobEiv * ((structure is not null ? (double)structure.FacilityTax / 100.0 : 0) + SccSurcharge)), 0);
        return (gross + taxes, new IndexTerm(systemId, indexActivity, weight, gross), seconds.Value);
    }

    /// <summary>The structure a park gives a kind of job: its assignment, else the park's default.</summary>
    private static IndyStructure? StructureFor(ProductionContext ctx, string categoryKey) =>
        ctx.StructByCategory.TryGetValue(categoryKey, out var s) && s is not null ? s : ctx.DefaultStructure;

    /// <summary>The lab rigs' cost reduction for this kind of job, scaled by security as every rig is.</summary>
    private static double RigCost(ProductionContext ctx, ScienceFacts facts, IndyStructure? structure, string categoryKey)
    {
        if (structure is null) return 0;
        double total = 0;
        foreach (var rig in ctx.Rigs.Where(r => r.StructureId == structure.Id && r.RigTypeId != 0))
        {
            if (!IndyRigMatching.RigApplies(ctx.RigCategoryKeys.GetValueOrDefault(rig.RigTypeId, ""), categoryKey)) continue;
            if (!facts.RigCost.TryGetValue(rig.RigTypeId, out var pct) || pct <= 0) continue;
            var mult = structure.SecurityClass switch
            {
                "lowsec"                => ctx.RigLowsecMultAttr.TryGetValue(rig.RigTypeId, out var l) ? l : 1.9,
                "nullsec" or "wormhole" => ctx.RigNullsecMultAttr.TryGetValue(rig.RigTypeId, out var n) ? n : 2.1,
                _                       => 1.0,
            };
            total += pct * mult;
        }
        return total;
    }

    /// <summary>
    /// The manufacturing value of what a blueprint makes, the base every job cost starts from:
    /// its ME 0 materials at the adjusted prices.
    /// </summary>
    private static double Eiv(ProductionContext ctx, int blueprintTypeId) =>
        ctx.MaterialsByBp.TryGetValue(blueprintTypeId, out var mats)
            ? mats.Sum(m => m.Quantity * ctx.AdjPrices.GetValueOrDefault(m.MaterialTypeId, 0.0))
            : 0;

    /// <summary>A material's price, as the calculator prices one: the market, else its build cost marked up.</summary>
    private static decimal PriceOf(ProductionContext ctx, int typeId) =>
        ctx.UnitCosts.TryGetValue(typeId, out var p) && p > 0 ? p
        : ctx.BuildCostLookup.TryGetValue(typeId, out var bc) && bc > 0 ? bc * (decimal)ctx.MarkupFactor
        : 0m;

    /// <summary>
    /// The cost again at another day's indices. A term whose index that day is not known keeps
    /// today's share, so a gap in the history bends the line no more than it has to.
    /// </summary>
    public static decimal TotalAt(ParkCost cost, Func<int, string, double?> indexOn)
    {
        var total = cost.Total;
        foreach (var t in cost.Terms)
            if (indexOn(t.SystemId, t.Activity) is { } index)
                total += Math.Round((decimal)(t.Weight * index), 0) - t.Gross;
        return total;
    }
}

/// <summary>
/// System cost indices, now and over time. The history is the map statistics' own record
/// (MapIndustryIndices): hourly for the last day, one sample a day before that.
/// </summary>
public sealed class CostIndexHistoryService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>The six activities ESI reports an index for, in the order shown.</summary>
    public static readonly string[] Activities =
    [
        "manufacturing", "reaction", "invention", "copying",
        "researching_material_efficiency", "researching_time_efficiency",
    ];

    /// <summary>Today's indices: the snapshot the Production Calculator costs jobs with.</summary>
    public async Task<Dictionary<(int SystemId, string Activity), double>> CurrentAsync(
        IReadOnlyCollection<int> systems, CancellationToken ct = default)
    {
        if (systems.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.IndustryCostIndices.AsNoTracking()
                    .Where(i => systems.Contains(i.SolarSystemId))
                    .Select(i => new { i.SolarSystemId, i.Activity, i.CostIndex }).ToListAsync(ct))
               .GroupBy(i => (i.SolarSystemId, i.Activity))
               .ToDictionary(g => g.Key, g => g.First().CostIndex);
    }

    /// <summary>One value a day per system and activity: the day's samples averaged.</summary>
    public async Task<Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>>> DailyAsync(
        IReadOnlyCollection<int> systems, IReadOnlyCollection<string> activities, CancellationToken ct = default)
    {
        var result = new Dictionary<(int, string), SortedDictionary<DateOnly, double>>();
        if (systems.Count == 0 || activities.Count == 0) return result;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MapIndustryIndices.AsNoTracking()
            .Where(i => systems.Contains(i.SystemId) && activities.Contains(i.Activity))
            .Select(i => new { i.SystemId, i.Activity, i.Bucket, i.CostIndex })
            .ToListAsync(ct);

        // Bucket is "yyyy-MM-dd HH", UTC.
        foreach (var g in rows.GroupBy(r => (r.SystemId, r.Activity)))
        {
            var byDay = new SortedDictionary<DateOnly, double>();
            foreach (var d in g.GroupBy(r => r.Bucket.Length >= 10 ? r.Bucket[..10] : r.Bucket))
                if (DateOnly.TryParseExact(d.Key, "yyyy-MM-dd", out var day))
                    byDay[day] = d.Average(r => r.CostIndex);
            result[g.Key] = byDay;
        }
        return result;
    }

    /// <summary>The value on a day, or the last one before it; null before the first.</summary>
    public static double? On(SortedDictionary<DateOnly, double>? series, DateOnly day)
    {
        if (series is null || series.Count == 0) return null;
        if (series.TryGetValue(day, out var v)) return v;
        double? last = null;
        foreach (var (d, value) in series)
        {
            if (d > day) break;
            last = value;
        }
        return last;
    }
}
