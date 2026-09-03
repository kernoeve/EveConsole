using System.Collections.ObjectModel;
using System.Reactive.Linq;
using EveConsole.Data;
using EveConsole.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveConsole.ViewModels;

internal static class MarketFmt
{
    public static string Isk(double v) => Num(v);

    /// <summary>
    /// A value rounded to the precision <see cref="Num"/> would print it at.
    ///
    /// <para>⚠️ For prices that are STORED, not just shown. A quote of "3.71B" against a record
    /// of 3,705,101,922.94 is two different numbers for one agreement, and the buyer only ever
    /// saw the first. Rounding the value itself means the record says what was quoted.</para>
    ///
    /// <para>To nearest, not up: this is a price somebody pays, and always rounding it in the
    /// seller's favour is a thumb on the scale.</para>
    /// </summary>
    public static double RoundToDisplay(double v)
    {
        var a = Math.Abs(v);
        var step = a >= 1e12 ? 1e10    // 2dp of trillions
                 : a >= 1e9  ? 1e7     // 2dp of billions
                 : a >= 1e6  ? 1e4     // 2dp of millions
                 : a >= 1e3  ? 1e2     // 1dp of thousands
                 :             1;      // whole ISK
        return Math.Round(v / step, MidpointRounding.AwayFromZero) * step;
    }
    public static string Num(double v)
    {
        var a = Math.Abs(v);
        if (a >= 1e12) return $"{v / 1e12:N2}T";
        if (a >= 1e9)  return $"{v / 1e9:N2}B";
        if (a >= 1e6)  return $"{v / 1e6:N2}M";
        if (a >= 1e3)  return $"{v / 1e3:N1}K";
        return v.ToString("N0");
    }
}

public class MarketPeriodOption
{
    public string Label { get; }
    public int?   Days  { get; }   // null = all time
    public MarketPeriodOption(string label, int? days) { Label = label; Days = days; }
    public override string ToString() => Label;
}

public class MarketRegionOption
{
    public string Label    { get; }
    public int?   RegionId { get; }   // null = all regions
    public MarketRegionOption(string label, int? regionId) { Label = label; RegionId = regionId; }
    public override string ToString() => Label;
}

// One type row on the By Type tab.
public class MarketTypeSummaryVm
{
    public string Type { get; }

    public int  TypeId      { get; init; }
    public bool HasTypeLink => TypeId > 0 && Type.Length > 0;
    public void OpenType() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public string SellUnits { get; } public double SellUnitsRaw { get; }
    public string SellIsk   { get; } public double SellIskRaw   { get; }
    public string BuyUnits  { get; } public double BuyUnitsRaw  { get; }
    public string BuyIsk    { get; } public double BuyIskRaw    { get; }
    public string SalesUnits { get; } public double SalesUnitsRaw { get; }
    public string SalesIsk   { get; } public double SalesIskRaw   { get; }

    public MarketTypeSummaryVm(string type,
        double sellUnits, double sellIsk, double buyUnits, double buyIsk,
        double salesUnits, double salesIsk)
    {
        Type          = type;
        SellUnitsRaw  = sellUnits;  SellUnits  = MarketFmt.Num(sellUnits);
        SellIskRaw    = sellIsk;    SellIsk    = MarketFmt.Isk(sellIsk);
        BuyUnitsRaw   = buyUnits;   BuyUnits   = MarketFmt.Num(buyUnits);
        BuyIskRaw     = buyIsk;     BuyIsk     = MarketFmt.Isk(buyIsk);
        SalesUnitsRaw = salesUnits; SalesUnits = MarketFmt.Num(salesUnits);
        SalesIskRaw   = salesIsk;   SalesIsk   = MarketFmt.Isk(salesIsk);
    }
}

// One top-level-market-group row on the By Market Group tab.
public class MarketGroupSummaryVm
{
    public string Group { get; }
    public string SellUnits { get; } public double SellUnitsRaw { get; }
    public string SellIsk   { get; } public double SellIskRaw   { get; }
    public string BuyUnits  { get; } public double BuyUnitsRaw  { get; }
    public string BuyIsk    { get; } public double BuyIskRaw    { get; }
    public string SalesUnits { get; } public double SalesUnitsRaw { get; }
    public string SalesIsk   { get; } public double SalesIskRaw   { get; }

    public MarketGroupSummaryVm(string group,
        double sellUnits, double sellIsk, double buyUnits, double buyIsk,
        double salesUnits, double salesIsk)
    {
        Group         = group;
        SellUnitsRaw  = sellUnits;  SellUnits  = MarketFmt.Num(sellUnits);
        SellIskRaw    = sellIsk;    SellIsk    = MarketFmt.Isk(sellIsk);
        BuyUnitsRaw   = buyUnits;   BuyUnits   = MarketFmt.Num(buyUnits);
        BuyIskRaw     = buyIsk;     BuyIsk     = MarketFmt.Isk(buyIsk);
        SalesUnitsRaw = salesUnits; SalesUnits = MarketFmt.Num(salesUnits);
        SalesIskRaw   = salesIsk;   SalesIsk   = MarketFmt.Isk(salesIsk);
    }
}

// One type row on the Sell/Buy Orders by Type tabs.
public class MarketOrderByTypeVm
{
    public string Type  { get; }
    public string Units { get; } public double UnitsRaw { get; }
    public string Isk   { get; } public double IskRaw   { get; }

    public int  TypeId      { get; init; }
    public bool HasTypeLink => TypeId > 0 && Type.Length > 0;
    public void OpenType() => EveConsole.Services.EntityNavigator.Instance.Item(TypeId);

    public MarketOrderByTypeVm(string type, double units, double isk)
    {
        Type     = type;
        UnitsRaw = units; Units = MarketFmt.Num(units);
        IskRaw   = isk;   Isk   = MarketFmt.Isk(isk);
    }
}

// Region-level market views. Orders map to a region via the order's solar system, or — for player
// structures (null-sec), where the order has no system id — via the structure's system.
public class MarketViewerViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger                  _errorLogger;
    private bool _initialized;

    // Region derivation: order → system → region, else order → structure → system → region.
    private const string OrdersFrom =
        "FROM \"MarketRawOrders\" o " +
        "LEFT JOIN \"SdeSolarSystems\" ssSys ON ssSys.\"SolarSystemId\" = o.\"SystemId\" " +
        "LEFT JOIN \"EsiStructureNames\" sn   ON sn.\"StructureId\"     = o.\"LocationId\" " +
        "LEFT JOIN \"SdeSolarSystems\" ssStr  ON ssStr.\"SolarSystemId\" = sn.\"SolarSystemId\"";
    private const string RegionExpr = "COALESCE(ssSys.\"RegionId\", ssStr.\"RegionId\")";
    // Exclude NPC market orders: NPC seeded/buy orders use a 365-day duration, while player
    // orders cap at 90 days (no order falls between 91 and 364), so this keeps players only.
    private const string PlayerOrders = "o.\"Duration\" <= 90";

    // Maps every market group to its top-level ancestor (TopId/TopName).
    private const string MgTopCte =
        "WITH RECURSIVE mg_top(\"MarketGroupId\", \"TopId\", \"TopName\") AS (" +
        "SELECT \"MarketGroupId\", \"MarketGroupId\", \"Name\" FROM \"SdeMarketGroups\" WHERE \"ParentGroupId\" IS NULL " +
        "UNION ALL " +
        "SELECT g.\"MarketGroupId\", t.\"TopId\", t.\"TopName\" FROM \"SdeMarketGroups\" g " +
        "JOIN mg_top t ON g.\"ParentGroupId\" = t.\"MarketGroupId\") ";

    // Palette for pie slices (last entry — grey — is reserved for the "Other" bucket).
    private static readonly SKColor[] PiePalette =
    [
        new(0xc8, 0xa8, 0x4b), new(0x5b, 0x9b, 0xd5), new(0x70, 0xad, 0x47), new(0xed, 0x7d, 0x31),
        new(0xa8, 0x79, 0xd8), new(0x17, 0xbe, 0xcf), new(0xe7, 0x4c, 0x3c), new(0xf1, 0xc4, 0x0f),
        new(0x2e, 0xcc, 0x71), new(0xe8, 0x4d, 0x8a),
    ];
    private static readonly SKColor OtherColor = new(0x55, 0x55, 0x66);

    public ObservableCollection<MarketGroupSummaryVm> GroupRows { get; } = new();
    public ObservableCollection<MarketTypeSummaryVm>  TypeRows  { get; } = new();
    public ObservableCollection<MarketOrderByTypeVm> SellByTypeRows { get; } = new();
    public ObservableCollection<MarketOrderByTypeVm> BuyByTypeRows  { get; } = new();

    // ── Summary KPI boxes (selected region, or all regions) ───────────────────────
    private string _kpiSellCount = "—", _kpiSellIsk = "—", _kpiSellTypes = "—";
    private string _kpiBuyCount  = "—", _kpiBuyIsk  = "—", _kpiBuyTypes  = "—";
    private string _kpiSalesUnits = "—", _kpiSalesIsk = "—", _kpiSalesTypes = "—";
    public string KpiSellCount  { get => _kpiSellCount;  private set => this.RaiseAndSetIfChanged(ref _kpiSellCount,  value); }
    public string KpiSellIsk    { get => _kpiSellIsk;    private set => this.RaiseAndSetIfChanged(ref _kpiSellIsk,    value); }
    public string KpiSellTypes  { get => _kpiSellTypes;  private set => this.RaiseAndSetIfChanged(ref _kpiSellTypes,  value); }
    public string KpiBuyCount   { get => _kpiBuyCount;   private set => this.RaiseAndSetIfChanged(ref _kpiBuyCount,   value); }
    public string KpiBuyIsk     { get => _kpiBuyIsk;     private set => this.RaiseAndSetIfChanged(ref _kpiBuyIsk,     value); }
    public string KpiBuyTypes   { get => _kpiBuyTypes;   private set => this.RaiseAndSetIfChanged(ref _kpiBuyTypes,   value); }
    public string KpiSalesUnits { get => _kpiSalesUnits; private set => this.RaiseAndSetIfChanged(ref _kpiSalesUnits, value); }
    public string KpiSalesIsk   { get => _kpiSalesIsk;   private set => this.RaiseAndSetIfChanged(ref _kpiSalesIsk,   value); }
    public string KpiSalesTypes { get => _kpiSalesTypes; private set => this.RaiseAndSetIfChanged(ref _kpiSalesTypes, value); }

    // ── Summary pie charts ────────────────────────────────────────────────────────
    private ISeries[] _buyCorpSeries = [], _sellCorpSeries = [], _salesGroupSeries = [];
    // Slice label → item type, for the two by-type charts. See BuildTypePie.
    private Dictionary<string, int> _sellSliceTypes = [];
    private Dictionary<string, int> _buySliceTypes  = [];

    /// <summary>Opens the item a clicked slice stands for. Returns quietly for "Other", and for
    /// any label the map does not hold — both mean the slice names no single item.</summary>
    public void OpenSellSlice(string? label) => OpenSlice(_sellSliceTypes, label);
    public void OpenBuySlice(string? label)  => OpenSlice(_buySliceTypes,  label);

    private static void OpenSlice(Dictionary<string, int> map, string? label)
    {
        if (label is not null && map.TryGetValue(label, out var typeId))
            EveConsole.Services.EntityNavigator.Instance.Item(typeId);
    }

    public ISeries[] BuyCorpSeries    { get => _buyCorpSeries;    private set => this.RaiseAndSetIfChanged(ref _buyCorpSeries,    value); }
    public ISeries[] SellCorpSeries   { get => _sellCorpSeries;   private set => this.RaiseAndSetIfChanged(ref _sellCorpSeries,   value); }
    public ISeries[] SalesGroupSeries { get => _salesGroupSeries; private set => this.RaiseAndSetIfChanged(ref _salesGroupSeries, value); }

    private bool _hasBuyCorp, _hasSellCorp, _hasSalesGroup;
    public bool HasBuyCorp    { get => _hasBuyCorp;    private set => this.RaiseAndSetIfChanged(ref _hasBuyCorp,    value); }
    public bool HasSellCorp   { get => _hasSellCorp;   private set => this.RaiseAndSetIfChanged(ref _hasSellCorp,   value); }
    public bool HasSalesGroup { get => _hasSalesGroup; private set => this.RaiseAndSetIfChanged(ref _hasSalesGroup, value); }

    // ── Summary daily sales line ──────────────────────────────────────────────────
    private ISeries[] _salesLineSeries = [];
    public ISeries[] SalesLineSeries { get => _salesLineSeries; private set => this.RaiseAndSetIfChanged(ref _salesLineSeries, value); }
    private bool _hasSalesLine;
    public bool HasSalesLine { get => _hasSalesLine; private set => this.RaiseAndSetIfChanged(ref _hasSalesLine, value); }

    public Axis[] SalesXAxes { get; } =
    [
        new Axis
        {
            Labeler = value =>
            {
                var ticks = (long)value;
                return ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                    ? "" : new DateTime(ticks).ToString("MMM d");
            },
            UnitWidth       = TimeSpan.FromDays(1).Ticks,
            MinStep         = TimeSpan.FromDays(1).Ticks,
            TextSize        = 11,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];
    public Axis[] SalesYAxes { get; } =
    [
        new Axis
        {
            Labeler         = MarketFmt.Num,
            TextSize        = 11,
            MinLimit        = 0,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    public IReadOnlyList<MarketPeriodOption> Periods { get; } =
    [
        new("All \"Time\"",      null),
        new("Last 365 Days", 365),
        new("Last 90 Days",  90),
        new("Last 30 Days",  30),
        new("Last 7 Days",   7),
    ];
    private MarketPeriodOption _selectedPeriod;
    public MarketPeriodOption SelectedPeriod
    {
        get => _selectedPeriod;
        set { this.RaiseAndSetIfChanged(ref _selectedPeriod, value ?? Periods[3]); _ = LoadActiveAsync(); }
    }

    public ObservableCollection<MarketRegionOption> Regions { get; } = new();
    private MarketRegionOption? _selectedRegion;
    public MarketRegionOption? SelectedRegion
    {
        get => _selectedRegion;
        set { this.RaiseAndSetIfChanged(ref _selectedRegion, value); _ = LoadActiveAsync(); }
    }

    // 0 = Summary, 1 = By Market Group, 2 = By Type, 3 = Sell by Type, 4 = Buy by Type
    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set { this.RaiseAndSetIfChanged(ref _selectedTab, value); _ = LoadActiveAsync(); }
    }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public MarketViewerViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory      = dbFactory;
        _errorLogger    = errorLogger;
        _selectedPeriod = Periods[3];   // Last 30 Days

        // Auto-refresh the active tab every 5 minutes.
        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOnUi("MarketViewer.AutoRefresh")
            .Subscribe(tick => { _ = LoadActiveAsync(); });

        _ = InitAsync();
    }

    private string? Cutoff() =>
        _selectedPeriod.Days is int d ? DateTime.UtcNow.AddDays(-d).ToString("yyyy-MM-dd") : null;

    private Task LoadActiveAsync() => SelectedTab switch
    {
        1 => LoadByMarketGroupAsync(),
        2 => LoadByTypeAsync(),
        3 => LoadOrdersByTypeAsync(buy: false),
        4 => LoadOrdersByTypeAsync(buy: true),
        _ => LoadSummaryAsync(),
    };

    private async Task InitAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Regions we have data for (orders and/or any history).
            var ids = await db.Database.SqlQueryRaw<int>(
                $"SELECT DISTINCT {RegionExpr} AS \"Value\" {OrdersFrom} WHERE {RegionExpr} IS NOT NULL " +
                "UNION SELECT DISTINCT \"RegionId\" AS \"Value\" FROM \"MarketTypeHistories\"").ToListAsync();
            var names = await db.SdeRegions.AsNoTracking()
                .Where(r => ids.Contains(r.RegionId))
                .ToDictionaryAsync(r => r.RegionId, r => r.Name);

            Regions.Clear();
            Regions.Add(new MarketRegionOption("All regions", null));
            foreach (var id in ids.OrderBy(i => names.TryGetValue(i, out var n) ? n : $"{i}"))
                Regions.Add(new MarketRegionOption(names.TryGetValue(id, out var n) ? n : $"Region {id}", id));
            _selectedRegion = Regions.FirstOrDefault();
            this.RaisePropertyChanged(nameof(SelectedRegion));

            _initialized = true;
            await LoadSummaryAsync();
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "InitAsync", ex);
            StatusText = "Error initialising market viewer.";
        }
    }

    // ── Summary (KPI boxes + pies + daily line for the selected region, or all) ────
    private async Task LoadSummaryAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            int? region = _selectedRegion?.RegionId;
            var  cutoff = Cutoff();

            // Order KPIs — current open orders (not period-dependent).
            var orderWhere = region is int r1
                ? $"WHERE {PlayerOrders} AND {RegionExpr} = {r1}"
                : $"WHERE {PlayerOrders} AND {RegionExpr} IS NOT NULL";
            // Every SUM is wrapped: an ungrouped SUM over zero matching rows is NULL, not zero,
            // and these columns map to non-nullable properties. COUNT is left alone — it already
            // returns zero. Before this the whole summary threw on any empty order book, which
            // every new install has until the first market poll lands.
            var o = (await db.Database.SqlQueryRaw<KpiOrderAgg>(
                "SELECT " +
                "COALESCE(SUM(CASE WHEN o.\"IsBuyOrder\" = FALSE THEN 1 ELSE 0 END), 0)                        AS SellCount, " +
                "COALESCE(SUM(CASE WHEN o.\"IsBuyOrder\" = FALSE THEN o.\"Price\" * o.\"VolumeRemain\" ELSE 0 END), 0) AS SellIsk, " +
                "COUNT(DISTINCT CASE WHEN o.\"IsBuyOrder\" = FALSE THEN o.\"TypeId\" END)                          AS SellTypes, " +
                "COALESCE(SUM(CASE WHEN o.\"IsBuyOrder\" = TRUE THEN 1 ELSE 0 END), 0)                        AS BuyCount, " +
                "COALESCE(SUM(CASE WHEN o.\"IsBuyOrder\" = TRUE THEN o.\"Price\" * o.\"VolumeRemain\" ELSE 0 END), 0) AS BuyIsk, " +
                "COUNT(DISTINCT CASE WHEN o.\"IsBuyOrder\" = TRUE THEN o.\"TypeId\" END)                          AS BuyTypes " +
                OrdersFrom + " " + orderWhere).ToListAsync()).FirstOrDefault() ?? new KpiOrderAgg();

            // Sales KPIs — driven by the selected period.
            var saleConds = new List<string>();
            if (region is int r2)      saleConds.Add($"\"RegionId\" = {r2}");
            if (cutoff is not null)     saleConds.Add($"\"Date\" >= '{cutoff}'");
            var saleWhere = saleConds.Count > 0 ? "WHERE " + string.Join(" AND ", saleConds) + " " : "";
            var s = (await db.Database.SqlQueryRaw<KpiSalesAgg>(
                "SELECT COALESCE(SUM(\"Volume\"), 0) AS \"Units\", COALESCE(SUM(\"Volume\" * \"Average\"), 0) AS Isk, " +
                "COUNT(DISTINCT CASE WHEN \"Volume\" > 0 THEN \"TypeId\" END) AS Types " +
                "FROM \"MarketTypeHistories\" " + saleWhere).ToListAsync()).FirstOrDefault() ?? new KpiSalesAgg();

            KpiSellCount  = o.SellCount.ToString("N0");
            KpiSellIsk    = MarketFmt.Isk(o.SellIsk);
            KpiSellTypes  = o.SellTypes.ToString("N0");
            KpiBuyCount   = o.BuyCount.ToString("N0");
            KpiBuyIsk     = MarketFmt.Isk(o.BuyIsk);
            KpiBuyTypes   = o.BuyTypes.ToString("N0");
            KpiSalesUnits = MarketFmt.Num(s.Units);
            KpiSalesIsk   = MarketFmt.Isk(s.Isk);
            KpiSalesTypes = s.Types.ToString("N0");

            // Sell/Buy orders by type — from the public order book (same source as the KPI order
            // ISK, so the slices sum to it); current open orders, selected region.
            var byType = await db.Database.SqlQueryRaw<TypeIskAgg>(
                "SELECT o.\"TypeId\" AS \"TypeId\", o.\"IsBuyOrder\" AS \"IsBuyOrder\", SUM(o.\"Price\" * o.\"VolumeRemain\") AS Isk " +
                OrdersFrom + " " + orderWhere + " GROUP BY o.\"TypeId\", o.\"IsBuyOrder\"").ToListAsync();

            var sellByType = byType.Where(x => x.IsBuyOrder == 0).Select(x => (x.TypeId, x.Isk)).ToList();
            var buyByType  = byType.Where(x => x.IsBuyOrder == 1).Select(x => (x.TypeId, x.Isk)).ToList();

            // Only the top slices ever show a name; the rest collapse into "Other".
            var needIds = sellByType.OrderByDescending(x => x.Isk).Take(10)
                .Concat(buyByType.OrderByDescending(x => x.Isk).Take(10))
                .Select(x => x.TypeId).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => needIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            string TName(int id) => typeNames.TryGetValue(id, out var n) ? n : $"\"Type\" {id}";

            var sellPie    = BuildTypePie(sellByType.Select(x => (TName(x.TypeId), x.Isk, x.TypeId)));
            SellCorpSeries = sellPie.Series;
            _sellSliceTypes = sellPie.TypeByLabel;
            HasSellCorp    = SellCorpSeries.Length > 0;

            var buyPie     = BuildTypePie(buyByType.Select(x => (TName(x.TypeId), x.Isk, x.TypeId)));
            BuyCorpSeries  = buyPie.Series;
            _buySliceTypes = buyPie.TypeByLabel;
            HasBuyCorp     = BuyCorpSeries.Length > 0;

            // Sales by top-level market group — selected region and period.
            var groups = await db.Database.SqlQueryRaw<GroupSalesFlat>(
                MgTopCte +
                "SELECT mt.\"TopName\" AS \"Name\", SUM(h.\"Volume\" * h.\"Average\") AS Isk " +
                "FROM \"MarketTypeHistories\" h " +
                "JOIN \"SdeTypes\" ty ON ty.\"TypeId\" = h.\"TypeId\" " +
                "JOIN mg_top mt ON mt.\"MarketGroupId\" = ty.\"MarketGroupId\" " +
                (region is int r4 || cutoff is not null
                    ? "WHERE " + string.Join(" AND ",
                        new[] { region is int r5 ? $"h.\"RegionId\" = {r5}" : null, cutoff is not null ? $"h.\"Date\" >= '{cutoff}'" : null }
                        .Where(x => x is not null)) + " "
                    : "") +
                "GROUP BY mt.\"TopId\", mt.\"TopName\"").ToListAsync();
            SalesGroupSeries = BuildPie(groups.Select(g => (g.Name, g.Isk)));
            HasSalesGroup    = SalesGroupSeries.Length > 0;

            // Daily sales ISK across the period.
            var days = await db.Database.SqlQueryRaw<DayIsk>(
                "SELECT \"Date\" AS \"Date\", SUM(\"Volume\" * \"Average\") AS Isk FROM \"MarketTypeHistories\" " +
                saleWhere + "GROUP BY \"Date\" ORDER BY \"Date\"").ToListAsync();
            var points = days
                .Select(d => new DateTimePoint(DateTime.ParseExact(d.Date, "yyyy-MM-dd", null), d.Isk))
                .ToArray();
            SalesLineSeries = points.Length == 0 ? [] :
            [
                new LineSeries<DateTimePoint>
                {
                    Name                   = "Sales ISK",
                    Values                 = points,
                    Stroke                 = new SolidColorPaint(new SKColor(0xc8, 0xa8, 0x4b)) { StrokeThickness = 1.5f },
                    Fill                   = null,
                    GeometryFill           = new SolidColorPaint(new SKColor(0xc8, 0xa8, 0x4b)),
                    GeometryStroke         = null,
                    GeometrySize           = 4,
                    LineSmoothness         = 0.3,
                    YToolTipLabelFormatter = (ChartPoint<DateTimePoint, CircleGeometry, LabelGeometry> p)
                        => $"{MarketFmt.Isk(p.Coordinate.PrimaryValue)} ISK",
                }
            ];
            HasSalesLine = points.Length > 0;

            var regionLabel = _selectedRegion?.RegionId is null ? "all regions" : _selectedRegion!.Label;
            StatusText = $"{regionLabel} · {_selectedPeriod.Label}";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadSummary", ex);
            StatusText = "Error loading summary.";
        }
        finally { IsLoading = false; }
    }

    // Top 10 items by value; the remainder collapses into a single "Other" slice.
    /// <summary>
    /// <see cref="BuildPie"/> for the two by-type charts, which also need a way back from a
    /// clicked slice to the item it stands for.
    ///
    /// <para>⚠️ Keyed on the slice's label rather than carrying the id on the series. LiveCharts
    /// gives a click handler the ChartPoint and its series, and a PieSeries' Name is the only
    /// thing on it we set — so the label is what a click can be resolved through. "Other" is
    /// deliberately absent from the map: it stands for everything past the tenth slice and names
    /// no single item.</para>
    /// </summary>
    private static (ISeries[] Series, Dictionary<string, int> TypeByLabel) BuildTypePie(
        IEnumerable<(string Label, double Value, int TypeId)> items)
    {
        var list  = items.ToList();
        var series = BuildPie(list.Select(i => (i.Label, i.Value)));

        // Only labels that actually became their own slice can be clicked; the rest folded into
        // "Other". Duplicate names would be ambiguous, so the first wins and the rest drop out.
        var sliceLabels = series.Select(s => s.Name ?? "").ToHashSet();
        var map = list
            .Where(i => i.TypeId > 0 && sliceLabels.Contains(i.Label) && i.Label != "Other")
            .GroupBy(i => i.Label)
            .ToDictionary(g => g.Key, g => g.First().TypeId);

        return (series, map);
    }

    private static ISeries[] BuildPie(IEnumerable<(string Label, double Value)> items)
    {
        var ordered = items.Where(i => i.Value > 0).OrderByDescending(i => i.Value).ToList();
        var slices  = ordered.Take(10).ToList();
        var rest    = ordered.Skip(10).Sum(i => i.Value);
        if (rest > 0) slices.Add(("Other", rest));

        var series = new List<ISeries>(slices.Count);
        for (var i = 0; i < slices.Count; i++)
        {
            var (label, value) = slices[i];
            var color = label == "Other" ? OtherColor : PiePalette[i % PiePalette.Length];
            series.Add(new PieSeries<double>
            {
                Name                  = label,
                Values                = [value],
                Fill                  = new SolidColorPaint(color),
                Stroke                = null,
                DataLabelsPaint       = null,
                AnimationsSpeed       = TimeSpan.Zero,
                EasingFunction        = null,
                ToolTipLabelFormatter = cp => $"{label}: {MarketFmt.Isk(cp.Coordinate.PrimaryValue)}",
            });
        }
        return [.. series];
    }

    // ── By Market Group (one row per top-level market group; selected region or all) ──
    private async Task LoadByMarketGroupAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            int? region = _selectedRegion?.RegionId;

            var orderWhere = region is int r1
                ? $"WHERE {PlayerOrders} AND {RegionExpr} = {r1}"
                : $"WHERE {PlayerOrders} AND {RegionExpr} IS NOT NULL";
#pragma warning disable EF1002 // region is an inlined int from a fixed, DB-derived set — no injection risk
            var orders = await db.Database.SqlQueryRaw<GroupOrderAgg>(
                MgTopCte +
                "SELECT mt.\"TopId\" AS \"GroupId\", mt.\"TopName\" AS GroupName, " +
                "SUM(CASE WHEN o.\"IsBuyOrder\" = FALSE THEN o.\"VolumeRemain\" ELSE 0 END)           AS SellUnits, " +
                "SUM(CASE WHEN o.\"IsBuyOrder\" = FALSE THEN o.\"Price\" * o.\"VolumeRemain\" ELSE 0 END) AS SellIsk, " +
                "SUM(CASE WHEN o.\"IsBuyOrder\" = TRUE THEN o.\"VolumeRemain\" ELSE 0 END)           AS BuyUnits, " +
                "SUM(CASE WHEN o.\"IsBuyOrder\" = TRUE THEN o.\"Price\" * o.\"VolumeRemain\" ELSE 0 END) AS BuyIsk " +
                OrdersFrom + " " +
                "JOIN \"SdeTypes\" ty ON ty.\"TypeId\" = o.\"TypeId\" " +
                "JOIN mg_top mt ON mt.\"MarketGroupId\" = ty.\"MarketGroupId\" " +
                orderWhere + " GROUP BY mt.\"TopId\", mt.\"TopName\"").ToListAsync();
#pragma warning restore EF1002

            var cutoff = Cutoff();
            var conds = new List<string>();
            if (region is int r2) conds.Add($"h.\"RegionId\" = {r2}");
            if (cutoff is not null) conds.Add($"h.\"Date\" >= '{cutoff}'");
            var salesWhere = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";
            var sales = await db.Database.SqlQueryRaw<GroupSalesAgg>(
                MgTopCte +
                "SELECT mt.\"TopId\" AS \"GroupId\", mt.\"TopName\" AS GroupName, " +
                "SUM(h.\"Volume\") AS \"Units\", SUM(h.\"Volume\" * h.\"Average\") AS Isk " +
                "FROM \"MarketTypeHistories\" h " +
                "JOIN \"SdeTypes\" ty ON ty.\"TypeId\" = h.\"TypeId\" " +
                "JOIN mg_top mt ON mt.\"MarketGroupId\" = ty.\"MarketGroupId\" " +
                salesWhere + " GROUP BY mt.\"TopId\", mt.\"TopName\"").ToListAsync();

            var names = new Dictionary<int, string>();
            foreach (var o in orders) names[o.GroupId] = o.GroupName;
            foreach (var s in sales)   names.TryAdd(s.GroupId, s.GroupName);
            var oByG = orders.ToDictionary(o => o.GroupId);
            var sByG = sales.ToDictionary(s => s.GroupId);

            var rows = names.Keys.Select(gid =>
            {
                oByG.TryGetValue(gid, out var o); sByG.TryGetValue(gid, out var s);
                return new MarketGroupSummaryVm(
                    names.TryGetValue(gid, out var n) ? n : $"Group {gid}",
                    o?.SellUnits ?? 0, o?.SellIsk ?? 0, o?.BuyUnits ?? 0, o?.BuyIsk ?? 0,
                    s?.Units ?? 0, s?.Isk ?? 0);
            }).OrderByDescending(g => g.SalesIskRaw).ToList();

            GroupRows.Clear();
            foreach (var g in rows) GroupRows.Add(g);
            StatusText = rows.Count == 0 ? "No market data for this selection." : $"{rows.Count:N0} market group(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadByMarketGroup", ex);
            StatusText = "Error loading by-market-group data.";
        }
        finally { IsLoading = false; }
    }

    // ── By Type (one row per type; selected region or all) ────────────────────────
    private async Task LoadByTypeAsync()
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            int? region = _selectedRegion?.RegionId;

            var orderWhere = region is int r1
                ? $"WHERE {PlayerOrders} AND {RegionExpr} = {r1}"
                : $"WHERE {PlayerOrders} AND {RegionExpr} IS NOT NULL";
#pragma warning disable EF1002 // region is an inlined int from a fixed, DB-derived set — no injection risk
            var orders = await db.Database.SqlQueryRaw<TypeOrderAgg>(
                $"""
                 SELECT o."TypeId" AS "TypeId",
                        SUM(CASE WHEN o."IsBuyOrder" = FALSE THEN o."VolumeRemain" ELSE 0 END)           AS SellUnits,
                        SUM(CASE WHEN o."IsBuyOrder" = FALSE THEN o."Price" * o."VolumeRemain" ELSE 0 END) AS SellIsk,
                        SUM(CASE WHEN o."IsBuyOrder" = TRUE THEN o."VolumeRemain" ELSE 0 END)           AS BuyUnits,
                        SUM(CASE WHEN o."IsBuyOrder" = TRUE THEN o."Price" * o."VolumeRemain" ELSE 0 END) AS BuyIsk
                 {OrdersFrom}
                 {orderWhere}
                 GROUP BY o."TypeId"
                 """).ToListAsync();
#pragma warning restore EF1002

            var cutoff = Cutoff();
            var conds = new List<string>();
            if (region is int r2) conds.Add($"\"RegionId\" = {r2}");
            if (cutoff is not null) conds.Add($"\"Date\" >= '{cutoff}'");
            var salesWhere = conds.Count > 0 ? "WHERE " + string.Join(" AND ", conds) : "";
            var sales = await db.Database.SqlQueryRaw<TypeSalesAgg>(
                $"SELECT \"TypeId\" AS \"TypeId\", SUM(\"Volume\") AS \"Units\", SUM(\"Volume\" * \"Average\") AS Isk " +
                $"FROM \"MarketTypeHistories\" {salesWhere} GROUP BY \"TypeId\"").ToListAsync();

            var typeIds = orders.Select(o => o.TypeId).Concat(sales.Select(s => s.TypeId)).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);
            var oByT = orders.ToDictionary(o => o.TypeId);
            var sByT = sales.ToDictionary(s => s.TypeId);

            var rows = typeIds.Select(tid =>
            {
                oByT.TryGetValue(tid, out var o); sByT.TryGetValue(tid, out var s);
                return new MarketTypeSummaryVm(
                    typeNames.TryGetValue(tid, out var n) ? n : $"\"Type\" {tid}",
                    o?.SellUnits ?? 0, o?.SellIsk ?? 0, o?.BuyUnits ?? 0, o?.BuyIsk ?? 0,
                    s?.Units ?? 0, s?.Isk ?? 0) { TypeId = tid };
            }).OrderByDescending(t => t.SalesIskRaw).ToList();

            TypeRows.Clear();
            foreach (var t in rows) TypeRows.Add(t);
            StatusText = rows.Count == 0 ? "No market data for this selection." : $"{rows.Count:N0} type(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadByType", ex);
            StatusText = "Error loading by-type data.";
        }
        finally { IsLoading = false; }
    }

    // ── Sell/Buy Orders by Type (public order book; selected region or all) ────────
    private async Task LoadOrdersByTypeAsync(bool buy)
    {
        if (!_initialized || IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            int? region = _selectedRegion?.RegionId;

            var conds = new List<string> { PlayerOrders, $"o.IsBuyOrder = {(buy ? 1 : 0)}" };
            conds.Add(region is int r ? $"{RegionExpr} = {r}" : $"{RegionExpr} IS NOT NULL");
            var where = "WHERE " + string.Join(" AND ", conds);

            var rows = await db.Database.SqlQueryRaw<TypeUnitIskAgg>(
                "SELECT o.\"TypeId\" AS \"TypeId\", SUM(o.\"VolumeRemain\") AS \"Units\", " +
                "SUM(o.\"Price\" * o.\"VolumeRemain\") AS Isk " +
                OrdersFrom + " " + where + " GROUP BY o.\"TypeId\"").ToListAsync();

            var typeIds   = rows.Select(x => x.TypeId).ToList();
            var typeNames = await db.SdeTypes.AsNoTracking().Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            var vms = rows
                .Select(x => new MarketOrderByTypeVm(
                    typeNames.TryGetValue(x.TypeId, out var n) ? n : $"\"Type\" {x.TypeId}", x.Units, x.Isk)
                    { TypeId = x.TypeId })
                .OrderByDescending(v => v.IskRaw).ToList();

            var target = buy ? BuyByTypeRows : SellByTypeRows;
            target.Clear();
            foreach (var v in vms) target.Add(v);
            StatusText = vms.Count == 0 ? "No orders for this selection." : $"{vms.Count:N0} type(s)";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("MarketViewerViewModel", "LoadOrdersByType", ex);
            StatusText = "Error loading orders-by-type data.";
        }
        finally { IsLoading = false; }
    }

    private sealed class TypeUnitIskAgg
    {
        public int TypeId { get; set; } public double Units { get; set; } public double Isk { get; set; }
    }
    private sealed class KpiOrderAgg
    {
        public long SellCount { get; set; } public double SellIsk { get; set; } public long SellTypes { get; set; }
        public long BuyCount  { get; set; } public double BuyIsk  { get; set; } public long BuyTypes  { get; set; }
    }
    private sealed class KpiSalesAgg
    {
        public double Units { get; set; } public double Isk { get; set; } public long Types { get; set; }
    }
    private sealed class TypeIskAgg
    {
        public int TypeId { get; set; } public long IsBuyOrder { get; set; } public double Isk { get; set; }
    }
    private sealed class GroupSalesFlat
    {
        public string Name { get; set; } = ""; public double Isk { get; set; }
    }
    private sealed class DayIsk
    {
        public string Date { get; set; } = ""; public double Isk { get; set; }
    }
    private sealed class GroupOrderAgg
    {
        public int GroupId { get; set; } public string GroupName { get; set; } = "";
        public double SellUnits { get; set; } public double SellIsk { get; set; }
        public double BuyUnits  { get; set; } public double BuyIsk  { get; set; }
    }
    private sealed class GroupSalesAgg
    {
        public int GroupId { get; set; } public string GroupName { get; set; } = "";
        public double Units { get; set; } public double Isk { get; set; }
    }
    private sealed class TypeOrderAgg
    {
        public int TypeId { get; set; }
        public double SellUnits { get; set; } public double SellIsk { get; set; }
        public double BuyUnits  { get; set; } public double BuyIsk  { get; set; }
    }
    private sealed class TypeSalesAgg
    {
        public int TypeId { get; set; } public double Units { get; set; } public double Isk { get; set; }
    }
}
