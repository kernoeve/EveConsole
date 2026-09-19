using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One delivered job's output: in a hangar in the game, and not yet in the asset snapshot.</summary>
/// <param name="Site">The facility the job ran in, which is where the output landed. The job's
/// own output location is a hangar or container inside it and resolves to no structure.</param>
/// <param name="OwnerType">Whose hangar it landed in — the corporation's for a corp job, the
/// installer's otherwise.</param>
public sealed record DeliveredOutput(
    int JobId, long Site, string OwnerType, long OwnerId, int TypeId, long Units, DateTimeOffset DeliveredAt);

/// <summary>
/// The copies a delivered copy or invention job produced: in a hangar in the game, and not yet in
/// the blueprint snapshot.
/// </summary>
public sealed record DeliveredPrint(
    int JobId, long Site, string OwnerType, long OwnerId, int TypeId, int Copies, int RunsEach, int Me, int Te,
    DateTimeOffset DeliveredAt);

/// <summary>
/// What delivered jobs have put into hangars that the polls have not caught up with.
///
/// <para>⚠️ Assets and blueprints are polled hourly; industry jobs every five minutes. Delivering
/// a job puts its output in the hangar at once and, minutes later, the job stops counting as
/// "in build" — but for up to an hour the asset snapshot still shows nothing of it. In that
/// window the output is nowhere the planner can see: a component just delivered reads as
/// missing, the job that made it reads as gone, and the list raises a job to build it again.
/// The mirror image of the started-job correction (IndustryDemandService.AlreadyConsumedAsync),
/// and settled the same way — measured against the snapshot clock of the job's OWN owner, since
/// a corp job's output lands in the corp hangar and it is the corporation's poll that would show
/// it.</para>
///
/// <para>Delivery time is the job's completed date. Checked on 2,128 delivered jobs: never before
/// the job's end date, and the median delivery came six hours after the job finished — which is
/// why the end date would be the wrong clock.</para>
/// </summary>
public static class DeliveryLag
{
    /// <summary>Manufactured and reacted output delivered since the owner's last asset poll.</summary>
    /// <param name="typeIds">Only these products, where the caller has a list; null for every product.</param>
    public static async Task<List<DeliveredOutput>> ItemsAsync(
        AppDbContext db, CancellationToken ct, IReadOnlyCollection<int>? typeIds = null)
    {
        // Inside a worklist build every generator asks this, each for its own types, and the
        // answer is a few dozen rows: load the lot once and hand each caller its slice. Outside
        // a build, the filtered query as before.
        if (!Worklist.BuildCache.IsActive) return await ItemsUncachedAsync(db, ct, typeIds);

        var all = await Worklist.BuildCache.GetOrAddAsync("DeliveryLag.Items", () => ItemsUncachedAsync(db, ct, null));
        if (typeIds is null) return all;
        if (typeIds.Count == 0) return [];
        var set = typeIds as IReadOnlySet<int> ?? typeIds.ToHashSet();
        return all.Where(d => set.Contains(d.TypeId)).ToList();
    }

    private static async Task<List<DeliveredOutput>> ItemsUncachedAsync(
        AppDbContext db, CancellationToken ct, IReadOnlyCollection<int>? typeIds)
    {
        var snapshot = await SnapshotAsync(db, "char.assets", "corp.assets", ct);
        if (snapshot.Count == 0) return [];

        // Manufacturing (1) and reactions (9, and the legacy 11) put items in a hangar; the
        // other activities deliver a print, which PrintsAsync covers.
        var q = db.EsiIndustryJobs.AsNoTracking()
            .Where(j => j.Status == "delivered" && j.CompletedDate != null && j.ProductTypeId != null
                     && (j.ActivityId == 1 || j.ActivityId == 9 || j.ActivityId == 11));
        if (typeIds is not null)
        {
            if (typeIds.Count == 0) return [];
            var ids = typeIds.ToList();
            q = q.Where(j => ids.Contains(j.ProductTypeId!.Value));
        }

        var rows = await q
            .Select(j => new { j.JobId, j.OwnerType, j.OwnerId, j.FacilityId, j.ProductTypeId,
                               j.Runs, j.BlueprintTypeId, j.CompletedDate })
            .ToListAsync(ct);

        // ⚠️ Deduped BEFORE the snapshot test. A corp job comes back under the corporation and
        // under the installer; the corporation's row is the one whose hangar the output went to,
        // and it is that owner's poll the delivery has to be measured against. Testing each row
        // on its own could credit the output under the installer after the corporation's poll
        // had already counted it.
        //
        // ⚠️ Compared in memory: a DateTimeOffset in a Where does not translate on SQLite.
        var jobs = rows
            .GroupBy(j => j.JobId)
            .Select(g => g.FirstOrDefault(j => j.OwnerType == "corporation") ?? g.First())
            .Where(j => snapshot.TryGetValue(j.OwnerId, out var taken) && j.CompletedDate > taken)
            .ToList();
        if (jobs.Count == 0) return [];

        // ⚠️ Runs times what a run yields, not runs: a reaction run is thousands of units.
        var yield = await YieldAsync(db, jobs.Select(j => j.BlueprintTypeId).Distinct().ToList(), ct);

        return jobs
            .Select(j => new DeliveredOutput(
                j.JobId, j.FacilityId, j.OwnerType, j.OwnerId, j.ProductTypeId!.Value,
                (long)j.Runs * yield.GetValueOrDefault((j.BlueprintTypeId, j.ProductTypeId!.Value), 1L),
                j.CompletedDate!.Value))
            .ToList();
    }

    /// <summary>
    /// Copies delivered by copy and invention jobs since the owner's last blueprint poll.
    ///
    /// <para>A copy job says exactly what it made: successful runs is the number of copies (every
    /// run of a copy job succeeds) and licensed runs the runs each carries, and each inherits the
    /// research of the print it was taken from — checked against the copies that later appeared.
    /// An invention job says how many attempts succeeded, but its licensed runs are the runs of
    /// the copy it CONSUMED, not of what it made: measured on 49 delivered inventions, not one
    /// output print carried that figure. The invented print's runs are the SDE's base for the
    /// invention (the T2 print's quantity on the T1 print's invention activity), and it is
    /// credited at the base ME 2 / TE 4, because which decryptor was used is not on the job. A
    /// decryptor only ever adds runs and mostly adds ME, so for the hour until the blueprint poll
    /// shows the real print the credit is on the low side — the safe direction.</para>
    /// </summary>
    /// <param name="typeIds">Only these blueprint types, where the caller has a list; null for all.</param>
    public static async Task<List<DeliveredPrint>> PrintsAsync(
        AppDbContext db, CancellationToken ct, IReadOnlyCollection<int>? typeIds = null)
    {
        // Once per build and sliced per caller, as ItemsAsync.
        if (!Worklist.BuildCache.IsActive) return await PrintsUncachedAsync(db, ct, typeIds);

        var all = await Worklist.BuildCache.GetOrAddAsync("DeliveryLag.Prints", () => PrintsUncachedAsync(db, ct, null));
        if (typeIds is null) return all;
        if (typeIds.Count == 0) return [];
        var set = typeIds as IReadOnlySet<int> ?? typeIds.ToHashSet();
        return all.Where(p => set.Contains(p.TypeId)).ToList();
    }

    private static async Task<List<DeliveredPrint>> PrintsUncachedAsync(
        AppDbContext db, CancellationToken ct, IReadOnlyCollection<int>? typeIds)
    {
        var snapshot = await SnapshotAsync(db, "char.blueprints", "corp.blueprints", ct);
        if (snapshot.Count == 0) return [];

        var q = db.EsiIndustryJobs.AsNoTracking()
            .Where(j => j.Status == "delivered" && j.CompletedDate != null && j.ProductTypeId != null
                     && ((j.ActivityId == 5 && j.LicensedRuns != null) || j.ActivityId == 8));
        if (typeIds is not null)
        {
            if (typeIds.Count == 0) return [];
            var ids = typeIds.ToList();
            q = q.Where(j => ids.Contains(j.ProductTypeId!.Value));
        }

        var rows = await q
            .Select(j => new { j.JobId, j.OwnerType, j.OwnerId, j.FacilityId, j.ProductTypeId, j.BlueprintId,
                               j.BlueprintTypeId, j.ActivityId, j.Runs, j.LicensedRuns, j.SuccessfulRuns, j.CompletedDate })
            .ToListAsync(ct);

        var jobs = rows
            .GroupBy(j => j.JobId)
            .Select(g => g.FirstOrDefault(j => j.OwnerType == "corporation") ?? g.First())
            .Where(j => snapshot.TryGetValue(j.OwnerId, out var taken) && j.CompletedDate > taken)
            .ToList();
        if (jobs.Count == 0) return [];

        // The research on the print each copy job ran from, which its copies inherit.
        var sourceIds = jobs.Where(j => j.ActivityId == 5).Select(j => j.BlueprintId).Distinct().ToList();
        var research  = sourceIds.Count == 0
            ? new Dictionary<long, (int Me, int Te)>()
            : await db.EsiBlueprints.AsNoTracking()
                .Where(b => sourceIds.Contains(b.ItemId))
                .GroupBy(b => b.ItemId)
                .Select(g => new { ItemId = g.Key, Me = g.Max(b => b.MaterialEfficiency), Te = g.Max(b => b.TimeEfficiency) })
                .ToDictionaryAsync(x => x.ItemId, x => (x.Me, x.Te), ct);

        // The runs an invention yields, from the SDE.
        var inventedFrom = jobs.Where(j => j.ActivityId == 8).Select(j => j.BlueprintTypeId).Distinct().ToList();
        var inventedRuns = inventedFrom.Count == 0
            ? new Dictionary<(int, int), int>()
            : (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => p.Activity == "invention" && inventedFrom.Contains(p.TypeId))
                .Select(p => new { p.TypeId, p.ProductTypeId, p.Quantity })
                .ToListAsync(ct))
              .GroupBy(p => (p.TypeId, p.ProductTypeId))
              .ToDictionary(g => g.Key, g => g.Max(p => p.Quantity));

        var result = new List<DeliveredPrint>();
        foreach (var j in jobs)
        {
            var isCopy = j.ActivityId == 5;
            var copies = isCopy ? (j.SuccessfulRuns ?? j.Runs) : (j.SuccessfulRuns ?? 0);
            var runs   = isCopy ? (j.LicensedRuns ?? 0)
                                : inventedRuns.GetValueOrDefault((j.BlueprintTypeId, j.ProductTypeId!.Value));
            if (copies <= 0 || runs <= 0) continue;

            var (me, te) = isCopy ? research.GetValueOrDefault(j.BlueprintId, (0, 0)) : (2, 4);
            result.Add(new DeliveredPrint(
                j.JobId, j.FacilityId, j.OwnerType, j.OwnerId, j.ProductTypeId!.Value,
                copies, runs, me, te, j.CompletedDate!.Value));
        }
        return result;
    }

    /// <summary>When each owner's copy of the given resource was last read — the clock a delivery is measured against.</summary>
    private static async Task<Dictionary<long, DateTimeOffset>> SnapshotAsync(
        AppDbContext db, string characterEndpoint, string corporationEndpoint, CancellationToken ct) =>
        (await db.EsiCallRecords.AsNoTracking()
            .Where(r => r.Endpoint == characterEndpoint || r.Endpoint == corporationEndpoint)
            .Select(r => new { r.OwnerId, r.LastCalledAt })
            .ToListAsync(ct))
        .GroupBy(r => r.OwnerId)
        .ToDictionary(g => g.Key, g => g.Max(r => r.LastCalledAt));

    /// <summary>Units one run of each blueprint yields of each product; one where the SDE says nothing.</summary>
    private static async Task<Dictionary<(int Blueprint, int Product), long>> YieldAsync(
        AppDbContext db, List<int> blueprintTypeIds, CancellationToken ct) =>
        (await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => blueprintTypeIds.Contains(p.TypeId))
            .Select(p => new { p.TypeId, p.ProductTypeId, p.Quantity })
            .ToListAsync(ct))
        .GroupBy(p => (p.TypeId, p.ProductTypeId))
        .ToDictionary(g => g.Key, g => (long)Math.Max(1, g.Max(p => p.Quantity)));
}
