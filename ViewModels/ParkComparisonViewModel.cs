using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using Avalonia.Media;
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

/// <summary>One line in one park, as the results grid shows it. The ranking and the texts are
/// the report's, so the grid and a scheduled post say the same.</summary>
public sealed class ParkCompareRowVm(RankedCost ranked)
{
    public RankedCost Ranked { get; } = ranked;
    public ParkCost   Cost   => Ranked.Cost;

    /// <summary>The cheapest park for this line.</summary>
    public bool   IsBest    => Ranked.IsBest;
    /// <summary>How much dearer than the cheapest, as a fraction; 0 for the cheapest.</summary>
    public double OverBest  => Ranked.OverBest;
    /// <summary>The quickest park for this line.</summary>
    public bool   IsFastest => Ranked.IsFastest;
    /// <summary>How much slower than the quickest, as a fraction; 0 for the quickest.</summary>
    public double SlowerBy  => Ranked.SlowerBy;

    public string  ItemName       => Ranked.ItemName;
    public string  OperationLabel => ParkCompareReport.OpLabel(Cost.Job.Op);
    public string  ParkName       => Cost.ParkName;
    public long    Units          => Cost.Produced;
    public decimal Total          => Cost.Total;
    public decimal PerUnit        => Cost.PerUnit;
    public double  Seconds        => Cost.Seconds ?? double.MaxValue;
    public string  Detail         => Cost.Detail;
    public string UnitsText     => Ranked.UnitsText;
    public string MaterialsText => Ranked.MaterialsText;
    public string JobCostText   => Ranked.JobCostText;
    public string LeftoversText => Ranked.LeftoversText;
    public string TotalText     => Ranked.TotalText;
    public string PerUnitText   => Ranked.PerUnitText;
    public string OverBestText  => Ranked.OverBestText;
    public string TimeText      => Ranked.TimeText;
    public string SlowerText    => Ranked.SlowerText;
}

/// <summary>One job in one park, as a summary cell: cost and time, each against the best park
/// for that job. Empty where the job is not done there.</summary>
public sealed class CompareSummaryCellVm(RankedCost? ranked)
{
    private readonly RankedCost? _r = ranked is { Done: true } ? ranked : null;

    public bool    Has      => _r is not null;
    public decimal Total    => _r?.Cost.Total ?? decimal.MaxValue;
    public double  OverBest => _r?.OverBest ?? double.MaxValue;
    public double  Seconds  => _r?.Cost.Seconds ?? double.MaxValue;
    public double  SlowerBy => _r is { Cost.Seconds: not null } r ? r.SlowerBy : double.MaxValue;

    public string CostText     => _r?.TotalText ?? "–";
    public string OverBestText => _r?.OverBestText ?? "";
    public string TimeText     => _r?.TimeText ?? "–";
    public string SlowerText   => _r?.SlowerText ?? "";
    /// <summary>The cost cell's tip; none where there is nothing to say, so no empty tip shows.</summary>
    public string? Detail      => string.IsNullOrEmpty(_r?.Cost.Detail) ? null : _r.Cost.Detail;

    public IBrush FigureColor => IskText.FigureColour(Has);
    public IBrush OverColor   => Colour(_r?.CostStanding ?? Standing.None);
    public IBrush SlowerColor => Colour(_r?.TimeStanding ?? Standing.None);

    private static IBrush Colour(Standing s) => s switch
    {
        Standing.Best      => Palette.Good,
        Standing.FarBehind => Palette.Bad,
        Standing.None      => Palette.TextFaint,
        _                  => Palette.TextPrimary,
    };
}

/// <summary>A summary row: a job across the parks, or a park across the jobs.</summary>
public sealed class CompareSummaryRowVm(string title, IReadOnlyList<CompareSummaryCellVm> cells)
{
    public string Title { get; } = title;
    public IReadOnlyList<CompareSummaryCellVm> Cells { get; } = cells;
}

/// <summary>
/// Indy Parks, Compare: the same job list done in every park ticked — builds, copies, invention,
/// research — side by side for cost and time, and the cost again for each past day of system
/// cost indices. What it shows is <see cref="ParkCompareReport"/>'s, which a scheduled post uses.
///
/// <para>The job list and the parks left unticked are remembered on this machine (UiState); a
/// park defined later starts ticked, as all of them do.</para>
/// </summary>
public sealed class ParkComparisonViewModel : ReactiveObject
{
    private readonly ParkComparisonService   _service;
    private readonly CostIndexHistoryService _history;

    public ParkComparisonViewModel(ParkComparisonService service, CostIndexHistoryService history,
                                   IDbContextFactory<AppDbContext> dbFactory)
    {
        _service = service;
        _history = history;

        JobList = new CompareJobListVm(service, dbFactory, LoadJobs());
        JobList.ListChanged += () => UiState.Set(UiState.IndyCompareJobs, JsonSerializer.Serialize(JobList.Specs));

        CalculateCommand = ReactiveCommand.CreateFromTask(CalculateAsync);
        CalculateCommand.ThrownExceptions.Subscribe(ex => StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message));

        ChartPaint.TrackAxesOf(this);
        this.WhenAnyValue(x => x.ChartChoice).Skip(1).Subscribe(_ => { if (!_refilling) BuildCharts(); });
    }

    // ── Parks ────────────────────────────────────────────────────────────────

    private MultiChoice _parks = new([]);
    /// <summary>Every park, as one dropdown; all ticked unless unticked here before.</summary>
    public MultiChoice Parks { get => _parks; private set => this.RaiseAndSetIfChanged(ref _parks, value); }

    /// <summary>The park list, read again — the tab is shown, or a park was added or renamed. The
    /// decryptors come with it, the first time.</summary>
    public async Task LoadParksAsync()
    {
        var parks = await Task.Run(() => _service.ParksAsync());
        var off   = (UiState.Get(UiState.IndyCompareParksOff) ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var choice = new MultiChoice(parks.Select(p => new CategoryChoice(p.Id.ToString(), p.Name) { Selected = !off.Contains(p.Id.ToString()) }));
        choice.SelectionChanged += () => UiState.Set(UiState.IndyCompareParksOff,
            string.Join(",", choice.Items.Where(i => !i.Selected).Select(i => i.Key)));
        Parks = choice;
        await JobList.LoadDecryptorsAsync();
    }

    // ── The job list ─────────────────────────────────────────────────────────

    /// <summary>The lines to compare, remembered on this machine.</summary>
    public CompareJobListVm JobList { get; }

    /// <summary>Shorthand for the list's lines.</summary>
    public ObservableCollection<CompareJobVm> Jobs => JobList.Jobs;

    private static List<CompareJobSpec> LoadJobs()
    {
        try { return JsonSerializer.Deserialize<List<CompareJobSpec>>(UiState.Get(UiState.IndyCompareJobs) ?? "[]") ?? []; }
        catch (JsonException) { return []; }   // a list that cannot be read starts empty
    }

    // ── Calculate ────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> CalculateCommand { get; }

    public ObservableCollection<ParkCompareRowVm> Results { get; } = [];

    private string _statusText = IndustryText.CompareStart;
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    /// <summary>A line in the status, for what the view does itself: a picture that would not copy.</summary>
    public void ShowStatus(string text) => StatusText = text;

    private bool _isCalculating;
    public bool IsCalculating { get => _isCalculating; private set => this.RaiseAndSetIfChanged(ref _isCalculating, value); }

    private List<ParkCost>   _costs  = [];
    private List<RankedCost> _ranked = [];
    private List<string>     _lineLabels = [];
    private Dictionary<(int SystemId, string Activity), SortedDictionary<DateOnly, double>> _indexHistory = [];

    private async Task CalculateAsync()
    {
        if (Jobs.Count == 0) { StatusText = IndustryText.CompareErrNoItems; return; }
        // The list is read when the tab is shown; if that found nothing, read it again now.
        if (Parks.Items.Count == 0) await LoadParksAsync();
        var slots = Parks.Items.Select((p, i) => (Id: int.Parse(p.Key), p.Title, Slot: i, p.Selected)).ToList();
        var parks = slots.Where(p => p.Selected).Select(p => (p.Id, p.Title, p.Slot)).ToList();
        if (parks.Count == 0) { StatusText = IndustryText.CompareErrNoParks; return; }

        var specs = JobList.Specs;
        var jobs  = specs.Select((j, i) => j.ToJob(i)).ToList();
        IsCalculating = true;
        StatusText    = IndustryText.CompareCalculating;
        try
        {
            await SdeNames.EnsureLoadedAsync();
            _costs = await Task.Run(() => _service.CompareAsync(jobs, parks));
            var systems    = _costs.SelectMany(c => c.Terms).Select(t => t.SystemId).Distinct().ToList();
            var activities = _costs.SelectMany(c => c.Terms).Select(t => t.Activity).Distinct().ToList();
            _indexHistory  = await Task.Run(() => _history.DailyAsync(systems, activities));

            _ranked     = ParkCompareReport.Rank(_costs);
            _lineLabels = [.. specs.Select(s => s.Label)];
            Results.Clear();
            foreach (var r in _ranked) Results.Add(new ParkCompareRowVm(r));
            BuildSummary();

            // The charts are built once, after the list is refilled. Refilling it moves the
            // selection twice (cleared, then the first entry), and each move rebuilt the charts:
            // three sets of series in a row, the first Calculate showing none of them.
            _refilling = true;
            try
            {
                ChartChoices.Clear();
                ChartChoices.Add(new LabelledChoice("", IndustryText.CompareAllItems));
                for (var i = 0; i < _lineLabels.Count; i++) ChartChoices.Add(new LabelledChoice(i.ToString(), _lineLabels[i]));
                ChartChoice = ChartChoices[0];
            }
            finally { _refilling = false; }
            BuildCharts();

            StatusText = string.Format(IndustryText.CompareStatusDone, jobs.Count, parks.Count);
        }
        finally { IsCalculating = false; }
    }

    // ── The summary: the detail grid as jobs by parks, or parks by jobs ──────

    private const string ByJob = "jobs", ByPark = "parks";

    public List<LabelledChoice> SummaryLayouts { get; } =
        [new(ByJob, IndustryText.CompareSummaryRowsJobs), new(ByPark, IndustryText.CompareSummaryRowsParks)];

    private LabelledChoice? _summaryLayout;
    /// <summary>What the summary's rows are; the other becomes its groups of columns. Remembered
    /// on this machine.</summary>
    public LabelledChoice SummaryLayout
    {
        get => _summaryLayout ??= SummaryLayouts.FirstOrDefault(l => l.Key == UiState.Get(UiState.IndyCompareSummaryBy)) ?? SummaryLayouts[0];
        set
        {
            if (value is null || value.Key == SummaryLayout.Key) return;
            _summaryLayout = value;
            this.RaisePropertyChanged();
            UiState.Set(UiState.IndyCompareSummaryBy, value.Key);
            BuildSummary();
        }
    }

    public bool SummaryByPark => SummaryLayout.Key == ByPark;

    /// <summary>The headings over the groups of four columns: the parks, or the jobs. Set
    /// before the rows, so the view builds the columns the rows then fill.</summary>
    public IReadOnlyList<string> SummaryGroups { get; private set; } = [];

    public ObservableCollection<CompareSummaryRowVm> SummaryRows { get; } = [];

    public bool HasSummary => SummaryRows.Count > 0;

    private void BuildSummary()
    {
        var pivot = ParkCompareReport.Pivot(_ranked, _lineLabels, SummaryByPark);
        SummaryRows.Clear();
        SummaryGroups = pivot.Groups;
        this.RaisePropertyChanged(nameof(SummaryGroups));
        this.RaisePropertyChanged(nameof(SummaryByPark));
        foreach (var row in pivot.Rows)
            // ⚠️ A List, not a collection expression: that makes a compiler-made read-only type
            // whose indexer the grid's Cells[i] bindings cannot reach, and every cell came up blank.
            SummaryRows.Add(new CompareSummaryRowVm(row.Title, row.Cells.Select(c => new CompareSummaryCellVm(c)).ToList()));
        this.RaisePropertyChanged(nameof(HasSummary));
    }

    // ── The charts: cost over time, and time per park ────────────────────────

    /// <summary>All lines together, or one of them; both charts follow it.</summary>
    public ObservableCollection<LabelledChoice> ChartChoices { get; } = [];

    /// <summary>The chart list is being refilled: its selection moving is not a choice to redraw for.</summary>
    private bool _refilling;

    private LabelledChoice? _chartChoice;
    public LabelledChoice? ChartChoice { get => _chartChoice; set => this.RaiseAndSetIfChanged(ref _chartChoice, value); }

    private ISeries[] _series = [];
    public ISeries[] Series { get => _series; private set => this.RaiseAndSetIfChanged(ref _series, value); }

    private Axis[] _xAxes = [new Axis()];
    public Axis[] XAxes { get => _xAxes; private set => this.RaiseAndSetIfChanged(ref _xAxes, value); }

    private Axis[] _yAxes = [new Axis()];
    public Axis[] YAxes { get => _yAxes; private set => this.RaiseAndSetIfChanged(ref _yAxes, value); }

    private ISeries[] _timeSeries = [];
    public ISeries[] TimeSeries { get => _timeSeries; private set => this.RaiseAndSetIfChanged(ref _timeSeries, value); }

    private Axis[] _timeXAxes = [new Axis()];
    public Axis[] TimeXAxes { get => _timeXAxes; private set => this.RaiseAndSetIfChanged(ref _timeXAxes, value); }

    private Axis[] _timeYAxes = [new Axis()];
    public Axis[] TimeYAxes { get => _timeYAxes; private set => this.RaiseAndSetIfChanged(ref _timeYAxes, value); }

    public SolidColorPaint LegendPaint     => ChartPaint.Labels;
    public SolidColorPaint LegendBackPaint { get; } = new(Palette.Sk("SurfacePanel"));

    private string _chartNote = "";
    public string ChartNote { get => _chartNote; private set => this.RaiseAndSetIfChanged(ref _chartNote, value); }

    public bool HasChart     => Series.Length > 0;
    public bool HasTimeChart => TimeSeries.Length > 0;

    private void BuildCharts()
    {
        var line = ChartChoice is { Key.Length: > 0 } c ? int.Parse(c.Key) : (int?)null;

        if (ParkCompareReport.CostChart(_costs, _indexHistory, line, () => ChartPaint.Labels, () => ChartPaint.Separators) is { } cost)
            (Series, XAxes, YAxes, ChartNote) = (cost.Series, cost.XAxes, cost.YAxes, cost.Note);
        else
        {
            Series    = [];
            ChartNote = _costs.Count == 0 ? "" : IndustryText.CompareNoHistory;
        }

        if (ParkCompareReport.TimeChart(_costs, _lineLabels, line, () => ChartPaint.Labels, () => ChartPaint.Separators) is { } time)
            (TimeSeries, TimeXAxes, TimeYAxes) = (time.Series, time.XAxes, time.YAxes);
        else
            TimeSeries = [];

        this.RaisePropertyChanged(nameof(HasChart));
        this.RaisePropertyChanged(nameof(HasTimeChart));
    }
}
