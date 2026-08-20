using EveConsole.Controls;
using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>A module that can go in a slot.</summary>
public sealed record FittingOption(int TypeId, string Name, string GroupName)
{
    public override string ToString() => Name;
}

/// <summary>
/// What may be fitted where.
///
/// <para>⚠️ Driven by dogma EFFECTS, not by group names. A module declares the slot it occupies
/// through hiPower / medPower / loPower / rigSlot / serviceSlot, which is how the game decides and
/// is therefore the only answer that cannot drift. Matching on group names looked workable — the
/// structure groups are tidily named — but it would need a hand-maintained list that silently rots
/// every time CCP adds a group, and it cannot express a module that fits more than one slot.</para>
///
/// <para>Nothing here is specific to structures. Ships declare the same effects, so pointing this
/// at a hull needs only a different category filter.</para>
/// </summary>
public class FittingOptionService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Effect ids, verified against the SDE rather than assumed.
    private const int EffHiPower     = 12;
    private const int EffLoPower     = 11;
    private const int EffMedPower    = 13;
    private const int EffRigSlot     = 2663;
    private const int EffServiceSlot = 6306;
    private const int EffSubSystem   = 3772;

    /// <summary>Module category. Structure modules and rigs all live here.</summary>
    private const int StructureModuleCategory = 66;

    /// <summary>rigSize on both the rig and the hull it fits — a medium rig only goes on a hull
    /// that declares the same size, which is what keeps a Sotiyo rig off an Athanor.</summary>
    private const int AttrRigSize = 1547;

    private static int EffectFor(FittingBand band) => band switch
    {
        FittingBand.High      => EffHiPower,
        FittingBand.Mid       => EffMedPower,
        FittingBand.Low       => EffLoPower,
        FittingBand.Rig       => EffRigSlot,
        FittingBand.Service   => EffServiceSlot,
        FittingBand.Subsystem => EffSubSystem,
        _                     => 0,
    };

    /// <summary>
    /// Everything fittable in one band of one hull, by name.
    ///
    /// <para>Rigs are additionally filtered to the hull's own rig size; without that the list is
    /// nearly three hundred entries of which most cannot be fitted.</para>
    /// </summary>
    public async Task<List<FittingOption>> OptionsAsync(
        FittingBand band, int hullTypeId, CancellationToken ct = default)
    {
        var effect = EffectFor(band);
        if (effect == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var query =
            from te in db.SdeTypeDogmaEffects.AsNoTracking()
            join t  in db.SdeTypes.AsNoTracking()  on te.TypeId  equals t.TypeId
            join g  in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
            where te.EffectId == effect
                  && t.Published
                  && g.CategoryId == StructureModuleCategory
            select new { t.TypeId, t.Name, GroupName = g.Name };

        var options = await query.ToListAsync(ct);

        if (band == FittingBand.Rig)
        {
            var hullSize = await db.SdeTypeDogmaAttributes.AsNoTracking()
                .Where(a => a.TypeId == hullTypeId && a.AttributeId == AttrRigSize)
                .Select(a => (double?)a.Value)
                .FirstOrDefaultAsync(ct);

            if (hullSize is { } size)
            {
                var ids = options.Select(o => o.TypeId).ToList();

                var rigSizes = await db.SdeTypeDogmaAttributes.AsNoTracking()
                    .Where(a => a.AttributeId == AttrRigSize && ids.Contains(a.TypeId))
                    .ToDictionaryAsync(a => a.TypeId, a => a.Value, ct);

                options = options
                    .Where(o => rigSizes.TryGetValue(o.TypeId, out var rs) && Math.Abs(rs - size) < 0.01)
                    .ToList();
            }
        }

        return options
            .Select(o => new FittingOption(o.TypeId, o.Name, o.GroupName))
            .OrderBy(o => o.GroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
