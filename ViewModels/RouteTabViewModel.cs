using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One of the capsuleer's characters, as the route planner offers them: whose alliance
/// decides which jump bridges are open, and where they are now.</summary>
public sealed record RouteCharacter(long Id, string Name, long? AllianceId, int? SystemId, string SystemName, bool Online)
{
    public string Label => Online && SystemId is int id
        ? string.Format(MapText.RouteCharacterAt, Name, SdeNames.SolarSystem(id, SystemName))
        : Name;
    public override string ToString() => Label;
}

/// <summary>
/// The map tool's route planner: from a system to a system through stargates, the jump bridges
/// the chosen character's alliance may use, and Thera and Turnur's wormholes, keeping out of the
/// avoid list. The route is listed jump by jump, drawn on a map tab, and can be sent to the
/// game's autopilot.
/// </summary>
public sealed class RouteTabViewModel : MapTabViewModel
{
    private readonly RoutePlannerService             _planner;
    private readonly UniverseMapService              _map;
    private readonly IDbContextFactory<AppDbContext>? _db;
    private readonly MapStatsService?                _stats;

    public RouteTabViewModel(MapToolViewModel tool, RoutePlannerService planner, UniverseMapService map,
                             IDbContextFactory<AppDbContext>? db, MapStatsService? stats) : base(tool)
    {
        _planner = planner;
        _map     = map;
        _db      = db;
        _stats   = stats;

        Preferences =
        [
            new(RoutePreference.Shortest,   MapText.RouteShortest),
            new(RoutePreference.Safer,      MapText.RouteSafer),
            new(RoutePreference.LessSecure, MapText.RouteLessSecure),
        ];
        _preference = Preferences[0];

        ShipSizes =
        [
            new("small",   MapText.ShipSizeSmall),
            new("medium",  MapText.ShipSizeMedium),
            new("large",   MapText.ShipSizeLarge),
            new("xlarge",  MapText.ShipSizeXLarge),
        ];
        _shipSize = ShipSizes[2];

        PlanCommand           = ReactiveCommand.CreateFromTask(PlanAsync);
        SwapCommand           = ReactiveCommand.Create(Swap);
        FromHereCommand       = ReactiveCommand.Create(FromHere);
        ClearCommand          = ReactiveCommand.Create(ClearRoute);
        AddAvoidCommand       = ReactiveCommand.Create(AddAvoid);
        ToggleAvoidListCommand = ReactiveCommand.Create(() => { ShowAvoidList = !ShowAvoidList; });
        foreach (var c in new IHandleObservableErrors[] { PlanCommand })
            c.ThrownExceptions.Subscribe(ex => Message = string.Format(CommonText.ErrorWithMessage, ex.Message));

        // Suggestions for the three pickers: systems only, each with its region.
        this.WhenAnyValue(x => x.FromText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t, FromPlaces))).Subscribe();
        this.WhenAnyValue(x => x.ToText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t, ToPlaces))).Subscribe();
        this.WhenAnyValue(x => x.AvoidText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t, AvoidPlaces))).Subscribe();
        this.WhenAnyValue(x => x.AvoidPick).Where(p => p is not null)
            .Subscribe(_ => Dispatcher.UIThread.Post(AddAvoid));

        RouteAvoidList.Changed += OnAvoidChanged;
        _ = ShowAvoidAsync();
        _ = LoadCharactersAsync();

        // Who is online changes as characters log in and out: the pick list and "Here" follow it.
        _characterRefresh = Observable.Interval(TimeSpan.FromSeconds(30))
            .Subscribe(tick => _ = LoadCharactersAsync());
    }

    private readonly IDisposable _characterRefresh;

    private void OnAvoidChanged() => Dispatcher.UIThread.Post(() => _ = ShowAvoidAsync());

    /// <summary>Closed: the refresh stops and the avoid list is no longer followed. The map tool
    /// takes the route off the maps.</summary>
    public override void OnClosed()
    {
        _characterRefresh.Dispose();
        RouteAvoidList.Changed -= OnAvoidChanged;
    }

    public override string TabTitle => MapText.RoutePlannerTab;
    public override string TabGlyph => "➜";

    // ── Who and where ────────────────────────────────────────────────────────

    public ObservableCollection<RouteCharacter> Characters { get; } = [];

    private RouteCharacter? _character;
    public RouteCharacter? Character
    {
        get => _character;
        set
        {
            this.RaiseAndSetIfChanged(ref _character, value);
            this.RaisePropertyChanged(nameof(CanStartHere));
        }
    }

    public bool CanStartHere => Character is { Online: true, SystemId: not null };

    private async Task LoadCharactersAsync()
    {
        if (_db is null) return;
        try
        {
            await using var db = await _db.CreateDbContextAsync();
            var rows = await (from c in db.Characters.AsNoTracking()
                              join s in db.CharacterStatuses.AsNoTracking() on c.Id equals s.CharacterId into st
                              from s in st.DefaultIfEmpty()
                              select new { c.Id, c.Name, c.AllianceId, Online = s != null && s.Online, SystemId = s == null ? null : s.SolarSystemId })
                             .ToListAsync();
            var ids = rows.Where(r => r.SystemId is not null).Select(r => r.SystemId!.Value).Distinct().ToList();
            var names = await db.SdeSolarSystems.AsNoTracking().Where(s => ids.Contains(s.SolarSystemId))
                                .ToDictionaryAsync(s => s.SolarSystemId, s => s.Name);

            var list = rows.Select(r => new RouteCharacter(r.Id, r.Name, r.AllianceId, r.SystemId,
                                                            r.SystemId is int sid ? names.GetValueOrDefault(sid, "") : "", r.Online))
                           .OrderByDescending(c => c.Online).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // ⚠️ Changed entry by entry, never cleared and refilled: a ComboBox whose list is
                // emptied loses its selection. Unchanged — the usual case every 30 s — touches nothing.
                if (Characters.SequenceEqual(list)) return;
                var keep = Character?.Id;
                for (var i = 0; i < list.Count; i++)
                {
                    if (i < Characters.Count) { if (Characters[i] != list[i]) Characters[i] = list[i]; }
                    else Characters.Add(list[i]);
                }
                while (Characters.Count > list.Count) Characters.RemoveAt(Characters.Count - 1);
                Character = Characters.FirstOrDefault(c => c.Id == keep) ?? Characters.FirstOrDefault();
                if (From is null && CanStartHere) FromHere();
            });
        }
        catch (Exception ex) { Message = string.Format(CommonText.ErrorWithMessage, ex.Message); }
    }

    public ObservableCollection<PlaceMatch> FromPlaces  { get; } = [];
    public ObservableCollection<PlaceMatch> ToPlaces    { get; } = [];
    public ObservableCollection<PlaceMatch> AvoidPlaces { get; } = [];

    private string _fromText = "";
    public string FromText { get => _fromText; set => this.RaiseAndSetIfChanged(ref _fromText, value); }

    private string _toText = "";
    public string ToText { get => _toText; set => this.RaiseAndSetIfChanged(ref _toText, value); }

    private PlaceMatch? _from;
    public PlaceMatch? From { get => _from; set => this.RaiseAndSetIfChanged(ref _from, value); }

    private PlaceMatch? _to;
    public PlaceMatch? To { get => _to; set => this.RaiseAndSetIfChanged(ref _to, value); }

    /// <summary>Starts the route where the chosen character is.</summary>
    private void FromHere()
    {
        if (Character is not { SystemId: int id } c) return;
        var place = new PlaceMatch(c.SystemName, "", 0, id);
        FromPlaces.Clear();
        FromPlaces.Add(place);
        From     = place;
        FromText = place.Label;
    }

    /// <summary>Plans to the given system: what "route to here" elsewhere in the map tool does.</summary>
    public void SetDestination(int systemId, string englishName)
    {
        var place = new PlaceMatch(englishName, "", 0, systemId);
        ToPlaces.Clear();
        ToPlaces.Add(place);
        To     = place;
        ToText = place.Label;
    }

    private void Swap()
    {
        (From, To) = (To, From);
        var (ft, tt) = (FromText, ToText);
        (FromText, ToText) = (tt, ft);
    }

    private async Task SuggestAsync(string text, ObservableCollection<PlaceMatch> into)
    {
        try
        {
            var found = (await _map.SearchPlacesAsync(text, shownNames: true)).Where(p => p.IsSystem).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                into.Clear();
                foreach (var p in found) into.Add(p);
            });
        }
        catch
        {
            // The old suggestions stay; the next keystroke asks again.
        }
    }

    // ── How ──────────────────────────────────────────────────────────────────

    public IReadOnlyList<Choice<RoutePreference>> Preferences { get; }

    private Choice<RoutePreference> _preference;
    public Choice<RoutePreference> Preference
    {
        get => _preference;
        set { if (value is not null) this.RaiseAndSetIfChanged(ref _preference, value); }
    }

    private bool _useBridges = true;
    public bool UseBridges { get => _useBridges; set => this.RaiseAndSetIfChanged(ref _useBridges, value); }

    private bool _useWormholes = true;
    /// <summary>Thera and Turnur's connections, as EVE-Scout lists them.</summary>
    public bool UseWormholes { get => _useWormholes; set => this.RaiseAndSetIfChanged(ref _useWormholes, value); }

    /// <summary>The largest wormhole size the ship needs: EVE-Scout's words, shown as hull classes.</summary>
    public IReadOnlyList<Choice<string>> ShipSizes { get; }

    private Choice<string> _shipSize;
    public Choice<string> ShipSize
    {
        get => _shipSize;
        set { if (value is not null) this.RaiseAndSetIfChanged(ref _shipSize, value); }
    }

    // ── Avoid list ───────────────────────────────────────────────────────────

    public ObservableCollection<AvoidRowVm> Avoid { get; } = [];

    private bool _showAvoidList;
    public bool ShowAvoidList { get => _showAvoidList; set => this.RaiseAndSetIfChanged(ref _showAvoidList, value); }

    private string _avoidSummary = "";
    public string AvoidSummary { get => _avoidSummary; private set => this.RaiseAndSetIfChanged(ref _avoidSummary, value); }

    private string _avoidText = "";
    public string AvoidText { get => _avoidText; set => this.RaiseAndSetIfChanged(ref _avoidText, value); }

    private PlaceMatch? _avoidPick;
    public PlaceMatch? AvoidPick { get => _avoidPick; set => this.RaiseAndSetIfChanged(ref _avoidPick, value); }

    private void AddAvoid()
    {
        if (AvoidPick is not { IsSystem: true } p) return;
        RouteAvoidList.Add(p.SystemId);
        AvoidPick = null;
        AvoidText = "";
    }

    /// <summary>The list with names and regions, read from the SDE.</summary>
    private async Task ShowAvoidAsync()
    {
        var ids = RouteAvoidList.Ids;
        var rows = new List<AvoidRowVm>();
        if (_db is not null && ids.Count > 0)
        {
            try
            {
                await using var db = await _db.CreateDbContextAsync();
                rows = await (from s in db.SdeSolarSystems.AsNoTracking()
                              join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId
                              where ids.Contains(s.SolarSystemId)
                              select new AvoidRowVm(s.SolarSystemId, s.Name, s.RegionId, r.Name, s.Security))
                             .ToListAsync();
            }
            catch { /* the list shows what it can */ }
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Avoid.Clear();
            foreach (var r in rows.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase)) Avoid.Add(r);
            AvoidSummary = string.Format(MapText.AvoidSummary, Avoid.Count);
        });
    }

    // ── The route ────────────────────────────────────────────────────────────

    public ObservableCollection<RouteStepRowVm> Steps { get; } = [];

    private RoutePlan? _plan;
    public bool HasRoute => _plan is { NoRoute: false, Steps.Count: > 0 };

    private string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string _message = "";
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    public ReactiveCommand<Unit, Unit> PlanCommand           { get; }
    public ReactiveCommand<Unit, Unit> SwapCommand           { get; }
    public ReactiveCommand<Unit, Unit> FromHereCommand       { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand          { get; }

    private bool _showOnMap = UiState.GetBool(UiState.RouteShowOnMap, true);
    /// <summary>Whether the route is drawn on the map tabs. Remembered.</summary>
    public bool ShowOnMap
    {
        get => _showOnMap;
        set
        {
            this.RaiseAndSetIfChanged(ref _showOnMap, value);
            UiState.SetBool(UiState.RouteShowOnMap, value);
            PushRoute();
        }
    }

    /// <summary>The route on the maps, or off them, as Show on map and the plan say.</summary>
    private void PushRoute() =>
        Tool.SetRoute(this, ShowOnMap && _plan is { NoRoute: false } plan
            ? plan.Steps.Select(s => new Controls.MapRouteStep(s.SystemId, s.RegionId, s.Hop is RouteHop.Bridge or RouteHop.Wormhole)).ToList()
            : null);

    /// <summary>Clears the route here and on the map. Where it starts stays: usually where the
    /// pilot is.</summary>
    private void ClearRoute()
    {
        _plan = null;
        Steps.Clear();
        Summary = Message = "";
        To = null;
        ToText = "";
        this.RaisePropertyChanged(nameof(HasRoute));
        PushRoute();
    }
    public ReactiveCommand<Unit, Unit> AddAvoidCommand       { get; }
    public ReactiveCommand<Unit, Unit> ToggleAvoidListCommand { get; }

    private async Task PlanAsync()
    {
        if (From is not { IsSystem: true } from || To is not { IsSystem: true } to)
        {
            Message = MapText.RoutePickBoth;
            return;
        }

        var request = new RouteRequest(from.SystemId, to.SystemId, Preference.Value, RouteAvoidList.Ids,
                                       UseBridges, Character?.AllianceId, UseWormholes, ShipSize.Value);
        var plan = await Task.Run(() => _planner.PlanAsync(request));

        // Hostiles placed now, and the last hour's kills, beside each system.
        var live  = Tool.Snapshot;
        var kills = _stats is null ? new Dictionary<int, MapStatsService.SystemActivity>()
                  : await Task.Run(() => _stats.GetActivityAsync(MapStatsService.BucketOf(DateTimeOffset.UtcNow.AddHours(-1))));

        _plan = plan;
        Steps.Clear();
        var n = 0;
        foreach (var s in plan.Steps)
            Steps.Add(new RouteStepRowVm(this, s, n++,
                live?.Hostiles.TryGetValue(s.SystemId, out var h) == true ? h.Count : 0,
                kills.TryGetValue(s.SystemId, out var k) ? k.ShipKills + k.PodKills : 0));
        this.RaisePropertyChanged(nameof(HasRoute));

        PushRoute();
        if (plan.NoRoute)
        {
            Summary = "";
            Message = MapText.RouteNotFound;
            return;
        }

        var holes   = plan.Steps.Count(s => s.Hop == RouteHop.Wormhole);
        var lowest  = plan.Steps.Min(s => s.Security);
        Summary = string.Format(MapText.RouteSummary, plan.Jumps, plan.BridgeJumps, holes,
                                lowest.ToString("0.0", CultureInfo.CurrentCulture));
        Message = plan.Steps.Any(s => s.Bridge is { AccessKnown: false }) ? MapText.RouteBridgeAccessUnknown : "";
    }

    /// <summary>The characters logged in now, read when asked: Set destination offers these.</summary>
    public async Task<List<RouteCharacter>> OnlineCharactersAsync()
    {
        await LoadCharactersAsync();
        return [.. Characters.Where(c => c.Online).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Sends the route to the game's autopilot for each of the given characters — one,
    /// or everyone online — and says what came of it.</summary>
    public async Task SendToAsync(IReadOnlyList<RouteCharacter> characters)
    {
        if (_plan is not { NoRoute: false } plan) return;
        if (characters.Count == 0) { Message = MapText.RouteNobodyOnline; return; }

        var problems = new List<string>();
        foreach (var c in characters)
            if (await Task.Run(() => _planner.SendToAutopilotAsync(c.Id, plan)) is { } problem)
                problems.Add($"{c.Name}: {problem}");

        Message = problems.Count == 0
            ? string.Format(MapText.RouteSent, string.Join(", ", characters.Select(c => c.Name)), RoutePlannerService.Waypoints(plan).Count)
            : string.Format(MapText.RouteNotSent, string.Join("; ", problems));
    }
}

/// <summary>A system on the avoid list.</summary>
public sealed class AvoidRowVm(int systemId, string name, int regionId, string region, double security)
{
    public int    SystemId     { get; } = systemId;
    public string Label        { get; } = SdeNames.SolarSystem(systemId, name);
    public string RegionLabel  { get; } = SdeNames.Region(regionId, region);
    public string SecurityText { get; } = security.ToString("0.0", CultureInfo.CurrentCulture);
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; } = ReactiveCommand.Create(() => RouteAvoidList.Remove(systemId));
}

/// <summary>One system of the planned route.</summary>
public sealed class RouteStepRowVm
{
    public RouteStepRowVm(RouteTabViewModel tab, RouteStep s, int number, int hostiles, int kills)
    {
        Step        = s;
        Number      = number == 0 ? "" : number.ToString(CultureInfo.CurrentCulture);
        Label       = SdeNames.SolarSystem(s.SystemId, s.Name);
        RegionLabel = SdeNames.Region(s.RegionId, s.Region);
        Security    = s.Security;
        SecurityText = s.Security.ToString("0.0", CultureInfo.CurrentCulture);
        Hostiles    = hostiles;
        Kills       = kills;
        Via = s.Hop switch
        {
            RouteHop.Start    => MapText.RouteViaStart,
            RouteHop.Gate     => MapText.RouteViaGate,
            RouteHop.Bridge   => s.Bridge is { } b
                ? (b.Zone == 0 ? MapText.RouteViaBridge : string.Format(MapText.RouteViaBridgeZone, b.Zone, b.Multiplier))
                : MapText.RouteViaBridge,
            RouteHop.Wormhole => s.Wormhole is { } w
                ? string.Format(MapText.RouteViaWormhole, w.Signature, w.LandsOn)
                : MapText.RouteViaWormholeShort,
            _ => "",
        };
        IsJump  = s.Hop is RouteHop.Bridge or RouteHop.Wormhole;
        OpenCommand = ReactiveCommand.Create(() => tab.Tool.OpenSystem(s.SystemId));
        ToggleAvoidCommand = ReactiveCommand.Create(() => RouteAvoidList.Toggle(s.SystemId));
    }

    public RouteStep Step        { get; }
    public string    Number      { get; }
    public string    Label       { get; }
    public string    RegionLabel { get; }
    public double    Security    { get; }
    public string    SecurityText { get; }
    public string    Via         { get; }
    public bool      IsJump      { get; }
    public int       Hostiles    { get; }
    public int       Kills       { get; }
    public string    HostilesText => Hostiles > 0 ? Hostiles.ToString(CultureInfo.CurrentCulture) : "";
    public string    KillsText    => Kills    > 0 ? Kills.ToString(CultureInfo.CurrentCulture)    : "";
    public bool      HasHostiles => Hostiles > 0;

    /// <summary>High, low or null security, for the colour of the number.</summary>
    public bool IsHighSec => Security >= 0.45;
    public bool IsLowSec  => Security is > 0 and < 0.45;
    public bool IsNullSec => Security <= 0;

    public ReactiveCommand<Unit, Unit> OpenCommand        { get; }
    public ReactiveCommand<Unit, Unit> ToggleAvoidCommand { get; }
}
