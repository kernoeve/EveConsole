namespace EveConsole.Services.Pi;

/// <summary>The user's two kinds of colony, told apart by the layout alone.</summary>
public enum PiColonyKind
{
    /// <summary>Nothing producing: a command center, perhaps storage, no extractor or processor.</summary>
    Empty,
    /// <summary>Has at least one extractor control unit. No input, only output: re-run the
    /// extractors before they stop, haul the output off before storage fills.</summary>
    Extractor,
    /// <summary>No extractors, processors only. Haul input in before it runs out; haul output off
    /// before it fills — two separate jobs.</summary>
    Factory,
}

public enum PiFactoryState
{
    /// <summary>Mid-cycle: its inputs are already in it.</summary>
    Running,
    /// <summary>Waiting for a full set of inputs.</summary>
    Idle,
    /// <summary>No schematic, or a schematic the SDE does not know.</summary>
    Unknown,
}

/// <summary>One extractor's program, all of it exact: install, expiry and cycle time are fixed
/// when the program is set, and the per-cycle output is CCP's formula.</summary>
public sealed record PiExtractorProgram(
    long PinId,
    int? ProductTypeId,
    DateTimeOffset? InstallTime,
    DateTimeOffset? ExpiryTime,
    int CycleSeconds,
    int? QtyPerCycle,
    int HeadCount,
    int TotalCycles,
    // Cycles finished by the forecast's moment.
    int CyclesDone,
    IReadOnlyList<long> CycleOutputs,
    long TotalOutput,
    long OutputDone,
    long RemainingOutput,
    // The program has ended by the forecast's moment: the extractor needs re-running.
    bool IsExpired,
    // False when ESI gave no qty_per_cycle, cycle time or product: the outputs are then
    // zero because they are unknown, not because the extractor makes nothing.
    bool YieldKnown,
    // Average output per day over the whole program.
    double PerDay);

/// <summary>A storage facility, launchpad or command center at the forecast's moment.</summary>
public sealed record PiStorageForecast(
    long PinId,
    int TypeId,
    PiPinKind Kind,
    double Capacity,
    double UsedAtSnapshot,
    double UsedAt,
    IReadOnlyDictionary<int, long> ContentsAt,
    // When it first could not take what was sent to it (or was already at capacity).
    // May be before the forecast's moment — it is full now — or after; null when it does not fill
    // within the horizon.
    DateTimeOffset? FullAt)
{
    public double FillAt => Capacity > 0 ? Math.Min(1, UsedAt / Capacity) : 0;
}

public sealed record PiFactoryForecast(
    long PinId,
    int TypeId,
    PiProcessorTier Tier,
    int? SchematicId,
    int? OutputTypeId,
    PiFactoryState StateAt,
    // Since when it has been waiting, if it is idle at the forecast's moment.
    DateTimeOffset? IdleSince,
    // Cycles completed between the snapshot and the forecast's moment.
    int CyclesCompleted,
    IReadOnlyDictionary<int, long> BufferAt);

/// <summary>An input a colony must be supplied with because nothing on it makes it.</summary>
public sealed record PiInputForecast(
    int TypeId,
    long OnHandAtSnapshot,
    long OnHandAt,
    // What the colony uses a day when fully supplied.
    double PerDay,
    // When a processor first could not start for lack of it; null when that does not
    // happen within the horizon. Equal to the snapshot when it had already run out.
    DateTimeOffset? RunsOutAt);

/// <summary>A type's steady flow through the colony, per day.</summary>
public sealed record PiFlow(
    int TypeId,
    PiTier? Tier,
    double ProducedPerDay,
    double ConsumedPerDay,
    // Consumed but made nowhere on the colony: it has to be brought in.
    double ImportedPerDay,
    // Made and not used on the colony: it piles up and has to be taken off.
    double ExportedPerDay);

/// <summary>What kind of thing was thrown away.</summary>
public enum PiLossKind
{
    /// <summary>Something a factory made — on a finished colony, the product it exists for.</summary>
    Product,
    /// <summary>Raw material (P0) an extractor brought up and storage had no room for: extraction
    /// outpaces the factories, the sign of a program larger than the colony can use.</summary>
    Raw,
}

/// <summary>Output of one type thrown away at one pin within a period.</summary>
/// <param name="PinId">The pin that turned it away — the storage or launchpad it was routed to
/// (the first route for that type) — or, with <paramref name="NoRoute"/>, the pin that made it.</param>
/// <param name="NoRoute">The maker has no route for the type at all: nowhere to send it.</param>
/// <param name="Since">The first moment within the period it was thrown away there.</param>
public sealed record PiLoss(int TypeId, PiTier? Tier, PiLossKind Kind, long PinId, int PinTypeId, PiPinKind PinKind,
                            bool NoRoute, long Units, DateTimeOffset Since);

/// <summary>
/// The factories running one schematic, and the time they spent waiting for input within a
/// period.
/// </summary>
/// <param name="IdleSeconds">Waiting for a full set of input, summed over the factories.</param>
/// <param name="ExpectedIdleSeconds">The waiting the potential already counts on: where the
/// input is made on the colony and there is not enough of it, the steady flows run these factories
/// at a share of full rate (<see cref="PiEngine.Flows"/>), and the rest of the time they wait by
/// design — an extractor planet's factories are built for the program's first, richest cycles.
/// Zero where the input is brought in.</param>
/// <param name="IsFinal">Its output is one the colony exports, not one it uses itself.</param>
public sealed record PiIdle(int SchematicId, int OutputTypeId, int OutputQuantity, int CycleSeconds, int Factories,
                            double IdleSeconds, double ExpectedIdleSeconds, int CyclesCompleted, bool IsFinal)
{
    /// <summary>Waiting beyond what the potential counts on: the part that costs output.</summary>
    public double ExcessIdleSeconds => Math.Max(0, IdleSeconds - ExpectedIdleSeconds);

    /// <summary>What the factories would have made in <see cref="ExcessIdleSeconds"/>, to the
    /// nearest unit (the time is summed over factories, so a cycle may be shared between them).</summary>
    public long MissedUnits => CycleSeconds > 0
        ? (long)Math.Floor(ExcessIdleSeconds / CycleSeconds * OutputQuantity + 0.5)
        : 0;
}

/// <summary>
/// One colony over a period ahead, two ways: what it <b>could</b> make running as built (the
/// steady flows over the period) and what the simulation says it <b>will</b> — and where the
/// difference goes.
///
/// <para><b>The period.</b> An extractor planet's runs from the forecast's moment until its
/// extractors stop (the latest program end): idle after that is expected, and re-running them is
/// a task of its own. A factory planet's — or an extractor planet's whose programs run past the
/// horizon — is the horizon, 30 days.</para>
///
/// <para><b>How the numbers relate.</b> Potential − forecast ≈ destroyed + idle (final products
/// only). Destroyed output was made and thrown away; idle output was never made. Neither line is
/// exact: what remains is part-finished cycles at both ends of the period, stock still in storage
/// at the end of it (an extractor's last cycle lands as the period closes), the input cost and
/// charges that output which was never made or never kept would have carried, and — on a chain —
/// an idle lower tier, whose missing output also idles the tier above, so only the final tier's
/// idle is a loss of its own.</para>
/// </summary>
public sealed class PiPeriodForecast
{
    public static readonly PiPeriodForecast Empty = new();

    public DateTimeOffset From { get; init; }
    public DateTimeOffset To   { get; init; }

    /// <summary>Ends when the extractors stop; false for the horizon.</summary>
    public bool UntilExtractorsStop { get; init; }

    public TimeSpan Length => To > From ? To - From : TimeSpan.Zero;
    public double   Days   => Length.TotalDays;

    /// <summary>No time to forecast: the extractors have already stopped.</summary>
    public bool IsEmpty => Length <= TimeSpan.Zero;

    /// <summary>
    /// The potential: the colony's steady flows per day, worked as <see cref="PiEngine.Flows"/>
    /// does, with each extractor at what it yields within the period — its exact remaining cycles,
    /// not its whole program's average, which the decay puts above what is left.
    /// </summary>
    public IReadOnlyList<PiFlow> Flows { get; init; } = [];

    /// <summary>The types the colony makes and does not use: what it exports.</summary>
    public IReadOnlyCollection<int> ExportTypes { get; init; } = [];

    /// <summary>Units of each type that reached storage, launchpads or the command center.</summary>
    public IReadOnlyDictionary<int, long> Delivered        { get; init; } = new Dictionary<int, long>();
    /// <summary>Units of each type factories took back out of them.</summary>
    public IReadOnlyDictionary<int, long> TakenFromStorage { get; init; } = new Dictionary<int, long>();
    /// <summary>Units of each brought-in type factories used: input for each cycle started.</summary>
    public IReadOnlyDictionary<int, long> InputsConsumed   { get; init; } = new Dictionary<int, long>();

    public IReadOnlyList<PiLoss> Losses { get; init; } = [];
    public IReadOnlyList<PiIdle> Idle   { get; init; } = [];

    /// <summary>The forecast's output: of each type the colony exports, what is left in storage
    /// for taking off — what reached it less what factories took back.</summary>
    public IReadOnlyDictionary<int, long> OutputUnits => ExportTypes
        .ToDictionary(t => t, t => Math.Max(0, Delivered.GetValueOrDefault(t) - TakenFromStorage.GetValueOrDefault(t)));
}

/// <summary>Everything the engine says about one colony at one moment.</summary>
public sealed class PiColonyForecast
{
    public required PiColonyLayout Layout { get; init; }
    public required PiColonyKind   Kind   { get; init; }

    /// <summary>The snapshot's moment: the colony's last_update.</summary>
    public DateTimeOffset SnapshotAt { get; init; }

    /// <summary>The moment forecast for.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>⚠️ True whenever <see cref="At"/> is after the snapshot: every storage, factory
    /// and input figure is then simulated, not read. Extractor programs stay exact.</summary>
    public bool IsEstimate => At > SnapshotAt;

    /// <summary>How old the snapshot is at <see cref="At"/>.</summary>
    public TimeSpan DataAge => At > SnapshotAt ? At - SnapshotAt : TimeSpan.Zero;

    /// <summary>How far the simulation ran; nothing after it is known.</summary>
    public DateTimeOffset HorizonEnd { get; init; }

    public IReadOnlyList<PiExtractorProgram> Extractors { get; init; } = [];
    public IReadOnlyList<PiStorageForecast>  Storage    { get; init; } = [];
    public IReadOnlyList<PiFactoryForecast>  Factories  { get; init; } = [];
    public IReadOnlyList<PiInputForecast>    Inputs     { get; init; } = [];
    public IReadOnlyList<PiFlow>             Flows      { get; init; } = [];

    /// <summary>Output thrown away by the forecast's moment because there was nowhere to put it,
    /// by type.</summary>
    public IReadOnlyDictionary<int, long> LostAt { get; init; } = new Dictionary<int, long>();

    /// <summary>The period ahead from <see cref="At"/>: potential against forecast, destroyed and
    /// idle output.</summary>
    public PiPeriodForecast Period { get; init; } = PiPeriodForecast.Empty;

    /// <summary>The rate the charges below are worked at.</summary>
    public PiChargeRate? Rate { get; init; }
    public IReadOnlyList<PiTypeCharge> Charges { get; init; } = [];
    public double ExportChargesPerDay => Charges.Sum(c => c.ExportPerDay);
    public double ImportChargesPerDay => Charges.Sum(c => c.ImportPerDay);

    /// <summary>The earliest extractor expiry: when the first one needs re-running.</summary>
    public DateTimeOffset? ExtractorsStopAt =>
        Extractors.Where(e => e.ExpiryTime is not null).Min(e => e.ExpiryTime);

    /// <summary>The earliest moment any storage pin fills.</summary>
    public DateTimeOffset? StorageFullAt => Storage.Where(s => s.FullAt is not null).Min(s => s.FullAt);

    /// <summary>The earliest moment an input runs out.</summary>
    public DateTimeOffset? InputsRunOutAt => Inputs.Where(i => i.RunsOutAt is not null).Min(i => i.RunsOutAt);
}

/// <summary>
/// The Planetary Industry engine: classification, extractor programs, a forecast from the
/// snapshot to any moment, steady flows per day, and charges. Pure — no database, no clock — so
/// it runs the same on a stored colony and on a constructed one (tools/PiEngineCheck).
///
/// <para><b>The forecast.</b> ESI describes a colony only as of its last_update. From there the
/// engine replays the colony event by event: each extractor cycle's output as it completes, and
/// each processor's cycles, moved along the colony's routes into storage, launchpads and the
/// command center, bounded by their capacity. Everything after the snapshot is an estimate, and
/// the forecast says so (<see cref="PiColonyForecast.IsEstimate"/>, <see cref="PiColonyForecast.DataAge"/>).</para>
///
/// <para><b>What the game does not document, and what is assumed instead.</b></para>
/// <list type="bullet">
/// <item>Output moves at the end of a cycle, along the source's routes for that type in route-id
/// order: first each route up to its own quantity, then whatever is left to any route with room.
/// Output with nowhere to go is lost (counted in <see cref="PiColonyForecast.LostAt"/>) — the
/// documented behaviour for a processor that is sent more than it can hold, assumed here for full
/// storage too.</item>
/// <item>A processor holds at most one cycle's worth of each input (as documented); anything sent
/// beyond that is lost.</item>
/// <item>An idle processor takes what it lacks from the storage pins routed to it, in route-id
/// order, as soon as it is there, and starts the moment it holds a full set — consuming the set at
/// the start and delivering at the end. Within one moment, extractor deliveries happen first, then
/// finished cycles, then starts, lower tiers first.</item>
/// <item>A processor is running at the snapshot when its last cycle start plus its cycle time is
/// still ahead: the game would otherwise have started a newer one.</item>
/// <item>An extractor's cycle i ends at install + (i + 1) × cycle time and yields CCP's formula
/// for i; its output goes nowhere but along its routes (it has no storage of its own).</item>
/// </list>
///
/// <para><b>Steady flows</b> (<see cref="PiFlow"/>) are not the simulation: they are what the
/// colony makes and uses per day when it runs as built — extractors at their program's average,
/// processors at full rate where their input is brought in, and scaled down where it is made on
/// the colony and there is not enough of it. They are what hauling quantities and profit are
/// worked from.</para>
///
/// <para><b>The period</b> (<see cref="PiPeriodForecast"/>) sets the two against each other over
/// the time ahead: the steady flows as the potential, and the same simulation run, counting what
/// reaches storage, what is thrown away and where, and how long each factory waits for input.
/// ⚠️ Game rule: output with nowhere to go is destroyed and stops nothing — a factory keeps taking
/// input and running while its product is thrown away. A factory stops only for want of
/// input.</para>
/// </summary>
public static class PiEngine
{
    /// <summary>How far past the forecast's moment the simulation runs to find "full at" and
    /// "runs out at".</summary>
    public static readonly TimeSpan DefaultHorizon = TimeSpan.FromDays(30);

    /// <summary>Classification from the layout: any extractor makes it an extractor planet.</summary>
    public static PiColonyKind Classify(PiColonyLayout layout, PiStaticData sd)
    {
        var kinds = layout.Pins.Select(p => KindOf(p, sd)).ToList();
        if (kinds.Contains(PiPinKind.ExtractorControlUnit)) return PiColonyKind.Extractor;
        if (kinds.Contains(PiPinKind.Processor))            return PiColonyKind.Factory;
        return PiColonyKind.Empty;
    }

    /// <summary>
    /// A pin's kind. The SDE group decides; failing that (no SDE yet), the layout itself: only an
    /// extractor control unit has extractor details, only a processor a schematic.
    /// </summary>
    public static PiPinKind KindOf(PiLayoutPin pin, PiStaticData sd)
    {
        var kind = sd.KindOf(pin.TypeId);
        if (kind != PiPinKind.Unknown) return kind;
        if (pin.Extractor is not null) return PiPinKind.ExtractorControlUnit;
        if (pin.SchematicId is not null) return PiPinKind.Processor;
        return PiPinKind.Unknown;
    }

    /// <summary>One extractor's program at a moment.</summary>
    public static PiExtractorProgram Program(PiLayoutPin pin, PiStaticData sd, DateTimeOffset at)
    {
        var x       = pin.Extractor;
        var cycle   = x?.CycleSeconds ?? 0;
        var install = pin.InstallTime;
        var expiry  = pin.ExpiryTime;

        var total = install is { } i && expiry is { } e ? PiYield.TotalCycles(i, e, cycle) : 0;
        var known = x is { QtyPerCycle: > 0, CycleSeconds: > 0, ProductTypeId: not null } && total > 0;
        var outputs = known
            ? PiYield.ProgramOutputs(x!.QtyPerCycle!.Value, cycle, total, sd.DecayFactor, sd.NoiseFactor)
            : new long[Math.Max(0, total)];

        var done = 0;
        if (install is { } start && cycle > 0 && at > start)
            done = (int)Math.Min(total, (long)(at - start).TotalSeconds / cycle);

        var totalOut = outputs.Sum();
        var doneOut  = outputs.Take(done).Sum();
        var days     = total * (double)cycle / 86_400;

        return new PiExtractorProgram(
            pin.PinId, x?.ProductTypeId, install, expiry, cycle, x?.QtyPerCycle, x?.HeadCount ?? 0,
            total, done, outputs, totalOut, doneOut, totalOut - doneOut,
            IsExpired: expiry is { } end && end <= at,
            YieldKnown: known,
            PerDay: days > 0 ? totalOut / days : 0);
    }

    /// <summary>
    /// The colony at <paramref name="at"/>: exact where the data allows it, simulated from the
    /// snapshot where it does not.
    /// </summary>
    /// <param name="rate">The planet's tax rate; null leaves the charges empty.</param>
    /// <param name="horizon">How far past <paramref name="at"/> to look for "full at" and "runs out
    /// at"; <see cref="DefaultHorizon"/> when null.</param>
    public static PiColonyForecast Forecast(PiColonyLayout layout, PiStaticData sd, DateTimeOffset at,
                                            PiChargeRate? rate = null, TimeSpan? horizon = null)
    {
        var kind       = Classify(layout, sd);
        var t0         = layout.LastUpdate;
        // Nothing before the snapshot is known, so neither the horizon nor the period can start
        // earlier than it.
        var from       = at > t0 ? at : t0;
        var horizonEnd = from + (horizon ?? DefaultHorizon);

        var extractors = layout.Pins
            .Where(p => KindOf(p, sd) == PiPinKind.ExtractorControlUnit)
            .Select(p => Program(p, sd, at))
            .ToList();

        var (to, untilStop) = PeriodEnd(kind, extractors, from, horizonEnd);

        var sim = new Simulation(layout, sd, extractors, t0, horizonEnd, from, to);
        var snap = sim.Run(at);

        var flows = Flows(layout, sd, extractors);

        var imported = flows.Where(f => f.ImportedPerDay > 0).ToList();
        var inputs = imported
            .Select(f => new PiInputForecast(
                f.TypeId,
                sim.InitialTotal(f.TypeId),
                snap.Totals.GetValueOrDefault(f.TypeId),
                f.ImportedPerDay,
                sim.RunsOut.TryGetValue(f.TypeId, out var when) ? when : null))
            .ToList();

        return new PiColonyForecast
        {
            Layout     = layout,
            Kind       = kind,
            SnapshotAt = t0,
            At         = at,
            HorizonEnd = horizonEnd,
            Extractors = extractors,
            Storage    = snap.Storage,
            Factories  = snap.Factories,
            Inputs     = inputs,
            Flows      = flows,
            LostAt     = snap.Lost,
            Period     = Period(layout, sd, extractors, sim, from, to, untilStop),
            Rate       = rate,
            Charges    = rate is null ? [] : ChargesFor(flows, rate.Rate),
        };
    }

    /// <summary>
    /// Where the period ends: when an extractor planet's extractors stop — the latest program end,
    /// since the colony makes something until the last one stops — unless that is past the
    /// horizon; the horizon otherwise. An extractor planet whose extractors have all stopped has an
    /// empty period.
    /// </summary>
    private static (DateTimeOffset To, bool UntilExtractorsStop) PeriodEnd(
        PiColonyKind kind, IReadOnlyList<PiExtractorProgram> extractors, DateTimeOffset from, DateTimeOffset horizonEnd)
    {
        if (kind != PiColonyKind.Extractor) return (horizonEnd, false);
        var last = extractors.Where(x => x.ExpiryTime is not null).Max(x => x.ExpiryTime);
        if (last is not { } stop || stop > horizonEnd) return (horizonEnd, false);
        return (stop > from ? stop : from, true);
    }

    private static PiPeriodForecast Period(PiColonyLayout layout, PiStaticData sd,
                                           IReadOnlyList<PiExtractorProgram> extractors, Simulation sim,
                                           DateTimeOffset from, DateTimeOffset to, bool untilStop)
    {
        var length = to > from ? (to - from).TotalSeconds : 0;
        var days   = length / 86_400;

        // Each extractor at what it yields within the period: the cycles that end in it.
        var inPeriod = extractors
            .Select(x => x with { PerDay = days > 0 ? OutputBetween(x, from, to) / days : 0 })
            .ToList();
        var flows  = Flows(layout, sd, inPeriod, out var shares);
        var export = flows.Where(f => f.ExportedPerDay > 0 && f.ImportedPerDay <= 0).Select(f => f.TypeId).ToHashSet();

        var idle = sim.FactoryPins
            .GroupBy(p => p.Schematic!.SchematicId)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var s     = g.First().Schematic!;
                var share = shares.GetValueOrDefault(s.SchematicId, 1);
                return new PiIdle(s.SchematicId, s.OutputTypeId, s.OutputQuantity, s.CycleSeconds, g.Count(),
                                  g.Sum(p => p.IdleSeconds), g.Count() * (1 - share) * length,
                                  g.Sum(p => p.CyclesInPeriod), export.Contains(s.OutputTypeId));
            })
            .ToList();

        return new PiPeriodForecast
        {
            From                = from,
            To                  = to,
            UntilExtractorsStop = untilStop,
            Flows               = flows,
            ExportTypes         = export,
            Delivered           = sim.Delivered,
            TakenFromStorage    = sim.Taken,
            InputsConsumed      = sim.Consumed,
            Losses              = sim.LossesIn(sd),
            Idle                = idle,
        };
    }

    /// <summary>An extractor's output from the cycles that end after <paramref name="from"/> and
    /// by <paramref name="to"/>.</summary>
    private static long OutputBetween(PiExtractorProgram x, DateTimeOffset from, DateTimeOffset to)
    {
        if (!x.YieldKnown || x.InstallTime is not { } install || x.CycleSeconds <= 0) return 0;
        long sum = 0;
        for (var i = 0; i < x.CycleOutputs.Count; i++)
        {
            var ends = install.AddSeconds((double)(i + 1) * x.CycleSeconds);
            if (ends > to) break;
            if (ends > from) sum += x.CycleOutputs[i];
        }
        return sum;
    }

    /// <summary>Charges per unit and per day for every type that crosses the planet's edge.</summary>
    public static IReadOnlyList<PiTypeCharge> ChargesFor(IReadOnlyList<PiFlow> flows, double rate)
        => flows
            .Where(f => f.Tier is not null && (f.ExportedPerDay > 0 || f.ImportedPerDay > 0))
            .Select(f =>
            {
                var tier = f.Tier!.Value;
                var ex   = PiCharges.ExportPerUnit(tier, rate);
                var im   = PiCharges.ImportPerUnit(tier, rate);
                return new PiTypeCharge(f.TypeId, tier, ex, im, ex * f.ExportedPerDay, im * f.ImportedPerDay);
            })
            .ToList();

    // ── Steady flows ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the colony makes, uses, needs brought in and leaves to be taken off, per day, running
    /// as built. Processors are taken a tier at a time from the bottom: each draws on what the
    /// tiers below made, shared in proportion when there is not enough, and on imports — without
    /// limit — for anything made nowhere on the colony.
    /// </summary>
    public static IReadOnlyList<PiFlow> Flows(PiColonyLayout layout, PiStaticData sd,
                                              IReadOnlyList<PiExtractorProgram>? extractors = null)
        => Flows(layout, sd, extractors, out _);

    /// <param name="shares">The share of full rate each schematic runs at — below 1 only where its
    /// input is made on the colony and there is not enough of it.</param>
    private static IReadOnlyList<PiFlow> Flows(PiColonyLayout layout, PiStaticData sd,
                                               IReadOnlyList<PiExtractorProgram>? extractors,
                                               out Dictionary<int, double> shares)
    {
        var fractions = new Dictionary<int, double>();
        shares = fractions;
        extractors ??= layout.Pins
            .Where(p => KindOf(p, sd) == PiPinKind.ExtractorControlUnit)
            .Select(p => Program(p, sd, layout.LastUpdate))
            .ToList();

        var produced = new Dictionary<int, double>();
        var consumed = new Dictionary<int, double>();
        var imported = new Dictionary<int, double>();
        var supply   = new Dictionary<int, double>();

        foreach (var x in extractors)
            if (x.ProductTypeId is int p && x.PerDay > 0)
            {
                produced[p] = produced.GetValueOrDefault(p) + x.PerDay;
                supply[p]   = supply.GetValueOrDefault(p)   + x.PerDay;
            }

        var factories = layout.Pins
            .Where(p => KindOf(p, sd) == PiPinKind.Processor && p.SchematicId is int s && sd.Schematics.ContainsKey(s))
            .Select(p => sd.Schematics[p.SchematicId!.Value])
            .ToList();

        // Made somewhere on the colony: an extractor's product or a processor's output.
        var local = extractors.Where(x => x.ProductTypeId is not null).Select(x => x.ProductTypeId!.Value)
            .Concat(factories.Select(f => f.OutputTypeId))
            .ToHashSet();

        foreach (var tierGroup in factories
                     .GroupBy(f => (int)(sd.TierOf(f.OutputTypeId) ?? PiTier.P1))
                     .OrderBy(g => g.Key))
        {
            // Full-rate demand on each locally made input across the tier, then the share of it
            // the supply covers.
            var demand = new Dictionary<int, double>();
            foreach (var f in tierGroup)
                foreach (var input in f.Inputs.Where(i => local.Contains(i.TypeId)))
                    demand[input.TypeId] = demand.GetValueOrDefault(input.TypeId) + input.Quantity * RunsPerDay(f);

            var share = demand.ToDictionary(
                d => d.Key,
                d => d.Value > 0 ? Math.Min(1, supply.GetValueOrDefault(d.Key) / d.Value) : 1);

            var made = new Dictionary<int, double>();
            foreach (var f in tierGroup)
            {
                var runs     = RunsPerDay(f);
                var fraction = f.Inputs.Where(i => local.Contains(i.TypeId))
                    .Select(i => share[i.TypeId]).DefaultIfEmpty(1).Min();
                fractions[f.SchematicId] = fraction;

                foreach (var input in f.Inputs)
                {
                    var used = input.Quantity * runs * fraction;
                    consumed[input.TypeId] = consumed.GetValueOrDefault(input.TypeId) + used;
                    if (local.Contains(input.TypeId))
                        supply[input.TypeId] = supply.GetValueOrDefault(input.TypeId) - used;
                    else
                        imported[input.TypeId] = imported.GetValueOrDefault(input.TypeId) + used;
                }

                var output = f.OutputQuantity * runs * fraction;
                produced[f.OutputTypeId] = produced.GetValueOrDefault(f.OutputTypeId) + output;
                made[f.OutputTypeId]     = made.GetValueOrDefault(f.OutputTypeId) + output;
            }

            // Only after the whole tier has drawn: a tier does not feed itself.
            foreach (var (type, amount) in made)
                supply[type] = supply.GetValueOrDefault(type) + amount;
        }

        return produced.Keys.Concat(consumed.Keys).Concat(imported.Keys).Distinct().Order()
            .Select(t => new PiFlow(
                t, sd.TierOf(t),
                produced.GetValueOrDefault(t),
                consumed.GetValueOrDefault(t),
                imported.GetValueOrDefault(t),
                Leftover(supply.GetValueOrDefault(t))))
            .ToList();

        static double RunsPerDay(PiSchematic s) => s.CycleSeconds > 0 ? 86_400.0 / s.CycleSeconds : 0;

        // ⚠️ Supply less what a scaled-down tier used is zero in arithmetic and a few 1e-11 in
        // doubles: without this, raw material the factories use up entirely reads as "exported"
        // — an output type, a haul line of nothing, and stock counted as output.
        static double Leftover(double left) => left > 1e-6 ? left : 0;
    }

    // ── The simulation ──────────────────────────────────────────────────────────────────

    private sealed record Snapshot(
        IReadOnlyList<PiStorageForecast> Storage,
        IReadOnlyList<PiFactoryForecast> Factories,
        IReadOnlyDictionary<int, long>   Totals,
        IReadOnlyDictionary<int, long>   Lost);

    private sealed class SimPin
    {
        public required PiLayoutPin Source { get; init; }
        public required PiPinKind   Kind   { get; init; }
        public double Capacity { get; init; }
        public PiProcessorTier Tier { get; init; }
        public PiSchematic? Schematic { get; init; }
        public Dictionary<int, long> Stock { get; } = [];

        public bool IsStorage => Kind is PiPinKind.Storage or PiPinKind.Launchpad or PiPinKind.CommandCenter;
        public bool Running;
        public DateTimeOffset RunEnds;
        public DateTimeOffset? IdleSince;
        public int Cycles;
        public DateTimeOffset? FullAt;
        public double UsedAtStart;
        // Within the period: time spent waiting for input, and cycles finished.
        public double IdleSeconds;
        public int CyclesInPeriod;
    }

    private sealed class Simulation
    {
        private readonly PiStaticData _sd;
        private readonly DateTimeOffset _t0, _end;
        private readonly Dictionary<long, SimPin> _pins = [];
        private readonly List<SimPin> _factories;
        private readonly Dictionary<long, List<PiLayoutRoute>> _pushFrom = [];
        private readonly Dictionary<long, List<PiLayoutRoute>> _pullInto = [];
        private readonly List<(DateTimeOffset At, long Pin, int Type, long Qty)> _extractorEvents = [];
        private readonly HashSet<int> _local = [];
        private readonly Dictionary<int, long> _initialTotals = [];
        private readonly Dictionary<int, long> _lost = [];

        // The period (from, to]: what happens after its first moment and by its last. A cycle
        // finishing at the very start belongs to the moment before; one finishing as it closes, to it.
        private readonly DateTimeOffset _from, _to;
        private readonly Dictionary<(int Type, long Pin, bool NoRoute), (long Units, DateTimeOffset Since)> _periodLost = [];

        /// <summary>When each type made nowhere on the colony first stopped a processor.</summary>
        public Dictionary<int, DateTimeOffset> RunsOut { get; } = [];

        /// <summary>Within the period: into storage, taken back out by factories, and brought-in
        /// input used.</summary>
        public Dictionary<int, long> Delivered { get; } = [];
        public Dictionary<int, long> Taken     { get; } = [];
        public Dictionary<int, long> Consumed  { get; } = [];

        public IReadOnlyList<SimPin> FactoryPins => _factories;

        public long InitialTotal(int type) => _initialTotals.GetValueOrDefault(type);

        private bool InPeriod(DateTimeOffset t) => t > _from && t <= _to;

        public Simulation(PiColonyLayout layout, PiStaticData sd, IReadOnlyList<PiExtractorProgram> extractors,
                          DateTimeOffset t0, DateTimeOffset end, DateTimeOffset from, DateTimeOffset to)
        {
            _sd = sd; _t0 = t0; _end = end; _from = from; _to = to;

            foreach (var p in layout.Pins)
            {
                var kind = KindOf(p, sd);
                PiSchematic? schematic = kind == PiPinKind.Processor && p.SchematicId is int s
                    && sd.Schematics.TryGetValue(s, out var found) ? found : null;
                var tier = sd.PinTypes.TryGetValue(p.TypeId, out var pt) ? pt.ProcessorTier : PiProcessorTier.None;
                if (kind == PiPinKind.Processor && tier == PiProcessorTier.None && schematic is not null)
                    tier = PiStaticDataLoader.ProcessorTierFor(sd.TierOf(schematic.OutputTypeId));

                var capacity = sd.CapacityOf(p.TypeId);
                if (capacity <= 0) capacity = PiTiers.FallbackCapacity(kind);

                var pin = new SimPin { Source = p, Kind = kind, Capacity = capacity, Tier = tier, Schematic = schematic };
                foreach (var (type, amount) in p.Contents)
                    if (amount > 0)
                    {
                        pin.Stock[type] = amount;
                        _initialTotals[type] = _initialTotals.GetValueOrDefault(type) + amount;
                    }
                pin.UsedAtStart = Used(pin);
                // Full already: no room for another unit of the smallest thing it holds.
                if (pin.IsStorage && capacity > 0 && pin.Stock.Count > 0
                    && capacity - pin.UsedAtStart < pin.Stock.Keys.Min(sd.VolumeOf))
                    pin.FullAt = t0;
                _pins[p.PinId] = pin;
            }

            foreach (var r in layout.Routes.OrderBy(r => r.RouteId))
            {
                if (!_pins.TryGetValue(r.SourcePinId, out var src) || !_pins.ContainsKey(r.DestinationPinId)) continue;
                var into = src.IsStorage ? _pullInto : _pushFrom;
                var key  = src.IsStorage ? r.DestinationPinId : r.SourcePinId;
                if (!into.TryGetValue(key, out var list)) into[key] = list = [];
                list.Add(r);
            }

            _factories = _pins.Values
                // ⚠️ A zero cycle time would finish and restart at the same moment forever.
                .Where(p => p.Kind == PiPinKind.Processor && p.Schematic is { CycleSeconds: > 0 })
                .OrderBy(p => (int)(sd.TierOf(p.Schematic!.OutputTypeId) ?? PiTier.P1))
                .ThenBy(p => p.Source.PinId)
                .ToList();

            foreach (var x in extractors)
            {
                if (x.ProductTypeId is not int product) continue;
                _local.Add(product);
                if (!x.YieldKnown || x.InstallTime is not { } install) continue;
                for (var i = 0; i < x.TotalCycles; i++)
                {
                    var ends = install.AddSeconds((double)(i + 1) * x.CycleSeconds);
                    if (ends <= t0) continue;
                    if (ends > end) break;
                    _extractorEvents.Add((ends, x.PinId, product, x.CycleOutputs[i]));
                }
            }
            _extractorEvents.Sort((a, b) => a.At.CompareTo(b.At));
            foreach (var f in _factories) _local.Add(f.Schematic!.OutputTypeId);

            // Running at the snapshot when the last cycle has not finished yet.
            //
            // ⚠️ Not when that "cycle" started at the snapshot itself. The game stamps a processor's
            // last_cycle_start with the moment the colony is saved when its schematic is set, input
            // or not: four high-tech plants on a colony that had never been given any input all
            // read "started" at the second of the colony's last_update, and were simulated running
            // and delivering product that was never made. A cycle the game really started has its
            // own, earlier moment; one stamped with the save is taken as the schematic being set.
            // (A factory fed by hand at the very moment of the save loses its first cycle here —
            // the lesser error.)
            foreach (var f in _factories)
                if (f.Source.LastCycleStart is { } started
                    && t0 - started >= TimeSpan.FromSeconds(1)
                    && started.AddSeconds(f.Schematic!.CycleSeconds) is var ends && ends > t0)
                {
                    f.Running = true;
                    f.RunEnds = ends;
                }
        }

        public Snapshot Run(DateTimeOffset at)
        {
            Snapshot? snap = at <= _t0 ? Take(at) : null;

            StartIdle(_t0);

            var next = 0;
            while (true)
            {
                DateTimeOffset? nextAt = next < _extractorEvents.Count ? _extractorEvents[next].At : null;
                foreach (var f in _factories)
                    if (f.Running && (nextAt is null || f.RunEnds < nextAt)) nextAt = f.RunEnds;
                if (nextAt is not { } t || t > _end) break;

                if (snap is null && t > at) snap = Take(at);

                while (next < _extractorEvents.Count && _extractorEvents[next].At == t)
                {
                    var e = _extractorEvents[next++];
                    Deliver(e.Pin, e.Type, e.Qty, t);
                }

                foreach (var f in _factories)
                    if (f.Running && f.RunEnds == t)
                    {
                        f.Running = false;
                        f.Cycles++;
                        if (InPeriod(t)) f.CyclesInPeriod++;
                        Deliver(f.Source.PinId, f.Schematic!.OutputTypeId, f.Schematic.OutputQuantity, t);
                    }

                StartIdle(t);
            }

            // Still waiting when the run ends: waiting to the end of the period.
            foreach (var f in _factories)
                if (!f.Running && f.IdleSince is { } since) AddIdle(f, since, _to);

            snap ??= Take(at);

            // "Full at" is a fact about the whole run, not about the moment of the snapshot: a
            // store that fills after it still fills, and that date is the point of asking.
            return snap with
            {
                Storage = snap.Storage.Select(s => s with { FullAt = _pins[s.PinId].FullAt }).ToList(),
            };
        }

        private void StartIdle(DateTimeOffset t)
        {
            foreach (var f in _factories)
                if (!f.Running) TryStart(f, t);
        }

        private void TryStart(SimPin f, DateTimeOffset t)
        {
            var s = f.Schematic!;
            var short_ = new List<int>();
            foreach (var input in s.Inputs)
            {
                var have = f.Stock.GetValueOrDefault(input.TypeId);
                if (have < input.Quantity) have += Pull(f, input.TypeId, input.Quantity - have, t);
                if (have < input.Quantity) short_.Add(input.TypeId);
            }

            if (short_.Count == 0)
            {
                foreach (var input in s.Inputs)
                {
                    Take(f, input.TypeId, input.Quantity);
                    if (!_local.Contains(input.TypeId) && InPeriod(t))
                        Consumed[input.TypeId] = Consumed.GetValueOrDefault(input.TypeId) + input.Quantity;
                }
                if (f.IdleSince is { } since) AddIdle(f, since, t);
                f.Running   = true;
                f.RunEnds   = t.AddSeconds(s.CycleSeconds);
                f.IdleSince = null;
                return;
            }

            f.IdleSince ??= t;
            // An input nothing on the colony makes cannot come back on its own: that is the
            // moment it ran out.
            foreach (var type in short_)
                if (!_local.Contains(type) && !RunsOut.ContainsKey(type))
                    RunsOut[type] = t;
        }

        /// <summary>Moves up to <paramref name="want"/> of a type into a processor from the
        /// storage routed to it.</summary>
        /// <summary>Adds the part of a wait from <paramref name="since"/> to <paramref name="until"/>
        /// that falls in the period.</summary>
        private void AddIdle(SimPin f, DateTimeOffset since, DateTimeOffset until)
        {
            var a = since > _from ? since : _from;
            var b = until < _to ? until : _to;
            if (b > a) f.IdleSeconds += (b - a).TotalSeconds;
        }

        private long Pull(SimPin f, int type, long want, DateTimeOffset t)
        {
            if (!_pullInto.TryGetValue(f.Source.PinId, out var routes)) return 0;
            long got = 0;
            foreach (var r in routes)
            {
                if (got >= want) break;
                if (r.ContentTypeId != type) continue;
                var src  = _pins[r.SourcePinId];
                var take = Math.Min(want - got, src.Stock.GetValueOrDefault(type));
                if (take <= 0) continue;
                Take(src, type, take);
                Add(f, type, take);
                got += take;
                if (InPeriod(t)) Taken[type] = Taken.GetValueOrDefault(type) + take;
            }
            return got;
        }

        /// <summary>Sends a cycle's output along the producer's routes. What finds no room is lost.</summary>
        private void Deliver(long producer, int type, long qty, DateTimeOffset t)
        {
            var remaining = qty;
            var mine = _pushFrom.TryGetValue(producer, out var routes)
                ? routes.Where(r => r.ContentTypeId == type).ToList()
                : [];
            foreach (var r in mine)
            {
                if (remaining <= 0) break;
                var give = r.Quantity > 0 ? Math.Min(remaining, (long)Math.Ceiling(r.Quantity)) : remaining;
                remaining -= Accept(_pins[r.DestinationPinId], type, give, t);
            }
            foreach (var r in mine)
            {
                if (remaining <= 0) break;
                remaining -= Accept(_pins[r.DestinationPinId], type, remaining, t);
            }
            if (remaining <= 0) return;

            _lost[type] = _lost.GetValueOrDefault(type) + remaining;
            if (!InPeriod(t)) return;
            // Charged to the pin it was meant for — the first route's: when every route turned it
            // away, that is the one the owner set up to take it.
            var key = mine.Count > 0 ? (type, mine[0].DestinationPinId, false) : (type, producer, true);
            _periodLost[key] = _periodLost.TryGetValue(key, out var was)
                ? (was.Units + remaining, was.Since)
                : (remaining, t);
        }

        /// <summary>Output thrown away within the period, per type and pin, earliest first.</summary>
        public IReadOnlyList<PiLoss> LossesIn(PiStaticData sd)
            => _periodLost
                .Select(kv =>
                {
                    var pin  = _pins[kv.Key.Pin];
                    var tier = sd.TierOf(kv.Key.Type);
                    return new PiLoss(kv.Key.Type, tier, tier == PiTier.P0 ? PiLossKind.Raw : PiLossKind.Product,
                                      kv.Key.Pin, pin.Source.TypeId, pin.Kind, kv.Key.NoRoute,
                                      kv.Value.Units, kv.Value.Since);
                })
                .OrderBy(l => l.Since).ThenBy(l => l.TypeId).ThenBy(l => l.PinId)
                .ToList();

        private long Accept(SimPin dest, int type, long qty, DateTimeOffset t)
        {
            if (qty <= 0) return 0;

            if (dest.IsStorage)
            {
                var vol  = _sd.VolumeOf(type);
                var free = dest.Capacity - Used(dest);
                var fits = vol > 0 ? (long)Math.Floor(free / vol + 1e-9) : qty;
                var take = Math.Max(0, Math.Min(qty, fits));
                Add(dest, type, take);
                if (take > 0 && InPeriod(t)) Delivered[type] = Delivered.GetValueOrDefault(type) + take;
                // Full: it turned some away, or has no room left for another unit of this.
                if (take < qty || dest.Capacity - Used(dest) < vol) dest.FullAt ??= t;
                return take;
            }

            if (dest.Kind == PiPinKind.Processor && dest.Schematic is { } s)
            {
                // One cycle's worth per input, no more.
                var need = s.Inputs.FirstOrDefault(i => i.TypeId == type)?.Quantity ?? 0;
                var take = Math.Max(0, Math.Min(qty, need - dest.Stock.GetValueOrDefault(type)));
                Add(dest, type, take);
                return take;
            }

            return 0;
        }

        private double Used(SimPin p) => p.Stock.Sum(kv => kv.Value * _sd.VolumeOf(kv.Key));

        private static void Add(SimPin p, int type, long qty)
        {
            if (qty > 0) p.Stock[type] = p.Stock.GetValueOrDefault(type) + qty;
        }

        private static void Take(SimPin p, int type, long qty)
        {
            var left = p.Stock.GetValueOrDefault(type) - qty;
            if (left > 0) p.Stock[type] = left; else p.Stock.Remove(type);
        }

        private Snapshot Take(DateTimeOffset at)
        {
            var storage = _pins.Values.Where(p => p.IsStorage)
                .OrderBy(p => p.Source.PinId)
                .Select(p => new PiStorageForecast(
                    p.Source.PinId, p.Source.TypeId, p.Kind, p.Capacity, p.UsedAtStart, Used(p),
                    new Dictionary<int, long>(p.Stock), p.FullAt))
                .ToList();

            var factories = _pins.Values.Where(p => p.Kind == PiPinKind.Processor)
                .OrderBy(p => p.Source.PinId)
                .Select(p => new PiFactoryForecast(
                    p.Source.PinId, p.Source.TypeId, p.Tier, p.Source.SchematicId, p.Schematic?.OutputTypeId,
                    p.Schematic is null ? PiFactoryState.Unknown
                        : p.Running && p.RunEnds > at ? PiFactoryState.Running : PiFactoryState.Idle,
                    p.Schematic is not null && !(p.Running && p.RunEnds > at) ? p.IdleSince ?? at : null,
                    p.Cycles,
                    new Dictionary<int, long>(p.Stock)))
                .ToList();

            var totals = new Dictionary<int, long>();
            foreach (var p in _pins.Values)
                foreach (var (type, amount) in p.Stock)
                    totals[type] = totals.GetValueOrDefault(type) + amount;

            return new Snapshot(storage, factories, totals, new Dictionary<int, long>(_lost));
        }
    }
}
