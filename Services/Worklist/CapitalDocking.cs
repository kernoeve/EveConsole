using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Which capital hulls can get where.
///
/// <para>A capital cannot go in anybody's cargo: a pilot flies it, and jumps it between systems. And
/// it can only arrive somewhere it can dock. A standard capital docks at an NPC station, a Fortizar,
/// a Sotiyo or a Keepstar, and a supercarrier or titan at a Keepstar alone. So a T2 capital built in
/// an Azbel needs its T1 hull built in that same Azbel and never undocked: a Chimera elsewhere is no
/// use to it, and a trip to bring one there cannot be flown.</para>
///
/// <para>Freighters, Orcas and Bowheads dock anywhere, and subcapitals fit in cargo besides, so
/// none of them are hulls here.</para>
/// </summary>
public sealed class CapitalDocking(
    IReadOnlySet<int> capitals, IReadOnlySet<int> supers, IReadOnlyDictionary<long, int> structureTypes)
{
    private static readonly int[] CapitalGroups =
    [
        485,    // Dreadnought
        547,    // Carrier
        883,    // Capital Industrial Ship
        1538,   // Force Auxiliary
        4594,   // Lancer Dreadnought
        5120,   // Command Carrier
    ];

    private static readonly int[] SuperGroups = [IndustryMe.TitanGroupId, IndustryMe.SuperGroupId];

    private static readonly HashSet<int> Keepstars = [IndustryMe.KeepstarTypeId, 40340];

    private static readonly HashSet<int> TakesCapitals =
    [
        .. Keepstars,
        35827,                                              // Sotiyo
        IndustryMe.FortizarTypeId, 47512, 47513, 47514, 47515, 47516,    // Fortizar and its faction ones
    ];

    public static async Task<CapitalDocking> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var hulls = await db.SdeTypes.AsNoTracking()
            .Where(t => CapitalGroups.Contains(t.GroupId) || SuperGroups.Contains(t.GroupId))
            .Select(t => new { t.TypeId, t.GroupId })
            .ToListAsync(ct);

        var structures = await db.EsiStructureNames.AsNoTracking()
            .Where(s => s.TypeId != 0)
            .ToDictionaryAsync(s => s.StructureId, s => s.TypeId, ct);

        return new CapitalDocking(
            hulls.Where(h => CapitalGroups.Contains(h.GroupId)).Select(h => h.TypeId).ToHashSet(),
            hulls.Where(h => SuperGroups.Contains(h.GroupId)).Select(h => h.TypeId).ToHashSet(),
            structures);
    }

    public IEnumerable<int> Hulls => capitals.Concat(supers);

    /// <summary>A hull a pilot has to fly, rather than cargo.</summary>
    public bool IsHull(int typeId) => capitals.Contains(typeId) || supers.Contains(typeId);

    /// <summary>
    /// Whether this can arrive at that place. Anything that is not a capital hull can.
    ///
    /// <para>⚠️ A structure of unknown type counts as no: a hull sent there may have nowhere to dock
    /// when it arrives, while one not sent is only a trip the list did not suggest.</para>
    /// </summary>
    public bool CanDock(int typeId, long place)
    {
        if (!IsHull(typeId)) return true;

        var super = supers.Contains(typeId);
        if (place is >= 60_000_000 and < 64_100_000) return !super;   // an NPC station

        return structureTypes.TryGetValue(place, out var type)
            && (super ? Keepstars.Contains(type) : TakesCapitals.Contains(type));
    }
}
