using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveConsole.ViewModels;

// ── Row view-models ───────────────────────────────────────────────────────────

public class WalletOwnerOption
{
    public string  Label     { get; }
    public long?   OwnerId   { get; }
    public string? OwnerType { get; }
    public bool    IsCorp    { get; }

    public WalletOwnerOption(string label, long? ownerId, string? ownerType, bool isCorp)
    {
        Label     = label;
        OwnerId   = ownerId;
        OwnerType = ownerType;
        IsCorp    = isCorp;
    }

    public override string ToString() => Label;
}

public class WalletJournalRowVm
{
    public string          DateText     { get; }
    public DateTimeOffset  DateRaw      { get; }
    public string          RefTypeText  { get; }
    public string          Description  { get; }
    public string          AmountText   { get; }
    public string          AmountColor  { get; }
    public string          BalanceText  { get; }
    public string          OwnerText    { get; }
    public string          DivisionText { get; }
    public decimal         AmountRaw    { get; }
    public decimal         BalanceRaw   { get; }

    // ── Links ─────────────────────────────────────────────────────────────────
    //
    // The owner is the wallet's own character or corporation, so the kind is known outright
    // rather than guessed from the id.
    private readonly long   _ownerId;
    private readonly string _ownerType;

    public bool HasOwnerLink => _ownerId > 0 && OwnerText.Length > 0;
    public void OpenOwner() => EntityNavigator.Instance.Entity(
        _ownerType == "corporation" ? EntityKind.PlayerCorp : EntityKind.Pilot, _ownerId);

    public WalletJournalRowVm(WalletJournalEntry e,
        IReadOnlyDictionary<long, string>        ownerNames,
        IReadOnlyDictionary<(long, int), string> divisionNames)
    {
        DateRaw      = e.Date;
        DateText     = e.Date.ToLocalTime().ToString("MMM d, HH:mm");
        RefTypeText  = FormatRefType(e.RefType);
        Description  = e.Description ?? e.Reason ?? "";
        AmountRaw    = e.Amount;
        AmountText   = FormatAmount(e.Amount);
        AmountColor  = e.Amount >= 0 ? "#5cb85c" : "#d9534f";
        BalanceRaw   = e.Balance;
        BalanceText  = FormatIsk(e.Balance);
        OwnerText    = ownerNames.TryGetValue(e.OwnerId, out var n) ? n : "";
        _ownerId     = e.OwnerId;
        _ownerType   = e.OwnerType;
        DivisionText = e.Division is > 0
            ? (divisionNames.TryGetValue((e.OwnerId, e.Division.Value), out var dn) ? dn
               : e.Division.Value == 1 ? "Master Wallet" : $"Div {e.Division}")
            : "";
    }

    private static string FormatRefType(string s) =>
        string.Join(" ", s.Split('_')
            .Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));

    private static string FormatAmount(decimal v)
    {
        var sign = v >= 0 ? "+" : "-";
        var abs  = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{sign}{abs / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)     return $"{sign}{abs / 1_000_000m:F2}M";
        if (abs >= 1_000m)         return $"{sign}{abs / 1_000m:F1}K";
        return $"{sign}{abs:N0}";
    }

    private static string FormatIsk(decimal v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{v / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)     return $"{v / 1_000_000m:F2}M";
        if (abs >= 1_000m)         return $"{v / 1_000m:F1}K";
        return $"{v:N0}";
    }
}

public class WalletTransactionRowVm
{
    public string  DateText     { get; }
    public string  TypeName     { get; }
    public string  Quantity     { get; }
    public string  UnitPrice    { get; }
    public string  Total        { get; }
    public string  TotalColor   { get; }
    public string  Direction    { get; }
    public string  OwnerText    { get; }
    public string  DivisionText { get; }
    public string  LocationName { get; }
    public int     QuantityRaw  { get; }
    public decimal UnitPriceRaw { get; }
    public decimal TotalRaw     { get; }

    // ── Links ─────────────────────────────────────────────────────────────────
    private readonly int    _typeId;
    private readonly long   _locationId;
    private readonly long   _ownerId;
    private readonly string _ownerType;

    public bool HasItemLink     => _typeId     > 0 && TypeName.Length     > 0;
    public bool HasLocationLink => _locationId > 0 && LocationName.Length > 0;
    public bool HasOwnerLink    => _ownerId    > 0 && OwnerText.Length    > 0;

    public void OpenItem() => EntityNavigator.Instance.Item(_typeId);

    /// <summary>⚠️ Station versus structure by int range: SdeStations keys on an int, so an id
    /// above that range cannot be a station.</summary>
    public void OpenLocation()
    {
        if (_locationId <= 0) return;
        if (_locationId <= int.MaxValue)
            EntityNavigator.Instance.Entity(EntityKind.Station, _locationId);
        else
            EntityNavigator.Instance.Structure(_locationId);
    }

    public void OpenOwner() => EntityNavigator.Instance.Entity(
        _ownerType == "corporation" ? EntityKind.PlayerCorp : EntityKind.Pilot, _ownerId);

    public WalletTransactionRowVm(WalletTransaction t,
        IReadOnlyDictionary<int, string>         typeNames,
        IReadOnlyDictionary<long, string>        ownerNames,
        IReadOnlyDictionary<long, string>        locationNames,
        IReadOnlyDictionary<(long, int), string> divisionNames)
    {
        DateText     = t.Date.ToLocalTime().ToString("MMM d, HH:mm");
        TypeName     = typeNames.TryGetValue(t.TypeId, out var n) ? n : $"Type {t.TypeId}";
        QuantityRaw  = t.Quantity;
        Quantity     = t.Quantity.ToString("N0");
        UnitPriceRaw = t.UnitPrice;
        UnitPrice    = FormatIsk(t.UnitPrice);
        var gross    = (decimal)t.Quantity * t.UnitPrice;
        TotalRaw     = t.IsBuy ? -gross : gross;
        Total        = FormatIsk(gross);
        TotalColor   = t.IsBuy ? "#d9534f" : "#5cb85c";
        Direction    = t.IsBuy ? "Buy" : "Sell";
        OwnerText    = ownerNames.TryGetValue(t.OwnerId, out var on) ? on : "";
        DivisionText = t.Division is > 0
            ? (divisionNames.TryGetValue((t.OwnerId, t.Division.Value), out var dn) ? dn
               : t.Division.Value == 1 ? "Master Wallet" : $"Div {t.Division}")
            : "";
        LocationName = locationNames.TryGetValue(t.LocationId, out var ln) ? ln : "";
        _typeId      = t.TypeId;
        _locationId  = t.LocationId;
        _ownerId     = t.OwnerId;
        _ownerType   = t.OwnerType;
    }

    private static string FormatIsk(decimal v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{v / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)     return $"{v / 1_000_000m:F2}M";
        if (abs >= 1_000m)         return $"{v / 1_000m:F1}K";
        return $"{v:N0}";
    }
}

public class WalletDivisionRowVm
{
    public int    Division    { get; }
    public string Name        { get; }
    public string BalanceText { get; }

    public WalletDivisionRowVm(int division, string name, decimal balance)
    {
        Division    = division;
        Name        = name;
        BalanceText = FormatIsk(balance);
    }

    private static string FormatIsk(decimal v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{v / 1_000_000_000m:F2}B ISK";
        if (abs >= 1_000_000m)     return $"{v / 1_000_000m:F2}M ISK";
        if (abs >= 1_000m)         return $"{v / 1_000m:F1}K ISK";
        return $"{v:N0} ISK";
    }
}

// ── Main view-model ───────────────────────────────────────────────────────────

public class WalletViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errorLogger;
    private bool _initialized;

    // ── Owners ────────────────────────────────────────────────────────────────

    private List<WalletOwnerOption> _owners = [];
    public IReadOnlyList<WalletOwnerOption> Owners => _owners;

    private WalletOwnerOption? _selectedOwner;
    public WalletOwnerOption? SelectedOwner
    {
        get => _selectedOwner;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedOwner, value);
            this.RaisePropertyChanged(nameof(ShowDivisions));
            if (_initialized) _ = LoadAsync();
        }
    }

    public bool ShowDivisions => _selectedOwner?.IsCorp == true;

    // ── Balance ───────────────────────────────────────────────────────────────

    private string _balanceText = "—";
    public string BalanceText
    {
        get => _balanceText;
        private set => this.RaiseAndSetIfChanged(ref _balanceText, value);
    }

    // ── Period ────────────────────────────────────────────────────────────────

    public IReadOnlyList<ActivityPeriodOption> Periods { get; }

    private ActivityPeriodOption _selectedPeriod;
    public ActivityPeriodOption SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPeriod, value);
            if (_initialized) _ = LoadAsync();
        }
    }

    // ── Overview charts ───────────────────────────────────────────────────────

    private IEnumerable<ISeries> _incomeSeries  = [];
    public IEnumerable<ISeries> IncomeSeries
    {
        get => _incomeSeries;
        private set => this.RaiseAndSetIfChanged(ref _incomeSeries, value);
    }

    private IEnumerable<ISeries> _expenseSeries = [];
    public IEnumerable<ISeries> ExpenseSeries
    {
        get => _expenseSeries;
        private set => this.RaiseAndSetIfChanged(ref _expenseSeries, value);
    }

    private string _incomeTotalText = "";
    public string IncomeTotalText
    {
        get => _incomeTotalText;
        private set => this.RaiseAndSetIfChanged(ref _incomeTotalText, value);
    }

    private string _expenseTotalText = "";
    public string ExpenseTotalText
    {
        get => _expenseTotalText;
        private set => this.RaiseAndSetIfChanged(ref _expenseTotalText, value);
    }

    private bool _hasIncomeData;
    public bool HasIncomeData
    {
        get => _hasIncomeData;
        private set => this.RaiseAndSetIfChanged(ref _hasIncomeData, value);
    }

    private bool _hasExpenseData;
    public bool HasExpenseData
    {
        get => _hasExpenseData;
        private set => this.RaiseAndSetIfChanged(ref _hasExpenseData, value);
    }

    // ── Grids (server-side paged) ───────────────────────────────────────────────

    public ObservableCollection<WalletJournalRowVm>     JournalRows     { get; } = new();
    public ObservableCollection<WalletTransactionRowVm> TransactionRows { get; } = new();
    public ObservableCollection<WalletDivisionRowVm>    DivisionRows    { get; } = new();

    public GridPager JournalPager { get; }
    public GridPager TxnPager     { get; }

    public IReadOnlyList<GridSortOption> JournalSortOptions { get; } =
    [
        new("Date: newest first",   "\"Date\" DESC"),
        new("Date: oldest first",   "\"Date\" ASC"),
        new("Amount: high → low",   "CAST(Amount AS REAL) DESC"),
        new("Amount: low → high",   "CAST(Amount AS REAL) ASC"),
        new("Balance: high → low",  "CAST(Balance AS REAL) DESC"),
    ];
    private GridSortOption _selectedJournalSort;
    public GridSortOption SelectedJournalSort
    {
        get => _selectedJournalSort;
        set { this.RaiseAndSetIfChanged(ref _selectedJournalSort, value ?? JournalSortOptions[0]); ReloadJournal(); }
    }

    public IReadOnlyList<GridSortOption> TxnSortOptions { get; } =
    [
        new("Date: newest first",     "\"Date\" DESC"),
        new("Date: oldest first",     "\"Date\" ASC"),
        new("Total: high → low",      "(Quantity * CAST(UnitPrice AS REAL)) DESC"),
        new("Total: low → high",      "(Quantity * CAST(UnitPrice AS REAL)) ASC"),
        new("Unit price: high → low", "CAST(UnitPrice AS REAL) DESC"),
        new("Quantity: high → low",   "\"Quantity\" DESC"),
    ];
    private GridSortOption _selectedTxnSort;
    public GridSortOption SelectedTxnSort
    {
        get => _selectedTxnSort;
        set { this.RaiseAndSetIfChanged(ref _selectedTxnSort, value ?? TxnSortOptions[0]); ReloadTxn(); }
    }

    // ── Journal filters ───────────────────────────────────────────────────────

    private string _journalTypeFilter  = "";
    private string _journalOwnerFilter = "";
    private string _journalDivFilter   = "";
    private DateTime? _journalFromDate;
    private DateTime? _journalThruDate;

    public string JournalTypeFilter
    {
        get => _journalTypeFilter;
        set { this.RaiseAndSetIfChanged(ref _journalTypeFilter, value); DebounceJournal(); }
    }
    public string JournalOwnerFilter
    {
        get => _journalOwnerFilter;
        set { this.RaiseAndSetIfChanged(ref _journalOwnerFilter, value); DebounceJournal(); }
    }
    public string JournalDivFilter
    {
        get => _journalDivFilter;
        set { this.RaiseAndSetIfChanged(ref _journalDivFilter, value); DebounceJournal(); }
    }
    public DateTime? JournalFromDate
    {
        get => _journalFromDate;
        set { this.RaiseAndSetIfChanged(ref _journalFromDate, value); ReloadJournal(); }
    }
    public DateTime? JournalThruDate
    {
        get => _journalThruDate;
        set { this.RaiseAndSetIfChanged(ref _journalThruDate, value); ReloadJournal(); }
    }

    public ReactiveCommand<Unit, Unit> ClearJournalFiltersCommand { get; }

    // ── Transaction filters ───────────────────────────────────────────────────

    private string _txnItemFilter     = "";
    private string _txnDirectionFilter = "All";
    private string _txnLocationFilter = "";
    private string _txnOwnerFilter    = "";
    private string _txnDivFilter      = "";

    public IReadOnlyList<string> DirectionOptions { get; } = ["All", "Buy", "Sell"];

    public string TxnItemFilter
    {
        get => _txnItemFilter;
        set { this.RaiseAndSetIfChanged(ref _txnItemFilter, value); DebounceTxn(); }
    }
    public string TxnDirectionFilter
    {
        get => _txnDirectionFilter;
        set { this.RaiseAndSetIfChanged(ref _txnDirectionFilter, value ?? "All"); ReloadTxn(); }
    }
    public string TxnLocationFilter
    {
        get => _txnLocationFilter;
        set { this.RaiseAndSetIfChanged(ref _txnLocationFilter, value); DebounceTxn(); }
    }
    public string TxnOwnerFilter
    {
        get => _txnOwnerFilter;
        set { this.RaiseAndSetIfChanged(ref _txnOwnerFilter, value); DebounceTxn(); }
    }
    public string TxnDivFilter
    {
        get => _txnDivFilter;
        set { this.RaiseAndSetIfChanged(ref _txnDivFilter, value); DebounceTxn(); }
    }

    public ReactiveCommand<Unit, Unit> ClearTxnFiltersCommand { get; }

    // Reload helpers — reset to page 1 and re-query one grid; text filters are debounced.
    private void ReloadJournal() { if (_initialized) { JournalPager.Reset(); _ = LoadJournalPageAsync(); } }
    private void ReloadTxn()     { if (_initialized) { TxnPager.Reset();     _ = LoadTxnPageAsync(); } }

    private int _journalGen;
    private async void DebounceJournal()
    {
        if (!_initialized) return;
        int gen = ++_journalGen;
        try { await Task.Delay(350); } catch { return; }
        if (gen == _journalGen) ReloadJournal();
    }

    private int _txnGen;
    private async void DebounceTxn()
    {
        if (!_initialized) return;
        int gen = ++_txnGen;
        try { await Task.Delay(350); } catch { return; }
        if (gen == _txnGen) ReloadTxn();
    }

    // ── Status ────────────────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public WalletViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;

        Periods =
        [
            new("Last 24 Hours",  24),
            new("Last 7 Days",    168),
            new("Last 30 Days",   720),
            new("Last 90 Days",   2160),
        ];
        _selectedPeriod = Periods[2];

        JournalPager = new GridPager(LoadJournalPageAsync);
        TxnPager     = new GridPager(LoadTxnPageAsync);
        _selectedJournalSort = JournalSortOptions[0];
        _selectedTxnSort     = TxnSortOptions[0];

        RefreshCommand             = ReactiveCommand.CreateFromTask(LoadAsync);
        ClearJournalFiltersCommand = ReactiveCommand.Create(() =>
        {
            _journalTypeFilter  = ""; this.RaisePropertyChanged(nameof(JournalTypeFilter));
            _journalOwnerFilter = ""; this.RaisePropertyChanged(nameof(JournalOwnerFilter));
            _journalDivFilter   = ""; this.RaisePropertyChanged(nameof(JournalDivFilter));
            _journalFromDate    = null; this.RaisePropertyChanged(nameof(JournalFromDate));
            _journalThruDate    = null; this.RaisePropertyChanged(nameof(JournalThruDate));
            ReloadJournal();
        });
        ClearTxnFiltersCommand = ReactiveCommand.Create(() =>
        {
            _txnItemFilter      = ""; this.RaisePropertyChanged(nameof(TxnItemFilter));
            _txnDirectionFilter = "All"; this.RaisePropertyChanged(nameof(TxnDirectionFilter));
            _txnLocationFilter  = ""; this.RaisePropertyChanged(nameof(TxnLocationFilter));
            _txnOwnerFilter     = ""; this.RaisePropertyChanged(nameof(TxnOwnerFilter));
            _txnDivFilter       = ""; this.RaisePropertyChanged(nameof(TxnDivFilter));
            ReloadTxn();
        });
        _ = InitAsync();
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private async Task InitAsync()
    {
        try
        {
            await using var db   = await _dbFactory.CreateDbContextAsync();
            var chars = await db.Characters.OrderBy(c => c.Name).ToListAsync();
            var corps = await db.Corporations.OrderBy(c => c.Name).ToListAsync();

            var options = new List<WalletOwnerOption>
            {
                new("All Characters & Personal Corps", null, null, false)
            };
            foreach (var c in chars)
                options.Add(new WalletOwnerOption(c.Name, c.Id, "character", false));
            foreach (var corp in corps)
                options.Add(new WalletOwnerOption($"{corp.Name} [{corp.Ticker}]", corp.Id, "corporation", true));

            _owners = options;
            this.RaisePropertyChanged(nameof(Owners));
            _selectedOwner = options[0];
            this.RaisePropertyChanged(nameof(SelectedOwner));
        }
        catch (Exception ex) { _errorLogger.Log("WalletViewModel", "InitAsync", ex); }

        _initialized = true;
        await LoadAsync();
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var owner  = _selectedOwner;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-_selectedPeriod.Hours);
            await using var db = await _dbFactory.CreateDbContextAsync();

            StatusText = "Loading balances...";
            await LoadBalanceAsync(db, owner);

            StatusText = "Building charts...";
            await BuildChartsAsync(db, owner, cutoff);

            if (owner?.IsCorp == true)
            {
                StatusText = "Loading divisions...";
                await LoadDivisionsAsync(db, owner);
            }
            else
            {
                DivisionRows.Clear();
            }

            StatusText = "";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("WalletViewModel", "LoadAsync", ex);
            StatusText = "Error loading wallet data.";
        }
        finally { IsLoading = false; }

        // The Journal and Market Transactions grids are independently server-side paged: on an
        // owner/period change reset both to page 1 and reload the first page.
        JournalPager.Reset();
        TxnPager.Reset();
        await LoadJournalPageAsync();
        await LoadTxnPageAsync();
    }

    private async Task<(List<long> CharIds, List<long> PersonalCorpIds)> GetAllOwnerIdsAsync(AppDbContext db)
    {
        var charIds        = await db.Characters.Select(c => c.Id).ToListAsync();
        var personalCorpIds = await db.Corporations.Where(c => c.IsPersonal).Select(c => (long)c.Id).ToListAsync();
        return (charIds, personalCorpIds);
    }

    private async Task LoadBalanceAsync(AppDbContext db, WalletOwnerOption? owner)
    {
        // Balance stored as TEXT in SQLite; must CAST to REAL for SUM.
        BalanceSummary? result;
        if (owner?.OwnerId != null)
        {
            var oid = owner.OwnerId.Value;
            var ot  = owner.OwnerType!;
            result = await db.Database.SqlQuery<BalanceSummary>(
                $"""
                 SELECT COALESCE(SUM(CAST("Balance" AS REAL)), 0.0) AS "Total"
                 FROM "EsiWalletBalances"
                 WHERE "OwnerId" = {oid} AND "OwnerType" = {ot}
                 """).SingleOrDefaultAsync();
        }
        else
        {
            var (charIds, corpIds) = await GetAllOwnerIdsAsync(db);
            double total = 0.0;
            foreach (var id in charIds)
            {
                var r = await db.Database.SqlQuery<BalanceSummary>(
                    $"""
                     SELECT COALESCE(SUM(CAST("Balance" AS REAL)), 0.0) AS "Total"
                     FROM "EsiWalletBalances"
                     WHERE "OwnerId" = {id} AND "OwnerType" = 'character'
                     """).SingleOrDefaultAsync();
                total += r?.Total ?? 0.0;
            }
            foreach (var id in corpIds)
            {
                var r = await db.Database.SqlQuery<BalanceSummary>(
                    $"""
                     SELECT COALESCE(SUM(CAST("Balance" AS REAL)), 0.0) AS "Total"
                     FROM "EsiWalletBalances"
                     WHERE "OwnerId" = {id} AND "OwnerType" = 'corporation'
                     """).SingleOrDefaultAsync();
                total += r?.Total ?? 0.0;
            }
            result = new BalanceSummary { Total = total };
        }
        BalanceText = FormatIsk((decimal)(result?.Total ?? 0.0)) + " ISK";
    }

    // ── Journal page ────────────────────────────────────────────────────────────
    // Filters, sort and paging all run in SQL against the whole (owner + period) set, so they
    // apply to every row — not just a loaded window. Filter values are parameters; only computed
    // integers / the trusted sort expression are interpolated (hence the EF1002 suppression).
    private async Task LoadJournalPageAsync()
    {
        if (!_initialized) return;
        try
        {
            var owner  = _selectedOwner;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-_selectedPeriod.Hours);
            await using var db = await _dbFactory.CreateDbContextAsync();

            var (where, ps) = await BuildJournalWhereAsync(db, owner, cutoff);
            var pars = ps.ToArray();
            string baseSql = $"SELECT * FROM \"EsiWalletJournal\" WHERE {where}";

#pragma warning disable EF1002
            JournalPager.TotalCount = await db.EsiWalletJournal.FromSqlRaw(baseSql, pars).AsNoTracking().CountAsync();
            JournalPager.ClampToRange();

            var entries = JournalPager.TotalCount == 0
                ? new List<WalletJournalEntry>()
                : await db.EsiWalletJournal.FromSqlRaw(
                        baseSql + $" ORDER BY {_selectedJournalSort.Sql} LIMIT {GridPager.PageSize} OFFSET {JournalPager.Offset}",
                        pars).AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            var names  = await BuildOwnerNamesAsync(db, owner, entries.Select(r => r.OwnerId));
            var divMap = await BuildDivisionMapAsync(db, owner);
            JournalRows.Clear();
            foreach (var r in entries) JournalRows.Add(new WalletJournalRowVm(r, names, divMap));
        }
        catch (Exception ex) { _errorLogger.Log("WalletViewModel", "LoadJournalPageAsync", ex); }
    }

    // Treats a picked calendar date as UTC midnight — a DateTimeOffset with a zero offset can't be
    // built directly from the Local-kind DateTime the date picker returns, so use components.
    private static DateTimeOffset UtcMidnight(DateTime d) =>
        new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);

    private async Task<(string Where, List<object> Parameters)> BuildJournalWhereAsync(
        AppDbContext db, WalletOwnerOption? owner, DateTimeOffset cutoff)
    {
        var parts = new List<string>();
        var ps    = new List<object>();

        if (owner?.OwnerId != null)
        {
            int ti = ps.Count; ps.Add(owner.OwnerType!);       parts.Add($"\"OwnerType\" = {{{ti}}}");
            int oi = ps.Count; ps.Add(owner.OwnerId.Value);    parts.Add($"\"OwnerId\" = {{{oi}}}");
        }
        else
        {
            var (charIds, corpIds) = await GetAllOwnerIdsAsync(db);
            var conds = new List<string>();
            if (charIds.Count > 0) conds.Add($"(OwnerType='character' AND OwnerId IN ({string.Join(",", charIds)}))");
            if (corpIds.Count > 0) conds.Add($"(OwnerType='corporation' AND OwnerId IN ({string.Join(",", corpIds)}))");
            parts.Add(conds.Count > 0 ? "(" + string.Join(" OR ", conds) + ")" : "1=0");
        }

        int ci = ps.Count; ps.Add(cutoff); parts.Add($"\"Date\" >= {{{ci}}}");

        var typeF = _journalTypeFilter.Trim();
        if (typeF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + typeF.Replace(' ', '_') + "%");
            parts.Add($"RefType LIKE {{{i}}}");
        }

        var ownerF = _journalOwnerFilter.Trim();
        if (ownerF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + ownerF + "%");
            int j = ps.Count; ps.Add("%" + ownerF + "%");
            parts.Add($"\"OwnerId\" IN (SELECT \"Id\" FROM \"Characters\" WHERE \"Name\" LIKE {{{i}}} "
                    + $"UNION SELECT \"Id\" FROM \"Corporations\" WHERE \"Name\" LIKE {{{j}}})");
        }

        var divF = _journalDivFilter.Trim();
        if (divF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + divF + "%");
            int j = ps.Count; ps.Add("%" + divF + "%");
            parts.Add($"(\"Division\" IN (SELECT \"Division\" FROM \"EsiCorpDivisions\" WHERE \"DivisionType\"='wallet' AND \"Name\" LIKE {{{i}}}) "
                    + $"OR CAST(\"Division\" AS TEXT) LIKE {{{j}}})");
        }

        if (_journalFromDate is DateTime fd)
        { int i = ps.Count; ps.Add(UtcMidnight(fd)); parts.Add($"\"Date\" >= {{{i}}}"); }
        if (_journalThruDate is DateTime td)
        { int i = ps.Count; ps.Add(UtcMidnight(td.AddDays(1))); parts.Add($"\"Date\" < {{{i}}}"); }

        return (string.Join(" AND ", parts), ps);
    }

    // ── Transactions page ─────────────────────────────────────────────────────────
    private async Task LoadTxnPageAsync()
    {
        if (!_initialized) return;
        try
        {
            var owner  = _selectedOwner;
            var cutoff = DateTimeOffset.UtcNow.AddHours(-_selectedPeriod.Hours);
            await using var db = await _dbFactory.CreateDbContextAsync();

            var (baseInner, ps) = await BuildTxnBaseAsync(db, owner, cutoff);
            string filter = BuildTxnFilter(ps);
            var pars = ps.ToArray();
            string wrapped = $"SELECT * FROM ({baseInner}) x WHERE {filter}";

#pragma warning disable EF1002
            TxnPager.TotalCount = await db.EsiWalletTransactions.FromSqlRaw(wrapped, pars).AsNoTracking().CountAsync();
            TxnPager.ClampToRange();

            var rows = TxnPager.TotalCount == 0
                ? new List<WalletTransaction>()
                : await db.EsiWalletTransactions.FromSqlRaw(
                        wrapped + $" ORDER BY {_selectedTxnSort.Sql} LIMIT {GridPager.PageSize} OFFSET {TxnPager.Offset}",
                        pars).AsNoTracking().ToListAsync();
#pragma warning restore EF1002

            var typeIds   = rows.Select(r => r.TypeId).Distinct().ToList();
            var typeNames = await db.SdeTypes.Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            var ownerNames    = await BuildOwnerNamesAsync(db, owner, rows.Select(r => r.OwnerId));
            var locationNames = await BuildLocationNamesAsync(db, rows.Select(r => r.LocationId));
            var divMap        = await BuildDivisionMapAsync(db, owner);

            TransactionRows.Clear();
            foreach (var r in rows)
                TransactionRows.Add(new WalletTransactionRowVm(r, typeNames, ownerNames, locationNames, divMap));
        }
        catch (Exception ex) { _errorLogger.Log("WalletViewModel", "LoadTxnPageAsync", ex); }
    }

    // Base row set (owner + period), deduplicated across owners so a shared TransactionId shows once.
    private async Task<(string Sql, List<object> Parameters)> BuildTxnBaseAsync(
        AppDbContext db, WalletOwnerOption? owner, DateTimeOffset cutoff)
    {
        var ps = new List<object>();
        if (owner?.OwnerId != null)
        {
            int oi = ps.Count; ps.Add(owner.OwnerId.Value);
            int ti = ps.Count; ps.Add(owner.OwnerType!);
            int ci = ps.Count; ps.Add(cutoff);
            return ($"SELECT * FROM \"EsiWalletTransactions\" WHERE \"OwnerId\" = {{{oi}}} AND \"OwnerType\" = {{{ti}}} AND \"Date\" >= {{{ci}}}", ps);
        }

        var (charIds, corpIds) = await GetAllOwnerIdsAsync(db);
        var conds = new List<string>();
        if (charIds.Count > 0) conds.Add($"(OwnerType='character' AND OwnerId IN ({string.Join(",", charIds)}))");
        if (corpIds.Count > 0) conds.Add($"(OwnerType='corporation' AND OwnerId IN ({string.Join(",", corpIds)}))");
        string ownerCond = conds.Count > 0 ? "(" + string.Join(" OR ", conds) + ")" : "1=0";
        int cix = ps.Count; ps.Add(cutoff);

        // Corp row wins over the character copy of the same transaction.
        string sql =
            "SELECT \"TransactionId\",\"OwnerId\",\"OwnerType\",\"Division\",\"Date\",\"ClientId\"," +
            "\"LocationId\",\"Quantity\",\"TypeId\",\"UnitPrice\",\"IsBuy\",\"IsPersonal\",\"JournalRefId\" " +
            "FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY \"TransactionId\" " +
            "ORDER BY CASE WHEN \"OwnerType\"='corporation' THEN 0 ELSE 1 END) AS rn " +
            $"FROM \"EsiWalletTransactions\" WHERE {ownerCond} AND \"Date\" >= {{{cix}}}) WHERE rn = 1";
        return (sql, ps);
    }

    private string BuildTxnFilter(List<object> ps)
    {
        var parts = new List<string>();

        var itemF = _txnItemFilter.Trim();
        if (itemF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + itemF + "%");
            parts.Add($"x.\"TypeId\" IN (SELECT \"TypeId\" FROM \"SdeTypes\" WHERE \"Name\" LIKE {{{i}}})");
        }

        if (_txnDirectionFilter == "Buy")  parts.Add("x.\"IsBuy\" = 1");
        else if (_txnDirectionFilter == "Sell") parts.Add("x.\"IsBuy\" = 0");

        var locF = _txnLocationFilter.Trim();
        if (locF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + locF + "%");
            int j = ps.Count; ps.Add("%" + locF + "%");
            parts.Add($"x.\"LocationId\" IN (SELECT \"StationId\" FROM \"SdeStations\" WHERE \"Name\" LIKE {{{i}}} "
                    + $"UNION SELECT \"StructureId\" FROM \"EsiStructureNames\" WHERE \"Name\" LIKE {{{j}}})");
        }

        var ownerF = _txnOwnerFilter.Trim();
        if (ownerF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + ownerF + "%");
            int j = ps.Count; ps.Add("%" + ownerF + "%");
            parts.Add($"x.\"OwnerId\" IN (SELECT \"Id\" FROM \"Characters\" WHERE \"Name\" LIKE {{{i}}} "
                    + $"UNION SELECT \"Id\" FROM \"Corporations\" WHERE \"Name\" LIKE {{{j}}})");
        }

        var divF = _txnDivFilter.Trim();
        if (divF.Length > 0)
        {
            int i = ps.Count; ps.Add("%" + divF + "%");
            int j = ps.Count; ps.Add("%" + divF + "%");
            parts.Add($"(x.\"Division\" IN (SELECT \"Division\" FROM \"EsiCorpDivisions\" WHERE \"DivisionType\"='wallet' AND \"Name\" LIKE {{{i}}}) "
                    + $"OR CAST(x.\"Division\" AS TEXT) LIKE {{{j}}})");
        }

        return parts.Count > 0 ? string.Join(" AND ", parts) : "1=1";
    }

    private async Task<Dictionary<(long, int), string>> BuildDivisionMapAsync(
        AppDbContext db, WalletOwnerOption? owner)
    {
        var map = new Dictionary<(long, int), string>();
        List<CorpDivision> divs;
        if (owner?.IsCorp == true && owner.OwnerId.HasValue)
        {
            divs = await db.EsiCorpDivisions
                .Where(d => d.CorporationId == owner.OwnerId.Value && d.DivisionType == "wallet")
                .ToListAsync();
        }
        else
        {
            var personalIds = await db.Corporations
                .Where(c => c.IsPersonal).Select(c => (long)c.Id).ToListAsync();
            divs = personalIds.Count > 0
                ? await db.EsiCorpDivisions
                    .Where(d => personalIds.Contains(d.CorporationId) && d.DivisionType == "wallet")
                    .ToListAsync()
                : [];
        }
        foreach (var d in divs)
            if (!string.IsNullOrWhiteSpace(d.Name))
                map[(d.CorporationId, d.Division)] = d.Name;
        return map;
    }

    private async Task BuildChartsAsync(AppDbContext db, WalletOwnerOption? owner, DateTimeOffset cutoff)
    {
        var groups = new List<(string RefType, decimal Total)>();

        if (owner?.OwnerId != null)
        {
            var oid = owner.OwnerId.Value;
            var ot  = owner.OwnerType!;
            var rows = await db.Database.SqlQuery<JournalGroup>(
                $"""
                 SELECT "RefType", COALESCE(SUM(CAST("Amount" AS REAL)), 0.0) AS "TotalAmount"
                 FROM "EsiWalletJournal"
                 WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                 GROUP BY "RefType"
                 """).ToListAsync();
            groups.AddRange(rows.Select(r => (r.RefType, (decimal)r.TotalAmount)));
        }
        else
        {
            var chars = await db.Characters
                .Select(c => new { Id = c.Id, Type = "character" }).ToListAsync();
            var corps = await db.Corporations
                .Where(c => c.IsPersonal)
                .Select(c => new { Id = (long)c.Id, Type = "corporation" }).ToListAsync();

            foreach (var c in chars)
            {
                var oid = c.Id; var ot = c.Type;
                var rows = await db.Database.SqlQuery<JournalGroup>(
                    $"""
                     SELECT "RefType", COALESCE(SUM(CAST("Amount" AS REAL)), 0.0) AS "TotalAmount"
                     FROM "EsiWalletJournal"
                     WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                     GROUP BY "RefType"
                     """).ToListAsync();
                groups.AddRange(rows.Select(r => (r.RefType, (decimal)r.TotalAmount)));
            }
            foreach (var c in corps)
            {
                var oid = c.Id; var ot = c.Type;
                var rows = await db.Database.SqlQuery<JournalGroup>(
                    $"""
                     SELECT "RefType", COALESCE(SUM(CAST("Amount" AS REAL)), 0.0) AS "TotalAmount"
                     FROM "EsiWalletJournal"
                     WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                     GROUP BY "RefType"
                     """).ToListAsync();
                groups.AddRange(rows.Select(r => (r.RefType, (decimal)r.TotalAmount)));
            }
        }

        var byType = groups
            .GroupBy(g => g.RefType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Total), StringComparer.OrdinalIgnoreCase);

        var bountyTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bounty_prizes", "npc_bounty", "bounty_prize", "corporate_reward", "agent_bounty_prize" };
        var contractIncTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "contract_reward", "contract_price", "contract_price_payment_corp",
              "contract_reward_refund", "contract_auction_sold" };
        var knownExpenseTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "broker_fee", "brokers_fee", "transaction_tax",
              "industry_job_tax", "manufacturing_tax",
              "contract_deposit", "contract_sales_tax", "contract_deposit_sales_tax",
              "planetary_import_tax", "planetary_export_tax", "planetary_construction" };

        decimal mktSell = 0m, mktBuy = 0m;
        decimal npcBounty = 0m, contractInc = 0m, contractExp = 0m, otherIncome = 0m;
        decimal brokerFees = 0m, txnTax = 0m, indyTax = 0m, otherExpense = 0m;

        foreach (var (refType, total) in byType)
        {
            if (refType == "market_transaction")
            {
                if (total > 0) mktSell += total;
                else           mktBuy  += Math.Abs(total);
            }
            else if (bountyTypes.Contains(refType))
            {
                if (total > 0) npcBounty += total;
            }
            else if (contractIncTypes.Contains(refType))
            {
                if (total > 0) contractInc += total;
                else           contractExp += Math.Abs(total);
            }
            else if (refType is "broker_fee" or "brokers_fee")
                brokerFees += Math.Abs(total);
            else if (refType == "transaction_tax")
                txnTax += Math.Abs(total);
            else if (refType is "industry_job_tax" or "manufacturing_tax")
                indyTax += Math.Abs(total);
            else if (!knownExpenseTypes.Contains(refType))
            {
                if (total > 0) otherIncome  += total;
                else           otherExpense += Math.Abs(total);
            }
        }

        BuildPieCharts(mktSell, npcBounty, contractInc, otherIncome,
                       mktBuy, brokerFees, txnTax, indyTax, otherExpense, contractExp);
    }

    private void BuildPieCharts(
        decimal mktSell,   decimal npcBounty,  decimal contractInc, decimal otherIncome,
        decimal mktBuy,    decimal brokerFee,  decimal txnTax,      decimal indyTax,
        decimal otherExpense, decimal contractExp)
    {
        static ISeries Slice(string name, decimal value, SKColor color) =>
            new PieSeries<double>
            {
                Name            = name,
                Values          = [(double)value],
                Fill            = new SolidColorPaint(color),
                Stroke          = null,
                Pushout         = 3,
                InnerRadius     = 40,
                AnimationsSpeed = TimeSpan.Zero,
                EasingFunction  = null,
                ToolTipLabelFormatter = cp =>
                    $"{name}: {FormatIsk((decimal)cp.Coordinate.PrimaryValue)} ISK"
            };

        var incSlices = new List<ISeries>();
        if (mktSell     > 0) incSlices.Add(Slice("Market Sales",      mktSell,     new SKColor(200, 168,  75)));
        if (npcBounty   > 0) incSlices.Add(Slice("NPC Bounties",      npcBounty,   new SKColor(110, 190, 100)));
        if (contractInc > 0) incSlices.Add(Slice("Contract Sales",    contractInc, new SKColor( 91, 155, 213)));
        if (otherIncome > 0) incSlices.Add(Slice("Other Income",      otherIncome, new SKColor(155, 120, 200)));

        var expSlices = new List<ISeries>();
        if (mktBuy      > 0) expSlices.Add(Slice("Market Purchases",   mktBuy,       new SKColor(200,  90,  90)));
        if (contractExp > 0) expSlices.Add(Slice("Contract Purchases", contractExp,  new SKColor(200, 120, 160)));
        if (brokerFee   > 0) expSlices.Add(Slice("Broker Fees",        brokerFee,    new SKColor(220, 150,  60)));
        if (txnTax      > 0) expSlices.Add(Slice("Transaction Tax",    txnTax,       new SKColor(180, 180,  60)));
        if (indyTax     > 0) expSlices.Add(Slice("Industry Tax",       indyTax,      new SKColor(100, 170, 200)));
        if (otherExpense > 0) expSlices.Add(Slice("Other Expenses",    otherExpense, new SKColor(160, 100, 120)));

        IncomeSeries   = incSlices.Count > 0 ? incSlices : [];
        ExpenseSeries  = expSlices.Count > 0 ? expSlices : [];
        HasIncomeData  = incSlices.Count > 0;
        HasExpenseData = expSlices.Count > 0;

        var incTotal = mktSell + npcBounty + contractInc + otherIncome;
        var expTotal = mktBuy + contractExp + brokerFee + txnTax + indyTax + otherExpense;
        IncomeTotalText  = incTotal  > 0 ? FormatIsk(incTotal)  + " ISK" : "";
        ExpenseTotalText = expTotal  > 0 ? FormatIsk(expTotal)  + " ISK" : "";
    }

    private async Task LoadDivisionsAsync(AppDbContext db, WalletOwnerOption owner)
    {
        var corpId = owner.OwnerId!.Value;

        var divNames = await db.EsiCorpDivisions
            .Where(d => d.CorporationId == corpId && d.DivisionType == "wallet")
            .ToDictionaryAsync(d => d.Division, d => d.Name);

        var balances = await db.EsiWalletBalances
            .Where(b => b.OwnerId == corpId && b.OwnerType == "corporation")
            .ToDictionaryAsync(b => b.Division, b => b.Balance);

        DivisionRows.Clear();
        for (int i = 1; i <= 7; i++)
        {
            if (!balances.ContainsKey(i) && !divNames.ContainsKey(i)) continue;
            divNames.TryGetValue(i, out var rawName);
            var name = string.IsNullOrWhiteSpace(rawName)
                ? (i == 1 ? "Master Wallet" : $"Division {i}")
                : rawName;
            var balance = balances.TryGetValue(i, out var b) ? b : 0m;
            DivisionRows.Add(new WalletDivisionRowVm(i, name, balance));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<Dictionary<long, string>> BuildLocationNamesAsync(
        AppDbContext db, IEnumerable<long> locationIds)
    {
        var ids    = locationIds.Distinct().ToList();
        var result = new Dictionary<long, string>();

        // NPC stations: IDs are 32-bit ints stored as long (60,000,000 – 64,000,000 range)
        var stationIds = ids.Where(id => id < 100_000_000L).Select(id => (int)id).ToList();
        if (stationIds.Count > 0)
        {
            var stations = await db.SdeStations
                .Where(s => stationIds.Contains(s.StationId))
                .ToDictionaryAsync(s => (long)s.StationId, s => s.Name);
            foreach (var kv in stations) result[kv.Key] = kv.Value;
        }

        // Player structures: IDs > 1,000,000,000
        var structureIds = ids.Where(id => id >= 100_000_000L).ToList();
        if (structureIds.Count > 0)
        {
            var structures = await db.EsiStructureNames
                .Where(s => structureIds.Contains(s.StructureId))
                .ToDictionaryAsync(s => s.StructureId, s => s.Name);
            foreach (var kv in structures) result[kv.Key] = kv.Value;
        }

        return result;
    }

    private async Task<Dictionary<long, string>> BuildOwnerNamesAsync(
        AppDbContext db, WalletOwnerOption? owner, IEnumerable<long> ownerIds)
    {
        if (owner?.OwnerId != null) return [];

        var ids    = ownerIds.Distinct().ToList();
        var result = new Dictionary<long, string>();

        var chars = await db.Characters
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);
        foreach (var kv in chars) result[kv.Key] = kv.Value;

        var intIds = ids.Select(id => (int)id).ToList();
        var corps  = await db.Corporations
            .Where(c => intIds.Contains(c.Id))
            .ToDictionaryAsync(c => (long)c.Id, c => c.Name);
        foreach (var kv in corps) result[kv.Key] = kv.Value;

        return result;
    }

    private static string FormatIsk(decimal v)
    {
        var abs = Math.Abs(v);
        if (abs >= 1_000_000_000m) return $"{v / 1_000_000_000m:F2}B";
        if (abs >= 1_000_000m)     return $"{v / 1_000_000m:F2}M";
        if (abs >= 1_000m)         return $"{v / 1_000m:F1}K";
        return $"{v:N0}";
    }

    private sealed class JournalGroup
    {
        public string RefType     { get; set; } = "";
        public double TotalAmount { get; set; }
    }

    private sealed class BalanceSummary
    {
        public double Total { get; set; }
    }
}
