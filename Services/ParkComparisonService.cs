using EveConsole.Data;
using EveConsole.Models;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services;

/// <summary>One line of the comparison's job list: what to build, how many, at what blueprint ME.</summary>
public sealed record ParkCompareJob(int TypeId, string TypeName, long Quantity, int MeLevel);

/// <summary>
/// The share of a plan's job cost that one job's system cost index sets:
/// <c>Round(Weight × index[SystemId, Activity])</c>, which was <see cref="Gross"/> at today's index.
/// </summary>
public sealed record IndexTerm(int SystemId, string Activity, double Weight, decimal Gross);

/// <summary>
/// One job-list line built in one park: the whole chain, every step in the structure the park
/// gives that kind of job.
/// </summary>
/// <param name="Slot">The park's place in the list of every park, which picks its colour on the
/// chart: unticking another park does not repaint this one.</param>
/// <param name="Buildable">False when the park's plan does not build the item at all (it is
/// bought, or has no blueprint the calculator can use).</param>
public sealed record ParkCost(
    int ParkId, string ParkName, int Slot, ParkCompareJob Job, bool Buildable, long Produced,
    decimal Materials, decimal JobCost, decimal Total, IReadOnlyList<IndexTerm> Terms)
{
    public decimal PerUnit => Produced > 0 ? Total / Produced : 0;

    /// <summary>What the components made beyond need are worth: kept, not consumed, so the total
    /// is net of them, as the Production Calculator's Net Cost is.</summary>
    public decimal Leftovers => Materials + JobCost - Total;
}

/// <summary>
/// Builds the same items in several Indy Parks and sets the costs side by side, and works the
/// cost out again for each past day's system cost indices.
///
/// <para>⚠️ The costing is the Production Calculator's, untouched: one context per park, one
/// plan per item. Only the cost index moves over time. Each job records the part of its cost
/// the index sets (<see cref="PlanJob.IndexWeight"/>), so a past day is that part again at that
/// day's index — materials, taxes and the plan itself stay as calculated today.</para>
/// </summary>
public sealed class ParkComparisonService(ProductionCalculatorService calc, IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>Every park, in the Indy Parks list's order (the default first, then by name).</summary>
    public async Task<List<(int Id, string Name)>> ParksAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.IndyParks.AsNoTracking().OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
                    .Select(p => new { p.Id, p.Name }).ToListAsync(ct))
               .Select(p => (p.Id, p.Name)).ToList();
    }

    /// <summary>Every job-list line in every park given, park by park.</summary>
    public async Task<List<ParkCost>> CompareAsync(
        IReadOnlyList<ParkCompareJob> jobs, IReadOnlyList<(int Id, string Name, int Slot)> parks,
        CancellationToken ct = default)
    {
        var result = new List<ParkCost>();
        foreach (var park in parks)
        {
            ct.ThrowIfCancellationRequested();
            var ctx = await calc.LoadContextAsync(park.Id, ct);
            foreach (var job in jobs)
            {
                var plan = calc.Calculate(
                    [new ProductionQueueEntry { TypeId = job.TypeId, TypeName = job.TypeName, Quantity = job.Quantity, MeLevel = job.MeLevel }], ctx);
                var fp = plan.FinalProducts.FirstOrDefault(p => p.TypeId == job.TypeId);
                if (fp is null || !plan.AllJobs.Any(j => j.OutputTypeId == job.TypeId))
                {
                    result.Add(new ParkCost(park.Id, park.Name, park.Slot, job, false, 0, 0, 0, 0, []));
                    continue;
                }

                var terms = plan.AllJobs
                    .Where(j => j.IndexWeight > 0 && j.CostIndexSystemId > 0)
                    .Select(j => new IndexTerm(j.CostIndexSystemId, j.CostIndexActivity, j.IndexWeight, j.JobGross))
                    .ToList();
                result.Add(new ParkCost(park.Id, park.Name, park.Slot, job, true, fp.QuantityProduced,
                                        fp.TotalMaterialCost, fp.TotalJobCost, fp.TotalCost, terms));
            }
        }
        return result;
    }

    /// <summary>
    /// The cost again at another day's indices. A term whose index that day is not known keeps
    /// today's share, so a gap in the history bends the line no more than it has to.
    /// </summary>
    public static decimal TotalAt(ParkCost cost, Func<int, string, double?> indexOn)
    {
        var total = cost.Total;
        foreach (var t in cost.Terms)
            if (indexOn(t.SystemId, t.Activity) is { } index)
                total += Math.Round((decimal)(t.Weight * index), 0) - t.Gross;
        return total;
    }
}

/// <summary>
/// System cost indices, now and over time. The history is the map statistics' own record
/// (MapIndustryIndices): hourly for the last day, one sample a day before that.
/// </summary>
public sealed class CostIndexHistoryService(IDbContextFactory<AppDbContext> dbFactory)
{
    /// <summary>The six activities ESI reports an index for, in the order shown.</summary>
    public static readonly string[] Activities =
    [
        "manufacturing", "reaction", "invention", "copying",
        "researching_material_efficiency", "researching_time_efficiency",
    ];

    /// <summary>Today's indices: the snapshot the Production Calculator costs jobs with.</summary>
    public async Task<Dictionary<(int SystemId, string Activity), double>> CurrentAsync(
        IReadOnlyCollection<int> systems, CancellationToken ct = default)
    {
        if (systems.Count == 0) return [];
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.IndustryCostIndices.AsNoTracking()
                    .Where(i => systems.Contains(i.SolarSystemId))
                    .Select(i => new { i.SolarSystemId, i.Activity, i.CostIndex }).ToListAsync(ct))
               .GroupBy(i => (i.SolarSystemId, i.Activity))
               .ToDictionary(g => g.Key, g => g.First().CostIndex);
    }

    /// <summary>One value a day per system and activity: the day's samples averaged.</summary>
    public async Task<Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>>> DailyAsync(
        IReadOnlyCollection<int> systems, IReadOnlyCollection<string> activities, CancellationToken ct = default)
    {
        var result = new Dictionary<(int, string), SortedDictionary<DateOnly, double>>();
        if (systems.Count == 0 || activities.Count == 0) return result;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.MapIndustryIndices.AsNoTracking()
            .Where(i => systems.Contains(i.SystemId) && activities.Contains(i.Activity))
            .Select(i => new { i.SystemId, i.Activity, i.Bucket, i.CostIndex })
            .ToListAsync(ct);

        // Bucket is "yyyy-MM-dd HH", UTC.
        foreach (var g in rows.GroupBy(r => (r.SystemId, r.Activity)))
        {
            var byDay = new SortedDictionary<DateOnly, double>();
            foreach (var d in g.GroupBy(r => r.Bucket.Length >= 10 ? r.Bucket[..10] : r.Bucket))
                if (DateOnly.TryParseExact(d.Key, "yyyy-MM-dd", out var day))
                    byDay[day] = d.Average(r => r.CostIndex);
            result[g.Key] = byDay;
        }
        return result;
    }

    /// <summary>The value on a day, or the last one before it; null before the first.</summary>
    public static double? On(SortedDictionary<DateOnly, double>? series, DateOnly day)
    {
        if (series is null || series.Count == 0) return null;
        if (series.TryGetValue(day, out var v)) return v;
        double? last = null;
        foreach (var (d, value) in series)
        {
            if (d > day) break;
            last = value;
        }
        return last;
    }
}
