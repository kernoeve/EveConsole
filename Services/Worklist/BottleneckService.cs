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
    int    WindowDays,
    int    WantedNow,
    int    BlockedNow)
{
    /// <summary>Units a day one print can turn out, running without a pause.</summary>
    public double PerPrintPerDay => CycleDays <= 0 ? 0 : UnitsPerRun / CycleDays;

    /// <summary>Units a day the prints on hand can turn out between them — the ceiling.</summary>
    public double CapacityPerDay => Prints * PerPrintPerDay;

    /// <summary>What the ceiling becomes with one more original.</summary>
    public double CeilingWithOneMore => (Prints + 1) * PerPrintPerDay;

    /// <summary>
    /// The share of each print's time that was spent inside a job.
    ///
    /// <para>⚠️ Kept for context only — it does NOT identify bottlenecks, and ranking on it ranks
    /// the wrong things. A formula run constantly across eight copies shows high use and has never
    /// once made anybody wait; see <see cref="ContentionPercent"/>, which is the real signal.</para>
    /// </summary>
    public double UtilPercent => Prints <= 0 || WindowDays <= 0
        ? 0
        : Math.Min(100, 100.0 * BusyDays / (Prints * (double)WindowDays));

    /// <summary>
    /// The share of the window during which EVERY print of this type was occupied at once.
    ///
    /// <para><b>⚠️ This is what a blueprint bottleneck actually is.</b> Not "used a lot" — having
    /// to wait for one to free up. The two come apart completely once there is more than one copy:
    /// Fernite Carbide runs at 33% use across eight formulas and has been all-busy 0% of the time,
    /// because buying the eighth was what fixed it. Ranking on use would put it near the top of a
    /// list of things to buy; it is finished business.</para>
    ///
    /// <para>It also self-corrects, which no threshold does: every print added drives this down
    /// directly, so a formula that was a constraint last quarter stops reporting as one without
    /// anybody retuning anything.</para>
    /// </summary>
    public double ContentionPercent { get; init; }

    /// <summary>
    /// Where this sits in the operation's own spread of contention, 0–100.
    ///
    /// <para><b>⚠️ Relative, because an absolute cut cannot survive a different operation.</b>
    /// Fifteen percent contention is severe for somebody whose prints sit near zero and unremarkable
    /// for somebody running everything hot — the same number describes a crisis and a Tuesday. What
    /// travels between operations is the shape: the busiest tenth of YOUR prints are the ones worth
    /// looking at, whatever figure that tenth happens to start at.</para>
    ///
    /// <para>It also moves as the operation does. Buy copies until nothing is contended and the
    /// whole distribution collapses toward zero, where the floor below takes over and the list
    /// empties — which is the correct answer, not a threshold that needs revisiting.</para>
    /// </summary>
    public double ContentionRank { get; init; }

    /// <summary>
    /// Every copy was busy often enough, and often enough compared to everything else here, that
    /// wanting another would have meant waiting.
    ///
    /// <para>⚠️ The zero test is a fact rather than a threshold: a blueprint whose copies were
    /// never all busy at once has demonstrably never made anybody wait, however it ranks against
    /// its neighbours. Without it, an operation with no contention anywhere would still report its
    /// top tenth as bottlenecks — ranking something is not the same as it being a problem.</para>
    /// </summary>
    public bool IsTight => ContentionPercent > 0 && ContentionRank >= 90;

    /// <summary>Never all occupied, or unremarkable against the rest — another copy buys nothing.</summary>
    public bool IsIdle => ContentionPercent <= 0 || ContentionRank < 50;

    /// <summary>
    /// Whether this is a standing shortage or a spike, which is the whole question.
    ///
    /// <para>Current demand alone cannot tell them apart — a queue full of one item looks the
    /// same whether it is the third week running or the first time all year. History alone cannot
    /// either, since a print bought last week has none. Together they can: a print that has been
    /// busy for months AND has work queued now is a standing shortage; one that has sat idle and
    /// suddenly has a queue is a surge, and buying originals for a surge is buying for a week.</para>
    /// </summary>
    /// <summary>
    /// Whether this is a standing constraint or a spike.
    ///
    /// <para>⚠️ Contention is the OPPORTUNITY to block, not proof of blocking. A titan print
    /// running one long job reads as all-busy for half the window and nobody was waiting — you
    /// wanted one titan and you got it. What is queued now is the evidence that somebody was
    /// actually trying to get through the shut door.</para>
    /// </summary>
    public string Pattern =>
        IsTight && BlockedNow > 0 ? "Blocking"
      : IsTight && WantedNow > 0  ? "Steady"
      : IsTight                   ? "Contended"
      : BlockedNow > 0            ? "Blocked"
      : WantedNow > 0 && IsIdle   ? "Surge"
      : WantedNow > 0             ? "Building"
      :                             "Quiet";

    public string Advice => Pattern switch
    {
        "Blocking" =>
            $"Every copy was busy {ContentionPercent:N0}% of the last {WindowDays} days, and "
          + $"{BlockedNow:N0} job(s) cannot start right now. Another copy takes the ceiling from "
          + $"{CapacityPerDay:N2} to {CeilingWithOneMore:N2}/day.",

        "Steady" =>
            $"Every copy was busy {ContentionPercent:N0}% of the last {WindowDays} days with "
          + $"{WantedNow:N0} job(s) queued — wanting another means waiting for one to free up. "
          + $"A further copy takes the ceiling to {CeilingWithOneMore:N2}/day.",

        "Contended" =>
            $"Every copy was busy {ContentionPercent:N0}% of the last {WindowDays} days, but "
          + "nothing is queued for it today. Worth watching rather than buying for.",

        "Blocked" =>
            $"{BlockedNow:N0} job(s) cannot start, though the copies on hand have rarely all been "
          + $"busy at once ({ContentionPercent:N0}%). Check what is really missing before buying a "
          + "print — this looks like something other than the blueprint.",

        "Surge" =>
            $"{WantedNow:N0} job(s) want it now, but every copy was free for almost all of the "
          + $"last {WindowDays} days. A one-off rather than a standing need — a copy may serve "
          + "better than an original.",

        _ =>
            $"{WantedNow:N0} job(s) queued; all copies busy {ContentionPercent:N0}% of the window. "
          + $"Another copy would take the ceiling to {CeilingWithOneMore:N2}/day.",
    };

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
        // ⚠️ The window is applied in memory. StartDate is a DateTimeOffset over a TEXT column,
        // and EF cannot translate a comparison on one — it throws at runtime rather than failing
        // to compile, so the tab reported a LINQ error where it should have shown numbers. The
        // table is small enough that reading it and filtering here costs nothing worth saving.
        var history = (await db.EsiIndustryJobs.AsNoTracking()
                .Select(j => new { j.ActivityId, j.Duration, j.StartDate })
                .ToListAsync(ct))
            .Where(j => j.StartDate >= since)
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

            // ⚠️ Slot use is judged the same way blueprint use is: nobody keeps every slot full.
            // A job ends while its owner is asleep and the next starts when they next log in, so
            // a pool in constant demand still measures well under 100% — and the figure here is
            // an instantaneous count of what is installed, which is even more forgiving.

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
    public async Task<List<ItemBandwidth>> BlueprintBandwidthAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        // ⚠️ Current demand, which history cannot supply. What was produced is capped by the
        // prints; what is QUEUED is not capped by anything, so it is the only uncensored reading
        // of how much is wanted. Paired with utilisation it separates a standing shortage from a
        // spike — see ItemBandwidth.Pattern.
        var wantedNow = items
            .Where(i => i.Kind == WorklistKind.Job && i.TypeId > 0)
            .GroupBy(i => i.TypeId)
            .ToDictionary(g => g.Key,
                          g => (All: g.Count(),
                                Blocked: g.Count(i => i.Readiness == WorklistReadiness.Blocked)));

        var owned  = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var prints = (await blueprints.LoadAllAsync(ct))
            .Where(owned.Owns).Where(p => p.IsOriginal).ToList();
        if (prints.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var since = DateTimeOffset.UtcNow.AddDays(-WindowDays);

        // What has actually been made, and how long a run of it takes here. Per RUN, so a ten-run
        // job and a one-run job of the same thing agree about the cycle.
        // ⚠️ Same as above: the date test cannot go in the query. Everything else can, so it does.
        var now = DateTimeOffset.UtcNow;

        var runs = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Runs > 0 && j.ProductTypeId != null
                         && (j.ActivityId == 1 || j.ActivityId == 9 || j.ActivityId == 11))
                .Select(j => new { j.BlueprintTypeId, ProductTypeId = j.ProductTypeId!.Value,
                                   j.Runs, j.Duration, j.StartDate, j.EndDate, j.BlueprintId })
                .ToListAsync(ct))
            .Where(j => j.EndDate > since && j.StartDate < now)
            .ToList();
        if (runs.Count == 0) return [];

        var byBlueprint = runs
            .GroupBy(j => j.BlueprintTypeId)
            .ToDictionary(
                g => g.Key,
                g => (ProductTypeId: g.First().ProductTypeId,
                      CycleDays:     g.Average(j => (double)j.Duration / j.Runs) / 86400.0,
                      Runs:          g.Sum(j => (long)j.Runs),
                      // ⚠️ Clipped to the window at both ends. A job running longer than the
                      // window used to be counted whole against it, which put one print at 120%
                      // of a time it could not have spent.
                      BusyDays:      g.Sum(j => Overlap(j.StartDate, j.EndDate, since, now)),
                      // The copies actually seen working. Where more were in play then than are
                      // owned now, this is the honest denominator for what happened.
                      Seen:          g.Select(j => j.BlueprintId).Distinct().Count(),
                      Contention:    AllBusyDays(
                                        g.Select(j => (j.StartDate, j.EndDate)).ToList(),
                                        g.Select(j => j.BlueprintId).Distinct().Count(),
                                        since, now)));

        var productIds = byBlueprint.Values.Select(v => v.ProductTypeId).Distinct().ToList();
        var bpIds      = byBlueprint.Keys.ToList();

        // ⚠️ Keyed by BLUEPRINT, not by product. A handful of items have more than one recipe —
        // Tungsten Carbide is made by its own formula at 10,000 a run and by a leftover "Test
        // Reaction Blueprint" at 20 — so keying on the product both threw on the duplicate and,
        // had it not, would have priced a run at a five-hundredth of its real output. The output
        // quantity belongs to the blueprint that produces it and to nothing else.
        var perRun = (await db.SdeBlueprintProducts.AsNoTracking()
                .Where(p => (p.Activity == "manufacturing" || p.Activity == "reaction")
                         && bpIds.Contains(p.TypeId))
                .Select(p => new { p.TypeId, p.Quantity })
                .ToListAsync(ct))
            .GroupBy(p => p.TypeId)
            .ToDictionary(g => g.Key, g => Math.Max(1, g.Max(p => p.Quantity)));

        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => productIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        var result = new List<ItemBandwidth>();

        foreach (var (bpTypeId, made) in byBlueprint)
        {
            var mine = prints.Where(p => p.TypeId == bpTypeId).ToList();
            if (mine.Count == 0 || made.CycleDays <= 0) continue;   // built from copies, or unmeasurable

            var units = perRun.GetValueOrDefault(bpTypeId, 1);

            result.Add(new ItemBandwidth(
                made.ProductTypeId,
                names.GetValueOrDefault(made.ProductTypeId, $"Type {made.ProductTypeId}"),
                made.CycleDays,
                mine.Count,
                mine.Count(p => p.LockedInJob),
                made.Runs * units / (double)WindowDays,
                made.BusyDays,
                units,
                WindowDays,
                wantedNow.GetValueOrDefault(made.ProductTypeId).All,
                wantedNow.GetValueOrDefault(made.ProductTypeId).Blocked)
            {
                ContentionPercent = 100.0 * made.Contention / WindowDays,
            });
        }

        // ⚠️ Ranked against every blueprint measured, before anything is filtered out. Rank it
        // after filtering and the scale is drawn from the survivors — which is the list deciding
        // its own cut-off from the rows it already chose, and would call the least bad of a
        // healthy set a bottleneck.
        var spread = result.Select(r => r.ContentionPercent).OrderBy(v => v).ToList();
        result = result
            .Select(r => r with { ContentionRank = Percentile(spread, r.ContentionPercent) })
            .ToList();

        _lastScale = Describe(spread);

        // ⚠️ Ranked on contention, never on use. Use ranks a formula somebody already bought eight
        // copies of above one they own a single copy of, which is precisely backwards: the eight
        // are the fix, and the one is the problem. Blocked work first among equals, since that is
        // the difference between a door that was shut and a door somebody was trying to open.
        return result
            .Where(r => r.IsTight || r.WantedNow > 0)
            .OrderByDescending(r => r.BlockedNow > 0)
            .ThenByDescending(r => r.ContentionPercent)
            .ThenByDescending(r => r.WantedNow)
            .ToList();
    }

    private string _lastScale = "";

    /// <summary>
    /// The spread the last run's flags were drawn against, in words.
    ///
    /// <para>⚠️ Worth showing because nothing here uses a fixed cut-off. A reader told that a
    /// blueprint is in the top tenth is owed the scale that tenth begins at, or the flag is an
    /// assertion with no visible basis.</para>
    /// </summary>
    public Task<string> ContentionScaleAsync() => Task.FromResult(_lastScale);

    private static string Describe(List<double> sorted)
    {
        if (sorted.Count < 4) return "";

        double At(double p) => sorted[(int)(p * (sorted.Count - 1))];

        var never = sorted.Count(v => v <= 0);
        return $"Measured across {sorted.Count:N0} blueprint(s) over {WindowDays} days: "
             + $"{never:N0} never had every copy busy at once, the middle sits at {At(.5):N0}%, "
             + $"and the busiest tenth start at {At(.9):N0}% — which is the line the flags use.";
    }

    /// <summary>
    /// Where a value sits in a sorted spread, 0–100.
    ///
    /// <para>⚠️ Counts values strictly BELOW, so the lowest value scores 0 rather than sharing a
    /// rank with everything equal to it. In a long tail of zeroes — which is most of any real
    /// blueprint collection — averaging ties would hand every untouched print the same middling
    /// rank as its neighbours and drag the whole scale sideways.</para>
    /// </summary>
    private static double Percentile(List<double> sorted, double value)
    {
        if (sorted.Count <= 1) return 0;
        var below = sorted.Count(v => v < value);
        return 100.0 * below / (sorted.Count - 1);
    }

    /// <summary>Days of an interval that fall inside the window.</summary>
    private static double Overlap(DateTimeOffset s, DateTimeOffset e,
                                  DateTimeOffset from, DateTimeOffset to)
    {
        var a = s < from ? from : s;
        var b = e > to   ? to   : e;
        return b <= a ? 0 : (b - a).TotalDays;
    }

    /// <summary>
    /// Days on which every copy of a blueprint was inside a job at the same time.
    ///
    /// <para><b>⚠️ The whole point of the blueprint section.</b> Wanting a job and finding every
    /// copy busy is what a blueprint bottleneck is; running a lot is not. A sweep over the job
    /// intervals is the only way to tell them apart — summed durations cannot, because the same
    /// total spread across eight copies never blocks anybody and concentrated on one blocks
    /// everybody.</para>
    ///
    /// <para>⚠️ Measured against the copies SEEN WORKING in the window rather than the copies
    /// owned today. A print bought last week would otherwise make the months before it look
    /// uncontended, which is backwards — those months are exactly when the shortage was real.</para>
    /// </summary>
    private static double AllBusyDays(
        List<(DateTimeOffset Start, DateTimeOffset End)> jobs, int copies,
        DateTimeOffset from, DateTimeOffset to)
    {
        if (copies <= 0 || jobs.Count == 0) return 0;

        var edges = new List<(DateTimeOffset At, int Delta)>(jobs.Count * 2);
        foreach (var (s, e) in jobs)
        {
            var a = s < from ? from : s;
            var b = e > to   ? to   : e;
            if (b <= a) continue;
            edges.Add((a, +1));
            edges.Add((b, -1));
        }
        if (edges.Count == 0) return 0;

        edges.Sort((x, y) => x.At.CompareTo(y.At));

        double all = 0;
        var  last  = edges[0].At;
        var  open  = 0;

        foreach (var (at, delta) in edges)
        {
            if (open >= copies && at > last) all += (at - last).TotalDays;
            open += delta;
            last  = at;
        }

        return all;
    }

    /// <summary>A few names and their numbers, so a total can be checked against something.</summary>
    private static string Name(IEnumerable<(IndustryCandidate C, int N)> people)
    {
        var list = people.Take(4).Select(x => $"{x.C.Config.CharacterName} +{x.N}").ToList();
        return list.Count == 0 ? "" : string.Join(", ", list);
    }
}
