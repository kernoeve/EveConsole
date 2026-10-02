using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Pi;

/// <summary>
/// Which characters do Planetary Industry: the one place that answers it, for the PI tool, its
/// alerts, its worklist tasks and the layout poll alike.
///
/// <para>Every authorised character, less those whose PI box on Settings → Characters is clear.
/// ⚠️ Absence means on: a character with no row in WorklistIndyChars has never been configured,
/// and is in — exactly as the skill-queue box is read. Only a row saying false takes one out.</para>
/// </summary>
public static class PiCharacters
{
    /// <summary>The ids of every character that does PI.</summary>
    public static async Task<HashSet<long>> IdsAsync(AppDbContext db, CancellationToken ct = default)
    {
        var off = await OffAsync(db, ct);
        var all = await db.Characters.AsNoTracking()
            .Where(c => c.RefreshToken != "")
            .Select(c => c.Id)
            .ToListAsync(ct);
        return all.Where(id => !off.Contains(id)).ToHashSet();
    }

    /// <summary>Whether one character does PI.</summary>
    public static async Task<bool> IsOnAsync(AppDbContext db, long characterId, CancellationToken ct = default)
        => !await db.WorklistIndyChars.AsNoTracking()
            .AnyAsync(c => c.CharacterId == characterId && !c.PlanetaryIndustry, ct);

    /// <summary>The characters whose PI box has been cleared.</summary>
    public static async Task<HashSet<long>> OffAsync(AppDbContext db, CancellationToken ct = default)
        => (await db.WorklistIndyChars.AsNoTracking()
                .Where(c => !c.PlanetaryIndustry)
                .Select(c => c.CharacterId)
                .ToListAsync(ct))
            .ToHashSet();
}

/// <summary>
/// The two skills that bound a character's PI. Colonies allowed: 1 + Interplanetary
/// Consolidation, at most 6. Highest command center upgrade: the Command Center Upgrades level.
/// Read from the trained-and-active level, which is what an Alpha clone can actually use.
/// </summary>
public sealed record PiSkills(long CharacterId, int InterplanetaryConsolidation, int CommandCenterUpgrades)
{
    public const int InterplanetaryConsolidationId = 2495;
    public const int CommandCenterUpgradesId       = 2505;
    public const int MaxColonies                   = 6;

    public int ColoniesAllowed  => Math.Min(MaxColonies, 1 + InterplanetaryConsolidation);
    public int MaxUpgradeLevel  => Math.Clamp(CommandCenterUpgrades, 0, PiCommandCenter.MaxLevel);

    public static async Task<Dictionary<long, PiSkills>> LoadAsync(AppDbContext db, IReadOnlyCollection<long> characterIds,
                                                                  CancellationToken ct = default)
    {
        var ids  = characterIds.ToList();
        var rows = await db.EsiSkills.AsNoTracking()
            .Where(s => ids.Contains(s.CharacterId)
                     && (s.SkillId == InterplanetaryConsolidationId || s.SkillId == CommandCenterUpgradesId))
            .Select(s => new { s.CharacterId, s.SkillId, s.ActiveSkillLevel })
            .ToListAsync(ct);

        return ids.ToDictionary(id => id, id => new PiSkills(
            id,
            rows.FirstOrDefault(r => r.CharacterId == id && r.SkillId == InterplanetaryConsolidationId)?.ActiveSkillLevel ?? 0,
            rows.FirstOrDefault(r => r.CharacterId == id && r.SkillId == CommandCenterUpgradesId)?.ActiveSkillLevel ?? 0));
    }
}
