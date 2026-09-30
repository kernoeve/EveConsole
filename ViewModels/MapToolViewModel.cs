using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Controls;
using EveConsole.Localization;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The Universe Map tool: any number of map and system tabs, shown one at a time or two side by
/// side.
///
/// <para>Tabs live in two panes, left and right, laid out as the Fitting tool's are. Dragging a
/// tab to the right half splits the tool 50/50; a tab can be dragged back and forth between the
/// halves or along its own strip, and the split closes when one side runs out of tabs.</para>
///
/// <para>A universe tab is a whole map with its own overlay and zoom. A system tab is a system's
/// page. Double-clicking a system on a map opens its page in a tab; the region and constellation
/// links on a page go back to a map tab — the one used last — framed on that area, adding one if
/// none is open.</para>
///
/// <para>While the tool is on screen it keeps the maps live: who is where is read every
/// <see cref="LiveEvery"/> and drawn on every map tab, and an overlay counting intel or kills is
/// re-read every <see cref="OverlayEvery"/>.</para>
/// </summary>
public sealed class MapToolViewModel : ReactiveObject
{
    private readonly UniverseMapService         _map;
    private readonly MapStatsService?           _stats;
    private readonly AppPreferencesService?     _prefs;
    private readonly Func<SystemPageViewModel>  _newSystemPage;
    private readonly LiveIntelService?          _live;
    private readonly AppErrorLogger?            _errors;
    private readonly JumpBridgeService?         _bridges;

    public static readonly TimeSpan LiveEvery    = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OverlayEvery = TimeSpan.FromSeconds(60);

    public MapToolViewModel(
        UniverseMapService        map,
        MapStatsService?          stats,
        AppPreferencesService?    prefs,
        Func<SystemPageViewModel> newSystemPage,
        LiveIntelService?         live,
        AppErrorLogger?           errors  = null,
        JumpBridgeService?        bridges = null)
    {
        _errors        = errors;
        _bridges       = bridges;
        if (bridges is not null) bridges.Changed += () => _ = ReloadBridgesAsync();
        _map           = map;
        _stats         = stats;
        _prefs         = prefs;
        _newSystemPage = newSystemPage;
        _live          = live;

        LeftPane    = new MapPaneViewModel(this, false);
        RightPane   = new MapPaneViewModel(this, true);
        _activePane = LeftPane;
        LeftPane.IsActive = true;

        NewUniverseTabCommand = ReactiveCommand.Create(() => { NewUniverseTab(); });
        ShowBridgesTabCommand = ReactiveCommand.Create(() => { ShowBridgesTab(); });

        // Typing refreshes the suggestions; picking one opens it. Throttled: each keystroke is a
        // query.
        this.WhenAnyValue(x => x.SearchText)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(180))
            .SelectMany(t => Observable.FromAsync(() => RefreshSuggestionsAsync(t)))
            .Subscribe();

        this.WhenAnyValue(x => x.SelectedPlace)
            .Where(p => p is not null)
            .Subscribe(p => Dispatcher.UIThread.Post(() => OpenPlace(p!)));

        // The tool opens on New Eden, as the single map did.
        NewUniverseTab();
    }

    // ── Panes and tabs ───────────────────────────────────────────────────────

    public MapPaneViewModel LeftPane  { get; }
    public MapPaneViewModel RightPane { get; }

    public IEnumerable<MapTabViewModel> AllTabs => LeftPane.Tabs.Concat(RightPane.Tabs);

    public bool IsSplit => RightPane.Tabs.Count > 0;
    public bool HasTab  => LeftPane.Tabs.Count > 0;

    private MapPaneViewModel _activePane;
    /// <summary>The side last clicked: new tabs open there.</summary>
    public MapPaneViewModel ActivePane
    {
        get => _activePane;
        set
        {
            if (value == _activePane) return;
            _activePane.IsActive = false;
            _activePane = value;
            value.IsActive = true;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(SelectedTab));
        }
    }

    public MapTabViewModel? SelectedTab
    {
        get => _activePane.SelectedTab;
        set
        {
            if (value is null) return;
            value.Pane.SelectedTab = value;
            value.LastActive = ++_activations;
            if (value.Pane != _activePane) { ActivePane = value.Pane; return; }
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Counts selections, so "the map tab used last" can be told without a clock.</summary>
    private long _activations;

    public ReactiveCommand<Unit, Unit> NewUniverseTabCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowBridgesTabCommand { get; }

    public bool HasBridges => _bridges is not null;

    /// <summary>The jump bridges tab: there is only ever one, brought forward if open.</summary>
    public BridgesTabViewModel? ShowBridgesTab()
    {
        if (_bridges is null) return null;
        if (AllTabs.OfType<BridgesTabViewModel>().FirstOrDefault() is { } open)
        {
            SelectedTab = open;
            return open;
        }
        var tab = new BridgesTabViewModel(this, _bridges, _map);
        Add(tab);
        if (_bridgeList is { } list) tab.Show(list);
        else _ = ReloadBridgesAsync();
        return tab;
    }

    /// <summary>Adds a map of New Eden to the active side and shows it.</summary>
    public UniverseTabViewModel NewUniverseTab()
    {
        var map = new UniverseViewModel(_map, _stats, _prefs,
                                        _bridges is null ? null : ct => _bridges.GetZonesAsync(ct))
                  { OpenSystemRequested = OpenSystem };
        var tab = new UniverseTabViewModel(this, map);
        Add(tab);
        if (_snapshot is { } live) tab.ApplyLive(live);
        map.Bridges = _bridgeLines;
        return tab;
    }

    private SystemTabViewModel NewSystemTab(int systemId)
    {
        var tab = new SystemTabViewModel(this, _newSystemPage(), systemId);
        Add(tab);
        _ = tab.LoadAsync();
        return tab;
    }

    private void Add(MapTabViewModel tab)
    {
        tab.Pane = _activePane;
        _activePane.Tabs.Add(tab);
        SelectedTab = tab;
        PanesChanged();
    }

    public void CloseTab(MapTabViewModel tab)
    {
        var pane = tab.Pane;
        if (!TakeOut(tab)) return;
        if (pane == _activePane) this.RaisePropertyChanged(nameof(SelectedTab));
        PanesChanged();
    }

    /// <summary>Moves a tab to a side, at a position in its strip (the end if none).</summary>
    public void MoveTab(MapTabViewModel tab, MapPaneViewModel to, int? index = null)
    {
        var from = tab.Pane;
        var i    = from.Tabs.IndexOf(tab);
        if (i < 0) return;

        var at = Math.Clamp(index ?? to.Tabs.Count, 0, to.Tabs.Count);
        if (from == to)
        {
            if (at > i) at--;
            if (at != i) from.Tabs.Move(i, at);
        }
        else
        {
            TakeOut(tab);
            tab.Pane = to;
            to.Tabs.Insert(at, tab);
        }

        SelectedTab = tab;
        PanesChanged();
    }

    private static bool TakeOut(MapTabViewModel tab)
    {
        var pane = tab.Pane;
        var i    = pane.Tabs.IndexOf(tab);
        if (i < 0) return false;

        pane.Tabs.RemoveAt(i);
        if (pane.SelectedTab == tab)
            pane.SelectedTab = pane.Tabs.Count == 0 ? null : pane.Tabs[Math.Min(i, pane.Tabs.Count - 1)];
        return true;
    }

    /// <summary>
    /// Keeps the two sides consistent after any change: the left side is never the empty one
    /// (the right side's tabs move over when it empties, keeping the one on show), and the active
    /// side is never an empty right side.
    /// </summary>
    private void PanesChanged()
    {
        if (LeftPane.Tabs.Count == 0 && RightPane.Tabs.Count > 0)
        {
            var showing = RightPane.SelectedTab;
            var moving  = RightPane.Tabs.ToList();
            RightPane.Tabs.Clear();
            RightPane.SelectedTab = null;
            foreach (var t in moving)
            {
                t.Pane = LeftPane;
                LeftPane.Tabs.Add(t);
            }
            LeftPane.SelectedTab = showing;
        }
        if (RightPane.Tabs.Count == 0 && _activePane == RightPane) ActivePane = LeftPane;

        this.RaisePropertyChanged(nameof(IsSplit));
        this.RaisePropertyChanged(nameof(HasTab));
        this.RaisePropertyChanged(nameof(SelectedTab));
        this.RaisePropertyChanged(nameof(ShowSplitDropZone));
    }

    private bool _isDraggingTab;
    public bool IsDraggingTab
    {
        get => _isDraggingTab;
        set
        {
            this.RaiseAndSetIfChanged(ref _isDraggingTab, value);
            this.RaisePropertyChanged(nameof(ShowSplitDropZone));
        }
    }

    /// <summary>The "drop here for side by side" target: only while a tab is being dragged, the
    /// tool is not split yet, and the left side would still have a tab after the move.</summary>
    public bool ShowSplitDropZone => _isDraggingTab && !IsSplit && LeftPane.Tabs.Count > 1;

    // ── Navigation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Shows a system's page: its tab if one is open, a new tab on the active side if not. Every
    /// way into a system — a double-click on a map, the search, a link in another tool — comes
    /// through here, so a system never has two tabs.
    /// </summary>
    public void OpenSystem(int systemId)
    {
        if (systemId <= 0) return;
        if (AllTabs.OfType<SystemTabViewModel>().FirstOrDefault(t => t.SystemId == systemId) is { } open)
        {
            SelectedTab = open;
            return;
        }
        NewSystemTab(systemId);
    }

    /// <summary>The map tab used last, brought to the front — or a new one if none is open.</summary>
    public UniverseTabViewModel ShowUniverseTab()
    {
        var tab = AllTabs.OfType<UniverseTabViewModel>().MaxBy(t => t.LastActive) ?? NewUniverseTab();
        SelectedTab = tab;
        return tab;
    }

    /// <summary>A map tab framed on a region: the one used last (see <see cref="ShowUniverseTab"/>).</summary>
    public Task FocusRegionAsync(int regionId) => ShowUniverseTab().Map.FocusRegionAsync(regionId);

    /// <param name="englishName">⚠️ The English name; the map finds constellations by it.</param>
    public Task FocusConstellationAsync(string englishName) =>
        ShowUniverseTab().Map.FocusConstellationAsync(englishName);

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>Regions, constellations and systems matching what is typed.</summary>
    public ObservableCollection<PlaceMatch> Places { get; } = [];

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    private PlaceMatch? _selectedPlace;
    public PlaceMatch? SelectedPlace
    {
        get => _selectedPlace;
        set => this.RaiseAndSetIfChanged(ref _selectedPlace, value);
    }

    private async Task RefreshSuggestionsAsync(string text)
    {
        try
        {
            var matches = await _map.SearchPlacesAsync(text, shownNames: true, constellations: true);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Places.Clear();
                foreach (var m in matches) Places.Add(m);
            });
        }
        catch
        {
            // A failed lookup leaves the old suggestions; the next keystroke asks again.
        }
    }

    /// <summary>
    /// Opens what the search found, in a new tab: a system's page, or a map framed on a region
    /// or constellation. A system already open is brought forward instead (see
    /// <see cref="OpenSystem"/>).
    /// </summary>
    private void OpenPlace(PlaceMatch place)
    {
        if (place.IsSystem) OpenSystem(place.SystemId);
        else
        {
            var map = NewUniverseTab().Map;
            _ = place.IsConstellation
                ? map.FocusConstellationAsync(place.Name)
                : map.FocusRegionAsync(place.RegionId);
        }

        // Emptied so the next search starts clean and picking the same place again still works.
        SelectedPlace = null;
        SearchText    = "";
    }

    // ── Live ─────────────────────────────────────────────────────────────────

    private LiveMapSnapshot? _snapshot;
    private CancellationTokenSource? _liveCts;

    /// <summary>Starts reading who is where while the tool is on screen, and stops when it is
    /// not — nothing is read for a map nobody is looking at.</summary>
    public void SetOnScreen(bool onScreen)
    {
        if (onScreen)
        {
            if (_liveCts is not null || _live is null) return;
            _liveCts = new CancellationTokenSource();
            _ = LiveLoopAsync(_liveCts.Token);
        }
        else
        {
            _liveCts?.Cancel();
            _liveCts = null;
        }
    }

    // ── Jump bridges ─────────────────────────────────────────────────────────

    private JumpBridgeList?               _bridgeList;
    private IReadOnlyList<MapBridgeLine>? _bridgeLines;

    /// <summary>Reads the bridges and hands them to every map and to the bridges tab. On start,
    /// once a minute with the overlays, and at once after a bridge is added or removed.</summary>
    public async Task ReloadBridgesAsync()
    {
        if (_bridges is null) return;
        try
        {
            var list  = await Task.Run(() => _bridges.GetAsync());
            var lines = BridgeLines(list);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _bridgeList  = list;
                _bridgeLines = lines;
                foreach (var tab in AllTabs.OfType<UniverseTabViewModel>()) tab.Map.Bridges = lines;
                foreach (var tab in AllTabs.OfType<BridgesTabViewModel>()) tab.Show(list);
            });
        }
        catch (Exception ex)
        {
            _errors?.Log(nameof(MapToolViewModel), "jump bridges", ex);
        }
    }

    internal static List<MapBridgeLine> BridgeLines(JumpBridgeList list) => list.Bridges.Select(b =>
    {
        var a = SdeNames.SolarSystem(b.SystemA, b.NameA);
        var z = SdeNames.SolarSystem(b.SystemB, b.NameB);
        var lines = b.Gates.Select(g => g.FuelExpires is { } fuel
                ? $"{g.Name} · {string.Format(MapText.BridgeFuelUntil, fuel.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))}"
                : g.Name)
            .ToList();
        if (b.FromEsi && !b.BothEnds)
            lines.Add(string.Format(MapText.BridgeOneGate, b.Gates[0].SystemId == b.SystemA ? a : z));
        if (b.IsManual)
            lines.Add(b.Note is { Length: > 0 } note ? $"{MapText.BridgeAddedByHand} · {note}" : MapText.BridgeAddedByHand);
        lines.Add(BridgeRowVm.AccessText(b));
        // Each half in the zone of its end: the zone a jump LANDING there is in — for the half
        // at A, the jump from B.
        var intoA = b.Directions?.FirstOrDefault(d => d.ToSystemId == b.SystemA)?.Zone ?? 0;
        var intoB = b.Directions?.FirstOrDefault(d => d.ToSystemId == b.SystemB)?.Zone ?? 0;
        return new MapBridgeLine(b.SystemA, b.SystemB, string.Format(MapText.BridgeTitle, a, z),
                                 string.Join("\n", lines), Complete: !b.FromEsi || b.BothEnds || b.IsManual,
                                 ZoneFrom: intoA, ZoneTo: intoB);
    }).ToList();

    private async Task LiveLoopAsync(CancellationToken ct)
    {
        var overlayDue = DateTimeOffset.UtcNow + OverlayEvery;
        await ReloadBridgesAsync();
        using var timer = new PeriodicTimer(LiveEvery);
        do
        {
            try
            {
                var snapshot = await Task.Run(() => _live!.GetSnapshotAsync(ct), ct);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _snapshot = snapshot;
                    foreach (var tab in AllTabs.OfType<UniverseTabViewModel>()) tab.ApplyLive(snapshot);
                });

                if (DateTimeOffset.UtcNow >= overlayDue)
                {
                    overlayDue = DateTimeOffset.UtcNow + OverlayEvery;
                    var maps = await Dispatcher.UIThread.InvokeAsync(() =>
                        AllTabs.OfType<UniverseTabViewModel>().Select(t => t.Map).ToList());
                    // ⚠️ Off the UI thread. SQLite's async calls complete inline, so awaited from
                    // here a 30-day kill count would hold the window still once a minute; the
                    // overlay publishes its result back to the UI thread itself.
                    foreach (var map in maps) await Task.Run(map.RefreshLiveOverlayAsync, ct);
                    await ReloadBridgesAsync();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // A failed read leaves the marks as they were and the next tick tries again: the
                // map is a view and must not stop because one read did. Logged once per distinct
                // failure, not every five seconds.
                if (ex.Message != _lastLiveError)
                {
                    _lastLiveError = ex.Message;
                    _errors?.Log(nameof(MapToolViewModel), "live map", ex);
                }
            }
        }
        while (await WaitAsync(timer, ct));
    }

    private string? _lastLiveError;

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    // ── Markers from a snapshot ──────────────────────────────────────────────

    /// <summary>Most pilots listed in one hover. The rest are counted, not named.</summary>
    private const int MaxListed = 20;

    /// <summary>
    /// The marks for one map: a system's own, and on each region the sum of its systems, so the
    /// zoomed-out map still says where somebody is.
    /// </summary>
    internal static Dictionary<int, MapMarkers> BuildMarkers(LiveMapSnapshot live, MapGraph graph)
    {
        var nodes   = graph.Nodes.Where(n => !n.IsRegion || !graph.IsContinuous).ToDictionary(n => n.Id);
        var regions = graph.Nodes.Where(n => n.IsRegion && graph.IsContinuous).ToDictionary(n => n.RegionId);
        var result  = new Dictionary<int, MapMarkers>();

        var now = live.At;
        foreach (var id in live.Hostiles.Keys.Union(live.Own.Keys))
        {
            if (!nodes.TryGetValue(id, out var node)) continue;

            live.Hostiles.TryGetValue(id, out var h);
            live.Own.TryGetValue(id, out var o);

            var hostileRows = h is null ? null : HostileRows(h, now);
            var ownRows     = o is null ? null : OwnRows(o);
            result[id] = new MapMarkers(
                h?.Count ?? 0,
                h is null ? null : string.Format(MapText.LiveHostilesTitle, h.Count, node.Label),
                hostileRows is null ? null : string.Join("\n", hostileRows.Select(r => r.Text)),
                o?.Count ?? 0,
                o is null ? null : string.Format(MapText.LiveOwnTitle, o.Count, node.Label),
                ownRows is null ? null : string.Join("\n", ownRows.Select(r => r.Text)),
                hostileRows, ownRows);
        }

        // Regions: the sum, and which systems it is in.
        foreach (var region in regions.Values)
        {
            var hostile = live.Hostiles.Values
                .Where(h => nodes.TryGetValue(h.SystemId, out var n) && n.RegionId == region.RegionId)
                .OrderByDescending(h => h.Count).ToList();
            var own = live.Own
                .Where(kv => nodes.TryGetValue(kv.Key, out var n) && n.RegionId == region.RegionId)
                .OrderByDescending(kv => kv.Value.Count).ToList();
            if (hostile.Count == 0 && own.Count == 0) continue;

            var hCount = hostile.Sum(h => h.Count);
            var oCount = own.Sum(kv => kv.Value.Count);
            result[region.Id] = new MapMarkers(
                hCount,
                hCount == 0 ? null : string.Format(MapText.LiveHostilesTitle, hCount, region.Label),
                hCount == 0 ? null : string.Join("\n", hostile.Take(MaxListed)
                    .Select(h => string.Format(MapText.LiveSystemCount, nodes[h.SystemId].Label, h.Count))),
                oCount,
                oCount == 0 ? null : string.Format(MapText.LiveOwnTitle, oCount, region.Label),
                oCount == 0 ? null : string.Join("\n", own.Take(MaxListed)
                    .Select(kv => string.Format(MapText.LiveSystemCount, nodes[kv.Key].Label, kv.Value.Count))));
        }

        return result;
    }

    /// <summary>A hover's lines for one system's hostiles, each carrying the pilot and ship so
    /// the map can put the portrait and the ship's icon in front of it.</summary>
    private static List<MapMarkRow> HostileRows(SystemHostiles h, DateTimeOffset now)
    {
        var rows = h.Pilots.Take(MaxListed).Select(p =>
        {
            var ship    = p.Ship is { Length: > 0 } s ? s : MapText.LiveShipUnknown;
            var minutes = (int)Math.Max(0, (now - p.At).TotalMinutes);
            var ago     = minutes == 0 ? MapText.LiveJustNow : string.Format(MapText.LiveMinutesAgo, minutes);
            var source  = p.FromKillmail ? MapText.LiveSourceKillmail
                        : p.NoVisual     ? MapText.LiveSourceIntelNoVisual
                        :                  MapText.LiveSourceIntel;
            var (text, at) = WithShipAt(MapText.LiveHostileLine, ship, p.Name, ShipMark, ago, source);
            return new MapMarkRow(text, p.CharacterId, p.ShipTypeId ?? 0, at);
        }).ToList();

        if (h.Pilots.Count > MaxListed)
            rows.Add(new MapMarkRow(string.Format(MapText.LiveMore, h.Pilots.Count - MaxListed)));
        if (h.Unidentified > 0)
            rows.Add(new MapMarkRow(string.Format(MapText.LiveUnidentified, h.Unidentified)));
        return rows;
    }

    private static List<MapMarkRow> OwnRows(IReadOnlyList<OwnPilot> own) => own.Take(MaxListed).Select(o =>
    {
        var ship = o.Hull is { Length: > 0 } hull
            ? (o.ShipName is { Length: > 0 } name && name != hull ? $"{hull} ({name})" : hull)
            : MapText.LiveShipUnknown;
        var where = !o.Docked            ? MapText.LiveInSpace
                  : o.Place is { } place ? string.Format(MapText.LiveDockedAt, place)
                  :                        MapText.LiveDocked;
        var (text, at) = WithShipAt(MapText.LiveOwnLine, ship, o.Name, ShipMark, where);
        return new MapMarkRow(text, o.CharacterId, o.ShipTypeId ?? 0, at);
    }).ToList();

    /// <summary>Stands in for the ship's name while a line is formatted, to find where it lands.</summary>
    private const string ShipMark = "\u0001";

    /// <summary>
    /// Formats a hover line and says where the ship's name starts in it, so the map can put the
    /// ship's icon just before the name. Found by formatting with a mark in the name's place — a
    /// translation may put the words in any order.
    /// </summary>
    private static (string Text, int ShipAt) WithShipAt(string format, string ship, params object[] args)
    {
        var marked = string.Format(format, args);
        var at     = marked.IndexOf(ShipMark, StringComparison.Ordinal);
        return (marked.Replace(ShipMark, ship), at);
    }
}

/// <summary>One side of the map tool: a strip of tabs and the one on show.</summary>
public sealed class MapPaneViewModel(MapToolViewModel tool, bool isRight) : ReactiveObject
{
    public MapToolViewModel Tool    { get; } = tool;
    public bool             IsRight { get; } = isRight;

    public ObservableCollection<MapTabViewModel> Tabs { get; } = [];

    private MapTabViewModel? _selectedTab;
    public MapTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab is not null) _selectedTab.IsSelected = false;
            this.RaiseAndSetIfChanged(ref _selectedTab, value);
            if (value is not null) value.IsSelected = true;
        }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }
}

/// <summary>A tab of the map tool: a map, or a system's page.</summary>
public abstract class MapTabViewModel : ReactiveObject
{
    protected MapTabViewModel(MapToolViewModel tool)
    {
        Tool          = tool;
        CloseCommand  = ReactiveCommand.Create(() => Tool.CloseTab(this));
        SelectCommand = ReactiveCommand.Create(() => { Tool.SelectedTab = this; });
    }

    public MapToolViewModel  Tool { get; }
    public MapPaneViewModel  Pane { get; internal set; } = null!;

    public abstract string TabTitle { get; }

    /// <summary>A map tab or a system tab, for the glyph in front of the title.</summary>
    public abstract string TabGlyph { get; }

    public ReactiveCommand<Unit, Unit> CloseCommand  { get; }
    public ReactiveCommand<Unit, Unit> SelectCommand { get; }

    /// <summary>When this tab was last selected, in selections (see MapToolViewModel).</summary>
    public long LastActive { get; internal set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>A whole map of New Eden, with its own overlay and zoom.</summary>
public sealed class UniverseTabViewModel : MapTabViewModel
{
    public UniverseTabViewModel(MapToolViewModel tool, UniverseViewModel map) : base(tool)
    {
        Map = map;
        map.WhenAnyValue(m => m.Title).Subscribe(_ => this.RaisePropertyChanged(nameof(TabTitle)));

        // A map that arrives after the marks were read (the graph loads a moment after the tab
        // opens) gets them as soon as it can place them.
        map.WhenAnyValue(m => m.Graph).Where(g => g is not null)
           .Subscribe(_ => { if (_live is { } live) ApplyLive(live); });
    }

    public UniverseViewModel Map { get; }

    public override string TabTitle => Map.Title;
    public override string TabGlyph => "◎";

    private LiveMapSnapshot? _live;

    /// <summary>Draws a snapshot on this map. UI thread.</summary>
    public void ApplyLive(LiveMapSnapshot live)
    {
        _live = live;
        if (Map.Graph is { } graph) Map.Markers = MapToolViewModel.BuildMarkers(live, graph);
    }
}

/// <summary>One system's page.</summary>
public sealed class SystemTabViewModel : MapTabViewModel
{
    public SystemTabViewModel(MapToolViewModel tool, SystemPageViewModel page, int systemId) : base(tool)
    {
        Page     = page;
        SystemId = systemId;
        page.WhenAnyValue(p => p.Name).Subscribe(_ => this.RaisePropertyChanged(nameof(TabTitle)));
    }

    public SystemPageViewModel Page     { get; }
    public int                 SystemId { get; }

    public override string TabTitle => Page.Name.Length > 0 ? Page.Name : MapText.StatusLoadingSystem;
    public override string TabGlyph => "●";

    public async Task LoadAsync()
    {
        try { await Page.LoadAsync(SystemId); }
        catch
        {
            // The page reports its own failures in its status; a tab must not take the app down.
        }
    }

    public override string ToString() => TabTitle;
}
