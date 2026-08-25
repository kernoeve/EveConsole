using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>
/// One material the pipeline keeps running out of, and why.
/// </summary>
/// <param name="UsedPerDay">Drawn down per day across EVERY consumer, from the job history. An
/// item eaten by four different parents is short four times over, and a rate measured against one
/// of them would size its buffer for a quarter of the truth.</param>
/// <param name="MadePerDay">Produced per day over the same window. Zero for something bought.</param>
/// <param name="Level">What the inventory levels ask to keep on the shelf. Zero where none is set,
/// which is itself worth reporting: an item nothing asks to stock cannot absorb anything.</param>
/// <param name="Blocks">Items downstream waiting on this one, transitively — how much stops
/// moving while it is short.</param>
public sealed record ItemShortage(
    int    TypeId,
    string Name,
    double UsedPerDay,
    double MadePerDay,
    long   Level,
    long   OnHand,
    int    BlockedJobs,
    int    Blocks,
    bool   MustBuy,
    bool   Buildable,
    int    WindowDays)
{
    /// <summary>
    /// Days the shelf would last at the rate it is being drawn down.
    ///
    /// <para>⚠️ The figure a buffer should be judged on, not the stock level. Two hundred of
    /// something eaten twice a day is a hundred days of cushion; two hundred of something eaten
    /// two hundred times a day is a single day, and the stock number reads the same either way.
    /// Infinite when nothing consumes it.</para>
    /// </summary>
    public double DaysOfCover => UsedPerDay <= 0 ? double.PositiveInfinity : Level / UsedPerDay;

    /// <summary>
    /// Made per unit consumed. Below one and the shelf is draining however full it looks.
    ///
    /// <para><b>⚠️ This is the ranking, not the stock level.</b> A full buffer running at a
    /// sustained deficit is a worse problem than an empty one in equilibrium: no size of buffer
    /// survives being drawn down faster than it refills, and enlarging it only moves the date.</para>
    /// </summary>
    public double Balance => UsedPerDay <= 0 ? 1 : MadePerDay / UsedPerDay;

    /// <summary>Draining rather than cycling — production is not keeping up with consumption.</summary>
    public bool IsDraining => Buildable && UsedPerDay > 0 && Balance < 0.9;

    /// <summary>Days until the shelf is empty at the current deficit, if nothing changes.</summary>
    public double DaysToEmpty
    {
        get
        {
            var deficit = UsedPerDay - MadePerDay;
            return deficit <= 0 ? double.PositiveInfinity : OnHand / deficit;
        }
    }

    /// <summary>
    /// ⚠️ The rate is itself throttled by the shortage being measured, so it is a floor.
    ///
    /// <para>Nothing consumed what could not be made. If this item has blocked work, then what it
    /// was used for is what got through despite the blockage, not what was wanted — so every
    /// figure derived from it under-states, and a buffer sized on it is sized for the constrained
    /// world rather than the one worth having.</para>
    /// </summary>
    public bool RateSuppressed => BlockedJobs > 0;

    public string Verdict =>
        !Buildable && MustBuy ? "Buy"
      : IsDraining            ? "Making too few"
      : Level <= 0            ? "No level set"
      : DaysOfCover < 7       ? "Buffer thin"
      :                         "Holding";

    public string Advice => Verdict switch
    {
        "Buy" =>
            $"None owned, and nothing here makes it — {BlockedJobs:N0} job(s) are waiting on a "
          + "purchase.",

        "Making too few" =>
            $"Consumed {UsedPerDay:N1}/day and produced {MadePerDay:N1}/day — "
          + $"{(UsedPerDay <= 0 ? 0 : UsedPerDay / Math.Max(MadePerDay, 0.0001)):N1}× faster than "
          + "it is replaced, so the shelf drains whatever it is set to. "
          + (double.IsInfinity(DaysToEmpty) ? "" : $"Empty in about {DaysToEmpty:N0} day(s). ")
          + "Making more is the fix; a bigger buffer only moves the date.",

        "No level set" =>
            $"Consumed {UsedPerDay:N1}/day with nothing asking to keep any on the shelf, so there "
          + "is no cushion to absorb a run of demand.",

        "Buffer thin" =>
            $"The level of {Level:N0} is {DaysOfCover:N1} day(s) at {UsedPerDay:N1}/day. Anything "
          + "that takes longer than that to replace will block before it arrives.",

        _ =>
            $"{DaysOfCover:N1} day(s) of cover at {UsedPerDay:N1}/day, replaced at "
          + $"{MadePerDay:N1}/day.",
    };

    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);
}

/// <summary>
/// Which materials the pipeline keeps running out of, how fast they drain, and whether the answer
/// is to buy them, make more of them, or keep more on the shelf.
///
/// <para><b>⚠️ A buffer is a length of time, not a quantity.</b> Two hundred of something eaten
/// twice a day is a hundred days of cushion; two hundred of something eaten two hundred times a
/// day is one day. Everything here is a rate or the days a rate implies, because the stock number
/// on its own says nothing about whether the level is right.</para>
///
/// <para><b>⚠️ Material that exists but sits elsewhere is not a shortage.</b> The generator already
/// separates "none owned" from "owned, at the wrong station" and only the first belongs here — the
/// second is a hauling problem, and shopping for material already in your own hangar is the
/// expensive way to be wrong.</para>
///
/// <para><b>⚠️ Every consumption rate here is a floor.</b> Nothing consumed what could not be made,
/// so an item that has been blocking work was used at the rate the blockage allowed rather than
/// the rate that was wanted. Fix the constraint and the number rises — which means a buffer sized
/// on today's figure is sized for today's constrained pipeline.</para>
/// </summary>
public class ItemContentionService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>How far back rates are measured. Matches the Bottlenecks tab's other windows.</summary>
    private const int WindowDays = 90;

    public async Task<List<ItemShortage>> ShortagesAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        // ⚠️ Only genuine shortages. A material sitting at another station stops this job just as
        // firmly, but the fix is a hauler rather than a purchase or a build, and the hauling view
        // is where it belongs.
        var short_ = items
            .SelectMany(i => i.Shortages.Where(s => s.MustBuy).Select(s => (Item: i, Short: s)))
            .ToList();
        if (short_.Count == 0) return [];

        var blockedBy = short_
            .GroupBy(x => x.Short.TypeId)
            .ToDictionary(g => g.Key, g => (Name: g.First().Short.TypeName, Jobs: g.Count()));

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var (used, made) = await RatesAsync(db, ct);
        var ids          = blockedBy.Keys.ToList();

        var level = (await db.InvLevelItems.AsNoTracking()
                .Join(db.InvLevelGroups.AsNoTracking(), i => i.GroupId, g => g.Id,
                      (i, g) => new { i.TypeId, Target = i.TargetQuantity * (g.Multiplier <= 0 ? 1 : g.Multiplier) })
                .Where(x => ids.Contains(x.TypeId))
                .ToListAsync(ct))
            .GroupBy(x => x.TypeId)
            .ToDictionary(g => g.Key, g => (long)g.Max(x => x.Target));

        var onHand = (await db.EsiAssets.AsNoTracking()
                .Where(a => ids.Contains(a.TypeId))
                .Select(a => new { a.TypeId, a.Quantity })
                .ToListAsync(ct))
            .GroupBy(a => a.TypeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => (long)a.Quantity));

        // Whether anything here makes it at all: a buy problem and a build problem read the same
        // on the row that reports them, and the answers are nothing alike.
        var buildable = (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => (p.Activity == "manufacturing" || p.Activity == "reaction")
                         && ids.Contains(p.ProductTypeId))
                .Select(p => p.ProductTypeId)
                .ToListAsync(ct))
            .ToHashSet();

        // How much waits on it, carried from the demand walk rather than counted again here.
        var blocksOf = items
            .Where(i => i.TypeId > 0)
            .GroupBy(i => i.TypeId)
            .ToDictionary(g => g.Key, g => g.Max(i => i.Blocks));

        return blockedBy
            .Select(kv => new ItemShortage(
                kv.Key,
                kv.Value.Name,
                used.GetValueOrDefault(kv.Key) / WindowDays,
                made.GetValueOrDefault(kv.Key) / WindowDays,
                level.GetValueOrDefault(kv.Key),
                onHand.GetValueOrDefault(kv.Key),
                kv.Value.Jobs,
                blocksOf.GetValueOrDefault(kv.Key),
                MustBuy: true,
                Buildable: buildable.Contains(kv.Key),
                WindowDays))
            // Draining first, then by how much waits on it. A shelf being drawn down faster than
            // it refills is the problem that gets worse on its own; everything else is holding.
            .OrderByDescending(s => s.IsDraining)
            .ThenByDescending(s => s.Blocks)
            .ThenByDescending(s => s.BlockedJobs)
            .ToList();
    }

    /// <summary>
    /// What each type was consumed and produced at over the window, from the jobs themselves.
    ///
    /// <para>⚠️ Consumption is summed across every blueprint that eats it. An item drawn on by four
    /// parents is short four times over, and a rate measured against one of them sizes its buffer
    /// for a quarter of the truth.</para>
    ///
    /// <para>Material quantities are the SDE's base figures. Material efficiency shaves perhaps a
    /// tenth off them, which matters for a bill of materials and not for deciding whether a shelf
    /// holds days or weeks.</para>
    /// </summary>
    private static async Task<(Dictionary<int, double> Used, Dictionary<int, double> Made)>
        RatesAsync(AppDbContext db, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);

        // ⚠️ The window is applied in memory: StartDate is a DateTimeOffset over a TEXT column and
        // EF cannot translate a comparison on one against SQLite.
        var jobs = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Runs > 0)
                .Select(j => new { j.BlueprintTypeId, j.ProductTypeId, j.Runs, j.StartDate })
                .ToListAsync(ct))
            .Where(j => j.StartDate >= since)
            .ToList();
        if (jobs.Count == 0) return ([], []);

        var bpIds = jobs.Select(j => j.BlueprintTypeId).Distinct().ToList();

        var mats = (await db.SdeBlueprintMaterials.AsNoTracking()
                .Where(m => (m.Activity == "manufacturing" || m.Activity == "reaction")
                         && bpIds.Contains(m.TypeId))
                .Select(m => new { m.TypeId, m.MaterialTypeId, m.Quantity })
                .ToListAsync(ct))
            .GroupBy(m => m.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(m => (m.MaterialTypeId, (long)m.Quantity)).ToList());

        var perRun = (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => (p.Activity == "manufacturing" || p.Activity == "reaction")
                         && bpIds.Contains(p.TypeId))
                .Select(p => new { p.TypeId, p.Quantity })
                .ToListAsync(ct))
            .GroupBy(p => p.TypeId)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(p => p.Quantity)));

        var used = new Dictionary<int, double>();
        var made = new Dictionary<int, double>();

        foreach (var j in jobs)
        {
            if (mats.TryGetValue(j.BlueprintTypeId, out var list))
                foreach (var (mat, qty) in list)
                    used[mat] = used.GetValueOrDefault(mat) + (double)qty * j.Runs;

            if (j.ProductTypeId is int p)
                made[p] = made.GetValueOrDefault(p)
                        + (double)j.Runs * perRun.GetValueOrDefault(j.BlueprintTypeId, 1);
        }

        return (used, made);
    }
}
