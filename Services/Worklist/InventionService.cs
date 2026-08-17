using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// A decryptor and what it does to the invention it is used in.
///
/// <para>All four effects come off the item's own dogma attributes rather than a table written
/// here, including CCP's long-standing misspelling of the probability one.</para>
/// </summary>
public sealed record Decryptor(
    int TypeId, string Name, double ChanceMultiplier, int MeModifier, int TeModifier, int RunModifier)
{
    /// <summary>Inventing without one — the neutral element, so callers need no null branch.</summary>
    public static readonly Decryptor None = new(0, "no decryptor", 1.0, 0, 0, 0);

    public bool IsNone => TypeId == 0;
}

/// <summary>
/// Everything needed to invent one T2 blueprint: what it comes from, how likely it is, and what
/// the attempt consumes.
/// </summary>
/// <param name="BaseRunsPerSuccess">Runs on the copy a success produces, before any decryptor.
/// One for ships, ten for modules, ammo and drones — read from the SDE rather than assumed, since
/// the split is not exactly the one the words suggest.</param>
/// <param name="MaxCopyRuns">Licensed runs a single copy of the source blueprint can carry. This
/// one <i>is</i> a real ceiling, unlike the manufacturing run limit of the same name.</param>
public sealed record InventionRecipe(
    int    SourceBlueprintTypeId,
    string SourceBlueprintName,
    int    InventedBlueprintTypeId,
    int    ProductTypeId,
    double BaseChance,
    int    BaseRunsPerSuccess,
    IReadOnlyList<(int TypeId, long Quantity)> Datacores,
    int    EncryptionSkillId,
    IReadOnlyList<int> ScienceSkillIds,
    int    MaxCopyRuns);

/// <summary>What a given amount of T2 production actually costs in invention.</summary>
/// <param name="Attempts">Invention runs to expect to need. An expectation, not a guarantee — see
/// the note on <see cref="InventionService.Plan"/>.</param>
/// <param name="CopyRunsNeeded">Runs drawn off source copies, one per attempt.</param>
/// <param name="Materials">Datacores and decryptors for the whole batch, ready to be checked
/// against stock or turned into purchases.</param>
public sealed record InventionPlan(
    InventionRecipe Recipe,
    Decryptor       Decryptor,
    double          Chance,
    int             RunsPerBpc,
    long            SuccessesNeeded,
    long            Attempts,
    long            CopyRunsNeeded,
    long            CopiesNeeded,
    IReadOnlyList<(int TypeId, long Quantity)> Materials,
    int             InventedMe,
    int             InventedTe);

/// <summary>
/// The invention half of T2 production: which T2 items are invented, from what, at what odds, and
/// how much of everything a run of the line consumes.
///
/// <para>Kept apart from <see cref="IndustryJobGenerator"/> because invention is not a variation on
/// manufacturing. A manufacturing job of N runs produces N items; an invention job of N runs
/// produces somewhere between zero and N blueprints, each carrying several runs of its own, and the
/// thing it consumes is a licence rather than a material. Folding that into the job generator would
/// have meant a second meaning for nearly every quantity it handles.</para>
///
/// <para><b>The chance formula is additive in the skills, not multiplicative.</b> It reads
/// <c>base × (1 + enc/40 + (sci₁ + sci₂)/30) × decryptor</c>. This was settled against the player's
/// own 48 completed invention jobs rather than from memory: every one of the six distinct
/// skill-and-blueprint combinations divides out to exactly 1.5 or 1.9 — Parity and Optimized
/// Attainment — under the additive form, and to 1.47–1.87 under the multiplicative one. A rule that
/// lands on the published decryptor multipliers to six decimal places across 48 jobs is the right
/// rule.</para>
/// </summary>
public class InventionService(IDbContextFactory<AppDbContext> dbFactory)
{
    private const string InventionActivity = "invention";

    // Invention always yields ME2/TE4 before the decryptor adjusts it. Not in the SDE as a field —
    // it is a game rule — so it is named here rather than buried as a literal at the call site.
    public const int BaseInventedMe = 2;
    public const int BaseInventedTe = 4;

    // Dogma attributes on the decryptor items themselves. 1112 is spelled the way CCP spelled it.
    private const int AttrChanceMultiplier = 1112;   // inventionPropabilityMultiplier
    private const int AttrMeModifier       = 1113;
    private const int AttrTeModifier       = 1114;
    private const int AttrRunModifier      = 1124;

    /// <summary>
    /// The eight decryptors T2 invention actually uses.
    ///
    /// <para>Pinned to the group rather than found by which attributes an item carries, because
    /// forty-eight other published types carry the same four: four full racial sets left over from
    /// before decryptors were unified (Occult Parity, Cryptic Parity and so on, identical in effect
    /// and no longer obtainable), the Sleeper, Yan Jung, Takmahl and Talocan relics that drive T3
    /// invention, and the subsystem data interfaces. Offering any of those as a choice for a T2 job
    /// would be offering something that cannot be put in it.</para>
    /// </summary>
    private const int GenericDecryptorGroupId = 1304;

    /// <summary>
    /// Every decryptor, cheapest effect first. Eight of them, and the list is stable enough that
    /// callers may hold it for a whole refresh.
    /// </summary>
    public async Task<List<Decryptor>> DecryptorsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => t.GroupId == GenericDecryptorGroupId)
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);
        if (names.Count == 0) return [];

        var ids = names.Keys.ToList();

        var attrs = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => ids.Contains(a.TypeId)
                     && (a.AttributeId == AttrChanceMultiplier || a.AttributeId == AttrMeModifier
                      || a.AttributeId == AttrTeModifier       || a.AttributeId == AttrRunModifier))
            .Select(a => new { a.TypeId, a.AttributeId, a.Value })
            .ToListAsync(ct);

        var byType = attrs.GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.AttributeId, a => a.Value));

        return byType
            .Select(kv => new Decryptor(
                kv.Key,
                names.GetValueOrDefault(kv.Key, $"Type {kv.Key}"),
                kv.Value.GetValueOrDefault(AttrChanceMultiplier, 1.0),
                (int)kv.Value.GetValueOrDefault(AttrMeModifier),
                (int)kv.Value.GetValueOrDefault(AttrTeModifier),
                (int)kv.Value.GetValueOrDefault(AttrRunModifier)))
            .OrderBy(d => d.ChanceMultiplier)
            .ThenBy(d => d.Name)
            .ToList();
    }

    /// <summary>
    /// Invention recipes for the given T2 products, keyed by product type id. Products that are not
    /// invented — T1, faction, anything bought — are simply absent.
    /// </summary>
    public async Task<Dictionary<int, InventionRecipe>> LoadAsync(
        IReadOnlyCollection<int> productTypeIds, CancellationToken ct = default)
    {
        if (productTypeIds.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // The chain runs product → its manufacturing blueprint → the invention row that produces
        // that blueprint → the T1 blueprint the invention runs from. Three hops, because the SDE
        // has no direct edge from a T2 item to the T1 blueprint it descends from.
        var mfg = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.Activity == "manufacturing" && productTypeIds.Contains(p.ProductTypeId))
            .Select(p => new { p.TypeId, p.ProductTypeId })
            .ToListAsync(ct);
        if (mfg.Count == 0) return [];

        var t2BpIds = mfg.Select(p => p.TypeId).Distinct().ToList();

        var invention = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => p.Activity == InventionActivity && t2BpIds.Contains(p.ProductTypeId))
            .Select(p => new { SourceBp = p.TypeId, InventedBp = p.ProductTypeId, p.Probability, p.Quantity })
            .ToListAsync(ct);
        if (invention.Count == 0) return [];

        var sourceIds = invention.Select(i => i.SourceBp).Distinct().ToList();

        var datacores = (await db.SdeBlueprintMaterials.AsNoTracking()
                .Where(m => m.Activity == InventionActivity && sourceIds.Contains(m.TypeId))
                .Select(m => new { m.TypeId, m.MaterialTypeId, m.Quantity })
                .ToListAsync(ct))
            .GroupBy(m => m.TypeId)
            .ToDictionary(g => g.Key,
                          g => (IReadOnlyList<(int, long)>)g
                               .Select(m => (m.MaterialTypeId, (long)m.Quantity)).ToList());

        var skillRows = await db.SdeBlueprintSkills.AsNoTracking()
            .Where(s => s.Activity == InventionActivity && sourceIds.Contains(s.TypeId))
            .Select(s => new { s.TypeId, s.SkillTypeId })
            .ToListAsync(ct);

        var skillIds   = skillRows.Select(s => s.SkillTypeId).Distinct().ToList();
        var skillNames = await db.SdeTypes.AsNoTracking()
            .Where(t => skillIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // The three invention skills split two ways and are weighted differently, so they have to
        // be told apart. The racial encryption skill is the only one whose name ends that way,
        // which is how the game groups them too.
        var skillsBySource = skillRows.GroupBy(s => s.TypeId).ToDictionary(
            g => g.Key,
            g => (
                Encryption: g.Select(s => s.SkillTypeId).FirstOrDefault(
                    id => skillNames.GetValueOrDefault(id, "")
                                    .EndsWith("Encryption Methods", StringComparison.Ordinal)),
                Science: (IReadOnlyList<int>)g.Select(s => s.SkillTypeId).Where(
                    id => !skillNames.GetValueOrDefault(id, "")
                                     .EndsWith("Encryption Methods", StringComparison.Ordinal)).ToList()));

        var maxRuns = await db.SdeBlueprints.AsNoTracking()
            .Where(b => sourceIds.Contains(b.TypeId))
            .ToDictionaryAsync(b => b.TypeId, b => b.MaxProductionLimit, ct);

        var sourceNames = await db.SdeTypes.AsNoTracking()
            .Where(t => sourceIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var inventionByT2Bp = invention.ToDictionary(i => i.InventedBp, i => i);

        var result = new Dictionary<int, InventionRecipe>();

        foreach (var m in mfg)
        {
            if (!inventionByT2Bp.TryGetValue(m.TypeId, out var inv)) continue;

            var skills = skillsBySource.GetValueOrDefault(inv.SourceBp);

            result[m.ProductTypeId] = new InventionRecipe(
                SourceBlueprintTypeId:   inv.SourceBp,
                SourceBlueprintName:     sourceNames.GetValueOrDefault(inv.SourceBp, $"Type {inv.SourceBp}"),
                InventedBlueprintTypeId: m.TypeId,
                ProductTypeId:           m.ProductTypeId,
                BaseChance:              inv.Probability,
                BaseRunsPerSuccess:      Math.Max(1, inv.Quantity),
                Datacores:               datacores.GetValueOrDefault(inv.SourceBp, []),
                EncryptionSkillId:       skills.Encryption,
                ScienceSkillIds:         skills.Science ?? [],
                // Twenty is the near-universal value, but a few blueprints differ and a wrong
                // ceiling here silently sizes the copy job wrong.
                MaxCopyRuns:             Math.Max(1, maxRuns.GetValueOrDefault(inv.SourceBp, 1)));
        }

        return result;
    }

    /// <summary>Where a science job runs: the real facility, its name, and the park row whose
    /// rigs and security class decide how long the job takes.</summary>
    public readonly record struct Lab(long Site, string Name, IndyStructure? Structure);

    private const int ShipCategoryId = 6;

    /// <summary>
    /// Which decryptor a product gets.
    ///
    /// <para>Ships and everything else are configured separately because they invent differently:
    /// a hull comes out at one run a success and is transformed by three more, while a module
    /// already gets ten and is not. An unrecognised name reads as no decryptor, which costs runs
    /// and never invents something that cannot be invented.</para>
    /// </summary>
    public static Decryptor DecryptorFor(
        int productTypeId, ProductionContext ctx, IReadOnlyList<Decryptor> all,
        string shipChoice, string otherChoice)
    {
        var isShip = ctx.TypeGroupMap.TryGetValue(productTypeId, out var g)
                  && ctx.GroupCatMap.TryGetValue(g.GroupId, out var cat)
                  && cat.CategoryId == ShipCategoryId;

        var wanted = isShip ? shipChoice : otherChoice;

        return wanted.Length == 0
            ? Decryptor.None
            : all.FirstOrDefault(d => d.Name == wanted) ?? Decryptor.None;
    }

    /// <summary>Park category keys for the two science activities, as Indy Parks already names
    /// them. <see cref="IndyRigMatching.RigCategoryFromName"/> derives the same keys from rig
    /// names, so a lab's rigs and its assignment agree by construction.</summary>
    public const string CopyingCategory   = "bp_copying";
    public const string InventionCategory = "bp_invention";

    /// <summary>
    /// The lab a science job runs in, read from the park's own category assignment.
    ///
    /// <para>Routed exactly as manufacturing is: Indy Parks already lets a park say which structure
    /// does Blueprint Copying and which does Blueprint Invention, and that assignment is the
    /// answer. An earlier version of this asked for the structure again as a worklist preference
    /// and fell back to the park's first linked facility — which silently planned every invention
    /// job in whichever structure happened to sort first, and modelled its rigs rather than the
    /// lab's.</para>
    ///
    /// <para>Null when the park has not assigned that activity. Guessing would put the job in a
    /// structure that may not even have a laboratory, and saying nothing is the honest answer.</para>
    /// </summary>
    public static async Task<Lab?> LabAsync(
        AppDbContext db, int parkId, string categoryKey, CancellationToken ct = default)
    {
        if (parkId <= 0) return null;

        var assignment = await db.IndyCategoryAssignments.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.CategoryKey == categoryKey && a.StructureId != null)
            .Select(a => a.StructureId)
            .FirstOrDefaultAsync(ct);
        if (assignment is null) return null;

        var s = await db.IndyStructures.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == assignment && x.RealStructureId != null, ct);
        if (s is null) return null;

        var id = s.RealStructureId!.Value;

        var name = s.RealStructureName is { Length: > 0 } rn
            ? rn
            : await db.EsiCorpStructures.AsNoTracking()
                      .Where(c => c.StructureId == id).Select(c => c.Name).FirstOrDefaultAsync(ct)
              ?? s.DisplayName;

        return new Lab(id, name, s);
    }

    /// <summary>One item's worth of invention work, with the demand entry that asked for it.</summary>
    /// <param name="ShortRuns">T2 runs the demand still wants after the runs already sitting on
    /// invented copies are netted off.</param>
    public sealed record InventionNeed(BuildDemand Demand, InventionRecipe Recipe, InventionPlan Plan, long ShortRuns);

    /// <summary>
    /// Every invented item in the demand, planned once.
    ///
    /// <para>Shared between the generator that raises the jobs and the logistics pass that hauls
    /// their datacores, because the two must agree to the unit. Working the attempt count out
    /// twice is the same mistake the job generator already made once with build demand — the
    /// comment on <c>AddJobDemandAsync</c> records what it cost — and invention is worse, since a
    /// disagreement here means hauling datacores for a batch size that is never run.</para>
    /// </summary>
    /// <param name="scientistSkills">Skill maps of the characters who could install the job. The
    /// best odds win, since that is who the generator will assign it to.</param>
    public async Task<List<InventionNeed>> PlanDemandAsync(
        IReadOnlyDictionary<int, BuildDemand>            demand,
        IReadOnlyDictionary<int, SdeBlueprintProduct>    blueprintByProduct,
        IReadOnlyDictionary<int, List<BlueprintStock>>   printsByType,
        PrintOwnership                                   owned,
        Func<int, Decryptor>                             decryptorFor,
        IReadOnlyList<IReadOnlyDictionary<int, int>>     scientistSkills,
        CancellationToken ct = default)
    {
        var recipes = await LoadAsync(demand.Keys.ToList(), ct);
        if (recipes.Count == 0 || scientistSkills.Count == 0) return [];

        var needs = new List<InventionNeed>();

        foreach (var d in demand.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.TypeId))
        {
            if (!recipes.TryGetValue(d.TypeId, out var recipe)) continue;
            if (!blueprintByProduct.TryGetValue(d.TypeId, out var product)) continue;

            var t2Runs = IndustryJobSplit.RunsFor(d.Units, Math.Max(1, product.Quantity));

            // Runs on invented copies already owned come off the top. A blueprint is not needed
            // per shortfall — it is needed per run the shortfall has left uncovered, and an
            // original covers every run there will ever be.
            var invented = printsByType.GetValueOrDefault(recipe.InventedBlueprintTypeId, [])
                                       .Where(owned.Owns).ToList();
            if (invented.Any(b => b.IsOriginal)) continue;

            var shortRuns = t2Runs - invented.Sum(b => (long)b.Runs);
            if (shortRuns <= 0) continue;

            var decryptor = decryptorFor(d.TypeId);
            var skills    = scientistSkills
                .OrderByDescending(s => Chance(recipe, decryptor, s))
                .First();

            var plan = Plan(recipe, decryptor, shortRuns, skills);
            if (plan.Attempts <= 0) continue;

            needs.Add(new InventionNeed(d, recipe, plan, shortRuns));
        }

        return needs;
    }

    /// <summary>
    /// The chance one invention run succeeds.
    ///
    /// <para><c>base × (1 + enc/40 + (sci₁ + sci₂)/30) × decryptor</c> — additive in the skill
    /// terms. Confirmed against 48 of the player's completed jobs; see the note on the class.</para>
    /// </summary>
    public static double Chance(
        InventionRecipe recipe, Decryptor decryptor, IReadOnlyDictionary<int, int> skills)
    {
        var enc = Math.Clamp(skills.GetValueOrDefault(recipe.EncryptionSkillId), 0, 5);
        var sci = recipe.ScienceSkillIds.Sum(id => Math.Clamp(skills.GetValueOrDefault(id), 0, 5));

        var chance = recipe.BaseChance * (1 + enc / 40.0 + sci / 30.0) * decryptor.ChanceMultiplier;

        // Capped at certainty. Nothing in the game reaches it, but a plan that divided by a chance
        // above one would ask for fewer attempts than successes.
        return Math.Clamp(chance, 0.0001, 1.0);
    }

    /// <summary>
    /// What it takes to get <paramref name="t2RunsWanted"/> runs of T2 production out of invention.
    ///
    /// <para><b>Attempts are an expectation, not a promise.</b> Needing three successes at 45% gives
    /// seven attempts, and seven attempts will sometimes yield two. Rounding up the division is the
    /// honest middle: planning the mean is what a player does by hand, and planning a confidence
    /// bound instead would have the tool buying half again as many datacores as anyone actually
    /// buys. The shortfall reappears on the next refresh, because the worklist is regenerated from
    /// live state rather than remembered.</para>
    /// </summary>
    public static InventionPlan Plan(
        InventionRecipe recipe, Decryptor decryptor, long t2RunsWanted,
        IReadOnlyDictionary<int, int> skills)
    {
        var chance     = Chance(recipe, decryptor, skills);
        var runsPerBpc = Math.Max(1, recipe.BaseRunsPerSuccess + decryptor.RunModifier);

        var successes = t2RunsWanted <= 0 ? 0 : IndustryJobSplit.RunsFor(t2RunsWanted, runsPerBpc);
        var attempts  = successes <= 0 ? 0 : (long)Math.Ceiling(successes / chance);

        // One run comes off a source copy per attempt — the copy is returned with the rest, which
        // is why a 20-run copy feeds twenty attempts rather than one.
        var copyRuns = attempts;
        var copies   = IndustryJobSplit.RunsFor(copyRuns, recipe.MaxCopyRuns);

        var materials = recipe.Datacores
            .Select(d => (d.TypeId, d.Quantity * attempts))
            .Concat(decryptor.IsNone ? [] : new[] { (decryptor.TypeId, attempts) })
            .Where(m => m.Item2 > 0)
            .ToList();

        return new InventionPlan(
            recipe, decryptor, chance, runsPerBpc,
            successes, attempts, copyRuns, copies, materials,
            BaseInventedMe + decryptor.MeModifier,
            BaseInventedTe + decryptor.TeModifier);
    }
}
