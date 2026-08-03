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


public class UniverseViewModel : ReactiveObject
{
    private readonly UniverseMapService _map;
    private readonly MapStatsService?   _stats;

    public UniverseViewModel(
        UniverseMapService   map,
        MapStatsService?     stats     = null,
        SystemPageViewModel? systemPage = null)
    {
        _map       = map;
        _stats     = stats;
        SystemPage = systemPage;

        OverlayModes =
        [
            new("Security",      "security"),
            new("Constellation", "constellation"),   // regions, at universe level
            new("Sovereignty",   "sovereignty"),
            new("Sovereignty ADM", "adm"),
            new("Industry — manufacturing", "industry:manufacturing"),
            new("Industry — reactions",     "industry:reaction"),
            new("Industry — ME research",   "industry:researching_material_efficiency"),
            new("Industry — TE research",   "industry:researching_time_efficiency"),
            new("Industry — copying",       "industry:copying"),
            new("Industry — invention",     "industry:invention"),
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
            new("Planets",       "cel:0"),
            new("Moons",         "cel:1"),
            new("Asteroid belts", "cel:3"),
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

    /// <summary>The system page owns its own state; this view model only decides when it is
    /// shown and which system it is showing.</summary>
    public SystemPageViewModel? SystemPage { get; }

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

        if (SystemPage is null)
        {
            await OnUiAsync(() => Status = "System view unavailable");
            return;
        }

        var name = await _map.GetSystemDetailAsync(systemId);
        if (name is null)
        {
            await OnUiAsync(() => Status = "System not found");
            return;
        }

        await SystemPage.LoadAsync(systemId);

        await OnUiAsync(() =>
        {
            _systemId   = systemId;
            _systemName = name.Name;
            Level       = MapLevel.System;
            BuildCrumbs();
            Status = $"{name.Name} · {name.Region}";
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

            case "adm":
                await BuildAdmOverlayAsync(g, styles, legend, byRegion);
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
                BuildCountOverlay(g, styles, legend, counts, "station", "stations");
                break;
            }

            case { } k when k.StartsWith("cel:"):
            {
                var kind = int.Parse(k[4..]);
                var (singular, plural) = kind switch
                {
                    0 => ("planet", "planets"),
                    1 => ("moon",   "moons"),
                    _ => ("asteroid belt", "asteroid belts"),
                };
                var counts = await _map.GetCelestialCountsAsync(kind, byRegion);
                BuildCountOverlay(g, styles, legend, counts, singular, plural);
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
                BuildCountOverlay(g, styles, legend, counts, "kill", "kills");
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
    /// Long alliance names would make the node boxes enormous, so the caption is trimmed. The
    /// full name stays on the hover tooltip, so nothing is lost.
    ///
    /// 16 is measured, not guessed: swept across all 70 known-space regions at the zoom where
    /// boxes replace dots, 16 characters is the widest cap that collides nowhere, while 18
    /// starts overlapping in Omist. Median holder name is 15 characters, so most fit whole.
    /// </summary>
    private const int HolderCaptionChars = 16;

    private static string ShortHolder(string name) =>
        name.Length <= HolderCaptionChars ? name : name[..(HolderCaptionChars - 1)] + "…";

    /// <summary>
    /// Colours each system by who holds it, and names the holder in the caption. ADM is a
    /// separate overlay: it answers a different question, and showing it here meant the
    /// sovereignty map never actually told you whose space you were looking at.
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
                Caption: ShortHolder(s.Holder),
                Detail: s.Adm is { } a ? $"{s.Holder} · ADM {a:F1}" : s.Holder);
        }

        // The biggest holders, since a legend of 79 alliances would be useless.
        foreach (var top in sov.Values.Where(s => s.AllianceId is not null)
                     .GroupBy(s => s.AllianceId!.Value)
                     .OrderByDescending(x => x.Count()).Take(6))
            legend.Add(new LegendEntryVm(
                $"{ShortHolder(top.First().Holder)} ({top.Count()})",
                FromHsv(ranked.GetValueOrDefault(top.Key), 0.55, 0.85)));

        var held = sov.Values.Count(s => s.AllianceId is not null);
        legend.Add(new LegendEntryVm($"…{ranked.Count:N0} alliances, {held:N0} systems", unclaimed));
        legend.Add(new LegendEntryVm("Unclaimed / NPC", unclaimed));
    }

    /// <summary>
    /// Activity Defense Multiplier on its own, on the shared heat scale: light green at 1
    /// rising to red at 6.
    /// </summary>
    private async Task BuildAdmOverlayAsync(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend, bool byRegion)
    {
        if (_stats is null) return;

        var adm = await _stats.GetLatestAdmAsync();

        var values = byRegion && adm.Count > 0
            ? await _map.GetRegionAveragesAsync(adm)
            : byRegion ? [] : adm;

        foreach (var n in g.Nodes)
        {
            if (!values.TryGetValue(n.Id, out var v))
            {
                styles[n.Id] = new MapNodeStyle(HeatNone, Detail: "No sovereignty structure");
                continue;
            }

            // Anchored to ADM's own 1-6 range rather than to the values present, so a region
            // that happens to be uniformly high still reads high instead of being rescaled
            // back down to the middle of the ramp.
            styles[n.Id] = new MapNodeStyle(
                Heat((v - 1.0) / 5.0),
                Caption: v.ToString("F1"),
                Detail: $"ADM {v:F1}" + (byRegion ? " (region average)" : ""));
        }

        AddHeatLegend(legend, "1.0", "6.0");
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

        // Regions have no index of their own; averaging their systems is the honest summary.
        var values = byRegion ? new Dictionary<int, double>() : idx;
        if (byRegion && idx.Count > 0)
        {
            var byRegionAvg = await _map.GetRegionAveragesAsync(idx);
            values = byRegionAvg;
        }

        // Scaled to what is on screen, so a region's own spread is visible rather than being
        // flattened against the universe-wide peak.
        var max = VisibleMax(g, values);

        foreach (var n in g.Nodes)
        {
            var v = values.GetValueOrDefault(n.Id);
            var t = max > 0 ? v / max : 0;
            styles[n.Id] = new MapNodeStyle(
                v > 0 ? Heat(t) : HeatNone,
                Caption: v > 0 ? $"{v * 100:F2}%" : "—",
                Detail: v > 0
                    ? $"{activity.Replace('_', ' ')} index {v * 100:F2}%" +
                      (byRegion ? " (region average)" : "")
                    : "No index recorded");
        }

        AddHeatLegend(legend, "lowest",
            max > 0 ? $"{max * 100:F2}% — highest {(byRegion ? "region" : "system")} shown" : "no data");
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

        var (singular, plural) = measure switch
        {
            "jumps" => ("jump", "jumps"),
            "ship"  => ("ship kill", "ship kills"),
            "pod"   => ("pod kill", "pod kills"),
            _       => ("NPC kill", "NPC kills"),
        };

        BuildCountOverlay(g, styles, legend, counts, singular, plural);
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

    // ── Shared heat scale ────────────────────────────────────────────────────
    //
    // One scale for every numeric overlay — indices, ADM, kills, jumps, stations — so a colour
    // means the same thing whichever one is selected. Light green at the low end rising to red
    // at the high end, and no tint at all for zero, which keeps "nothing here" visually
    // distinct from "a little of something" instead of being the bottom of the ramp.

    private static readonly Color HeatNone = Color.Parse("#2b2b35");
    private static readonly Color HeatLow  = Color.Parse("#a9e3a0");
    private static readonly Color HeatMid  = Color.Parse("#e9d24d");
    private static readonly Color HeatHigh = Color.Parse("#d43f2f");

    private static Color Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5
            ? Lerp(HeatLow, HeatMid, t * 2)
            : Lerp(HeatMid, HeatHigh, (t - 0.5) * 2);
    }

    private static void AddHeatLegend(List<LegendEntryVm> legend, string low, string high)
    {
        legend.Add(new LegendEntryVm(low,  HeatLow));
        legend.Add(new LegendEntryVm("",   HeatMid));
        legend.Add(new LegendEntryVm(high, HeatHigh));
        legend.Add(new LegendEntryVm("none", HeatNone));
    }

    /// <summary>
    /// Largest value among the nodes the map is actually about.
    ///
    /// Scaling to the whole universe made every region except the busiest look uniformly cold —
    /// Tenerifis tops out at 6.4% manufacturing and rendered green-yellow because Jita sits at
    /// 17%, using two colour bands across 81 systems instead of five. Anchoring to what is on
    /// screen spreads the ramp across the map in front of you, and the legend names the value
    /// it corresponds to so the absolute number is never lost.
    ///
    /// Gateway systems are excluded even though they are drawn: they belong to the neighbouring
    /// region, and a single border system next to a trade hub would otherwise flatten the whole
    /// region's scale — the very problem this is fixing. They clamp to the top of the ramp.
    /// </summary>
    private static double VisibleMax(MapGraph g, IReadOnlyDictionary<int, double> values)
    {
        double max = 0;
        foreach (var n in g.Nodes)
            if (!n.IsOutsideRegion && values.TryGetValue(n.Id, out var v) && v > max) max = v;
        return max;
    }

    private static int VisibleMax(MapGraph g, IReadOnlyDictionary<int, int> values)
    {
        var max = 0;
        foreach (var n in g.Nodes)
            if (!n.IsOutsideRegion && values.TryGetValue(n.Id, out var v) && v > max) max = v;
        return max;
    }

    private static void BuildCountOverlay(
        MapGraph g, Dictionary<int, MapNodeStyle> styles, List<LegendEntryVm> legend,
        Dictionary<int, int> counts, string singular, string plural)
    {
        var max = VisibleMax(g, counts);

        foreach (var n in g.Nodes)
        {
            var c = counts.GetValueOrDefault(n.Id);
            // Log scale: a handful of systems carry most of the activity, and a linear ramp
            // leaves everything else indistinguishable at the bottom.
            var t = max > 0 && c > 0 ? Math.Log(1 + c) / Math.Log(1 + max) : 0;
            styles[n.Id] = new MapNodeStyle(
                c > 0 ? Heat(t) : HeatNone,
                Caption: c > 0 ? c.ToString("N0") : "—",
                Detail: $"{c:N0} {(c == 1 ? singular : plural)}");
        }

        AddHeatLegend(legend, "lowest", max > 0 ? $"{max:N0} — highest shown" : "no data");
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
