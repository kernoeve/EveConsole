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
using SkiaSharp;

namespace EveConsole.ViewModels;

/// <summary>
/// One line of the comparison's job list: an item or a blueprint, what to do with it, and that
/// operation's numbers. A blueprint offers building what it makes, copying, invention (when it
/// can be invented from) and ME and TE research; an item offers building only.
/// </summary>
public sealed class CompareJobVm : ReactiveObject
{
    private readonly Action _changed;

    public CompareJobVm(int typeId, string typeName, bool isBlueprint, bool inventable, CompareOp op,
                        long quantity, int second, int decryptorTypeId,
                        ObservableCollection<LabelledChoice> decryptors, Action<CompareJobVm> remove,
                        Action<CompareJobVm, int> move, Action changed)
    {
        TypeId      = typeId;
        TypeName    = typeName;
        IsBlueprint = isBlueprint;
        Inventable  = inventable;
        Decryptors  = decryptors;
        _changed    = changed;

        Operations = [new(nameof(CompareOp.Build), IndustryText.CompareOpBuild)];
        if (isBlueprint)
        {
            Operations.Add(new(nameof(CompareOp.Copy), IndustryText.CompareOpCopy));
            if (inventable) Operations.Add(new(nameof(CompareOp.Invention), IndustryText.CompareOpInvention));
            Operations.Add(new(nameof(CompareOp.MeResearch), IndustryText.CompareOpMeResearch));
            Operations.Add(new(nameof(CompareOp.TeResearch), IndustryText.CompareOpTeResearch));
        }
        _operation   = Operations.FirstOrDefault(o => o.Key == op.ToString()) ?? Operations[0];
        _quantity    = Math.Max(1, quantity).ToString();
        _second      = second.ToString();
        _decryptorId = decryptorTypeId;

        RemoveCommand   = ReactiveCommand.Create(() => remove(this));
        MoveUpCommand   = ReactiveCommand.Create(() => move(this, -1));
        MoveDownCommand = ReactiveCommand.Create(() => move(this, +1));
        this.WhenAnyValue(x => x.QuantityText, x => x.SecondText).Skip(1).Subscribe(_ => changed());
    }

    public int    TypeId      { get; }
    public string TypeName    { get; }
    public bool   IsBlueprint { get; }
    public bool   Inventable  { get; }
    public string DisplayName => SdeNames.Type(TypeId, TypeName);

    public List<LabelledChoice> Operations { get; }

    private LabelledChoice _operation;
    /// <summary>What to do. Changing it fills the boxes with that operation's starting numbers.</summary>
    public LabelledChoice Operation
    {
        get => _operation;
        set
        {
            if (value is null || value.Key == _operation.Key) return;
            this.RaiseAndSetIfChanged(ref _operation, value);
            (QuantityText, SecondText) = Op switch
            {
                CompareOp.Copy       => ("10", "1"),
                CompareOp.Invention  => ("10", "0"),
                CompareOp.MeResearch => ("1", "10"),
                CompareOp.TeResearch => ("1", "20"),
                _                    => ("1", "10"),
            };
            foreach (var n in new[] { nameof(Op), nameof(ShowQuantity), nameof(ShowSecond), nameof(ShowDecryptor),
                                      nameof(QuantityLabel), nameof(SecondLabel), nameof(QuantityTip), nameof(SecondTip) })
                this.RaisePropertyChanged(n);
            _changed();
        }
    }

    public CompareOp Op => Enum.TryParse<CompareOp>(Operation.Key, out var op) ? op : CompareOp.Build;

    // The boxes hold what was typed; what they mean depends on the operation, and a word reads
    // as the operation's default.
    private string _quantity, _second;
    public string QuantityText { get => _quantity; set => this.RaiseAndSetIfChanged(ref _quantity, value); }
    public string SecondText   { get => _second;   set => this.RaiseAndSetIfChanged(ref _second, value); }

    public bool ShowQuantity  => Op is CompareOp.Build or CompareOp.Copy or CompareOp.Invention;
    public bool ShowSecond    => Op is not CompareOp.Invention;
    public bool ShowDecryptor => Op is CompareOp.Invention;

    public string QuantityLabel => Op switch
    {
        CompareOp.Copy      => IndustryText.CompareRunsPerCopy,
        CompareOp.Invention => IndustryText.CompareRunsWanted,
        _                   => IndustryText.CompareUnits,
    };
    public string SecondLabel => Op switch
    {
        CompareOp.Copy       => IndustryText.CompareCopies,
        CompareOp.MeResearch => IndustryText.CompareToMe,
        CompareOp.TeResearch => IndustryText.CompareToTe,
        _                    => "ME",
    };
    public string QuantityTip => Op switch
    {
        CompareOp.Copy      => IndustryText.TipCompareRunsPerCopy,
        CompareOp.Invention => IndustryText.TipCompareRunsWanted,
        _                   => IndustryText.TipCompareUnits,
    };
    public string SecondTip => Op switch
    {
        CompareOp.Copy       => IndustryText.TipCompareCopies,
        CompareOp.MeResearch => IndustryText.TipCompareToMe,
        CompareOp.TeResearch => IndustryText.TipCompareToTe,
        _                    => IndustryText.TipCompareMe,
    };

    /// <summary>"No decryptor", then every decryptor; shared by every line.</summary>
    public ObservableCollection<LabelledChoice> Decryptors { get; }

    private int _decryptorId;
    public LabelledChoice? Decryptor
    {
        get => Decryptors.FirstOrDefault(d => d.Key == _decryptorId.ToString()) ?? Decryptors.FirstOrDefault();
        set
        {
            if (value is null) return;   // the list refilling, not a choice
            var id = int.TryParse(value.Key, out var v) ? v : 0;
            if (id == _decryptorId) return;
            _decryptorId = id;
            this.RaisePropertyChanged();
            _changed();
        }
    }

    /// <summary>Called when the decryptor list arrives, so a saved choice shows.</summary>
    public void DecryptorsLoaded() => this.RaisePropertyChanged(nameof(Decryptor));

    private static long Number(string text, long fallback) =>
        long.TryParse(text.Replace(",", "").Trim().TrimEnd('%'), out var v) && v > 0 ? v : fallback;

    public long Quantity => Number(QuantityText, Op is CompareOp.Copy or CompareOp.Invention ? 10 : 1);

    /// <summary>The second box: ME for a build, copies, the ME level, or the TE percent.</summary>
    public int Second => Op switch
    {
        CompareOp.Build      => int.TryParse(SecondText.Trim(), out var me) ? Math.Clamp(me, 0, 10) : 10,
        CompareOp.Copy       => (int)Math.Clamp(Number(SecondText, 1), 1, 1000),
        CompareOp.MeResearch => (int)Math.Clamp(Number(SecondText, 10), 1, 10),
        CompareOp.TeResearch => (int)Math.Clamp(Number(SecondText, 20), 2, 20),
        _                    => 0,
    };
    public int DecryptorTypeId => _decryptorId;

    /// <summary>The line as the service takes it. TE research is asked in percent, two a level.</summary>
    public ParkCompareJob ToJob(int line) => new(TypeId, TypeName, Quantity,
        Op == CompareOp.Build ? Second : 10, Op,
        Copies:          Op == CompareOp.Copy ? Second : 1,
        TargetLevel:     Op switch { CompareOp.MeResearch => Second, CompareOp.TeResearch => Second / 2, _ => 10 },
        DecryptorTypeId: Op == CompareOp.Invention ? _decryptorId : 0,
        Line:            line);

    /// <summary>The line as the chart choice names it: the item, and the operation unless a build.</summary>
    public string LineLabel => Op == CompareOp.Build ? DisplayName : $"{DisplayName} — {Operation.Label}";

    public ReactiveCommand<Unit, Unit> RemoveCommand   { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand   { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
}

/// <summary>One line in one park, as the results grid shows it.</summary>
public sealed class ParkCompareRowVm
{
    public required ParkCost Cost { get; init; }
    public required string OperationLabel { get; init; }
    /// <summary>The cheapest park for this line.</summary>
    public bool IsBest { get; init; }
    /// <summary>How much dearer than the cheapest, as a fraction; 0 for the cheapest.</summary>
    public double OverBest { get; init; }
    /// <summary>The quickest park for this line.</summary>
    public bool IsFastest { get; init; }
    /// <summary>How much slower than the quickest, as a fraction; 0 for the quickest.</summary>
    public double SlowerBy { get; init; }

    public string ItemName   => SdeNames.Type(Cost.Job.TypeId, Cost.Job.TypeName);
    public string ParkName   => Cost.ParkName;
    public long   Units      => Cost.Produced;
    public decimal Total     => Cost.Total;
    public decimal PerUnit   => Cost.PerUnit;
    public double Seconds    => Cost.Seconds ?? double.MaxValue;
    public string Detail     => Cost.Detail;
    public string UnitsText     => Cost.Buildable ? Units.ToString("N0") : "–";
    public string MaterialsText => Cost.Buildable ? MarketFmt.Isk((double)Cost.Materials) : "–";
    public string JobCostText   => Cost.Buildable ? MarketFmt.Isk((double)Cost.JobCost) : "–";
    public string LeftoversText => !Cost.Buildable ? "–" : Cost.Leftovers > 0 ? "−" + MarketFmt.Isk((double)Cost.Leftovers) : "0";
    public string TotalText     => Cost.Buildable ? MarketFmt.Isk((double)Cost.Total) : IndustryText.CompareNotBuilt;
    public string PerUnitText   => Cost.Buildable ? MarketFmt.Isk((double)Cost.PerUnit) : "–";
    public string OverBestText  => !Cost.Buildable ? "" : IsBest ? IndustryText.CompareBest : Over(OverBest);
    public string TimeText      => Cost.Buildable && Cost.Seconds is { } s ? PlanTime.Format(s) : "–";
    public string SlowerText    => !Cost.Buildable || Cost.Seconds is null ? "" : IsFastest ? IndustryText.CompareBest : Over(SlowerBy);

    /// <summary>A gap under a tenth of a percent — invention, where materials swamp the job cost —
    /// gets two decimals, so it does not read as a tie.</summary>
    internal static string Over(double fraction) => fraction < 0.001 ? $"+{fraction:P2}" : $"+{fraction:P1}";
}

/// <summary>One job in one park, as a summary cell: cost and time, each against the best park
/// for that job. Empty where the job is not done there.</summary>
public sealed class CompareSummaryCellVm(ParkCompareRowVm? row)
{
    /// <summary>A quarter dearer or slower than the best shows in red, as Item Valuation does.</summary>
    private const double FarBehind = 0.25;

    private readonly ParkCompareRowVm? _row = row is { Cost.Buildable: true } ? row : null;

    public bool    Has      => _row is not null;
    public decimal Total    => _row?.Total ?? decimal.MaxValue;
    public double  OverBest => _row?.OverBest ?? double.MaxValue;
    public double  Seconds  => _row?.Seconds ?? double.MaxValue;
    public double  SlowerBy => _row is { Cost.Seconds: not null } r ? r.SlowerBy : double.MaxValue;

    public string CostText     => _row?.TotalText ?? "–";
    public string OverBestText => _row?.OverBestText ?? "";
    public string TimeText     => _row?.TimeText ?? "–";
    public string SlowerText   => _row?.SlowerText ?? "";
    /// <summary>The cost cell's tip; none where there is nothing to say, so no empty tip shows.</summary>
    public string? Detail      => string.IsNullOrEmpty(_row?.Detail) ? null : _row.Detail;

    public IBrush FigureColor => IskText.FigureColour(Has);
    public IBrush OverColor   => Standing(_row?.IsBest ?? false, OverBest);
    public IBrush SlowerColor => Standing(_row?.IsFastest ?? false, SlowerBy);

    private IBrush Standing(bool isBest, double behind) =>
        !Has ? Palette.TextFaint : isBest ? Palette.Good : behind >= FarBehind ? Palette.Bad : Palette.TextPrimary;
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
/// cost indices.
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

        Decryptors.Add(new LabelledChoice("0", IndustryText.CompareNoDecryptor));

        CalculateCommand = ReactiveCommand.CreateFromTask(CalculateAsync);
        CalculateCommand.ThrownExceptions.Subscribe(ex => StatusText = string.Format(CommonText.ErrorWithMessage, ex.Message));

        LoadJobs();
        ChartPaint.TrackAxesOf(this);

        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t => _ = SearchAsync(t ?? ""));

        this.WhenAnyValue(x => x.ChartChoice).Skip(1).Subscribe(_ => BuildCharts());
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

        if (Decryptors.Count == 1)
        {
            await SdeNames.EnsureLoadedAsync();
            var all = await Task.Run(() => _service.DecryptorsAsync());
            foreach (var d in all.Select(d => (d, Name: SdeNames.Type(d.TypeId, d.Name))).OrderBy(x => x.Name, StringComparer.CurrentCulture))
                Decryptors.Add(new LabelledChoice(d.d.TypeId.ToString(), string.Format(IndustryText.CompareDecryptorChoice,
                    d.Name, d.d.ChanceMultiplier, d.d.RunModifier, d.d.MeModifier, d.d.TeModifier)));
            foreach (var j in Jobs) j.DecryptorsLoaded();
        }
    }

    /// <summary>"No decryptor", then every decryptor with what it does.</summary>
    public ObservableCollection<LabelledChoice> Decryptors { get; } = [];

    // ── The job list ─────────────────────────────────────────────────────────

    public ObservableCollection<CompareJobVm> Jobs { get; } = [];

    // T type, N name, Q the first box, Me the second box (named for what it held first), Op the
    // operation, D the decryptor, B a blueprint, I inventable from.
    private sealed record SavedJob(int T, string N, long Q, int Me, string? Op = null, int D = 0, bool B = false, bool I = false);

    private void LoadJobs()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<List<SavedJob>>(UiState.Get(UiState.IndyCompareJobs) ?? "[]") ?? [];
            foreach (var s in saved)
                Jobs.Add(NewJob(s.T, s.N, s.B, s.I, Enum.TryParse<CompareOp>(s.Op, out var op) ? op : CompareOp.Build, s.Q, s.Me, s.D));
        }
        catch (JsonException) { /* a list that cannot be read starts empty */ }
    }

    private void SaveJobs() => UiState.Set(UiState.IndyCompareJobs,
        JsonSerializer.Serialize(Jobs.Select(j => new SavedJob(j.TypeId, j.TypeName, j.Quantity, j.Second, j.Op.ToString(),
                                                               j.DecryptorTypeId, j.IsBlueprint, j.Inventable))));

    private CompareJobVm NewJob(int typeId, string name, bool isBlueprint, bool inventable, CompareOp op, long quantity, int second, int decryptor) =>
        new(typeId, name, isBlueprint, inventable, op, quantity, second, decryptor, Decryptors,
            j => { Jobs.Remove(j); SaveJobs(); }, Move, SaveJobs);

    /// <summary>A line one place up (-1) or down (+1) the list; at either end it stays. The
    /// results keep the order they were calculated in until Calculate runs again.</summary>
    private void Move(CompareJobVm job, int by)
    {
        var from = Jobs.IndexOf(job);
        var to   = from + by;
        if (from < 0 || to < 0 || to >= Jobs.Count) return;
        Jobs.Move(from, to);
        SaveJobs();
    }

    /// <summary>A search result picked: added to the list, after the click that picked it.</summary>
    public ProdTypeSearchResult? PickedResult
    {
        get => null;
        set { if (value is { } pick) Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = AddAsync(pick)); }
    }

    /// <summary>
    /// Adds a line. A blueprint starts on copying, an item on building. The same blueprint may
    /// be listed more than once, each line its own operation.
    /// </summary>
    private async Task AddAsync(ProdTypeSearchResult pick)
    {
        SearchText = "";
        var bps  = await Task.Run(() => _service.BlueprintsAsync([pick.TypeId]));
        var isBp = bps.TryGetValue(pick.TypeId, out var bp);
        if (!isBp && Jobs.Any(j => j.TypeId == pick.TypeId)) return;
        Jobs.Add(NewJob(pick.TypeId, pick.TypeName, isBp, bp.Inventable,
                        isBp ? CompareOp.Copy : CompareOp.Build, isBp ? 10 : 1, isBp ? 1 : 10, 0));
        SaveJobs();
    }

    // ── Search: what can be built, and blueprints ────────────────────────────

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
            // The products of a manufacturing or reaction blueprint, as the Production Calculator
            // offers them, and the blueprints themselves, for copying, invention and research.
            return await db.SdeTypes.AsNoTracking()
                .Where(t => t.Published
                         && (EF.Functions.Like(t.Name, $"%{text}%") || shownIds.Contains(t.TypeId))
                         && (db.SdeBlueprintProducts.Any(p => p.ProductTypeId == t.TypeId
                                && (p.Activity == "manufacturing" || p.Activity == "reaction"))
                             || db.SdeBlueprints.Any(b => b.TypeId == t.TypeId)))
                .OrderBy(t => t.Name).Take(60)
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

    /// <summary>A line in the status, for what the view does itself: a picture that would not copy.</summary>
    public void ShowStatus(string text) => StatusText = text;

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

        var lines = Jobs.ToList();
        var jobs  = lines.Select((j, i) => j.ToJob(i)).ToList();
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
            foreach (var line in _costs.GroupBy(c => c.Job.Line))
            {
                var done    = line.Where(c => c.Buildable).ToList();
                var best    = done.Select(c => c.Total).DefaultIfEmpty(0).Min();
                var fastest = done.Where(c => c.Seconds is not null).Select(c => c.Seconds!.Value).DefaultIfEmpty(0).Min();
                var opLabel = lines[line.Key].Operation.Label;
                foreach (var c in line.OrderBy(c => !c.Buildable).ThenBy(c => c.Total))
                    Results.Add(new ParkCompareRowVm
                    {
                        Cost           = c,
                        OperationLabel = opLabel,
                        IsBest         = c.Buildable && c.Total == best,
                        OverBest       = c.Buildable && best > 0 ? (double)((c.Total - best) / best) : 0,
                        IsFastest      = c.Buildable && c.Seconds == fastest,
                        SlowerBy       = c.Buildable && c.Seconds is { } s && fastest > 0 ? (s - fastest) / fastest : 0,
                    });
            }

            _lineLabels = lines.Select(l => l.LineLabel).ToList();
            BuildSummary();

            ChartChoices.Clear();
            ChartChoices.Add(new LabelledChoice("", IndustryText.CompareAllItems));
            for (var i = 0; i < lines.Count; i++) ChartChoices.Add(new LabelledChoice(i.ToString(), lines[i].LineLabel));
            ChartChoice = ChartChoices[0];
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

    private List<string> _lineLabels = [];

    private void BuildSummary()
    {
        var parks = _costs.Select(c => (c.ParkId, c.ParkName, c.Slot)).Distinct().OrderBy(p => p.Slot).ToList();
        var lines = Enumerable.Range(0, _lineLabels.Count).ToList();
        var byKey = Results.ToDictionary(r => (r.Cost.Job.Line, r.Cost.ParkId));
        CompareSummaryCellVm Cell(int line, int parkId) => new(byKey.GetValueOrDefault((line, parkId)));

        SummaryRows.Clear();
        SummaryGroups = SummaryByPark ? lines.Select(l => _lineLabels[l]).ToList() : parks.Select(p => p.ParkName).ToList();
        this.RaisePropertyChanged(nameof(SummaryGroups));
        this.RaisePropertyChanged(nameof(SummaryByPark));

        if (SummaryByPark)
            foreach (var p in parks) SummaryRows.Add(new(p.ParkName, lines.Select(l => Cell(l, p.ParkId)).ToList()));
        else
            foreach (var l in lines) SummaryRows.Add(new(_lineLabels[l], parks.Select(p => Cell(l, p.ParkId)).ToList()));
        this.RaisePropertyChanged(nameof(HasSummary));
    }

    // ── The charts: cost over time, and time per park ────────────────────────

    /// <summary>All lines together, or one of them; both charts follow it.</summary>
    public ObservableCollection<LabelledChoice> ChartChoices { get; } = [];

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
        var lineFilter = ChartChoice is { Key.Length: > 0 } c ? int.Parse(c.Key) : (int?)null;
        var byPark = _costs.Where(x => x.Buildable && (lineFilter is null || x.Job.Line == lineFilter))
                           .GroupBy(x => (x.ParkId, x.ParkName, x.Slot))
                           .OrderBy(g => g.Key.Slot)
                           .ToList();
        var drawn = byPark.Where(g => g.Key.Slot < ChartPaint.MaxSeries).ToList();
        BuildCostChart(byPark, drawn);
        BuildTimeChart(drawn);
        this.RaisePropertyChanged(nameof(HasChart));
        this.RaisePropertyChanged(nameof(HasTimeChart));
    }

    /// <summary>
    /// One line per park: the chosen line, or all of them, costed with each day's indices. Days
    /// are those the history has for any of the systems involved; a day a system's index is
    /// missing carries its last known value forward.
    /// </summary>
    private void BuildCostChart(
        List<IGrouping<(int ParkId, string ParkName, int Slot), ParkCost>> byPark,
        List<IGrouping<(int ParkId, string ParkName, int Slot), ParkCost>> drawn)
    {
        var keys = byPark.SelectMany(g => g).SelectMany(x => x.Terms).Select(t => (t.SystemId, t.Activity)).Distinct().ToList();
        var days = keys.SelectMany(k => _indexHistory.TryGetValue(k, out var s) ? s.Keys : Enumerable.Empty<DateOnly>())
                       .Distinct().OrderBy(d => d).ToList();

        if (byPark.Count == 0 || days.Count == 0)
        {
            Series    = [];
            ChartNote = _costs.Count == 0 ? "" : IndustryText.CompareNoHistory;
            return;
        }

        var lines = drawn.Select(g => (g.Key, Values: days.Select(d => (double?)(double)g.Sum(x => ParkComparisonService.TotalAt(x,
                              (sys, act) => CostIndexHistoryService.On(_indexHistory.GetValueOrDefault((sys, act)), d)))).ToArray())).ToList();
        // Where the index moves the total by under 2% (invention: materials swamp the job cost),
        // abbreviated ISK would label every gridline the same; whole ISK tells them apart.
        var all  = lines.SelectMany(l => l.Values).OfType<double>().DefaultIfEmpty(0).ToList();
        var flat = all.Max() > 0 && (all.Max() - all.Min()) < 0.02 * all.Max();

        Series = lines.Select(l => (ISeries)new LineSeries<double?>
        {
            Name           = l.Key.ParkName,
            Values         = l.Values,
            GeometrySize   = 0,
            LineSmoothness = 0,
            Stroke         = ChartPaint.Series(l.Key.Slot),
            Fill           = null,
        }).ToArray();

        XAxes =
        [
            new Axis
            {
                Labels          = days.Select(d => d.ToString("MM-dd")).ToArray(),
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinStep         = Math.Max(1, days.Count / 6),
                SeparatorsPaint = null,
            },
        ];
        YAxes =
        [
            new Axis
            {
                Labeler         = v => flat ? v.ToString("N0") : MarketFmt.Isk(v),
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                SeparatorsPaint = ChartPaint.Separators,
            },
        ];

        var left = byPark.Count - drawn.Count;
        ChartNote = string.Format(IndustryText.CompareChartNote, days[0], days[^1])
                  + (left > 0 ? "  " + string.Format(IndustryText.CompareChartTooMany, ChartPaint.MaxSeries) : "");
    }

    /// <summary>
    /// A group of bars per line — the chosen one, or every line in the list's order — named for
    /// the job, with a bar per park in the park's colour on the cost chart. A park where a job is
    /// not done, or its time is unknown, has no bar in that group.
    /// </summary>
    private void BuildTimeChart(List<IGrouping<(int ParkId, string ParkName, int Slot), ParkCost>> drawn)
    {
        var lineFilter = ChartChoice is { Key.Length: > 0 } c ? int.Parse(c.Key) : (int?)null;
        var lines = lineFilter is { } only ? [only] : Enumerable.Range(0, _lineLabels.Count).ToList();
        var timed = drawn.Where(g => g.Any(x => x.Seconds is not null)).ToList();
        if (timed.Count == 0 || lines.Count == 0) { TimeSeries = []; return; }

        TimeSeries = timed.Select(g => (ISeries)new ColumnSeries<double?>
        {
            Name        = g.Key.ParkName,
            Values      = lines.Select(l => g.FirstOrDefault(x => x.Job.Line == l)?.Seconds is { } sec ? sec / 3600.0 : (double?)null).ToArray(),
            YToolTipLabelFormatter = p => $"{g.Key.ParkName}: {PlanTime.Format(p.Coordinate.PrimaryValue * 3600)}",
            Fill        = new SolidColorPaint(SKColor.Parse(ChartPaint.SeriesHex[g.Key.Slot])),
            Stroke      = null,
            MaxBarWidth = 48,
            Padding     = 4,
            Rx          = 3,
            Ry          = 3,
        }).ToArray();

        TimeXAxes =
        [
            new Axis
            {
                Labels          = lines.Select(l => _lineLabels[l]).ToArray(),
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                // Job names side by side collide past a few; tilted, each keeps its own room.
                LabelsRotation  = lines.Count > 3 ? 20 : 0,
                MinStep         = 1,
                SeparatorsPaint = null,
            },
        ];
        TimeYAxes =
        [
            new Axis
            {
                Labeler         = v => v <= 0 ? "0" : PlanTime.Format(v * 3600),
                LabelsPaint     = ChartPaint.Labels,
                TextSize        = 10,
                MinLimit        = 0,
                SeparatorsPaint = ChartPaint.Separators,
            },
        ];
    }
}
