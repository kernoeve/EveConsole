using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>
/// Owned items that are not usable stock, and so must not be counted, hauled or bought against.
///
/// <para>Three kinds. Two of them hide beneath a container chain rather than being flagged item by
/// item — which is why a naive filter misses nearly all of them — and the third is a flag that was
/// there all along and not being read.</para>
/// </summary>
public static class AssetExclusions
{
    private const string AssetSafetyFlag = "AssetSafety";

    /// <summary>Category 6 — every ship hull in the game.</summary>
    private const int ShipCategory = 6;

    /// <summary>
    /// Everything inside an asset safety wrap or inside a ship, plus every assembled hull.
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
    ///
    /// <para><b>⚠️ Assembled hulls.</b> A PACKAGED ship is stock: it stacks, it is what a job
    /// produces, and hauling one is an ordinary errand. An ASSEMBLED one is a ship somebody
    /// flies — named, fitted, possibly undocked — and it is not material for anything. The tool
    /// was listing freighters at the top of the haul plan as input to blocked jump freighter
    /// jobs; they were the corporation's working freighters, and the fleet that would have moved
    /// them was the fleet being asked to move.</para>
    /// </summary>
    public static Task<HashSet<long>> UnusableItemIdsAsync(AppDbContext db, CancellationToken ct = default)
        // Seven generators ask this in one build and none of them changes the answer; fetched
        // once per build (see BuildCache), and the set is only ever read by its callers.
        => Worklist.BuildCache.GetOrAddAsync("AssetExclusions.Unusable", () => UnusableItemIdsUncachedAsync(db, ct));

    private static async Task<HashSet<long>> UnusableItemIdsUncachedAsync(
        AppDbContext db, CancellationToken ct)
    {
        // ⚠️ Ships and wraps read separately, because they are not excluded on the same terms.
        // Both are walked INTO, but only the assembled ships are themselves unusable.
        var ships = await db.EsiAssets.AsNoTracking()
            .Where(a => db.SdeTypes.Any(t => t.TypeId == a.TypeId
                         && db.SdeGroups.Any(g => g.GroupId == t.GroupId
                              && g.CategoryId == ShipCategory)))
            .Select(a => new { a.ItemId, a.IsSingleton })
            .ToListAsync(ct);

        var wraps = await db.EsiAssets.AsNoTracking()
            .Where(a => a.LocationFlag == AssetSafetyFlag)
            .Select(a => a.ItemId)
            .ToListAsync(ct);

        var roots = ships.Select(s => s.ItemId).Concat(wraps).ToHashSet();
        if (roots.Count == 0) return [];

        // The wraps themselves stay usable, and so does a packed hull sitting in a hangar — it is
        // a real asset and may even be the thing being built. An assembled hull is not.
        var excluded = ships.Where(s => s.IsSingleton).Select(s => s.ItemId).ToHashSet();

        var frontier = roots.ToList();

        // Downward, one pass per level of nesting, rather than a chain walk per item. Real
        // nesting is a few deep; the cap only stops malformed data from spinning. Each step is
        // kept — whose snapshot put the item there — for the check below.
        var steps = new List<(long Item, long Owner, long Parent)>();
        for (var depth = 0; depth < 16 && frontier.Count > 0; depth++)
        {
            var children = await db.EsiAssets.AsNoTracking()
                .Where(a => frontier.Contains(a.LocationId))
                .Select(a => new { a.ItemId, a.OwnerId, a.LocationId })
                .ToListAsync(ct);

            steps.AddRange(children.Select(c => (c.ItemId, c.OwnerId, c.LocationId)));
            frontier = children.Select(c => c.ItemId).Where(excluded.Add).ToList();
        }

        var unusable = new UnusableAssets();
        unusable.UnionWith(excluded);
        await UnloadedSinceAsync(db, steps, unusable, ct);
        return unusable;
    }

    /// <summary>
    /// Whether one asset row is unusable: its item, wherever it is — or this owner's copy of it,
    /// where an older snapshot still has it inside a ship it has since left.
    /// <para>⚠️ Use this, not <c>Contains</c>, wherever the row's owner is to hand: an item
    /// unloaded from a ship is listed twice until the older snapshot is polled again, and only
    /// the owner tells the copy in the hangar from the stale one in the hold.</para>
    /// </summary>
    public static bool Excludes(this HashSet<long> unusable, long itemId, long ownerId) =>
        unusable.Contains(itemId) || unusable is UnusableAssets u && u.StaleRows.Contains((itemId, ownerId));

    /// <summary>
    /// Items one snapshot has inside a ship or a wrap that a newer snapshot has somewhere else.
    ///
    /// <para>⚠️ An item keeps its id when it is unloaded. A pilot's asset poll from before the
    /// unloading still lists the stack in their ship's hold; the corporation's, run since, lists
    /// the same stack in its hangar where it was put down — and excluded by id, that hangar stack
    /// was thrown away with the ship's contents. Measured: a hold unloaded at three structures,
    /// the corporation polled thirteen minutes later, and 1,433,337 Isogen, 7,800 Construction
    /// Blocks and three blueprint copies were "not on hand" at the structures holding them, for
    /// the hour until the pilot was polled again — the hauling loop sent the pilot to fetch them
    /// a second time. The freshest snapshot says where an item is.</para>
    /// </summary>
    /// <param name="steps">Each item the walk found inside a ship or a wrap: whose snapshot, and
    /// what it is inside.</param>
    /// <param name="unusable">Has those items taken out, and the stale copies — the older
    /// snapshot's, inside the ship — put in their place.</param>
    private static async Task UnloadedSinceAsync(
        AppDbContext db, List<(long Item, long Owner, long Parent)> steps, UnusableAssets unusable, CancellationToken ct)
    {
        var freed = new HashSet<long>();
        if (steps.Count == 0) return;

        var ids  = steps.Select(s => s.Item).Distinct().ToList();
        var rows = await db.EsiAssets.AsNoTracking()
            .Where(a => ids.Contains(a.ItemId))
            .Select(a => new { a.ItemId, a.OwnerId, a.LocationId })
            .ToListAsync(ct);
        // Only items more than one snapshot lists can have been moved between them.
        var shared = rows.GroupBy(r => r.ItemId).Where(g => g.Select(r => r.OwnerId).Distinct().Count() > 1).ToList();
        if (shared.Count == 0) return;

        var clocks = await HaulLag.AssetClocksAsync(db, ct);
        DateTimeOffset At(long owner) => clocks.GetValueOrDefault(owner);
        var inside = steps.ToLookup(s => s.Item);

        // Outside the ship by a newer snapshot than any that has it inside — or inside something
        // that is itself out: a container unloaded whole takes its contents with it.
        for (var pass = 0; pass < 16; pass++)
        {
            var more = false;
            foreach (var g in shared)
            {
                if (freed.Contains(g.Key)) continue;
                var stale = inside[g.Key].Where(s => !freed.Contains(s.Parent)).Select(s => At(s.Owner)).DefaultIfEmpty().Max();
                var newer = g.Any(r => At(r.OwnerId) > stale
                                       && (!inside[g.Key].Any(s => s.Owner == r.OwnerId && s.Parent == r.LocationId)
                                           || freed.Contains(r.LocationId)));
                if (newer) { freed.Add(g.Key); more = true; }
            }
            if (!more) break;
        }

        // The item counts where the newest snapshot has it; every older copy does not.
        foreach (var g in shared.Where(g => freed.Contains(g.Key)))
        {
            unusable.Remove(g.Key);
            var newest = g.Max(r => At(r.OwnerId));
            foreach (var r in g.Where(r => At(r.OwnerId) < newest))
                unusable.StaleRows.Add((g.Key, r.OwnerId));
        }
    }
}

/// <summary>
/// Item ids that are not usable stock, and the stale copies of items moved out of a ship that an
/// older snapshot still lists inside it. Test a row with <see cref="AssetExclusions.Excludes"/>.
/// </summary>
public sealed class UnusableAssets : HashSet<long>
{
    /// <summary>(Item, owner): the copy in this owner's older snapshot, inside a ship the item
    /// has since left.</summary>
    public HashSet<(long Item, long Owner)> StaleRows { get; } = [];
}
