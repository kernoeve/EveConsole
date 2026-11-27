using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// The skills a character has now: what ESI reports, with every level their queue has finished
/// since counted as trained. Everything that plans or judges by a pilot's skills reads them here.
///
/// <para>⚠️ ESI does not record a finished skill until the character next logs in. Until then the
/// skills list keeps the old level and the finished entry stays at the head of the queue, for as
/// long as the character stays logged out. Kerno Rutledge's Advanced Mass Reactions V finished on
/// 17 November 2026 and was still level IV ten days later, so the worklist planned his reactions
/// with ten slots instead of eleven. The queue's finish time is EVE's own: the level is his from
/// then, and logging in only makes ESI say so.</para>
///
/// <para>The tables keep what ESI said. This adds the finished levels when they are read, so a
/// level counts from its finish time rather than from the next poll after it.</para>
/// </summary>
public static class SkillLevels
{
    /// <summary>
    /// Active levels by skill for each character asked for. A skill the character does not have is
    /// absent; one injected but not yet trained is there at 0, as ESI lists it.
    /// </summary>
    /// <param name="skillIds">Only these skills, or every one when null.</param>
    public static async Task<Dictionary<long, Dictionary<int, int>>> LoadAsync(
        AppDbContext db, IReadOnlyCollection<long> characterIds,
        IReadOnlyCollection<int>? skillIds = null, CancellationToken ct = default)
    {
        var ids = characterIds.Distinct().ToList();

        var skills = db.EsiSkills.AsNoTracking().Where(s => ids.Contains(s.CharacterId));
        var queue  = db.EsiSkillQueue.AsNoTracking().Where(q => ids.Contains(q.CharacterId));
        if (skillIds is not null)
        {
            var wanted = skillIds.Distinct().ToList();
            skills = skills.Where(s => wanted.Contains(s.SkillId));
            queue  = queue.Where(q => wanted.Contains(q.SkillId));
        }

        var settled = Settle(await skills.ToListAsync(ct), await queue.ToListAsync(ct), DateTimeOffset.UtcNow);

        var result = ids.ToDictionary(id => id, _ => new Dictionary<int, int>());
        foreach (var s in settled) result[s.CharacterId][s.SkillId] = s.ActiveSkillLevel;
        return result;
    }

    /// <summary>One character's levels by skill.</summary>
    public static async Task<Dictionary<int, int>> LoadAsync(
        AppDbContext db, long characterId, CancellationToken ct = default)
        => (await LoadAsync(db, [characterId], null, ct))[characterId];

    /// <summary>
    /// The skills as they stand at <paramref name="now"/>: copies of <paramref name="skills"/> with each
    /// level <paramref name="queue"/> finished by then trained, its points at that level's end. A
    /// skill trained from nothing while logged out gets a row of its own.
    ///
    /// <para>The active level rises with the trained one only when the two were equal. A lower
    /// active level is a clone state's cap, which training past it does not lift.</para>
    /// </summary>
    public static List<StoredSkill> Settle(
        IEnumerable<StoredSkill> skills, IEnumerable<StoredSkillQueueEntry> queue, DateTimeOffset now)
    {
        var byKey = skills.ToDictionary(
            s => (s.CharacterId, s.SkillId),
            s => new StoredSkill
            {
                CharacterId        = s.CharacterId,
                SkillId            = s.SkillId,
                TrainedSkillLevel  = s.TrainedSkillLevel,
                ActiveSkillLevel   = s.ActiveSkillLevel,
                SkillpointsInSkill = s.SkillpointsInSkill,
            });

        // ⚠️ The date test in memory: a DateTimeOffset in a Where does not translate on SQLite.
        foreach (var q in queue.Where(q => q.FinishDate is { } f && f <= now).OrderBy(q => q.FinishedLevel))
        {
            if (!byKey.TryGetValue((q.CharacterId, q.SkillId), out var s))
                byKey[(q.CharacterId, q.SkillId)] = s = new StoredSkill { CharacterId = q.CharacterId, SkillId = q.SkillId };
            if (q.FinishedLevel <= s.TrainedSkillLevel) continue;

            if (s.ActiveSkillLevel == s.TrainedSkillLevel) s.ActiveSkillLevel = q.FinishedLevel;
            s.TrainedSkillLevel  = q.FinishedLevel;
            s.SkillpointsInSkill = Math.Max(s.SkillpointsInSkill, q.LevelEndSp);
        }

        return [.. byKey.Values];
    }
}
