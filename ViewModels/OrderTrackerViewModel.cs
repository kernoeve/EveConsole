using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using EveCortex.Data;
using EveCortex.Models;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

// Result returned by the add/edit order dialog.
public record OrderDialogResult(int TypeId, string TypeName, int Units, string Buyer,
    string? EstimatedDate, double PurchasePrice, string Status);

// One row on the Order Tracker grid.
public class TrackedOrderRowVm
{
    public int Id { get; }
    public DateTimeOffset Created { get; } public long CreatedSort { get; } public string CreatedText { get; }
    public int    TypeId  { get; } public string Type   { get; }
    public int    Units   { get; } public string UnitsText { get; }
    public string Buyer   { get; }
    public string EstDate { get; }
    public double PurchaseRaw { get; } public string Purchase { get; }
    public string StatusRaw   { get; } public string Status   { get; }
    public double BuildRaw  { get; } public string Build  { get; }
    public double ProfitRaw { get; } public string Profit { get; }
    public double ProfitPctRaw { get; } public string ProfitPct { get; }

    public TrackedOrderRowVm(TrackedOrder o, string typeName, double? buildCost)
    {
        Id          = o.Id;
        Created     = o.CreatedAt;
        CreatedSort = o.CreatedAt.UtcTicks;
        CreatedText = o.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd");
        TypeId      = o.TypeId;   Type  = typeName;
        Units       = o.Units;    UnitsText = o.Units.ToString("N0");
        Buyer       = o.Buyer;
        EstDate     = o.EstimatedDate ?? "";
        PurchaseRaw = o.PurchasePrice; Purchase = MarketFmt.Isk(o.PurchasePrice);
        StatusRaw   = o.Status;
        Status      = o.Status.Length > 0 ? char.ToUpper(o.Status[0]) + o.Status[1..] : o.Status;

        BuildRaw = buildCost ?? 0;
        Build    = buildCost is double b ? MarketFmt.Isk(b) : "—";
        var profit = buildCost is double bc ? o.PurchasePrice - bc : (double?)null;
        ProfitRaw = profit ?? double.MinValue;
        Profit    = profit is double p ? MarketFmt.Isk(p) : "—";
        var pct = buildCost is double bc2 && bc2 != 0 ? (o.PurchasePrice - bc2) / bc2 * 100 : (double?)null;
        ProfitPctRaw = pct ?? double.MinValue;
        ProfitPct    = pct is double pp ? $"{pp:N1}%" : "—";
    }

    public OrderDialogResult ToDialog() =>
        new(TypeId, Type, Units, Buyer, string.IsNullOrEmpty(EstDate) ? null : EstDate, PurchaseRaw, StatusRaw);
}

public record OrderStatusFilter(string Label, string? Value) { public override string ToString() => Label; }

// Order Tracker — user-entered outgoing orders (items promised to buyers), with build cost and
// profit vs the agreed purchase price. Fully user-driven; nothing is pulled from ESI.
public class OrderTrackerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;

    private readonly List<TrackedOrderRowVm> _all = new();

    public ObservableCollection<TrackedOrderRowVm> Rows { get; } = new();

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

    public OrderTrackerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory     = dbFactory;
        _errorLogger   = errorLogger;
        _statusFilter  = StatusFilters[0];   // Active (pending)

        AddCommand    = ReactiveCommand.CreateFromTask(AddAsync);
        EditCommand   = ReactiveCommand.CreateFromTask(EditAsync);
        DeleteCommand = ReactiveCommand.CreateFromTask(DeleteAsync);

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var orders = await db.TrackedOrders.AsNoTracking().ToListAsync();

            var typeIds = orders.Select(o => o.TypeId).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            var buildCosts = await db.BuildCosts.AsNoTracking().Where(b => typeIds.Contains(b.TypeId))
                .ToDictionaryAsync(b => b.TypeId, b => (double)b.TotalCost);

            _all.Clear();
            foreach (var o in orders)
            {
                double? build = buildCosts.TryGetValue(o.TypeId, out var bc) && bc > 0 ? bc * o.Units : null;
                _all.Add(new TrackedOrderRowVm(o, typeNames.TryGetValue(o.TypeId, out var n) ? n : $"Type {o.TypeId}", build));
            }
            _all.Sort((a, b) => b.CreatedSort.CompareTo(a.CreatedSort));
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("OrderTrackerViewModel", "Load", ex);
            StatusText = "Error loading orders.";
        }
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

        var list = q.ToList();
        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        StatusText = $"{list.Count:N0} order(s)";
    }

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { date = d.Date; return true; }
        date = default; return false;
    }

    // ── CRUD ──────────────────────────────────────────────────────────────────
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
                EstimatedDate = r.EstimatedDate,
                PurchasePrice = r.PurchasePrice,
                Status        = r.Status,
                CreatedAt     = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
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
            o.EstimatedDate = r.EstimatedDate;
            o.PurchasePrice = r.PurchasePrice;
            o.Status        = r.Status;
            await db.SaveChangesAsync();
            await LoadAsync();
        }
        catch (Exception ex) { _errorLogger.Log("OrderTrackerViewModel", "Edit", ex); StatusText = "Error saving order."; }
    }

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
}
