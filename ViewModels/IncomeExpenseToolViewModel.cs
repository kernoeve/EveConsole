using System.Collections.ObjectModel;
using EveConsole.Data;
using EveConsole.Localization;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// The Income &amp; Expense tool: one owner or all of them, over a date range, as a summary (the
/// categories and the daily chart) and as the journal entries themselves — income on one tab,
/// expense on another.
///
/// <para>⚠️ Not the Overview's Income &amp; Expense section, which follows the Overview's period
/// and has no owner picker. That keeps its own <see cref="IncomeExpenseViewModel"/>; this one
/// holds a second, scoped one as its Summary, so choosing an owner here never changes the
/// Overview.</para>
///
/// <para>The entries leave out exactly what the summary does — ISK moved between the player's own
/// wallets (see <see cref="WalletJournalTotals"/>) — so a tab's total is the summary's figure.</para>
/// </summary>
public sealed class IncomeExpenseToolViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errorLogger;

    public IncomeExpenseViewModel Summary { get; }
    public IncomeExpenseJournalVm Income  { get; }
    public IncomeExpenseJournalVm Expense { get; }

    /// <summary>All characters and personal corporations together, then each on its own.</summary>
    public ObservableCollection<WalletOwnerOption> Owners { get; } = [];

    private WalletOwnerOption? _selectedOwner;
    public WalletOwnerOption? SelectedOwner
    {
        get => _selectedOwner;
        set
        {
            // A refilled or detaching ComboBox sets null; that is not a choice.
            if (value is null || ReferenceEquals(value, _selectedOwner)) return;
            this.RaiseAndSetIfChanged(ref _selectedOwner, value);
            Reload();
        }
    }

    private DateTime? _fromDate = DateTime.UtcNow.Date.AddDays(-DefaultDays);
    /// <summary>The first day, inclusive, in EVE time. Cleared, the range has no start.</summary>
    public DateTime? FromDate
    {
        get => _fromDate;
        set { if (value == _fromDate) return; this.RaiseAndSetIfChanged(ref _fromDate, value); Reload(); }
    }

    private DateTime? _thruDate = DateTime.UtcNow.Date;
    /// <summary>The last day, inclusive, in EVE time. Cleared, the range runs to now.</summary>
    public DateTime? ThruDate
    {
        get => _thruDate;
        set { if (value == _thruDate) return; this.RaiseAndSetIfChanged(ref _thruDate, value); Reload(); }
    }

    /// <summary>How far back the range starts when the tool opens.</summary>
    public const int DefaultDays = 30;

    /// <summary>What the current choices ask for, as the grids query it.</summary>
    internal IncomeExpenseScope? Scope { get; private set; }

    private bool _initialized;

    public IncomeExpenseToolViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        Summary = new IncomeExpenseViewModel(dbFactory, errorLogger, scoped: true);
        Income  = new IncomeExpenseJournalVm(this, dbFactory, errorLogger, income: true);
        Expense = new IncomeExpenseJournalVm(this, dbFactory, errorLogger, income: false);

        OwnerList.Changed += () => _ = LoadOwnersAsync();
        _ = LoadOwnersAsync();
    }

    private async Task LoadOwnersAsync()
    {
        try
        {
            var (chars, corps) = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var c = await db.Characters.AsNoTracking().Where(x => x.RefreshToken != "").OrderBy(x => x.Name)
                                .Select(x => new { x.Id, x.Name }).ToListAsync();
                var p = await db.Corporations.AsNoTracking().Where(x => x.IsPersonal).OrderBy(x => x.Name)
                                .Select(x => new { x.Id, x.Name, x.Ticker }).ToListAsync();
                return (c, p);
            });

            var options = new List<WalletOwnerOption> { new(FinanceText.OwnerAllCharsCorps, null, null, false) };
            options.AddRange(chars.Select(c => new WalletOwnerOption(c.Name, c.Id, "character", false)));
            options.AddRange(corps.Select(c => new WalletOwnerOption($"{c.Name} [{c.Ticker}]", c.Id, "corporation", true)));
            ListSync.Sync(Owners, options, o => (o.OwnerType, o.OwnerId));

            // Everybody at first; afterwards the user's choice, unless what they chose has gone.
            if (_selectedOwner is null || !Owners.Contains(_selectedOwner))
            {
                _selectedOwner = Owners[0];
                this.RaisePropertyChanged(nameof(SelectedOwner));
            }
        }
        catch (Exception ex)
        {
            _errorLogger.Log("IncomeExpenseToolViewModel", "LoadOwnersAsync", ex);
        }

        _initialized = true;
        Reload();
    }

    /// <summary>A day picked in the calendar, as the start of that day in EVE time.</summary>
    private static DateTimeOffset UtcMidnight(DateTime d) => new(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);

    private void Reload()
    {
        if (!_initialized) return;
        var owner = _selectedOwner is { OwnerId: { } id, OwnerType: { } type } ? (type, id) : ((string, long)?)null;
        var from  = _fromDate is { } f ? UtcMidnight(f) : (DateTimeOffset?)null;
        var until = _thruDate is { } t ? UtcMidnight(t.AddDays(1)) : (DateTimeOffset?)null;
        Scope = new IncomeExpenseScope(owner, from, until);

        Summary.SetScope(owner, from, until);
        Income.Reload();
        Expense.Reload();
    }
}

/// <summary>One owner or all of them, from a moment (inclusive) up to another (exclusive).</summary>
internal sealed record IncomeExpenseScope((string OwnerType, long OwnerId)? Owner, DateTimeOffset? From, DateTimeOffset? Until);

/// <summary>
/// One tab of the tool's entries: the income (amounts above zero) or the expense (below), paged at
/// the database. A month on a busy corporation is a hundred thousand rows; only a page is ever
/// read into the grid.
/// </summary>
public sealed class IncomeExpenseJournalVm : ReactiveObject
{
    private readonly IncomeExpenseToolViewModel      _tool;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly bool                            _income;

    public GridPager Pager { get; }

    private IReadOnlyList<WalletJournalRowVm> _rows = [];
    /// <summary>The page on show, assigned whole: one change, not one per row.</summary>
    public IReadOnlyList<WalletJournalRowVm> Rows { get => _rows; private set => this.RaiseAndSetIfChanged(ref _rows, value); }

    /// <summary>Largest means the most ISK either way: an expense's amount is negative, so the
    /// order is by its size, not its sign.</summary>
    public IReadOnlyList<GridSortOption> SortOptions { get; } =
    [
        new(FinanceText.SortDateNewest,    "\"Date\" DESC"),
        new(FinanceText.SortDateOldest,    "\"Date\" ASC"),
        new(FinanceText.SortAmountHighLow, "ABS(CAST(\"Amount\" AS DOUBLE PRECISION)) DESC"),
        new(FinanceText.SortAmountLowHigh, "ABS(CAST(\"Amount\" AS DOUBLE PRECISION)) ASC"),
    ];

    private GridSortOption _selectedSort;
    public GridSortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedSort)) return;
            this.RaiseAndSetIfChanged(ref _selectedSort, value);
            Reload();
        }
    }

    private string _totalText = "";
    /// <summary>"1,234 entries · 5.67B ISK": every entry in range, not only the page.</summary>
    public string TotalText { get => _totalText; private set => this.RaiseAndSetIfChanged(ref _totalText, value); }

    /// <summary>The figure itself, for checking against the summary.</summary>
    public decimal Total { get; private set; }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    internal IncomeExpenseJournalVm(IncomeExpenseToolViewModel tool, IDbContextFactory<AppDbContext> dbFactory,
                                    AppErrorLogger errorLogger, bool income)
    {
        _tool        = tool;
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _income      = income;
        _selectedSort = SortOptions[0];
        Pager = new GridPager(LoadPageAsync);
    }

    public void Reload() { Pager.Reset(); _ = LoadPageAsync(); }

    // ⚠️ The newest request wins: a page that comes back after the scope moved on is dropped.
    private int _generation;

    private async Task LoadPageAsync()
    {
        if (_tool.Scope is not { } scope) return;
        var gen = ++_generation;
        var sort = _selectedSort.Sql;
        IsLoading = true;
        try
        {
            var (count, sum, entries, owners, divisions) = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync();
                var (where, ps) = await WhereAsync(db, scope);
                var pars = ps.ToArray();

#pragma warning disable EF1002, EF1003
                // Spliced: the owner ids and the internal-transfer filter are the database's own
                // integers and this class's own SQL; every value that varies is a parameter.
                var totals = await db.Database.SqlQueryRaw<TotalRow>(
                    $"SELECT COUNT(*) AS \"Count\", COALESCE(SUM(CAST(\"Amount\" AS DOUBLE PRECISION)), 0.0) AS \"Sum\" FROM \"EsiWalletJournal\" WHERE {where}",
                    pars).SingleAsync();
                var page = totals.Count == 0 ? [] : await db.EsiWalletJournal.FromSqlRaw(
                        $"SELECT * FROM \"EsiWalletJournal\" WHERE {where} ORDER BY {sort} LIMIT {GridPager.PageSize} OFFSET {PagerOffset(totals.Count)}",
                        pars).AsNoTracking().ToListAsync();
#pragma warning restore EF1002, EF1003

                var names = await OwnerNamesAsync(db, page.Select(e => e.OwnerId));
                var divs  = await DivisionNamesAsync(db);
                return ((int)totals.Count, (decimal)totals.Sum, page, names, divs);
            });
            if (gen != _generation) return;

            Pager.TotalCount = count;
            Pager.ClampToRange();
            Rows  = entries.Select(e => new WalletJournalRowVm(e, owners, divisions)).ToList();
            Total = Math.Abs(sum);
            TotalText = Plurals.Format(FinanceText.ResourceManager, nameof(FinanceText.JournalEntriesTotalOther), count,
                                       MarketFmt.Isk((double)Total));
        }
        catch (Exception ex)
        {
            _errorLogger.Log("IncomeExpenseToolViewModel", _income ? "income entries" : "expense entries", ex);
            TotalText = AppErrorLogger.Line("Error loading the journal", ex);
        }
        finally { if (gen == _generation) IsLoading = false; }
    }

    /// <summary>The page's offset, with the page pulled back inside a total that has shrunk.</summary>
    private int PagerOffset(long total)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)GridPager.PageSize));
        return (Math.Min(Pager.CurrentPage, pages) - 1) * GridPager.PageSize;
    }

    /// <summary>The scope as SQL: the owner or owners, the dates, the sign, and no transfers
    /// between the player's own wallets.</summary>
    private async Task<(string Where, List<object> Parameters)> WhereAsync(AppDbContext db, IncomeExpenseScope scope)
    {
        var parts = new List<string>();
        var ps    = new List<object>();

        var owners = scope.Owner is { } one ? [one] : await IncomeExpenseViewModel.AllOwnersAsync(db);
        var chars  = owners.Where(o => o.OwnerType == "character").Select(o => o.OwnerId).ToList();
        var corps  = owners.Where(o => o.OwnerType == "corporation").Select(o => o.OwnerId).ToList();
        var conds  = new List<string>();
        if (chars.Count > 0) conds.Add($"(\"OwnerType\" = 'character' AND \"OwnerId\" IN ({string.Join(",", chars)}))");
        if (corps.Count > 0) conds.Add($"(\"OwnerType\" = 'corporation' AND \"OwnerId\" IN ({string.Join(",", corps)}))");
        parts.Add(conds.Count > 0 ? "(" + string.Join(" OR ", conds) + ")" : "1=0");

        if (scope.From  is { } from)  { parts.Add($"\"Date\" >= {{{ps.Count}}}"); ps.Add(from); }
        if (scope.Until is { } until) { parts.Add($"\"Date\" < {{{ps.Count}}}");  ps.Add(until); }

        parts.Add(_income ? "CAST(\"Amount\" AS DOUBLE PRECISION) > 0" : "CAST(\"Amount\" AS DOUBLE PRECISION) < 0");

        // The same exclusion the summary's totals make, so the two agree to the ISK.
        var where = string.Join(" AND ", parts)
                  + WalletJournalTotals.ExcludeInternalTransfers(await WalletJournalTotals.PlayerPartyIdsAsync(db));
        return (where, ps);
    }

    private static async Task<Dictionary<long, string>> OwnerNamesAsync(AppDbContext db, IEnumerable<long> ownerIds)
    {
        var ids    = ownerIds.Distinct().ToList();
        var result = new Dictionary<long, string>();
        foreach (var kv in await db.Characters.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name))
            result[kv.Key] = kv.Value;
        var intIds = ids.Select(id => (int)id).ToList();
        foreach (var kv in await db.Corporations.AsNoTracking().Where(c => intIds.Contains(c.Id)).ToDictionaryAsync(c => (long)c.Id, c => c.Name))
            result[kv.Key] = kv.Value;
        return result;
    }

    private static async Task<Dictionary<(long, int), string>> DivisionNamesAsync(AppDbContext db)
    {
        var personal = await db.Corporations.AsNoTracking().Where(c => c.IsPersonal).Select(c => (long)c.Id).ToListAsync();
        var map = new Dictionary<(long, int), string>();
        if (personal.Count == 0) return map;
        foreach (var d in await db.EsiCorpDivisions.AsNoTracking()
                     .Where(d => personal.Contains(d.CorporationId) && d.DivisionType == "wallet").ToListAsync())
            if (!string.IsNullOrWhiteSpace(d.Name))
                map[(d.CorporationId, d.Division)] = d.Name;
        return map;
    }

    private sealed class TotalRow { public long Count { get; set; } public double Sum { get; set; } }
}
