using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Which slot pool an activity draws on. The three pools are independent in game.</summary>
public enum IndustryPool { Manufacturing, Reaction, Science }

/// <summary>
/// One character the worklist may hand a job to, with everything needed to decide whether it
/// should be them.
/// </summary>
public sealed class IndustryCandidate
{
    public required WorklistIndyChar Config { get; init; }

    /// <summary>
    /// The corporation this character is actually in.
    ///
    /// <para>Needed because "include corp assets" is not the same as "include every corporation's
    /// assets". A main sitting in a large alliance corp gives the app visibility of that corp's
    /// hangars, and none of it is the player's to build with or move.</para>
    /// </summary>
    public required long CorporationId { get; init; }

    /// <summary>Skill id to active level. Blueprint requirements are checked against this.</summary>
    public required Dictionary<int, int> Skills { get; init; }

    /// <summary>Free slots per pool: capacity from skills, less jobs currently occupying it.</summary>
    public required Dictionary<IndustryPool, int> FreeSlots { get; init; }
    public required Dictionary<IndustryPool, int> Capacity  { get; init; }

    /// <summary>
    /// How much industry capability this character represents, as the sum of their levels in
    /// every skill any blueprint gates on.
    ///
    /// Used to spend the least scarce character first. The alt who can build titans should not
    /// be filled with capital armour plates that anyone could have made, because their capacity
    /// is the one thing that cannot be substituted.
    /// </summary>
    public required int Capability { get; init; }

    public bool Runs(IndustryPool pool) => pool switch
    {
        IndustryPool.Manufacturing => Config.Manufacturing,
        IndustryPool.Reaction      => Config.Reactions,
        _                          => Config.Science,
    };

    public bool MeetsSkills(IReadOnlyList<SdeBlueprintSkill> required) =>
        required.All(r => Skills.GetValueOrDefault(r.SkillTypeId) >= r.Level);
}

/// <summary>
/// Works out who can run which job, and picks between them.
///
/// <para><b>Deterministic by construction.</b> Given the same characters, skills, running jobs
/// and demand, this produces the same assignments every time. Every ordering it depends on is
/// total: candidates sort by capability, then free slots, then character id; demand is walked in
/// a fixed order by the caller. Nothing consults a clock or a hash. That matters because the
/// worklist is regenerated on every refresh — assignments that shuffled between runs would make
/// the list unreadable, and would undermine the item keys, which deliberately exclude the
/// assigned character so that snooze and age survive a reassignment.</para>
/// </summary>
public class IndustryAssignmentService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Each level of these adds one slot to its pool, on top of the one every character has.
    private const int MassProduction         = 3387;
    private const int AdvancedMassProduction = 24625;
    private const int MassReactions          = 45748;
    private const int AdvancedMassReactions  = 45749;
    private const int LaboratoryOperation    = 3406;
    private const int AdvancedLabOperation   = 24624;

    /// <summary>ESI activity ids, mapped to the slot pool they consume.</summary>
    public static IndustryPool PoolOf(int activityId) => activityId switch
    {
        1          => IndustryPool.Manufacturing,
        9 or 11    => IndustryPool.Reaction,
        _          => IndustryPool.Science,   // research, copying, invention
    };

    /// <summary>SDE activity names, as used by SdeBlueprintSkill/Product.</summary>
    public static string ActivityName(IndustryPool pool) => pool switch
    {
        IndustryPool.Manufacturing => "manufacturing",
        IndustryPool.Reaction      => "reaction",
        _                          => "copying",
    };

    /// <summary>
    /// Corporations whose hangars may be counted, or null when every one of them may.
    ///
    /// <para>Personal corporations only by default — the ones already flagged as the player's on
    /// the Corporations tab. Authorising a main in a large alliance corp hands the app that
    /// corp's whole hangar, and treating it as material makes every shortfall look filled.</para>
    /// </summary>
    public async Task<HashSet<long>?> UsableCorporationsAsync(
        bool includeNonPersonal, CancellationToken ct = default)
    {
        if (includeNonPersonal) return null;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Corporations.AsNoTracking()
                .Where(c => c.IsPersonal)
                .Select(c => (long)c.Id)
                .ToListAsync(ct))
            .ToHashSet();
    }

    public async Task<List<IndustryCandidate>> LoadCandidatesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var configs = await db.WorklistIndyChars.AsNoTracking().ToListAsync(ct);
        if (configs.Count == 0) return [];

        var charIds = configs.Select(c => c.CharacterId).ToList();

        var corpOf = await db.Characters.AsNoTracking()
            .Where(c => charIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => (long)c.CorporationId, ct);

        var skills = (await db.EsiSkills.AsNoTracking()
                .Where(s => charIds.Contains(s.CharacterId))
                .Select(s => new { s.CharacterId, s.SkillId, s.ActiveSkillLevel })
                .ToListAsync(ct))
            .GroupBy(s => s.CharacterId)
            .ToDictionary(g => g.Key,
                          g => g.ToDictionary(s => s.SkillId, s => s.ActiveSkillLevel));

        // Only skills some blueprint actually gates on count toward capability — total SP would
        // rank a combat pilot above a dedicated industrialist.
        var gatingSkills = (await db.SdeBlueprintSkills.AsNoTracking()
            .Select(s => s.SkillTypeId).Distinct().ToListAsync(ct)).ToHashSet();

        // Jobs occupying a slot right now. "delivered" is finished and collected; everything
        // else in the live set still holds its slot, including "ready" — the output is waiting
        // to be picked up and the slot is not free until it is.
        var running = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Status == "active" || j.Status == "paused" || j.Status == "ready")
                .Select(j => new { j.InstallerId, j.ActivityId })
                .ToListAsync(ct))
            .GroupBy(j => (j.InstallerId, Pool: PoolOf(j.ActivityId)))
            .ToDictionary(g => g.Key, g => g.Count());

        var candidates = new List<IndustryCandidate>(configs.Count);

        foreach (var cfg in configs)
        {
            var mine = skills.GetValueOrDefault(cfg.CharacterId) ?? [];

            var capacity = new Dictionary<IndustryPool, int>
            {
                [IndustryPool.Manufacturing] = 1 + mine.GetValueOrDefault(MassProduction)
                                                 + mine.GetValueOrDefault(AdvancedMassProduction),
                [IndustryPool.Reaction]      = 1 + mine.GetValueOrDefault(MassReactions)
                                                 + mine.GetValueOrDefault(AdvancedMassReactions),
                [IndustryPool.Science]       = 1 + mine.GetValueOrDefault(LaboratoryOperation)
                                                 + mine.GetValueOrDefault(AdvancedLabOperation),
            };

            var free = capacity.ToDictionary(
                kv => kv.Key,
                kv => Math.Max(0, kv.Value - running.GetValueOrDefault((cfg.CharacterId, kv.Key))));

            candidates.Add(new IndustryCandidate
            {
                Config        = cfg,
                CorporationId = corpOf.GetValueOrDefault(cfg.CharacterId),
                Skills     = mine,
                Capacity   = capacity,
                FreeSlots  = free,
                Capability = mine.Where(s => gatingSkills.Contains(s.Key)).Sum(s => s.Value),
            });
        }

        return candidates;
    }

    /// <summary>
    /// Candidates able to run one job, cheapest capability first.
    ///
    /// The ordering is the whole point: least capable first spends the substitutable characters
    /// before the scarce ones. Ties break on free slots and then character id, so the sequence is
    /// total and the same every run.
    /// </summary>
    public static List<IndustryCandidate> EligibleFor(
        IReadOnlyList<IndustryCandidate> candidates, IndustryPool pool,
        IReadOnlyList<SdeBlueprintSkill> required) =>
        candidates
            .Where(c => c.Runs(pool) && c.MeetsSkills(required))
            .OrderBy(c => c.Capability)
            .ThenByDescending(c => c.FreeSlots.GetValueOrDefault(pool))
            .ThenBy(c => c.Config.CharacterId)
            .ToList();
}
