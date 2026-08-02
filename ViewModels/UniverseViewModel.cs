using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using EveConsole.Controls;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

public enum MapLevel { Universe, Region, System }

/// <summary>One hop in the "Universe › Tenerifis › C-FD0D" trail.</summary>
public class CrumbVm : ReactiveObject
{
    public string                        Text      { get; }
    public ReactiveCommand<Unit, Unit>   GoCommand { get; }
    public bool                          IsLast    { get; }

    public CrumbVm(string text, bool isLast, Func<Task> go)
    {
        Text      = text;
        IsLast    = isLast;
        GoCommand = ReactiveCommand.CreateFromTask(go);
    }
}

public class OverlayModeVm(string name, string key)
{
    public string Name { get; } = name;
    public string Key  { get; } = key;
    public override string ToString() => Name;
}

public class LegendEntryVm(string label, Color color)
{
    public string Label { get; } = label;
    public IBrush Brush { get; } = new SolidColorBrush(color);
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

    public UniverseViewModel(UniverseMapService map)
    {
        _map = map;

        OverlayModes =
        [
            new("Security",      "security"),
            new("Constellation", "constellation"),   // regions, at universe level

            new("Kills (30d)",   "kills30"),
            new("Kills (7d)",    "kills7"),
            new("Kills (24h)",   "kills1"),
            new("Stations",      "stations"),
        ];
        _selectedOverlay = OverlayModes[0];

        DrillDownCommand = ReactiveCommand.CreateFromTask<int>(DrillDownAsync);
        GoUniverseCommand = ReactiveCommand.CreateFromTask(ShowUniverseAsync);
        RefreshCommand    = ReactiveCommand.CreateFromTask(RefreshAsync);

        // Re-paint on overlay change without refetching geometry.
        this.WhenAnyValue(x => x.SelectedOverlay)
            .Skip(1)
            .SelectMany(_ => Observable.FromAsync(ApplyOverlayAsync))
            .Subscribe();

        // The jump box is an AutoCompleteBox, so it reports every keystroke; only act once the
        // text is an exact region name, which is what picking a suggestion produces.
        this.WhenAnyValue(x => x.RegionSearch)
            .Skip(1)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .SelectMany(t => Observable.FromAsync(() => JumpToRegionAsync(t)))
            .Subscribe();

        // Selecting a node just updates the detail pane; drilling down is a double-click.
        this.WhenAnyValue(x => x.SelectedId)
            .Skip(1)
            .Throttle(TimeSpan.FromMilliseconds(60))
            .ObserveOn(RxApp.MainThreadScheduler)
            .SelectMany(_ => Observable.FromAsync(LoadDetailAsync))
            .Subscribe();

        _ = ShowUniverseAsync();
    }

    // ── State ────────────────────────────────────────────────────────────────

    private MapLevel _level = MapLevel.Universe;
    public MapLevel Level
    {
        get => _level;
        private set => this.RaiseAndSetIfChanged(ref _level, value);
    }

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
    public ObservableCollection<CrumbVm>       Crumbs  { get; } = [];
    public ObservableCollection<LegendEntryVm> Legend  { get; } = [];

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
    public ReactiveCommand<Unit, Unit> GoUniverseCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand    { get; }

    private int    _regionId;
    private string _regionName = "";
    private List<RegionSummary> _regions = [];

    // ── Navigation ───────────────────────────────────────────────────────────

    public async Task ShowUniverseAsync()
    {
        Level = MapLevel.Universe;
        Status = "Loading universe…";

        if (_regions.Count == 0)
        {
            _regions = await _map.GetRegionsAsync();
            RegionNames.Clear();
            foreach (var r in _regions.Where(r => !r.IsWormhole)) RegionNames.Add(r.Name);
        }

        Graph      = await _map.GetUniverseGraphAsync();
        SelectedId = 0;
        BuildCrumbs();
        await ApplyOverlayAsync();

        Status = $"{Graph.Nodes.Count} regions · {Graph.Edges.Count} region links · " +
                 "double-click a region to open it";
        DetailTitle = "";
        DetailRows.Clear();
    }

    public async Task ShowRegionAsync(int regionId)
    {
        var summary = _regions.FirstOrDefault(r => r.RegionId == regionId);
        _regionId   = regionId;
        _regionName = summary?.Name ?? regionId.ToString();

        Level  = MapLevel.Region;
        Status = $"Loading {_regionName}…";

        Graph      = await _map.GetRegionGraphAsync(regionId);
        SelectedId = 0;
        BuildCrumbs();
        await ApplyOverlayAsync();

        var inside = Graph.Nodes.Count(n => !n.IsOutsideRegion);
        var outside = Graph.Nodes.Count - inside;
        Status = $"{_regionName}: {inside} systems" +
                 (outside > 0 ? $" · {outside} adjacent systems shown in neighbouring regions" : "");
        DetailTitle = "";
        DetailRows.Clear();
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

        // Systems inside the current region have no deeper level yet — the system view is a
        // later phase — so select it and show what the SDE knows.
        SelectedId = id;
        await LoadDetailAsync();
    }

    private async Task RefreshAsync()
    {
        _regions.Clear();
        if (Level == MapLevel.Region) await ShowRegionAsync(_regionId);
        else                          await ShowUniverseAsync();
    }

    public async Task JumpToRegionAsync(string name)
    {
        var r = _regions.FirstOrDefault(
            x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (r is not null) await ShowRegionAsync(r.RegionId);
    }

    private void BuildCrumbs()
    {
        Crumbs.Clear();
        var atUniverse = Level == MapLevel.Universe;
        Crumbs.Add(new CrumbVm("Universe", atUniverse, ShowUniverseAsync));
        if (!atUniverse)
        {
            var id = _regionId;
            Crumbs.Add(new CrumbVm(_regionName, true, () => ShowRegionAsync(id)));
        }
    }

    // ── Detail pane ──────────────────────────────────────────────────────────

    private async Task LoadDetailAsync()
    {
        DetailRows.Clear();
        if (SelectedId == 0) { DetailTitle = ""; return; }

        if (Level == MapLevel.Universe)
        {
            var d = await _map.GetRegionDetailAsync(SelectedId);
            if (d is null) return;
            DetailTitle = d.Name;
            DetailRows.Add(new("Systems",         d.Systems.ToString("N0")));
            DetailRows.Add(new("Constellations",  d.Constellations.ToString("N0")));
            DetailRows.Add(new("NPC stations",    d.Stations.ToString("N0")));
            DetailRows.Add(new("Avg security",    d.AvgSecurity.ToString("F2")));
            DetailRows.Add(new("Region gateways", d.Gateways.ToString("N0")));
        }
        else
        {
            var d = await _map.GetSystemDetailAsync(SelectedId);
            if (d is null) return;
            DetailTitle = d.Name;
            DetailRows.Add(new("Security", d.Security.ToString("F2")));
            if (!string.IsNullOrEmpty(d.SecurityClass))
                DetailRows.Add(new("Security class", d.SecurityClass));
            DetailRows.Add(new("Constellation", d.Constellation));
            DetailRows.Add(new("Region",        d.Region));
            DetailRows.Add(new("Stargates",     d.Gates.ToString("N0")));
            DetailRows.Add(new("NPC stations",  d.Stations.ToString("N0")));
            DetailRows.Add(new("Planets",       d.Planets.ToString("N0")));
            DetailRows.Add(new("Moons",         d.Moons.ToString("N0")));
        }
    }

    // ── Overlays ─────────────────────────────────────────────────────────────

    private async Task ApplyOverlayAsync()
    {
        var g = Graph;
        if (g is null) return;

        var byRegion = Level == MapLevel.Universe;
        var styles   = new Dictionary<int, MapNodeStyle>(g.Nodes.Count);
        Legend.Clear();

        switch (SelectedOverlay.Key)
        {
            case "constellation":
                BuildConstellationOverlay(g, styles);
                break;

            case "stations":
            {
                var counts = await _map.GetStationCountsAsync(byRegion);
                BuildCountOverlay(g, styles, counts, "station", "stations",
                                  Color.Parse("#2a3550"), Color.Parse("#7fc8f0"));
                break;
            }

            case "kills30":
            case "kills7":
            case "kills1":
            {
                var days   = SelectedOverlay.Key == "kills30" ? 30 : SelectedOverlay.Key == "kills7" ? 7 : 1;
                var counts = await _map.GetKillCountsAsync(days, byRegion);
                BuildCountOverlay(g, styles, counts, "kill", "kills",
                                  Color.Parse("#2a2a38"), Color.Parse("#ff6a3d"));
                break;
            }

            default:
                BuildSecurityOverlay(g, styles);
                break;
        }

        Overlay = styles;
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
        return SecurityRamp[^1].Color;   // null-sec and below all share the 0.0 colour
    }

    private void BuildSecurityOverlay(MapGraph g, Dictionary<int, MapNodeStyle> styles)
    {
        foreach (var n in g.Nodes)
            styles[n.Id] = new MapNodeStyle(
                SecurityColor(n.Security),
                Detail: $"Security {n.Security:F2}" +
                        (Level == MapLevel.Universe ? " (region average)" : ""));

        foreach (var (sec, color) in SecurityRamp.Where(r => r.Sec is 1.0 or 0.5 or 0.0))
            Legend.Add(new LegendEntryVm(sec switch
            {
                1.0 => "1.0 high sec",
                0.5 => "0.5 high sec floor",
                _   => "0.0 and below",
            }, color));
    }

    private void BuildConstellationOverlay(MapGraph g, Dictionary<int, MapNodeStyle> styles)
    {
        // Regions have no constellation of their own, so at universe level each region becomes
        // its own group — same idea one level up, and it makes region boundaries readable.
        Func<MapNode, int> group = Level == MapLevel.Universe
            ? n => n.Id
            : n => n.ConstellationId;

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
            styles[n.Id] = new MapNodeStyle(color, Detail: $"Security {n.Security:F2}");
        }
    }

    private void BuildCountOverlay(
        MapGraph g, Dictionary<int, MapNodeStyle> styles,
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
                Badge: c > 0 ? c.ToString("N0") : null,
                Detail: $"{c:N0} {(c == 1 ? singular : plural)}");
        }

        Legend.Add(new LegendEntryVm("none", cold));
        Legend.Add(new LegendEntryVm(max > 0 ? $"most ({max:N0})" : "none recorded", hot));
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
