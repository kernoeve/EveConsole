using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

public class ProdTypeSearchResult : ReactiveObject
{
    public int    TypeId   { get; set; }
    public string TypeName { get; set; } = "";

    private bool _isHighlighted;
    /// <summary>The one the arrow keys are on: Enter picks it.</summary>
    public bool IsHighlighted { get => _isHighlighted; set => this.RaiseAndSetIfChanged(ref _isHighlighted, value); }

    /// <summary>The name the list shows, in the interface language; TypeName stays English.</summary>
    public string DisplayName => SdeNames.Type(TypeId, TypeName);
    public override string ToString() => DisplayName;
}

public class ProductionQueueVm : ReactiveObject
{
    public int    TypeId   { get; set; }
    public string TypeName { get; set; } = "";
    public string DisplayName => SdeNames.Type(TypeId, TypeName);

    // Edited in place in the queue. A cleared box keeps the last value: a row of nothing to build
    // is what the remove button is for.
    private int _quantity = 1;
    public int Quantity { get => _quantity; set => this.RaiseAndSetIfChanged(ref _quantity, Math.Max(1, value)); }

    private int _meLevel = 10;
    public int MeLevel { get => _meLevel; set => this.RaiseAndSetIfChanged(ref _meLevel, Math.Clamp(value, 0, 10)); }

    // Set by ProductionCalculatorViewModel.AddToQueue so compiled DataTemplates
    // can bind directly without traversing to the parent DataContext.
    public ReactiveCommand<Unit, Unit>? RemoveCommand    { get; set; }
    public ReactiveCommand<int,  Unit>? NavigateCommand  { get; set; }
}

public class ParkOption
{
    public int    Id   { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public class JobTreeNode : ReactiveObject
{
    public PlanJob           Job             { get; set; } = null!;
    public List<JobTreeNode> Children        { get; set; } = [];
    public ReactiveCommand<int, Unit>? NavigateCommand { get; set; }

    /// <summary>
    /// The job's facility as the Jobs tab shows it. A park entry linked to an NPC station is named
    /// after the station, in ESI's English, and reads here in the interface language; any other name
    /// is the park's own. ⚠️ Job.StructureName stays English: the Worklist reads the plan.
    /// </summary>
    public string StructureDisplayName =>
        Job.StationId is { } id && Job.StructureName == Job.StationName
            ? SdeNames.Location(id, Job.StructureName)
            : Job.StructureName;

    private bool _showMaterials;
    public bool ShowMaterials
    {
        get => _showMaterials;
        set => this.RaiseAndSetIfChanged(ref _showMaterials, value);
    }
}

/// <summary>
/// One facility in the Jobs tab's station view: every job the plan runs there, and what those
/// jobs need of it between them.
///
/// <para>Keyed by the real facility a park structure is linked to, since that is where the stock
/// is. Park structures with no facility linked group by their own name and system, and say their
/// stock cannot be counted rather than reporting every material missing.</para>
/// </summary>
public class JobStationGroup : ReactiveObject
{
    /// <summary>A job here, for the facility and system links; every job in the group shares them.</summary>
    public PlanJob Lead { get; init; } = null!;

    public string DisplayName { get; init; } = "";

    /// <summary>Earliest first: a job whose inputs another job here makes comes after that job.</summary>
    public List<JobTreeNode>          Children  { get; init; } = [];
    public List<StationMaterialLine>  Materials { get; init; } = [];

    /// <summary>A real facility is linked, so its stock was counted.</summary>
    public bool StockKnown { get; init; }

    public double JobSeconds   { get; init; }
    public decimal MaterialCost => Materials.Sum(m => m.TotalCost);

    public string JobCountText      => Plurals.Format(IndustryText.ResourceManager, nameof(IndustryText.PlanStationJobsOther), Children.Count);
    public string MaterialCountText => Plurals.Format(IndustryText.ResourceManager, nameof(IndustryText.PlanStationMaterialsOther), Materials.Count);
    public bool   HasSlotTime       => JobSeconds > 0;
    public string SlotTimeText      => HasSlotTime ? string.Format(IndustryText.FmtSlotTime, PlanTime.Format(JobSeconds)) : "";

    public int  ShortCount => Materials.Count(m => m.Missing > 0);
    public bool HasShort   => StockKnown && ShortCount > 0;
    public bool AllOnHand  => StockKnown && ShortCount == 0;
    public string ShortText => Plurals.Format(IndustryText.ResourceManager, nameof(IndustryText.PlanStationShortOther), ShortCount);

    public bool HasStationLink   => Lead.HasStationLink;
    public bool ShowStationPlain => !HasStationLink;
    public bool HasSystemLink    => Lead.HasSystemLink;
    public bool ShowSystemPlain  => Lead.ShowSystemPlain;
    public bool ShowAtSeparator  => Lead.SystemName.Length > 0;

    private bool _showMaterials;
    /// <summary>Closed until asked for, as on a job: the summary line says whether anything is short.</summary>
    public bool ShowMaterials
    {
        get => _showMaterials;
        set => this.RaiseAndSetIfChanged(ref _showMaterials, value);
    }
}

/// <summary>A material the jobs at one facility need, summed over them.</summary>
public class StationMaterialLine
{
    public int     MaterialTypeId { get; init; }
    public string  TypeName       { get; init; } = "";
    public string  DisplayName    { get; init; } = "";
    public bool    IsBought       { get; init; }

    /// <summary>Another job at this facility makes it, so it is not hauled in and never short.</summary>
    public bool    BuiltHere      { get; init; }

    public long    Needed         { get; init; }
    public decimal TotalCost      { get; init; }

    public bool    AvailabilityKnown { get; init; }

    /// <summary>Everything of it at the facility, needed or not.</summary>
    public long    Available      { get; init; }

    /// <summary>How much of what is needed is there: the stock, up to the need. 18 million of a
    /// material that 63 thousand is wanted of says nothing about these jobs.</summary>
    public long    OnHand  => Math.Min(Needed, Available);

    public long    Missing => AvailabilityKnown && !BuiltHere ? Needed - OnHand : 0;

    public string  AvailableDisplay => AvailabilityKnown ? OnHand.ToString("N0") : "—";
    public string  MissingDisplay   => AvailabilityKnown ? Missing.ToString("N0")   : "—";
    public string  MissingColor     => Missing > 0 ? "#e0902e" : "#555566";

    public string  Source => IsBought ? IndustryText.SourceBuy
                           : BuiltHere ? IndustryText.SourceBuiltHere
                           : IndustryText.SourceBuiltElsewhere;

    public void OpenItem() => EveConsole.Services.EntityNavigator.Instance.Item(MaterialTypeId);
}

public class ProductionCalculatorViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ProductionCalculatorService     _service;

    // ── Search ────────────────────────────────────────────────────────────
    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public ObservableCollection<ProdTypeSearchResult> SearchResults { get; } = [];

    private bool _showResults;
    public bool ShowResults
    {
        get => _showResults;
        private set => this.RaiseAndSetIfChanged(ref _showResults, value);
    }

    private ProdTypeSearchResult? _pendingType;
    public ProdTypeSearchResult? PendingType
    {
        get => _pendingType;
        private set
        {
            this.RaiseAndSetIfChanged(ref _pendingType, value);
            this.RaisePropertyChanged(nameof(CanAdd));
        }
    }

    public bool CanAdd => _pendingType is not null;

    /// <summary>The pending item will cost a blueprint copy: queuing it ticks "include BPC cost".</summary>
    private bool _pendingNeedsBpc;

    // ── New item settings ─────────────────────────────────────────────────
    private int _newQuantity = 1;
    public int NewQuantity { get => _newQuantity; set => this.RaiseAndSetIfChanged(ref _newQuantity, value); }

    private int _newMeLevel = 10;
    public int NewMeLevel { get => _newMeLevel; set => this.RaiseAndSetIfChanged(ref _newMeLevel, value); }

    // ── Queue ─────────────────────────────────────────────────────────────
    public ObservableCollection<ProductionQueueVm> Queue { get; } = [];

    /// <summary>Something is queued: the Clear button shows.</summary>
    public bool HasQueue => Queue.Count > 0;

    private ProdTypeSearchResult? _selectedSearchResult;
    public ProdTypeSearchResult? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedSearchResult, value);
            if (value is not null) SelectResult(value);
        }
    }

    // ── Parks ─────────────────────────────────────────────────────────────
    public ObservableCollection<ParkOption> Parks { get; } = [];

    private ParkOption? _selectedPark;
    public ParkOption? SelectedPark
    {
        get => _selectedPark;
        set
        {
            if (ReferenceEquals(value, _selectedPark)) return;
            var changed = value?.Id != _selectedPark?.Id;
            this.RaiseAndSetIfChanged(ref _selectedPark, value);
            if (changed) MarkStale();
        }
    }

    // Include blueprint copies (contract price) as an input: the final product's, and every
    // BPC-only item's at any depth. Ticked when a BPC-only item is queued; unticked, no copy is
    // costed at all — the build without the copies, which the stored build cost never shows.
    private bool _includeBpcCost;
    public bool IncludeBpcCost
    {
        get => _includeBpcCost;
        set
        {
            if (value == _includeBpcCost) return;
            this.RaiseAndSetIfChanged(ref _includeBpcCost, value);
            MarkStale();
        }
    }

    // ── Out of date ───────────────────────────────────────────────────────
    private bool _isStale;
    /// <summary>The queue, park or BPC choice changed since the results were worked out: they
    /// still show, under a note saying so, until the next Calculate.</summary>
    public bool IsStale { get => _isStale; private set => this.RaiseAndSetIfChanged(ref _isStale, value); }

    private void MarkStale()
    {
        if (_plan is not null) IsStale = true;
    }

    // ── Missing-stock mode (Raw Materials tab) ────────────────────────────
    // Station mode asks "what do I still have to haul in", asset mode asks "what do I
    // have to buy at all". The choice sticks between sessions because a given player
    // almost always wants the same one.
    private const string RawMissingModeKey = "prodcalc.raw_missing_by_station";

    private readonly AppPreferencesService? _prefs;

    private bool _rawMissingByStation;
    public bool RawMissingByStation
    {
        get => _rawMissingByStation;
        set
        {
            if (_rawMissingByStation == value) return;
            this.RaiseAndSetIfChanged(ref _rawMissingByStation, value);
            this.RaisePropertyChanged(nameof(MissingModeDescription));
            _ = OnMissingModeChangedAsync();
        }
    }

    public string MissingModeDescription => RawMissingByStation
        ? IndustryText.CalcMissingByStation
        : IndustryText.CalcMissingByAssets;

    // ── Jobs tab: by hierarchy or by station ──────────────────────────────
    private const string JobsByStationKey = "prodcalc.jobs_by_station";

    private bool _jobsByStation;
    /// <summary>The Jobs tab groups its jobs under the facility each runs at, instead of under the
    /// job that needs them. Kept between sessions, like the missing-stock mode.</summary>
    public bool JobsByStation
    {
        get => _jobsByStation;
        set
        {
            if (_jobsByStation == value) return;
            this.RaiseAndSetIfChanged(ref _jobsByStation, value);
            if (_prefs is not null) _ = _prefs.SetBoolAsync(JobsByStationKey, value);
        }
    }

    private ProductionCalculatorService.MissingMode CurrentMissingMode =>
        RawMissingByStation
            ? ProductionCalculatorService.MissingMode.Station
            : ProductionCalculatorService.MissingMode.Assets;

    private async Task OnMissingModeChangedAsync()
    {
        if (_prefs is not null)
            await _prefs.SetBoolAsync(RawMissingModeKey, _rawMissingByStation);

        // Recompute in place. The raw material rows notify, so the grid updates without
        // reassigning Plan — which would rebuild and collapse the Jobs tree.
        if (_plan is not null)
        {
            try { await _service.ApplyAvailabilityAsync(_plan, CurrentMissingMode); }
            catch (Exception ex) { Status = string.Format(CommonText.ErrorWithMessage, ex.Message); }
        }
    }

    // ── Results ───────────────────────────────────────────────────────────
    private ProductionPlan? _plan;
    public ProductionPlan? Plan
    {
        get => _plan;
        private set
        {
            this.RaiseAndSetIfChanged(ref _plan, value);
            _stationGroups = null;
            this.RaisePropertyChanged(nameof(HasResults));
            this.RaisePropertyChanged(nameof(JobTreeRoots));
            this.RaisePropertyChanged(nameof(JobStationGroups));
            this.RaisePropertyChanged(nameof(HasPlanWarnings));
            this.RaisePropertyChanged(nameof(PlanWarningHeader));
            this.RaisePropertyChanged(nameof(PlanWarningLines));
            this.RaisePropertyChanged(nameof(HasPricingWarnings));
            this.RaisePropertyChanged(nameof(PricingWarningHeader));
            this.RaisePropertyChanged(nameof(PricingWarningLines));
            this.RaisePropertyChanged(nameof(HasSkippedBpcs));
            this.RaisePropertyChanged(nameof(SkippedBpcHeader));
            this.RaisePropertyChanged(nameof(SkippedBpcLines));
            this.RaisePropertyChanged(nameof(LongestChainText));
            this.RaisePropertyChanged(nameof(SlotTimeText));
        }
    }

    /// <summary>How soon the plan could be done with a slot for every job; see ApplyDurationsAsync.</summary>
    public string LongestChainText => _plan is { LongestChainSeconds: > 0 } p ? PlanTime.Format(p.LongestChainSeconds) : "—";

    /// <summary>Every job's time added up, under the longest chain.</summary>
    public string SlotTimeText => _plan is { TotalJobSeconds: > 0 } p
        ? string.Format(IndustryText.FmtSlotTime, PlanTime.Format(p.TotalJobSeconds))
        : "";

    public bool HasSkippedBpcs => _plan?.SkippedBpcs.Count > 0;

    public string SkippedBpcHeader => _plan is null || _plan.SkippedBpcs.Count == 0
        ? ""
        : string.Format(IndustryText.CalcSkippedBpcs, _plan.SkippedBpcs.Count);

    public List<string> SkippedBpcLines => ShownLines(_plan?.SkippedBpcs);

    public bool HasPlanWarnings => _plan?.Warnings.Count > 0;

    public string PlanWarningHeader => _plan is null || _plan.Warnings.Count == 0
        ? ""
        : string.Format(IndustryText.CalcWarnNoAssignment, _plan.Warnings.Count);

    /// <summary>The warnings as the Summary tab lists them: item names in the interface language,
    /// in the order they then read.</summary>
    public List<string> PlanWarningLines => ShownLines(_plan?.Warnings);

    public bool HasPricingWarnings => _plan?.PricingWarnings.Count > 0;

    public string PricingWarningHeader => _plan is null || _plan.PricingWarnings.Count == 0
        ? ""
        : string.Format(IndustryText.CalcWarnBlueprintPrices, _plan.PricingWarnings.Count);

    public List<string> PricingWarningLines => ShownLines(_plan?.PricingWarnings);

    private static List<string> ShownLines(List<PlanItemNote>? notes) =>
        notes is null ? [] : [.. notes.Select(n => n.DisplayText).Order(StringComparer.CurrentCulture)];

    public bool HasResults => _plan is not null;

    public List<JobTreeNode> JobTreeRoots
    {
        get
        {
            if (_plan is null) return [];
            var jobIndex = _plan.AllJobs.ToDictionary(j => j.OutputTypeId);

            // Jobs needed by more than one parent (fuel blocks, reactions, etc.) are shown
            // once at root level rather than duplicated under every parent that needs them.
            var sharedIds = _plan.AllJobs
                .Where(j => !j.IsFinalProduct && j.ParentTypeIds.Count > 1)
                .Select(j => j.OutputTypeId)
                .ToHashSet();

            JobTreeNode BuildNode(int typeId)
            {
                var job  = jobIndex[typeId];
                var node = new JobTreeNode { Job = job, NavigateCommand = OpenInItemBrowserCommand };
                foreach (var childId in job.ChildTypeIds)
                    if (jobIndex.ContainsKey(childId) && !sharedIds.Contains(childId))
                        node.Children.Add(BuildNode(childId));
                return node;
            }

            var roots = _plan.RootTypeIds
                .Where(id => jobIndex.ContainsKey(id))
                .Select(BuildNode)
                .ToList();

            // Append shared multi-parent jobs at root level, sorted by the name shown.
            //
            // Through BuildNode, not as a bare node: a shared job has its own sub-jobs, and
            // creating the node directly left them rendered nowhere at all. Their single
            // parent was this shared job, so hoisting it out of the tree took them with it —
            // a reaction like Pressurized Oxidizers appeared with its materials marked
            // "Build" and no job anywhere that built them. They were always in the plan and
            // in the cost totals; only the tree dropped them.
            foreach (var id in sharedIds.OrderBy(id => jobIndex[id].OutputDisplayName, StringComparer.CurrentCulture))
                roots.Add(BuildNode(id));

            // Safety net. The tree is walked over ChildTypeIds from RootTypeIds, so a job
            // reachable by neither would vanish silently — which is exactly how the bug
            // above went unnoticed. Anything left over is shown rather than lost.
            var rendered = new HashSet<int>();
            void Collect(JobTreeNode n)
            {
                rendered.Add(n.Job.OutputTypeId);
                foreach (var c in n.Children) Collect(c);
            }
            foreach (var r in roots) Collect(r);

            foreach (var job in _plan.AllJobs
                         .Where(j => !rendered.Contains(j.OutputTypeId))
                         .OrderBy(j => j.OutputDisplayName, StringComparer.CurrentCulture))
                roots.Add(new JobTreeNode { Job = job, NavigateCommand = OpenInItemBrowserCommand });

            return roots;
        }
    }

    private List<JobStationGroup>? _stationGroups;

    /// <summary>The Jobs tab's station view: one group per facility, its jobs beneath it. Built
    /// once per plan, so the rows on screen are the rows anything else reads.</summary>
    public List<JobStationGroup> JobStationGroups =>
        _plan is null ? [] : _stationGroups ??= GroupByStation(_plan, OpenInItemBrowserCommand);

    /// <summary>
    /// The plan's jobs under the facility each runs at, with what each facility's jobs need of it.
    ///
    /// <para>⚠️ A material is counted against the facility's stock once, for all its jobs together:
    /// ten jobs in one Raitaru draw on one pile. A job row says whether that one job could start;
    /// this says what the facility is short of for all of them. A material another job here makes
    /// is never short — it is made on site — and one made at another facility is, until it has
    /// been hauled in.</para>
    /// </summary>
    public static List<JobStationGroup> GroupByStation(ProductionPlan plan, ReactiveCommand<int, Unit>? navigate = null)
    {
        var jobIndex = plan.AllJobs.ToDictionary(j => j.OutputTypeId);

        // How many jobs must finish before this one can start: zero for a job none of whose inputs
        // the plan builds. The jobs at a facility are listed in this order, earliest first.
        var depth = new Dictionary<int, int>();
        int Depth(int typeId, int guard = 0)
        {
            if (depth.TryGetValue(typeId, out var d)) return d;
            var job = jobIndex[typeId];
            d = guard > 64 ? 0 : job.ChildTypeIds.Where(jobIndex.ContainsKey)
                                                .Select(c => Depth(c, guard + 1) + 1)
                                                .DefaultIfEmpty(0).Max();
            return depth[typeId] = d;
        }

        var groups = new List<JobStationGroup>();
        foreach (var g in plan.AllJobs.GroupBy(j => j.StationId is { } id
                     ? $"facility:{id}"
                     : $"park:{j.StructureName}|{j.SystemName}"))
        {
            var jobs = g.OrderBy(j => Depth(j.OutputTypeId))
                        .ThenBy(j => j.OutputDisplayName, StringComparer.CurrentCulture)
                        .ToList();
            var lead  = jobs[0];
            var known = lead.StationId.HasValue;
            var made  = jobs.Select(j => j.OutputTypeId).ToHashSet();

            // A copy's line is its own line: the same blueprint as a copy and as nothing else.
            var lines = jobs.SelectMany(j => j.Materials)
                .GroupBy(m => (m.MaterialTypeId, m.BlueprintName ?? ""))
                .Select(m =>
                {
                    var first = m.First();
                    return new StationMaterialLine
                    {
                        MaterialTypeId    = first.MaterialTypeId,
                        TypeName          = first.TypeName,
                        DisplayName       = first.DisplayName,
                        IsBought          = first.IsBought,
                        BuiltHere         = !first.IsBought && made.Contains(first.MaterialTypeId),
                        Needed            = m.Sum(x => x.TotalQty),
                        TotalCost         = m.Sum(x => x.TotalCost),
                        AvailabilityKnown = known,
                        // The facility's stock, which every job here was compared against whole.
                        Available         = known ? first.Available : 0,
                    };
                })
                .OrderByDescending(l => l.Missing > 0)
                .ThenBy(l => l.DisplayName, StringComparer.CurrentCulture)
                .ToList();

            groups.Add(new JobStationGroup
            {
                Lead        = lead,
                DisplayName = StationDisplayName(lead),
                StockKnown  = known,
                JobSeconds  = jobs.Sum(j => j.DurationSeconds ?? 0),
                Materials   = lines,
                Children    = [.. jobs.Select(j => new JobTreeNode { Job = j, NavigateCommand = navigate })],
            });
        }

        // Facilities by name; park structures with nothing linked after them, since their stock is
        // the part of the answer missing.
        return [.. groups.OrderBy(g => !g.StockKnown).ThenBy(g => g.DisplayName, StringComparer.CurrentCulture)];
    }

    /// <summary>The facility's own name, in the interface language for an NPC station; the park
    /// structure's name where none is linked.</summary>
    private static string StationDisplayName(PlanJob job) =>
        job.StationId is { } id && job.StationName.Length > 0 ? SdeNames.Location(id, job.StationName)
        : job.StructureName.Length > 0 ? job.StructureName
        : IndustryText.PlanStationNone;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    // ── Navigation callback (set by MainWindowViewModel) ──────────────────
    public Action<int>? NavigateToItemAction { get; set; }

    // ── Commands ──────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit>              AddToQueueCommand          { get; }
    public ReactiveCommand<ProductionQueueVm, Unit> RemoveFromQueueCommand     { get; }
    public ReactiveCommand<Unit, Unit>              ClearQueueCommand          { get; }
    public ReactiveCommand<ProdTypeSearchResult, Unit> SelectResultCommand     { get; }
    public ReactiveCommand<Unit, Unit>              CalculateCommand           { get; }
    public ReactiveCommand<int,  Unit>              OpenInItemBrowserCommand   { get; }

    // Tracks when SearchText was set programmatically to suppress the debounced re-search.
    private int _suppressSearchCount;

    public ProductionCalculatorViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        ProductionCalculatorService     service,
        AppPreferencesService?          prefs = null)
    {
        _dbFactory = dbFactory;
        _service   = service;
        _prefs     = prefs;
        _rawMissingByStation = prefs?.GetBool(RawMissingModeKey, true) ?? true;
        _jobsByStation       = prefs?.GetBool(JobsByStationKey, false) ?? false;

        var canAdd  = this.WhenAnyValue(x => x.CanAdd);
        var canCalc = this.WhenAnyValue(x => x.IsBusy, x => x.Queue.Count, (b, c) => !b && c > 0);

        AddToQueueCommand        = ReactiveCommand.Create(AddToQueue, canAdd);
        RemoveFromQueueCommand   = ReactiveCommand.Create<ProductionQueueVm>(Remove);
        ClearQueueCommand        = ReactiveCommand.Create(ClearQueue);
        CalculateCommand         = ReactiveCommand.CreateFromTask(CalculateAsync, canCalc);
        SelectResultCommand      = ReactiveCommand.Create<ProdTypeSearchResult>(SelectResult);
        OpenInItemBrowserCommand = ReactiveCommand.Create<int>(typeId => NavigateToItemAction?.Invoke(typeId));

        // Search debounce — skip if the text change was caused by a programmatic selection.
        this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(t =>
            {
                if (_suppressSearchCount > 0) { _suppressSearchCount--; return; }
                _ = SearchAsync(t);
            });

        // Any change to what is queued puts results already shown out of date.
        Queue.CollectionChanged += (_, e) =>
        {
            foreach (var item in e.NewItems?.OfType<ProductionQueueVm>() ?? [])
                item.PropertyChanged += (_, p) =>
                {
                    if (p.PropertyName is nameof(ProductionQueueVm.Quantity) or nameof(ProductionQueueVm.MeLevel)) MarkStale();
                };
            this.RaisePropertyChanged(nameof(HasQueue));
            MarkStale();
        };

        _ = LoadParksAsync();
    }

    /// <summary>
    /// Fills the park dropdown: at start, and again whenever Indy Parks adds, deletes or renames a
    /// park, so the list is never stale for the rest of the session.
    ///
    /// <para>The park already chosen stays chosen while it exists. Only a first load, or a chosen
    /// park that was deleted, falls back to the default park.</para>
    /// </summary>
    public async Task LoadParksAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parks     = await db.IndyParks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        var defaultId = parks.FirstOrDefault(p => p.IsDefault)?.Id;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Read before the list is cleared: clearing it can push a null selection back
            // through the dropdown's binding.
            var keep = _selectedPark?.Id;

            Parks.Clear();
            foreach (var p in parks)
                Parks.Add(new ParkOption { Id = p.Id, Name = p.Name });

            SelectedPark = Parks.FirstOrDefault(p => p.Id == keep)
                        ?? Parks.FirstOrDefault(p => p.Id == defaultId)
                        ?? Parks.FirstOrDefault();
        });
    }

    private async Task SearchAsync(string text)
    {
        SearchResults.Clear();
        PendingType = null;
        if (text.Length < 2) { ShowResults = false; return; }

        // The English name or the one shown: a name typed in the interface language matches by
        // the ids of the types it names.
        await SdeNames.EnsureLoadedAsync();
        var shownIds = SdeNames.Find(SdeNameKind.Type, text).Select(id => (int)id).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync();
        // Only offer items that can actually be produced — i.e. types that are the output of a
        // manufacturing or reaction blueprint. This excludes BPOs, raw materials, etc.
        var matches = await db.SdeTypes.AsNoTracking()
            .Where(t => t.Published
                     && (EF.Functions.Like(t.Name, $"%{text}%") || shownIds.Contains(t.TypeId))
                     && db.SdeBlueprintProducts.Any(p => p.ProductTypeId == t.TypeId
                            && (p.Activity == "manufacturing" || p.Activity == "reaction")))
            .OrderBy(t => t.Name)
            .Take(100)
            .Select(t => new { t.TypeId, t.Name })
            .ToListAsync();

        // Listed by the name shown. The hundred are still the first hundred in English order.
        foreach (var r in matches.Select(m => new ProdTypeSearchResult { TypeId = m.TypeId, TypeName = m.Name })
                                 .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture))
            SearchResults.Add(r);
        ShowResults = SearchResults.Count > 0;
    }

    /// <summary>Arrow keys in the search box: move the highlight through the results.</summary>
    public void MoveHighlight(int delta)
    {
        if (SearchResults.Count == 0) return;
        var at   = SearchResults.ToList().FindIndex(r => r.IsHighlighted);
        var next = at < 0 ? (delta > 0 ? 0 : SearchResults.Count - 1)
                          : Math.Clamp(at + delta, 0, SearchResults.Count - 1);
        if (at >= 0) SearchResults[at].IsHighlighted = false;
        SearchResults[next].IsHighlighted = true;
    }

    /// <summary>
    /// Enter: picks the highlighted result — the first, if none is — or, once an item is picked
    /// and the list is closed, queues it.
    /// </summary>
    public void AcceptSearch()
    {
        if (ShowResults && SearchResults.Count > 0)
            SelectResult(SearchResults.FirstOrDefault(r => r.IsHighlighted) ?? SearchResults[0]);
        else if (CanAdd)
            AddToQueue();
    }

    private async void SelectResult(ProdTypeSearchResult result)
    {
        // ⚠️ Blocks the debounced search this assignment triggers — only when it triggers one. Text
        // that already reads the name changes nothing, and a count taken anyway was spent on the
        // next search typed, which then showed nothing.
        if (SearchText != result.DisplayName) _suppressSearchCount++;
        PendingType           = result;
        SearchText            = result.DisplayName;
        ShowResults           = false;
        SearchResults.Clear();
        _selectedSearchResult = null;
        this.RaisePropertyChanged(nameof(SelectedSearchResult));

        _pendingNeedsBpc = false;
        // Pre-select the default ME for this item (user can still change it before adding), and
        // note whether it will cost a copy.
        try
        {
            var (me, needsBpc) = await _service.QueueDefaultsAsync(result.TypeId);
            NewMeLevel       = me;
            _pendingNeedsBpc = needsBpc;
        }
        catch { /* leave NewMeLevel as-is on lookup failure */ }
    }

    private void AddToQueue()
    {
        if (PendingType is null) return;

        // ⚠️ Into the row already there, not a second one: the plan takes one ME per item, so two
        // rows of one item were never two builds. The row keeps its own ME, which may have been
        // edited since it was queued.
        if (Queue.FirstOrDefault(q => q.TypeId == PendingType.TypeId) is { } queued)
            queued.Quantity += NewQuantity;
        else
            Queue.Add(NewEntry(PendingType.TypeId, PendingType.TypeName, NewQuantity, NewMeLevel));
        if (_pendingNeedsBpc) IncludeBpcCost = true;
        _pendingNeedsBpc = false;
        // Reset for next item — suppress the debounced search triggered by clearing SearchText
        if (SearchText.Length > 0) _suppressSearchCount++;
        SearchText  = "";
        PendingType = null;
        NewQuantity = 1;
        // Keep NewMeLevel as-is for efficiency when adding multiple items at same ME
    }

    private ProductionQueueVm NewEntry(int typeId, string typeName, int quantity, int me)
    {
        var entry = new ProductionQueueVm { TypeId = typeId, TypeName = typeName, Quantity = quantity, MeLevel = me };
        entry.RemoveCommand   = ReactiveCommand.Create(() => { Queue.Remove(entry); });
        entry.NavigateCommand = OpenInItemBrowserCommand;
        return entry;
    }

    /// <summary>
    /// Items sent from another tool, one of each, as if each were picked and added here: at the
    /// ME a new row starts with, into the row already there when the item is queued, and with the
    /// copy cost switched on when one needs a copy. <paramref name="replace"/> empties the queue
    /// first. <paramref name="items"/> carry the English name, as the search hands one over.
    /// Returns how many were added.
    /// </summary>
    public async Task<int> QueueItemsAsync(IReadOnlyList<(int TypeId, string TypeName)> items, bool replace)
    {
        if (replace) ClearQueue();
        var added = 0;
        foreach (var (typeId, typeName) in items.DistinctBy(i => i.TypeId))
        {
            if (Queue.FirstOrDefault(q => q.TypeId == typeId) is { } queued) queued.Quantity += 1;
            else
            {
                var (me, needsBpc) = (10, false);
                try { (me, needsBpc) = await _service.QueueDefaultsAsync(typeId); }
                catch { /* the usual ME on a failed lookup, as the search leaves it */ }
                Queue.Add(NewEntry(typeId, typeName, 1, me));
                if (needsBpc) IncludeBpcCost = true;
            }
            added++;
        }
        if (added > 0 && Plan is not null) IsStale = true;
        return added;
    }

    private void Remove(ProductionQueueVm item) => Queue.Remove(item);

    private void ClearQueue()
    {
        Queue.Clear();
        Plan    = null;
        IsStale = false;
        Status  = "";
    }

    private async Task CalculateAsync()
    {
        if (SelectedPark is null || Queue.Count == 0) return;
        IsBusy = true;
        Status = IndustryText.CalcStatusCalculating;
        Plan   = null;
        try
        {
            var requests = Queue.Select(q => new ProductionQueueEntry
            {
                TypeId   = q.TypeId,
                TypeName = q.TypeName,
                Quantity = q.Quantity,
                MeLevel  = q.MeLevel,
            }).ToList();

            var plan = await _service.CalculateAsync(requests, SelectedPark.Id, IncludeBpcCost, bpcFollowsToggle: true);
            // Before assigning, so the job rows arrive with their Missing values and durations
            // already filled — PlanJob and PlanJobMaterial have no change notification.
            await _service.ApplyAvailabilityAsync(plan, CurrentMissingMode);
            await _service.ApplyDurationsAsync(plan, SelectedPark.Id);

            // The rows show their names in the interface language, looked up as they are drawn,
            // so the names are waited for once rather than shown in English first. The
            // intermediates come sorted by their English names; this list is for reading, so it
            // is sorted by the name shown.
            await SdeNames.EnsureLoadedAsync();
            plan.Intermediates = [.. plan.Intermediates.OrderBy(i => i.DisplayName, StringComparer.CurrentCulture)];
            Plan    = plan;
            IsStale = false;
            Status  = string.Format(IndustryText.CalcStatusDone, plan.AllJobs.Count, plan.RawMaterials.Count);
        }
        catch (Exception ex) { Status = string.Format(CommonText.ErrorWithMessage, ex.Message); }
        finally { IsBusy = false; }
    }
}
