using EveConsole.Data;
using Microsoft.EntityFrameworkCore;

namespace EveConsole.Services.Worklist;

/// <summary>One way of getting more slots, with what it would actually buy.</summary>
/// <param name="Detail">Who or what it applies to, named so the figure can be checked.</param>
public sealed record SlotRemedy(string Action, int Slots, string Detail)
{
    /// <summary>Share of current capacity this would add. Set by the caller, which knows it.</summary>
    public double PercentGain { get; init; }

    public string Gain => Slots <= 0 ? "—"
                        : PercentGain > 0 ? $"+{Slots:N0}  ({PercentGain:N0}%)"
                        : $"+{Slots:N0}";
}

/// <summary>
/// How hard one slot pool is being pushed, and what could be done about it.
/// </summary>
public sealed record SlotPressure(
    IndustryPool     Pool,
    int              Capacity,
    int              InUse,
    int              Waiting,
    int              Blocked,
    IReadOnlyList<SlotRemedy> Remedies)
{
    public int    Free      => Math.Max(0, Capacity - InUse);
    public double Utilised  => Capacity <= 0 ? 0 : 100.0 * InUse / Capacity;

    /// <summary>
    /// Whether this pool is what is holding work up.
    ///
    /// <para>⚠️ Full is not the same as short. A pool at 100% with nothing queued behind it is a
    /// pool being used properly; it only becomes a bottleneck when there is work that would start
    /// if a slot existed. Reporting every full pool would make the tab noise on a healthy day.</para>
    /// </summary>
    public bool IsBottleneck => Waiting > 0 && Free == 0;
}

/// <summary>
/// A product whose blueprint count, not its slots or its material, is the limit.
/// </summary>
public sealed record PrintPressure(
    int    ProductTypeId,
    string ProductName,
    int    JobsWanted,
    int    Originals,
    int    Busy,
    int    Idle)
{
    /// <summary>How many more originals it would take to run the wanted work in parallel.</summary>
    public int Short => Math.Max(0, JobsWanted - Originals);

    public string Advice => $"Buy {Short:N0} more original(s) — {JobsWanted:N0} job(s) want this "
                          + $"and {Originals:N0} print(s) can run at once"
                          + (Busy > 0 ? $", {Busy:N0} of them already installed." : ".");

    public void OpenItem() => EntityNavigator.Instance.Item(ProductTypeId);
}

/// <summary>
/// What is holding the pipeline up.
///
/// <para>Built from the worklist's own conclusions rather than from a second reading of the same
/// data. The worklist has already decided which jobs cannot start and why; working that out again
/// here would produce a second opinion, and two answers to "why is this stuck" is worse than
/// none.</para>
///
/// <para>Three different ceilings, which is why they are reported separately: slots, blueprints,
/// and material. They are not interchangeable — buying an account does nothing for a shortage of
/// fuel block originals, and neither one helps if the gas never arrived.</para>
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
    /// Slot pressure for each pool, with the remedies quantified.
    ///
    /// <para>Every remedy is a number rather than a suggestion. "Train more" is advice anyone
    /// could give; "+7 slots across four characters, a 21% increase" is a decision someone can
    /// weigh against buying an account.</para>
    /// </summary>
    public async Task<List<SlotPressure>> SlotPressureAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        var candidates = await assignment.LoadCandidatesAsync(ct);
        if (candidates.Count == 0) return [];

        var corps = await assignment.UsableCorporationsAsync(settings.IncludeNonPersonalCorps, ct);

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

            double Pct(int slots) => capacity <= 0 ? 0 : 100.0 * slots / capacity;

            var remedies = new List<SlotRemedy>();

            // ── Train what is already switched on ────────────────────────────
            // The cheapest capacity there is: no new account, no new character, and the slots
            // arrive on people already trusted with the work.
            var trainable = on
                .Select(c => (c, Room: MaxSlotsPerCharacter - c.Capacity.GetValueOrDefault(pool)))
                .Where(x => x.Room > 0)
                .OrderByDescending(x => x.Room)
                .ToList();

            var trainSlots = trainable.Sum(x => x.Room);
            remedies.Add(new SlotRemedy(
                "Train the characters already running this",
                trainSlots,
                trainable.Count == 0
                    ? "Everyone running this pool is already at the skill cap."
                    : $"{trainable.Count} character(s) below the {MaxSlotsPerCharacter}-slot cap: "
                    + Name(trainable.Select(x => (x.c, x.Room))))
            { PercentGain = Pct(trainSlots) });

            // ── Switch on the ones that are off ──────────────────────────────
            // ⚠️ Quantified, not recommended. These are normally off for a reason the app cannot
            // see — a trading alt in another corp, a character whose assets are not the player's
            // to spend — so the number is here to be weighed, not acted on.
            var offSlots = off.Sum(c => c.Capacity.GetValueOrDefault(pool));
            remedies.Add(new SlotRemedy(
                "Enable characters currently switched off for this pool",
                offSlots,
                off.Count == 0
                    ? "Every configured character already runs this pool."
                    : $"{off.Count} character(s) switched off, holding {offSlots:N0} slot(s) today"
                    + (corps is null ? "" : " — usually off for a reason, so read this as a size, not a plan.")
                    + $" {Name(off.Select(c => (c, c.Capacity.GetValueOrDefault(pool))))}")
            { PercentGain = Pct(offSlots) });

            // ── Buy capacity ────────────────────────────────────────────────
            var newSlots = CharactersPerAccount * MaxSlotsPerCharacter;
            remedies.Add(new SlotRemedy(
                $"Add {CharactersPerAccount} characters (one account), trained to the cap",
                newSlots,
                $"{CharactersPerAccount} × {MaxSlotsPerCharacter} slots against the {capacity:N0} "
                + "running now. The training is not instant — this is the ceiling it buys, not "
                + "what it gives on day one.")
            { PercentGain = Pct(newSlots) });

            result.Add(new SlotPressure(pool, capacity, inUse, waiting, blocked, remedies));
        }

        return result;
    }

    /// <summary>
    /// Products where the number of blueprints, not the number of slots, is the ceiling.
    ///
    /// <para><b>⚠️ An original runs one job at a time.</b> Everything else in the tool treats
    /// owning a BPO as owning the ability to build the thing — which is true of the recipe and
    /// false of the throughput. Two fuel block originals is two concurrent jobs however many
    /// slots and however much material are standing by, and no amount of either fixes it.</para>
    ///
    /// <para>Counted against what the worklist wants to run right now, plus what is already
    /// installed: a print locked in a live job is owned but not available, and the gap between
    /// those two is exactly the thing to buy.</para>
    /// </summary>
    public async Task<List<PrintPressure>> PrintPressureAsync(
        IReadOnlyList<WorklistItem> items, CancellationToken ct = default)
    {
        var owned  = await assignment.PrintOwnershipAsync(settings.IncludeNonPersonalCorps, ct);
        var prints = (await blueprints.LoadAllAsync(ct)).Where(owned.Owns).ToList();
        if (prints.Count == 0) return [];

        // Jobs the worklist is asking for, by product. A blocked one counts: it is work that
        // wants a print as much as a startable one does, and often it is blocked FOR the print.
        var wanted = items
            .Where(i => i.Kind == WorklistKind.Job && i.TypeId > 0)
            .GroupBy(i => i.TypeId)
            .ToDictionary(g => g.Key, g => g.Count());
        if (wanted.Count == 0) return [];

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ids = wanted.Keys.ToList();
        var byProduct = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => (p.Activity == "manufacturing" || p.Activity == "reaction")
                     && ids.Contains(p.ProductTypeId))
            .ToDictionaryAsync(p => p.ProductTypeId, p => p.TypeId, ct);

        var result = new List<PrintPressure>();

        foreach (var (typeId, jobs) in wanted)
        {
            if (!byProduct.TryGetValue(typeId, out var bpTypeId)) continue;

            var mine = prints.Where(p => p.TypeId == bpTypeId).ToList();
            if (mine.Count == 0) continue;                       // nothing owned is a different problem

            // ⚠️ Originals only. A copy is consumed by the job it runs, so a stack of copies is
            // throughput a purchase already paid for; an original is the reusable thing whose
            // count is a standing limit.
            var originals = mine.Where(p => p.IsOriginal).ToList();
            if (originals.Count == 0) continue;

            var busy  = originals.Count(p => p.LockedInJob);
            var idle  = originals.Count - busy;

            // Not short unless the work outruns the prints. A single original covering a single
            // job is a pipeline working exactly as intended.
            if (jobs <= originals.Count) continue;

            result.Add(new PrintPressure(
                typeId,
                items.First(i => i.TypeId == typeId).TypeName,
                jobs,
                originals.Count,
                busy,
                idle));
        }

        return result.OrderByDescending(p => p.Short).ThenBy(p => p.ProductName).ToList();
    }

    /// <summary>A few names and their numbers, so a total can be checked against something.</summary>
    private static string Name(IEnumerable<(IndustryCandidate C, int N)> people)
    {
        var list = people.Take(4).Select(x => $"{x.C.Config.CharacterName} +{x.N}").ToList();
        return list.Count == 0 ? "" : string.Join(", ", list);
    }
}
