using EveConsole.Data;
using EveConsole.Localization;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Finished industry jobs waiting to be delivered: one task for all of them, at the top of the jobs.
///
/// <para>A finished job still holds its slot, and its output is not in the hangar for anything
/// downstream until somebody delivers it — so it comes before starting anything new, which may well
/// be waiting on that very output or that very slot. The Overview's alert says how many; this says
/// what.</para>
///
/// <para>⚠️ One task, on no station and no pilot. Delivering is one sweep through the industry
/// window, not an errand per place: a task per pilot and facility filled the top of the list with
/// the same click.</para>
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

        var pilots = await db.Characters.AsNoTracking().Select(c => c.Id).ToListAsync(ct);
        jobs = jobs.Where(j => pilots.Contains(j.InstallerId)).ToList();
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
                return (j.FacilityId, TypeId: typeId, j.ActivityId,
                        Units: (long)j.Runs * perRun.GetValueOrDefault((j.BlueprintTypeId, typeId), 1));
            })
            .ToList();

        // What comes out, by item: a copy job's copies and an invention's prints are copies,
        // research hands the original back (see PrintLabel).
        var lines = outputs
            .GroupBy(o => (o.TypeId, o.ActivityId))
            .Select(x => new WorklistLine(
                x.Key.TypeId,
                names.GetValueOrDefault(x.Key.TypeId, string.Format(WorklistText.TypeWithId, x.Key.TypeId)),
                x.Sum(o => o.Units))
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

        var shown = lines.Take(4).Select(l => $"{ItemRef.Of(l.TypeId, l.TypeName, l.Print)} ×{l.Quantity:N0}");
        // One item: the row is about it, for its picture and link. Several: about none of them.
        var only  = lines.Count == 1 ? lines[0] : null;

        return
        [
            new WorklistItem
            {
                Key       = "completed_jobs",
                Source    = Id,
                Kind      = WorklistKind.Job,
                Title     = Plurals.Format(WorklistText.ResourceManager, nameof(WorklistText.DeliverJobsTitleOther), jobs.Count),
                Detail    = string.Format(WorklistText.DeliverJobsDetail,
                                string.Join(CommonText.ListSeparator, shown)
                                + (lines.Count > 4 ? " " + string.Format(WorklistText.DeliverJobsMore, lines.Count - 4) : "")),
                Readiness = WorklistReadiness.Ready,
                TypeId    = only?.TypeId ?? 0,
                TypeName  = only?.TypeName ?? "",
                Print     = only?.Print ?? PrintKind.None,
                Lines     = lines,
                Delivers  = [.. outputs.Select(o => new WorklistDelivered(o.FacilityId, o.TypeId, o.Units))],
                Priority  = WorklistPriority.DeliverJobs,
            },
        ];
    }
}
