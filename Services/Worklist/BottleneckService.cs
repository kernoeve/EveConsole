using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>One way of getting more bandwidth, and what it would do to the backlog.</summary>
/// <param name="Detail">Who or what it applies to, named so the figure can be checked.</param>
public sealed record SlotRemedy(string Action, int Slots, string Detail)
{
    /// <summary>Share of current capacity this would add. Set by the caller, which knows it.</summary>
    public double PercentGain { get; init; }

    /// <summary>Days to work through the backlog once this is in place.</summary>
    public double ClearDaysAfter { get; init; }

    /// <summary>Days to work through it as things stand, for the comparison.</summary>
    public double ClearDaysNow { get; init; }

    public string Gain => Slots <= 0 ? "—"
                        : PercentGain > 0 ? $"+{Slots:N0}  ({PercentGain:N0}%)"
                        : $"+{Slots:N0}";

    /// <summary>
    /// What it buys in time, which is the only unit that answers the question.
    ///
    /// <para>⚠️ Not "starts N% of the queue". A queue is not a shopping list of slots — it is work
    /// that piled up because throughput sat below demand, and the fix is a rate, not one slot per
    /// waiting job. Time to clear is what changes when the rate changes.</para>
    /// </summary>
    public string Effect =>
        Slots <= 0 || double.IsInfinity(ClearDaysNow) || ClearDaysNow <= 0 ? ""
        : double.IsInfinity(ClearDaysAfter)                                ? ""
        : $"backlog clears in {ClearDaysAfter:N0}d instead of {ClearDaysNow:N0}d";
}

/// <summary>
/// How much work one slot pool can get through, against how much is waiting for it.
/// </summary>
/// <param name="BacklogJobDays">Work queued, in job-days: how long it would take one slot on its
/// own. Divided by the slots actually running, this is the time to catch up.</param>
/// <param name="JobsDone">Jobs started in the measured window — what the pool has really been
/// managing, as opposed to what its slot count says it could.</param>
public sealed record SlotPressure(
    IndustryPool     Pool,
    int              Capacity,
    int              InUse,
    int              Waiting,
    int              Blocked,
    double           BacklogJobDays,
    double           AvgJobDays,
    int              JobsDone,
    int              WindowDays,
    IReadOnlyList<SlotRemedy> Remedies)
{
    public int    Free     => Math.Max(0, Capacity - InUse);
    public double Utilised => Capacity <= 0 ? 0 : 100.0 * InUse / Capacity;

    /// <summary>Days to work through what is queued, at the bandwidth on hand.</summary>
    public double ClearDays => Capacity <= 0 ? double.PositiveInfinity : BacklogJobDays / Capacity;

    /// <summary>Jobs a day this pool has actually been starting.</summary>
    public double ThroughputPerDay => WindowDays <= 0 ? 0 : (double)JobsDone / WindowDays;

    /// <summary>
    /// Whether this pool is what is holding work up.
    ///
    /// <para><b>⚠️ Queued work is the test, not a full pool.</b> A hundred slots with ten free and
    /// two hundred jobs that could start today is a slot bottleneck by any reading that matters —
    /// requiring zero free slots would have called that pool healthy because ten of its hundred
    /// happened to be idle at the moment it was measured.</para>
    /// </summary>
    public bool IsBottleneck => Waiting > 0;
}

/// <summary>
/// A product whose blueprints, not its slots, set how fast it can be made.
/// </summary>
/// <param name="CycleDays">How long one run takes, from what this blueprint has actually been
/// doing rather than from the SDE — the structure, rigs and skills in play are already in it.</param>
/// <param name="Prints">Originals owned. Copies are excluded: a copy is throughput already bought
/// and consumed, an original is a standing limit.</param>
/// <param name="MadePerDay">Units a day actually produced. ⚠️ Bounded above by the ceiling these
/// same prints impose, so it measures what was achieved and never what was wanted.</param>
/// <param name="BusyDays">Days these prints spent inside a job during the window. Against the
/// days they were available, this is the only honest read on whether they are the limit.</param>
public sealed record ItemBandwidth(
    int    ProductTypeId,
    string ProductName,
    double CycleDays,
    int    Prints,
    int    Busy,
    double MadePerDay,
    double BusyDays,
    int    UnitsPerRun,
    int    WindowDays)
{
    /// <summary>Units a day one print can turn out, running without a pause.</summary>
    public double PerPrintPerDay => CycleDays <= 0 ? 0 : UnitsPerRun / CycleDays;

    /// <summary>Units a day the prints on hand can turn out between them — the ceiling.</summary>
    public double CapacityPerDay => Prints * PerPrintPerDay;

    /// <summary>What the ceiling becomes with one more original.</summary>
    public double CeilingWithOneMore => (Prints + 1) * PerPrintPerDay;

    /// <summary>
    /// How much of the time these prints could have been running, they were.
    ///
    /// <para><b>⚠️ This is the pressure signal, not the production rate.</b> What was produced can
    /// never exceed what the prints could produce — history is censored by the very ceiling being
    /// measured, so "made 0.41/day against a ceiling of 0.14/day" is not a shortage, it is an
    /// impossibility. Two items with the same ceiling can sit at 0.09/day and 0.001/day, and the
    /// difference between them is not demand, it is whether the print is ever idle.</para>
    ///
    /// <para>⚠️ Can exceed 100% where a print was acquired part-way through the window: the busy
    /// time is real but was earned by more prints than are owned now, or by copies. Capped, and
    /// treated as saturated, which is what it means either way.</para>
    /// </summary>
    public double UtilPercent => Prints <= 0 || WindowDays <= 0
        ? 0
        : Math.Min(100, 100.0 * BusyDays / (Prints * (double)WindowDays));

    /// <summary>
    /// Saturated enough that the print, rather than anything else, is setting the pace.
    ///
    /// <para>A print running two thirds of the time has little idle left to absorb more work; one
    /// running a twentieth of the time is not what is holding anything up, whatever else is.</para>
    /// </summary>
    public bool IsTight => UtilPercent >= 60;

    public string Advice => !IsTight
        ? $"Idle {100 - UtilPercent:N0}% of the time — this print is not the limit."
        : $"Running {UtilPercent:N0}% of the time. A second print raises the ceiling from "
        + $"{CapacityPerDay:N2}/day to {CeilingWithOneMore:N2}/day, and halves the time on any "
        + "order of them.";

    public void OpenItem() => EntityNavigator.Instance.Item(ProductTypeId);
}

/// <summary>
/// What is holding the pipeline up.
///
/// <para><b>Bandwidth, not queue length.</b> Slots and blueprints are both throughput: twenty
/// slots get through twice the work of ten, and two Thanatos originals turn out two a cycle where
/// one turns out one. So five hundred jobs waiting does not mean five hundred slots are needed —
/// that queue is what accumulated while throughput sat under demand, and more bandwidth eats into
/// it over time rather than clearing it at a stroke. Everything here is a rate, or the days a
/// rate implies.</para>
///
/// <para><b>Measured, not assumed.</b> Cycle times and demand rates come from the job history, so
/// they already carry the structures, rigs and skills actually in use. What the tool is planning
/// today says what is wanted now; what has been run for months says what is wanted repeatedly,
/// and the gap between those two is where a standing shortage of bandwidth hides — a part
/// consumed three times as often as its neighbours needs three times the prints, and no snapshot
/// of this morning's queue will ever say so.</para>
///
/// <para>Built on the worklist's own conclusions about what cannot start, so the two can never
/// give different answers to "why is this stuck".</para>
/// </summary>
public class BottleneckService(
    IDbContextFactory<AppDbContext> dbFactory,
    IndustryAssignmentService assignment,
    IndustryBlueprintService  blueprints,
    WorklistSettings          settings)
{
    /// <summary>
    /// The most slots one character can hold in a pool: the free one everybody has, plus five
    /// levels of the basic skill and five of the advanced.
    /// </summary>
    public const int MaxSlotsPerCharacter = 11;

    /// <summary>Characters on one account — the unit people actually buy capacity in.</summary>
    private const int CharactersPerAccount = 3;

    /// <summary>
    /// How far back the rates are measured.
    ///
    /// <para>⚠️ Long enough that a quiet fortnight does not read as a collapse in demand, short
    /// enough that what the pipeline was doing six months ago does not outvote what it is doing
    /// now. Counted by when a job STARTED, so a long build still in flight counts toward the
    /// period it was committed in.</para>
    /// </summary>
    private const int WindowDays = 90;

    // ── Slots ─────────────────────────────────────────────────────────────────

    public async Task<List<SlotPressure>> SlotPressureAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);

        // What each pool has actually been getting through. Durations included, because a pool
        // running long jobs and one running short jobs are not comparable by job count.
        var history = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.StartDate >= since)
                .Select(j => new { j.ActivityId, j.Duration })
                .ToListAsync(ct))
            .GroupBy(j => IndustryAssignmentService.PoolOf(j.ActivityId))
            .ToDictionary(g => g.Key, g => (Jobs: g.Count(), Days: g.Sum(j => (double)j.Duration) / 86400.0));

        var result = new List<SlotPressure>();

        foreach (var pool in new[] { IndustryPool.Manufacturing, IndustryPool.Reaction, IndustryPool.Science })
        {
            var on  = candidates.Where(c => c.Runs(pool)).ToList();
            var off = candidates.Where(c => !c.Runs(pool)).ToList();

            var capacity = on.Sum(c => c.Capacity.GetValueOrDefault(pool));
            var free     = on.Sum(c => c.FreeSlots.GetValueOrDefault(pool));
            var inUse    = Math.Max(0, capacity - free);

            var waiting = items.Count(i => i.Pool == pool && i.Readiness == WorklistReadiness.Waiting);
            var blocked = items.Count(i => i.Pool == pool && i.Readiness == WorklistReadiness.Blocked);

            var seen = history.GetValueOrDefault(pool);

            // ⚠️ The queue is priced in job-days, not in jobs. A waiting job holds a slot for as
            // long as it runs, so a pool whose jobs take a fortnight is far further behind on the
            // same queue length than one whose jobs take an hour. Sized from what this pool's
            // jobs have actually taken, because nothing knows the duration of a job never started.
            var avgJobDays = seen.Jobs > 0 ? seen.Days / seen.Jobs : 0;
            var backlog    = waiting * avgJobDays;

            double Pct(int slots)   => capacity <= 0 ? 0 : 100.0 * slots / capacity;
            double Clear(int extra) => capacity + extra <= 0
                                     ? double.PositiveInfinity
                                     : backlog / (capacity + extra);

            SlotRemedy Remedy(string action, int slots, string detail) =>
                new(action, slots, detail)
                {
                    PercentGain    = Pct(slots),
                    ClearDaysNow   = Clear(0),
                    ClearDaysAfter = Clear(slots),
                };

            var remedies = new List<SlotRemedy>();

            // Train what is already switched on — the cheapest bandwidth there is.
            var trainable = on
                .Select(c => (c, Room: MaxSlotsPerCharacter - c.Capacity.GetValueOrDefault(pool)))
                .Where(x => x.Room > 0)
                .OrderByDescending(x => x.Room)
                .ToList();

            remedies.Add(Remedy(
                "Train the characters already running this",
                trainable.Sum(x => x.Room),
                trainable.Count == 0
                    ? "Everyone running this pool is already at the skill cap."
                    : $"{trainable.Count} character(s) below the {MaxSlotsPerCharacter}-slot cap: "
                    + Name(trainable.Select(x => (x.c, x.Room)))));

            // ⚠️ Quantified, not recommended. These are off for reasons the app cannot see — a
            // trading alt, a character in someone else's corp — so this is a size, not a plan.
            var offSlots = off.Sum(c => c.Capacity.GetValueOrDefault(pool));
            remedies.Add(Remedy(
                "Enable characters currently switched off for this pool",
                offSlots,
                off.Count == 0
                    ? "Every configured character already runs this pool."
                    : $"{off.Count} character(s) switched off, holding {offSlots:N0} slot(s) today "
                    + "— usually off for a reason, so read this as a size rather than a plan. "
                    + Name(off.Select(c => (c, c.Capacity.GetValueOrDefault(pool))))));

            remedies.Add(Remedy(
                $"Add {CharactersPerAccount} characters (one account), trained to the cap",
                CharactersPerAccount * MaxSlotsPerCharacter,
                $"{CharactersPerAccount} × {MaxSlotsPerCharacter} slots against the {capacity:N0} "
                + "running now. Training is not instant — this is the ceiling it buys."));

            result.Add(new SlotPressure(pool, capacity, inUse, waiting, blocked,
                                        backlog, avgJobDays, seen.Jobs, WindowDays, remedies));
        }

        return result;
    }

    // ── Blueprints ────────────────────────────────────────────────────────────

    /// <summary>
    /// Products whose blueprint count is the ceiling on how fast they can be made.
    ///
    /// <para><b>⚠️ An original runs one job at a time.</b> Everything else in the tool treats
    /// owning a BPO as owning the ability to build the thing — true of the recipe, false of the
    /// throughput. One Thanatos original is one hull a cycle however many slots are free; a
    /// second doubles it, and halves the time on any order of ten.</para>
    /// </summary>
    public async Task<List<ItemBandwidth>> BlueprintBandwidthAsync(CancellationToken ct = default)
    {
        var owned  = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var prints = (await blueprints.LoadAllAsync(ct))
            .Where(owned.Owns).Where(p => p.IsOriginal).ToList();
        if (prints.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);

        // What has actually been made, and how long a run of it takes here. Per RUN, so a ten-run
        // job and a one-run job of the same thing agree about the cycle.
        var runs = await db.EsiIndustryJobs.AsNoTracking()
            .Where(j => j.StartDate >= since && j.Runs > 0 && j.ProductTypeId != null
                     && (j.ActivityId == 1 || j.ActivityId == 9 || j.ActivityId == 11))
            .Select(j => new { j.BlueprintTypeId, ProductTypeId = j.ProductTypeId!.Value, j.Runs, j.Duration })
            .ToListAsync(ct);
        if (runs.Count == 0) return [];

        var byBlueprint = runs
            .GroupBy(j => j.BlueprintTypeId)
            .ToDictionary(
                g => g.Key,
                g => (ProductTypeId: g.First().ProductTypeId,
                      CycleDays:     g.Average(j => (double)j.Duration / j.Runs) / 86400.0,
                      Runs:          g.Sum(j => (long)j.Runs),
                      // ⚠️ Time occupied, not jobs counted. This is what makes a saturated print
                      // distinguishable from an idle one, and the two are indistinguishable by
                      // production rate alone — both are bounded by the same ceiling.
                      BusyDays:      g.Sum(j => (double)j.Duration) / 86400.0));

        var productIds = byBlueprint.Values.Select(v => v.ProductTypeId).Distinct().ToList();

        var perRun = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => (p.Activity == "manufacturing" || p.Activity == "reaction")
                     && productIds.Contains(p.ProductTypeId))
            .ToDictionaryAsync(p => p.ProductTypeId, p => Math.Max(1, p.Quantity), ct);

        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => productIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var result = new List<ItemBandwidth>();

        foreach (var (bpTypeId, made) in byBlueprint)
        {
            var mine = prints.Where(p => p.TypeId == bpTypeId).ToList();
            if (mine.Count == 0 || made.CycleDays <= 0) continue;   // built from copies, or unmeasurable

            var units = perRun.GetValueOrDefault(made.ProductTypeId, 1);

            result.Add(new ItemBandwidth(
                made.ProductTypeId,
                names.GetValueOrDefault(made.ProductTypeId, $"Type {made.ProductTypeId}"),
                made.CycleDays,
                mine.Count,
                mine.Count(p => p.LockedInJob),
                made.Runs * units / (double)WindowDays,
                made.BusyDays,
                units,
                WindowDays));
        }

        // ⚠️ Busiest first, not "most short". A shortfall cannot be measured from history — what
        // was produced is capped by the very prints being judged, so demand above the ceiling
        // leaves no trace. Time spent occupied is the one thing that does distinguish a print
        // that never stops from one that rarely starts.
        return result
            .Where(r => r.IsTight)
            .OrderByDescending(r => r.UtilPercent)
            .ThenByDescending(r => r.MadePerDay)
            .ToList();
    }

    /// <summary>A few names and their numbers, so a total can be checked against something.</summary>
    private static string Name(IEnumerable<(IndustryCandidate C, int N)> people)
    {
        var list = people.Take(4).Select(x => $"{x.C.Config.CharacterName} +{x.N}").ToList();
        return list.Count == 0 ? "" : string.Join(", ", list);
    }
}
