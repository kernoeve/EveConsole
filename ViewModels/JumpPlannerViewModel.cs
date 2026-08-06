using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A stop on the route the user asked for, as opposed to one the planner filled in.</summary>
public sealed class WaypointVm(int id, string name, string region, double security) : ReactiveObject
{
    public int    Id       { get; } = id;
    public string Name     { get; } = name;
    public string Region   { get; } = region;
    public double Security { get; } = security;

    public string SecurityText => Security.ToString("N2", CultureInfo.InvariantCulture);

    /// <summary>Jump drives cannot enter high security space, so such a waypoint cannot be flown to.</summary>
    public bool   Unreachable => Security >= 0.45;
    public string Detail      => Unreachable ? $"{Region} · {SecurityText} · high sec" : $"{Region} · {SecurityText}";
}

/// <summary>One jump on the planned route.</summary>
public sealed class JumpLegVm
{
    public required int    Number     { get; init; }
    public required string From       { get; init; }
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
            TotalsText = "";
            StatusText = "Add a start and a destination.";
        });

        RemoveWaypointCommand = ReactiveCommand.Create<WaypointVm>(w =>
        {
            Waypoints.Remove(w);
            RenumberWaypoints();
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
        new("Anywhere",         JumpMidpoints.Any),
        new("Station systems",  JumpMidpoints.StationSystems),
        new("Keepstar systems", JumpMidpoints.KeepstarSystems),
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

    /// <summary>
    /// Type-ahead over system names. Exposed as a property rather than a method: AutoCompleteBox
    /// binds its populator, and a binding resolves properties only.
    /// </summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> SystemPopulator =>
        async (text, ct) =>
        {
            var hits = await _planner.SearchSystemsAsync(text ?? "", ct);
            return hits.Select(h => (object)h.Name).ToList();
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
                    Number     = n++,
                    From       = leg.FromSystem,
                    To         = leg.ToSystem,
                    ToRegion   = leg.ToRegion,
                    ToSecurity = leg.ToSecurity,
                    DistanceLy = leg.DistanceLy,
                    Fuel       = leg.Fuel,
                    IsWaypoint = endsWaypoint,
                });

            TotalsText = $"{all.Count} jump{(all.Count == 1 ? "" : "s")} · {dist:N3} ly · " +
                         $"{fuel:N0} {fuelName} · {range:N2} ly range";
            StatusText = "Route planned.";
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
