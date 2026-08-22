using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Material already owned that is in the wrong form: ore waiting to be reprocessed, compressed gas
/// waiting to be decompressed.
///
/// <para>The rest of the worklist counts this stock toward what it becomes —
/// <see cref="MaterialSubstitutionService"/> stops the tool buying Tritanium that is sitting in
/// the hangar as Veldspar — but counting it deliberately does not unblock the job, because the
/// material is still ore. This is the task that closes that gap. It is also the cheapest work the
/// list carries: no trip, no ISK, one action at the station the material is already in, which is
/// why it ranks at <see cref="WorklistPriority.Refining"/>.</para>
///
/// <para><b>One task per station per kind.</b> Reprocessing is a single window with everything
/// dropped into it, so a row per ore type would be a dozen rows for one sitting. Each type is a
/// line on the task instead.</para>
///
/// <para><b>⚠️ Ore is batched; gas is not.</b> Reprocessing works in whole portions and a part
/// batch yields nothing, so a type only earns a line once there is a full batch of it, and the
/// figure quoted is the batched amount rather than everything held — 150 Veldspar is one batch of
/// 100 with 50 staying put. Compressed gas decompresses a unit at a time, so any amount counts.
/// </para>
///
/// <para><b>Compressed ore is ore.</b> It reprocesses directly and never decompresses, so it
/// belongs to the refine task; only gas has a separate decompression step. Which is which comes
/// from the same compressible table <see cref="MaterialSubstitutionService"/> reads, so the two
/// cannot disagree about what a thing is.</para>
/// </summary>
public class RefiningGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService assignment,
    WorklistSettings settings) : IWorklistGenerator
{
    public string Id          => "refining";
    public string DisplayName => "Refining";

    /// <summary>Ore, ice and moon ore.</summary>
    private const int AsteroidCategory  = 25;

    /// <summary>Harvestable gas clouds.</summary>
    private const int CelestialCategory = 2;

    /// <summary>A station holding less than this of everything together is not worth a trip to a
    /// reprocessing window. Ore arrives in odd handfuls from salvage and expired orders, and a
    /// task for eight hundred units of Veldspar is noise.</summary>
    private const long MinimumUnits = 5_000;

    private sealed record Holding(long LocationId, int TypeId, string Name, int PortionSize, long Units);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Same ownership rule as the rest of the worklist: every character, corporations only when
        // the user has opted the non-personal ones in.
        var corps = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);

        var compressed = (await db.HoboCompressibleTypes.AsNoTracking()
            .Select(h => h.CompressedTypeId).ToListAsync(ct)).ToHashSet();

        var rows = await (
            from a in db.EsiAssets.AsNoTracking()
            join t in db.SdeTypes.AsNoTracking()  on a.TypeId  equals t.TypeId
            join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
            where (g.CategoryId == AsteroidCategory || g.CategoryId == CelestialCategory)
                  && (a.OwnerType != "corporation" || corps == null || corps.Contains(a.OwnerId))
            select new { a.RootLocationId, a.TypeId, t.Name, t.PortionSize, g.CategoryId, a.Quantity })
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        // ⚠️ Widened before summing. Asset quantities are int and a station can hold billions of
        // units of one ore, which overflows the accumulator on the way in.
        var held = rows
            .GroupBy(r => (r.RootLocationId, r.TypeId, r.Name, r.PortionSize, r.CategoryId))
            .Select(g => new
            {
                g.Key.RootLocationId, g.Key.TypeId, g.Key.Name, g.Key.CategoryId,
                PortionSize = Math.Max(1, g.Key.PortionSize),
                Units       = g.Sum(r => (long)r.Quantity),
            })
            .ToList();

        var toRefine = held
            .Where(h => h.CategoryId == AsteroidCategory)
            // Only whole batches, and only where there is at least one.
            .Select(h => new Holding(h.RootLocationId, h.TypeId, h.Name, h.PortionSize,
                                     h.Units / h.PortionSize * h.PortionSize))
            .Where(h => h.Units > 0)
            .ToList();

        // Gas decompresses a unit at a time, and only the compressed form has anywhere to go.
        var toDecompress = held
            .Where(h => h.CategoryId == CelestialCategory && compressed.Contains(h.TypeId))
            .Select(h => new Holding(h.RootLocationId, h.TypeId, h.Name, h.PortionSize, h.Units))
            .Where(h => h.Units > 0)
            .ToList();

        var places = await PlaceNamesAsync(db,
            toRefine.Concat(toDecompress).Select(h => h.LocationId).Distinct().ToList(), ct);

        var items = new List<WorklistItem>();
        items.AddRange(Tasks(toRefine,     WorklistKind.Refine,     places));
        items.AddRange(Tasks(toDecompress, WorklistKind.Decompress, places));
        return items;
    }

    private List<WorklistItem> Tasks(
        List<Holding> holdings, WorklistKind kind, Dictionary<long, string> places)
    {
        var refine = kind == WorklistKind.Refine;
        var items  = new List<WorklistItem>();

        foreach (var station in holdings.GroupBy(h => h.LocationId))
        {
            var lines = station
                .OrderByDescending(h => h.Units)
                .Select(h => new WorklistLine(h.TypeId, h.Name, h.Units))
                .ToList();

            var total = lines.Sum(l => l.Quantity);
            if (total < MinimumUnits) continue;

            var place = places.GetValueOrDefault(station.Key, $"Location {station.Key}");
            var what  = refine ? "ore" : "compressed gas";
            var verb  = refine ? "Reprocess" : "Decompress";

            var types = lines.Count == 1
                ? lines[0].TypeName
                : $"{lines.Count} {what} types";

            items.Add(new WorklistItem
            {
                // Station and kind only. What is sitting there changes with every refresh, and a
                // key that moved with it would reset the task's age and drop its snooze.
                Key           = $"{Id}:{(refine ? "refine" : "decompress")}:{station.Key}",
                Source        = Id,
                Kind          = kind,
                Title         = $"{verb} {types} — {total:N0} units",
                Quantity      = total,
                Detail        = Detail(refine, lines, total, place),
                // Nothing to check: the material is there and the action is local.
                Readiness     = WorklistReadiness.Ready,
                LocationId    = station.Key,
                LocationName  = place,
                Priority      = WorklistPriority.Refining,
                Lines         = lines,
            });
        }

        return items;
    }

    private static string Detail(bool refine, List<WorklistLine> lines, long total, string place)
    {
        var biggest = string.Join(", ", lines.Take(3).Select(l => $"{l.Quantity:N0} {l.TypeName}"));
        var more    = lines.Count > 3 ? $" and {lines.Count - 3} more" : "";

        return refine
            // ⚠️ Says "reprocessable" rather than "held" on purpose. The two differ by whatever
            // does not fill a whole batch, and quoting the held figure would send someone to a
            // window that then refuses part of it.
            ? $"At {place}: {total:N0} units reprocessable in whole batches — {biggest}{more}. "
            + "Anything short of a full batch stays where it is."
            : $"At {place}: {total:N0} units to decompress — {biggest}{more}.";
    }

    private static async Task<Dictionary<long, string>> PlaceNamesAsync(
        AppDbContext db, List<long> ids, CancellationToken ct)
    {
        var names = new Dictionary<long, string>();
        if (ids.Count == 0) return names;

        foreach (var s in await db.SdeStations.AsNoTracking()
                     .Where(s => ids.Contains(s.StationId))
                     .Select(s => new { s.StationId, s.Name }).ToListAsync(ct))
            names[s.StationId] = s.Name;

        foreach (var s in await db.Structures.AsNoTracking()
                     .Where(s => ids.Contains(s.StructureId) && s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            names[s.StructureId] = s.Name;

        return names;
    }
}
