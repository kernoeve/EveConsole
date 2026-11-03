using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Finished industry jobs waiting to be delivered: one task per pilot and facility, at the top of
/// the jobs.
///
/// <para>A finished job still holds its slot, and its output is not in the hangar for anything
/// downstream until somebody delivers it — so it comes before starting anything new, which may well
/// be waiting on that very output or that very slot. The Overview's alert says how many; this says
/// who, where and what.</para>
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

        var places = await PlaceNamesAsync(db, ct);
        var items  = new List<WorklistItem>();

        foreach (var g in jobs.GroupBy(j => (j.InstallerId, j.FacilityId)))
        {
            // What comes out, by item: a copy job's copies and an invention's prints are copies,
            // research hands the original back (see PrintLabel).
            var lines = g
                .GroupBy(j => (TypeId: j.ProductTypeId ?? j.BlueprintTypeId, j.ActivityId))
                .Select(x => new WorklistLine(
                    x.Key.TypeId,
                    names.GetValueOrDefault(x.Key.TypeId, string.Format(WorklistText.TypeWithId, x.Key.TypeId)),
                    x.Sum(j => (long)j.Runs * perRun.GetValueOrDefault((j.BlueprintTypeId, x.Key.TypeId), 1)))
                    {
                        Print = x.Key.ActivityId switch
                        {
                            5 or 8 => PrintKind.Copy,
                            3 or 4 => PrintKind.Original,
                            _      => PrintKind.None,
                        },
                    })
                .OrderByDescending(l => l.Quantity)
                .ToList();
            var lead  = lines[0];
            var place = places.GetValueOrDefault(g.Key.FacilityId, string.Format(WorklistText.LocationWithId, g.Key.FacilityId));
            var shown = lines.Take(4).Select(l => $"{PrintLabel.Name(l.TypeId, l.TypeName, l.Print)} ×{l.Quantity:N0}");

            items.Add(new WorklistItem
            {
                Key           = $"completed_jobs:{g.Key.InstallerId}:{g.Key.FacilityId}",
                Source        = Id,
                Kind          = WorklistKind.Job,
                Title         = Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.DeliverJobsTitleOther), g.Count()),
                Detail        = string.Format(WorklistText.DeliverJobsDetail,
                                    string.Join(CommonText.ListSeparator, shown)
                                    + (lines.Count > 4 ? " " + string.Format(WorklistText.DeliverJobsMore, lines.Count - 4) : "")),
                Readiness     = WorklistReadiness.Ready,
                CharacterId   = g.Key.InstallerId,
                CharacterName = pilots[g.Key.InstallerId],
                LocationId    = g.Key.FacilityId,
                LocationName  = place,
                TypeId        = lead.TypeId,
                TypeName      = lead.TypeName,
                Print         = lead.Print,
                Lines         = lines,
                Priority      = WorklistPriority.DeliverJobs,
            });
        }

        return items;
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
