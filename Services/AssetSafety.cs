using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Which owned items are sitting in an asset safety wrap, and therefore are not usable stock.
///
/// <para>The flag only lands on the top of the wrap. A container inside it carries an ordinary
/// hangar flag, and so does everything inside that container — so a plain
/// <c>LocationFlag != 'AssetSafety'</c> test misses nearly all of it. In this player's data 317
/// items carry the flag and over two thousand more sit beneath them.</para>
///
/// <para>Counting any of it as available is the expensive kind of wrong: a wrap has to be
/// unpacked at a station of the game's choosing and hauled back, so material in one cannot fill
/// a job this week, and treating it as present suppresses the purchase that would.</para>
/// </summary>
public static class AssetSafety
{
    private const string Flag = "AssetSafety";

    /// <summary>
    /// Every asset item id inside a wrap, following the container chain down from each wrap.
    ///
    /// <para>Walks downward rather than upward because the answer is needed for whole queries at
    /// once — one pass per level of nesting, rather than a chain walk per item. Real nesting is
    /// two or three deep; the iteration cap only stops a cycle in malformed data from spinning.</para>
    /// </summary>
    public static async Task<HashSet<long>> WrappedItemIdsAsync(
        AppDbContext db, CancellationToken ct = default)
    {
        var wrapped = (await db.EsiAssets.AsNoTracking()
                .Where(a => a.LocationFlag == Flag)
                .Select(a => a.ItemId)
                .ToListAsync(ct))
            .ToHashSet();
        if (wrapped.Count == 0) return wrapped;

        var frontier = wrapped.ToList();

        for (var depth = 0; depth < 16 && frontier.Count > 0; depth++)
        {
            var children = await db.EsiAssets.AsNoTracking()
                .Where(a => frontier.Contains(a.LocationId))
                .Select(a => a.ItemId)
                .ToListAsync(ct);

            frontier = children.Where(wrapped.Add).ToList();
        }

        return wrapped;
    }
}
