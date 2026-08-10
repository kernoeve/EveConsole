using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>Why a job's facility is or isn't bonusing it.</summary>
public enum FacilityRigVerdict
{
    /// <summary>A fitted rig bonuses this job's category.</summary>
    Rigged,
    /// <summary>The facility is linked to a park structure, but nothing fitted there
    /// bonuses this job's category.</summary>
    NotRigged,
    /// <summary>The facility isn't linked to any park structure, so its rigs are
    /// unknown. Never reported as a problem.</summary>
    Unknown,
    /// <summary>Research, copying or invention — bonused by rigs this check does not
    /// model, so silence rather than a false alarm.</summary>
    NotApplicable,
}

public sealed record FacilityRigResult(int JobId, FacilityRigVerdict Verdict, string Note);

/// <summary>
/// Checks running industry jobs against the rigs configured on the Indy Parks
/// structure their facility is linked to.
///
/// ESI exposes no structure-fitting endpoint, so a structure's rigs can only be known
/// because the user described them in Indy Parks and linked that entry to a real
/// facility. Anything unlinked is <see cref="FacilityRigVerdict.Unknown"/> — reporting
/// it as unrigged would be inventing a finding out of missing configuration.
/// </summary>
public class IndyFacilityCheckService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// Verdict per job, keyed by JobId. Only jobs in <paramref name="jobIds"/> are
    /// considered; pass the ones on screen rather than the whole history.
    /// </summary>
    public async Task<Dictionary<int, FacilityRigResult>> CheckAsync(
        IReadOnlyCollection<int> jobIds, CancellationToken ct = default)
    {
        var results = new Dictionary<int, FacilityRigResult>();
        if (jobIds.Count == 0) return results;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Park structures that name a real facility. Without at least one link there
        // is nothing to check against, so bail before doing any further work.
        var linked = await db.IndyStructures.AsNoTracking()
            .Where(s => s.RealStructureId != null)
            .Select(s => new { s.Id, s.DisplayName, s.RealStructureId, s.StructureTypeKey })
            .ToListAsync(ct);
        if (linked.Count == 0) return results;

        var byFacility = linked
            .GroupBy(s => s.RealStructureId!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var jobs = await db.EsiIndustryJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.JobId))
            .Select(j => new { j.JobId, j.FacilityId, j.ActivityId, j.ProductTypeId, j.BlueprintTypeId })
            .ToListAsync(ct);

        var structIds = linked.Select(s => s.Id).ToList();
        var rigs = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => structIds.Contains(r.StructureId) && r.RigTypeId != 0)
            .Select(r => new { r.StructureId, r.RigTypeId })
            .ToListAsync(ct);

        var rigTypeIds = rigs.Select(r => r.RigTypeId).Distinct().ToList();
        var rigNames = rigTypeIds.Count > 0
            ? await db.SdeTypes.AsNoTracking()
                .Where(t => rigTypeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct)
            : [];

        var rigCategoryByStructure = rigs
            .GroupBy(r => r.StructureId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => IndyRigMatching.RigCategoryFromName(rigNames.GetValueOrDefault(r.RigTypeId, "")))
                      .Where(c => c.Length > 0)
                      .Distinct()
                      .ToList());

        // The product decides the category. Fall back to the blueprint when ESI didn't
        // record a product type, which happens on some older job rows.
        var productIds = jobs
            .Select(j => j.ProductTypeId ?? j.BlueprintTypeId)
            .Distinct()
            .ToList();

        var typeToGroup = await db.SdeTypes.AsNoTracking()
            .Where(t => productIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.GroupId, ct);

        var groupIds = typeToGroup.Values.Distinct().ToList();
        var groupInfo = await db.SdeGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.GroupId))
            .ToDictionaryAsync(
                g => g.GroupId,
                g => new IndyRigMatching.GroupInfo(g.GroupId, g.CategoryId, g.Name), ct);

        foreach (var j in jobs)
        {
            if (!IndyRigMatching.IsRigCheckable(j.ActivityId))
            {
                results[j.JobId] = new FacilityRigResult(j.JobId, FacilityRigVerdict.NotApplicable, "");
                continue;
            }

            if (!byFacility.TryGetValue(j.FacilityId, out var park))
            {
                results[j.JobId] = new FacilityRigResult(j.JobId, FacilityRigVerdict.Unknown, "");
                continue;
            }

            var productId  = j.ProductTypeId ?? j.BlueprintTypeId;
            var isReaction = j.ActivityId == IndyRigMatching.Activity.Reactions;
            var itemCat    = IndyRigMatching.ItemCategoryKey(productId, isReaction, typeToGroup, groupInfo);

            if (itemCat.Length == 0)
            {
                // Nothing rig-bonusable about this product — silence, not a finding.
                results[j.JobId] = new FacilityRigResult(j.JobId, FacilityRigVerdict.NotApplicable, "");
                continue;
            }

            var fitted  = rigCategoryByStructure.GetValueOrDefault(park.Id, []);
            var applies = fitted.Any(c => IndyRigMatching.RigApplies(c, itemCat));

            results[j.JobId] = applies
                ? new FacilityRigResult(j.JobId, FacilityRigVerdict.Rigged, "")
                : new FacilityRigResult(j.JobId, FacilityRigVerdict.NotRigged,
                    fitted.Count == 0
                        ? $"{park.DisplayName} has no industry rigs fitted"
                        : $"{park.DisplayName} is not rigged for {Pretty(itemCat)}");
        }

        return results;
    }

    /// <summary>Count of running jobs whose facility gives them no rig bonus.</summary>
    public async Task<int> CountUnriggedRunningAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var runningIds = await db.EsiIndustryJobs.AsNoTracking()
            .Where(j => j.Status == "active")
            .Select(j => j.JobId)
            .ToListAsync(ct);

        if (runningIds.Count == 0) return 0;

        var checks = await CheckAsync(runningIds, ct);
        return checks.Values.Count(v => v.Verdict == FacilityRigVerdict.NotRigged);
    }

    /// <summary>Category keys are internal identifiers; jobs are read by people.</summary>
    private static string Pretty(string categoryKey) => categoryKey switch
    {
        "small_ships"           => "small ships",
        "medium_ships"          => "medium ships",
        "large_ships"           => "large ships",
        "adv_small_ships"       => "advanced small ships",
        "adv_medium_ships"      => "advanced medium ships",
        "adv_large_ships"       => "advanced large ships",
        "capital_ships"         => "capital ships",
        "drones_fighters"       => "drones and fighters",
        "modules_equipment"     => "equipment",
        "ammo_charges"          => "ammunition",
        "capital_components"    => "capital components",
        "adv_components"        => "advanced components",
        "structure_ammo"        => "structures and fuel",
        "react_bio_gas"         => "gas reactions",
        "react_biochemical"     => "moon reactions",
        "react_composite"       => "composite reactions",
        "biochemical_reactions" => "reactions",
        _                       => categoryKey.Replace('_', ' '),
    };
}
