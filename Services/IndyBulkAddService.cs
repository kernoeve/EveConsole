using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One real structure that could be added to a park.</summary>
public sealed record BulkAddCandidate(
    long StructureId, string Name, int TypeId, string TypeKey, string TypeLabel,
    bool OnMoon, string Celestial, bool AlreadyInPark);

/// <summary>
/// Finds the industrial structures in a system so a whole park can be set up at once instead of
/// one row at a time.
///
/// Two filters do the work, and both matter more than they look.
///
/// <para>Only industry types are offered. Citadels — Astrahus, Fortizar, Keepstar — have no
/// industry slots, so a park entry for one would model a facility that can never run a job.</para>
///
/// <para>Refineries anchored to a moon are optional, and separated from everything else because
/// they are usually moon mining rather than industry, and there are a lot of them: across the
/// structures known here, 404 of 470 Athanors sit on a moon. The same test is deliberately not
/// applied to engineering complexes — 121 of 314 Raitarus are also nearest to a moon, but that is
/// proximity, not purpose, and filtering them would drop real factories.</para>
/// </summary>
public class IndyBulkAddService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Structure type id to the park's own type key. Anything absent is not industry.</summary>
    private static readonly Dictionary<int, (string Key, string Label)> IndyTypes = new()
    {
        [35825] = ("raitaru", "Raitaru"),
        [35826] = ("azbel",   "Azbel"),
        [58735] = ("azbel",   "Azbel"),   // second published-flag variant of the same hull
        [35827] = ("sotiyo",  "Sotiyo"),
        [35835] = ("athanor", "Athanor"),
        [35836] = ("tatara",  "Tatara"),
    };

    /// <summary>
    /// The hull type id behind a park's own type key, or 0 for a key that names no player
    /// structure — <c>npc_station</c>, or anything unrecognised.
    ///
    /// <para>Read off <see cref="IndyTypes"/> rather than written out a second time, so the two
    /// cannot drift. Lowest id wins where a hull has more than one published variant: they are the
    /// same structure, and the canonical id is the one everything else reports.</para>
    /// </summary>
    public static int TypeIdForKey(string key)
        => IndyTypes.Where(p => p.Value.Key == key).Select(p => p.Key).DefaultIfEmpty(0).Min();

    /// <summary>Refineries are the moon-mining ones; the skip option applies only to these.</summary>
    private static readonly HashSet<string> RefineryKeys = ["athanor", "tatara"];

    /// <summary>SdeCelestials.Kind for a moon. Verified against the data: kind 1 rows are named
    /// "&lt;planet&gt; - Moon &lt;n&gt;".</summary>
    private const int CelestialKindMoon = 1;

    public async Task<List<BulkAddCandidate>> FindInSystemAsync(
        int solarSystemId, int parkId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var typeIds = IndyTypes.Keys.ToList();

        var rows = await db.EsiStructureNames.AsNoTracking()
            .Where(s => s.SolarSystemId == solarSystemId && typeIds.Contains(s.TypeId))
            .Select(s => new { s.StructureId, s.Name, s.TypeId, s.NearestCelestialId })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var celestialIds = rows.Select(r => r.NearestCelestialId).Where(id => id > 0).Distinct().ToList();
        var celestials = celestialIds.Count > 0
            ? await db.SdeCelestials.AsNoTracking()
                .Where(c => celestialIds.Contains(c.ItemId))
                .Select(c => new { c.ItemId, c.Kind, c.Name })
                .ToDictionaryAsync(c => c.ItemId, ct)
            : [];

        // Already-linked structures are shown but not offered again, so re-running the button
        // after adding one more structure does not create duplicates.
        var linked = (await db.IndyStructures.AsNoTracking()
            .Where(s => s.ParkId == parkId && s.RealStructureId != null)
            .Select(s => s.RealStructureId!.Value)
            .ToListAsync(ct)).ToHashSet();

        return rows
            .Select(r =>
            {
                var (key, label) = IndyTypes[r.TypeId];
                celestials.TryGetValue(r.NearestCelestialId, out var cel);
                var onMoon = RefineryKeys.Contains(key) && cel?.Kind == CelestialKindMoon;

                return new BulkAddCandidate(
                    r.StructureId,
                    r.Name.Length > 0 ? r.Name : $"Structure {r.StructureId}",
                    r.TypeId, key, label,
                    onMoon,
                    cel?.Name ?? "",
                    linked.Contains(r.StructureId));
            })
            // Stable order so the same system always lists the same way.
            .OrderBy(c => c.TypeLabel)
            .ThenBy(c => c.Name)
            .ToList();
    }
}
