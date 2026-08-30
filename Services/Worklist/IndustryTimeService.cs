using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// How long one run of a job will take, in the facility it will actually run in, on the print it
/// will actually run from, for the character who will actually install it.
///
/// <para>Needed because the worklist caps job length: to know whether a shortfall is one job or
/// five, the length of a run has to be a real figure rather than the blueprint's unmodified base
/// time. The modifiers are large — a T2 component in a rigged nullsec Raitaru runs at about a
/// fifth of base — so using base time would split every build into roughly five times too many
/// jobs.</para>
///
/// <para>The chain is the game's:
/// <c>base × (1 − TE/100) × skills × structure role × (1 − rig bonus)</c>, with the rig bonus
/// scaled by the system's security class. Checked against live jobs in the player's own park: the
/// predicted factor matches the durations ESI reports to within rounding.</para>
///
/// <para>Time efficiency comes from the specific print, not a default. A TE0 copy and a TE20
/// original of the same blueprint differ by a fifth, which is the difference between four jobs
/// and five.</para>
/// </summary>
public class IndustryTimeService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Dogma attributes. Rig bonuses are stored negative (a reduction) and as percentages; the
    // structure role bonuses are stored as outright multipliers.
    private const int AttrMfgRigTime      = 2593;
    private const int AttrRxnRigTime      = 2713;
    private const int AttrRigLowsecMult   = 2356;
    private const int AttrRigNullsecMult  = 2357;
    private const int AttrStructMfgTime   = 2602;
    private const int AttrStructRxnTime   = 2721;

    /// <summary>
    /// A structure's role bonus to job time, as a percentage — the Tatara's second one.
    ///
    /// <para>⚠️ A reaction structure carries TWO time bonuses and the app read one. A Tatara has
    /// 2721 = 0.75 as a multiplier AND this at -20, and EVE applies both: 0.75 x 0.80 x the rig's
    /// 0.736 is 0.4416, which is exactly what 600+ real reaction jobs measure. On its own, 2721
    /// modelled a reaction 25% slower than the game charges.</para>
    ///
    /// <para>⚠️ Reactions only. It also sits on Fortizars, Azbels, Sotiyos and Keepstars, where
    /// measurement says it does NOT shorten a manufacturing job — see where it is folded in.</para>
    /// </summary>
    private const int AttrStructRoleTime  = 2749;

    // Science rig bonuses. Copying and invention are separately bonused, so a lab rigged for one
    // does nothing for the other and they cannot share a lookup.
    private const int AttrCopyRigTime      = 2780;
    private const int AttrInventionRigTime = 2781;

    // Industry cuts manufacturing time by 4% a level; Advanced Industry cuts both manufacturing
    // and reaction time by 3% a level. Reactions have no time-efficiency research, so a reaction
    // formula's TE is always zero and the term drops out on its own.
    private const int SkillIndustry         = 3380;
    private const int SkillAdvancedIndustry = 3388;

    /// <summary>
    /// Science cuts copying time by 5% a level — and copying only. Invention takes Advanced
    /// Industry and nothing else.
    ///
    /// <para>Both settled against the player's own completed jobs rather than from memory. Every
    /// invention job runs at exactly <c>base × 0.85 × 0.85 × 0.496</c> of its SDE time and every
    /// copy job at <c>× 0.75</c> of that again, across every blueprint and run count on record. The
    /// three factors are Advanced Industry V, the Raitaru's engineering role bonus, and a 24%
    /// laboratory rig doubled by nullsec; the extra quarter off copying is Science V, which those
    /// same characters all have.</para>
    /// </summary>
    private const int SkillScience = 3402;

    public const string CopyingActivity   = "copying";
    public const string InventionActivity = "invention";

    public sealed class TimeContext
    {
        public required Dictionary<(int TypeId, string Activity), int> BaseSeconds { get; init; }
        public required Dictionary<int, double> MfgRigTime     { get; init; }
        public required Dictionary<int, double> RxnRigTime     { get; init; }
        public required Dictionary<int, double> CopyRigTime      { get; init; }
        public required Dictionary<int, double> InventionRigTime { get; init; }
        public required Dictionary<int, double> RigLowsecMult  { get; init; }
        public required Dictionary<int, double> RigNullsecMult { get; init; }
        public required Dictionary<int, string> RigCategories  { get; init; }
        public required Dictionary<string, double> StructMfgTime { get; init; }
        public required Dictionary<string, double> StructRxnTime { get; init; }

        /// <summary>Rigs fitted to each park structure, by structure row id.</summary>
        public required Dictionary<int, List<int>> RigsByStructure { get; init; }

        /// <summary>True when no base time is known for this blueprint, so no split can be
        /// reasoned about and the caller should say so rather than invent a duration.</summary>
        public bool Knows(int bpTypeId, string activity) =>
            BaseSeconds.ContainsKey((bpTypeId, activity));
    }

    public async Task<TimeContext> LoadAsync(int parkId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var baseTimes = (await db.HoboBlueprintActivities.AsNoTracking()
                .Where(a => a.Activity == "manufacturing" || a.Activity == "reaction"
                         || a.Activity == CopyingActivity || a.Activity == InventionActivity)
                .Select(a => new { a.TypeId, a.Activity, a.Time })
                .ToListAsync(ct))
            .GroupBy(a => (a.TypeId, a.Activity))
            .ToDictionary(g => g.Key, g => g.First().Time);

        var attrs = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrMfgRigTime     || a.AttributeId == AttrRxnRigTime
                     || a.AttributeId == AttrRigLowsecMult  || a.AttributeId == AttrRigNullsecMult
                     || a.AttributeId == AttrCopyRigTime    || a.AttributeId == AttrInventionRigTime
                     || a.AttributeId == AttrStructMfgTime  || a.AttributeId == AttrStructRxnTime
                     || a.AttributeId == AttrStructRoleTime)
            .Select(a => new { a.TypeId, a.AttributeId, a.Value })
            .ToListAsync(ct);

        var mfgRig = new Dictionary<int, double>();
        var rxnRig = new Dictionary<int, double>();
        var cpyRig = new Dictionary<int, double>();
        var invRig = new Dictionary<int, double>();
        var lowMul = new Dictionary<int, double>();
        var nulMul = new Dictionary<int, double>();
        var roleTime = new Dictionary<int, double>();
        var structTypeIds = new List<int>();

        foreach (var a in attrs)
        {
            switch (a.AttributeId)
            {
                case AttrMfgRigTime:       mfgRig[a.TypeId] = Math.Abs(a.Value) / 100.0; break;
                case AttrRxnRigTime:       rxnRig[a.TypeId] = Math.Abs(a.Value) / 100.0; break;
                case AttrCopyRigTime:      cpyRig[a.TypeId] = Math.Abs(a.Value) / 100.0; break;
                case AttrInventionRigTime: invRig[a.TypeId] = Math.Abs(a.Value) / 100.0; break;
                case AttrRigLowsecMult:    lowMul[a.TypeId] = a.Value; break;
                case AttrRigNullsecMult:   nulMul[a.TypeId] = a.Value; break;
                case AttrStructRoleTime:   roleTime[a.TypeId] = 1.0 - Math.Abs(a.Value) / 100.0; break;
                default:                   structTypeIds.Add(a.TypeId); break;
            }
        }

        // Structure role bonuses are keyed by lowercased type name, which is what
        // IndyStructure.StructureTypeKey holds ("raitaru", "sotiyo", …).
        var structNames = await db.SdeTypes.AsNoTracking()
            .Where(t => structTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name.ToLowerInvariant(), ct);

        var structMfg = new Dictionary<string, double>();
        var structRxn = new Dictionary<string, double>();
        foreach (var a in attrs)
        {
            if (a.AttributeId != AttrStructMfgTime && a.AttributeId != AttrStructRxnTime) continue;
            if (!structNames.TryGetValue(a.TypeId, out var key)) continue;
            if (a.AttributeId == AttrStructMfgTime)
            {
                structMfg[key] = a.Value;
                continue;
            }

            // ⚠️ Both of a reaction structure's role bonuses, combined here so the one lookup
            // downstream carries the whole thing.
            //
            // 2749 is folded in ONLY where 2721 is present, which is what makes a structure a
            // reaction structure. Fortizars, Azbels, Sotiyos and Keepstars carry 2749 as well and
            // cannot run a reaction at all, so reading it on its own would invent a bonus for a
            // structure that never gets asked.
            //
            // ⚠️ Manufacturing does NOT get it, and that is measured rather than assumed.
            // Dividing blueprint TE and the skill factor out of real jobs leaves role x rig, and
            // all three cases land exactly on 2602 alone:
            //
            //     Raitaru, no applicable rig      0.85   = 2602
            //     Azbel + L-Set Cap Ship Mfg I    0.464  = 0.8 x 0.58   (-20% x 2.1 nullsec)
            //     Sotiyo + XL-Set Ship Mfg I      0.406  = 0.7 x 0.58
            //
            // Folding 2749 in would make those 0.64 x rig and 0.49 x rig, and neither then
            // divides into a whole rig bonus. Whatever 2749 does on a Fortizar or an Azbel, it
            // does not shorten a manufacturing job.
            structRxn[key] = a.Value * roleTime.GetValueOrDefault(a.TypeId, 1.0);
        }

        var rigs = await db.IndyStructureRigs.AsNoTracking()
            .Where(r => db.IndyStructures.Any(s => s.Id == r.StructureId && s.ParkId == parkId))
            .Select(r => new { r.StructureId, r.RigTypeId })
            .ToListAsync(ct);

        var rigTypeIds = rigs.Select(r => r.RigTypeId).Distinct().ToList();
        var rigNames = await db.SdeTypes.AsNoTracking()
            .Where(t => rigTypeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        return new TimeContext
        {
            BaseSeconds    = baseTimes,
            MfgRigTime     = mfgRig,
            RxnRigTime     = rxnRig,
            CopyRigTime      = cpyRig,
            InventionRigTime = invRig,
            RigLowsecMult  = lowMul,
            RigNullsecMult = nulMul,
            RigCategories  = rigNames.ToDictionary(
                                 kv => kv.Key, kv => IndyRigMatching.RigCategoryFromName(kv.Value)),
            StructMfgTime  = structMfg,
            StructRxnTime  = structRxn,
            RigsByStructure = rigs.Where(r => r.RigTypeId != 0)
                                  .GroupBy(r => r.StructureId)
                                  .ToDictionary(g => g.Key, g => g.Select(r => r.RigTypeId).ToList()),
        };
    }

    /// <summary>
    /// Seconds for one run. Null when the blueprint has no base time on record, which the caller
    /// must treat as "cannot be split" rather than as an instant job.
    /// </summary>
    public static double? PerRunSeconds(
        TimeContext     ctx,
        int             blueprintTypeId,
        bool            isReaction,
        int             timeEfficiency,
        IndyStructure?  structure,
        string          itemCategoryKey,
        IReadOnlyDictionary<int, int> skills)
    {
        var activity = isReaction ? "reaction" : "manufacturing";
        if (!ctx.BaseSeconds.TryGetValue((blueprintTypeId, activity), out var baseSeconds)
            || baseSeconds <= 0)
            return null;

        // Reaction formulas cannot be researched, so any TE on one is noise, not a bonus.
        var teFactor = isReaction ? 1.0 : 1.0 - Math.Clamp(timeEfficiency, 0, 20) / 100.0;

        // ⚠️ NEITHER industry skill touches a reaction. Advanced Industry was being applied to
        // one, and it is not: measured across 600+ real reaction jobs, the per-run time is
        // identical at Advanced Industry 0 and V, and at Industry 1, 3 and 5.
        //
        // It mattered beyond the 15%. EligibleFor deliberately ranks the LEAST capable character
        // first, and that character is the one the job is sized against — so enabling an
        // untrained alt for reactions silently shortened every reaction job in the plan.
        var advIndustry = Math.Clamp(skills.GetValueOrDefault(SkillAdvancedIndustry), 0, 5);
        var industry    = Math.Clamp(skills.GetValueOrDefault(SkillIndustry), 0, 5);
        var skillFactor = isReaction
            ? 1.0
            : (1.0 - 0.03 * advIndustry) * (1.0 - 0.04 * industry);

        var roleFactor = 1.0;
        if (structure is not null)
        {
            var key = structure.StructureTypeKey.ToLowerInvariant();
            var map = isReaction ? ctx.StructRxnTime : ctx.StructMfgTime;
            if (map.TryGetValue(key, out var role)) roleFactor = role;
        }

        var rigFactor = Math.Max(0.0, 1.0 - RigTimeBonus(ctx, structure, itemCategoryKey, isReaction));

        return baseSeconds * teFactor * skillFactor * roleFactor * rigFactor;
    }

    /// <summary>
    /// Seconds for one unit of science work: one copy-run for copying, one attempt for invention.
    /// Null when the blueprint has no base time for that activity on record.
    ///
    /// <para><b>Copying is charged per run per copy.</b> A job making two copies of thirty runs
    /// each costs sixty times the base, not two — which is exactly what the player's own copy jobs
    /// show, so the caller multiplies by both. Invention is charged per attempt and has no second
    /// dimension.</para>
    ///
    /// <para>The chain is the manufacturing one with the science terms swapped in, and the same
    /// engineering-complex role bonus: a Raitaru's 15% applies to a lab job as much as to a build.
    /// Verified exact against every invention and copy job in the player's history.</para>
    /// </summary>
    public static double? PerScienceUnitSeconds(
        TimeContext    ctx,
        int            blueprintTypeId,
        string         activity,
        IndyStructure? structure,
        string         itemCategoryKey,
        IReadOnlyDictionary<int, int> skills)
    {
        if (!ctx.BaseSeconds.TryGetValue((blueprintTypeId, activity), out var baseSeconds)
            || baseSeconds <= 0)
            return null;

        var advIndustry = Math.Clamp(skills.GetValueOrDefault(SkillAdvancedIndustry), 0, 5);
        var skillFactor = 1.0 - 0.03 * advIndustry;

        // Science shortens copying and nothing else. Applying it to invention as well would model
        // every invention job a quarter shorter than it runs.
        if (activity == CopyingActivity)
            skillFactor *= 1.0 - 0.05 * Math.Clamp(skills.GetValueOrDefault(SkillScience), 0, 5);

        var roleFactor = 1.0;
        if (structure is not null
            && ctx.StructMfgTime.TryGetValue(structure.StructureTypeKey.ToLowerInvariant(), out var role))
            roleFactor = role;

        var rigFactor = Math.Max(0.0, 1.0 - ScienceRigBonus(ctx, structure, itemCategoryKey, activity));

        return baseSeconds * skillFactor * roleFactor * rigFactor;
    }

    /// <summary>
    /// Laboratory rig reduction for this activity, scaled by security class exactly as the
    /// manufacturing rigs are. Copying and invention are bonused separately, so a rig that helps
    /// one may do nothing for the other.
    /// </summary>
    private static double ScienceRigBonus(
        TimeContext ctx, IndyStructure? structure, string itemCategoryKey, string activity)
    {
        if (structure is null || itemCategoryKey.Length == 0) return 0;
        if (!ctx.RigsByStructure.TryGetValue(structure.Id, out var fitted)) return 0;

        var bonusAttr = activity == CopyingActivity ? ctx.CopyRigTime : ctx.InventionRigTime;

        double total = 0;
        foreach (var rigTypeId in fitted)
        {
            if (!IndyRigMatching.RigApplies(
                    ctx.RigCategories.GetValueOrDefault(rigTypeId, ""), itemCategoryKey))
                continue;
            if (!bonusAttr.TryGetValue(rigTypeId, out var pct)) continue;

            total += pct * SecurityMultiplier(ctx, structure, rigTypeId);
        }
        return total;
    }

    /// <summary>
    /// Total time reduction from the rigs fitted to this structure that apply to this item, with
    /// each rig's percentage scaled by the security class it sits in — the same nullsec and
    /// lowsec multipliers the cost calculator uses, so the two cannot disagree about a facility.
    /// </summary>
    private static double RigTimeBonus(
        TimeContext ctx, IndyStructure? structure, string itemCategoryKey, bool isReaction)
    {
        if (structure is null || itemCategoryKey.Length == 0) return 0;
        if (!ctx.RigsByStructure.TryGetValue(structure.Id, out var fitted)) return 0;

        var bonusAttr = isReaction ? ctx.RxnRigTime : ctx.MfgRigTime;

        double total = 0;
        foreach (var rigTypeId in fitted)
        {
            if (!IndyRigMatching.RigApplies(
                    ctx.RigCategories.GetValueOrDefault(rigTypeId, ""), itemCategoryKey))
                continue;
            if (!bonusAttr.TryGetValue(rigTypeId, out var pct)) continue;

            total += pct * SecurityMultiplier(ctx, structure, rigTypeId);
        }
        return total;
    }

    private static double SecurityMultiplier(TimeContext ctx, IndyStructure s, int rigTypeId) =>
        s.SecurityClass switch
        {
            "lowsec"              => ctx.RigLowsecMult.TryGetValue(rigTypeId, out var l) ? l : 1.9,
            "nullsec" or "wormhole" => ctx.RigNullsecMult.TryGetValue(rigTypeId, out var n) ? n : 2.1,
            _                     => 1.0,
        };
}
