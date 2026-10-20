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
/// with what can be done there. In space after undocking from a structure here, from that
/// structure's undock, turn and all. Anywhere else the first leg is free of any turn. The hold
/// holds what the caller says is aboard (see <see cref="Cargo.Aboard"/>), and nothing else.</para>
///
/// <para><b>⚠️ The same question always gets the same answer.</b> Every choice is a function of
/// what is left to move and where the pilot is, and nothing else — not the order the cargo came
/// in, not how a line was split. So a loop half flown, re-planned from where the pilot now is with
/// what is now left, gives the rest of the plan it started from. Keep it that way: a tie broken by
/// list position, or a start that differs from the step it stands for, reshuffles the stops under
/// a pilot who has done exactly what was asked.</para>
/// </summary>
public static class HaulLoopPlanner
{
    /// <summary>Some of one item, going from one structure to another.</summary>
    /// <param name="From">Where it is picked up — or, below zero, already aboard: in the hold of
    /// the pilot whose id it is the negative of (see <see cref="HoldOf"/>), to be dropped off only.</param>
    public sealed record Cargo(long From, long To, int TypeId, long Quantity, double UnitM3, int Priority)
    {
        public bool Aboard => From < 0;
    }

    /// <summary>The site that stands for a pilot's hold: cargo picked up and ticked off, not yet
    /// put down. Negative, so it can never be a station, a structure or a system.</summary>
    public static long HoldOf(long pilotId) => -pilotId;

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
    /// <param name="start">The structure the pilot is docked in or has just undocked from, or null.</param>
    /// <param name="docked">Docked at <paramref name="start"/>, rather than in space off its undock.</param>
    /// <param name="capacityM3">The hold; 0 or less means unlimited.</param>
    /// <param name="alignSeconds">The ship's align time, which is also what a full turn costs.</param>
    public static Plan Make(
        IReadOnlyList<Cargo> cargo,
        IReadOnlyDictionary<long, (double X, double Y, double Z)> positions,
        IReadOnlyDictionary<long, (double X, double Y, double Z)> facing,
        long? start, double capacityM3, double alignSeconds, bool docked = true)
    {
        var cap = capacityM3 > 0 ? capacityM3 : double.PositiveInfinity;

        // A unit bigger than the hold never fits, whatever the order: set aside rather than looped on.
        // Cargo already aboard fits by definition — it is in the hold.
        var tooBig = cargo.Where(c => !c.Aboard && c.UnitM3 > cap).ToList();
        // One line per item and pair of places, in a fixed order: how the caller happened to list
        // or split them must not decide a tie.
        var work   = cargo.Where(c => (c.Aboard || c.UnitM3 <= cap) && c.Quantity > 0 && c.From != c.To)
            .GroupBy(c => (c.From, c.To, c.TypeId))
            .Select(g => g.First() with { Quantity = g.Sum(c => c.Quantity), Priority = g.Max(c => c.Priority) })
            .OrderBy(c => c.From).ThenBy(c => c.To).ThenBy(c => c.TypeId)
            .ToList();

        var state = new State(work, cap);

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

        // Docked somewhere with work to do: that work comes first, and costs no leg. Cargo aboard
        // for here is put down before anything else — before the loop moves on.
        if (docked && here is { } s0 && state.HasWorkAt(s0))
            stops.Add(state.Visit(s0, turnBefore: false));

        while (!state.Done && stops.Count < MaxStops)
        {
            var options = state.Candidates().ToList();
            if (options.Count == 0) break;

            // Equal within a millisecond is equal, and the lower id wins. Structures in a line
            // make real ties — three next stops at exactly the same cost — and a tie is broken by
            // rule, never by the last bits of the arithmetic.
            long  best     = options[0];
            double bestCost = double.PositiveInfinity;
            foreach (var next in options.OrderBy(o => o))
            {
                var trial = state.Clone();
                var cost  = Leg(here, next, out _);
                trial.Visit(next, false);
                cost += Rollout(trial, next, Leg);
                if (cost < bestCost - TieSeconds) { bestCost = cost; best = next; }
            }

            secs += Leg(here, best, out var turned);
            if (turned) turns++;
            stops.Add(state.Visit(best, turned));
            here = best;
        }

        // Whatever the stops did not reach is reported rather than silently dropped.
        return new Plan(stops, tooBig, state.Leftover().ToList(), turns, secs);
    }

    /// <summary>The rest of the loop played out by always taking the cheapest next leg.</summary>
    private static double Rollout(State state, long here, LegFn leg)
    {
        double total = 0;
        var guard = 0;
        while (!state.Done && guard++ < MaxStops)
        {
            var options = state.Candidates().ToList();
            if (options.Count == 0) break;

            var next = options
                .Select(o => (o, Cost: Math.Round(leg(here, o, out _) / TieSeconds)))
                .OrderBy(x => x.Cost).ThenByDescending(x => state.WorkAt(x.o)).ThenBy(x => x.o)
                .First().o;

            total += leg(here, next, out _);
            state.Visit(next, false);
            here = next;
        }
        return total;
    }

    private delegate double LegFn(long? from, long to, out bool turned);

    /// <summary>Costs closer than this are a tie, broken the same way every time.</summary>
    private const double TieSeconds = 1e-3;

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

    /// <summary>
    /// What is still to collect, and what is in the hold.
    ///
    /// <para>⚠️ Volumes are counted in whole ten-thousandths of a m³, not as floating point. Added
    /// up in a different order — the same cargo with one line already dropped, against the same
    /// cargo dropped along the way — doubles came out a hair apart, a hold that was exactly full
    /// took one unit more or less, and a re-plan of the same loop went another way round.</para>
    /// </summary>
    private sealed class State
    {
        /// <summary>Volume ticks per m³: fine enough for any item's volume (0.01 m³ and up).</summary>
        private const double TicksPerM3 = 10_000;

        /// <summary>No hold limit, kept well clear of overflow.</summary>
        private const long Unlimited = long.MaxValue / 4;

        // Per cargo line, by index: units still at the source, units aboard, and one unit's volume.
        private readonly IReadOnlyList<Cargo> _lines;
        private readonly long[] _waiting;
        private readonly long[] _aboard;
        private readonly long[] _unit;
        private readonly long   _cap;
        private long _loaded;

        public State(IReadOnlyList<Cargo> lines, double cap)
        {
            _lines   = lines;
            _unit    = lines.Select(l => Ticks(l.UnitM3)).ToArray();
            _cap     = double.IsPositiveInfinity(cap) ? Unlimited : (long)Math.Floor(cap * TicksPerM3);
            // What the pilot already carries starts in the hold, waiting only to be put down.
            _waiting = lines.Select(l => l.Aboard ? 0 : l.Quantity).ToArray();
            _aboard  = lines.Select(l => l.Aboard ? l.Quantity : 0).ToArray();
            _loaded  = Enumerable.Range(0, lines.Count).Sum(i => _aboard[i] * _unit[i]);
        }

        private State(State other)
        {
            _lines   = other._lines;
            _unit    = other._unit;
            _cap     = other._cap;
            _waiting = (long[])other._waiting.Clone();
            _aboard  = (long[])other._aboard.Clone();
            _loaded  = other._loaded;
        }

        // Rounded up, so a hold is never planned past full by the rounding.
        private static long Ticks(double m3) => Math.Max(0, (long)Math.Ceiling(m3 * TicksPerM3 - 1e-6));

        public State Clone() => new(this);

        public bool Done => _waiting.All(w => w == 0) && _aboard.All(a => a == 0);

        /// <summary>Somewhere worth docking: something aboard is for it, or something there can
        /// be loaded.</summary>
        public IEnumerable<long> Candidates()
        {
            var free = _cap - _loaded;
            var set  = new HashSet<long>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_aboard[i] > 0) set.Add(_lines[i].To);
                if (_waiting[i] > 0 && _unit[i] <= free) set.Add(_lines[i].From);
            }
            return set;
        }

        /// <summary>Something would actually move here: aboard for it, or waiting here and fitting
        /// in the hold. Not merely waiting here for a hold with no room — a stop where nothing
        /// happens.</summary>
        public bool HasWorkAt(long station)
        {
            for (var i = 0; i < _lines.Count; i++)
                if (_lines[i].To == station && _aboard[i] > 0) return true;
            var free = _cap - _loaded;
            for (var i = 0; i < _lines.Count; i++)
                if (_lines[i].From == station && _waiting[i] > 0 && _unit[i] <= free)
                    return true;
            return false;
        }

        /// <summary>Volume that would move at a stop: dropped, plus what fits once it is dropped —
        /// and one tick more where only units without volume would.</summary>
        public long WorkAt(long station)
        {
            long drop = 0, pick = 0;
            for (var i = 0; i < _lines.Count; i++)
                if (_lines[i].To == station) drop += _aboard[i] * _unit[i];
            var free = _cap - _loaded + drop;
            for (var i = 0; i < _lines.Count; i++)
                if (_lines[i].From == station && _waiting[i] > 0)
                    pick += Math.Min(_waiting[i] * _unit[i], Math.Max(0, free - pick));
            return drop + pick + (HasUnitsAt(station) ? 1 : 0);
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
        public Stop Visit(long station, bool turnBefore)
        {
            var drops = new List<Cargo>();
            for (var i = 0; i < _lines.Count; i++)
            {
                if (_lines[i].To != station || _aboard[i] == 0) continue;
                drops.Add(_lines[i] with { Quantity = _aboard[i] });
                _loaded   -= _aboard[i] * _unit[i];
                _aboard[i] = 0;
            }

            var picks = new List<Cargo>();
            foreach (var i in Enumerable.Range(0, _lines.Count)
                         .Where(i => _lines[i].From == station && _waiting[i] > 0)
                         .OrderByDescending(i => _lines[i].Priority)
                         .ThenByDescending(i => _waiting[i] * _unit[i])
                         .ThenBy(i => i))
            {
                var free  = _cap - _loaded;
                var units = _unit[i] == 0 ? _waiting[i] : Math.Min(_waiting[i], free / _unit[i]);
                if (units <= 0) continue;

                picks.Add(_lines[i] with { Quantity = units });
                _waiting[i] -= units;
                _aboard[i]  += units;
                _loaded     += units * _unit[i];
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
