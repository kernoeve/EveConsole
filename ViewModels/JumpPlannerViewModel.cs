using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Controls;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A stop on the route the user asked for, as opposed to one the planner filled in.</summary>
public sealed class WaypointVm(int id, string name, string region, double security, bool isPinned = false)
    : ReactiveObject
{
    public int    Id       { get; } = id;
    public string Name     { get; } = name;
    public string Region   { get; } = region;
    public double Security { get; } = security;

    /// <summary>A midpoint the user chose by hand in place of the one the planner picked. It is
    /// routed through like any other stop, but stays movable on the map and can be dropped to go
    /// back to automatic routing.</summary>
    public bool IsPinned { get; } = isPinned;

    public string SecurityText => Security.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Jump drives cannot enter high security space, so such a waypoint cannot be flown to.</summary>
    public bool   Unreachable => Security >= 0.45;

    public string Detail => Unreachable
        ? $"{Region} · {SecurityText} · high sec"
        : IsPinned ? $"{Region} · {SecurityText} · chosen midpoint"
                   : $"{Region} · {SecurityText}";
}

/// <summary>One jump on the planned route.</summary>
public sealed class JumpLegVm
{
    public required int    Number     { get; init; }
    public required int    FromSystemId { get; init; }
    public required string From       { get; init; }
    public required string FromRegion { get; init; }
    public required int    ToSystemId { get; init; }
    public required string To         { get; init; }
    public required string ToRegion   { get; init; }
    public required double ToSecurity { get; init; }
    public required double DistanceLy { get; init; }
    public required double Fuel       { get; init; }

    /// <summary>Set on a leg that ends at a stop the user asked for, rather than a filled-in one.</summary>
    public required bool IsWaypoint { get; init; }

    // Properties, not fields: a binding resolves properties only, and a field here would
    // silently render as blank.
    public string DistanceText  => $"{DistanceLy:N3} ly";
    public string FuelText      => Fuel.ToString("N0", CultureInfo.InvariantCulture);
    public string SecurityText  => ToSecurity.ToString("N2", CultureInfo.InvariantCulture);
}

public sealed class JumpPlannerViewModel : ReactiveObject
{
    private readonly JumpPlannerService _planner;

    public JumpPlannerViewModel(JumpPlannerService planner)
    {
        _planner = planner;

        PlanCommand         = ReactiveCommand.CreateFromTask(PlanAsync);
        AddWaypointCommand  = ReactiveCommand.CreateFromTask(AddWaypointAsync);
        ClearCommand        = ReactiveCommand.Create(() =>
        {
            Waypoints.Clear();
            Legs.Clear();
            Alternatives.Clear();
            IsPickingAlternative = false;
            MapRoute      = null;
            MapDots       = null;
            MapLinks      = null;
            MapCandidates = null;
            TotalsText    = "";
            StatusText = "Add a start and a destination.";
        });

        RemoveWaypointCommand = ReactiveCommand.Create<WaypointVm>(w =>
        {
            Waypoints.Remove(w);
            RenumberWaypoints();
        });

        NodeClickedCommand = ReactiveCommand.CreateFromTask<JumpMapNode>(ShowAlternativesAsync);
        NodeMovedCommand   = ReactiveCommand.CreateFromTask<JumpMapDrop>(SnapMidpointAsync);
        DragStartedCommand = ReactiveCommand.CreateFromTask<JumpMapNode>(LightCandidatesAsync);

        ApplyAlternativeCommand  = ReactiveCommand.CreateFromTask(ApplyAlternativeAsync);
        CancelAlternativeCommand = ReactiveCommand.Create(() =>
        {
            IsPickingAlternative = false;
            Alternatives.Clear();
            _pickingFor = null;
        });

        _ = LoadShipsAsync();
    }

    public ObservableCollection<JumpShip>  Ships     { get; } = [];
    public ObservableCollection<WaypointVm> Waypoints { get; } = [];
    public ObservableCollection<JumpLegVm>  Legs      { get; } = [];

    private JumpShip? _selectedShip;
    public JumpShip? SelectedShip
    {
        get => _selectedShip;
        set { this.RaiseAndSetIfChanged(ref _selectedShip, value); this.RaisePropertyChanged(nameof(RangeText)); }
    }

    public IReadOnlyList<int> SkillLevels { get; } = [0, 1, 2, 3, 4, 5];

    private int _jdcLevel = 5;
    public int JdcLevel
    {
        get => _jdcLevel;
        set { this.RaiseAndSetIfChanged(ref _jdcLevel, value); this.RaisePropertyChanged(nameof(RangeText)); }
    }

    private int _jfcLevel = 4;
    public int JfcLevel { get => _jfcLevel; set => this.RaiseAndSetIfChanged(ref _jfcLevel, value); }

    public IReadOnlyList<MidpointOption> MidpointOptions { get; } =
    [
        new("Anywhere",                     JumpMidpoints.Any),
        new("Stations & structures",        JumpMidpoints.StationSystems),
        new("Keepstar systems",             JumpMidpoints.KeepstarSystems),
    ];

    private MidpointOption? _selectedMidpoints;
    public MidpointOption? SelectedMidpoints
    {
        get => _selectedMidpoints;
        set => this.RaiseAndSetIfChanged(ref _selectedMidpoints, value);
    }

    /// <summary>What the picked hull and skill actually reach, so the number is visible before planning.</summary>
    public string RangeText => SelectedShip is { } s
        ? $"{JumpPlannerService.MaxRange(s.BaseRangeLy, JdcLevel):N2} ly per jump " +
          $"({s.BaseRangeLy:N1} base, JDC {JdcLevel})"
        : "";

    private string _systemSearch = "";
    public string SystemSearch { get => _systemSearch; set => this.RaiseAndSetIfChanged(ref _systemSearch, value); }

    private string _statusText = "Add a start and a destination.";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private string _totalsText = "";
    public string TotalsText { get => _totalsText; private set => this.RaiseAndSetIfChanged(ref _totalsText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    public ReactiveCommand<Unit, Unit>       PlanCommand           { get; }
    public ReactiveCommand<Unit, Unit>       AddWaypointCommand    { get; }
    public ReactiveCommand<Unit, Unit>       ClearCommand          { get; }
    public ReactiveCommand<WaypointVm, Unit> RemoveWaypointCommand { get; }

    public ReactiveCommand<JumpMapNode, Unit> NodeClickedCommand { get; }
    public ReactiveCommand<JumpMapDrop, Unit> NodeMovedCommand   { get; }
    public ReactiveCommand<JumpMapNode, Unit> DragStartedCommand { get; }
    public ReactiveCommand<Unit, Unit>        ApplyAlternativeCommand  { get; }
    public ReactiveCommand<Unit, Unit>        CancelAlternativeCommand { get; }

    // ── Map ──────────────────────────────────────────────────────────────────

    private IReadOnlyList<JumpMapNode>? _mapRoute;
    public IReadOnlyList<JumpMapNode>? MapRoute
    {
        get => _mapRoute;
        private set => this.RaiseAndSetIfChanged(ref _mapRoute, value);
    }

    private IReadOnlyList<JumpMapDot>? _mapDots;
    public IReadOnlyList<JumpMapDot>? MapDots
    {
        get => _mapDots;
        private set => this.RaiseAndSetIfChanged(ref _mapDots, value);
    }

    private IReadOnlyList<JumpMapLink>? _mapLinks;
    public IReadOnlyList<JumpMapLink>? MapLinks
    {
        get => _mapLinks;
        private set => this.RaiseAndSetIfChanged(ref _mapLinks, value);
    }

    /// <summary>Legal drop targets for the midpoint currently being dragged. Null when no drag
    /// is in progress, which is what clears the highlight on the map.</summary>
    private IReadOnlyDictionary<int, JumpMapCandidate>? _mapCandidates;
    public IReadOnlyDictionary<int, JumpMapCandidate>? MapCandidates
    {
        get => _mapCandidates;
        private set => this.RaiseAndSetIfChanged(ref _mapCandidates, value);
    }

    /// <summary>Candidate replacements for the midpoint the user clicked.</summary>
    public ObservableCollection<JumpAlternative> Alternatives { get; } = [];

    private JumpAlternative? _selectedAlternative;
    public JumpAlternative? SelectedAlternative
    {
        get => _selectedAlternative;
        set => this.RaiseAndSetIfChanged(ref _selectedAlternative, value);
    }

    private bool _isPickingAlternative;
    public bool IsPickingAlternative
    {
        get => _isPickingAlternative;
        private set => this.RaiseAndSetIfChanged(ref _isPickingAlternative, value);
    }

    private string _alternativesTitle = "";
    public string AlternativesTitle
    {
        get => _alternativesTitle;
        private set => this.RaiseAndSetIfChanged(ref _alternativesTitle, value);
    }

    /// <summary>The midpoint whose alternatives are on screen.</summary>
    private JumpMapNode? _pickingFor;

    /// <summary>
    /// Type-ahead over system names. Exposed as a property rather than a method: AutoCompleteBox
    /// binds its populator, and a binding resolves properties only.
    ///
    /// <para>Returns objects rather than strings so the drop-down can show the region beside each
    /// name — EVE has a great many systems whose names differ by one character, and the name
    /// alone is not enough to tell them apart. The text box still receives only the name, via the
    /// view's ValueMemberBinding.</para>
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> SystemPopulator =>
        async (text, ct) =>
        {
            var hits = await _planner.SearchSystemsAsync(text ?? "", ct);
            return hits.Select(h => (object)new SystemMatch(h.Id, h.Name, h.Region, h.Security))
                       .ToList();
        };

    private async Task LoadShipsAsync()
    {
        try
        {
            var ships = await Task.Run(() => _planner.GetShipsAsync());
            foreach (var s in ships) Ships.Add(s);

            SelectedShip      ??= Ships.FirstOrDefault();
            SelectedMidpoints ??= MidpointOptions[0];
        }
        catch (Exception ex)
        {
            StatusText = $"Could not load jump-capable hulls: {ex.Message}";
        }
    }

    private async Task AddWaypointAsync()
    {
        var name = SystemSearch.Trim();
        if (name.Length == 0) return;

        var hits  = await _planner.SearchSystemsAsync(name);
        var exact = hits.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exact.Id == 0)
        {
            StatusText = $"\"{name}\" is not a system — pick one from the list.";
            return;
        }

        Waypoints.Add(new WaypointVm(exact.Id, exact.Name, exact.Region, exact.Security));
        RenumberWaypoints();
        SystemSearch = "";
        StatusText   = Waypoints.Count < 2
            ? "Add a destination."
            : "Ready to plan.";
    }

    private void RenumberWaypoints() { /* order is the collection order; nothing to renumber yet */ }

    /// <summary>
    /// Drops one waypoint onto another, taking its place. The route already planned is left on
    /// screen but is now stale, so the status line says so rather than quietly showing jumps
    /// that no longer match the order.
    /// </summary>
    public void MoveWaypoint(WaypointVm source, WaypointVm target)
    {
        var from = Waypoints.IndexOf(source);
        var to   = Waypoints.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;

        Waypoints.Move(from, to);
        StatusText = Legs.Count > 0
            ? "Waypoint order changed — plan again to update the route."
            : "Ready to plan.";
    }

    /// <summary>Range of the last planned route, needed to find alternatives for its midpoints.</summary>
    private double _maxRangeLy;

    /// <summary>Same nodes as <see cref="MapRoute"/>, kept as a list so their order can be looked
    /// up — a read-only list has no index-of.</summary>
    private List<JumpMapNode> _mapNodes = [];

    /// <summary>
    /// Lays the planned legs out on CCP's 2D map layout, plus a faint scatter of the systems
    /// around the corridor so the route reads as a path through space rather than a bare
    /// zig-zag. Context is limited to the route's own bounding box: drawing all of New Eden
    /// would bury the route in dots that carry no information about it.
    /// </summary>
    private async Task BuildMapAsync()
    {
        if (Legs.Count == 0) { MapRoute = null; MapDots = null; return; }

        var points = await _planner.MapPointsAsync();
        var pinned = Waypoints.Where(w => w.IsPinned).Select(w => w.Id).ToHashSet();
        var asked  = Waypoints.Where(w => !w.IsPinned).Select(w => w.Id).ToHashSet();

        var nodes = new List<JumpMapNode>();

        void Add(int id, string name, string region, string caption)
        {
            if (!points.TryGetValue(id, out var p)) return;   // outside the published layout
            nodes.Add(new JumpMapNode(id, name, p.X, p.Y, nodes.Count,
                                      asked.Contains(id), caption, pinned.Contains(id)));
        }

        var first = Legs[0];
        Add(first.FromSystemId, first.From, first.FromRegion, first.FromRegion);
        foreach (var leg in Legs)
            Add(leg.ToSystemId, leg.To, leg.ToRegion, $"{leg.ToRegion} · {leg.DistanceLy:N2} ly");

        _mapNodes = nodes;
        MapRoute  = nodes;

        if (nodes.Count == 0) { MapDots = null; return; }

        double minX = nodes.Min(n => n.X), maxX = nodes.Max(n => n.X);
        double minY = nodes.Min(n => n.Y), maxY = nodes.Max(n => n.Y);
        var padX = Math.Max((maxX - minX) * 0.25, 3e16);
        var padY = Math.Max((maxY - minY) * 0.25, 3e16);

        var onRoute   = nodes.Select(n => n.Id).ToHashSet();
        var inWindow  = points.Values
            .Where(p => p.X >= minX - padX && p.X <= maxX + padX &&
                        p.Y >= minY - padY && p.Y <= maxY + padY)
            .ToList();

        var facilities = await _planner.FacilitiesAsync();
        var named      = await _planner.SystemNamesAsync();

        MapDots = inWindow
            .Where(p => !onRoute.Contains(p.Id))
            .Select(p =>
            {
                var (name, region) = named.GetValueOrDefault(p.Id, ("", ""));
                var f = facilities.GetValueOrDefault(p.Id);
                return new JumpMapDot(p.Id, name, region, p.X, p.Y, p.Security, f?.Badges ?? "");
            })
            .Where(d => d.Name.Length > 0)
            .ToList();

        // Gate links, limited to systems on screen at both ends — the whole cluster's topology
        // is far more line than the corridor around one route can carry.
        var shown = inWindow.ToDictionary(p => p.Id);
        var links = await _planner.StargateLinksAsync();

        MapLinks = links
            .Where(l => shown.ContainsKey(l.A) && shown.ContainsKey(l.B))
            .Select(l => new JumpMapLink(shown[l.A].X, shown[l.A].Y, shown[l.B].X, shown[l.B].Y))
            .ToList();
    }

    /// <summary>
    /// The stops either side of a midpoint on the drawn route. Both are needed to ask for
    /// alternatives: a replacement has to be within range of the leg before it and the leg after.
    /// </summary>
    private (JumpMapNode? Prev, JumpMapNode? Next) NeighboursOf(JumpMapNode node)
    {
        var i = _mapNodes.IndexOf(node);
        if (i <= 0 || i >= _mapNodes.Count - 1) return (null, null);
        return (_mapNodes[i - 1], _mapNodes[i + 1]);
    }

    private async Task<List<JumpAlternative>> AlternativesFor(JumpMapNode node)
    {
        var (prev, next) = NeighboursOf(node);
        if (prev is null || next is null || _maxRangeLy <= 0) return [];

        var restriction = SelectedMidpoints?.Value ?? JumpMidpoints.Any;
        return await _planner.AlternativesAsync(prev.Id, next.Id, _maxRangeLy, restriction);
    }

    /// <summary>
    /// Lights every system the dragged midpoint could legally land on, each labelled with what
    /// choosing it would cost against the midpoint being replaced. Which systems are eligible is
    /// the whole difficulty of picking one by hand and nothing on the map implies it, so it is
    /// shown the moment the drag begins rather than discovered by dropping and being refused.
    /// </summary>
    private async Task LightCandidatesAsync(JumpMapNode node)
    {
        MapCandidates = null;
        if (node.IsWaypoint) return;

        var options = await AlternativesFor(node);
        if (options.Count == 0) return;

        // The leg pair being replaced, to express each option as a difference rather than an
        // absolute nobody can weigh at a glance.
        var (prev, next) = NeighboursOf(node);
        var current = prev is null || next is null
            ? null
            : Legs.FirstOrDefault(l => l.FromSystemId == prev.Id && l.ToSystemId == node.Id) is { } inLeg
              && Legs.FirstOrDefault(l => l.FromSystemId == node.Id && l.ToSystemId == next.Id) is { } outLeg
                ? (Ly: inLeg.DistanceLy + outLeg.DistanceLy, Fuel: inLeg.Fuel + outLeg.Fuel)
                : ((double Ly, double Fuel)?)null;

        var fuelPerLy = SelectedShip?.FuelPerLy ?? 0;

        MapCandidates = options.ToDictionary(
            o => o.Id,
            o =>
            {
                var ly   = o.InLy + o.OutLy;
                var fuel = JumpPlannerService.FuelFor(ly, fuelPerLy, JfcLevel);

                if (current is not { } c)
                    return new JumpMapCandidate(o.Id, $"{ly:N2} ly · {fuel:N0} fuel");

                var dLy   = ly   - c.Ly;
                var dFuel = fuel - c.Fuel;
                return new JumpMapCandidate(o.Id,
                    $"{Signed(dLy, "N2")} ly · {Signed(dFuel, "N0")} fuel vs {node.Name}");
            });

        static string Signed(double v, string format) =>
            (v > 0 ? "+" : "") + v.ToString(format, CultureInfo.InvariantCulture);
    }

    private async Task ShowAlternativesAsync(JumpMapNode node)
    {
        MapCandidates = null;          // a press-and-release is still the end of a drag
        if (node.IsWaypoint) return;   // a stop the user asked for is not the planner's to change

        Alternatives.Clear();
        SelectedAlternative = null;
        _pickingFor         = node;
        AlternativesTitle   = $"Instead of {node.Name}";
        IsPickingAlternative = true;

        var options = await AlternativesFor(node);
        foreach (var o in options.Where(o => o.Id != node.Id).Take(200)) Alternatives.Add(o);

        StatusText = Alternatives.Count == 0
            ? $"No other system reaches both sides of {node.Name} at this range."
            : $"{Alternatives.Count} system{(Alternatives.Count == 1 ? "" : "s")} could replace {node.Name}.";
    }

    private async Task ApplyAlternativeAsync()
    {
        if (_pickingFor is not { } node || SelectedAlternative is not { } pick) return;

        IsPickingAlternative = false;
        Alternatives.Clear();
        _pickingFor = null;

        await PinMidpointAsync(node, pick);
    }

    /// <summary>
    /// A midpoint dropped somewhere on the map snaps to the nearest system that could actually
    /// stand in for it — dropping on empty space, or on a system out of range of either side,
    /// would otherwise produce a route that cannot be flown.
    /// </summary>
    private async Task SnapMidpointAsync(JumpMapDrop drop)
    {
        MapCandidates = null;   // the drag is over; the highlight goes with it
        if (drop.Node.IsWaypoint) return;

        var options = await AlternativesFor(drop.Node);
        if (options.Count == 0)
        {
            StatusText = $"Nothing within range could replace {drop.Node.Name}.";
            await BuildMapAsync();   // put the marker back where it was
            return;
        }

        var nearest = options
            .OrderBy(o => (o.MapX - drop.X) * (o.MapX - drop.X) + (o.MapY - drop.Y) * (o.MapY - drop.Y))
            .First();

        await PinMidpointAsync(drop.Node, nearest);
    }

    /// <summary>
    /// Fixes a chosen system in place of a filled-in midpoint by making it a real stop, then
    /// re-plans. Routing through it falls out of the existing waypoint machinery rather than
    /// needing a second notion of "route must pass here".
    /// </summary>
    private async Task PinMidpointAsync(JumpMapNode node, JumpAlternative pick)
    {
        var replacement = new WaypointVm(pick.Id, pick.Name, pick.Region, pick.Security, isPinned: true);

        var existing = Waypoints.FirstOrDefault(w => w.Id == node.Id);
        if (existing is not null)
        {
            // Moving a midpoint that was already pinned: swap it where it stands.
            Waypoints[Waypoints.IndexOf(existing)] = replacement;
        }
        else
        {
            // Count the stops ahead of it on the route to find which pair of waypoints it lies
            // between, and insert it there.
            var at    = _mapNodes.IndexOf(node);
            var ahead = 0;
            for (var i = 0; i < at && i < _mapNodes.Count; i++)
                if (Waypoints.Any(w => w.Id == _mapNodes[i].Id)) ahead++;

            Waypoints.Insert(Math.Clamp(ahead, 0, Waypoints.Count), replacement);
        }

        StatusText = $"Routing through {pick.Name}.";
        await PlanAsync();
    }

    private async Task PlanAsync()
    {
        if (SelectedShip is not { } ship) { StatusText = "Pick a ship."; return; }
        if (Waypoints.Count < 2) { StatusText = "Add at least a start and a destination."; return; }

        if (Waypoints.FirstOrDefault(w => w.Unreachable) is { } bad)
        {
            StatusText = $"{bad.Name} is high security space — a jump drive cannot go there.";
            return;
        }

        IsBusy = true;
        Legs.Clear();
        TotalsText = "";

        try
        {
            var restriction = SelectedMidpoints?.Value ?? JumpMidpoints.Any;
            var all         = new List<(JumpLeg Leg, bool EndsWaypoint)>();
            double dist = 0, fuel = 0;
            string fuelName = ship.FuelTypeName;
            double range = 0;

            // Each requested hop is planned on its own, then the hops are laid end to end, so a
            // waypoint the user asked for is always visited rather than routed around.
            for (var i = 0; i < Waypoints.Count - 1; i++)
            {
                var a = Waypoints[i];
                var b = Waypoints[i + 1];

                var route = await Task.Run(() =>
                    _planner.PlanAsync(a.Id, b.Id, ship, JdcLevel, JfcLevel, restriction));

                range = route.MaxRangeLy;

                if (!route.Ok)
                {
                    StatusText = $"{a.Name} to {b.Name}: {route.Problem}";
                    return;
                }

                for (var j = 0; j < route.Legs.Count; j++)
                    all.Add((route.Legs[j], j == route.Legs.Count - 1));

                dist += route.TotalDistanceLy;
                fuel += route.TotalFuel;
            }

            var n = 1;
            foreach (var (leg, endsWaypoint) in all)
                Legs.Add(new JumpLegVm
                {
                    Number       = n++,
                    FromSystemId = leg.FromSystemId,
                    From         = leg.FromSystem,
                    FromRegion   = leg.FromRegion,
                    ToSystemId   = leg.ToSystemId,
                    To           = leg.ToSystem,
                    ToRegion     = leg.ToRegion,
                    ToSecurity = leg.ToSecurity,
                    DistanceLy = leg.DistanceLy,
                    Fuel       = leg.Fuel,
                    IsWaypoint = endsWaypoint,
                });

            TotalsText = $"{all.Count} jump{(all.Count == 1 ? "" : "s")} · {dist:N3} ly · " +
                         $"{fuel:N0} {fuelName} · {range:N2} ly range";
            StatusText = "Route planned.";

            _maxRangeLy = range;
            await BuildMapAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not plan the route: {ex.Message}";
        }
        finally { IsBusy = false; }
    }
}

public sealed record MidpointOption(string Label, JumpMidpoints Value)
{
    public override string ToString() => Label;
}

/// <summary>One row of the system type-ahead. ToString is the bare name, so anything that falls
/// back to it (rather than the view's ValueMemberBinding) still puts a searchable name in the
/// box rather than a formatted line.</summary>
public sealed record SystemMatch(int Id, string Name, string Region, double Security)
{
    public string SecurityText => Security.ToString("N1", CultureInfo.InvariantCulture);
    public override string ToString() => Name;
}
