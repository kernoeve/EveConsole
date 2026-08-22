using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Which processing a raw material needs, and where the park does it.
///
/// <para>Shared by the two generators that have to agree about it:
/// <see cref="LogisticsGenerator"/> hauls material to the facility, and
/// <see cref="RefiningGenerator"/> raises the task once it has arrived. Split between them, one
/// could route Veldspar to a refinery the other never looked at — the haul would land and nothing
/// would ever say to press the button.</para>
/// </summary>
public static class RefiningRoutes
{
    public const string Ore     = "refine_ore";
    public const string MoonOre = "refine_moon_ore";
    public const string IceKey  = "refine_ice";
    public const string Gas     = "decompress_gas";

    /// <summary>Every route, in the order a reader would expect to see them.</summary>
    public static readonly string[] All = [Ore, MoonOre, IceKey, Gas];

    /// <summary>Routes that end at a reprocessing window rather than a decompression one.</summary>
    public static bool IsRefine(string route) => route != Gas;

    // Gas lives in the Celestial category alongside cargo containers, wrecks and planetary clouds,
    // so the category is far too broad to route on — matching it sent every secure container in
    // Jita to the refinery.
    private const int CompressedGas        = 4168;

    private const int Ice                  = 465;
    private const int AncientCompressedIce = 903;

    /// <summary>Moon asteroid groups: Ubiquitous, Common, Uncommon, Rare, Exceptional.</summary>
    private static readonly HashSet<int> MoonOreGroups = [1884, 1920, 1921, 1922, 1923];

    private const int AsteroidCategory = 25;

    /// <summary>Which processing a type needs, or null when it is not raw material at all.</summary>
    public static string? Route(int categoryId, int groupId) => groupId switch
    {
        // Compressed gas only. Harvestable Cloud is the raw gas that decompression produces, so
        // sending it to be decompressed asks for a process it has already been through — and it
        // is not reprocessed either, it is consumed as-is by reactions.
        CompressedGas                         => Gas,
        Ice or AncientCompressedIce           => IceKey,
        _ when MoonOreGroups.Contains(groupId) => MoonOre,
        _ when categoryId == AsteroidCategory => Ore,
        _                                     => null,
    };

    /// <summary>
    /// Where the park processes each route, by real structure id. A route with no assignment maps
    /// to null and nothing is hauled or refined for it.
    ///
    /// <para>Falls back to a "reprocessing" assignment, so a park that names one facility for all
    /// of it does not have to repeat itself four times.</para>
    /// </summary>
    public static async Task<Dictionary<string, long?>> TargetsAsync(
        AppDbContext db, int parkId, CancellationToken ct = default)
    {
        var facilities = await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == parkId && s.RealStructureId != null)
            .Select(s => new { s.Id, s.RealStructureId })
            .ToListAsync(ct);

        var assignments = await db.IndyCategoryAssignments.AsNoTracking()
            .Where(a => a.ParkId == parkId && a.StructureId != null)
            .Select(a => new { a.CategoryKey, a.StructureId })
            .ToListAsync(ct);

        long? Facility(string key)
        {
            var a = assignments.FirstOrDefault(x => x.CategoryKey == key)
                 ?? assignments.FirstOrDefault(x => x.CategoryKey == "reprocessing");
            if (a is null) return null;
            return facilities.FirstOrDefault(f => f.Id == a.StructureId)?.RealStructureId;
        }

        return All.ToDictionary(k => k, Facility);
    }
}
