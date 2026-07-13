using System.Collections.ObjectModel;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class ProdTypeSearchResult
{
    public int    TypeId   { get; set; }
    public string TypeName { get; set; } = "";
    public override string ToString() => TypeName;
}

public class ProductionQueueVm : ReactiveObject
{
    public int    TypeId   { get; set; }
    public string TypeName { get; set; } = "";

    private int _quantity = 1;
    public int Quantity { get => _quantity; set => this.RaiseAndSetIfChanged(ref _quantity, value); }

    private int _meLevel = 10;
    public int MeLevel { get => _meLevel; set => this.RaiseAndSetIfChanged(ref _meLevel, value); }

    private Bitmap? _icon;
    public Bitmap? Icon { get => _icon; set => this.RaiseAndSetIfChanged(ref _icon, value); }

    public string MeBadge   => $"ME{MeLevel}";
    public string QtyDisplay => $"×{Quantity:N0}";

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

    private bool _showMaterials;
    public bool ShowMaterials
    {
        get => _showMaterials;
        set => this.RaiseAndSetIfChanged(ref _showMaterials, value);
    }
}

public class ProductionCalculatorViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ProductionCalculatorService     _service;
    private static readonly HttpClient               _http = new();
    private readonly Dictionary<int, Bitmap?>        _iconCache = [];

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

    // ── New item settings ─────────────────────────────────────────────────
    private int _newQuantity = 1;
    public int NewQuantity { get => _newQuantity; set => this.RaiseAndSetIfChanged(ref _newQuantity, value); }

    private int _newMeLevel = 10;
    public int NewMeLevel { get => _newMeLevel; set => this.RaiseAndSetIfChanged(ref _newMeLevel, value); }

    // ── Queue ─────────────────────────────────────────────────────────────
    public ObservableCollection<ProductionQueueVm> Queue { get; } = [];

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
    public ParkOption? SelectedPark { get => _selectedPark; set => this.RaiseAndSetIfChanged(ref _selectedPark, value); }

    // Include the final product's blueprint copy (contract price) as an input. Always applied for
    // non-BPO (BPC-only) items regardless of this toggle; optional for standard BPO items.
    private bool _includeBpcCost;
    public bool IncludeBpcCost { get => _includeBpcCost; set => this.RaiseAndSetIfChanged(ref _includeBpcCost, value); }

    // ── Results ───────────────────────────────────────────────────────────
    private ProductionPlan? _plan;
    public ProductionPlan? Plan
    {
        get => _plan;
        private set
        {
            this.RaiseAndSetIfChanged(ref _plan, value);
            this.RaisePropertyChanged(nameof(HasResults));
            this.RaisePropertyChanged(nameof(JobTreeRoots));
        }
    }

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

            // Append shared multi-parent jobs at root level, sorted by name
            foreach (var id in sharedIds.OrderBy(id => jobIndex[id].OutputTypeName))
                roots.Add(new JobTreeNode { Job = jobIndex[id], NavigateCommand = OpenInItemBrowserCommand });

            return roots;
        }
    }

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
        ProductionCalculatorService     service)
    {
        _dbFactory = dbFactory;
        _service   = service;

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

        _ = LoadParksAsync();
    }

    private async Task LoadParksAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var parks = await db.IndyParks.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
        Parks.Clear();
        foreach (var p in parks)
            Parks.Add(new ParkOption { Id = p.Id, Name = p.Name });

        // Select the default park
        ParkOption? defaultPark = null;
        foreach (var p in Parks)
        {
            await using var ctx = await _dbFactory.CreateDbContextAsync();
            var pk = await ctx.IndyParks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == p.Id);
            if (pk?.IsDefault == true) { defaultPark = p; break; }
        }
        SelectedPark = defaultPark ?? Parks.FirstOrDefault();
    }

    private async Task SearchAsync(string text)
    {
        SearchResults.Clear();
        PendingType = null;
        if (text.Length < 2) { ShowResults = false; return; }

        await using var db = await _dbFactory.CreateDbContextAsync();
        // Only offer items that can actually be produced — i.e. types that are the output of a
        // manufacturing or reaction blueprint. This excludes BPOs, raw materials, etc.
        var matches = await db.SdeTypes.AsNoTracking()
            .Where(t => t.Published && EF.Functions.Like(t.Name, $"%{text}%")
                     && db.SdeBlueprintProducts.Any(p => p.ProductTypeId == t.TypeId
                            && (p.Activity == "manufacturing" || p.Activity == "reaction")))
            .OrderBy(t => t.Name)
            .Take(100)
            .Select(t => new { t.TypeId, t.Name })
            .ToListAsync();

        foreach (var m in matches)
            SearchResults.Add(new ProdTypeSearchResult { TypeId = m.TypeId, TypeName = m.Name });
        ShowResults = SearchResults.Count > 0;
    }

    private void SelectResult(ProdTypeSearchResult result)
    {
        _suppressSearchCount++;          // block the debounced search that this assignment triggers
        PendingType           = result;
        SearchText            = result.TypeName;
        ShowResults           = false;
        SearchResults.Clear();
        _selectedSearchResult = null;
        this.RaisePropertyChanged(nameof(SelectedSearchResult));
    }

    private void AddToQueue()
    {
        if (PendingType is null) return;
        var entry = new ProductionQueueVm
        {
            TypeId   = PendingType.TypeId,
            TypeName = PendingType.TypeName,
            Quantity = NewQuantity,
            MeLevel  = NewMeLevel,
        };
        entry.RemoveCommand   = ReactiveCommand.Create(() => { Queue.Remove(entry); });
        entry.NavigateCommand = OpenInItemBrowserCommand;
        Queue.Add(entry);
        _ = LoadIconAsync(entry);
        // Reset for next item — suppress the debounced search triggered by clearing SearchText
        _suppressSearchCount++;
        SearchText  = "";
        PendingType = null;
        NewQuantity = 1;
        // Keep NewMeLevel as-is for efficiency when adding multiple items at same ME
    }

    private void Remove(ProductionQueueVm item) => Queue.Remove(item);

    private void ClearQueue()
    {
        Queue.Clear();
        Plan   = null;
        Status = "";
    }

    private async Task LoadIconAsync(ProductionQueueVm entry)
    {
        if (_iconCache.TryGetValue(entry.TypeId, out var cached)) { entry.Icon = cached; return; }
        try
        {
            var url   = $"https://images.evetech.net/types/{entry.TypeId}/icon?size=32";
            var bytes = await _http.GetByteArrayAsync(url);
            using var ms = new System.IO.MemoryStream(bytes);
            var bmp   = new Bitmap(ms);
            _iconCache[entry.TypeId] = bmp;
            await Dispatcher.UIThread.InvokeAsync(() => entry.Icon = bmp);
        }
        catch { _iconCache[entry.TypeId] = null; }
    }

    private async Task CalculateAsync()
    {
        if (SelectedPark is null || Queue.Count == 0) return;
        IsBusy = true;
        Status = "Calculating...";
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

            var plan = await _service.CalculateAsync(requests, SelectedPark.Id, IncludeBpcCost);
            Plan   = plan;
            Status = $"Done — {plan.AllJobs.Count} jobs, {plan.RawMaterials.Count} raw materials";
        }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}
