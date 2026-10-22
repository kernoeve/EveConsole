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
    /// <summary>Its place in the list as added, which picks its colour on the chart.</summary>
    public int    Slot     { get; set; }

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
/// every activity, and one activity's index over time, a line a system.
/// </summary>
public sealed class SystemIndexViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly CostIndexHistoryService         _history;

    public SystemIndexViewModel(IDbContextFactory<AppDbContext> dbFactory, CostIndexHistoryService history)
    {
        _dbFactory = dbFactory;
        _history   = history;

        Activities = [.. CostIndexHistoryService.Activities.Select(a => new LabelledChoice(a, ActivityName(a)))];
        _activity  = Activities.FirstOrDefault(a => a.Key == UiState.Get(UiState.SystemIndexActivity)) ?? Activities[0];

        AddCommand     = ReactiveCommand.CreateFromTask<SystemSearchResult>(AddAsync);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        ChartPaint.TrackAxesOf(this);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => _ = SearchAsync(t ?? ""));
        this.WhenAnyValue(x => x.Activity).Skip(1).Subscribe(a =>
        {
            UiState.Set(UiState.SystemIndexActivity, a.Key);
            BuildChart();
        });
    }

    public static string ActivityName(string activity) => activity switch
    {
        "manufacturing"                   => IndustryText.IndexManufacturing,
        "reaction"                        => IndustryText.IndexReaction,
        "invention"                       => IndustryText.IndexInvention,
        "copying"                         => IndustryText.IndexCopying,
        "researching_material_efficiency" => IndustryText.IndexMeResearch,
        "researching_time_efficiency"     => IndustryText.IndexTeResearch,
        _                                 => activity,
    };

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
        BuildChart();
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

    private async Task<List<SystemSearchResult>> LookUpAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return [];
        await SdeNames.EnsureLoadedAsync();
        var rows = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await (from s in db.SdeSolarSystems.AsNoTracking()
                          join r in db.SdeRegions.AsNoTracking() on s.RegionId equals r.RegionId into rs
                          from r in rs.DefaultIfEmpty()
                          where ids.Contains(s.SolarSystemId)
                          select new { s.SolarSystemId, s.Name, s.RegionId, Region = r != null ? r.Name : "" }).ToListAsync();
        });
        var byId = rows.ToDictionary(r => r.SolarSystemId);
        return ids.Where(byId.ContainsKey).Select(id => byId[id]).Select(r => new SystemSearchResult
        {
            SystemId = r.SolarSystemId,
            Name     = SdeNames.SolarSystem(r.SolarSystemId, r.Name),
            Region   = r.Region.Length > 0 ? SdeNames.Region(r.RegionId, r.Region) : "",
        }).ToList();
    }

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

        await SdeNames.EnsureLoadedAsync();
        var shownIds = SdeNames.Find(SdeNameKind.SolarSystem, text).Select(id => (int)id).ToList();
        var ids = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            return await db.SdeSolarSystems.AsNoTracking()
                .Where(s => EF.Functions.Like(s.Name, $"{text}%") || shownIds.Contains(s.SolarSystemId))
                .OrderBy(s => s.Name).Take(30).Select(s => s.SolarSystemId).ToListAsync();
        });
        if (SearchText.Trim() != text) return;

        foreach (var r in (await LookUpAsync(ids)).OrderBy(r => r.Name, StringComparer.CurrentCulture))
            SearchResults.Add(r);
        ShowResults = SearchResults.Count > 0;
    }

    // ── Numbers ──────────────────────────────────────────────────────────────

    public List<LabelledChoice> Activities { get; }

    private LabelledChoice _activity;
    /// <summary>The activity the chart draws. The table shows every one.</summary>
    public LabelledChoice Activity { get => _activity; set => this.RaiseAndSetIfChanged(ref _activity, value ?? Activities[0]); }

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
            BuildChart();
            return;
        }

        var current = await Task.Run(() => _history.CurrentAsync(ids));
        _daily      = await Task.Run(() => _history.DailyAsync(ids, CostIndexHistoryService.Activities));
        foreach (var s in Systems)
            s.SetCurrent(CostIndexHistoryService.Activities
                .Where(a => current.ContainsKey((s.SystemId, a)))
                .ToDictionary(a => a, a => current[(s.SystemId, a)]));
        StatusText = string.Format(IndustryText.IndexStatus, Systems.Count);
        BuildChart();
    }

    // ── The chart ────────────────────────────────────────────────────────────

    private ISeries[] _series = [];
    public ISeries[] Series { get => _series; private set => this.RaiseAndSetIfChanged(ref _series, value); }

    private Axis[] _xAxes = [new Axis()];
    public Axis[] XAxes { get => _xAxes; private set => this.RaiseAndSetIfChanged(ref _xAxes, value); }

    private Axis[] _yAxes = [new Axis()];
    public Axis[] YAxes { get => _yAxes; private set => this.RaiseAndSetIfChanged(ref _yAxes, value); }

    public SolidColorPaint LegendPaint     => ChartPaint.Labels;
    public SolidColorPaint LegendBackPaint { get; } = new(Palette.Sk("SurfacePanel"));

    private string _chartNote = "";
    public string ChartNote { get => _chartNote; private set => this.RaiseAndSetIfChanged(ref _chartNote, value); }

    public bool HasChart => Series.Length > 0;

    private void BuildChart()
    {
        var act   = Activity.Key;
        var shown = Systems.Where(s => _daily.ContainsKey((s.SystemId, act))).ToList();
        var days  = shown.SelectMany(s => _daily[(s.SystemId, act)].Keys).Distinct().OrderBy(d => d).ToList();

        if (shown.Count == 0 || days.Count == 0)
        {
            Series    = [];
            ChartNote = Systems.Count == 0 ? "" : IndustryText.IndexNoHistory;
            this.RaisePropertyChanged(nameof(HasChart));
            return;
        }

        var drawn = shown.Where(s => s.Slot < ChartPaint.MaxSeries).ToList();
        Series = drawn.Select(s =>
        {
            var series = _daily[(s.SystemId, act)];
            return (ISeries)new LineSeries<double?>
            {
                Name           = s.Name,
                Values         = days.Select(d => series.TryGetValue(d, out var v) ? v * 100 : (double?)null).ToArray(),
                GeometrySize   = 0,
                LineSmoothness = 0,
                Stroke         = ChartPaint.Series(s.Slot),
                Fill           = null,
            };
        }).ToArray();

        XAxes =
        [
            new Axis
            {
                Labels          = days.Select(d => d.ToString("MM-dd")).ToArray(),
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinStep         = Math.Max(1, days.Count / 10),
                SeparatorsPaint = null,
            },
        ];
        YAxes =
        [
            new Axis
            {
                Labeler         = v => $"{v:0.##}%",
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinLimit        = 0,
                SeparatorsPaint = ChartPaint.Separators,
            },
        ];

        var left = shown.Count - drawn.Count;
        ChartNote = string.Format(IndustryText.IndexChartNote, Activity.Label, days[0], days[^1])
                  + (left > 0 ? "  " + string.Format(IndustryText.CompareChartTooMany, ChartPaint.MaxSeries) : "");
        this.RaisePropertyChanged(nameof(HasChart));
    }
}
