using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// One job stopped for want of material that is already owned, sitting somewhere else.
///
/// <para>The other half of Item Contention. That tab reports material nothing owns enough of,
/// where the answer is a purchase or a build; this one reports material that exists and is in the
/// wrong place, where the answer is a trip. Reading the two as one list invited the mistake the
/// split exists to prevent: sending somebody shopping for something already in a hangar three
/// jumps away.</para>
///
/// <para>⚠️ One row per BLOCKED JOB. Grouping by destination was tried first and read as a
/// logistics summary rather than as a list of stopped work — the question is which jobs are
/// waiting on a delivery, and a station is not waiting on anything.</para>
/// </summary>
/// <param name="StalledTasks">Jobs stopped behind this one, transitively — what starts moving
/// again once this job can run.</param>
/// <param name="HaulTasks">Hauls already on the worklist bringing any of this job's missing
/// material to it. Zero is the finding: the material exists, the job is stopped, and nothing is
/// moving it.</param>
/// <param name="ItemTypes">Distinct items this job is waiting on.</param>
/// <param name="Volume">Cubic metres of what this job is short of.</param>
/// <param name="Sources">Stops a hauler would have to make to cover what is short, largest
/// source first. Not the number of places holding the item: Tritanium sits in thirteen
/// hangars and one of them covers the job.</param>
public sealed record HaulBlock(
    string TaskKey,
    string Title,
    int    TypeId,
    long   StationId,
    string StationName,
    int    StalledTasks,
    int    ItemTypes,
    double Volume,
    int    Sources,
    int    HaulTasks,
    IReadOnlyList<ShortageTask> Tasks)
{
    /// <summary>
    /// What the row amounts to.
    ///
    /// <para>The distinction that matters is whether anything is already moving. A haul on the
    /// list is work in progress; no haul at all is work nobody has raised, and the same stopped
    /// job means something different in each case.</para>
    /// </summary>
    public string Verdict =>
        HaulTasks <= 0 ? "Nothing moving"
      : Sources    > 1 ? "Several stops"
      :                  "Haul raised";

    public string Advice => Verdict switch
    {
        "Nothing moving" =>
            $"Stopped for want of {ItemTypes:N0} item(s) already owned. {Volume:N0} m3 to move, "
          + $"{Stops}. No haul on the list brings any of it here, so nothing about this changes "
          + $"on its own.{Behind}",

        "Several stops" =>
            $"Stopped for want of {ItemTypes:N0} item(s), {Volume:N0} m3 in all, {Stops}, with "
          + $"{HaulTasks:N0} haul(s) already raised. More than one pickup, so it stays stopped "
          + $"until the last of them lands.{Behind}",

        _ =>
            $"Stopped, and {HaulTasks:N0} haul(s) are already raised to bring the {Volume:N0} m3 "
          + $"here. Nothing to decide — it starts when the material arrives.{Behind}",
    };

    /// <summary>How many places have to be visited to cover it.</summary>
    private string Stops =>
        Sources <= 0 ? "none of it reachable in scope"
      : Sources == 1 ? "all of it from one place"
      :                $"from {Sources:N0} places";

    /// <summary>What waiting costs beyond this job. Silent where nothing waits on it.</summary>
    private string Behind =>
        StalledTasks > 0
            ? $" {StalledTasks:N0} further task(s) are stopped behind it."
            : "";
}

/// <summary>Builds the Hauling rows from the worklist and the asset table.</summary>
public class HaulPressureService(
    IDbContextFactory<AppDbContext> dbFactory,
    WorklistSettings                settings)
{
    public async Task<List<HaulBlock>> PressuresAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        // ⚠️ MustBuy false is the whole selection: owned within the scope, just not where the
        // job is. Its opposite is Item Contention's list, and nothing belongs on both.
        var blocked = items
            .Select(i => (Item: i, Short: i.Shortages.Where(s => !s.MustBuy).ToList()))
            .Where(x => x.Short.Count > 0 && x.Item.LocationId > 0)
            .ToList();
        if (blocked.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var index = TaskChain.Index(items);
        var ids   = blocked.SelectMany(x => x.Short.Select(s => s.TypeId)).Distinct().ToList();

        var volumes = await db.SdeTypes.AsNoTracking()
            .Where(t => ids.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Volume, ct);

        // Where the material actually is. Scoped as the generator scopes it: stock the plan
        // cannot reach is not a pickup, it is somebody else's.
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);

        // ⚠️ Quantities, not just a set of locations. Counting the places that hold a type at
        // all answered a question nobody asked: Tritanium is in thirteen hangars, and "13 other
        // place(s)" beside a job read as thirteen pickups when one of them covers the shortfall
        // outright. What a trip costs is the number of stops needed to COVER what is short.
        var held = (await db.EsiAssets.AsNoTracking()
                .Where(a => ids.Contains(a.TypeId) && a.Quantity > 0)
                .Select(a => new { a.TypeId, a.RootLocationId, a.Quantity })
                .ToListAsync(ct))
            .Where(a => scope is null || scope.Contains(a.RootLocationId))
            .GroupBy(a => a.TypeId)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(a => a.RootLocationId)
                      .Select(x => (Loc: x.Key, Qty: x.Sum(a => (long)a.Quantity)))
                      .OrderByDescending(x => x.Qty)
                      .ToList());

        // Somewhere to send the reader. A bare id is not a place.
        var places = (await db.SdeStations.AsNoTracking()
                .Select(x => new { Id = (long)x.StationId, x.Name }).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Name);
        foreach (var x in await db.EsiStructureNames.AsNoTracking()
                     .Where(x => x.Name != "")
                     .Select(x => new { x.StructureId, x.Name }).ToListAsync(ct))
            places[x.StructureId] = x.Name;

        // Largest first until the shortfall is covered: the stops a hauler would actually make.
        List<long> Pickups(int typeId, long shortBy, long here)
        {
            var stops = new List<long>();
            var left  = shortBy;
            foreach (var (loc, qty) in held.GetValueOrDefault(typeId, []))
            {
                if (left <= 0) break;
                if (loc == here) continue;
                stops.Add(loc);
                left -= qty;
            }
            return stops;
        }

        // Hauls already raised, by where they are going and what they carry.
        var hauls = items
            .Where(i => i.Kind == WorklistKind.Haul && i.DestinationId > 0)
            .Select(i => (
                i.DestinationId,
                i.Title,
                i.Readiness,
                From: i.LocationName,
                Types: i.Lines.Count > 0
                    ? i.Lines.Select(l => l.TypeId).ToHashSet()
                    : new HashSet<int> { i.TypeId }))
            .ToList();

        var rows = new List<HaulBlock>();

        foreach (var (item, short_) in blocked)
        {
            var types = short_.Select(s => s.TypeId).Distinct().ToList();
            var here  = item.LocationId;

            // What this job would have made is missing from whatever was waiting on it. Seeded
            // from the OUTPUT, so the job itself is not counted among the things it blocks.
            var chain = item.TypeId > 0 ? TaskChain.Stalled(index, item.TypeId) : [];

            // The union across items: one trip, however many things it collects.
            var stops = short_
                .SelectMany(sh => Pickups(sh.TypeId, sh.Short, here))
                .Distinct()
                .ToList();

            var moving = hauls
                .Where(h => h.DestinationId == here && h.Types.Overlaps(types))
                .ToList();

            // The row opens on what it is short of, then what is moving, then what waits on it.
            var detail = new List<ShortageTask>();

            detail.AddRange(short_.Select(sh =>
            {
                var from = Pickups(sh.TypeId, sh.Short, here);
                var name = from.Count > 0
                    ? places.GetValueOrDefault(from[0], $"Location {from[0]}")
                    : "nowhere in scope";

                return new ShortageTask(
                    "Needs", 0, sh.TypeName, sh.TypeName,
                    from.Count == 1 ? "1 stop" : $"{from.Count:N0} stops",
                    $"short {sh.Short:N0} of {sh.Wanted:N0}, "
                  + $"{sh.Short * volumes.GetValueOrDefault(sh.TypeId):N0} m3 — from {name}"
                  + (from.Count > 1 ? $" and {from.Count - 1:N0} more" : ""));
            }));

            detail.AddRange(moving.Select(h => new ShortageTask(
                "Hauling", -1, "", h.Title, h.Readiness.ToString(),
                h.From.Length > 0 ? $"already on the list, from {h.From}" : "already on the list")));

            detail.AddRange(chain.Select(t => t with { Hop = t.Hop + 1 }));

            rows.Add(new HaulBlock(
                item.Key,
                item.Title,
                item.TypeId,
                here,
                item.LocationName,
                chain.Count,
                types.Count,
                short_.Sum(s => s.Short * volumes.GetValueOrDefault(s.TypeId)),
                stops.Count,
                moving.Count,
                detail));
        }

        // What is holding up the most work first, then the trips nobody has raised: a job with a
        // haul moving is waiting, and one with none is waiting for somebody to notice.
        return [.. rows
            .OrderByDescending(r => r.StalledTasks)
            .ThenByDescending(r => r.HaulTasks == 0)
            .ThenByDescending(r => r.Volume)];
    }
}
