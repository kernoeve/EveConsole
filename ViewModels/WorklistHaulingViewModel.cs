using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Services;
using EveConsole.Services.Worklist;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

/// <summary>A pilot a lane can use.</summary>
public sealed record HaulPilotOption(long Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A hull to plan a lane in; <see cref="TypeId"/> 0 is "the pilot's current ship".</summary>
/// <param name="Detail">The hull's own cargo hold, shown beside it.</param>
public sealed record HaulHullOption(int TypeId, string Label, string Detail = "")
{
    public override string ToString() => Label;
}

public sealed record HaulMethodOption(HaulMethod Method, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One pilot in a lane's list, with their place in it.</summary>
public sealed class HaulPilotRow(HaulLaneVm lane, HaulPilotOption pilot, int position)
{
    public HaulLaneVm      Lane     { get; } = lane;
    public HaulPilotOption Pilot    { get; } = pilot;
    public string          Name     => Pilot.Name;
    public string          Position => position == 1 ? WorklistText.HaulPilotPrimary : $"{position}.";
    public bool            CanMoveUp => position > 1;
}

/// <summary>
/// One lane's set-up: how its trips are made, in what, and by whom. Saved as it is changed.
/// </summary>
public sealed class HaulLaneVm : ReactiveObject
{
    private readonly WorklistSettings _settings;
    private readonly Action           _changed;
    private bool _loading = true;

    public HaulLane Lane  { get; }
    public string   Title => HaulLaneText.Lane(Lane);
    public string   Note  => Lane switch
    {
        HaulLane.InSystem => WorklistText.HaulLaneInSystemNote,
        HaulLane.Nearby   => WorklistText.HaulLaneNearbyNote,
        _                 => WorklistText.HaulLaneLongDistanceNote,
    };

    /// <summary>Inside one system the only way is to fly it, so there is no method to choose.</summary>
    public bool ShowsMethod => Lane != HaulLane.InSystem;

    public IReadOnlyList<HaulMethodOption> Methods { get; } =
        new[] { HaulMethod.Gating, HaulMethod.BridgedFreighter, HaulMethod.JumpFreighter, HaulMethod.Service }
            .Select(m => new HaulMethodOption(m, HaulLaneText.Method(m))).ToList();

    private HaulMethodOption? _method;
    public HaulMethodOption? SelectedMethod
    {
        get => _method;
        set
        {
            // A null is the box being refilled, not a choice.
            if (value is null || value == _method) return;
            this.RaiseAndSetIfChanged(ref _method, value);
            this.RaisePropertyChanged(nameof(ShowsShip));
            Save(() => _settings.SetHaulMethodAsync(Lane, value.Method));
        }
    }

    /// <summary>A hauling service flies its own ships: nothing of ours to size the trip by.</summary>
    public bool ShowsShip => Lane == HaulLane.InSystem || _method?.Method != HaulMethod.Service;

    private IReadOnlyList<HaulHullOption> _hulls = [];
    public IReadOnlyList<HaulHullOption> Hulls { get => _hulls; private set => this.RaiseAndSetIfChanged(ref _hulls, value); }

    private HaulHullOption? _hull;
    public HaulHullOption? SelectedHull
    {
        get => _hull;
        set
        {
            if (value is null || value == _hull) return;
            this.RaiseAndSetIfChanged(ref _hull, value);
            Save(() => _settings.SetHaulShipAsync(Lane, value.TypeId));
        }
    }

    private decimal? _cargo;
    /// <summary>The hold to plan with; empty takes the hull's own.</summary>
    public decimal? CargoM3
    {
        get => _cargo;
        set
        {
            if (value == _cargo) return;
            this.RaiseAndSetIfChanged(ref _cargo, value);
            Save(() => _settings.SetHaulCargoAsync(Lane, (double)(value ?? 0)));
        }
    }

    /// <summary>In order; the first is the pilot used when only one is needed.</summary>
    public ObservableCollection<HaulPilotRow> Pilots { get; } = [];

    private IReadOnlyList<HaulPilotOption> _available = [];
    /// <summary>Characters not already on this lane, for the add box.</summary>
    public IReadOnlyList<HaulPilotOption> AvailablePilots { get => _available; private set => this.RaiseAndSetIfChanged(ref _available, value); }

    private HaulPilotOption? _toAdd;
    /// <summary>Picking one adds it at the end of the list, and the box empties for the next.</summary>
    public HaulPilotOption? PilotToAdd
    {
        get => _toAdd;
        set
        {
            this.RaiseAndSetIfChanged(ref _toAdd, value);
            if (value is null || _loading) return;
            _ids.Add(value.Id);
            SavePilots();
            Dispatcher.UIThread.Post(() => PilotToAdd = null);
        }
    }

    public ReactiveCommand<HaulPilotRow, Unit> RemovePilotCommand { get; }
    public ReactiveCommand<HaulPilotRow, Unit> MovePilotUpCommand { get; }

    private List<long> _ids = [];
    private IReadOnlyList<HaulPilotOption> _everyone = [];

    public HaulLaneVm(HaulLane lane, WorklistSettings settings, Action changed)
    {
        Lane      = lane;
        _settings = settings;
        _changed  = changed;

        RemovePilotCommand = ReactiveCommand.Create<HaulPilotRow>(r =>
        {
            _ids.Remove(r.Pilot.Id);
            SavePilots();
        });
        MovePilotUpCommand = ReactiveCommand.Create<HaulPilotRow>(r =>
        {
            var at = _ids.IndexOf(r.Pilot.Id);
            if (at <= 0) return;
            (_ids[at - 1], _ids[at]) = (_ids[at], _ids[at - 1]);
            SavePilots();
        });
    }

    /// <summary>Puts the stored set-up in the boxes, with the choices the page has loaded.</summary>
    internal void Load(IReadOnlyList<HaulHullOption> hulls, IReadOnlyList<HaulPilotOption> everyone)
    {
        _loading = true;
        try
        {
            var set = _settings.HaulLaneFor(Lane);

            _method = Methods.FirstOrDefault(m => m.Method == set.Method) ?? Methods[0];

            // A stored hull missing from the list keeps an entry of its own, so the box never
            // shows blank over a setting that is still in force.
            var list = hulls.ToList();
            if (set.ShipTypeId > 0 && list.All(h => h.TypeId != set.ShipTypeId))
                list.Add(new HaulHullOption(set.ShipTypeId, string.Format(WorklistText.HaulHullNumbered, set.ShipTypeId)));
            Hulls = list;
            _hull = list.FirstOrDefault(h => h.TypeId == Math.Max(0, set.ShipTypeId)) ?? list.FirstOrDefault();

            _cargo = set.CargoM3 > 0 ? (decimal)set.CargoM3 : null;

            _everyone = everyone;
            _ids      = set.CharacterIds.ToList();
            ShowPilots();

            this.RaisePropertyChanged(nameof(SelectedMethod));
            this.RaisePropertyChanged(nameof(ShowsShip));
            this.RaisePropertyChanged(nameof(SelectedHull));
            this.RaisePropertyChanged(nameof(CargoM3));
        }
        finally { _loading = false; }
    }

    /// <summary>
    /// The list as stored, named. A character no longer set up stays in the stored order but is
    /// not shown — removing them from Settings → Characters should not quietly rewrite the lane,
    /// and adding them back puts them where they were.
    /// </summary>
    private void ShowPilots()
    {
        var byId  = _everyone.ToDictionary(p => p.Id);
        var shown = _ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();

        Pilots.Clear();
        for (var i = 0; i < shown.Count; i++) Pilots.Add(new HaulPilotRow(this, shown[i], i + 1));

        AvailablePilots = _everyone.Where(p => !_ids.Contains(p.Id)).ToList();
    }

    private void SavePilots()
    {
        ShowPilots();
        var ids = _ids.ToList();
        Save(() => _settings.SetHaulCharactersAsync(Lane, ids));
    }

    private void Save(Func<Task> write)
    {
        if (_loading) return;
        _ = Task.Run(async () =>
        {
            await write();
            _changed();
        });
    }
}

/// <summary>
/// Config → Hauling: where nearby ends and long distance begins, and how each lane is flown.
/// </summary>
public sealed class WorklistHaulingViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly WorklistSettings                _settings;
    private readonly AppErrorLogger                  _errorLogger;
    private bool _loaded;

    /// <summary>The hulls offered — see <see cref="HaulHulls"/>. The hold override is for the
    /// space a hull's capacity does not show.</summary>
    private static readonly int[] HaulerGroups = HaulHulls.Groups;

    public IReadOnlyList<HaulLaneVm> Lanes { get; }

    private decimal _nearJumps;
    public decimal NearJumps
    {
        get => _nearJumps;
        set
        {
            var v = Math.Clamp(Math.Round(value), 1, 100);
            if (v == _nearJumps) return;
            this.RaiseAndSetIfChanged(ref _nearJumps, v);
            if (!_loaded) return;
            _ = Task.Run(async () =>
            {
                await _settings.SetHaulNearJumpsAsync((int)v);
                RefreshSoon();
            });
        }
    }

    /// <summary>Asks for the list to be rebuilt once the settings settle. Set by the worklist.</summary>
    public Func<Task>? HaulingChanged { get; set; }

    public WorklistHaulingViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                    WorklistSettings settings, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _settings    = settings;
        _errorLogger = errorLogger;

        Lanes = new[] { HaulLane.InSystem, HaulLane.Nearby, HaulLane.LongDistance }
            .Select(l => new HaulLaneVm(l, settings, RefreshSoon)).ToList();

        _nearJumps = settings.HaulNearJumps;

        // A character added or removed: in the pilot boxes without a restart.
        OwnerList.Changed += () => _ = LoadAsync();

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var (hulls, pilots) = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();

                var ships = await db.SdeTypes.AsNoTracking()
                    .Where(t => HaulerGroups.Contains(t.GroupId) && t.Published)
                    .Select(t => new { t.TypeId, t.GroupId, t.Name, t.Capacity })
                    .ToListAsync();

                var chars = await db.Characters.AsNoTracking()
                    .Select(c => new HaulPilotOption(c.Id, c.Name))
                    .ToListAsync();

                // Grouped as the SDE groups them, smallest first, then by name as shown.
                var hullList = new List<HaulHullOption> { new(0, WorklistText.HaulHullCurrentShip) };
                hullList.AddRange(ships
                    .OrderBy(t => Array.IndexOf(HaulerGroups, t.GroupId))
                    .ThenBy(t => SdeNames.Type(t.TypeId, t.Name), StringComparer.CurrentCulture)
                    .Select(t => new HaulHullOption(t.TypeId, SdeNames.Type(t.TypeId, t.Name),
                                                    string.Format(WorklistText.HaulHoldM3, t.Capacity))));

                return (hullList, chars.OrderBy(c => c.Name, StringComparer.CurrentCulture).ToList());
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var lane in Lanes) lane.Load(hulls, pilots);
                _loaded = true;
            });
        }
        catch (Exception ex) { _errorLogger.Log(nameof(WorklistHaulingViewModel), "load", ex); }
    }

    private CancellationTokenSource? _pending;

    /// <summary>
    /// Rebuilds the list a moment after the last change rather than after each one: setting up a
    /// lane is several picks in a row, and each rebuild takes seconds.
    /// </summary>
    private void RefreshSoon()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), cts.Token); }
            catch (OperationCanceledException) { return; }
            if (HaulingChanged is { } refresh) await Dispatcher.UIThread.InvokeAsync(refresh);
        });
    }
}
