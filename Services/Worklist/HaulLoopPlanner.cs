namespace EveConsole.Services.Worklist;

/// <summary>
/// Orders the stops of an in-system hauling loop: where to dock, in what order, and what to drop
/// and pick up at each.
///
/// <para><b>The problem.</b> Every structure in the system can be both a pickup and a drop-off,
/// so a single pass in some fixed order rarely serves everything — what Cap Parts makes goes to
/// Large Ships, and what Large Ships makes goes back to Cap Parts. An item has to be collected
/// before it can be delivered, and the hold may not take everything at once.</para>
///
/// <para><b>What a stop costs.</b> Each leg is a dock, an undock, an align and a short warp.
/// Undocking points the ship where the structure faces, so a next stop behind it means turning
/// round first — for a freighter the slowest part of the whole leg. A leg is costed as its fixed
/// part plus an extra align scaled by how far the ship has to turn: nothing straight ahead, a
/// whole align time for a full 180°. The caller supplies the facing — recorded, or the closest
/// gate by default; a structure with none at all is charged no turn.</para>
///
/// <para><b>How it chooses.</b> Greedy with a look-ahead (a "rollout"): for every stop it could
/// go to next, it plays the rest of the loop out by always taking the cheapest leg, and goes
/// where that finishes soonest. A dozen structures make this a few thousand simulated legs —
/// nothing — and unlike pure nearest-first it does not strand one delivery at the far end of the
/// line to be fetched on its own afterwards.</para>
///
/// <para><b>The start.</b> Where the pilot is docked, if that is in this system: the plan begins
/// with what can be done there. Anywhere else, including in space, the first leg is free of any
/// turn. The hold starts empty — what is in it now is not known freshly enough to plan on.</para>
/// </summary>
public static class HaulLoopPlanner
{
    /// <summary>Some of one item, going from one structure to another.</summary>
    public sealed record Cargo(long From, long To, int TypeId, long Quantity, double UnitM3, int Priority);

    /// <summary>What happens at one stop, in flying order.</summary>
    /// <param name="TurnBefore">Reaching this stop meant undocking away from it — a turn of more
    /// than 90° before the warp.</param>
    public sealed record Stop(long StationId, IReadOnlyList<Cargo> DropOff, IReadOnlyList<Cargo> PickUp, bool TurnBefore);

    /// <param name="TooBig">Cargo that can never be carried: a single unit bigger than the hold.</param>
    /// <param name="Later">Cargo the planned stops do not reach — a small hold against a large
    /// load — left for the loops after this one.</param>
    /// <param name="Seconds">A rough flying time, for comparing orders — not a promise.</param>
    public sealed record Plan(IReadOnlyList<Stop> Stops, IReadOnlyList<Cargo> TooBig, IReadOnlyList<Cargo> Later,
                              int Turns, double Seconds);

    /// <summary>Docking, undocking and a warp of a few thousand kilometres, before any align.</summary>
    private const double FixedLegSeconds = 30;

    /// <summary>
    /// The most stops one plan lists. A loop longer than this is several sittings anyway — a T1
    /// industrial against two million m³ is hundreds of loads — and a list of four hundred stops
    /// is not something anyone flies from. What is left is reported, and the next refresh plans
    /// it from wherever the pilot then is.
    /// </summary>
    private const int MaxStops = 60;

    /// <param name="positions">Where each structure is. One missing is charged no turn.</param>
    /// <param name="facing">The point each structure's undock faces, where one is recorded.</param>
    /// <param name="start">The structure the pilot is docked in, or null.</param>
    /// <param name="capacityM3">The hold; 0 or less means unlimited.</param>
    /// <param name="alignSeconds">The ship's align time, which is also what a full turn costs.</param>
    public static Plan Make(
        IReadOnlyList<Cargo> cargo,
        IReadOnlyDictionary<long, (double X, double Y, double Z)> positions,
        IReadOnlyDictionary<long, (double X, double Y, double Z)> facing,
        long? start, double capacityM3, double alignSeconds)
    {
        var cap = capacityM3 > 0 ? capacityM3 : double.PositiveInfinity;

        // A unit bigger than the hold never fits, whatever the order: set aside rather than looped on.
        var tooBig = cargo.Where(c => c.UnitM3 > cap).ToList();
        var work      = cargo.Where(c => c.UnitM3 <= cap && c.Quantity > 0 && c.From != c.To).ToList();

        var state = new State(work);

        var stops   = new List<Stop>();
        long? here  = start;
        double secs = 0;
        var turns   = 0;

        double Leg(long? from, long to, out bool turned)
        {
            turned = false;
            if (from is not { } a || a == to) return FixedLegSeconds + alignSeconds;
            var extra = TurnFraction(a, to, positions, facing) * alignSeconds;
            turned    = extra > alignSeconds / 2;
            return FixedLegSeconds + alignSeconds + extra;
        }

        // Docked somewhere with work to do: that work comes first, and costs no leg.
        if (here is { } s0 && state.HasWorkAt(s0, cap))
            stops.Add(state.Visit(s0, cap, turnBefore: false));

        while (!state.Done && stops.Count < MaxStops)
        {
            var options = state.Candidates(cap).ToList();
            if (options.Count == 0) break;

            long  best     = options[0];
            double bestCost = double.PositiveInfinity;
            foreach (var next in options.OrderBy(o => o))
            {
                var trial = state.Clone();
                var cost  = Leg(here, next, out _);
                trial.Visit(next, cap, false);
                cost += Rollout(trial, next, cap, Leg);
                if (cost < bestCost - 1e-9) { bestCost = cost; best = next; }
            }

            secs += Leg(here, best, out var turned);
            if (turned) turns++;
            stops.Add(state.Visit(best, cap, turned));
            here = best;
        }

        // Whatever the stops did not reach is reported rather than silently dropped.
        return new Plan(stops, tooBig, state.Leftover().ToList(), turns, secs);
    }

    /// <summary>The rest of the loop played out by always taking the cheapest next leg.</summary>
    private static double Rollout(State state, long here, double cap, LegFn leg)
    {
        double total = 0;
        var guard = 0;
        while (!state.Done && guard++ < MaxStops)
        {
            var options = state.Candidates(cap).ToList();
            if (options.Count == 0) break;

            var next = options
                .Select(o => (o, Cost: leg(here, o, out _)))
                .OrderBy(x => x.Cost).ThenByDescending(x => state.WorkAt(x.o, cap)).ThenBy(x => x.o)
                .First().o;

            total += leg(here, next, out _);
            state.Visit(next, cap, false);
            here = next;
        }
        return total;
    }

    private delegate double LegFn(long? from, long to, out bool turned);

    /// <summary>
    /// How much of a turn leaving <paramref name="from"/> for <paramref name="to"/> takes: 0 when
    /// the undock points straight at it, 1 when it points directly away.
    /// </summary>
    internal static double TurnFraction(
        long from, long to,
        IReadOnlyDictionary<long, (double X, double Y, double Z)> positions,
        IReadOnlyDictionary<long, (double X, double Y, double Z)> facing)
    {
        if (!positions.TryGetValue(from, out var a) || !positions.TryGetValue(to, out var b)
            || !facing.TryGetValue(from, out var f)) return 0;

        var (ux, uy, uz) = (f.X - a.X, f.Y - a.Y, f.Z - a.Z);
        var (vx, vy, vz) = (b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var lu = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        var lv = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (lu < 1 || lv < 1) return 0;

        var cos = Math.Clamp((ux * vx + uy * vy + uz * vz) / (lu * lv), -1, 1);
        return (1 - cos) / 2;
    }

    /// <summary>What is still to collect, and what is in the hold.</summary>
    private sealed class State
    {
        // Per cargo line, by index: units still at the source, units aboard.
        private readonly IReadOnlyList<Cargo> _lines;
        private readonly long[] _waiting;
        private readonly long[] _aboard;
        private double _loadedM3;

        public State(IReadOnlyList<Cargo> lines)
        {
            _lines   = lines;
            _waiting = lines.Select(l => l.Quantity).ToArray();
            _aboard  = new long[lines.Count];
        }

        private State(State other)
        {
            _lines    = other._lines;
            _waiting  = (long[])other._waiting.Clone();
            _aboard   = (long[])other._aboard.Clone();
            _loadedM3 = other._loadedM3;
        }

        public State Clone() => new(this);

        public bool Done => _waiting.All(w => w == 0) && _aboard.All(a => a == 0);

        /// <summary>Somewhere worth docking: something aboard is for it, or something there can
        /// be loaded.</summary>
        public IEnumerable<long> Candidates(double cap)
        {
            var free = cap - _loadedM3;
            var set  = new HashSet<long>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_aboard[i] > 0) set.Add(_lines[i].To);
                if (_waiting[i] > 0 && _lines[i].UnitM3 <= free + 1e-9) set.Add(_lines[i].From);
            }
            return set;
        }

        public bool HasWorkAt(long station, double cap) => WorkAt(station, cap) > 0;

        /// <summary>m³ that would move at a stop: dropped, plus what fits once it is dropped.</summary>
        public double WorkAt(long station, double cap)
        {
            double drop = 0, free = cap - _loadedM3, pick = 0;
            for (var i = 0; i < _lines.Count; i++)
                if (_lines[i].To == station) drop += _aboard[i] * _lines[i].UnitM3;
            free += drop;
            for (var i = 0; i < _lines.Count; i++)
                if (_lines[i].From == station && _waiting[i] > 0)
                    pick += Math.Min(_waiting[i] * _lines[i].UnitM3, Math.Max(0, free - pick));
            return drop + pick + (HasUnitsAt(station) ? 1e-6 : 0);
        }

        private bool HasUnitsAt(long station)
        {
            for (var i = 0; i < _lines.Count; i++)
                if ((_lines[i].To == station && _aboard[i] > 0) || (_lines[i].From == station && _waiting[i] > 0))
                    return true;
            return false;
        }

        /// <summary>
        /// Docks: everything aboard for here comes off, then as much as fits goes on — the most
        /// urgent first, then the largest, so a hold that cannot take everything leaves the
        /// routine and the small for the next lap.
        /// </summary>
        public Stop Visit(long station, double cap, bool turnBefore)
        {
            var drops = new List<Cargo>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].To != station || _aboard[i] == 0) continue;
                drops.Add(_lines[i] with { Quantity = _aboard[i] });
                _loadedM3 -= _aboard[i] * _lines[i].UnitM3;
                _aboard[i] = 0;
            }
            if (_loadedM3 < 1e-6) _loadedM3 = 0;

            var picks = new List<Cargo>();
            foreach (var i in Enumerable.Range(0, _lines.Count)
                         .Where(i => _lines[i].From == station && _waiting[i] > 0)
                         .OrderByDescending(i => _lines[i].Priority)
                         .ThenByDescending(i => _waiting[i] * _lines[i].UnitM3)
                         .ThenBy(i => i))
            {
                var unit  = _lines[i].UnitM3;
                var free  = cap - _loadedM3;
                var units = unit <= 0 ? _waiting[i]
                          : double.IsPositiveInfinity(free) ? _waiting[i]
                          : Math.Min(_waiting[i], (long)Math.Floor((free + 1e-9) / unit));
                if (units <= 0) continue;

                picks.Add(_lines[i] with { Quantity = units });
                _waiting[i] -= units;
                _aboard[i]  += units;
                _loadedM3   += units * unit;
            }

            return new Stop(station, drops, picks, turnBefore);
        }

        public IEnumerable<Cargo> Leftover()
        {
            for (var i = 0; i < _lines.Count; i++)
                if (_waiting[i] + _aboard[i] > 0)
                    yield return _lines[i] with { Quantity = _waiting[i] + _aboard[i] };
        }
    }
}
