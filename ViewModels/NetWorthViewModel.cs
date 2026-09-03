using System.Collections.ObjectModel;
using EveConsole.Data;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveConsole.ViewModels;

public record NetWorthOwnerOption(string DisplayName, bool IsPersonal, long? CorpId);

public enum NetWorthTimeframe { Days90, Days365, YearToDate, PriorYear, Custom }
public record TimeframeOption(string Label, NetWorthTimeframe Kind);

public class NetWorthViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    // ── Owner dropdown ────────────────────────────────────────────────────────

    public ObservableCollection<NetWorthOwnerOption> OwnerOptions { get; } = [];

    private NetWorthOwnerOption? _selectedOwner;
    public NetWorthOwnerOption? SelectedOwner
    {
        get => _selectedOwner;
        set { this.RaiseAndSetIfChanged(ref _selectedOwner, value); _ = LoadDataAsync(); }
    }

    // ── Timeframe ─────────────────────────────────────────────────────────────

    public ObservableCollection<TimeframeOption> TimeframeOptions { get; } =
    [
        new("Last 90 Days",   NetWorthTimeframe.Days90),
        new("Last 365 Days",  NetWorthTimeframe.Days365),
        new("Year to Date",   NetWorthTimeframe.YearToDate),
        new("Prior Year",     NetWorthTimeframe.PriorYear),
        new("Custom Range",   NetWorthTimeframe.Custom),
    ];

    private TimeframeOption _selectedTimeframe;
    public TimeframeOption SelectedTimeframe
    {
        get => _selectedTimeframe;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedTimeframe, value);
            this.RaisePropertyChanged(nameof(IsCustomRange));
            _ = LoadDataAsync();
        }
    }

    private string _customFromText = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
    public string CustomFromText
    {
        get => _customFromText;
        set
        {
            this.RaiseAndSetIfChanged(ref _customFromText, value);
            if (_selectedTimeframe.Kind == NetWorthTimeframe.Custom
                && DateTime.TryParseExact(value, "yyyy-MM-dd", null,
                       System.Globalization.DateTimeStyles.None, out _))
                _ = LoadDataAsync();
        }
    }

    private string _customToText = DateTime.UtcNow.ToString("yyyy-MM-dd");
    public string CustomToText
    {
        get => _customToText;
        set
        {
            this.RaiseAndSetIfChanged(ref _customToText, value);
            if (_selectedTimeframe.Kind == NetWorthTimeframe.Custom
                && DateTime.TryParseExact(value, "yyyy-MM-dd", null,
                       System.Globalization.DateTimeStyles.None, out _))
                _ = LoadDataAsync();
        }
    }

    public bool IsCustomRange => _selectedTimeframe.Kind == NetWorthTimeframe.Custom;

    // ── Chart options ─────────────────────────────────────────────────────────

    private bool _autoRange;
    public bool AutoRange
    {
        get => _autoRange;
        set { this.RaiseAndSetIfChanged(ref _autoRange, value); ApplyAxisOptions(); }
    }

    private bool _isLogScale;
    public bool IsLogScale
    {
        get => _isLogScale;
        set { this.RaiseAndSetIfChanged(ref _isLogScale, value); BuildSeries(_cachedRows); }
    }

    // ── Chart ─────────────────────────────────────────────────────────────────

    private ISeries[] _series = [];
    public ISeries[] Series
    {
        get => _series;
        private set => this.RaiseAndSetIfChanged(ref _series, value);
    }

    public Axis[] XAxes { get; } =
    [
        new Axis
        {
            Labeler    = value =>
            {
                var ticks = (long)value;
                return ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
                    ? ""
                    : new DateTime(ticks).ToString("MMM d");
            },
            UnitWidth  = TimeSpan.FromDays(1).Ticks,
            MinStep    = TimeSpan.FromDays(1).Ticks,
            TextSize   = 11,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    public Axis[] YAxes { get; } =
    [
        new Axis
        {
            Labeler         = FormatIskAxis,
            TextSize        = 11,
            MinLimit        = 0,
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    // ── KPI strip ─────────────────────────────────────────────────────────────

    private string _currentDate       = "—";
    private string _currentTotal      = "—";
    private string _currentAssets     = "—";
    private string _currentIndustry   = "—";
    private string _currentWallet     = "—";
    private string _currentSellOrders = "—";
    private string _currentBuyEscrow  = "—";
    private string _currentCollateral = "—";
    private string _currentContracts  = "—";

    public string CurrentDate       { get => _currentDate;       private set => this.RaiseAndSetIfChanged(ref _currentDate,       value); }
    public string CurrentTotal      { get => _currentTotal;      private set => this.RaiseAndSetIfChanged(ref _currentTotal,      value); }
    public string CurrentAssets     { get => _currentAssets;     private set => this.RaiseAndSetIfChanged(ref _currentAssets,     value); }
    public string CurrentIndustry   { get => _currentIndustry;   private set => this.RaiseAndSetIfChanged(ref _currentIndustry,   value); }
    public string CurrentWallet     { get => _currentWallet;     private set => this.RaiseAndSetIfChanged(ref _currentWallet,     value); }
    public string CurrentSellOrders { get => _currentSellOrders; private set => this.RaiseAndSetIfChanged(ref _currentSellOrders, value); }
    public string CurrentBuyEscrow  { get => _currentBuyEscrow;  private set => this.RaiseAndSetIfChanged(ref _currentBuyEscrow,  value); }
    public string CurrentCollateral { get => _currentCollateral; private set => this.RaiseAndSetIfChanged(ref _currentCollateral, value); }
    public string CurrentContracts  { get => _currentContracts;  private set => this.RaiseAndSetIfChanged(ref _currentContracts,  value); }

    // ── State ─────────────────────────────────────────────────────────────────

    private bool _isEmpty = true;
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => this.RaiseAndSetIfChanged(ref _isEmpty, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private List<DayRow> _cachedRows = [];

    // ── Construction ─────────────────────────────────────────────────────────

    public NetWorthViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _selectedTimeframe = TimeframeOptions[0];
    }

    public async Task InitializeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var corps = await db.Corporations
            .Where(c => !c.IsPersonal)
            .OrderBy(c => c.Name)
            .AsNoTracking()
            .ToListAsync();

        OwnerOptions.Clear();
        OwnerOptions.Add(new NetWorthOwnerOption("Personal", IsPersonal: true, CorpId: null));
        foreach (var c in corps)
            OwnerOptions.Add(new NetWorthOwnerOption(c.Name, IsPersonal: false, CorpId: c.Id));

        _selectedOwner = OwnerOptions[0];
        this.RaisePropertyChanged(nameof(SelectedOwner));
        await LoadDataAsync();
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    /// <param name="Owners">How many owner rows the day is built from. Carried so a day still
    /// being written can be told from a day that is genuinely smaller.</param>
    private record DayRow(
        string Date, double Assets, double Industry, double Wallet,
        double SellOrders, double BuyEscrow, double Collateral, double Contracts, double Total,
        int Owners = 1);

    private async Task LoadDataAsync()
    {
        if (_selectedOwner is null) return;
        IsLoading = true;

        try
        {
            var rows = await FetchRowsAsync(_selectedOwner);
            _cachedRows = rows;
            IsEmpty = rows.Count == 0;
            BuildSeries(rows);
            UpdateKpi(rows.Count > 0 ? rows[^1] : null);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private (string From, string To) GetDateRange()
    {
        var today = DateTime.UtcNow.Date;
        return _selectedTimeframe.Kind switch
        {
            NetWorthTimeframe.Days90     => (today.AddDays(-90).ToString("yyyy-MM-dd"),                today.ToString("yyyy-MM-dd")),
            NetWorthTimeframe.Days365    => (today.AddDays(-365).ToString("yyyy-MM-dd"),               today.ToString("yyyy-MM-dd")),
            NetWorthTimeframe.YearToDate => (new DateTime(today.Year, 1, 1).ToString("yyyy-MM-dd"),    today.ToString("yyyy-MM-dd")),
            NetWorthTimeframe.PriorYear  => (new DateTime(today.Year - 1, 1, 1).ToString("yyyy-MM-dd"), new DateTime(today.Year - 1, 12, 31).ToString("yyyy-MM-dd")),
            NetWorthTimeframe.Custom     => (
                DateTime.TryParseExact(_customFromText, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var cf)
                    ? cf.ToString("yyyy-MM-dd") : today.AddDays(-30).ToString("yyyy-MM-dd"),
                DateTime.TryParseExact(_customToText, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var ct)
                    ? ct.ToString("yyyy-MM-dd") : today.ToString("yyyy-MM-dd")),
            _ => (today.AddDays(-90).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd")),
        };
    }

    private async Task<List<DayRow>> FetchRowsAsync(NetWorthOwnerOption owner)
    {
        var (fromDate, toDate) = GetDateRange();

        await using var db   = await _dbFactory.CreateDbContextAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        var sql = owner.IsPersonal ? PersonalSql : CorpSql;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.AddWithValue("@fromDate", fromDate);
        cmd.AddWithValue("@toDate",   toDate);
        if (!owner.IsPersonal)
            cmd.AddWithValue("@corpId", owner.CorpId!.Value);

        using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<DayRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new DayRow(
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.GetDouble(8),
                reader.FieldCount > 9 ? reader.GetInt32(9) : 1));
        }

        return DropPartialDay(rows);
    }

    /// <summary>
    /// Drops the newest day while it is still being written.
    ///
    /// <para>⚠️ A day's snapshot is not one write. NetWorthService recalculates one owner
    /// at a time as each owner's poll finishes, so at the midnight flip the current date
    /// exists with some owners in it and not others — and the chart plotted that as a real
    /// point. Reading at 00:05, with two of twenty-five owners written, showed net worth
    /// down 20%; by 00:07 it was whole again and the drop had never happened.</para>
    ///
    /// <para>Recognised by owner count rather than by clock: comparing against the previous
    /// day needs no assumption about how long the write takes or when it starts, and a day
    /// that is genuinely smaller — an owner removed — settles on the next day's read rather
    /// than being hidden forever.</para>
    /// </summary>
    private static List<DayRow> DropPartialDay(List<DayRow> rows) =>
        rows.Count >= 2 && rows[^1].Owners < rows[^2].Owners
            ? rows[..^1]
            : rows;

    // ── Chart building ────────────────────────────────────────────────────

    private void ApplyAxisOptions()
    {
        YAxes[0].MinLimit = _autoRange && !_isLogScale ? null : (double?)0;
    }

    private void BuildSeries(List<DayRow> rows)
    {
        if (rows.Count == 0) { Series = []; return; }

        Func<double, double> scale = _isLogScale
            ? v => Math.Log10(Math.Max(v, 1.0))
            : v => v;

        YAxes[0].MinLimit = _autoRange && !_isLogScale ? null : (double?)0;
        YAxes[0].Labeler  = _isLogScale
            ? v => FormatIskAxis(Math.Pow(10, v))
            : FormatIskAxis;

        bool log = _isLogScale;
        Series =
        [
            MakeLine("Net Worth (Total)", rows, r => scale(r.Total),      0xc8, 0xa8, 0x4b, thickness: 3, logScale: log),
            MakeLine("Assets",            rows, r => scale(r.Assets),     0x5b, 0x9b, 0xd5, logScale: log),
            MakeLine("Wallet",            rows, r => scale(r.Wallet),     0x70, 0xad, 0x47, logScale: log),
            MakeLine("Industry Jobs",     rows, r => scale(r.Industry),   0xed, 0x7d, 0x31, logScale: log),
            MakeLine("Sell Orders",       rows, r => scale(r.SellOrders), 0xa8, 0x79, 0xd8, logScale: log),
            MakeLine("Buy Escrow",        rows, r => scale(r.BuyEscrow),  0x17, 0xbe, 0xcf, logScale: log),
            MakeLine("Contract Colat.",   rows, r => scale(r.Collateral), 0xe7, 0x4c, 0x3c, logScale: log),
            MakeLine("Contract Value",    rows, r => scale(r.Contracts),  0xf1, 0xc4, 0x0f, logScale: log),
        ];
    }

    private static LineSeries<DateTimePoint> MakeLine(
        string name, List<DayRow> rows, Func<DayRow, double> selector,
        byte r, byte g, byte b, float thickness = 1.5f, bool logScale = false)
    {
        var color  = new SKColor(r, g, b);
        var points = rows.Select(row => new DateTimePoint(
            DateTime.ParseExact(row.Date, "yyyy-MM-dd", null), selector(row))).ToArray();

        Func<ChartPoint<DateTimePoint, CircleGeometry, LabelGeometry>, string> formatter = logScale
            ? p => $"{FormatIskFull(Math.Pow(10, p.Coordinate.PrimaryValue))} ISK"
            : p => $"{FormatIskFull(p.Coordinate.PrimaryValue)} ISK";

        return new LineSeries<DateTimePoint>
        {
            Name                   = name,
            Values                 = points,
            Stroke                 = new SolidColorPaint(color) { StrokeThickness = thickness },
            Fill                   = null,
            GeometryFill           = new SolidColorPaint(color),
            GeometryStroke         = null,
            GeometrySize           = thickness > 2 ? 7 : 4,
            LineSmoothness         = 0.3,
            YToolTipLabelFormatter = formatter,
        };
    }

    // ── KPI strip ─────────────────────────────────────────────────────────────

    private void UpdateKpi(DayRow? row)
    {
        if (row is null)
        {
            CurrentDate = "—"; CurrentTotal = "—"; CurrentAssets = "—";
            CurrentIndustry = "—"; CurrentWallet = "—"; CurrentSellOrders = "—";
            CurrentBuyEscrow = "—"; CurrentCollateral = "—"; CurrentContracts = "—";
            return;
        }
        CurrentDate       = $"As of {row.Date}";
        CurrentTotal      = FormatIskKpi(row.Total);
        CurrentAssets     = FormatIskKpi(row.Assets);
        CurrentIndustry   = FormatIskKpi(row.Industry);
        CurrentWallet     = FormatIskKpi(row.Wallet);
        CurrentSellOrders = FormatIskKpi(row.SellOrders);
        CurrentBuyEscrow  = FormatIskKpi(row.BuyEscrow);
        CurrentCollateral = FormatIskKpi(row.Collateral);
        CurrentContracts  = FormatIskKpi(row.Contracts);
    }

    // ── Formatting ────────────────────────────────────────────────────────────

    private static string FormatIskAxis(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:F1}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:F1}B",
        >= 1_000_000         => $"{v / 1_000_000:F1}M",
        >= 1_000             => $"{v / 1_000:F1}K",
        _                    => $"{v:F0}",
    };

    private static string FormatIskKpi(double v) => v switch
    {
        >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:N2}T",
        >= 1_000_000_000     => $"{v / 1_000_000_000:N2}B",
        >= 1_000_000         => $"{v / 1_000_000:N2}M",
        _                    => $"{v:N2}",
    };

    private static string FormatIskFull(double v) => $"{v:N2}";

    // ── SQL ───────────────────────────────────────────────────────────────────

    private const string PersonalSql = """
        SELECT n."Date",
               ROUND(SUM(n."AssetValue"),         2),
               ROUND(SUM(n."IndustryJobValue"),   2),
               ROUND(SUM(n."WalletBalance"),       2),
               ROUND(SUM(n."SellOrderValue"),      2),
               ROUND(SUM(n."BuyOrderEscrow"),      2),
               ROUND(SUM(n."ContractCollateral"),  2),
               ROUND(SUM(n."ContractValue"),        2),
               ROUND(SUM(n."Total"),               2),
               COUNT(*)
        FROM "NetWorthSnapshots" n
        WHERE n."Date" >= @fromDate AND n."Date" <= @toDate
          AND NOT (
              n."OwnerType" = 'corporation'
              AND n."OwnerId" IN (
                  SELECT CAST("Id" AS INTEGER) FROM "Corporations" WHERE "IsPersonal" = FALSE
              )
          )
        GROUP BY n."Date"
        ORDER BY n."Date"
        """;

    private const string CorpSql = """
        SELECT "Date",
               ROUND("AssetValue",         2),
               ROUND("IndustryJobValue",   2),
               ROUND("WalletBalance",       2),
               ROUND("SellOrderValue",      2),
               ROUND("BuyOrderEscrow",      2),
               ROUND("ContractCollateral",  2),
               ROUND("ContractValue",        2),
               ROUND("Total",               2)
        FROM "NetWorthSnapshots"
        WHERE "OwnerId" = @corpId AND "OwnerType" = 'corporation'
          AND "Date" >= @fromDate AND "Date" <= @toDate
        ORDER BY "Date"
        """;
}
