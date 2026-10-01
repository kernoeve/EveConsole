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
        var horizonEnd = (at > t0 ? at : t0) + (horizon ?? DefaultHorizon);

        var extractors = layout.Pins
            .Where(p => KindOf(p, sd) == PiPinKind.ExtractorControlUnit)
            .Select(p => Program(p, sd, at))
            .ToList();

        var sim = new Simulation(layout, sd, extractors, t0, horizonEnd);
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
            Rate       = rate,
            Charges    = rate is null ? [] : ChargesFor(flows, rate.Rate),
        };
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
    {
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
                Math.Max(0, supply.GetValueOrDefault(t))))
            .ToList();

        static double RunsPerDay(PiSchematic s) => s.CycleSeconds > 0 ? 86_400.0 / s.CycleSeconds : 0;
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

        /// <summary>When each type made nowhere on the colony first stopped a processor.</summary>
        public Dictionary<int, DateTimeOffset> RunsOut { get; } = [];

        public long InitialTotal(int type) => _initialTotals.GetValueOrDefault(type);

        public Simulation(PiColonyLayout layout, PiStaticData sd, IReadOnlyList<PiExtractorProgram> extractors,
                          DateTimeOffset t0, DateTimeOffset end)
        {
            _sd = sd; _t0 = t0; _end = end;

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
            foreach (var f in _factories)
                if (f.Source.LastCycleStart is { } started
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
                        Deliver(f.Source.PinId, f.Schematic!.OutputTypeId, f.Schematic.OutputQuantity, t);
                    }

                StartIdle(t);
            }

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
                if (have < input.Quantity) have += Pull(f, input.TypeId, input.Quantity - have);
                if (have < input.Quantity) short_.Add(input.TypeId);
            }

            if (short_.Count == 0)
            {
                foreach (var input in s.Inputs) Take(f, input.TypeId, input.Quantity);
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
        private long Pull(SimPin f, int type, long want)
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
            }
            return got;
        }

        /// <summary>Sends a cycle's output along the producer's routes. What finds no room is lost.</summary>
        private void Deliver(long producer, int type, long qty, DateTimeOffset t)
        {
            var remaining = qty;
            if (_pushFrom.TryGetValue(producer, out var routes))
            {
                var mine = routes.Where(r => r.ContentTypeId == type).ToList();
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
            }
            if (remaining > 0) _lost[type] = _lost.GetValueOrDefault(type) + remaining;
        }

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
