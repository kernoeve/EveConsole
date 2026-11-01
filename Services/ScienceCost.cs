using EveConsole.Models;
using EveConsole.Services.Worklist;

namespace EveConsole.Services;

/// <summary>What one run of an invented blueprint copy costs to make, and how that came about.</summary>
/// <param name="PerRun">The cost a T2 manufacturing run carries for its copy: an attempt's cost
/// over the runs an attempt yields on average.</param>
/// <param name="PerAttempt">Datacores, the decryptor, the source run (a copy run, or the relic
/// itself) and both job fees.</param>
public sealed record InventedCopyCost(
    decimal PerRun, decimal PerAttempt, double Chance, int RunsPerSuccess, Decryptor Decryptor);

/// <summary>
/// What science jobs cost, by the game's rules: copying and invention, for the plans that price a
/// T2 copy at its invention and for the park comparison's science lines.
///
/// <para>A science job's estimated item value is 2% of the manufacturing value — ME 0 materials at
/// adjusted prices — of what the blueprint makes: per copy run, or per invention attempt of the
/// invented blueprint's product. That value × system index × the hull's role cost bonus × (1 − the
/// lab rigs' cost bonus), plus facility tax and the SCC surcharge on the value itself.</para>
///
/// <para>⚠️ One copy of these rules. The park comparison had them to itself; the build costs
/// needed the same copy and invention fees, and two transcriptions of a fee formula are how the
/// rig matching came to exist three times and disagree.</para>
/// </summary>
public static class ScienceCost
{
    public const double SciencePerRun = 0.02;
    public const double SccSurcharge  = 0.04;
    // ⚠️ ME and TE research pay half the SCC surcharge since the July 2025 "Exploration &
    // Industry Balance Rework" (CCP calls it temporary). Copying and invention still pay 4%:
    // your own copy jobs match at 4% to 0.1%, and research jobs only match at 2%.
    public const double ResearchSccSurcharge = 0.02;

    /// <summary>All-V skills, the science ones included, as the calculator's durations assume.</summary>
    public static Dictionary<int, int> SkillsAtFive(InventionRecipe? recipe)
    {
        var skills = new Dictionary<int, int> { [3380] = 5, [3388] = 5, [45746] = 5, [3402] = 5, [3403] = 5, [3409] = 5 };
        if (recipe is not null)
        {
            skills[recipe.EncryptionSkillId] = 5;
            foreach (var id in recipe.ScienceSkillIds) skills[id] = 5;
        }
        return skills;
    }

    /// <summary>The structure a park gives a kind of job: its assignment, else the park's default.</summary>
    public static IndyStructure? StructureFor(ProductionContext ctx, string categoryKey) =>
        ctx.StructByCategory.TryGetValue(IndyRigMatching.StructureCategory(categoryKey), out var s) && s is not null ? s : ctx.DefaultStructure;

    /// <summary>The lab rigs' cost reduction for this kind of job, scaled by security as every rig is.</summary>
    public static double RigCost(ProductionContext ctx, IndyStructure? structure, string categoryKey)
    {
        if (structure is null) return 0;
        double total = 0;
        foreach (var rig in ctx.Rigs.Where(r => r.StructureId == structure.Id && r.RigTypeId != 0))
        {
            if (!IndyRigMatching.RigApplies(ctx.RigCategoryKeys.GetValueOrDefault(rig.RigTypeId, ""), categoryKey)) continue;
            if (!ctx.LabRigCost.TryGetValue(rig.RigTypeId, out var pct) || pct <= 0) continue;
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
    public static double Eiv(ProductionContext ctx, int blueprintTypeId) =>
        ctx.MaterialsByBp.TryGetValue(blueprintTypeId, out var mats)
            ? mats.Sum(m => m.Quantity * ctx.AdjPrices.GetValueOrDefault(m.MaterialTypeId, 0.0))
            : 0;

    /// <summary>
    /// One science job's install cost, and the part of it the system index sets: <paramref name="Weight"/>
    /// × index, rounded, is <c>Gross</c>.
    /// </summary>
    /// <param name="indexActivity">The activity as ESI names its cost index.</param>
    /// <param name="jobEiv">The job's estimated item value: 2% of <see cref="Eiv"/> per unit, times the units.</param>
    public static (decimal Cost, int SystemId, double Weight, decimal Gross) Fee(
        ProductionContext ctx, IndyStructure? structure, string categoryKey, string indexActivity,
        double jobEiv, double scc)
    {
        var systemId = structure is not null && ctx.SystemIds.TryGetValue(structure.SystemName, out var sys) ? sys : 0;
        var index    = systemId > 0 && ctx.CiLookup.TryGetValue(systemId, out var byAct) && byAct.TryGetValue(indexActivity, out var ci) ? ci : 0;
        var role     = structure is not null && ctx.HullCost.TryGetValue(structure.StructureTypeKey.ToLowerInvariant(), out var r) ? r : 1.0;
        var rigs     = RigCost(ctx, structure, categoryKey);
        var weight   = jobEiv * role * Math.Max(0, 1 - rigs);
        var gross    = Math.Round((decimal)(weight * index), 0);
        var taxes    = Math.Round((decimal)(jobEiv * ((structure is not null ? (double)structure.FacilityTax / 100.0 : 0) + scc)), 0);
        return (gross + taxes, systemId, weight, gross);
    }

    /// <summary>
    /// What one run of an invented blueprint costs, made in this park's labs. Null for a blueprint
    /// that is not invented.
    ///
    /// <para>An attempt eats its datacores, its decryptor, and one run of a T1 copy — copied off
    /// an original, which costs nothing to keep — or, for T3, the relic whole. It succeeds at the
    /// chance all-V skills give, and a success carries the decryptor-adjusted runs. So a run's
    /// share is an attempt's cost over chance × runs: the mean, as the Worklist plans it.</para>
    ///
    /// <para>The decryptor is the Worklist's choice for ships or for everything else, so the build
    /// cost prices the invention the Worklist would actually raise.</para>
    /// </summary>
    /// <param name="priceOf">A material's price — the plan's, so a price override on a datacore
    /// counts here as it does everywhere else.</param>
    public static InventedCopyCost? Invention(ProductionContext ctx, int inventedBlueprintTypeId, Func<int, decimal> priceOf)
    {
        if (!ctx.InventionRecipes.TryGetValue(inventedBlueprintTypeId, out var recipe)) return null;

        var decryptor = InventionService.DecryptorFor(
            recipe.ManufacturedTypeId, ctx, ctx.Decryptors, ctx.ShipDecryptor, ctx.OtherDecryptor);
        var chance = InventionService.Chance(recipe, decryptor, SkillsAtFive(recipe));
        var runs   = Math.Max(1, recipe.BaseRunsPerSuccess + decryptor.RunModifier);

        var attempt = recipe.Datacores.Sum(d => d.Quantity * priceOf(d.TypeId));
        if (!decryptor.IsNone) attempt += priceOf(decryptor.TypeId);

        if (ctx.RelicSources.Contains(recipe.SourceBlueprintTypeId))
            attempt += priceOf(recipe.SourceBlueprintTypeId);
        else
        {
            var copyLab = StructureFor(ctx, InventionService.CopyingCategory);
            attempt += Fee(ctx, copyLab, InventionService.CopyingCategory, "copying",
                           Eiv(ctx, recipe.SourceBlueprintTypeId) * SciencePerRun, SccSurcharge).Cost;
        }

        var lab = StructureFor(ctx, InventionService.InventionCategory);
        attempt += Fee(ctx, lab, InventionService.InventionCategory, "invention",
                       Eiv(ctx, inventedBlueprintTypeId) * SciencePerRun, SccSurcharge).Cost;

        return new InventedCopyCost(attempt / (decimal)(chance * runs), attempt, chance, runs, decryptor);
    }
}
