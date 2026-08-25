using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// Work stopped for want of material that is already owned, sitting somewhere else.
///
/// <para>The other half of Item Contention. That tab reports material nothing owns enough of,
/// which is a purchase or a build; this one reports material that exists and is in the wrong
/// place, which is a hauler. Reading the two as one list invited the mistake the split exists to
/// prevent: sending somebody shopping for something already in a hangar three jumps away.</para>
///
/// <para>⚠️ One row per DESTINATION, not per item. The reader's next action is a trip, and a trip
/// is defined by where it ends — five jobs at one station short of four different things is one
/// errand, and five rows would read as five.</para>
/// </summary>
/// <param name="StationId">Where the jobs would run, and where the material has to arrive.</param>
/// <param name="BlockedTasks">Tasks at this station stopped for want of material held elsewhere.</param>
/// <param name="StalledTasks">Those, plus everything stopped behind what they would have made.</param>
/// <param name="HaulTasks">Hauls already on the worklist bringing any of these items here. Zero
/// is the finding: the material exists, the work is stopped, and nothing is moving it.</param>
/// <param name="ItemTypes">Distinct items being waited on.</param>
/// <param name="Volume">Cubic metres of the shortfall, so the trip can be sized before it is
/// planned.</param>
/// <param name="Sources">Distinct places the material sits, excluding the destination itself.</param>
public sealed record HaulPressure(
    long   StationId,
    string StationName,
    int    BlockedTasks,
    int    StalledTasks,
    int    HaulTasks,
    int    ItemTypes,
    double Volume,
    int    Sources,
    IReadOnlyList<ShortageTask> Tasks)
{
    /// <summary>
    /// What the row amounts to.
    ///
    /// <para>The distinction that matters is whether anything is already moving. A haul on the
    /// list is work in progress; no haul at all is work nobody has raised, and the same blocked
    /// jobs mean something different in each case.</para>
    /// </summary>
    public string Verdict =>
        BlockedTasks <= 0 ? "Clear"
      : HaulTasks   <= 0 ? "Nothing moving"
      : Sources      > 1 ? "Several trips"
      :                    "Haul raised";

    public string Advice => Verdict switch
    {
        "Nothing moving" =>
            $"{Stopped} for want of {ItemTypes:N0} item(s) already owned, sitting at "
          + $"{Sources:N0} other place(s) — {Volume:N0} m3 in all. No haul on the list brings any "
          + "of it here, so nothing about this changes on its own.",

        "Several trips" =>
            $"{Stopped} for want of {ItemTypes:N0} item(s) held at {Sources:N0} different places, "
          + $"{Volume:N0} m3 in all, with {HaulTasks:N0} haul(s) already raised. More than one "
          + "pickup, so the work stays stopped until the last of them lands.",

        "Haul raised" =>
            $"{Stopped}, and {HaulTasks:N0} haul(s) are already raised to bring the {Volume:N0} m3 "
          + "here. Nothing further to decide — the work starts when the material arrives.",

        _ => "Nothing here is stopped for want of a trip.",
    };

    private string Stopped =>
        StalledTasks > BlockedTasks
            ? $"{StalledTasks:N0} task(s) stopped, {BlockedTasks:N0} of them here"
            : $"{BlockedTasks:N0} task(s) stopped";
}

/// <summary>Builds the Hauling rows from the worklist and the asset table.</summary>
public class HaulPressureService(
    IDbContextFactory<AppDbContext> dbFactory,
    WorklistSettings                settings)
{
    public async Task<List<HaulPressure>> PressuresAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        // ⚠️ MustBuy false is the whole selection: owned within the scope, just not where the
        // job is. Its opposite is Item Contention's list, and nothing belongs on both.
        var short_ = items
            .SelectMany(i => i.Shortages.Where(s => !s.MustBuy).Select(s => (Item: i, Short: s)))
            .Where(x => x.Item.LocationId > 0)
            .ToList();
        if (short_.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var index = TaskChain.Index(items);
        var ids   = short_.Select(x => x.Short.TypeId).Distinct().ToList();

        var volumes = await db.SdeTypes.AsNoTracking()
            .Where(t => ids.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Volume, ct);

        // Where the material actually is. Scoped as the generator scopes it: stock the plan
        // cannot reach is not a pickup, it is somebody else's.
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);

        var held = (await db.EsiAssets.AsNoTracking()
                .Where(a => ids.Contains(a.TypeId) && a.Quantity > 0)
                .Select(a => new { a.TypeId, a.RootLocationId })
                .ToListAsync(ct))
            .Where(a => scope is null || scope.Contains(a.RootLocationId))
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.RootLocationId).ToHashSet());

        // Hauls already raised, by where they are going and what they carry.
        var hauls = items
            .Where(i => i.Kind == WorklistKind.Haul && i.DestinationId > 0)
            .Select(i => (
                i.DestinationId,
                i.Title,
                i.Readiness,
                Types: i.Lines.Count > 0
                    ? i.Lines.Select(l => l.TypeId).ToHashSet()
                    : new HashSet<int> { i.TypeId }))
            .ToList();

        var rows = new List<HaulPressure>();

        foreach (var g in short_.GroupBy(x => x.Item.LocationId))
        {
            var here  = g.Key;
            var types = g.Select(x => x.Short.TypeId).Distinct().ToList();

            // ⚠️ The chain is walked per ITEM and then de-duplicated by task. Summing the walks
            // counts a job short of three of these three times, and what a reader acts on is how
            // much work is stopped, not how many ways it is stopped.
            var chain = types
                .SelectMany(t => TaskChain.Stalled(index, t))
                .GroupBy(t => t.Title + "|" + t.Why)
                .Select(x => x.First())
                .OrderBy(t => t.Hop)
                .ToList();

            var sources = types
                .SelectMany(t => held.GetValueOrDefault(t, []))
                .Where(l => l != here)
                .Distinct()
                .Count();

            var moving = hauls
                .Where(h => h.DestinationId == here && h.Types.Overlaps(types))
                .ToList();

            var haulTasks = moving
                .Select(h => new ShortageTask(
                    "Hauling", -1, "", h.Title, h.Readiness.ToString(), "already on the list"))
                .ToList();

            rows.Add(new HaulPressure(
                here,
                g.First().Item.LocationName,
                g.Select(x => x.Item.Key).Distinct().Count(),
                chain.Count,
                moving.Count,
                types.Count,
                g.Sum(x => x.Short.Short * volumes.GetValueOrDefault(x.Short.TypeId)),
                sources,
                [.. chain, .. haulTasks]));
        }

        // Stopped work first, then the trips nobody has raised: a station with hauls moving is
        // waiting, and one with none is waiting for somebody to notice.
        return [.. rows
            .OrderByDescending(r => r.StalledTasks)
            .ThenByDescending(r => r.HaulTasks == 0)
            .ThenByDescending(r => r.BlockedTasks)];
    }
}
