using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using Avalonia.Media;
using EveConsole.Controls;
using EveConsole.Data;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveConsole.ViewModels;

/// <summary>How wide a bucket the summary charts group into.</summary>
public sealed record SalesGrain(string Label, string Key)
{
    public override string ToString() => Label;
}

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
    /// The contract behind this sale, named the way the Order Tracker names it: the description
    /// typed on it and its id, or just the id when it carries no description.
    ///
    /// <para>Empty for a market sale. An order filling against whoever is buying has no contract
    /// and no message from either side.</para>
    /// </summary>
    public string Contract { get; } = "";

    /// <summary>Whether <see cref="Contract"/> is something the Contracts tool can open.</summary>
    public bool HasContractLink => Kind == "Contract" && SaleId is > 0 and <= int.MaxValue;

    /// <summary>
    /// Tags on this sale — the same list orders use, and the same tags where the two share a
    /// contract. See OrderLabelService.
    /// </summary>
    public IReadOnlyList<string> LabelList { get; private set; } = [];

    /// <summary>The same labels as coloured chips, drawn exactly as the Order Tracker draws them.</summary>
    public List<LabelChip> LabelChips { get; private set; } = [];

    public void SetLabels(IReadOnlyList<string> labels)
    {
        LabelList  = labels;
        LabelChips = LabelPalette.Chips(labels);
    }

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
    /// <summary>The cost basis actually used for this row, or null where neither a build cost
    /// nor a market value was known. Set by ApplyBasis alongside the profit it produces.</summary>
    public double? CostRaw { get; private set; }

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
        EntityKind buyerKind = EntityKind.Pilot, string contractTitle = "")
    {
        SaleId      = saleId;

        // Same shape as the Order Tracker's contract cell, so one contract reads the same
        // wherever it appears: its own description when it has one, its id when it does not.
        Contract = kind == "Contract" && saleId > 0
            ? (contractTitle.Length > 0 ? $"{contractTitle} ({saleId})" : $"Contract {saleId}")
            : "";
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

        // ⚠️ Kept, not just used. The charts need cost and sale as separate figures, and
        // recomputing the basis rule beside them would be a second copy of the fallback above
        // waiting to disagree with this one.
        CostRaw = cost;

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
    // — Summary charts ———————————————————————————————————

    public IReadOnlyList<SalesGrain> Grains { get; } =
    [
        new("Daily",   "d"),
        new("Weekly",  "w"),
        new("Monthly", "m"),
    ];

    private SalesGrain _grain;
    public SalesGrain Grain
    {
        get => _grain;
        set { this.RaiseAndSetIfChanged(ref _grain, value ?? Grains[0]); ApplyFilters(); }
    }

    private ISeries[] _iskSeries = [];
    public ISeries[] IskSeries { get => _iskSeries; private set => this.RaiseAndSetIfChanged(ref _iskSeries, value); }

    private ISeries[] _marginSeries = [];
    public ISeries[] MarginSeries { get => _marginSeries; private set => this.RaiseAndSetIfChanged(ref _marginSeries, value); }

    /// <summary>Sales in the filter with no cost basis on either side. They are left out of the
    /// charts so the three ISK lines still add up, and the count says so rather than the figures
    /// quietly disagreeing with the grid.</summary>
    public int Uncosted { get; private set; }
    public string UncostedNote => Uncosted > 0
        ? $"{Uncosted:N0} sale(s) have no cost on either basis and are not charted."
        : "";

    public Axis[] ChartXAxes { get; } =
    [
        new Axis
        {
            Labeler = v =>
            {
                var t = (long)v;
                return t < DateTime.MinValue.Ticks || t > DateTime.MaxValue.Ticks
                    ? "" : new DateTime(t).ToString("MMM d");
            },
            UnitWidth       = TimeSpan.FromDays(1).Ticks,
            MinStep         = TimeSpan.FromDays(1).Ticks,
            TextSize        = 11,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    public Axis[] IskYAxes { get; } =
    [
        new Axis
        {
            Labeler         = FormatIskAxis,
            TextSize        = 11,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    public Axis[] MarginYAxes { get; } =
    [
        new Axis
        {
            Labeler         = v => $"{v:N0}%",
            TextSize        = 11,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    private static string FormatIskAxis(double v) =>
        Math.Abs(v) >= 1_000_000_000_000 ? $"{v / 1_000_000_000_000:N1}T"
      : Math.Abs(v) >= 1_000_000_000     ? $"{v / 1_000_000_000:N1}B"
      : Math.Abs(v) >= 1_000_000         ? $"{v / 1_000_000:N1}M"
      : Math.Abs(v) >= 1_000             ? $"{v / 1_000:N1}K"
      :                                    v.ToString("N0");

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
        set
        {
            // ⚠️ A null from the control is refused, not defaulted. This view is built from a
            // DataTemplate in the tab host, so switching tabs detaches the ComboBox and it pushes
            // SelectedItem=null on the way out. Substituting the default here made that a
            // one-way door: come back to the tab and the filter had quietly reset to everything.
            // Re-raising instead tells the rebuilt control what the selection actually is.
            if (value is null) { this.RaisePropertyChanged(); return; }
            this.RaiseAndSetIfChanged(ref _selectedOwner, value);
            ApplyFilters();
        }
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
        set
        {
            // Same reason as SelectedOwner: a detaching ComboBox must not be able to clear it.
            if (value is null) { this.RaisePropertyChanged(); return; }
            this.RaiseAndSetIfChanged(ref _selectedType, value);
            ApplyFilters();
        }
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

    private readonly OrderLabelService _labels;

    public SalesTrackerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger,
        CorpActivityService names, OrderLabelService labels)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;
        _names       = names;
        _labels      = labels;
        _selectedOwner = OwnerOptions[1];                                  // All Characters and Personal Corps
        _selectedType  = SaleTypeOptions[0];                               // All types
        // A year, so the charts open on a trend rather than a quarter of one.
        _grain         = Grains[0];
        _dateFrom      = DateTime.UtcNow.Date.AddYears(-1).ToString("yyyy-MM-dd");

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
                        $"""INSERT INTO "SaleExclusions" ("Kind","SaleId","MarkedAt") VALUES ({kind},{saleId},{DateTimeOffset.UtcNow}) ON CONFLICT DO NOTHING""");
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

        // ⚠️ One filtered set, used by everything on the screen. The rollups used to be built
        // from the for-profit rows whatever the checkbox said, so ticking "show not for profit"
        // changed the grid underneath three summaries that still described a different set of
        // sales. A filter that moves half the screen is worse than one that moves none of it.
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
        FillGroup(TopBuyers,          list, r => r.Buyer,
                  r => r.HasBuyerLink ? r.OpenBuyer : null);
        FillProfitGroup(MarketGroups, list, r => r.MarketGroup);
        FillProfitGroup(TopItems,     list, r => r.Items,
                  r => r.HasItemLink ? r.OpenItem : null);

        BuildCharts(list);
    }

    // — Summary charts ————————————————————————————————

    /// <summary>
    /// Costs, sales and profit over time, and the margin they imply.
    ///
    /// <para>⚠️ Built only from sales whose cost is known, so the three ISK series add up: sales
    /// less costs IS the profit line, and the margin is that profit over those same sales. A sale
    /// with no cost basis on either side would otherwise land in the sales line and nowhere else,
    /// lifting both profit and margin by an amount that never existed.</para>
    ///
    /// <para>⚠️ Every bucket between the first and the last is plotted, filled with zero where
    /// nothing sold. Plotting only the days that carry a sale draws a line straight from one to
    /// the next, and a quiet fortnight then reads as a steady one.</para>
    /// </summary>
    private void BuildCharts(List<SaleRowVm> rows)
    {
        var costed = rows.Where(r => r.CostRaw is not null).ToList();

        Uncosted = rows.Count - costed.Count;
        this.RaisePropertyChanged(nameof(UncostedNote));

        if (costed.Count == 0)
        {
            IskSeries    = [];
            MarginSeries = [];
            return;
        }

        var byBucket = costed
            .GroupBy(r => Bucket(r.When.UtcDateTime.Date))
            .ToDictionary(
                g => g.Key,
                g => (Sales: g.Sum(r => r.TotalRaw), Cost: g.Sum(r => r.CostRaw!.Value)));

        var sales  = new List<DateTimePoint>();
        var costs  = new List<DateTimePoint>();
        var profit = new List<DateTimePoint>();
        var margin = new List<DateTimePoint>();

        foreach (var d in Range(byBucket.Keys.Min(), byBucket.Keys.Max()))
        {
            var hit = byBucket.GetValueOrDefault(d);

            sales.Add(new DateTimePoint(d, hit.Sales));
            costs.Add(new DateTimePoint(d, hit.Cost));
            profit.Add(new DateTimePoint(d, hit.Sales - hit.Cost));
        }

        // ⚠️ Margin is plotted ONLY where something sold, and the line joins across the gaps.
        //
        // The other three are amounts: a day with no sales earned nothing, and a zero is the
        // truth about it. Margin is a ratio, and a day with no sales has no margin at all — not
        // a margin of zero. Filling those with zero dragged the line to the floor between every
        // pair of selling days, and filling them with null broke it into disconnected stubs, one
        // per day, which is what this chart looked like.
        //
        // Joining across them says the thing the chart is for: whether the margin being achieved
        // is rising or falling. The gaps are visible in the three charts beside it.
        foreach (var kv in byBucket.Where(k => k.Value.Sales > 0).OrderBy(k => k.Key))
            margin.Add(new DateTimePoint(
                kv.Key, (kv.Value.Sales - kv.Value.Cost) / kv.Value.Sales * 100));

        IskSeries =
        [
            Line("Gross costs", costs,  new SKColor(0xaa, 0x44, 0x44)),
            Line("Gross sales", sales,  new SKColor(0x55, 0x99, 0xaa)),
            Line("Net profit",  profit, new SKColor(0x4a, 0x8a, 0x5a)),
        ];

        MarginSeries = [Line("Margin", margin, new SKColor(0xc8, 0xa8, 0x4b))];
    }

    /// <summary>Every bucket start from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private IEnumerable<DateTime> Range(DateTime first, DateTime last)
    {
        for (var d = first; d <= last; d = Step(d)) yield return d;
    }

    private DateTime Bucket(DateTime day) => Grain.Key switch
    {
        "m" => new DateTime(day.Year, day.Month, 1),
        // Monday, not the first row's day: a bucket that floats with the data cannot be compared
        // between two loads of the same chart.
        "w" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        _   => day,
    };

    private DateTime Step(DateTime d) => Grain.Key switch
    {
        "m" => d.AddMonths(1),
        "w" => d.AddDays(7),
        _   => d.AddDays(1),
    };

    private static LineSeries<DateTimePoint> Line(string name, List<DateTimePoint> pts, SKColor color) =>
        new()
        {
            Name           = name,
            Values         = pts,
            Stroke         = new SolidColorPaint(color) { StrokeThickness = 1.5f },
            Fill           = null,
            GeometryFill   = null,
            GeometryStroke = null,
            GeometrySize   = 0,
            LineSmoothness = 0.2,
        };

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

    /// <summary>The labels a picker should offer — the same list orders draw from.</summary>
    public Task<List<string>> KnownLabelsAsync() => _labels.AllAsync();

    /// <summary>
    /// Puts one label on several sales.
    ///
    /// <para>⚠️ It reaches the orders behind them too, wherever a sale and an order are the same
    /// contract. Labelling a sale and finding the order it fulfils untagged would make the pair
    /// disagree about the one thing they are meant to share.</para>
    /// </summary>
    public async Task AddLabelToAsync(IReadOnlyList<SaleRowVm> rows, string label)
    {
        var clean = OrderLabelService.Clean(label);
        if (clean.Length == 0 || rows.Count == 0) return;

        try
        {
            await _labels.AddToSalesAsync(rows.Select(r => (r.Kind, r.SaleId)).ToList(), clean);
            await LoadAsync();
            StatusText = $"Labelled {rows.Count:N0} sale(s) \"{clean}\".";
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(SalesTrackerViewModel), nameof(AddLabelToAsync), ex);
            StatusText = $"Could not label: {ex.Message}";
        }
    }

    /// <summary>Takes one label off several sales, and off the orders sharing their contract.</summary>
    public async Task RemoveLabelFromAsync(IReadOnlyList<SaleRowVm> rows, string label)
    {
        var clean = OrderLabelService.Clean(label);
        if (clean.Length == 0 || rows.Count == 0) return;

        try
        {
            await _labels.RemoveFromSalesAsync(rows.Select(r => (r.Kind, r.SaleId)).ToList(), clean);
            await LoadAsync();
            StatusText = $"Removed \"{clean}\" from {rows.Count:N0} sale(s).";
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(SalesTrackerViewModel), nameof(RemoveLabelFromAsync), ex);
            StatusText = $"Could not remove label: {ex.Message}";
        }
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            // ⚠️ Before the labels are read, not after. A contract linked since the last look is
            // an order and a sale that have only just become the same thing, and this is where
            // they find out. Idempotent, so running it every load costs nothing when nothing
            // changed. See OrderLabelService.SyncByContractAsync.
            await _labels.SyncByContractAsync();

            var result = await SalesQuery.LoadAsync(_dbFactory, _names, _errorLogger);
            BuildOwnerOptions(result.Chars, result.Corps);
            _all.Clear();
            _all.AddRange(result.Rows);

            var labels = await _labels.ForSalesAsync(
                _all.Select(r => (r.Kind, r.SaleId)).Distinct().ToList());
            foreach (var r in _all)
                r.SetLabels(labels.GetValueOrDefault((r.Kind, r.SaleId), []));

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
