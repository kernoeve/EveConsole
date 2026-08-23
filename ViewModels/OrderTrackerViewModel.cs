using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// Result returned by the add/edit order dialog.
/// <param name="BuyerId">Zero when the buyer was typed rather than picked — an order predating
/// the picker, or a name the search could not reach.</param>
public record OrderDialogResult(int TypeId, string TypeName, int Units, string Buyer,
    string? EstimatedDate, double PurchasePrice, string Status, bool IsPriority = false,
    long BuyerId = 0, string BuyerType = "",
    /// <summary>Typed in by hand when the automatic match cannot find the contract — usually
    /// because its item list differs from the order. Null clears the link.</summary>
    int? LinkedContractId = null,
    /// <summary>Overrides the automatic settled date when the real one differs.</summary>
    string? CompletedOn = null,
    /// <summary>Free tags on the order. Null means "not edited here" and leaves them alone.</summary>
    IReadOnlyList<string>? Labels = null);

/// <summary>One candidate in the buyer picker. Subtitle disambiguates two similar names the way
/// the entity browser's own dropdown does.</summary>
public record BuyerResultVm(long Id, string Name, string Subtitle, string EntityType)
{
    // AutoComplete-style lists write ToString() back into the box; the bare name is what belongs.
    public override string ToString() => Name;
}

// One row on the Order Tracker grid.
public class TrackedOrderRowVm
{
    public int Id { get; }
    public DateTimeOffset Created { get; } public long CreatedSort { get; } public string CreatedText { get; }
    public int    TypeId  { get; } public string Type   { get; }
    public int    Units   { get; } public string UnitsText { get; }
    public string Buyer   { get; }

    /// <summary>The picked buyer. Zero on an order whose buyer was typed, which is every order
    /// made before the field became a picker — those show the name without a link.</summary>
    public long   BuyerId   { get; }
    public string BuyerType { get; }

    /// <summary>
    /// Who the contract is made out to, when that is not the buyer.
    ///
    /// <para>Blank on most orders, and blank means "the buyer" rather than "unknown" — a contract
    /// with nobody named goes to whoever ordered.</para>
    /// </summary>
    public long   ContractToId   { get; }
    public string ContractTo     { get; }
    public string ContractToType { get; }

    public bool HasContractToLink => ContractToId > 0 && ContractTo.Length > 0;

    public void OpenContractTo() => EntityNavigator.Instance.Entity(
        ContractToType == "corporation" ? EntityKind.PlayerCorp : EntityKind.Pilot, ContractToId);

    public bool HasTypeLink  => TypeId  > 0 && Type.Length  > 0;
    public bool HasBuyerLink => BuyerId > 0 && Buyer.Length > 0;

    public void OpenType()  => EntityNavigator.Instance.Item(TypeId);
    public void OpenBuyer() => EntityNavigator.Instance.Entity(
        BuyerType == "corporation" ? EntityKind.PlayerCorp : EntityKind.Pilot, BuyerId);

    public string EstDate { get; }

    /// <summary>When the order was settled — from the contract-s acceptance date, or today when
    /// the status was set by hand.</summary>
    public string CompletedOn { get; }
    public bool   IsPriority   { get; }
    /// <summary>A star rather than True/False: the column is scanned, not read.</summary>
    public string PriorityMark { get; }
    public double PurchaseRaw { get; } public string Purchase { get; }
    public string StatusRaw   { get; } public string Status   { get; }
    public double BuildRaw  { get; } public string Build  { get; }
    /// <summary>Which day's cost the Build cell is quoting — the settled day, or today.</summary>
    public string BuildBasis { get; } = "";
    public double ProfitRaw { get; } public string Profit { get; }
    public double ProfitPctRaw { get; } public string ProfitPct { get; }

    /// <summary>
    /// Where the units are coming from, as worked out by OrderFulfilmentService — and for a job or
    /// a contract, which one. Read-only: the tool does not set these, the poll does.
    /// </summary>
    /// <summary>
    /// Kept apart on purpose. A job and a contract are different facts about the same order — what
    /// is making it and what delivered it — and folding them into one field meant linking the
    /// contract erased the job that built the thing.
    /// </summary>
    public string IndyJob      { get; }
    public string Contract     { get; }
    public string FromStock    { get; }
    public int?   LinkedJobId      { get; }
    public int?   LinkedContractId { get; }
    public bool   HasContractLink => LinkedContractId is > 0;

    public void OpenContract()
    {
        if (LinkedContractId is > 0 and { } id) EntityNavigator.Instance.Contract(id);
    }

    /// <summary>
    /// Which store took this order, or "" for one entered by hand.
    ///
    /// <para>⚠️ Empty on an order whose store was deleted BEFORE stores were soft-deleted — that
    /// row is genuinely gone and its name is unrecoverable. Everything since keeps its name.</para>
    /// </summary>
    public string Store { get; private set; } = "";

    /// <summary>
    /// The order's code — what a buyer quotes to ask about it or cancel it.
    ///
    /// <para>⚠️ Shared by every row of one order: three items ordered together are three rows
    /// under one code. Blank only on orders that predate codes being issued.</para>
    /// </summary>
    public string OrderRef { get; private set; } = "";

    /// <summary>Tags on this order, for display and for filtering.</summary>
    public IReadOnlyList<string> LabelList { get; private set; } = [];

    /// <summary>The column's text — the tags, comma-separated.</summary>
    public string Labels => string.Join(", ", LabelList);

    public void SetLabels(IReadOnlyList<string> labels) => LabelList = labels;

    public TrackedOrderRowVm(TrackedOrder o, string typeName, double? buildCost,
                             string storeName = "",
                             string contractLabel = "", string buildAsOf = "")
    {
        Store    = storeName;
        OrderRef = o.OrderRef;
        Id             = o.Id;
        ContractToId   = o.ContractToId;
        ContractTo     = o.ContractToName;
        ContractToType = o.ContractToType;
        Created     = o.CreatedAt;
        CreatedSort = o.CreatedAt.UtcTicks;
        CreatedText = o.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd");
        TypeId      = o.TypeId;   Type  = typeName;
        Units       = o.Units;    UnitsText = o.Units.ToString("N0");
        Buyer       = o.Buyer;
        BuyerId     = o.BuyerId;
        BuyerType   = o.BuyerType;
        EstDate     = o.EstimatedDate ?? "";
        CompletedOn = o.CompletedOn ?? "";
        PurchaseRaw = o.PurchasePrice; Purchase = MarketFmt.Isk(o.PurchasePrice);
        StatusRaw   = o.Status;
        IsPriority  = o.IsPriority;
        PriorityMark = o.IsPriority ? "★" : "";
        Status      = o.Status.Length > 0 ? char.ToUpper(o.Status[0]) + o.Status[1..] : o.Status;

        LinkedJobId      = o.LinkedJobId;
        LinkedContractId = o.LinkedContractId;
        IndyJob   = o.LinkedJobId is { } job ? $"Job {job}" : "";
        Contract  = contractLabel;
        FromStock = o.FulfilmentSource == OrderFulfilmentService.SourceStock ? "✓" : "";

        BuildRaw = buildCost ?? 0;
        Build    = buildCost is double b ? MarketFmt.Isk(b) : "—";
        // Two settled orders for the same item can legitimately show different costs, so the cell
        // says which day it is quoting.
        BuildBasis = buildAsOf.Length > 0
            ? $"Build cost as it stood on {buildAsOf}, the day this order settled."
            : "Current build cost.";
        var profit = buildCost is double bc ? o.PurchasePrice - bc : (double?)null;
        ProfitRaw = profit ?? double.MinValue;
        Profit    = profit is double p ? MarketFmt.Isk(p) : "—";
        var pct = buildCost is double bc2 && bc2 != 0 ? (o.PurchasePrice - bc2) / bc2 * 100 : (double?)null;
        ProfitPctRaw = pct ?? double.MinValue;
        ProfitPct    = pct is double pp ? $"{pp:N1}%" : "—";
    }

    public OrderDialogResult ToDialog() =>
        new(TypeId, Type, Units, Buyer, string.IsNullOrEmpty(EstDate) ? null : EstDate,
            PurchaseRaw, StatusRaw, IsPriority, BuyerId, BuyerType, LinkedContractId,
            string.IsNullOrEmpty(CompletedOn) ? null : CompletedOn,
            LabelList.ToList());
}

public record OrderStatusFilter(string Label, string? Value) { public override string ToString() => Label; }

// Order Tracker — user-entered outgoing orders (items promised to buyers), with build cost and
// profit vs the agreed purchase price. Fully user-driven; nothing is pulled from ESI.
public class OrderTrackerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;

    private readonly List<TrackedOrderRowVm> _all = new();

    public BulkObservableCollection<TrackedOrderRowVm> Rows { get; } = [];

    // Set by the view — shows the add/edit dialog (null initial = add) and returns the result.
    public Func<OrderDialogResult?, Task<OrderDialogResult?>>? ShowOrderDialog { get; set; }

    private TrackedOrderRowVm? _selected;
    public TrackedOrderRowVm? Selected { get => _selected; set => this.RaiseAndSetIfChanged(ref _selected, value); }

    // ── Filters ───────────────────────────────────────────────────────────────
    public IReadOnlyList<OrderStatusFilter> StatusFilters { get; } =
    [
        new("Active",    "pending"),
        new("Completed", "completed"),
        new("Canceled",  "canceled"),
        new("All",       null),
    ];
    private OrderStatusFilter _statusFilter;
    public OrderStatusFilter StatusFilter
    {
        get => _statusFilter;
        set { this.RaiseAndSetIfChanged(ref _statusFilter, value ?? StatusFilters[0]); ApplyFilters(); }
    }

    private string _createdFrom = "";
    public string CreatedFrom { get => _createdFrom; set { this.RaiseAndSetIfChanged(ref _createdFrom, value); ApplyFilters(); } }
    private string _createdThru = "";
    public string CreatedThru { get => _createdThru; set { this.RaiseAndSetIfChanged(ref _createdThru, value); ApplyFilters(); } }
    private string _typeFilter = "";
    public string TypeFilter { get => _typeFilter; set { this.RaiseAndSetIfChanged(ref _typeFilter, value); ApplyFilters(); } }
    private string _buyerFilter = "";
    public string BuyerFilter { get => _buyerFilter; set { this.RaiseAndSetIfChanged(ref _buyerFilter, value); ApplyFilters(); } }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ReactiveCommand<Unit, Unit> AddCommand    { get; }
    public ReactiveCommand<Unit, Unit> EditCommand   { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Shown in the filter for "do not filter" and "anything tagged at all".</summary>
    public const string AnyLabel    = "(any)";
    public const string HasAnyLabel = "(labelled)";

    public BulkObservableCollection<string> LabelOptions { get; } = [];

    private string _labelFilter = AnyLabel;
    public string LabelFilter
    {
        get => _labelFilter;
        set { this.RaiseAndSetIfChanged(ref _labelFilter, value ?? AnyLabel); ApplyFilters(); }
    }

    private readonly OrderLabelService _labels;

    public OrderTrackerViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                 OrderLabelService labels,
                                 AppErrorLogger errorLogger)
    {
        _dbFactory     = dbFactory;
        _labels        = labels;
        _errorLogger   = errorLogger;
        _statusFilter  = StatusFilters[0];   // Active (pending)

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        EditCommand   = ReactiveCommand.CreateFromTask(EditAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);
        // The rows the fulfilment poll touches — a linked contract, an order it completed — are
        // written straight to the database, so this is how they reach the grid without a restart.
        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);

        foreach (var c in new[] { AddCommand, EditCommand, DeleteCommand, RefreshCommand })
            c.ThrownExceptions.Subscribe(ex => errorLogger.Log(nameof(OrderTrackerViewModel), "command", ex));

        // ⚠️ There was no automatic refresh at all. Everything that moves an order moves it in
        // the database — the fulfilment poll linking a contract, a store taking an order by mail
        // — and none of it went through this view model, so the grid was only ever as current as
        // the last time somebody pressed Refresh or reopened the tab.
        //
        // A minute matches the two things that feed it: the store checks its mail every minute,
        // and the fulfilment pass runs every five.
        Observable.Interval(TimeSpan.FromSeconds(60))
            .ObserveOnUi("OrderTracker.AutoRefresh")
            .SubscribeAsyncSafe(_ => LoadAsync(), errorLogger, "OrderTracker.AutoRefresh");

        _ = LoadAsync();
    }

    /// <summary>⚠️ Guards against a slow load overlapping the next tick. Two passes filling the
    /// same collection would fight over it, and the second would finish with rows the first was
    /// still building.</summary>
    private bool _loading;

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var orders = await db.TrackedOrders.AsNoTracking().ToListAsync();

            var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();
            // ⚠️ Includes deleted stores. A soft-deleted shop keeps its row precisely so its
            // orders can still name it; filtering them out here would undo that.
            var storeNames = await db.Stores.AsNoTracking()
                .Select(s => new { s.Id, s.Name })
                .ToDictionaryAsync(s => s.Id, s => s.Name);

            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            var buildCosts = await db.BuildCosts.AsNoTracking().Where(b => typeIds.Contains(b.TypeId))
                .ToDictionaryAsync(b => b.TypeId, b => (double)b.TotalCost);

            // Build cost is a moving number, so an order that has settled is judged against what the
            // item cost on the day it settled. Otherwise the profit shown against a months-old order
            // drifts every time its materials move, and stops describing the deal that was done.
            // Orders still open keep tracking the current cost.
            var settledTypeIds = orders.Where(o => o.CompletedOn is { Length: > 0 })
                                       .Select(o => o.TypeId).Distinct().ToList();

            // ⚠️ Only the type filter is pushed into SQL. Date is stored as text, so bounding it in
            // the query would mean leaning on string.CompareTo translation — for no gain, since what
            // comes back is one row per settled type per day.
            var history = await db.TypePriceSnapshots.AsNoTracking()
                .Where(s => settledTypeIds.Contains(s.TypeId) && s.BuildCost != null)
                .Select(s => new { s.TypeId, s.Date, s.BuildCost })
                .ToListAsync();

            var asOf = history.GroupBy(s => s.TypeId).ToDictionary(
                g => g.Key,
                g => g.Where(s => s.BuildCost is not null)
                      .Select(s => (s.Date, Value: s.BuildCost!.Value))
                      .OrderBy(s => s.Date, StringComparer.Ordinal)
                      .ToList());

            // That day, else the next day carrying a cost, else the last one before — the shared
            // rule, so this and the Sales Tracker cannot answer the same question differently.
            //
            // This used to look only backwards, which reports nothing at all on a database whose
            // snapshots all postdate the order. That is every new install, and it is why a build
            // cost went missing and a market price took its place.
            double? CostAsOf(int typeId, string? date)
                => date is { Length: > 0 } && asOf.TryGetValue(typeId, out var rows)
                    ? TypePriceHistoryService.ValueAsOf(rows, date)
                    : null;

            // Contract titles for the linked contracts, so the column can name one rather than
            // print a bare number. A contract without a title falls back to its id alone.
            var contractIds = orders.Where(o => o.LinkedContractId != null)
                                    .Select(o => o.LinkedContractId!.Value).Distinct().ToList();
            var contractNames = contractIds.Count == 0
                ? new Dictionary<int, string>()
                : await db.EsiContracts.AsNoTracking()
                    .Where(c => contractIds.Contains(c.ContractId))
                    .GroupBy(c => c.ContractId)
                    .Select(g => new { Id = g.Key, Title = g.Min(x => x.Title) })
                    .ToDictionaryAsync(x => x.Id, x => x.Title ?? "");

            _all.Clear();
            foreach (var o in orders)
            {
                // As of the settled day when there is one, today's cost when there is not. A settled
                // order with no snapshot that far back — an order older than price history — falls
                // back to the current cost rather than showing nothing.
                var settled = CostAsOf(o.TypeId, o.CompletedOn);
                var unit = settled
                        ?? (buildCosts.TryGetValue(o.TypeId, out var bc) ? (double?)bc : null);
                double? build = unit > 0 ? unit * o.Units : null;

                var label = o.LinkedContractId is { } cid
                    ? (contractNames.TryGetValue(cid, out var title) && title.Length > 0
                        ? $"{title} ({cid})"
                        : $"Contract {cid}")
                    : "";

                _all.Add(new TrackedOrderRowVm(
                    o, typeNames.TryGetValue(o.TypeId, out var n) ? n : $"Type {o.TypeId}", build, label,
                    storeNames.GetValueOrDefault(o.StoreId, ""),
                    settled is not null ? o.CompletedOn ?? "" : ""));
            }
            // One query for every row's labels rather than one per row.
            var labels = await _labels.ForOrdersAsync(_all.Select(r => r.Id).ToList());
            foreach (var row in _all)
                row.SetLabels(labels.GetValueOrDefault(row.Id, []));

            var known = await _labels.AllAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var keep = LabelFilter;
                LabelOptions.ResetTo([AnyLabel, HasAnyLabel, .. known]);
                // Held even when the label has just stopped existing, so a filter does not
                // silently widen to everything the moment its last order is untagged.
                _labelFilter = LabelOptions.Contains(keep) ? keep : AnyLabel;
                this.RaisePropertyChanged(nameof(LabelFilter));
            });

            _all.Sort((a, b) => b.CreatedSort.CompareTo(a.CreatedSort));
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("OrderTrackerViewModel", "Load", ex);
            StatusText = "Error loading orders.";
        }
        finally { _loading = false; }
    }

    private void ApplyFilters()
    {
        IEnumerable<TrackedOrderRowVm> q = _all;

        if (_statusFilter?.Value is string s) q = q.Where(r => r.StatusRaw == s);
        if (TryDate(_createdFrom, out var from)) q = q.Where(r => r.Created.UtcDateTime.Date >= from);
        if (TryDate(_createdThru, out var thru)) q = q.Where(r => r.Created.UtcDateTime.Date <= thru);
        if (!string.IsNullOrWhiteSpace(_typeFilter))
            q = q.Where(r => r.Type.Contains(_typeFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(_buyerFilter))
            q = q.Where(r => r.Buyer.Contains(_buyerFilter, StringComparison.OrdinalIgnoreCase));

        // Filtered off the rows' own labels rather than by querying again: they were loaded with
        // the rows, and a filter that went back to the database on every keystroke elsewhere in
        // this bar would be the slow part of a fast panel.
        if (_labelFilter == HasAnyLabel)
            q = q.Where(r => r.LabelList.Count > 0);
        else if (_labelFilter != AnyLabel && !string.IsNullOrWhiteSpace(_labelFilter))
            q = q.Where(r => r.LabelList.Any(
                    l => string.Equals(l, _labelFilter, StringComparison.OrdinalIgnoreCase)));

        // Rebuilding the rows drops the DataGrid's selection, which greys out Edit and Delete. A
        // reload replaces every row object, so the order has to be found again by id.
        var keepId = Selected?.Id;

        var list = q.ToList();
        // One notification rather than one per row: this now runs on a timer, and a grid that
        // relaid itself once per order every minute would be felt on a long list.
        Rows.ResetTo(list);

        if (keepId is { } id) Selected = Rows.FirstOrDefault(r => r.Id == id);
        StatusText = $"{list.Count:N0} order(s)";
    }

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { date = d.Date; return true; }
        date = default; return false;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// Puts one label on several orders at once — the grid's right-click.
    ///
    /// <para>Takes the rows given rather than the single Selected row, because tagging a
    /// selection is the whole point: labelling twenty orders one at a time is not a feature
    /// anybody would use.</para>
    /// </summary>
    public async Task AddLabelToAsync(IReadOnlyList<TrackedOrderRowVm> rows, string label)
    {
        var clean = OrderLabelService.Clean(label);
        if (clean.Length == 0 || rows.Count == 0) return;

        try
        {
            await _labels.AddAsync(rows.Select(r => r.Id).ToList(), clean);
            await LoadAsync();
            StatusText = $"Labelled {rows.Count:N0} order(s) \"{clean}\".";
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(OrderTrackerViewModel), nameof(AddLabelToAsync), ex);
            StatusText = $"Could not label: {ex.Message}";
        }
    }

    /// <summary>Takes one label off several orders — the other half of the right-click.</summary>
    public async Task RemoveLabelFromAsync(IReadOnlyList<TrackedOrderRowVm> rows, string label)
    {
        var clean = OrderLabelService.Clean(label);
        if (clean.Length == 0 || rows.Count == 0) return;

        try
        {
            foreach (var row in rows)
                await _labels.SetAsync(row.Id, row.LabelList.Where(
                    l => !string.Equals(l, clean, StringComparison.OrdinalIgnoreCase)));

            await LoadAsync();
            StatusText = $"Removed \"{clean}\" from {rows.Count:N0} order(s).";
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(OrderTrackerViewModel), nameof(RemoveLabelFromAsync), ex);
        }
    }

    /// <summary>The labels a picker should offer.</summary>
    public Task<List<string>> KnownLabelsAsync() => _labels.AllAsync();

    private async Task AddAsync()
    {
        if (ShowOrderDialog is null) return;
        var r = await ShowOrderDialog(null);
        if (r is null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.TrackedOrders.Add(new TrackedOrder
            {
                TypeId        = r.TypeId,
                Units         = r.Units,
                Buyer         = r.Buyer,
                BuyerId       = r.BuyerId,
                BuyerType     = r.BuyerType,
                EstimatedDate = r.EstimatedDate,
                PurchasePrice = r.PurchasePrice,
                Status        = r.Status,
                IsPriority    = r.IsPriority,
                CompletedOn   = r.CompletedOn ?? SettledOn(r.Status, null),
                // ⚠️ From the same pool as the store's, so a code identifies one order whichever
                // way it arrived. An order typed in after a conversation is still an order
                // somebody may ask about by number.
                OrderRef      = await OrderReference.NewAsync(db),
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            // ⚠️ After the save, because the labels key off the id the insert just assigned.
            if (r.Labels is { Count: > 0 })
            {
                var added = await db.TrackedOrders.OrderByDescending(o => o.Id)
                    .Select(o => o.Id).FirstAsync();
                await _labels.SetAsync(added, r.Labels);
            }

            await LoadAsync();
        }
        catch (Exception ex) { _errorLogger.Log("OrderTrackerViewModel", "Add", ex); StatusText = "Error adding order."; }
    }

    private async Task EditAsync()
    {
        if (ShowOrderDialog is null || Selected is null) return;
        var r = await ShowOrderDialog(Selected.ToDialog());
        if (r is null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var o = await db.TrackedOrders.FindAsync(Selected.Id);
            if (o is null) return;
            o.TypeId        = r.TypeId;
            o.Units         = r.Units;
            o.Buyer         = r.Buyer;
            o.BuyerId       = r.BuyerId;
            o.BuyerType     = r.BuyerType;
            o.EstimatedDate = r.EstimatedDate;
            o.PurchasePrice = r.PurchasePrice;
            // A date typed into the dialog wins outright; otherwise the automatic stamp applies.
            o.CompletedOn   = r.CompletedOn
                           ?? SettledOn(r.Status, o.Status == r.Status ? o.CompletedOn : null);
            o.Status        = r.Status;
            o.IsPriority    = r.IsPriority;

            // A contract typed in by hand overrides whatever the poll found — the point of the
            // field is the case where the automatic match cannot see it, usually because the
            // contract-s item list differs from the order.
            if (r.LinkedContractId != o.LinkedContractId)
            {
                o.LinkedContractId = r.LinkedContractId;
                // Unlinking drops the date the contract supplied — unless the user typed one
                // in this same edit, which is an explicit instruction to keep that date.
                if (r.LinkedContractId is null && r.CompletedOn is null) o.CompletedOn = null;
            }
            await db.SaveChangesAsync();

            // Null means the dialog did not touch them; a list — empty included — is what the
            // box was left holding, so clearing every chip really does clear the labels.
            if (r.Labels is not null) await _labels.SetAsync(o.Id, r.Labels);

            await LoadAsync();
        }
        catch (Exception ex) { _errorLogger.Log("OrderTrackerViewModel", "Edit", ex); StatusText = "Error saving order."; }
    }


    /// <summary>
    /// The settled date for a hand-set status: today when the user marks an order completed or
    /// cancelled, nothing while it is pending.
    ///
    /// <para>⚠️ An existing date is kept rather than re-stamped, so editing something else about a
    /// completed order does not move the day it was settled — and a date the fulfilment poll took
    /// from a contract's acceptance survives an unrelated edit.</para>
    /// </summary>
    private static string? SettledOn(string status, string? existing)
        => status is "completed" or "canceled"
            ? existing ?? DateTime.Now.ToString("yyyy-MM-dd")
            : null;
    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var o = await db.TrackedOrders.FindAsync(Selected.Id);
            if (o is not null) { db.TrackedOrders.Remove(o); await db.SaveChangesAsync(); }
            await LoadAsync();
        }
        catch (Exception ex) { _errorLogger.Log("OrderTrackerViewModel", "Delete", ex); StatusText = "Error deleting order."; }
    }

    // Type search for the add/edit dialog.
    public async Task<List<TypeResultVm>> SearchTypesAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return [];
        await using var db = await _dbFactory.CreateDbContextAsync();
        var pattern = $"%{text}%";
        var results = await db.SdeTypes.AsNoTracking()
            .Where(t => EF.Functions.Like(t.Name, pattern) && t.Published)
            .OrderBy(t => t.Name).Take(50)
            .Select(t => new { t.TypeId, t.Name }).ToListAsync();
        return results.Select(r => new TypeResultVm(r.TypeId, r.Name)).ToList();
    }

    /// <summary>
    /// Buyer candidates: characters and corporations, from what the app already knows.
    ///
    /// <para>⚠️ Local only — Characters, Corporations and the shared UniverseNames cache. No ESI
    /// search: a buyer is somebody you have dealt with, so they are almost always already named
    /// somewhere in the database, and reaching out on every keystroke would put an ESI round trip
    /// behind a text box. A name the search cannot reach is still typeable; it simply saves
    /// without an id and does not link.</para>
    /// </summary>
    public async Task<List<BuyerResultVm>> SearchBuyersAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 3) return [];

        await using var db = await _dbFactory.CreateDbContextAsync();
        var pattern = $"%{text}%";

        var chars = await db.Characters.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Name, pattern))
            .OrderBy(c => c.Name).Take(20)
            .Select(c => new BuyerResultVm(c.Id, c.Name, "Character", "character"))
            .ToListAsync();

        var corps = await db.Corporations.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Name, pattern))
            .OrderBy(c => c.Name).Take(20)
            .Select(c => new BuyerResultVm(c.Id, c.Name, "Corporation", "corporation"))
            .ToListAsync();

        // The shared name cache covers everyone else the app has ever resolved — buyers from past
        // sales, killmail participants, contract acceptors.
        var cached = await db.UniverseNames.AsNoTracking()
            .Where(u => EF.Functions.Like(u.Name, pattern)
                     && (u.Category == "character" || u.Category == "corporation"))
            .OrderBy(u => u.Name).Take(40)
            .Select(u => new BuyerResultVm(u.EntityId, u.Name,
                        u.Category == "corporation" ? "Corporation" : "Character", u.Category))
            .ToListAsync();

        return chars.Concat(corps).Concat(cached)
            .GroupBy(b => b.Id).Select(g => g.First())   // our own records win over the cache
            .OrderBy(b => b.Name)
            .Take(50)
            .ToList();
    }
}
