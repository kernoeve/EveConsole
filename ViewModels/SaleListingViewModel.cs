using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Media;
using EveCortex.Data;
using EveCortex.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveCortex.ViewModels;

public enum SaleCostBasis { BuildCost, MarketValue }

// One row on a Sale Listing tool: Date, Buyer, Item, Amount, Profit, Profit %. Profit is measured
// against build cost or market value depending on the tool; positive is green, negative red.
public class SaleListingRowVm
{
    public DateTimeOffset When { get; }
    public long   WhenSort { get; }
    public string WhenText { get; }
    public string Buyer    { get; }
    public string Item     { get; }
    public string OwnerType { get; }
    public long   OwnerId   { get; }
    public bool   OwnerIsPersonal { get; }
    public string Kind      { get; }

    public string Amount    { get; } public double AmountRaw    { get; }
    public string Profit    { get; } public double ProfitRaw    { get; }
    public string ProfitPct { get; } public double ProfitPctRaw { get; }
    public IBrush ProfitBrush { get; }

    public SaleListingRowVm(SaleRowVm s, SaleCostBasis basis)
    {
        When = s.When; WhenSort = s.WhenSort; WhenText = s.WhenText;
        Buyer = s.Buyer; Item = s.Items;
        OwnerType = s.OwnerType; OwnerId = s.OwnerId; OwnerIsPersonal = s.OwnerIsPersonal; Kind = s.Kind;
        AmountRaw = s.TotalRaw; Amount = MarketFmt.Isk(s.TotalRaw);

        var cost = basis == SaleCostBasis.BuildCost ? s.BuildOrNull : s.MarketOrNull;
        if (cost is double c)
        {
            var profit = s.TotalRaw - c;
            ProfitRaw = profit; Profit = MarketFmt.Isk(profit);
            var pct = c != 0 ? profit / c * 100 : (double?)null;
            ProfitPctRaw = pct ?? double.MinValue;
            ProfitPct    = pct is double pp ? $"{pp:N1}%" : "—";
            ProfitBrush  = profit >= 0 ? ProfitBrushes.Green : ProfitBrushes.Red;
        }
        else
        {
            ProfitRaw = double.MinValue; Profit = "—";
            ProfitPctRaw = double.MinValue; ProfitPct = "—";
            ProfitBrush = ProfitBrushes.Gray;
        }
    }
}

// Sale Listing tool — a focused sales grid (Date / Buyer / Item / Amount / Profit / Profit %) with
// profit measured against build cost or market value. Two instances are registered (one per basis).
public class SaleListingViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger      _errorLogger;
    private readonly CorpActivityService _names;
    private readonly SaleCostBasis       _basis;

    private readonly List<SaleRowVm> _all = new();
    public ObservableCollection<SaleListingRowVm> Rows { get; } = new();

    public string Title { get; }

    // Wired by MainWindowViewModel — opens the Sales Tracker tool from the header link.
    public Action? OpenSalesTracker { get; set; }

    // ── Filters (same as Sales Tracker) ─────────────────────────────────────────
    public ObservableCollection<SalesOwnerOption> OwnerOptions { get; } =
    [
        new("All",                               OwnerScope.All),
        new("All Characters and Personal Corps", OwnerScope.CharsAndPersonalCorps),
    ];
    private SalesOwnerOption _selectedOwner;
    public SalesOwnerOption SelectedOwner
    {
        get => _selectedOwner;
        set { this.RaiseAndSetIfChanged(ref _selectedOwner, value ?? OwnerOptions[1]); ApplyFilters(); }
    }

    public IReadOnlyList<SalesTypeOption> SaleTypeOptions { get; } =
    [
        new("All types", null),
        new("Market",    "Market"),
        new("Contract",  "Contract"),
    ];
    private SalesTypeOption _selectedType;
    public SalesTypeOption SelectedType
    {
        get => _selectedType;
        set { this.RaiseAndSetIfChanged(ref _selectedType, value ?? SaleTypeOptions[0]); ApplyFilters(); }
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); ApplyFilters(); } }
    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); ApplyFilters(); } }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public SaleListingViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger,
        CorpActivityService names, SaleCostBasis basis)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        _basis       = basis;
        Title = basis == SaleCostBasis.BuildCost ? "Sale Listing — Build Cost" : "Sale Listing — Market Value";

        _selectedOwner = OwnerOptions[1];
        _selectedType  = SaleTypeOptions[0];
        _dateFrom      = DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd");

        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tick => { _ = LoadAsync(); });

        _ = LoadAsync();
    }

    // Set the date window to the last N days (driven by the Overview period dropdown when this
    // VM is embedded as an Overview section). Data is loaded once and filtered in memory.
    public void SetPeriodDays(int days)
    {
        _dateThru = "";
        _dateFrom = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        this.RaisePropertyChanged(nameof(DateFrom));
        this.RaisePropertyChanged(nameof(DateThru));
        ApplyFilters();
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            var result = await SalesQuery.LoadAsync(_dbFactory, _names, _errorLogger);
            BuildOwnerOptions(result.Chars, result.Corps);
            _all.Clear();
            _all.AddRange(result.Rows);
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("SaleListingViewModel", "Load", ex);
            StatusText = "Error loading sales.";
        }
        finally { IsLoading = false; }
    }

    private void ApplyFilters()
    {
        IEnumerable<SaleRowVm> q = _all;

        q = _selectedOwner?.Scope switch
        {
            OwnerScope.CharsAndPersonalCorps => q.Where(r => r.OwnerType == "character" || (r.OwnerType == "corporation" && r.OwnerIsPersonal)),
            OwnerScope.Specific              => q.Where(r => r.OwnerType == _selectedOwner.OwnerType && r.OwnerId == _selectedOwner.OwnerId),
            _                                => q,
        };

        if (_selectedType?.Kind is string kind)
            q = q.Where(r => r.Kind == kind);

        if (TryDate(_dateFrom, out var from)) q = q.Where(r => r.When.UtcDateTime.Date >= from);
        if (TryDate(_dateThru, out var thru)) q = q.Where(r => r.When.UtcDateTime.Date <= thru);

        var list = q.Select(r => new SaleListingRowVm(r, _basis)).ToList();
        Rows.Clear();
        foreach (var r in list) Rows.Add(r);
        StatusText = list.Count == 0 ? "No sales match the filters." : $"{list.Count:N0} sale(s)";
    }

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { date = d.Date; return true; }
        date = default; return false;
    }

    private void BuildOwnerOptions(IReadOnlyList<(long Id, string Name)> chars, IReadOnlyList<(long Id, string Name)> corps)
    {
        if (OwnerOptions.Count > 2) return;
        foreach (var (id, name) in chars.OrderBy(c => c.Name))
            OwnerOptions.Add(new SalesOwnerOption(name, OwnerScope.Specific, id, "character"));
        foreach (var (id, name) in corps.OrderBy(c => c.Name))
            OwnerOptions.Add(new SalesOwnerOption(name, OwnerScope.Specific, id, "corporation"));
    }
}
