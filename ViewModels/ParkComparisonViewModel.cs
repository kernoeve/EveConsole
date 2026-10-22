using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveConsole.ViewModels;

/// <summary>One line of the comparison's job list: an item, how many, at what ME.</summary>
public sealed class CompareJobVm : ReactiveObject
{
    public CompareJobVm(int typeId, string typeName, long quantity, int me, Action<CompareJobVm> remove, Action changed)
    {
        TypeId    = typeId;
        TypeName  = typeName;
        _quantity = Math.Max(1, quantity).ToString();
        _me       = Math.Clamp(me, 0, 10).ToString();
        RemoveCommand = ReactiveCommand.Create(() => remove(this));
        this.WhenAnyValue(x => x.QuantityText, x => x.MeText).Skip(1).Subscribe(_ => changed());
    }

    public int    TypeId   { get; }
    public string TypeName { get; }
    public string DisplayName => SdeNames.Type(TypeId, TypeName);

    // Boxes hold what was typed; a word reads as the default (1 unit, ME 10).
    private string _quantity, _me;
    public string QuantityText { get => _quantity; set => this.RaiseAndSetIfChanged(ref _quantity, value); }
    public string MeText       { get => _me;       set => this.RaiseAndSetIfChanged(ref _me, value); }

    public long Quantity => long.TryParse(QuantityText.Replace(",", "").Trim(), out var q) && q > 0 ? q : 1;
    public int  Me       => int.TryParse(MeText.Trim(), out var m) ? Math.Clamp(m, 0, 10) : 10;

    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
}

/// <summary>One item in one park, as the results grid shows it.</summary>
public sealed class ParkCompareRowVm
{
    public required ParkCost Cost { get; init; }
    /// <summary>The cheapest park for this item.</summary>
    public bool IsBest { get; init; }
    /// <summary>How much dearer than the cheapest, as a fraction; 0 for the cheapest.</summary>
    public double OverBest { get; init; }

    public string ItemName   => SdeNames.Type(Cost.Job.TypeId, Cost.Job.TypeName);
    public string ParkName   => Cost.ParkName;
    public long   Units      => Cost.Produced;
    public decimal Total     => Cost.Total;
    public decimal PerUnit   => Cost.PerUnit;
    public string UnitsText     => Cost.Buildable ? Units.ToString("N0") : "–";
    public string MaterialsText => Cost.Buildable ? MarketFmt.Isk((double)Cost.Materials) : "–";
    public string JobCostText   => Cost.Buildable ? MarketFmt.Isk((double)Cost.JobCost) : "–";
    public string LeftoversText => !Cost.Buildable ? "–" : Cost.Leftovers > 0 ? "−" + MarketFmt.Isk((double)Cost.Leftovers) : "0";
    public string TotalText     => Cost.Buildable ? MarketFmt.Isk((double)Cost.Total) : IndustryText.CompareNotBuilt;
    public string PerUnitText   => Cost.Buildable ? MarketFmt.Isk((double)Cost.PerUnit) : "–";
    public string OverBestText  => !Cost.Buildable ? "" : IsBest ? IndustryText.CompareBest : $"+{OverBest:P1}";
}

/// <summary>
/// Indy Parks, Compare: the same items built in every park ticked, side by side, and the cost
/// again for each past day of system cost indices.
///
/// <para>The job list and the parks left unticked are remembered on this machine (UiState); a
/// park defined later starts ticked, as all of them do.</para>
/// </summary>
public sealed class ParkComparisonViewModel : ReactiveObject
{
    private readonly ParkComparisonService           _service;
    private readonly CostIndexHistoryService         _history;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ParkComparisonViewModel(ParkComparisonService service, CostIndexHistoryService history,
                                   IDbContextFactory<AppDbContext> dbFactory)
    {
        _service   = service;
        _history   = history;
        _dbFactory = dbFactory;

        CalculateCommand = ReactiveCommand.CreateFromTask(CalculateAsync);
        AddCommand       = ReactiveCommand.Create<ProdTypeSearchResult>(Add);
        CalculateCommand.ThrownExceptions.Subscribe(ex => StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message));

        LoadJobs();
        ChartPaint.TrackAxesOf(this);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => _ = SearchAsync(t ?? ""));

        this.WhenAnyValue(x => x.ChartChoice).Skip(1).Subscribe(_ => BuildChart());
    }

    // ── Parks ────────────────────────────────────────────────────────────────

    private MultiChoice _parks = new([]);
    /// <summary>Every park, as one dropdown; all ticked unless unticked here before.</summary>
    public MultiChoice Parks { get => _parks; private set => this.RaiseAndSetIfChanged(ref _parks, value); }

    /// <summary>The park list, read again — the tab is shown, or a park was added or renamed.</summary>
    public async Task LoadParksAsync()
    {
        var parks = await Task.Run(() => _service.ParksAsync());
        var off   = (UiState.Get(UiState.IndyCompareParksOff) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var choice = new MultiChoice(parks.Select(p => new CategoryChoice(p.Id.ToString(), p.Name) { Selected = !off.Contains(p.Id.ToString()) }));
        choice.SelectionChanged += () => UiState.Set(UiState.IndyCompareParksOff,
            string.Join(",", choice.Items.Where(i => !i.Selected).Select(i => i.Key)));
        Parks = choice;
    }

    // ── The job list ─────────────────────────────────────────────────────────

    public ObservableCollection<CompareJobVm> Jobs { get; } = [];

    private sealed record SavedJob(int T, string N, long Q, int Me);

    private void LoadJobs()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedJob>>(UiState.Get(UiState.IndyCompareJobs) ?? "[]") ?? [];
            foreach (var s in saved) Jobs.Add(NewJob(s.T, s.N, s.Q, s.Me));
        }
        catch (JsonException) { /* a list that cannot be read starts empty */ }
    }

    private void SaveJobs() => UiState.Set(UiState.IndyCompareJobs,
        JsonSerializer.Serialize(Jobs.Select(j => new SavedJob(j.TypeId, j.TypeName, j.Quantity, j.Me))));

    private CompareJobVm NewJob(int typeId, string name, long quantity, int me) =>
        new(typeId, name, quantity, me, j => { Jobs.Remove(j); SaveJobs(); }, SaveJobs);

    public ReactiveCommand<ProdTypeSearchResult, Unit> AddCommand { get; }

    /// <summary>A search result picked: added to the list, after the click that picked it.</summary>
    public ProdTypeSearchResult? PickedResult
    {
        get => null;
        set { if (value is { } pick) Avalonia.Threading.Dispatcher.UIThread.Post(() => Add(pick)); }
    }

    private void Add(ProdTypeSearchResult pick)
    {
        if (Jobs.All(j => j.TypeId != pick.TypeId)) Jobs.Add(NewJob(pick.TypeId, pick.TypeName, 1, 10));
        SaveJobs();
        SearchText = "";
    }

    // ── Search: only what can be built ───────────────────────────────────────

    private string _searchText = "";
    public string SearchText { get => _searchText; set => this.RaiseAndSetIfChanged(ref _searchText, value); }

    public ObservableCollection<ProdTypeSearchResult> SearchResults { get; } = [];

    private bool _showResults;
    public bool ShowResults { get => _showResults; private set => this.RaiseAndSetIfChanged(ref _showResults, value); }

    private async Task SearchAsync(string text)
    {
        text = text.Trim();
        SearchResults.Clear();
        if (text.Length < 2) { ShowResults = false; return; }

        await SdeNames.EnsureLoadedAsync();
        var shownIds = SdeNames.Find(SdeNameKind.Type, text).Select(id => (int)id).ToList();
        var matches = await Task.Run(async () =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            // As the Production Calculator offers them: the products of a manufacturing or reaction blueprint.
            return await db.SdeTypes.AsNoTracking()
                .Where(t => t.Published
                         && (EF.Functions.Like(t.Name, $"%{text}%") || shownIds.Contains(t.TypeId))
                         && db.SdeBlueprintProducts.Any(p => p.ProductTypeId == t.TypeId
                                && (p.Activity == "manufacturing" || p.Activity == "reaction")))
                .OrderBy(t => t.Name).Take(50)
                .Select(t => new { t.TypeId, t.Name }).ToListAsync();
        });
        if (SearchText.Trim() != text) return;   // typed on since

        foreach (var r in matches.Select(m => new ProdTypeSearchResult { TypeId = m.TypeId, TypeName = m.Name })
                                 .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture))
            SearchResults.Add(r);
        ShowResults = SearchResults.Count > 0;
    }

    // ── Calculate ────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> CalculateCommand { get; }

    public ObservableCollection<ParkCompareRowVm> Results { get; } = [];

    private string _statusText = IndustryText.CompareStart;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    private bool _isCalculating;
    public bool IsCalculating { get => _isCalculating; private set => this.RaiseAndSetIfChanged(ref _isCalculating, value); }

    private List<ParkCost> _costs = [];
    private Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> _indexHistory = [];

    private async Task CalculateAsync()
    {
        if (Jobs.Count == 0) { StatusText = IndustryText.CompareErrNoItems; return; }
        // The list is read when the tab is shown; if that found nothing, read it again now.
        if (Parks.Items.Count == 0) await LoadParksAsync();
        var slots = Parks.Items.Select((p, i) => (Id: int.Parse(p.Key), p.Title, Slot: i, p.Selected)).ToList();
        var parks = slots.Where(p => p.Selected).Select(p => (p.Id, p.Title, p.Slot)).ToList();
        if (parks.Count == 0) { StatusText = IndustryText.CompareErrNoParks; return; }

        var jobs = Jobs.Select(j => new ParkCompareJob(j.TypeId, j.TypeName, j.Quantity, j.Me)).ToList();
        IsCalculating = true;
        StatusText    = IndustryText.CompareCalculating;
        try
        {
            await SdeNames.EnsureLoadedAsync();
            _costs = await Task.Run(() => _service.CompareAsync(jobs, parks));
            var systems    = _costs.SelectMany(c => c.Terms).Select(t => t.SystemId).Distinct().ToList();
            var activities = _costs.SelectMany(c => c.Terms).Select(t => t.Activity).Distinct().ToList();
            _indexHistory  = await Task.Run(() => _history.DailyAsync(systems, activities));

            Results.Clear();
            foreach (var item in _costs.GroupBy(c => c.Job.TypeId))
            {
                var best = item.Where(c => c.Buildable).Select(c => c.Total).DefaultIfEmpty(0).Min();
                foreach (var c in item.OrderBy(c => !c.Buildable).ThenBy(c => c.Total))
                    Results.Add(new ParkCompareRowVm
                    {
                        Cost     = c,
                        IsBest   = c.Buildable && c.Total == best,
                        OverBest = c.Buildable && best > 0 ? (double)((c.Total - best) / best) : 0,
                    });
            }

            ChartChoices.Clear();
            ChartChoices.Add(new LabelledChoice("", IndustryText.CompareAllItems));
            foreach (var j in Jobs) ChartChoices.Add(new LabelledChoice(j.TypeId.ToString(), j.DisplayName));
            ChartChoice = ChartChoices[0];
            BuildChart();

            StatusText = string.Format(IndustryText.CompareStatusDone, jobs.Count, parks.Count);
        }
        finally { IsCalculating = false; }
    }

    // ── The chart: today's plan, past days' indices ──────────────────────────

    /// <summary>All items together, or one of them.</summary>
    public ObservableCollection<LabelledChoice> ChartChoices { get; } = [];

    private LabelledChoice? _chartChoice;
    public LabelledChoice? ChartChoice { get => _chartChoice; set => this.RaiseAndSetIfChanged(ref _chartChoice, value); }

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

    /// <summary>
    /// One line per park: the chosen item, or the whole list, costed with each day's indices.
    /// Days are those the history has for any of the systems involved; a day a system's index
    /// is missing carries its last known value forward.
    /// </summary>
    private void BuildChart()
    {
        var typeFilter = ChartChoice is { Key.Length: > 0 } c ? int.Parse(c.Key) : (int?)null;
        var byPark = _costs.Where(x => x.Buildable && (typeFilter is null || x.Job.TypeId == typeFilter))
                           .GroupBy(x => (x.ParkId, x.ParkName, x.Slot))
                           .OrderBy(g => g.Key.Slot)
                           .ToList();

        var keys = byPark.SelectMany(g => g).SelectMany(x => x.Terms).Select(t => (t.SystemId, t.Activity)).Distinct().ToList();
        var days = keys.SelectMany(k => _indexHistory.TryGetValue(k, out var s) ? s.Keys : Enumerable.Empty<DateOnly>())
                       .Distinct().OrderBy(d => d).ToList();

        if (byPark.Count == 0 || days.Count == 0)
        {
            Series    = [];
            ChartNote = _costs.Count == 0 ? "" : IndustryText.CompareNoHistory;
            this.RaisePropertyChanged(nameof(HasChart));
            return;
        }

        var drawn = byPark.Where(g => g.Key.Slot < ChartPaint.MaxSeries).ToList();
        Series = drawn.Select(g => (ISeries)new LineSeries<double?>
        {
            Name           = g.Key.ParkName,
            Values         = days.Select(d => (double?)(double)g.Sum(x => ParkComparisonService.TotalAt(x,
                                  (sys, act) => CostIndexHistoryService.On(_indexHistory.GetValueOrDefault((sys, act)), d)))).ToArray(),
            GeometrySize   = 0,
            LineSmoothness = 0,
            Stroke         = ChartPaint.Series(g.Key.Slot),
            Fill           = null,
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
                Labeler         = v => MarketFmt.Isk(v),
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                SeparatorsPaint = ChartPaint.Separators,
            },
        ];

        var left = byPark.Count - drawn.Count;
        ChartNote = string.Format(IndustryText.CompareChartNote, days[0], days[^1])
                  + (left > 0 ? "  " + string.Format(IndustryText.CompareChartTooMany, ChartPaint.MaxSeries) : "");
        this.RaisePropertyChanged(nameof(HasChart));
    }
}
