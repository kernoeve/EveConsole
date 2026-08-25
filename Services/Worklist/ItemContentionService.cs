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
/// <summary>
/// One task behind a number on the contention row, so the count can be audited rather than
/// believed.
///
/// <para>A figure nobody can take apart is a figure nobody can trust: "Blocking 4" is only
/// useful once it can be read as four named tasks.</para>
/// </summary>
/// <param name="Role">Stopped by the shortage, or making the item.</param>
/// <param name="Hop">0 for a task directly short of it, 1 for a task stopped behind one of
/// those, and so on. -1 for the tasks that make it, which are not on the chain.</param>
/// <param name="Why">Why this task is on the list — the shortfall that stopped it, or what
/// is in the way of making it.</param>
public sealed record ShortageTask(
    string Role,
    int    Hop,
    string TypeName,
    string Title,
    string State,
    string Why);

public sealed record ItemShortage(
    int    TypeId,
    string Name,
    double UsedPerDay,
    double MadePerDay,
    long   Level,
    long   OnHand,
    int    BlockedTasks,
    int    StalledTasks,
    bool   MustBuy,
    bool   Buildable,
    int    WindowDays,
    double RecentUsedPerDay = 0,
    int    RecentDays = 30,
    long   TotalShort = 0,
    int    MakingRunning = 0,
    int    MakingReady = 0,
    int    MakingWaiting = 0,
    int    MakingBlocked = 0,
    IReadOnlyList<ShortageTask>? Tasks = null)
{
    /// <summary>
    /// How much harder this is being drawn on now than across the whole window.
    ///
    /// <para>⚠️ What separates a wave from a trend. Two titans and four supers put a quarter's
    /// demand through in a fortnight, and during it every input reads as under-produced — which
    /// says nothing about whether the pipeline is sized right, only that a big order is passing
    /// through it.</para>
    /// </summary>
    public double Surge => UsedPerDay <= 0 ? 1 : RecentUsedPerDay / UsedPerDay;

    /// <summary>Demand is spiking rather than sitting where it usually does.</summary>
    public bool IsWave => Surge >= 1.5;

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
    /// Made per unit consumed. Below one the shelf is being drawn down.
    ///
    /// <para>⚠️ Supporting detail, NOT the ranking. A deficit is usually a build wave passing
    /// through rather than a verdict on the pipeline — sustained over-consumption cannot happen,
    /// because it empties the buffer and then the work stops. Absorbing exactly that is what the
    /// buffer is for, and what ranks here is whether it ran out.</para>
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
    public bool RateSuppressed => BlockedTasks > 0;

    /// <summary>
    /// What is actually wrong here.
    ///
    /// <para><b>⚠️ Blocked work comes first, always.</b> The harm is not a ratio — it is that the
    /// buffer ran out, jobs stopped, and slots are sitting idle. A deficit with stock still on the
    /// shelf is a number; a deficit with an empty shelf and work stalled behind it is the thing
    /// this tab exists to find.</para>
    ///
    /// <para>⚠️ And a deficit during a build wave is not a verdict on the pipeline. Sustained
    /// over-consumption is self-limiting — it empties the buffer and the work stops — so what a
    /// deficit usually means is that a large order is passing through. Only one that holds across
    /// both windows says production is genuinely undersized.</para>
    /// </summary>
    /// <summary>
    /// Whether the shelf is actually empty, rather than merely being drawn on.
    ///
    /// <para>⚠️ "Buffer spent" has to mean the buffer was spent. It was reported off the surge
    /// alone, so an item sitting at 3,096 against a level of 3,000 — a full shelf — was told its
    /// buffer had run out and that it needed a bigger one.</para>
    /// </summary>
    public bool BufferEmpty => Level > 0 && OnHand < Level * 0.2;

    /// <summary>What the work on the list actually needs: what is here, plus what it fell short by.</summary>
    public long Need => OnHand + TotalShort;

    /// <summary>
    /// Why the shelf drained, appended where it applies.
    ///
    /// <para>⚠️ This was a verdict of its own, "Never made", and the verdict was a false
    /// statement: nothing was made in the WINDOW, which is not the same as never. It is evidence
    /// about why a level emptied, not a claim about the item, so it reads as a clause on whatever
    /// verdict actually applies.</para>
    /// </summary>
    private string NothingMadeNote =>
        MadePerDay <= 0 && UsedPerDay > 0
            ? $" Not one has been made in the last {WindowDays} days, which is what drained it."
            : "";

    public string Verdict =>
        BlockedTasks <= 0                        ? NotBlockedVerdict
      : !Buildable                              ? "Buy now"
      // ⚠️ No level at all is its own finding, and it used to fall through to "not the shelf" —
      // because an empty-buffer test cannot be true when there is no buffer to be empty. An item
      // in constant demand that nothing sets aside has no cushion by construction.
      : Level <= 0                              ? "No buffer"
      : BufferEmpty && IsWave                   ? "Buffer spent"
      : BufferEmpty                             ? "Blocked"
      // The shelf met its level and the work still wants more than is here: the level is sized
      // for ordinary draw and this is not that.
      : Need > OnHand                           ? "Level too low"
      :                                           "Not the shelf";

    private string NotBlockedVerdict =>
        !Buildable && MustBuy               ? "Buy"
      : IsDraining && !IsWave               ? "Making too few"
      : IsDraining                          ? "Wave"
      : Level <= 0                          ? "No level set"
      : DaysOfCover < 7                     ? "Buffer thin"
      :                                       "Holding";

    /// <summary>What the shortage holds up beyond the tasks that consume it directly.</summary>
    private string StalledNote =>
        StalledTasks > BlockedTasks
            ? $" A further {StalledTasks - BlockedTasks:N0} task(s) are stopped behind those."
            : "";

    /// <summary>
    /// Whether anything is actually refilling it, said rather than left to be looked up.
    ///
    /// <para>⚠️ Advice that ends "check whether it is being made" is advice to go and do the
    /// lookup this row already did. The counts are on the row; the sentence should use them.</para>
    /// </summary>
    private string MakingNote =>
        MakingRunning > 0
            ? $" {MakingRunning:N0} job(s) making it are running now."
      : MakingReady > 0
            ? $" {MakingReady:N0} task(s) to make it are ready to start — starting them is the fix."
      : MakingWaiting > 0
            ? $" {MakingWaiting:N0} task(s) to make it are waiting on a free slot."
      : MakingBlocked > 0
            ? $" Nothing is refilling it: all {MakingBlocked:N0} task(s) to make it are blocked too."
            : " Nothing on the list is making it at all.";

    public string Advice =>
        BlockedTasks > 0 ? AdviceCore + StalledNote + MakingNote : AdviceCore;

    private string AdviceCore => Verdict switch
    {
        "Buy now" =>
            $"{BlockedTasks:N0} task(s) stopped, none owned, and nothing here makes it. "
          + $"Drawn on at {UsedPerDay:N1}/day"
          + (Level > 0 ? $" against a level of {Level:N0}." : " with no level set to hold any.")
          + " Buying is the only thing that starts them.",

        // ⚠️ The buffer did its job and was not big enough. Saying "make more" here would be
        // advice for the wrong problem: production has not changed, demand has, and absorbing
        // exactly this is what a buffer is FOR.
        "Buffer spent" =>
            $"{BlockedTasks:N0} task(s) stopped with {OnHand:N0} left. Demand is running "
          + $"{Surge:N1}× its {WindowDays}-day average and the level of "
          + $"{Level:N0} covered {DaysOfCover:N0} day(s) of ordinary draw but not this. "
          + "A larger level absorbs the next wave; if waves like this are routine, the durable "
          + "fix is making more of it, since no level survives a rate it cannot refill at."
          + NothingMadeNote,

        // ⚠️ The level is met and the work still wants more than is here. Nothing has failed —
        // the level was sized for ordinary draw, and the demand on the list is not that.
        "Level too low" =>
            $"{BlockedTasks:N0} task(s) stopped. The work on the list needs {Need:N0} and "
          + $"{OnHand:N0} are on hand — the level of {Level:N0} is met, so this is not a buffer "
          + $"that ran out but one sized for {DaysOfCover:N0} day(s) of ordinary draw when the "
          + $"work in front of it wants {TotalShort:N0} more than exists"
          + (IsWave ? $", with demand running {Surge:N1}× its {WindowDays}-day average." : ".")
          + $" Raising the level, or making more, is what closes that gap.{NothingMadeNote}",

        // ⚠️ No level at all. Not a small buffer — none, so there is nothing to absorb anything.
        "No buffer" =>
            $"{BlockedTasks:N0} task(s) stopped and nothing sets a level for this at all, though it "
          + $"is drawn on at {UsedPerDay:N1}/day. There is no cushion by construction: every "
          + $"unit has to be made or bought exactly when it is wanted. Short {TotalShort:N0} "
          + $"against what the list needs.{NothingMadeNote}",

        // Everything the list wants is here, and the jobs still cannot start. Whatever stopped
        // them is not this material.
        "Not the shelf" =>
            $"{BlockedTasks:N0} task(s) stopped, but {OnHand:N0} are on hand and the work needs "
          + $"{Need:N0} — this material is not what stopped them. Something else on those jobs is "
          + "short, or they are waiting on a slot, a blueprint, or stock sitting at another station.",

        "Blocked" =>
            $"{BlockedTasks:N0} task(s) stopped with {OnHand:N0} left against a level of {Level:N0} "
          + $"— about {DaysOfCover:N0} day(s) of cover at {UsedPerDay:N1}/day, and it ran out. "
          + "Either the level is too low for how fast this moves, or it is not being refilled in "
          + "time." + NothingMadeNote,

        "Buy" =>
            $"None owned and nothing here makes it, drawn on at {UsedPerDay:N1}/day. Nothing is "
          + "stopped yet.",

        // Only reached when the deficit holds across both windows — not a spike.
        "Making too few" =>
            $"Consumed {UsedPerDay:N1}/day against {MadePerDay:N1}/day made, and demand is flat. "
          + (double.IsInfinity(DaysToEmpty) ? "" : $"Empty in about {DaysToEmpty:N0} day(s) at this rate. ")
          + "More production, not a larger buffer.",

        "Wave" =>
            $"Being drawn on {Surge:N1}× harder than usual — a build wave passing through. "
          + $"{OnHand:N0} left, roughly {DaysToEmpty:N0} day(s) at the current draw. Nothing is "
          + "stopped yet; worth watching rather than acting on.",

        "No level set" =>
            $"Consumed {UsedPerDay:N1}/day with nothing asking to keep any on the shelf, so there "
          + "is no cushion at all when demand rises.",

        "Buffer thin" =>
            $"The level of {Level:N0} is {DaysOfCover:N0} day(s) at {UsedPerDay:N1}/day. Anything "
          + "taking longer than that to replace will block before it arrives.",

        _ =>
            $"{DaysOfCover:N0} day(s) of cover at {UsedPerDay:N1}/day, replaced at "
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
public class ItemContentionService(
    IDbContextFactory<AppDbContext> dbFactory,
    WorklistSettings                settings)
{
    /// <summary>How far back rates are measured. Matches the Bottlenecks tab's other windows.</summary>
    private const int WindowDays = 90;

    /// <summary>
    /// The recent slice, compared against the whole window to tell a wave from a trend.
    ///
    /// <para>⚠️ Sustained over-consumption cannot happen: it drains the buffer, and then the
    /// consumption stops because the work stops. So a deficit measured today is usually a build
    /// wave passing through — two titans and four supers dumping a quarter's demand into a
    /// fortnight — and reading it as "we permanently under-produce this" prescribes the wrong
    /// cure. Only a deficit that holds across BOTH windows is structural.</para>
    /// </summary>
    private const int RecentDays = 30;

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

        // Kept as the tasks themselves rather than counted here: the walk below has to follow
        // what each stopped task would have produced, so it needs the rows, not a number.
        var stoppedBy = short_
            .GroupBy(x => x.Short.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(x => (x.Item, x.Short)).ToList());

        var blockedBy = short_
            .GroupBy(x => x.Short.TypeId)
            .ToDictionary(g => g.Key, g => (
                Name:  g.First().Short.TypeName,
                Jobs:  g.Count(),
                // ⚠️ Summed across the jobs that fell short. What the work in front of this
                // material wants beyond what it can get — the number that says whether a level
                // is merely met or actually sufficient.
                Short: g.Sum(x => x.Short.Short)));

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var (used, made)  = await RatesAsync(db, WindowDays, ct);
        var (recent, _)   = await RatesAsync(db, RecentDays, ct);
        var ids          = blockedBy.Keys.ToList();

        var level = (await db.InvLevelItems.AsNoTracking()
                .Join(db.InvLevelGroups.AsNoTracking(), i => i.GroupId, g => g.Id,
                      (i, g) => new { i.TypeId, Target = i.TargetQuantity * (g.Multiplier <= 0 ? 1 : g.Multiplier) })
                .Where(x => ids.Contains(x.TypeId))
                .ToListAsync(ct))
            .GroupBy(x => x.TypeId)
            .ToDictionary(g => g.Key, g => (long)g.Max(x => x.Target));

        // ⚠️ Scoped exactly as the generator scopes it. Counting every asset row anywhere made a
        // buffer look full while the industry scope could not reach a unit of it — three thousand
        // Sense-Heuristic Enhancers on hand, a level of three thousand, and jobs stopped for want
        // of them. Stock the plan cannot spend is not stock as far as the plan is concerned.
        var scope = await InvLevelService.ResolveScopeFilterAsync(
            db, settings.IndustryScope, settings.IndustryScopeId, ct);
        if (scope is not null)
            scope.UnionWith(await db.WorklistIndyScopeStations.AsNoTracking()
                .Select(s => s.LocationId).ToListAsync(ct));

        var onHand = (await db.EsiAssets.AsNoTracking()
                .Where(a => ids.Contains(a.TypeId))
                .Select(a => new { a.TypeId, a.Quantity, a.RootLocationId })
                .ToListAsync(ct))
            .Where(a => scope is null || scope.Contains(a.RootLocationId))
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

        // Everything one shortage holds up.
        //
        // ⚠️ Over blocked TASKS, not over the recipe tree. Counting the types that could consume
        // this reported a Leviathan as downstream of it whether or not anyone was building one —
        // true about EVE, useless about the queue. A task stopped for want of this cannot produce
        // its own output, so whatever is stopped for want of THAT is stopped by this too, and the
        // walk carries on until nothing new is reached.
        List<ShortageTask> Stalled(int typeId)
        {
            var found = new List<ShortageTask>();
            var tasks = new HashSet<string>();
            var types = new HashSet<int> { typeId };
            var queue = new Queue<(int Type, int Hop)>();
            queue.Enqueue((typeId, 0));

            while (queue.Count > 0)
            {
                var (type, hop) = queue.Dequeue();
                if (!stoppedBy.TryGetValue(type, out var stopped)) continue;

                foreach (var (task, sh) in stopped)
                {
                    if (!tasks.Add(task.Key)) continue;

                    found.Add(new ShortageTask(
                        "Stopped", hop, task.TypeName, task.Title,
                        task.Readiness.ToString(),
                        $"short {sh.Short:N0} of {sh.Wanted:N0} {sh.TypeName}"));

                    // What this task would have made is now short for whatever eats it.
                    if (task.TypeId > 0 && types.Add(task.TypeId)) queue.Enqueue((task.TypeId, hop + 1));
                }
            }
            return found;
        }

        // Tasks to make the item itself, by whether the player can act on them.
        var makeTasks = items
            .Where(i => i.TypeId > 0 && i.Kind == WorklistKind.Job)
            .GroupBy(i => i.TypeId)
            .ToDictionary(g => g.Key, g => g.Select(i => new ShortageTask(
                "Making", -1, i.TypeName, i.Title, i.Readiness.ToString(),
                i.BlockedBy.Length > 0 ? i.BlockedBy : "ready to install")).ToList());

        var makes = items
            .Where(i => i.TypeId > 0 && i.Kind == WorklistKind.Job)
            .GroupBy(i => i.TypeId)
            .ToDictionary(g => g.Key, g => (
                Ready:   g.Count(i => i.Readiness == WorklistReadiness.Ready),
                Waiting: g.Count(i => i.Readiness == WorklistReadiness.Waiting),
                Blocked: g.Count(i => i.Readiness == WorklistReadiness.Blocked)));

        // Already installed and turning. Not a task — nothing on the list asks for it, and a shelf
        // with four jobs about to land on it is in a different position from one with none.
        var runningJobs = (await db.EsiIndustryJobs.AsNoTracking()
                .Where(j => j.Status == "active" && j.ProductTypeId != null
                         && ids.Contains(j.ProductTypeId.Value))
                .Select(j => new { Type = j.ProductTypeId!.Value, j.Runs, j.EndDate })
                .ToListAsync(ct))
            .GroupBy(j => j.Type)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(j => j.EndDate)
                .Select(j => new ShortageTask(
                    "Making", -1, "", $"{j.Runs:N0} run(s) installed",
                    "Running", $"lands {j.EndDate.LocalDateTime:d MMM HH:mm}"))
                .ToList());

        var running = runningJobs.ToDictionary(kv => kv.Key, kv => kv.Value.Count);

        return blockedBy
            .Select(kv => new ItemShortage(
                kv.Key,
                kv.Value.Name,
                used.GetValueOrDefault(kv.Key) / WindowDays,
                made.GetValueOrDefault(kv.Key) / WindowDays,
                level.GetValueOrDefault(kv.Key),
                onHand.GetValueOrDefault(kv.Key),
                kv.Value.Jobs,
                Stalled(kv.Key).Count,
                MustBuy: true,
                Buildable: buildable.Contains(kv.Key),
                WindowDays,
                recent.GetValueOrDefault(kv.Key) / RecentDays,
                RecentDays,
                kv.Value.Short,
                running.GetValueOrDefault(kv.Key),
                makes.GetValueOrDefault(kv.Key).Ready,
                makes.GetValueOrDefault(kv.Key).Waiting,
                makes.GetValueOrDefault(kv.Key).Blocked,
                // Stopped work first and nearest first, then whatever is refilling it: the row
                // reads top to bottom as the question does.
                [.. Stalled(kv.Key).OrderBy(t => t.Hop),
                    .. runningJobs.GetValueOrDefault(kv.Key) ?? [],
                    .. makeTasks.GetValueOrDefault(kv.Key) ?? []]))
            // ⚠️ Stopped work first, then how much waits behind it. The harm is idle slots and
            // stalled jobs, not a ratio — a deficit with stock still on the shelf costs nothing
            // yet, and an empty shelf with forty jobs behind it is costing everything. Sorting on
            // the balance put the whole "consuming faster than making" block at the top whatever
            // its consequences, and left genuinely stopped work underneath it.
            .OrderByDescending(s => s.BlockedTasks > 0)
            .ThenByDescending(s => s.StalledTasks)
            .ThenByDescending(s => s.BlockedTasks)
            .ThenByDescending(s => s.IsDraining && !s.IsWave)
            .ThenBy(s => s.DaysToEmpty)
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
        RatesAsync(AppDbContext db, int days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);

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
