using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using EveConsole.Controls;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

public enum MapLevel { Universe, Region, System }

/// <summary>One hop in the "Universe › Tenerifis › C-FD0D" trail.</summary>
public class CrumbVm : ReactiveObject
{
    public string                      Text      { get; }
    public ReactiveCommand<Unit, Unit> GoCommand { get; }
    public bool                        IsLast    { get; }

    public CrumbVm(string text, bool isLast, Func<Task> go)
    {
        Text      = text;
        IsLast    = isLast;
        GoCommand = ReactiveCommand.CreateFromTask(go);
        GoCommand.ThrownExceptions.Subscribe(_ => { });
    }
}

public class OverlayModeVm(string name, string key)
{
    public string Name { get; } = name;
    public string Key  { get; } = key;
    public override string ToString() => Name;
}

/// <summary>
/// One legend row. Carries a list of swatches rather than a single colour because security is a
/// stepped ramp — showing one sample per band claimed 1.0 cyan stood for all of high sec and
/// left out the greens entirely.
/// </summary>
public class LegendEntryVm
{
    public string               Label    { get; }
    public IReadOnlyList<IBrush> Swatches { get; }

    public LegendEntryVm(string label, Color color) : this(label, [color]) { }

    public LegendEntryVm(string label, IEnumerable<Color> colors)
    {
        Label    = label;
        Swatches = colors.Select(c => (IBrush)new SolidColorBrush(c)).ToList();
    }
}

/// <summary>A label/value line in the detail pane. A named tuple will not do here — tuple
/// element names exist only at compile time, so a binding would look for Item1/Item2.</summary>
public class DetailRowVm(string label, string value)
{
    public string Label { get; } = label;
    public string Value { get; } = value;
}

public class SysKillVm(int killMailId, DateTimeOffset when, string ship, string victim)
{
    public int    KillMailId { get; } = killMailId;
    public string When       { get; } = when == default ? "" : when.UtcDateTime.ToString("MMM dd HH:mm");
    public string Ship       { get; } = ship;
    public string Victim     { get; } = victim;
}

public class UniverseViewModel : ReactiveObject
{
    private readonly UniverseMapService _map;
    private readonly MapStatsService?   _stats;

    public UniverseViewModel(UniverseMapService map, MapStatsService? stats = null)
    {
        _map   = map;
        _stats = stats;

        OverlayModes =
        [
            new("Security",      "security"),
            new("Constellation", "constellation"),   // regions, at universe level
            new("Sovereignty",   "sovereignty"),
            new("Industry — manufacturing", "industry:manufacturing"),
            new("Industry — reactions",     "industry:reaction"),
            new("Ship jumps (24h)",  "act:jumps:1"),
            new("Ship jumps (7d)",   "act:jumps:7"),
            new("Ship kills (24h)",  "act:ship:1"),
            new("Ship kills (7d)",   "act:ship:7"),
            new("Pod kills (7d)",    "act:pod:7"),
            new("NPC kills (24h)",   "act:npc:1"),
            new("Faction warfare",   "fw"),
            new("Incursions",        "incursions"),
            // Distinguished from "Ship kills" above: these count the killmails this app has
            // stored, whereas ship kills is CCP's own universe-wide tally.
            new("Killmails held (30d)", "kills30"),
            new("Killmails held (7d)",  "kills7"),
            new("Killmails held (24h)", "kills1"),
            new("Stations",      "stations"),
        ];
        _selectedOverlay = OverlayModes[0];

        DrillDownCommand  = ReactiveCommand.CreateFromTask<int>(DrillDownAsync);
        OpenSystemCommand = ReactiveCommand.CreateFromTask<int>(ShowSystemAsync);
        OpenSystemCommand.ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");
        GoUniverseCommand = ReactiveCommand.CreateFromTask(ShowUniverseAsync);
        RefreshCommand    = ReactiveCommand.CreateFromTask(RefreshAsync);

        // Without these, a command that throws breaks its pipeline and RxApp's default handler
        // rethrows on the UI thread, taking the app down over what should be a status message.
        DrillDownCommand .ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");
        GoUniverseCommand.ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");
        RefreshCommand   .ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");

        // Re-paint on overlay change without refetching geometry.
        this.WhenAnyValue(x => x.SelectedOverlay)
            .Skip(1)
            .SelectMany(_ => Guarded(ReapplyOverlayAsync))
            .Subscribe();

        // The jump box is an AutoCompleteBox, so it reports every keystroke; only act once the
        // text is an exact region name, which is what picking a suggestion produces.
        this.WhenAnyValue(x => x.RegionSearch)
            .Skip(1)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => Guarded(() => JumpToRegionAsync(t)))
            .Subscribe();

        // Selecting a node just updates the detail pane; drilling down is a double-click.
        this.WhenAnyValue(x => x.SelectedId)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(60))
            .SelectMany(_ => Guarded(LoadDetailAsync))
            .Subscribe();

        _ = Guarded(ShowUniverseAsync).Subscribe();
    }

    /// <summary>
    /// Runs work without ever letting it break the calling pipeline: an Rx pipeline that sees an
    /// exception unsubscribes for good, so the control would go dead for the rest of the session.
    /// Failures land in <see cref="Status"/> instead.
    /// </summary>
    private IObservable<Unit> Guarded(Func<Task> work) => Observable.FromAsync(async () =>
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            await OnUiAsync(() => Status = $"Error: {ex.Message}");
        }
        return Unit.Default;
    });

    /// <summary>
    /// Applies UI state on the UI thread. Everything here runs after an await, so it would
    /// otherwise be on a thread-pool thread — fatal for the ObservableCollections bound to
    /// ItemsControls.
    /// </summary>
    private static Task OnUiAsync(Action action) =>
        Dispatcher.UIThread.InvokeAsync(action).GetTask();

    // ── State ────────────────────────────────────────────────────────────────

    private MapLevel _level = MapLevel.Universe;
    public MapLevel Level
    {
        get => _level;
        private set
        {
            this.RaiseAndSetIfChanged(ref _level, value);
            // Drives which half of the view is showing, so it must follow every level change
            // rather than only the ones that open a system.
            this.RaisePropertyChanged(nameof(IsSystemLevel));
            this.RaisePropertyChanged(nameof(IsMapLevel));
        }
    }

    public bool IsMapLevel => Level != MapLevel.System;

    private MapGraph? _graph;
    public MapGraph? Graph
    {
        get => _graph;
        private set => this.RaiseAndSetIfChanged(ref _graph, value);
    }

    private IReadOnlyDictionary<int, MapNodeStyle>? _overlay;
    public IReadOnlyDictionary<int, MapNodeStyle>? Overlay
    {
        get => _overlay;
        private set => this.RaiseAndSetIfChanged(ref _overlay, value);
    }

    private int _selectedId;
    public int SelectedId
    {
        get => _selectedId;
        set => this.RaiseAndSetIfChanged(ref _selectedId, value);
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private string _detailTitle = "";
    public string DetailTitle
    {
        get => _detailTitle;
        private set => this.RaiseAndSetIfChanged(ref _detailTitle, value);
    }

    public ObservableCollection<DetailRowVm>   DetailRows { get; } = [];
    public ObservableCollection<CrumbVm>       Crumbs     { get; } = [];
    public ObservableCollection<LegendEntryVm> Legend     { get; } = [];

    public IReadOnlyList<OverlayModeVm> OverlayModes { get; }

    private OverlayModeVm _selectedOverlay;
    public OverlayModeVm SelectedOverlay
    {
        get => _selectedOverlay;
        set => this.RaiseAndSetIfChanged(ref _selectedOverlay, value);
    }

    /// <summary>Region names for the jump-to box.</summary>
    public ObservableCollection<string> RegionNames { get; } = [];

    private string _regionSearch = "";
    public string RegionSearch
    {
        get => _regionSearch;
        set => this.RaiseAndSetIfChanged(ref _regionSearch, value);
    }

    public ReactiveCommand<int,  Unit> DrillDownCommand  { get; }
    public ReactiveCommand<int,  Unit> OpenSystemCommand { get; }
    public ReactiveCommand<Unit, Unit> GoUniverseCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand    { get; }

    private int    _regionId;
    private string _regionName = "";
    private int    _systemId;
    private string _systemName = "";
    private List<RegionSummary> _regions = [];

    // ── System page ──────────────────────────────────────────────────────────

    public bool IsSystemLevel => Level == MapLevel.System;

    private string _sysHeader = "";
    public string SysHeader
    {
        get => _sysHeader;
        private set => this.RaiseAndSetIfChanged(ref _sysHeader, value);
    }

    private string _sysSubHeader = "";
    public string SysSubHeader
    {
        get => _sysSubHeader;
        private set => this.RaiseAndSetIfChanged(ref _sysSubHeader, value);
    }

    public ObservableCollection<DetailRowVm>                     SysFacts      { get; } = [];
    public ObservableCollection<DetailRowVm>                     SysActivity   { get; } = [];
    public ObservableCollection<DetailRowVm>                     SysIndices    { get; } = [];
    public ObservableCollection<UniverseMapService.NeighbourRow> SysNeighbours { get; } = [];
    public ObservableCollection<UniverseMapService.StationRow>   SysStations   { get; } = [];
    public ObservableCollection<UniverseMapService.StructureRow> SysStructures { get; } = [];
    public ObservableCollection<SysKillVm>                       SysKills      { get; } = [];

    /// <summary>
    /// Placeholder until intel-channel parsing lands. Stated plainly rather than left as an
    /// empty panel, so an absent feature does not read as "no reports in this system".
    /// </summary>
    public string SysIntelNote =>
        "Intel channel reports will appear here once chat-log parsing is in place.";

    // ── Navigation ───────────────────────────────────────────────────────────

    public async Task ShowUniverseAsync()
    {
        await OnUiAsync(() => Status = "Loading universe…");

        if (_regions.Count == 0) _regions = await _map.GetRegionsAsync();
        var graph = await _map.GetUniverseGraphAsync();
        var (styles, legend) = await BuildOverlayAsync(graph, byRegion: true);

        await OnUiAsync(() =>
        {
            Level      = MapLevel.Universe;
            Graph      = graph;
            Overlay    = styles;
            SelectedId = 0;

            // Only regions the universe map actually plots, so the jump box cannot land you
            // somewhere the breadcrumb trail has no route back from.
            RegionNames.Clear();
            foreach (var r in _regions.Where(r => r.IsKnownSpace)) RegionNames.Add(r.Name);

            Replace(Legend, legend);
            BuildCrumbs();
            DetailTitle = "";
            DetailRows.Clear();

            Status = $"{graph.Nodes.Count} regions · {graph.Edges.Count} region links · " +
                     "double-click a region to open it";
        });
    }

    public async Task ShowRegionAsync(int regionId)
    {
        if (_regions.Count == 0) _regions = await _map.GetRegionsAsync();

        var name = _regions.FirstOrDefault(r => r.RegionId == regionId)?.Name
                   ?? regionId.ToString();

        await OnUiAsync(() => Status = $"Loading {name}…");

        var graph = await _map.GetRegionGraphAsync(regionId);
        var (styles, legend) = await BuildOverlayAsync(graph, byRegion: false);

        await OnUiAsync(() =>
        {
            _regionId   = regionId;
            _regionName = name;

            Level      = MapLevel.Region;
            Graph      = graph;
            Overlay    = styles;
            SelectedId = 0;

            Replace(Legend, legend);
            BuildCrumbs();
            DetailTitle = "";
            DetailRows.Clear();

            var inside  = graph.Nodes.Count(n => !n.IsOutsideRegion);
            var outside = graph.Nodes.Count - inside;
            Status = $"{name}: {inside} systems" +
                     (outside > 0 ? $" · {outside} adjacent systems in neighbouring regions" : "");
        });
    }

    private async Task DrillDownAsync(int id)
    {
        // The universe map's nodes are regions, so a double-click there opens that region.
        // Inside a region the nodes are systems; double-clicking one that belongs to another
        // region jumps to that region, which is how the border systems act as exits.
        if (Level == MapLevel.Universe)
        {
            await ShowRegionAsync(id);
            return;
        }

        var node = Graph?.Nodes.FirstOrDefault(n => n.Id == id);
        if (node is { IsOutsideRegion: true })
        {
            var target = _regions.FirstOrDefault(r => r.Name == node.RegionName);
            if (target is not null) await ShowRegionAsync(target.RegionId);
            return;
        }

        await ShowSystemAsync(id);
    }

    /// <summary>
    /// Opens the system page. The map is replaced rather than shown alongside: at this level
    /// there is no graph left to draw, and the breadcrumb is what gets you back.
    /// </summary>
    public async Task ShowSystemAsync(int systemId)
    {
        await OnUiAsync(() => Status = "Loading system…");

        var view = await _map.GetSystemViewAsync(systemId);
        if (view is null)
        {
            await OnUiAsync(() => Status = "System not found");
            return;
        }

        // Statistics live in the other service, so they are layered on here rather than
        // dragging map-stats knowledge into the SDE service.
        var sov      = _stats is null ? null : await _stats.GetSovereigntyOverlayAsync();
        var indices  = new List<DetailRowVm>();
        var activity = new List<DetailRowVm>();

        if (_stats is not null)
        {
            foreach (var act in new[]
                     {
                         "manufacturing", "researching_time_efficiency",
                         "researching_material_efficiency", "copying", "invention", "reaction",
                     })
            {
                var idx = await _stats.GetLatestIndustryAsync(act);
                if (idx.TryGetValue(systemId, out var v))
                    indices.Add(new DetailRowVm(act.Replace('_', ' '), $"{v * 100:F2}%"));
            }

            var day  = await _stats.GetActivityWindowAsync(1);
            var week = await _stats.GetActivityWindowAsync(7);
            var d    = day.GetValueOrDefault(systemId);
            var w    = week.GetValueOrDefault(systemId);

            activity.Add(new DetailRowVm("Ship jumps (24h)", $"{d?.ShipJumps ?? 0:N0}"));
            activity.Add(new DetailRowVm("Ship jumps (7d)",  $"{w?.ShipJumps ?? 0:N0}"));
            activity.Add(new DetailRowVm("Ship kills (24h)", $"{d?.ShipKills ?? 0:N0}"));
            activity.Add(new DetailRowVm("Ship kills (7d)",  $"{w?.ShipKills ?? 0:N0}"));
            activity.Add(new DetailRowVm("Pod kills (7d)",   $"{w?.PodKills  ?? 0:N0}"));
            activity.Add(new DetailRowVm("NPC kills (24h)",  $"{d?.NpcKills  ?? 0:N0}"));
        }

        var facts = new List<DetailRowVm>
        {
            new("Security",      view.Detail.Security.ToString("F2")),
            new("Constellation", view.Detail.Constellation),
            new("Region",        view.Detail.Region),
        };
        if (!string.IsNullOrEmpty(view.Detail.SecurityClass))
            facts.Add(new DetailRowVm("Security class", view.Detail.SecurityClass));
        foreach (var c in view.Celestials) facts.Add(new DetailRowVm(c.Kind, c.Count.ToString("N0")));
        facts.Add(new DetailRowVm("Stargates", view.Detail.Gates.ToString("N0")));

        var holder = sov?.GetValueOrDefault(systemId);
        if (holder is not null)
        {
            facts.Add(new DetailRowVm("Sovereignty", holder.Holder));
            if (holder.Adm is { } adm) facts.Add(new DetailRowVm("ADM", adm.ToString("F1")));
        }

        await OnUiAsync(() =>
        {
            _systemId   = systemId;
            _systemName = view.Detail.Name;
            Level       = MapLevel.System;
            this.RaisePropertyChanged(nameof(IsSystemLevel));

            SysHeader    = view.Detail.Name;
            SysSubHeader = $"{view.Detail.Constellation} · {view.Detail.Region}";

            Replace(SysFacts,      facts);
            Replace(SysActivity,   activity);
            Replace(SysIndices,    indices);
            Replace(SysNeighbours, view.Neighbours);
            Replace(SysStations,   view.Stations);
            Replace(SysStructures, view.Structures);
            Replace(SysKills, view.RecentKills
                .Select(k => new SysKillVm(k.KillMailId, k.When, k.ShipName, k.VictimName)));

            BuildCrumbs();
            Status = $"{view.Detail.Name} · {view.Neighbours.Count} gates · " +
                     $"{view.Stations.Count} stations · {view.Structures.Count} known structures";
        });
    }

    private async Task RefreshAsync()
    {
        _regions = [];
        if (Level == MapLevel.Region) await ShowRegionAsync(_regionId);
        else                          await ShowUniverseAsync();
    }

    public async Task JumpToRegionAsync(string name)
    {
        if (_regions.Count == 0) _regions = await _map.GetRegionsAsync();

        var r = _regions.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (r is not null && r.RegionId != _regionId) await ShowRegionAsync(r.RegionId);
    }

    /// <summary>Caller must already be on the UI thread — Crumbs is bound to an ItemsControl.</summary>
    private void BuildCrumbs()
    {
        Crumbs.Clear();
        Crumbs.Add(new CrumbVm("Universe", Level == MapLevel.Universe, ShowUniverseAsync));

        if (Level == MapLevel.Universe) return;

        var regionId = _regionId;
        Crumbs.Add(new CrumbVm(_regionName, Level == MapLevel.Region, () => ShowRegionAsync(regionId)));

        if (Level != MapLevel.System) return;

        var systemId = _systemId;
        Crumbs.Add(new CrumbVm(_systemName, true, () => ShowSystemAsync(systemId)));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    // ── Detail pane ──────────────────────────────────────────────────────────

    private async Task LoadDetailAsync()
    {
        var id = SelectedId;
        if (id == 0)
        {
            await OnUiAsync(() => { DetailTitle = ""; DetailRows.Clear(); });
            return;
        }

        string title;
        var rows = new List<DetailRowVm>();

        if (Level == MapLevel.Universe)
        {
            var d = await _map.GetRegionDetailAsync(id);
            if (d is null) return;
            title = d.Name;
            rows.Add(new("Systems",         d.Systems.ToString("N0")));
            rows.Add(new("Constellations",  d.Constellations.ToString("N0")));
            rows.Add(new("NPC stations",    d.Stations.ToString("N0")));
            rows.Add(new("Avg security",    d.AvgSecurity.ToString("F2")));
            rows.Add(new("Region gateways", d.Gateways.ToString("N0")));
        }
        else
        {
            var d = await _map.GetSystemDetailAsync(id);
            if (d is null) return;
            title = d.Name;
            rows.Add(new("Security", d.Security.ToString("F2")));
            if (!string.IsNullOrEmpty(d.SecurityClass))
                rows.Add(new("Security class", d.SecurityClass));
            rows.Add(new("Constellation", d.Constellation));
            rows.Add(new("Region",        d.Region));
            rows.Add(new("Stargates",     d.Gates.ToString("N0")));
            rows.Add(new("NPC stations",  d.Stations.ToString("N0")));
            rows.Add(new("Planets",       d.Planets.ToString("N0")));
            rows.Add(new("Moons",         d.Moons.ToString("N0")));
        }

        await OnUiAsync(() =>
        {
            // The selection may have moved on while the queries ran.
            if (SelectedId != id) return;
            DetailTitle = title;
            Replace(DetailRows, rows);
        });
    }

    // ── Overlays ─────────────────────────────────────────────────────────────

    private async Task ReapplyOverlayAsync()
    {
        var graph = Graph;
        if (graph is null) return;

        var (styles, legend) = await BuildOverlayAsync(graph, Level == MapLevel.Universe);
        await OnUiAsync(() =>
        {
            Overlay = styles;
            Replace(Legend, legend);
        });
    }

    /// <summary>Pure: builds the style map and legend without touching bound state, so the
    /// caller decides when to publish them on the UI thread.</summary>
    private async Task<(Dictionary<int, MapNodeStyle> Styles, List<LegendEntryVm> Legend)>
        BuildOverlayAsync(MapGraph g, bool byRegion)
    {
        var styles = new Dictionary<int, MapNodeStyle>(g.Nodes.Count);
        var legend = new List<LegendEntryVm>();

        switch (SelectedOverlay.Key)
        {
            case "constellation":
                BuildConstellationOverlay(g, styles, byRegion);
                break;

            case "sovereignty":
                await BuildSovereigntyOverlayAsync(g, styles, legend, byRegion);
                break;

            case { } k when k.StartsWith("industry:"):
                await BuildIndustryOverlayAsync(g, styles, legend, k[9..], byRegion);
                break;

            case { } k when k.StartsWith("act:"):
            {
                var parts = k.Split(':');
                await BuildActivityOverlayAsync(g, styles, legend, parts[1], int.Parse(parts[2]), byRegion);
                break;
            }

            case "fw":
                await BuildFactionWarfareOverlayAsync(g, styles, legend, byRegion);
                break;

            case "incursions":
                await BuildIncursionOverlayAsync(g, styles, legend, byRegion);
                break;

            case "stations":
            {
                var counts = await _map.GetStationCountsAsync(byRegion);
                BuildCountOverlay(g, styles, legend, counts, "station", "stations",
                                  Color.Parse("#2a3550"), Color.Parse("#7fc8f0"));
                break;
            }

            case "kills30":
            case "kills7":
            case "kills1":
            {
                var days = SelectedOverlay.Key switch
                {
                    "kills30" => 30,
                    "kills7"  => 7,
                    _         => 1,
                };
                var counts = await _map.GetKillCountsAsync(days, byRegion);
                BuildCountOverlay(g, styles, legend, counts, "kill", "kills",
                                  Color.Parse("#2a2a38"), Color.Parse("#ff6a3d"));
                break;
            }

            default:
                BuildSecurityOverlay(g, styles, legend, byRegion);
                break;
        }

        return (styles, legend);
    }

    // EVE's own security colour ramp, keyed on the security value rounded to one decimal —
    // the same rounding the client uses, so 0.45 reads as 0.5 and counts as high sec.
    private static readonly (double Sec, Color Color)[] SecurityRamp =
    [
        (1.0, Color.Parse("#2FEFEF")), (0.9, Color.Parse("#48F0C0")),
        (0.8, Color.Parse("#00EF47")), (0.7, Color.Parse("#00F000")),
        (0.6, Color.Parse("#8FEF2F")), (0.5, Color.Parse("#EFEF00")),
        (0.4, Color.Parse("#D77700")), (0.3, Color.Parse("#F06000")),
        (0.2, Color.Parse("#F04800")), (0.1, Color.Parse("#D73000")),
        (0.0, Color.Parse("#F00000")),
    ];

    private static Color SecurityColor(double security)
    {
        var s = Math.Round(security, 1, MidpointRounding.AwayFromZero);
        foreach (var (sec, color) in SecurityRamp)
            if (s >= sec) return color;
        return SecurityRamp[^1].Color;   // null sec and below all share the 0.0 colour
    }

    private static void BuildSecurityOverlay(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend, bool byRegion)
    {
        foreach (var n in g.Nodes)
            styles[n.Id] = new MapNodeStyle(
                SecurityColor(n.Security),
                // Two decimals: null-sec systems all sit just below zero, and one decimal
                // collapses most of them onto an indistinguishable "-0.0".
                Caption: n.Security.ToString("F2"),
                Detail: $"Security {n.Security:F2}" + (byRegion ? " (region average)" : ""));

        // Every stop in the ramp, grouped by the band it belongs to, so the legend shows the
        // colours actually on the map — greens included — instead of one sample per band.
        legend.Add(new LegendEntryVm("High sec  1.0 – 0.5",
            SecurityRamp.Where(r => r.Sec >= 0.5).Select(r => r.Color)));
        legend.Add(new LegendEntryVm("Low sec  0.4 – 0.1",
            SecurityRamp.Where(r => r.Sec is < 0.5 and > 0.0).Select(r => r.Color)));
        legend.Add(new LegendEntryVm("Null sec  0.0 and below",
            [SecurityRamp[^1].Color]));
    }

    private static void BuildConstellationOverlay(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, bool byRegion)
    {
        // Regions have no constellation of their own, so at universe level each region becomes
        // its own group — same idea one level up, and it makes region boundaries readable.
        Func<MapNode, int> group = byRegion ? n => n.Id : n => n.ConstellationId;

        // Distinct hues spaced by the golden angle, which keeps adjacent groups visually
        // separated no matter how many there are.
        var hue = g.Nodes.Select(group).Where(c => c != 0).Distinct()
            .Select((id, i) => (id, h: i * 137.508 % 360))
            .ToDictionary(x => x.id, x => x.h);

        foreach (var n in g.Nodes)
        {
            var key = group(n);
            var color = key != 0 && hue.TryGetValue(key, out var h)
                // Systems outside the region are washed out so the region under inspection
                // still reads as the subject of the map.
                ? FromHsv(h, n.IsOutsideRegion ? 0.20 : 0.55, n.IsOutsideRegion ? 0.45 : 0.90)
                : Color.Parse("#6a6a80");

            styles[n.Id] = new MapNodeStyle(
                color,
                // Naming the constellation makes the grouping readable without having to trace
                // which blobs of colour belong together.
                Caption: byRegion ? null : n.ConstellationName,
                Detail: byRegion
                    ? $"Security {n.Security:F2} (region average)"
                    : $"{n.ConstellationName} · security {n.Security:F2}");
        }
    }

    /// <summary>
    /// Colours each system by who holds it, with the Activity Defense Multiplier as the caption
    /// — the same pairing dotlan shows, and the reason the node box has a second line.
    /// </summary>
    private async Task BuildSovereigntyOverlayAsync(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend, bool byRegion)
    {
        if (_stats is null) return;

        var sov = await _stats.GetSovereigntyOverlayAsync();

        // A distinct hue per alliance, ordered by how much space they hold so the largest
        // blocs get stable colours rather than shuffling as the map is redrawn.
        var ranked = sov.Values
            .Where(s => s.AllianceId is not null)
            .GroupBy(s => s.AllianceId!.Value)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key)
            .Select((x, i) => (Alliance: x.Key, Hue: i * 137.508 % 360))
            .ToDictionary(x => x.Alliance, x => x.Hue);

        var unclaimed = Color.Parse("#3a3a48");

        foreach (var n in g.Nodes)
        {
            // At universe level a node is a region, which has no single holder — the overlay
            // only means anything system by system.
            if (byRegion)
            {
                styles[n.Id] = new MapNodeStyle(unclaimed, Detail: "Open a region to see sovereignty");
                continue;
            }

            if (!sov.TryGetValue(n.Id, out var s) || s.AllianceId is null)
            {
                styles[n.Id] = new MapNodeStyle(
                    unclaimed,
                    Caption: sov.TryGetValue(n.Id, out var f) && f.Holder != "Unclaimed" ? f.Holder : null,
                    Detail: sov.GetValueOrDefault(n.Id)?.Holder ?? "Unclaimed");
                continue;
            }

            var color = FromHsv(ranked.GetValueOrDefault(s.AllianceId.Value), 0.55, 0.85);
            styles[n.Id] = new MapNodeStyle(
                color,
                Caption: s.Adm is { } adm ? adm.ToString("F1") : null,
                Detail: s.Adm is { } a ? $"{s.Holder} · ADM {a:F1}" : s.Holder);
        }

        var held = sov.Values.Count(s => s.AllianceId is not null);
        legend.Add(new LegendEntryVm($"{ranked.Count:N0} alliances, {held:N0} systems",
            ranked.Count > 0 ? FromHsv(ranked.Values.First(), 0.55, 0.85) : unclaimed));
        legend.Add(new LegendEntryVm("Unclaimed / NPC", unclaimed));
        legend.Add(new LegendEntryVm("Caption is the ADM", Color.Parse("#8a8a9a")));
    }

    /// <summary>
    /// Colours by industry cost index for one activity. The index is a small fraction — a busy
    /// manufacturing hub sits a few percent — so the ramp is scaled to the values actually
    /// present rather than to a fixed 0-100%.
    /// </summary>
    private async Task BuildIndustryOverlayAsync(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend,
        string activity, bool byRegion)
    {
        if (_stats is null) return;

        var idx = await _stats.GetLatestIndustryAsync(activity);
        var cold = Color.Parse("#22303a");
        var hot  = Color.Parse("#5fd0a0");

        // Regions have no index of their own; averaging their systems is the honest summary.
        var values = byRegion ? new Dictionary<int, double>() : idx;
        if (byRegion && idx.Count > 0)
        {
            var byRegionAvg = await _map.GetRegionAveragesAsync(idx);
            values = byRegionAvg;
        }

        var max = values.Count == 0 ? 0 : values.Values.Max();

        foreach (var n in g.Nodes)
        {
            var v = values.GetValueOrDefault(n.Id);
            var t = max > 0 ? v / max : 0;
            styles[n.Id] = new MapNodeStyle(
                v > 0 ? Lerp(cold, hot, t) : cold,
                Caption: v > 0 ? $"{v * 100:F2}%" : "—",
                Detail: v > 0
                    ? $"{activity.Replace('_', ' ')} index {v * 100:F2}%" +
                      (byRegion ? " (region average)" : "")
                    : "No index recorded");
        }

        legend.Add(new LegendEntryVm("lowest", cold));
        legend.Add(new LegendEntryVm(max > 0 ? $"highest ({max * 100:F2}%)" : "no data", hot));
    }

    /// <summary>
    /// Jumps and kills from CCP's own hourly counts. Distinct from the "Killmails held"
    /// overlays, which count what this app has stored — these are the universe-wide tallies and
    /// include NPC kills, which killmails do not cover at all.
    /// </summary>
    private async Task BuildActivityOverlayAsync(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend,
        string measure, int days, bool byRegion)
    {
        if (_stats is null) return;

        // Always through the windowed accessor: hourly rows survive only a day by default, so
        // reading them directly for a 7-day window would return a day and look convincing.
        var activity = await _stats.GetActivityWindowAsync(days);

        var bySystem = activity.ToDictionary(
            kv => kv.Key,
            kv => measure switch
            {
                "jumps" => kv.Value.ShipJumps,
                "ship"  => kv.Value.ShipKills,
                "pod"   => kv.Value.PodKills,
                _       => kv.Value.NpcKills,
            });

        var counts = byRegion
            ? await _map.GetRegionSumsAsync(bySystem)
            : bySystem;

        var (cold, hot, singular, plural) = measure switch
        {
            "jumps" => (Color.Parse("#22303a"), Color.Parse("#6fc8f0"), "jump", "jumps"),
            "ship"  => (Color.Parse("#2a2a38"), Color.Parse("#ff6a3d"), "ship kill", "ship kills"),
            "pod"   => (Color.Parse("#2a2a38"), Color.Parse("#f0d040"), "pod kill", "pod kills"),
            _       => (Color.Parse("#26302a"), Color.Parse("#7fd070"), "NPC kill", "NPC kills"),
        };

        BuildCountOverlay(g, styles, legend, counts, singular, plural, cold, hot);
    }

    /// <summary>
    /// Faction warfare: colour by who holds the system, caption by how contested it is.
    /// </summary>
    private async Task BuildFactionWarfareOverlayAsync(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend, bool byRegion)
    {
        if (_stats is null) return;

        var fw       = await _stats.GetLatestFactionWarfareAsync();
        var factions = await _stats.GetFactionNamesAsync();
        var neutral  = Color.Parse("#2e2e3a");

        // Only four militias hold faction-warfare space, so fixed hues read better than
        // generated ones and stay recognisable between sessions.
        var hues = fw.Values.Select(f => f.OccupierFactionId).Distinct().OrderBy(x => x)
            .Select((id, i) => (id, h: i * 90.0 + 15))
            .ToDictionary(x => x.id, x => x.h);

        foreach (var n in g.Nodes)
        {
            if (byRegion || !fw.TryGetValue(n.Id, out var f))
            {
                styles[n.Id] = new MapNodeStyle(neutral, Detail: byRegion
                    ? "Open a region to see faction warfare"
                    : "Not faction-warfare space");
                continue;
            }

            var contested = f.VictoryPointsThreshold > 0
                ? 100.0 * f.VictoryPoints / f.VictoryPointsThreshold
                : 0;

            // Contested systems are lifted toward full saturation so a fight stands out
            // against quiet space held by the same militia.
            var color = FromHsv(hues.GetValueOrDefault(f.OccupierFactionId),
                                f.ContestedState == "contested" ? 0.75 : 0.35,
                                f.ContestedState == "contested" ? 0.95 : 0.65);

            styles[n.Id] = new MapNodeStyle(
                color,
                Caption: contested > 0 ? $"{contested:F0}%" : f.ContestedState,
                Detail: $"{factions.GetValueOrDefault(f.OccupierFactionId, "Unknown")} · " +
                        $"{f.ContestedState}" +
                        (f.VictoryPointsThreshold > 0
                            ? $" · {f.VictoryPoints:N0}/{f.VictoryPointsThreshold:N0} VP"
                            : ""));
        }

        foreach (var (id, h) in hues)
            legend.Add(new LegendEntryVm(factions.GetValueOrDefault(id, $"Faction {id}"),
                FromHsv(h, 0.75, 0.95)));
        legend.Add(new LegendEntryVm("Not FW space", neutral));
    }

    /// <summary>
    /// Incursions are scoped to a constellation, not a system, so every system in an affected
    /// constellation is coloured and the staging system is called out.
    /// </summary>
    private async Task BuildIncursionOverlayAsync(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend, bool byRegion)
    {
        if (_stats is null) return;

        var inc     = await _stats.GetLatestIncursionsAsync();
        var quiet   = Color.Parse("#2a2a34");
        var staging = Color.Parse("#ff4f4f");

        var stateColor = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["established"] = Color.Parse("#c8543f"),
            ["mobilizing"]  = Color.Parse("#e0913c"),
            ["withdrawing"] = Color.Parse("#9a7a5a"),
        };

        foreach (var n in g.Nodes)
        {
            if (byRegion || n.ConstellationId == 0 || !inc.TryGetValue(n.ConstellationId, out var i))
            {
                styles[n.Id] = new MapNodeStyle(quiet, Detail: byRegion
                    ? "Open a region to see incursions"
                    : "No incursion");
                continue;
            }

            var isStaging = n.Id == i.StagingSystemId;
            styles[n.Id] = new MapNodeStyle(
                isStaging ? staging : stateColor.GetValueOrDefault(i.State, quiet),
                Caption: isStaging ? "staging" : $"{i.Influence * 100:F0}%",
                Detail: $"{i.State}" +
                        (isStaging ? " · staging system" : "") +
                        $" · influence {i.Influence * 100:F0}%" +
                        (i.HasBoss ? " · boss up" : ""));
        }

        legend.Add(new LegendEntryVm("Staging system", staging));
        foreach (var (state, c) in stateColor) legend.Add(new LegendEntryVm(state, c));
        legend.Add(new LegendEntryVm($"{inc.Count} active", quiet));
    }

    private static void BuildCountOverlay(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend,
        Dictionary<int, int> counts, string singular, string plural, Color cold, Color hot)
    {
        var max = counts.Count == 0 ? 0 : counts.Values.Max();

        foreach (var n in g.Nodes)
        {
            var c = counts.GetValueOrDefault(n.Id);
            // Log scale: a handful of systems carry most of the activity, and a linear ramp
            // leaves everything else flat black.
            var t = max > 0 && c > 0 ? Math.Log(1 + c) / Math.Log(1 + max) : 0;
            styles[n.Id] = new MapNodeStyle(
                Lerp(cold, hot, t),
                Caption: c > 0 ? c.ToString("N0") : "—",
                Detail: $"{c:N0} {(c == 1 ? singular : plural)}");
        }

        legend.Add(new LegendEntryVm("none", cold));
        legend.Add(new LegendEntryVm(max > 0 ? $"most ({max:N0})" : "none recorded", hot));
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    private static Color FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60  => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _     => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
