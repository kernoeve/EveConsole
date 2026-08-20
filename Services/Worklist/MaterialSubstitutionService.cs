using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>Stock of one thing that would become another: ore holding minerals, compressed gas
/// holding gas.</summary>
/// <param name="PortionSize">Reprocessing works in batches; a part batch yields nothing.</param>
/// <param name="PerPortion">Units of the product one batch gives at perfect yield.</param>
public sealed record Substitute(
    int SourceTypeId, string SourceName, int PortionSize, int PerPortion, double Yield)
{
    /// <summary>What a pile of the source is worth in the product, after batching and yield.</summary>
    public long From(long units) =>
        PortionSize <= 0 ? 0 : (long)(units / PortionSize * (double)PerPortion * Yield);
}

/// <summary>
/// Material a player already holds in an unrefined form.
///
/// <para>A shortfall of Tritanium is not a shortfall if the ore is sitting in the hangar, and a
/// task to buy it would be spending ISK on something already owned. Ore, ice and gas are counted
/// toward the things they turn into — compressed or not, because the choice to compress is about
/// hauling and says nothing about whether the material is there.</para>
///
/// <para>What it deliberately does not do is call the job unblocked. The material is in the wrong
/// form and probably the wrong place, so the job still cannot start; the refining and hauling
/// that would fix that are their own work. This only stops the tool from buying a second copy of
/// something already owned.</para>
/// </summary>
public class MaterialSubstitutionService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>
    /// Ore and ice into minerals and isotopes. The figure the reprocessing valuation already
    /// uses, and sourced the same way: a Tatara with a T2 rig in nullsec, Reprocessing,
    /// Reprocessing Efficiency and the ore specialisation at V, and an RX-804 implant.
    /// </summary>
    public const double RefiningYield = 0.9063;

    /// <summary>
    /// Compressed gas back into gas. Held apart from refining because it is a different
    /// operation on a different thing, and rated conservatively at the player's estimate rather
    /// than assumed lossless — under-counting delays a purchase, over-counting cancels one that
    /// was needed.
    /// </summary>
    public const double DecompressionYield = 0.95;

    private const int AsteroidCategory  = 25;   // ore, ice, moon ore
    private const int CelestialCategory = 2;    // harvestable gas clouds

    /// <summary>
    /// Everything that reduces to something else, keyed by what it produces.
    ///
    /// <para>Restricted to ore, ice and gas. Every built item also lists the minerals it
    /// reprocesses into, and counting those would have the tool decide a Tritanium shortfall is
    /// covered because there are ships in the hangar to melt down.</para>
    /// </summary>
    public async Task<Dictionary<int, List<Substitute>>> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sources = await db.SdeTypes.AsNoTracking()
            .Where(t => db.SdeGroups.Any(g => g.GroupId == t.GroupId
                        && (g.CategoryId == AsteroidCategory || g.CategoryId == CelestialCategory)))
            .Select(t => new { t.TypeId, t.Name, t.PortionSize })
            .ToListAsync(ct);
        if (sources.Count == 0) return [];

        var sourceIds = sources.Select(s => s.TypeId).ToList();

        var outputs = await db.SdeTypeMaterials.AsNoTracking()
            .Where(m => sourceIds.Contains(m.TypeId))
            .Select(m => new { m.TypeId, m.MaterialTypeId, m.Quantity })
            .ToListAsync(ct);

        // Compressed gas lists its own uncompressed form as the output. That edge is a
        // decompression rather than a refine, and carries the gentler loss.
        var twins = (await db.HoboCompressibleTypes.AsNoTracking()
                .Select(c => new { c.CompressedTypeId, c.SourceTypeId })
                .ToListAsync(ct))
            .Select(c => (c.CompressedTypeId, c.SourceTypeId))
            .ToHashSet();

        var byType = sources.ToDictionary(s => s.TypeId);
        var result = new Dictionary<int, List<Substitute>>();

        foreach (var o in outputs)
        {
            if (!byType.TryGetValue(o.TypeId, out var src)) continue;
            if (o.Quantity <= 0) continue;

            var yield = twins.Contains((o.TypeId, o.MaterialTypeId))
                ? DecompressionYield
                : RefiningYield;

            result.TryAdd(o.MaterialTypeId, []);
            result[o.MaterialTypeId].Add(new Substitute(
                o.TypeId, src.Name, Math.Max(1, src.PortionSize), o.Quantity, yield));
        }

        return result;
    }
}
