using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Raw material that has arrived where it is processed and is now waiting on someone to press the
/// button.
///
/// <para><b>⚠️ Only at the park's own processing facilities.</b> This is the second half of a
/// journey <see cref="LogisticsGenerator"/> starts: it hauls ore, ice and compressed gas to
/// whichever facility the park assigns to <c>refine_ore</c>, <c>refine_moon_ore</c>,
/// <c>refine_ice</c> or <c>decompress_gas</c>, and this raises the task once it lands. Ore
/// anywhere else is either already being hauled here or is somewhere the player never intended to
/// process anything — a few hundred units left in an NPC station by an expired courier contract is
/// not work, and at this priority it would sit near the top of the list saying otherwise.</para>
///
/// <para>Both generators read the routing from <see cref="RefiningRoutes"/>. Kept together because
/// a disagreement between them fails silently: material hauled to a facility the other never looks
/// at, sitting there with nothing ever saying to process it.</para>
///
/// <para><b>One task per facility per kind.</b> Reprocessing is a single window with everything
/// dropped into it, so a row per ore type would be a dozen rows for one sitting. Each type is a
/// line on the task instead.</para>
///
/// <para><b>Ore is batched; gas is not.</b> Reprocessing works in whole portions and a part batch
/// yields nothing, so a type earns a line only once there is a full batch of it and the figure
/// quoted is the batched amount rather than everything held — 150 Veldspar is one batch of 100
/// with 50 staying put. Quoting the held figure would send someone to a window that then refuses
/// part of it. Compressed gas decompresses a unit at a time, so any amount counts.</para>
/// </summary>
public class RefiningGenerator(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService assignment,
    WorklistSettings settings,
    AppErrorLogger errorLogger) : IWorklistGenerator
{
    public string Id          => "refining";
    public string DisplayName => "Refining";

    private sealed record Holding(long LocationId, string Route, int TypeId, string Name, long Units);

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var parkId = await WorklistSettings.ResolveParkIdAsync(db, settings.IndustryParkId, ct);
        if (parkId <= 0) return [];

        try
        {
            return await BuildAsync(db, parkId, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            errorLogger.Log(nameof(RefiningGenerator), $"Park {parkId}", ex);
            return [];
        }
    }

    private async Task<List<WorklistItem>> BuildAsync(AppDbContext db, int parkId, CancellationToken ct)
    {
        var target = await RefiningRoutes.TargetsAsync(db, parkId, ct);
        if (target.Values.All(v => v is null)) return [];

        // Only the facilities the park actually processes at. Everything else the player owns is
        // either on its way here or somewhere they never meant to refine.
        var here = target.Values.OfType<long>().Distinct().ToList();
        if (here.Count == 0) return [];

        // Same ownership rule as the rest of the worklist: every character, corporations only when
        // the user has opted the non-personal ones in.
        var corps = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);

        var rows = await (
            from a in db.EsiAssets.AsNoTracking()
            join t in db.SdeTypes.AsNoTracking()  on a.TypeId  equals t.TypeId
            join g in db.SdeGroups.AsNoTracking() on t.GroupId equals g.GroupId
            where here.Contains(a.RootLocationId)
                  && (a.OwnerType != "corporation" || corps == null || corps.Contains(a.OwnerId))
            select new { a.RootLocationId, a.TypeId, t.Name, t.PortionSize, g.CategoryId, g.GroupId, a.Quantity })
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        // ⚠️ Widened before summing. Asset quantities are int and a refinery can hold billions of
        // units of one ore, which overflows the accumulator on the way in.
        var holdings = new List<Holding>();

        foreach (var g in rows.GroupBy(r => (r.RootLocationId, r.TypeId, r.Name, r.PortionSize,
                                             r.CategoryId, r.GroupId)))
        {
            var route = RefiningRoutes.Route(g.Key.CategoryId, g.Key.GroupId);
            if (route is null) continue;

            // Sitting at a facility that processes something else entirely — ore in the gas
            // refinery — is a hauling question, not this task's.
            if (target.GetValueOrDefault(route) != g.Key.RootLocationId) continue;

            var units   = g.Sum(r => (long)r.Quantity);
            var portion = Math.Max(1, g.Key.PortionSize);

            // Whole batches only for anything that reprocesses; gas decompresses a unit at a time.
            if (RefiningRoutes.IsRefine(route)) units = units / portion * portion;
            if (units <= 0) continue;

            holdings.Add(new Holding(g.Key.RootLocationId, route, g.Key.TypeId, g.Key.Name, units));
        }

        if (holdings.Count == 0) return [];

        var places = await PlaceNamesAsync(db, holdings.Select(h => h.LocationId).Distinct().ToList(), ct);

        // One window per facility, so the refine routes collapse together — ore, moon ore and ice
        // dropped into the same reprocessing window at the same structure is one sitting.
        return holdings
            .GroupBy(h => (h.LocationId, Refine: RefiningRoutes.IsRefine(h.Route)))
            .Select(g => Task(g.Key.LocationId, g.Key.Refine, g.ToList(), places))
            .ToList();
    }

    private WorklistItem Task(
        long locationId, bool refine, List<Holding> holdings, Dictionary<long, string> places)
    {
        var lines = holdings
            .OrderByDescending(h => h.Units)
            .Select(h => new WorklistLine(h.TypeId, h.Name, h.Units))
            .ToList();

        var total = lines.Sum(l => l.Quantity);
        var place = places.GetValueOrDefault(locationId, $"Location {locationId}");
        var what  = refine ? "ore" : "compressed gas";
        var verb  = refine ? "Reprocess" : "Decompress";

        var types = lines.Count == 1 ? lines[0].TypeName : $"{lines.Count} {what} types";

        var biggest = string.Join(", ", lines.Take(3).Select(l => $"{l.Quantity:N0} {l.TypeName}"));
        var more    = lines.Count > 3 ? $" and {lines.Count - 3} more" : "";

        return new WorklistItem
        {
            // Facility and kind only. What is sitting there changes with every refresh, and a key
            // that moved with it would reset the task's age and drop its snooze.
            Key           = $"{Id}:{(refine ? "refine" : "decompress")}:{locationId}",
            Source        = Id,
            Kind          = refine ? WorklistKind.Refine : WorklistKind.Decompress,
            Title         = $"{verb} {types} — {total:N0} units",
            Quantity      = total,
            Detail        = refine
                // ⚠️ "Reprocessable" rather than "held". The two differ by whatever does not fill
                // a whole batch, and quoting the held figure sends someone to a window that then
                // refuses part of it.
                ? $"At {place}: {total:N0} units reprocessable in whole batches — {biggest}{more}. "
                + "Anything short of a full batch stays where it is."
                : $"At {place}: {total:N0} units to decompress — {biggest}{more}.",
            // Nothing to check: the material is here and the action is local.
            Readiness     = WorklistReadiness.Ready,
            LocationId    = locationId,
            LocationName  = place,
            Priority      = WorklistPriority.Refining,
            Lines         = lines,
        };
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
