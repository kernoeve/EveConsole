using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Media;
using EveConsole.Data;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;

namespace EveConsole.ViewModels;

// Shared profit-colour brushes for the sales grids.
internal static class ProfitBrushes
{
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#4caf50"));
    public static readonly IBrush Red   = new SolidColorBrush(Color.Parse("#e05252"));
    public static readonly IBrush Gray  = new SolidColorBrush(Color.Parse("#888899"));
}

// One sale on the Sales Tracker grid (a market transaction or a contract sale).
// ReactiveObject so the main grid's Profit columns refresh live when the cost basis changes.
public class SaleRowVm : ReactiveObject
{
    public DateTimeOffset When { get; }
    public long   WhenSort { get; }
    public string WhenText { get; }
    public string Kind      { get; }   // "Market" or "Contract"

    /// <summary>
    /// Opens the contract this sale came from, in the Contracts tool.
    ///
    /// <para>Does nothing for a market sale: <see cref="SaleId"/> is a wallet transaction id
    /// there, and passing one to the contract lookup would open somebody else's contract.</para>
    /// </summary>
    public void OpenContract()
    {
        if (Kind == "Contract" && SaleId is > 0 and <= int.MaxValue)
            EntityNavigator.Instance.Contract((int)SaleId);
    }

    /// <summary>
    /// The description typed on the contract this sale came from.
    ///
    /// <para>Empty for a market sale, which has nowhere to put one — an order fills against
    /// whoever is buying and carries no message from either side.</para>
    /// </summary>
    public string Note { get; } = "";

    /// <summary>Wallet transaction id for a market sale, contract id for a contract sale.
    /// Unique only together with <see cref="Kind"/>.</summary>
    public long SaleId { get; }

    /// <summary>
    /// Marked by the user as not a profit-making sale. Such rows are left out of every profit
    /// figure — the rollups here, the Sale Listing tools, the Overview — and appear in the grid
    /// below only when it is asked to show them.
    /// </summary>
    private bool _notForProfit;
    public bool NotForProfit
    {
        get => _notForProfit;
        set
        {
            this.RaiseAndSetIfChanged(ref _notForProfit, value);
            this.RaisePropertyChanged(nameof(NotForProfitMark));
        }
    }

    /// <summary>A marker in the grid's first column, so a shown-but-not-counted row is obvious.</summary>
    public string NotForProfitMark => NotForProfit ? "∅" : "";
    public string OwnerType { get; }   // "character" or "corporation" (for filtering)
    public long   OwnerId   { get; }
    public bool   OwnerIsPersonal { get; }
    public string Owner    { get; }
    public string Location { get; }
    public string Buyer    { get; }
    public string Items    { get; }
    public string Units    { get; }
    public string Total  { get; } public double TotalRaw  { get; }
    public string Build  { get; } public double BuildRaw  { get; }
    public string Market { get; } public double MarketRaw { get; }
    public string Profit    { get; private set; } = "—"; public double ProfitRaw    { get; private set; } = double.MinValue;
    public string ProfitPct { get; private set; } = "—"; public double ProfitPctRaw { get; private set; } = double.MinValue;

    // Nullable cost bases (null when no snapshot was available) — used by the Sale Listing
    // tools to compute profit against build cost or market value.
    public double? BuildOrNull  { get; }
    public double? MarketOrNull { get; }

    // Item type and its market group two levels up (e.g. Revelation → "Standard Dreadnoughts"),
    // used by the Sales Tracker rollup grids.
    public int    TypeId      { get; }
    public string MarketGroup { get; }

    // ── Where each name goes when clicked ─────────────────────────────────────
    //
    // Ids alongside the names, so a row is not just a sentence about a sale but a way into the
    // four things it mentions. Nothing here is displayed; it exists to make the text clickable.
    public long       LocationId        { get; }
    /// <summary>NPC station rather than player structure — the two have different browsers.</summary>
    public bool       LocationIsStation { get; }
    public long       BuyerId           { get; }
    public EntityKind BuyerKind         { get; }

    public bool HasOwnerLink    => OwnerId    > 0;
    public bool HasLocationLink => LocationId > 0 && Location.Length > 0;
    public bool HasBuyerLink    => BuyerId    > 0 && Buyer.Length    > 0;
    public bool HasItemLink     => TypeId     > 0;

    // ⚠️ Routed through the shared EntityNavigator rather than a callback threaded in from the
    // host. These rows are built inside SalesQuery and rendered by the Sales Tracker, both Sale
    // Listing tools and the Overview; a per-host callback would be four copies to keep in step.
    public void OpenOwner() => EntityNavigator.Instance.Entity(
        OwnerType == "corporation" ? EntityKind.PlayerCorp : EntityKind.Pilot, OwnerId);

    /// <summary>A station goes to the entity browser, a player structure to its own tool.</summary>
    public void OpenLocation()
    {
        if (LocationIsStation) EntityNavigator.Instance.Entity(EntityKind.Station, LocationId);
        else                   EntityNavigator.Instance.Structure(LocationId);
    }

    public void OpenBuyer() => EntityNavigator.Instance.Entity(BuyerKind, BuyerId);

    /// <summary>The item itself. On a multi-item contract this is the first one — the "+3 more"
    /// stands for a list the row does not carry, so it is text rather than a link.</summary>
    public void OpenItem() => EntityNavigator.Instance.Item(TypeId);

    // Green when profit (for the active cost basis) is positive, red when negative, grey when unknown.
    public IBrush ProfitBrush => ProfitRaw == double.MinValue ? ProfitBrushes.Gray
                               : ProfitRaw >= 0 ? ProfitBrushes.Green : ProfitBrushes.Red;

    public SaleRowVm(DateTimeOffset when, string kind, string ownerType, long ownerId, bool ownerIsPersonal,
        string owner, string location, string buyer,
        string items, string units, double total, double? build, double? market,
        int typeId = 0, string marketGroup = "—", long saleId = 0,
        long locationId = 0, bool locationIsStation = false, long buyerId = 0,
        EntityKind buyerKind = EntityKind.Pilot, string note = "")
    {
        Note        = note;
        SaleId      = saleId;
        TypeId      = typeId;
        MarketGroup = marketGroup;
        LocationId        = locationId;
        LocationIsStation = locationIsStation;
        BuyerId           = buyerId;
        BuyerKind         = buyerKind;
        When     = when;
        WhenSort = when.UtcTicks;
        WhenText = when.UtcDateTime.ToString("yyyy-MM-dd HH:mm");
        Kind      = kind;
        OwnerType = ownerType;
        OwnerId   = ownerId;
        OwnerIsPersonal = ownerIsPersonal;
        Owner    = owner;
        Location = location;
        Buyer    = buyer;
        Items    = items;
        Units    = units;
        TotalRaw  = total;      Total  = MarketFmt.Isk(total);
        BuildRaw  = build  ?? 0; Build  = build  is double b ? MarketFmt.Isk(b) : "—";
        MarketRaw = market ?? 0; Market = market is double m ? MarketFmt.Isk(m) : "—";
        BuildOrNull  = build;
        MarketOrNull = market;

        ApplyBasis(SaleCostBasis.BuildCost);   // default; Sales Tracker can switch to market value
    }

    // Recompute profit (sale price − cost basis) against build cost or market value. The Sales
    // Tracker calls this when the "Profit based on" selection changes; the main grid reads Profit/
    // ProfitPct/ProfitBrush and the rollups read ProfitRaw/ProfitPctRaw.
    public void ApplyBasis(SaleCostBasis basis)
    {
        // On the build basis, anything without a build cost falls back to market value.
        // Minerals, ore, gas, isotopes and meta modules are not manufactured, so they have no
        // build cost at all — and leaving them at "—" quietly dropped them out of every profit
        // figure, which for a trader is most of what they sell.
        var cost = basis == SaleCostBasis.BuildCost
            ? BuildOrNull ?? MarketOrNull
            : MarketOrNull;
        var profit = cost is double c ? TotalRaw - c : (double?)null;
        ProfitRaw = profit ?? double.MinValue;
        Profit    = profit is double p ? MarketFmt.Isk(p) : "—";
        var pct = cost is double c2 && c2 != 0 ? (TotalRaw - c2) / c2 * 100 : (double?)null;
        ProfitPctRaw = pct ?? double.MinValue;
        ProfitPct    = pct is double pp ? $"{pp:N1}%" : "—";

        // Notify so the main grid's Profit / Profit % cells (and their colour) update in place.
        this.RaisePropertyChanged(nameof(Profit));
        this.RaisePropertyChanged(nameof(ProfitRaw));
        this.RaisePropertyChanged(nameof(ProfitPct));
        this.RaisePropertyChanged(nameof(ProfitPctRaw));
        this.RaisePropertyChanged(nameof(ProfitBrush));
    }
}

public enum OwnerScope { All, CharsAndPersonalCorps, Specific }
public record SalesOwnerOption(string Label, OwnerScope Scope, long OwnerId = 0, string OwnerType = "")
{ public override string ToString() => Label; }
public record SalesTypeOption(string Label, string? Kind)
{ public override string ToString() => Label; }

// One row on a Sales Tracker rollup grid (sales grouped by buyer / market group / item).
public class GroupRowVm
{
    public string Name      { get; }
    public string Amount    { get; }
    public double AmountRaw { get; }

    /// <summary>Where the group's name goes when clicked, or null when it names nothing with a
    /// page of its own. Taken from the first sale in the group — every sale in it shares the
    /// name, so they share the id behind it.</summary>
    public Action? Open    { get; }
    public bool    HasLink => Open is not null;

    public GroupRowVm(string name, double amount, Action? open = null)
    {
        Name = name; AmountRaw = amount; Amount = MarketFmt.Isk(amount); Open = open;
    }
}

// One row on a profit rollup grid — summed build-based profit plus the average profit % of the
// sales in the group. Sorted by the profit amount (ProfitRaw). "—" when no sale in the group had
// a cost basis to profit against.
public class ProfitGroupRowVm
{
    public string Name      { get; }
    public string Profit    { get; }
    public double ProfitRaw { get; }
    public string ProfitPct { get; }

    /// <summary>As <see cref="GroupRowVm.Open"/>. Null on the market-group rollup, whose rows
    /// name a category rather than a thing with a page.</summary>
    public Action? Open    { get; }
    public bool    HasLink => Open is not null;

    public ProfitGroupRowVm(string name, double? profit, double? pctAvg, Action? open = null)
    {
        Name      = name;
        Open      = open;
        ProfitRaw = profit ?? double.MinValue;
        Profit    = profit is double p  ? MarketFmt.Isk(p) : "—";
        ProfitPct = pctAvg is double pp ? $"{pp:N1}%"      : "—";
    }
}

// Sales Tracker — lists market sales and contract sales with build/market value and build-based
// profit. Data is loaded by the shared SalesQuery.
public class SalesTrackerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private readonly CorpActivityService             _names;

    private readonly List<SaleRowVm> _all = new();

    public ObservableCollection<SaleRowVm> Rows { get; } = new();

    // Rollup grids (grouped over the filtered sales). Top Buyers ranks by ISK sold; the market
    // group and item grids rank by build-based profit.
    public ObservableCollection<GroupRowVm>       TopBuyers    { get; } = new();
    public ObservableCollection<ProfitGroupRowVm> MarketGroups { get; } = new();
    public ObservableCollection<ProfitGroupRowVm> TopItems     { get; } = new();

    // ── Filters ───────────────────────────────────────────────────────────────
    public ObservableCollection<SalesOwnerOption> OwnerOptions { get; } =
    [
        new("All",                             OwnerScope.All),
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

    // Cost basis the profit columns / rollups are measured against.
    public IReadOnlyList<string> ProfitBasisOptions { get; } = ["Build", "Market"];
    private string _selectedProfitBasis = "Build";
    public string SelectedProfitBasis
    {
        get => _selectedProfitBasis;
        set { this.RaiseAndSetIfChanged(ref _selectedProfitBasis, value ?? "Build"); ApplyProfitBasis(); }
    }
    private SaleCostBasis CurrentBasis =>
        _selectedProfitBasis == "Market" ? SaleCostBasis.MarketValue : SaleCostBasis.BuildCost;

    private void ApplyProfitBasis()
    {
        foreach (var r in _all) r.ApplyBasis(CurrentBasis);
        ApplyFilters();
    }

    private string _dateFrom;
    public string DateFrom { get => _dateFrom; set { this.RaiseAndSetIfChanged(ref _dateFrom, value); ApplyFilters(); } }
    private string _dateThru = "";
    public string DateThru { get => _dateThru; set { this.RaiseAndSetIfChanged(ref _dateThru, value); ApplyFilters(); } }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public SalesTrackerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger,
        CorpActivityService names)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        _selectedOwner = OwnerOptions[1];                                  // All Characters and Personal Corps
        _selectedType  = SaleTypeOptions[0];                               // All types
        _dateFrom      = DateTime.UtcNow.AddDays(-90).ToString("yyyy-MM-dd"); // last 90 days

        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOnUi("SalesTracker.AutoRefresh")
            .Subscribe(tick => { _ = LoadAsync(); });

        _ = LoadAsync();
    }

    /// <summary>
    /// Whether the grid lists sales marked as not for profit. Off by default, and it only ever
    /// affects this grid — the rollups exclude them either way.
    /// </summary>
    private bool _showNotForProfit;
    public bool ShowNotForProfit
    {
        get => _showNotForProfit;
        set { this.RaiseAndSetIfChanged(ref _showNotForProfit, value); ApplyFilters(); }
    }

    /// <summary>
    /// Marks or unmarks sales, persisting the change and refreshing every derived figure.
    /// Takes the rows the user actually selected, so a multi-row selection is one action.
    /// </summary>
    public async Task SetNotForProfitAsync(IReadOnlyList<SaleRowVm> rows, bool notForProfit)
    {
        if (rows.Count == 0) return;

        var keys = rows.Select(r => (r.Kind, r.SaleId)).Distinct().ToList();

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            foreach (var (kind, saleId) in keys)
            {
                if (notForProfit)
                {
                    // INSERT OR IGNORE: marking something already marked is not an error, and
                    // a selection can legitimately contain a mix.
                    await db.Database.ExecuteSqlAsync(
                        $"""INSERT OR IGNORE INTO "SaleExclusions" ("Kind","SaleId","MarkedAt") VALUES ({kind},{saleId},{DateTimeOffset.UtcNow})""");
                }
                else
                {
                    await db.Database.ExecuteSqlAsync(
                        $"""DELETE FROM "SaleExclusions" WHERE "Kind" = {kind} AND "SaleId" = {saleId}""");
                }
            }
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(SalesTrackerViewModel), nameof(SetNotForProfitAsync), ex);
            return;
        }

        // Update every loaded row with the same identity, not just the selected instances — the
        // same sale can appear in more than one place once the grid is re-filtered.
        var changed = keys.ToHashSet();
        foreach (var r in _all)
            if (changed.Contains((r.Kind, r.SaleId))) r.NotForProfit = notForProfit;

        ApplyFilters();
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

        var matched = q.ToList();

        // Sales marked as not for profit never reach the rollups — that is the point of the
        // mark. The grid can be asked to show them, so it is filtered separately.
        var forProfit = matched.Where(r => !r.NotForProfit).ToList();
        var excluded  = matched.Count - forProfit.Count;
        var list      = ShowNotForProfit ? matched : forProfit;

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);

        StatusText = list.Count == 0
            ? "No sales match the filters."
            : $"{list.Count:N0} sale(s)" +
              (excluded > 0
                  ? ShowNotForProfit
                      ? $" · {excluded:N0} not for profit, shown but not counted"
                      : $" · {excluded:N0} not for profit, hidden"
                  : "");

        // Buyers and items link the same way their columns in the grid below do. Market group is
        // deliberately plain: "Standard Dreadnoughts" is a category, not a thing with a page.
        FillGroup(TopBuyers,          forProfit, r => r.Buyer,
                  r => r.HasBuyerLink ? r.OpenBuyer : null);
        FillProfitGroup(MarketGroups, forProfit, r => r.MarketGroup);
        FillProfitGroup(TopItems,     forProfit, r => r.Items,
                  r => r.HasItemLink ? r.OpenItem : null);
    }

    private static void FillGroup(ObservableCollection<GroupRowVm> target, List<SaleRowVm> rows,
                                  Func<SaleRowVm, string> key, Func<SaleRowVm, Action?>? link = null)
    {
        target.Clear();
        var groups = rows
            .Where(r => !string.IsNullOrEmpty(key(r)))
            .GroupBy(key)
            .Select(g => new GroupRowVm(g.Key, g.Sum(r => r.TotalRaw), link?.Invoke(g.First())))
            .OrderByDescending(g => g.AmountRaw);
        foreach (var g in groups) target.Add(g);
    }

    // Group sales and sum build-based profit, plus the average profit % over the sales that had a
    // cost basis. Ordered by profit amount (still by amount, not by percent).
    private static void FillProfitGroup(ObservableCollection<ProfitGroupRowVm> target, List<SaleRowVm> rows,
                                        Func<SaleRowVm, string> key, Func<SaleRowVm, Action?>? link = null)
    {
        target.Clear();
        var groups = rows
            .Where(r => !string.IsNullOrEmpty(key(r)))
            .GroupBy(key)
            .Select(g =>
            {
                var profits = g.Where(r => r.ProfitRaw    != double.MinValue).Select(r => r.ProfitRaw).ToList();
                var pcts    = g.Where(r => r.ProfitPctRaw != double.MinValue).Select(r => r.ProfitPctRaw).ToList();
                double? profit = profits.Count > 0 ? profits.Sum()     : (double?)null;
                double? pctAvg = pcts.Count    > 0 ? pcts.Average()    : (double?)null;
                return new ProfitGroupRowVm(g.Key, profit, pctAvg, link?.Invoke(g.First()));
            })
            .OrderByDescending(g => g.ProfitRaw);
        foreach (var g in groups) target.Add(g);
    }

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        { date = d.Date; return true; }
        date = default; return false;
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
            foreach (var r in _all) r.ApplyBasis(CurrentBasis);
            ApplyFilters();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("SalesTrackerViewModel", "Load", ex);
            StatusText = "Error loading sales.";
        }
        finally { IsLoading = false; }
    }

    // Populate the owner filter with every tracked character and corp (once).
    private void BuildOwnerOptions(IReadOnlyList<(long Id, string Name)> chars, IReadOnlyList<(long Id, string Name)> corps)
    {
        if (OwnerOptions.Count > 2) return;   // already built (keeps the current selection intact)
        foreach (var (id, name) in chars.OrderBy(c => c.Name))
            OwnerOptions.Add(new SalesOwnerOption(name, OwnerScope.Specific, id, "character"));
        foreach (var (id, name) in corps.OrderBy(c => c.Name))
            OwnerOptions.Add(new SalesOwnerOption(name, OwnerScope.Specific, id, "corporation"));
    }
}
