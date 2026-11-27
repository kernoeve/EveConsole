using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Finished industry jobs waiting to be delivered: a task per character who installed them, at the
/// top of the jobs and on that character's own list.
///
/// <para>A finished job still holds its slot, and its output is not in the hangar for anything
/// downstream until somebody delivers it — so it comes before starting anything new, which may well
/// be waiting on that very output or that very slot. The Overview's alert says how many; this says
/// what.</para>
///
/// <para>⚠️ One task per installer, not one for everybody. A personal job can be delivered only by
/// the character who installed it, and a corporation job only by someone in that corporation, so a
/// single task named nobody who could act on it — and the character panels, which show each
/// character's own tasks, never showed it at all. ESI keeps the installer on corporation jobs too,
/// so that is who each job is listed under. Delivering is still one sweep through the industry
/// window: one task per character, not per facility. A corporation job a corp-mate delivers first
/// simply drops out at the next jobs poll.</para>
///
/// <para>⚠️ Finished is judged as the alert judges it: ESI marks a finished job "ready" only
/// sometimes, and more often leaves it "active" past its end date, so both count. A job installed
/// by someone who is not one of the user's characters is left out — nobody here can deliver it.</para>
/// </summary>
public class CompletedJobsGenerator(IDbContextFactory<AppDbContext> dbFactory) : IWorklistGenerator
{
    public string Id          => "completed_jobs";
    public string DisplayName => WorklistText.SourceCompletedJobs;

    public async Task<List<WorklistItem>> GenerateAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var now = DateTimeOffset.UtcNow;
        // ⚠️ The date test in memory: a DateTimeOffset in a Where does not translate on SQLite.
        // A corp job comes back under the corporation and under its installer: one per job.
        var jobs = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Status == "ready" || j.Status == "active")
                .Select(j => new { j.JobId, j.Status, j.EndDate, j.InstallerId, j.FacilityId, j.ActivityId,
                                   j.ProductTypeId, j.BlueprintTypeId, j.Runs })
                .ToListAsync(ct))
            .Where(j => j.Status == "ready" || j.EndDate <= now)
            .DistinctBy(j => j.JobId)
            .ToList();
        if (jobs.Count == 0) return [];

        var pilots = await db.Characters.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        jobs = jobs.Where(j => pilots.ContainsKey(j.InstallerId)).ToList();
        if (jobs.Count == 0) return [];

        var productIds = jobs.Select(j => j.ProductTypeId ?? j.BlueprintTypeId).Distinct().ToList();
        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => productIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // Units a run makes, for what comes out: a reaction run is thousands.
        var bpIds  = jobs.Select(j => j.BlueprintTypeId).Distinct().ToList();
        var perRun = (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => bpIds.Contains(p.TypeId) && (p.Activity == "manufacturing" || p.Activity == "reaction"))
                .Select(p => new { p.TypeId, p.ProductTypeId, p.Quantity })
                .ToListAsync(ct))
            .GroupBy(p => (p.TypeId, p.ProductTypeId))
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(p => p.Quantity)));

        var outputs = jobs
            .Select(j =>
            {
                var typeId = j.ProductTypeId ?? j.BlueprintTypeId;
                return (j.JobId, j.FacilityId, j.InstallerId, TypeId: typeId, j.ActivityId,
                        Units: (long)j.Runs * perRun.GetValueOrDefault((j.BlueprintTypeId, typeId), 1));
            })
            .ToList();

        var places = await PlaceNamesAsync(db, ct);
        string PlaceName(long id) => places.GetValueOrDefault(id, string.Format(WorklistText.LocationWithId, id));

        // A task for each character with jobs to deliver, in name order.
        return [.. outputs
            .GroupBy(o => o.InstallerId)
            .OrderBy(g => pilots[g.Key], StringComparer.CurrentCulture)
            .Select(mine => TaskFor(mine.Key, pilots[mine.Key], [.. mine]))];

        WorklistItem TaskFor(long pilotId, string pilotName,
            List<(int JobId, long FacilityId, long InstallerId, int TypeId, int ActivityId, long Units)> mine)
        {
            // What comes out, by item and by the station it is waiting at. A copy job's copies and
            // an invention's prints are copies, research hands the original back (see PrintLabel).
            var lines = mine
                .GroupBy(o => (o.FacilityId, o.TypeId, o.ActivityId))
                .Select(x => new WorklistLine(
                    x.Key.TypeId,
                    names.GetValueOrDefault(x.Key.TypeId, string.Format(WorklistText.TypeWithId, x.Key.TypeId)),
                    x.Sum(o => o.Units))
                    {
                        Place = PlaceName(x.Key.FacilityId),
                        Print = x.Key.ActivityId switch
                        {
                            5 or 8 => PrintKind.Copy,
                            3 or 4 => PrintKind.Original,
                            _      => PrintKind.None,
                        },
                    })
                .OrderBy(l => l.Place, StringComparer.CurrentCulture)
                .ThenByDescending(l => l.Quantity)
                .ToList();

            // For the sentence, an item once, wherever it is waiting; largest first.
            var totals = lines
                .GroupBy(l => (l.TypeId, l.Print))
                .Select(g => g.First() with { Quantity = g.Sum(l => l.Quantity) })
                .OrderByDescending(l => l.Quantity)
                .ToList();
            var shown = totals.Take(4).Select(l => $"{ItemRef.Of(l.TypeId, l.TypeName, l.Print)} ×{l.Quantity:N0}");
            // One item: the row is about it, for its picture and link. Several: about none of them.
            var only  = totals.Count == 1 ? totals[0] : null;
            // One station: the task is at it. Several: at none, and each line says where.
            var sites = mine.Select(o => o.FacilityId).Distinct().ToList();

            return new WorklistItem
            {
                Key           = $"completed_jobs:{pilotId}",
                Source        = Id,
                Kind          = WorklistKind.Job,
                Title         = Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.DeliverJobsTitleOther), mine.Count),
                Detail        = string.Format(WorklistText.DeliverJobsDetail,
                                    string.Join(CommonText.ListSeparator, shown)
                                    + (totals.Count > 4 ? " " + string.Format(WorklistText.DeliverJobsMore, totals.Count - 4) : "")),
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = pilotId,
                CharacterName = pilotName,
                LocationId    = sites.Count == 1 ? sites[0] : 0,
                LocationName  = sites.Count == 1 ? PlaceName(sites[0]) : "",
                TypeId        = only?.TypeId ?? 0,
                TypeName      = only?.TypeName ?? "",
                Print         = only?.Print ?? PrintKind.None,
                Lines         = lines,
                Delivers      = [.. mine.Select(o => new WorklistDelivered(o.FacilityId, o.TypeId, o.Units))],
                Priority      = WorklistPriority.DeliverJobs,
            };
        }
    }

    /// <summary>Places as the screen names them.</summary>
    private static async Task<Dictionary<long, string>> PlaceNamesAsync(AppDbContext db, CancellationToken ct)
    {
        var map = (await db.SdeStations.AsNoTracking()
                .Select(s => new { Id = (long)s.StationId, s.Name }).ToListAsync(ct))
            .ToDictionary(s => s.Id, s => SdeNames.Station(s.Id, s.Name));
        foreach (var s in await db.EsiStructureNames.AsNoTracking()
                     .Where(s => s.Name != "")
                     .Select(s => new { s.StructureId, s.Name }).ToListAsync(ct))
            map[s.StructureId] = s.Name;
        return map;
    }
}
