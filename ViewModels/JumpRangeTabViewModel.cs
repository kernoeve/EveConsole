using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Controls;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One of the capsuleer's characters as the jump range tab offers them: where they are,
/// what they fly and how far their Jump Drive Calibration takes it.</summary>
public sealed record JumpRangeCharacter(long Id, string Name, int? SystemId, string SystemName, bool Online,
                                        int? ShipTypeId, int? JdcLevel)
{
    public string Label => Online && SystemId is int id
        ? string.Format(MapText.RouteCharacterAt, Name, SdeNames.SolarSystem(id, SystemName))
        : Name;
    public override string ToString() => Label;
}

/// <summary>One system in range.</summary>
public sealed class JumpRangeRowVm(JumpRangeSystem s)
{
    public int    SystemId    { get; } = s.Id;
    public string Name        { get; } = s.Label;
    public string Region      { get; } = s.RegionLabel;
    public string Security    { get; } = Math.Round(s.Security, 1).ToString("0.0", CultureInfo.CurrentCulture);
    public bool   IsLow       { get; } = s.Security >= 0.05;
    public bool   IsNull      { get; } = s.Security < 0.05;
    public string Ly          { get; } = s.Ly.ToString("0.00", CultureInfo.CurrentCulture);
    public double Distance    { get; } = s.Ly;
}

/// <summary>
/// The map tool's jump range tab: every system a jump drive reaches from where a character is, or
/// from any system, for a hull and Jump Drive Calibration level — listed nearest first and ringed
/// on the map. Picking a character takes their system, their hull when it has a jump drive, and
/// their trained Jump Drive Calibration; from then on the range follows them as they move.
/// </summary>
public sealed class JumpRangeTabViewModel : MapTabViewModel
{
    private const int JumpDriveCalibration = 21611;

    private readonly JumpPlannerService               _service;
    private readonly UniverseMapService               _map;
    private readonly IDbContextFactory<AppDbContext>? _db;
    private readonly IDisposable                      _characterRefresh;
    private readonly IDisposable                      _recompute;

    public JumpRangeTabViewModel(MapToolViewModel tool, JumpPlannerService service, UniverseMapService map,
                                 IDbContextFactory<AppDbContext>? db) : base(tool)
    {
        _service = service;
        _map     = map;
        _db      = db;

        Landings =
        [
            new(MapText.MidpointsAnywhere,              JumpMidpoints.Any),
            new(MapText.MidpointsStationsAndStructures, JumpMidpoints.StationSystems),
            new(MapText.MidpointsCitadelSystems,        JumpMidpoints.CitadelSystems),
            new(MapText.MidpointsKeepstarSystems,       JumpMidpoints.KeepstarSystems),
        ];
        _landing = Landings[0];

        FromHereCommand = ReactiveCommand.Create(() => TakeCharacter(Character));
        OpenCommand     = ReactiveCommand.Create<JumpRangeRowVm>(row => Tool.OpenSystem(row.SystemId));

        this.WhenAnyValue(x => x.FromText).Skip(1).Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => SuggestAsync(t))).Subscribe();

        // Anything that changes the answer works it out again, once the changes settle.
        _recompute = this.WhenAnyValue(x => x.From, x => x.SelectedShip, x => x.JdcLevel, x => x.Landing)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .Subscribe(change => Dispatcher.UIThread.Post(() => _ = ComputeAsync()));

        _ = LoadShipsAsync();
        _ = LoadCharactersAsync();

        // Who is online and where changes: the list follows, and so does a range taken from a character.
        _characterRefresh = Observable.Interval(TimeSpan.FromSeconds(30))
            .Subscribe(tick => _ = LoadCharactersAsync());
    }

    public override void OnClosed()
    {
        _characterRefresh.Dispose();
        _recompute.Dispose();
    }

    public override string TabTitle => MapText.JumpRangeTab;
    public override string TabGlyph => "◎";

    public ReactiveCommand<Unit, Unit>           FromHereCommand { get; }
    public ReactiveCommand<JumpRangeRowVm, Unit> OpenCommand     { get; }

    // ── Who and where ────────────────────────────────────────────────────────

    public ObservableCollection<JumpRangeCharacter> Characters { get; } = [];

    private JumpRangeCharacter? _character;
    /// <summary>Picking one takes their system, hull and skill at once.</summary>
    public JumpRangeCharacter? Character
    {
        get => _character;
        set
        {
            if (_character?.Id == value?.Id) { this.RaiseAndSetIfChanged(ref _character, value); return; }
            this.RaiseAndSetIfChanged(ref _character, value);
            this.RaisePropertyChanged(nameof(CanStartHere));
            if (!_loadingCharacters) TakeCharacter(value);
        }
    }

    public bool CanStartHere => Character is { Online: true, SystemId: not null };

    /// <summary>The character whose system the range follows; null once a system is typed.</summary>
    private long? _following;

    private bool _loadingCharacters;

    private async Task LoadCharactersAsync()
    {
        if (_db is null) return;
        try
        {
            await using var db = await _db.CreateDbContextAsync();
            var rows = await (from c in db.Characters.AsNoTracking()
                              join s in db.CharacterStatuses.AsNoTracking() on c.Id equals s.CharacterId into st
                              from s in st.DefaultIfEmpty()
                              select new { c.Id, c.Name, Online = s != null && s.Online,
                                           SystemId = s == null ? null : s.SolarSystemId,
                                           ShipTypeId = s == null ? null : s.ShipTypeId })
                             .ToListAsync();
            var ids    = rows.Select(r => r.Id).ToList();
            var skills = await db.EsiSkills.AsNoTracking()
                .Where(k => k.SkillId == JumpDriveCalibration && ids.Contains(k.CharacterId))
                .ToDictionaryAsync(k => k.CharacterId, k => k.ActiveSkillLevel);
            var sysIds = rows.Where(r => r.SystemId is not null).Select(r => r.SystemId!.Value).Distinct().ToList();
            var names  = await db.SdeSolarSystems.AsNoTracking().Where(s => sysIds.Contains(s.SolarSystemId))
                                 .ToDictionaryAsync(s => s.SolarSystemId, s => s.Name);

            var list = rows.Select(r => new JumpRangeCharacter(r.Id, r.Name, r.SystemId,
                                            r.SystemId is int sid ? names.GetValueOrDefault(sid, "") : "", r.Online,
                                            r.ShipTypeId, skills.TryGetValue(r.Id, out var lvl) ? lvl : null))
                           .OrderByDescending(c => c.Online).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                           .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // ⚠️ Changed entry by entry, never cleared and refilled: a ComboBox whose list is
                // emptied loses its selection.
                if (!Characters.SequenceEqual(list))
                {
                    _loadingCharacters = true;
                    var keep = Character?.Id;
                    for (var i = 0; i < list.Count; i++)
                    {
                        if (i < Characters.Count) { if (Characters[i] != list[i]) Characters[i] = list[i]; }
                        else Characters.Add(list[i]);
                    }
                    while (Characters.Count > list.Count) Characters.RemoveAt(Characters.Count - 1);
                    Character = Characters.FirstOrDefault(c => c.Id == keep) ?? Characters.FirstOrDefault();
                    _loadingCharacters = false;
                }

                // The first time, start from whoever is picked; afterwards follow them as they move.
                if (From is null && _following is null && CanStartHere) TakeCharacter(Character);
                else if (_following is long who && list.FirstOrDefault(c => c.Id == who) is { Online: true, SystemId: int now } c
                         && now != From?.SystemId)
                    SetFrom(new PlaceMatch(c.SystemName, "", 0, now));
            });
        }
        catch (Exception ex) { Message = string.Format(CommonText.ErrorWithMessage, ex.Message); }
    }

    /// <summary>Starts from a character: their system, their hull if it jumps, their skill.</summary>
    private void TakeCharacter(JumpRangeCharacter? c)
    {
        if (c is not { SystemId: int id }) return;
        _following = c.Online ? c.Id : null;
        if (c.ShipTypeId is int ship && Ships.FirstOrDefault(s => s.TypeId == ship) is { } hull) SelectedShip = hull;
        if (c.JdcLevel is int level) JdcLevel = level;
        SetFrom(new PlaceMatch(c.SystemName, "", 0, id));
        this.RaisePropertyChanged(nameof(FollowText));
    }

    public ObservableCollection<PlaceMatch> FromPlaces { get; } = [];

    private bool _settingFrom;

    private string _fromText = "";
    public string FromText { get => _fromText; set => this.RaiseAndSetIfChanged(ref _fromText, value); }

    private PlaceMatch? _from;
    /// <summary>The system the range is from. Picking one by hand stops following a character.</summary>
    public PlaceMatch? From
    {
        get => _from;
        set
        {
            this.RaiseAndSetIfChanged(ref _from, value);
            if (!_settingFrom && value is not null) { _following = null; this.RaisePropertyChanged(nameof(FollowText)); }
        }
    }

    private void SetFrom(PlaceMatch place)
    {
        _settingFrom = true;
        FromPlaces.Clear();
        FromPlaces.Add(place);
        From     = place;
        FromText = place.Label;
        _settingFrom = false;
    }

    /// <summary>"Following …": said while the range moves with a character.</summary>
    public string FollowText => _following is long id && Characters.FirstOrDefault(c => c.Id == id) is { } c
        ? string.Format(MapText.JumpRangeFollowing, c.Name) : "";

    private async Task SuggestAsync(string text)
    {
        try
        {
            var found = (await _map.SearchPlacesAsync(text, shownNames: true)).Where(p => p.IsSystem).ToList();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_settingFrom) return;
                FromPlaces.Clear();
                foreach (var p in found) FromPlaces.Add(p);
            });
        }
        catch { /* the old suggestions stay; the next keystroke asks again */ }
    }

    // ── What ─────────────────────────────────────────────────────────────────

    public ObservableCollection<JumpShip> Ships { get; } = [];

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

    public IReadOnlyList<MidpointOption> Landings { get; }

    private MidpointOption _landing;
    public MidpointOption Landing
    {
        get => _landing;
        set { if (value is not null) this.RaiseAndSetIfChanged(ref _landing, value); }
    }

    public string RangeText => SelectedShip is { } s
        ? string.Format(MapText.RangePerJump, JumpPlannerService.MaxRange(s.BaseRangeLy, JdcLevel), s.BaseRangeLy, JdcLevel)
        : "";

    private async Task LoadShipsAsync()
    {
        try
        {
            var ships = await Task.Run(() => _service.GetShipsAsync());
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var s in ships) Ships.Add(s);
                SelectedShip ??= Ships.FirstOrDefault();
                // A character picked before the hulls were in gets theirs now.
                if (Character is { ShipTypeId: int t } && Ships.FirstOrDefault(s => s.TypeId == t) is { } hull) SelectedShip = hull;
            });
        }
        catch (Exception ex) { Message = string.Format(CommonText.ErrorWithMessage, ex.Message); }
    }

    // ── The answer ───────────────────────────────────────────────────────────

    private IReadOnlyList<JumpRangeRowVm> _rows = [];
    /// <summary>Replaced whole, never filled row by row: a long list filled item by item freezes the grid.</summary>
    public IReadOnlyList<JumpRangeRowVm> Rows { get => _rows; private set => this.RaiseAndSetIfChanged(ref _rows, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    private string _message = "";
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    private bool _showOnMap = UiState.GetBool(UiState.JumpRangeShowOnMap, true);
    /// <summary>Ring the systems in range on the map tabs. Remembered.</summary>
    public bool ShowOnMap
    {
        get => _showOnMap;
        set
        {
            this.RaiseAndSetIfChanged(ref _showOnMap, value);
            UiState.SetBool(UiState.JumpRangeShowOnMap, value);
            Push();
        }
    }

    private JumpRangeResult? _result;
    private int _origin;

    private async Task ComputeAsync()
    {
        if (From is not { SystemId: > 0 } from || SelectedShip is not { } ship)
        {
            (_result, Rows, Summary) = (null, [], "");
            Push();
            return;
        }
        try
        {
            var range  = JumpPlannerService.MaxRange(ship.BaseRangeLy, JdcLevel);
            var result = await Task.Run(() => _service.InRangeAsync(from.SystemId, range, Landing.Value));
            _result = result;
            _origin = from.SystemId;
            Rows    = [.. result.Systems.Select(s => new JumpRangeRowVm(s))];
            Message = result.Problem ?? (result.Systems.Count == 0 ? MapText.JumpRangeNothing : "");
            Summary = result.Problem is null
                ? string.Format(MapText.JumpRangeSummary, result.Systems.Count, range.ToString("0.0", CultureInfo.CurrentCulture), from.Label)
                : "";
            Push();
        }
        catch (Exception ex) { Message = string.Format(CommonText.ErrorWithMessage, ex.Message); }
    }

    /// <summary>Puts the range on the map tabs, or takes it off.</summary>
    private void Push() => Tool.SetJumpRange(this,
        ShowOnMap && _result is { Problem: null } r
            ? new MapJumpRange(_origin, r.Systems.Select(s => s.Id).ToHashSet())
            : null);
}
