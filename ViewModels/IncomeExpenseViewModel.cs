using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using EveCortex.Data;
using EveCortex.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveCortex.ViewModels;

public class IncomeExpenseRowVm
{
    public string Name   { get; }
    public string Amount { get; }
    public IncomeExpenseRowVm(WalletCategory c) { Name = c.Name; Amount = MarketFmt.Isk((double)c.Amount); }
}

public record IncomeExpensePeriod(string Label, int Days) { public override string ToString() => Label; }

// Income & Expense tool — income category totals on the left, expense on the right, plus a daily
// line chart of total income, total expense, and running cashflow (net). Scoped to authenticated
// characters and personal corporations, over the selected period.
public class IncomeExpenseViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errorLogger;

    public ObservableCollection<IncomeExpenseRowVm> IncomeRows  { get; } = new();
    public ObservableCollection<IncomeExpenseRowVm> ExpenseRows { get; } = new();

    private string _incomeTotal = "—";  public string IncomeTotal  { get => _incomeTotal;  private set => this.RaiseAndSetIfChanged(ref _incomeTotal, value); }
    private string _expenseTotal = "—"; public string ExpenseTotal { get => _expenseTotal; private set => this.RaiseAndSetIfChanged(ref _expenseTotal, value); }
    private string _netTotal = "—";     public string NetTotal     { get => _netTotal;     private set => this.RaiseAndSetIfChanged(ref _netTotal, value); }

    public IReadOnlyList<IncomeExpensePeriod> Periods { get; } =
    [
        new("Last 30 Days",  30),
        new("Last 90 Days",  90),
        new("Last 365 Days", 365),
    ];
    private IncomeExpensePeriod _selectedPeriod;
    public IncomeExpensePeriod SelectedPeriod
    {
        get => _selectedPeriod;
        set { this.RaiseAndSetIfChanged(ref _selectedPeriod, value ?? Periods[1]); _ = LoadAsync(); }
    }

    private ISeries[] _series = [];
    public ISeries[] Series { get => _series; private set => this.RaiseAndSetIfChanged(ref _series, value); }

    public Axis[] XAxes { get; } =
    [
        new Axis
        {
            Labeler    = v => { var t = (long)v; return t < DateTime.MinValue.Ticks || t > DateTime.MaxValue.Ticks ? "" : new DateTime(t).ToString("MMM d"); },
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
            LabelsPaint     = new SolidColorPaint(new SKColor(0x88, 0x88, 0x99)),
            SeparatorsPaint = new SolidColorPaint(new SKColor(0x1e, 0x1e, 0x2e)),
        }
    ];

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }
    private string _statusText = "";
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public IncomeExpenseViewModel(IDbContextFactory<AppDbContext> dbFactory, AppErrorLogger errorLogger)
    {
        _dbFactory      = dbFactory;
        _errorLogger    = errorLogger;
        _selectedPeriod = Periods[1];

        Observable.Interval(TimeSpan.FromMinutes(5))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(tick => { _ = LoadAsync(); });

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        StatusText = "Loading…";
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var charIds = await db.Characters.AsNoTracking().Where(c => c.RefreshToken != "").Select(c => c.Id).ToListAsync();
            var corpIds = await db.Corporations.AsNoTracking().Where(c => c.IsPersonal).Select(c => c.Id).ToListAsync();
            var owners  = charIds.Select(id => ("character", (long)id))
                          .Concat(corpIds.Select(id => ("corporation", (long)id))).ToList();

            var days      = _selectedPeriod.Days;
            var cutoff    = DateTimeOffset.UtcNow.AddDays(-days);
            var refTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var dailyMap  = new Dictionary<string, (decimal Inc, decimal Exp)>();

            foreach (var (ot, oid) in owners)
            {
                var rt = await db.Database.SqlQuery<RefRow>(
                    $"""
                    SELECT "RefType", COALESCE(SUM(CAST("Amount" AS REAL)), 0.0) AS "Total"
                    FROM "EsiWalletJournal"
                    WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                    GROUP BY "RefType"
                    """).ToListAsync();
                foreach (var r in rt)
                    refTotals[r.RefType] = refTotals.GetValueOrDefault(r.RefType) + (decimal)r.Total;

                var dl = await db.Database.SqlQuery<DailyRow>(
                    $"""
                    SELECT substr("Date", 1, 10) AS "Day",
                           COALESCE(SUM(CASE WHEN CAST("Amount" AS REAL) > 0 THEN CAST("Amount" AS REAL) ELSE 0 END), 0.0) AS "Income",
                           COALESCE(SUM(CASE WHEN CAST("Amount" AS REAL) < 0 THEN -CAST("Amount" AS REAL) ELSE 0 END), 0.0) AS "Expense"
                    FROM "EsiWalletJournal"
                    WHERE "OwnerType" = {ot} AND "OwnerId" = {oid} AND "Date" >= {cutoff}
                    GROUP BY substr("Date", 1, 10)
                    """).ToListAsync();
                foreach (var d in dl)
                {
                    var cur = dailyMap.GetValueOrDefault(d.Day);
                    dailyMap[d.Day] = (cur.Inc + (decimal)d.Income, cur.Exp + (decimal)d.Expense);
                }
            }

            var cats = WalletCategorizer.Categorize(refTotals);
            var inc  = cats.Where(c => c.IsIncome).ToList();
            var exp  = cats.Where(c => !c.IsIncome).ToList();

            IncomeRows.Clear();  foreach (var c in inc) IncomeRows.Add(new IncomeExpenseRowVm(c));
            ExpenseRows.Clear(); foreach (var c in exp) ExpenseRows.Add(new IncomeExpenseRowVm(c));

            var incomeTotal  = inc.Sum(c => c.Amount);
            var expenseTotal = exp.Sum(c => c.Amount);
            IncomeTotal  = MarketFmt.Isk((double)incomeTotal);
            ExpenseTotal = MarketFmt.Isk((double)expenseTotal);
            NetTotal     = MarketFmt.Isk((double)(incomeTotal - expenseTotal));

            BuildChart(dailyMap, cutoff.UtcDateTime.Date);
            StatusText = owners.Count == 0 ? "No characters." : $"{_selectedPeriod.Label}";
        }
        catch (Exception ex)
        {
            _errorLogger.Log("IncomeExpenseViewModel", "Load", ex);
            StatusText = "Error loading data.";
        }
        finally { IsLoading = false; }
    }

    private void BuildChart(Dictionary<string, (decimal Inc, decimal Exp)> dailyMap, DateTime start)
    {
        var end = DateTime.UtcNow.Date;
        var incPts  = new List<DateTimePoint>();
        var expPts  = new List<DateTimePoint>();
        var cashPts = new List<DateTimePoint>();

        decimal cash = 0m;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var (inc, exp) = dailyMap.GetValueOrDefault(d.ToString("yyyy-MM-dd"));
            cash += inc - exp;
            incPts.Add(new DateTimePoint(d, (double)inc));
            expPts.Add(new DateTimePoint(d, (double)exp));
            cashPts.Add(new DateTimePoint(d, (double)cash));
        }

        Series =
        [
            Line("Income",   incPts,  new SKColor(0x70, 0xad, 0x47)),
            Line("Expense",  expPts,  new SKColor(0xe0, 0x52, 0x52)),
            Line("Cashflow", cashPts, new SKColor(0xc8, 0xa8, 0x4b), thickness: 3),
        ];
    }

    private static LineSeries<DateTimePoint> Line(string name, List<DateTimePoint> pts, SKColor color, float thickness = 1.5f) =>
        new()
        {
            Name           = name,
            Values         = pts,
            Stroke         = new SolidColorPaint(color) { StrokeThickness = thickness },
            Fill           = null,
            GeometryFill   = null,
            GeometryStroke = null,
            GeometrySize   = 0,
            LineSmoothness = 0.2,
            YToolTipLabelFormatter = p => $"{name}: {p.Coordinate.PrimaryValue:N0} ISK",
        };

    private static string FormatIskAxis(double v)
    {
        var a = Math.Abs(v);
        return a switch
        {
            >= 1_000_000_000_000 => $"{v / 1_000_000_000_000:F1}T",
            >= 1_000_000_000     => $"{v / 1_000_000_000:F1}B",
            >= 1_000_000         => $"{v / 1_000_000:F1}M",
            >= 1_000             => $"{v / 1_000:F1}K",
            _                    => $"{v:F0}",
        };
    }

    private sealed class RefRow   { public string RefType { get; set; } = ""; public double Total { get; set; } }
    private sealed class DailyRow { public string Day { get; set; } = ""; public double Income { get; set; } public double Expense { get; set; } }
}
