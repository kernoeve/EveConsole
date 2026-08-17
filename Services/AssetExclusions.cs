using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Owned items that are not usable stock, and so must not be counted, hauled or bought against.
///
/// <para>Two kinds, both of which hide beneath a container chain rather than being flagged
/// item by item — which is why a naive filter misses nearly all of them.</para>
/// </summary>
public static class AssetExclusions
{
    private const string AssetSafetyFlag = "AssetSafety";

    /// <summary>Category 6 — every ship hull in the game.</summary>
    private const int ShipCategory = 6;

    /// <summary>
    /// Everything inside an asset safety wrap or inside a ship.
    ///
    /// <para><b>Asset safety.</b> The flag lands only on the top of the wrap; a container inside
    /// it carries an ordinary hangar flag and so does everything inside that. In this player's
    /// data 317 items carry the flag and over two thousand more sit beneath them. A wrap has to
    /// be unpacked at a station of the game's choosing and hauled back, so none of it can fill a
    /// job this week.</para>
    ///
    /// <para><b>Ship contents.</b> Cargo holds, ship maintenance bays, fuel bays, drone bays and
    /// fitted modules are all just items whose parent happens to be a hull. They are packed for a
    /// purpose, and counting them as available stock would have the tool propose hauling the fuel
    /// out of the ship that is about to burn it.</para>
    /// </summary>
    public static async Task<HashSet<long>> UnusableItemIdsAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        var roots = (await db.EsiAssets.AsNoTracking()
                .Where(a => a.LocationFlag == AssetSafetyFlag
                            || db.SdeTypes.Any(t => t.TypeId == a.TypeId
                                 && db.SdeGroups.Any(g => g.GroupId == t.GroupId
                                      && g.CategoryId == ShipCategory)))
                .Select(a => a.ItemId)
                .ToListAsync(ct))
            .ToHashSet();
        if (roots.Count == 0) return [];

        // The hulls and wraps themselves stay usable — a ship sitting in a hangar is a real asset
        // and may even be the thing being built. Only what is inside them is excluded.
        var excluded = new HashSet<long>();
        var frontier = roots.ToList();

        // Downward, one pass per level of nesting, rather than a chain walk per item. Real
        // nesting is a few deep; the cap only stops malformed data from spinning.
        for (var depth = 0; depth < 16 && frontier.Count > 0; depth++)
        {
            var children = await db.EsiAssets.AsNoTracking()
                .Where(a => frontier.Contains(a.LocationId))
                .Select(a => a.ItemId)
                .ToListAsync(ct);

            frontier = children.Where(excluded.Add).ToList();
        }

        return excluded;
    }
}
