using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>A solar system found by the search box.</summary>
public sealed class SystemSearchResult
{
    public int    SystemId { get; init; }
    public string Name     { get; init; } = "";
    public string Region   { get; init; } = "";
    public string Label    => Region.Length > 0 ? $"{Name}  ·  {Region}" : Name;
}

/// <summary>One system in the list: today's index for every activity.</summary>
public sealed class SystemIndexRowVm : ReactiveObject
{
    public SystemIndexRowVm(int systemId, string name, string region, int slot, Action<SystemIndexRowVm> remove)
    {
        SystemId = systemId;
        Name     = name;
        Region   = region;
        Slot     = slot;
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
    }

    public int    SystemId { get; }
    public string Name     { get; }
    public string Region   { get; }
    /// <summary>Its place in the list as added, which picks its colour on the charts.</summary>
    public int    Slot     { get; set; }

    /// <summary>The dot beside its name: its colour in all six charts, the charts' one legend.
    /// Clear past the eighth, which the charts leave off.</summary>
    public Avalonia.Media.IBrush Swatch => Slot < ChartPaint.MaxSeries
        ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(ChartPaint.SeriesHex[Slot]))
        : Avalonia.Media.Brushes.Transparent;

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    private Dictionary<string, double> _current = [];
    public void SetCurrent(Dictionary<string, double> byActivity)
    {
        _current = byActivity;
        foreach (var n in new[] { nameof(Manufacturing), nameof(Reaction), nameof(Invention), nameof(Copying), nameof(MeResearch), nameof(TeResearch) })
            this.RaisePropertyChanged(n);
    }

    private string Pct(string activity) => _current.TryGetValue(activity, out var v) ? $"{v * 100:0.##}%" : "–";
    public string Manufacturing => Pct("manufacturing");
    public string Reaction      => Pct("reaction");
    public string Invention     => Pct("invention");
    public string Copying       => Pct("copying");
    public string MeResearch    => Pct("researching_material_efficiency");
    public string TeResearch    => Pct("researching_time_efficiency");

    public double? Value(string activity) => _current.TryGetValue(activity, out var v) ? v : null;
}

/// <summary>
/// The System Index tool: a list of systems, kept on this machine, their cost indices today for
/// every activity, and every activity's index over time, six charts, a line a system.
/// </summary>
public sealed class SystemIndexViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CostIndexHistoryService         _history;

    public SystemIndexViewModel(IDbContextFactory<AppDbContext> dbFactory, CostIndexHistoryService history)
    {
        _dbFactory = dbFactory;
        _history   = history;

        Charts = [.. CostIndexHistoryService.Activities.Select(a => new IndexChartVm(a))];

        AddCommand     = ReactiveCommand.CreateFromTask<SystemSearchResult>(AddAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => _ = SearchAsync(t ?? ""));
    }

    public static string ActivityName(string activity) => SystemIndexReport.ActivityName(activity);

    // ── The list ─────────────────────────────────────────────────────────────

    public ObservableCollection<SystemIndexRowVm> Systems { get; } = [];

    private bool _loaded;

    /// <summary>The saved list, read once; then the numbers. Called when the tool is shown.</summary>
    public async Task InitializeAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            var ids = (UiState.Get(UiState.SystemIndexSystems) ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList();
            foreach (var r in await LookUpAsync(ids)) Systems.Add(Row(r));
        }
        await RefreshAsync();
    }

    private SystemIndexRowVm Row(SystemSearchResult r) => new(r.SystemId, r.Name, r.Region, NextSlot(), row =>
    {
        Systems.Remove(row);
        Save();
        BuildCharts();
    });

    /// <summary>The lowest colour slot no system in the list holds, so a removal does not
    /// repaint the others and the next one added fills the gap.</summary>
    private int NextSlot()
    {
        var taken = Systems.Select(s => s.Slot).ToHashSet();
        var slot = 0;
        while (taken.Contains(slot)) slot++;
        return slot;
    }

    private void Save() => UiState.Set(UiState.SystemIndexSystems, string.Join(",", Systems.Select(s => s.SystemId)));

    public ReactiveCommand<SystemSearchResult, Unit> AddCommand     { get; }

    /// <summary>A search result picked: added, after the click that picked it.</summary>
    public SystemSearchResult? PickedResult
    {
        get => null;
        set { if (value is { } pick) Avalonia.Threading.Dispatcher.UIThread.Post(() => AddCommand.Execute(pick).Subscribe()); }
    }
    public ReactiveCommand<Unit, Unit>               RefreshCommand { get; }

    private async Task AddAsync(SystemSearchResult pick)
    {
        SearchText = "";
        if (Systems.Any(s => s.SystemId == pick.SystemId)) return;
        Systems.Add(Row(pick));
        Save();
        await RefreshAsync();
    }

    private async Task<List<SystemSearchResult>> LookUpAsync(IReadOnlyCollection<int> ids) =>
        [.. (await Task.Run(() => SystemIndexReport.LookUpAsync(_dbFactory, [.. ids]))).Select(Result)];

    private static SystemSearchResult Result(IndexSystem s) => new() { SystemId = s.SystemId, Name = s.Name, Region = s.Region };

    // ── Search ───────────────────────────────────────────────────────────────

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    public ObservableCollection<SystemSearchResult> SearchResults { get; } = [];

    private bool _showResults;
    public bool ShowResults { get => _showResults; private set => this.RaiseAndSetIfChanged(ref _showResults, value); }

    private async Task SearchAsync(string text)
    {
        text = text.Trim();
        SearchResults.Clear();
        if (text.Length < 2) { ShowResults = false; return; }

        var found = await Task.Run(() => SystemIndexReport.SearchAsync(_dbFactory, text));
        if (SearchText.Trim() != text) return;

        foreach (var r in found) SearchResults.Add(Result(r));
        ShowResults = SearchResults.Count > 0;
    }

    // ── Numbers ──────────────────────────────────────────────────────────────

    private Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> _daily = [];

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private async Task RefreshAsync()
    {
        var ids = Systems.Select(s => s.SystemId).ToList();
        if (ids.Count == 0)
        {
            _daily = [];
            StatusText = IndustryText.IndexStart;
            BuildCharts();
            return;
        }

        var current = await Task.Run(() => _history.CurrentAsync(ids));
        _daily      = await Task.Run(() => _history.DailyAsync(ids, CostIndexHistoryService.Activities));
        foreach (var s in Systems)
            s.SetCurrent(CostIndexHistoryService.Activities
                .Where(a => current.ContainsKey((s.SystemId, a)))
                .ToDictionary(a => a, a => current[(s.SystemId, a)]));
        StatusText = string.Format(IndustryText.IndexStatus, Systems.Count);
        BuildCharts();
    }

    // ── The charts: one an activity, a line a system ──────────────────────────

    /// <summary>The six activities, each its own chart, in the table's order.</summary>
    public List<IndexChartVm> Charts { get; }

    private string _chartNote = "";
    public string ChartNote { get => _chartNote; private set => this.RaiseAndSetIfChanged(ref _chartNote, value); }

    private void BuildCharts()
    {
        var days = _daily.Values.SelectMany(v => v.Keys).Distinct().OrderBy(d => d).ToList();
        foreach (var chart in Charts) chart.Build(Systems, _daily, days);

        var withHistory = Systems.Any(s => _daily.Keys.Any(k => k.SystemId == s.SystemId));
        var left = Systems.Count(s => s.Slot >= ChartPaint.MaxSeries);
        ChartNote = Systems.Count == 0 ? ""
                  : !withHistory || days.Count == 0 ? IndustryText.IndexNoHistory
                  : string.Format(IndustryText.IndexChartsNote, days[0], days[^1])
                    + (left > 0 ? "  " + string.Format(IndustryText.CompareChartTooMany, ChartPaint.MaxSeries) : "");
    }
}

/// <summary>
/// One activity's cost index over time, a line a system. The colour key is the table's dot
/// beside each system's name — the same in all six charts, so it is shown once, not six times.
/// </summary>
public sealed class IndexChartVm : ReactiveObject
{
    public IndexChartVm(string activity)
    {
        Activity = activity;
        Title    = SystemIndexViewModel.ActivityName(activity);
        ChartPaint.TrackAxesOf(this);
    }

    public string Activity { get; }
    public string Title    { get; }

    private ISeries[] _series = [];
    public ISeries[] Series { get => _series; private set => this.RaiseAndSetIfChanged(ref _series, value); }

    private Axis[] _xAxes = [new Axis()];
    public Axis[] XAxes { get => _xAxes; private set => this.RaiseAndSetIfChanged(ref _xAxes, value); }

    private Axis[] _yAxes = [new Axis()];
    public Axis[] YAxes { get => _yAxes; private set => this.RaiseAndSetIfChanged(ref _yAxes, value); }

    private bool _hasChart;
    public bool HasChart
    {
        get => _hasChart;
        private set { this.RaiseAndSetIfChanged(ref _hasChart, value); this.RaisePropertyChanged(nameof(ChartOpacity)); }
    }

    /// <summary>Transparent rather than hidden when empty: a chart hidden while its series
    /// arrive keeps no size and stays blank once shown.</summary>
    public double ChartOpacity => HasChart ? 1 : 0;

    /// <summary>The report's chart for this activity; a day with no sample is a gap in its line.
    /// No legend here: the table's dot beside each system is the key.</summary>
    public void Build(IEnumerable<SystemIndexRowVm> systems,
                      Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> daily,
                      List<DateOnly> days)
    {
        // Nothing to draw: said before the theme's paints are asked for, which only the UI thread
        // may read; the first refresh, with no systems yet, can run off it.
        var list = systems.ToList();
        if (days.Count == 0 || !list.Any(s => daily.ContainsKey((s.SystemId, Activity)))) { HasChart = false; Series = []; return; }
        var chart = SystemIndexReport.Chart(Activity,
            [.. list.Select(s => new IndexSystem(s.SystemId, s.Name, s.Region, s.Slot))],
            daily, () => ChartPaint.Labels, () => ChartPaint.Separators);
        HasChart = chart is not null;
        if (chart is null) { Series = []; return; }
        (Series, XAxes, YAxes) = (chart.Series, chart.XAxes, chart.YAxes);
    }
}
