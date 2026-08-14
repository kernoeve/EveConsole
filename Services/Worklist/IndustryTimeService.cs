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

    // Industry cuts manufacturing time by 4% a level; Advanced Industry cuts both manufacturing
    // and reaction time by 3% a level. Reactions have no time-efficiency research, so a reaction
    // formula's TE is always zero and the term drops out on its own.
    private const int SkillIndustry         = 3380;
    private const int SkillAdvancedIndustry = 3388;

    public sealed class TimeContext
    {
        public required Dictionary<(int TypeId, string Activity), int> BaseSeconds { get; init; }
        public required Dictionary<int, double> MfgRigTime     { get; init; }
        public required Dictionary<int, double> RxnRigTime     { get; init; }
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
                .Where(a => a.Activity == "manufacturing" || a.Activity == "reaction")
                .Select(a => new { a.TypeId, a.Activity, a.Time })
                .ToListAsync(ct))
            .GroupBy(a => (a.TypeId, a.Activity))
            .ToDictionary(g => g.Key, g => g.First().Time);

        var attrs = await db.SdeTypeDogmaAttributes.AsNoTracking()
            .Where(a => a.AttributeId == AttrMfgRigTime     || a.AttributeId == AttrRxnRigTime
                     || a.AttributeId == AttrRigLowsecMult  || a.AttributeId == AttrRigNullsecMult
                     || a.AttributeId == AttrStructMfgTime  || a.AttributeId == AttrStructRxnTime)
            .Select(a => new { a.TypeId, a.AttributeId, a.Value })
            .ToListAsync(ct);

        var mfgRig = new Dictionary<int, double>();
        var rxnRig = new Dictionary<int, double>();
        var lowMul = new Dictionary<int, double>();
        var nulMul = new Dictionary<int, double>();
        var structTypeIds = new List<int>();

        foreach (var a in attrs)
        {
            switch (a.AttributeId)
            {
                case AttrMfgRigTime:     mfgRig[a.TypeId] = Math.Abs(a.Value) / 100.0; break;
                case AttrRxnRigTime:     rxnRig[a.TypeId] = Math.Abs(a.Value) / 100.0; break;
                case AttrRigLowsecMult:  lowMul[a.TypeId] = a.Value; break;
                case AttrRigNullsecMult: nulMul[a.TypeId] = a.Value; break;
                default:                 structTypeIds.Add(a.TypeId); break;
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
            if (a.AttributeId == AttrStructMfgTime) structMfg[key] = a.Value;
            else                                    structRxn[key] = a.Value;
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

        var advIndustry = Math.Clamp(skills.GetValueOrDefault(SkillAdvancedIndustry), 0, 5);
        var industry    = Math.Clamp(skills.GetValueOrDefault(SkillIndustry), 0, 5);
        var skillFactor = (1.0 - 0.03 * advIndustry)
                        * (isReaction ? 1.0 : 1.0 - 0.04 * industry);

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
