using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Threading;
using System.Threading.Tasks;
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

/// <summary>One industry job that produced, or will produce, something the operation sells.</summary>
public sealed class FinalProductJobVm
{
    public int    JobId    { get; init; }
    public int    TypeId   { get; init; }
    public string Item     { get; init; } = "";
    public string Source   { get; init; } = "";   // why it is on this list
    public string Status   { get; init; } = "";

    public int    Runs      { get; init; }
    public long   Units     { get; init; }
    public string UnitsText => Units.ToString("N0");

    public DateTime Started    { get; init; }
    public DateTime Completed  { get; init; }
    public string StartedText   => Started.ToString("yyyy-MM-dd");
    public string CompletedText => Completed.ToString("yyyy-MM-dd");

    /// <summary>Sorted on the tick count: the column shows a date, and two jobs finishing on one
    /// day are not interchangeable.</summary>
    public long CompletedSort => Completed.Ticks;

    /// <summary>True where the job has not finished yet, so its figures are today's rather than
    /// the day's it will land on.</summary>
    public bool  IsFuture { get; init; }
    public string AsOfNote => IsFuture ? "today" : CompletedText;

    public double BuildCost   { get; init; }
    public double MarketValue { get; init; }
    public double Profit      => MarketValue - BuildCost;

    public string BuildText  => BuildCost   > 0 ? MarketFmt.Isk(BuildCost)   : "—";
    public string MarketText => MarketValue > 0 ? MarketFmt.Isk(MarketValue) : "—";
    public string ProfitText => BuildCost > 0 || MarketValue > 0 ? MarketFmt.Isk(Profit) : "—";

    public string ProfitColor => Profit >= 0 ? "#4a8a5a" : "#aa4444";

    public double ProfitPctRaw => BuildCost > 0 ? Profit / BuildCost * 100 : double.MinValue;
    public string ProfitPct    => BuildCost > 0 ? $"{Profit / BuildCost * 100:N1}%" : "—";
}

public sealed record ChartGrain(string Label, string Key)
{
    public override string ToString() => Label;
}

/// <summary>
/// What the worklist is ultimately for: the things the operation sells.
///
/// <para>The rest of the tool is about the work in front of you. This is the record of what that
/// work produced — every job, past and running, whose output is a final product or something a
/// buyer has ordered, with what it cost and what it was worth on the day it landed.</para>
///
/// <para>⚠️ Two sources of "final", because neither is complete on its own. The inventory rules
/// carry a hand-set final-product flag, which is the operation's own statement of what it sells;
/// but an order can name anything, and a T2 rig or a batch of battleships sold to one buyer never
/// appears in those rules. Taking the union means the list matches what was actually sold rather
/// than what was configured.</para>
/// </summary>
public class WorklistFinalProductsViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppErrorLogger _errorLogger;

    public ObservableCollection<FinalProductJobVm> Jobs { get; } = [];

    public IReadOnlyList<ChartGrain> Grains { get; } =
    [
        new("Daily",   "d"),
        new("Weekly",  "w"),
        new("Monthly", "m"),
    ];

    private ChartGrain _grain;
    public ChartGrain Grain
    {
        get => _grain;
        set { this.RaiseAndSetIfChanged(ref _grain, value ?? Grains[0]); BuildCharts(); }
    }

    private string _from;
    public string From
    {
        get => _from;
        set { this.RaiseAndSetIfChanged(ref _from, value ?? ""); Apply(); }
    }

    private string _thru = "";
    public string Thru
    {
        get => _thru;
        set { this.RaiseAndSetIfChanged(ref _thru, value ?? ""); Apply(); }
    }

    private string _status = "";
    public string StatusText { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private bool _loading;
    public bool IsLoading { get => _loading; private set => this.RaiseAndSetIfChanged(ref _loading, value); }

    private ISeries[] _marketSeries = [];
    public ISeries[] MarketSeries { get => _marketSeries; private set => this.RaiseAndSetIfChanged(ref _marketSeries, value); }

    private ISeries[] _profitSeries = [];
    public ISeries[] ProfitSeries { get => _profitSeries; private set => this.RaiseAndSetIfChanged(ref _profitSeries, value); }

    public Axis[] XAxes { get; } =
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

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Everything loaded, before the date filter. Kept so changing a date does not go
    /// back to the database for figures that have not moved.</summary>
    private List<FinalProductJobVm> _all = [];

    public WorklistFinalProductsViewModel(IDbContextFactory<AppDbContext> dbFactory,
                                          AppErrorLogger errorLogger)
    {
        _dbFactory   = dbFactory;
        _errorLogger = errorLogger;

        _grain = Grains[0];

        // A year back, so the charts open on something worth looking at. Blank "thru" because the
        // interesting end of this list is the jobs that have not finished yet.
        _from = DateTime.UtcNow.Date.AddYears(-1).ToString("yyyy-MM-dd");

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            var rows = await Task.Run(() => GatherAsync(ct), ct);
            _all = rows;
            Apply();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(WorklistFinalProductsViewModel), nameof(LoadAsync), ex);
            StatusText = $"Could not load: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private async Task<List<FinalProductJobVm>> GatherAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // ── What counts as a final product ────────────────────────────────────
        var finalGroups = await db.WorklistInvRules.AsNoTracking()
            .Where(r => r.Enabled && r.IsFinalProduct)
            .Select(r => r.GroupId)
            .Distinct()
            .ToListAsync(ct);

        var finalTypes = finalGroups.Count == 0
            ? []
            : await db.InvLevelItems.AsNoTracking()
                .Where(i => finalGroups.Contains(i.GroupId))
                .Select(i => i.TypeId)
                .Distinct()
                .ToListAsync(ct);

        var orderTypes = await db.TrackedOrders.AsNoTracking()
            .Select(o => o.TypeId)
            .Distinct()
            .ToListAsync(ct);

        var finalSet = finalTypes.ToHashSet();
        var orderSet = orderTypes.ToHashSet();

        var typeIds = finalSet.Union(orderSet).ToList();
        if (typeIds.Count == 0) return [];

        // ── The jobs ──────────────────────────────────────────────────────────
        // Cancelled jobs produced nothing and cost nothing worth reporting; everything else,
        // finished or still running, belongs on the record.
        var jobs = await db.EsiIndustryJobs.AsNoTracking()
            .Where(j => j.ProductTypeId != null
                     && typeIds.Contains(j.ProductTypeId!.Value)
                     && j.Status != "cancelled")
            .Select(j => new
            {
                j.JobId, j.ProductTypeId, j.Runs, j.Status,
                j.StartDate, j.EndDate, j.CompletedDate,
            })
            .ToListAsync(ct);

        if (jobs.Count == 0) return [];

        var names = await db.SdeTypes.AsNoTracking()
            .Where(t => typeIds.Contains(t.TypeId))
            .ToDictionaryAsync(t => t.TypeId, t => t.Name, ct);

        // A run is not a unit. Reactions especially produce in batches, and pricing a job by its
        // run count would understate every one of them.
        var perRun = await db.SdeBlueprintProducts.AsNoTracking()
            .Where(p => typeIds.Contains(p.ProductTypeId)
                     && (p.Activity == "manufacturing" || p.Activity == "reaction"))
            .GroupBy(p => p.ProductTypeId)
            .Select(g => new { TypeId = g.Key, Qty = g.Max(x => x.Quantity) })
            .ToDictionaryAsync(x => x.TypeId, x => Math.Max(1, x.Qty), ct);

        // ── Prices, by day ────────────────────────────────────────────────────
        // ⚠️ Only the type filter goes into SQL. Date is stored as text and the as-of rule is a
        // walk rather than a comparison, so the series come back whole and are searched here.
        var snaps = await db.TypePriceSnapshots.AsNoTracking()
            .Where(s => typeIds.Contains(s.TypeId))
            .Select(s => new { s.TypeId, s.Date, s.MarketValue, s.BuildCost })
            .ToListAsync(ct);

        var buildBy = snaps
            .Where(s => s.BuildCost != null)
            .GroupBy(s => s.TypeId)
            .ToDictionary(g => g.Key, g => g
                .Select(s => (s.Date, Value: s.BuildCost!.Value))
                .OrderBy(s => s.Date, StringComparer.Ordinal)
                .ToList());

        var marketBy = snaps
            .Where(s => s.MarketValue != null)
            .GroupBy(s => s.TypeId)
            .ToDictionary(g => g.Key, g => g
                .Select(s => (s.Date, Value: s.MarketValue!.Value))
                .OrderBy(s => s.Date, StringComparer.Ordinal)
                .ToList());

        var today = DateTime.UtcNow.Date;
        var list  = new List<FinalProductJobVm>(jobs.Count);

        foreach (var j in jobs)
        {
            ct.ThrowIfCancellationRequested();

            var typeId = j.ProductTypeId!.Value;

            // Finished when it says so, otherwise when it is due. A running job's figures are as
            // good as today's prices, which is what the note beside them says.
            var completed = (j.CompletedDate ?? j.EndDate).UtcDateTime.Date;
            var future    = completed > today;

            // ⚠️ The same rule the Sales Tracker uses: the day itself, else the next day carrying
            // a figure, else the last one before it. A price file with a gap on the day a job
            // landed is normal, and looking only backwards reports nothing at all on a database
            // whose snapshots all postdate the job.
            var asOf  = (future ? today : completed).ToString("yyyy-MM-dd");
            var units = (long)j.Runs * perRun.GetValueOrDefault(typeId, 1);

            var unitBuild  = buildBy.TryGetValue(typeId, out var bs)
                           ? TypePriceHistoryService.ValueAsOf(bs, asOf) : null;
            var unitMarket = marketBy.TryGetValue(typeId, out var ms)
                           ? TypePriceHistoryService.ValueAsOf(ms, asOf) : null;

            list.Add(new FinalProductJobVm
            {
                JobId  = j.JobId,
                TypeId = typeId,
                Item   = names.GetValueOrDefault(typeId, $"Type {typeId}"),

                // Which list put it here. An item on both is a final product that also happens to
                // have been ordered, and saying so is more use than picking one.
                Source = finalSet.Contains(typeId) && orderSet.Contains(typeId) ? "Final · ordered"
                       : finalSet.Contains(typeId)                              ? "Final product"
                       :                                                          "Ordered",

                Status    = j.Status.Length > 0 ? char.ToUpper(j.Status[0]) + j.Status[1..] : j.Status,
                Runs      = j.Runs,
                Units     = units,
                Started   = j.StartDate.UtcDateTime.Date,
                Completed = completed,
                IsFuture  = future,

                BuildCost   = (unitBuild  ?? 0) * units,
                MarketValue = (unitMarket ?? 0) * units,
            });
        }

        return list;
    }

    /// <summary>Applies the date range, re-sorts, and rebuilds both charts.</summary>
    private void Apply()
    {
        IEnumerable<FinalProductJobVm> q = _all;

        if (TryDate(From, out var from)) q = q.Where(r => r.Completed >= from);
        if (TryDate(Thru, out var thru)) q = q.Where(r => r.Completed <= thru);

        // Newest first: the question this list answers is usually "what have we just made".
        var rows = q.OrderByDescending(r => r.CompletedSort).ToList();

        Jobs.Clear();
        foreach (var r in rows) Jobs.Add(r);

        StatusText = rows.Count == 0
            ? "No jobs in this range."
            : $"{rows.Count:N0} job(s) · {MarketFmt.Isk(rows.Sum(r => r.MarketValue))} market value · "
            + $"{MarketFmt.Isk(rows.Sum(r => r.Profit))} profit";

        BuildCharts();
    }

    private void BuildCharts()
    {
        var rows = Jobs.ToList();
        if (rows.Count == 0)
        {
            MarketSeries = [];
            ProfitSeries = [];
            return;
        }

        var market = new List<DateTimePoint>();
        var profit = new List<DateTimePoint>();

        foreach (var g in rows.GroupBy(r => Bucket(r.Completed)).OrderBy(g => g.Key))
        {
            market.Add(new DateTimePoint(g.Key, g.Sum(r => r.MarketValue)));
            profit.Add(new DateTimePoint(g.Key, g.Sum(r => r.Profit)));
        }

        MarketSeries = [Line("Market value", market, new SKColor(0x55, 0x99, 0xaa))];
        ProfitSeries = [Line("Profit",       profit, new SKColor(0x4a, 0x8a, 0x5a))];
    }

    /// <summary>
    /// The day a job's figures are counted against, at the chosen granularity.
    ///
    /// <para>⚠️ Weeks start on Monday rather than on the first row's day. A bucket that floats
    /// with the data cannot be compared between two loads of the same chart.</para>
    /// </summary>
    private DateTime Bucket(DateTime day) => Grain.Key switch
    {
        "m" => new DateTime(day.Year, day.Month, 1),
        "w" => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        _   => day,
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
            YToolTipLabelFormatter = p => $"{name}: {p.Coordinate.PrimaryValue:N0} ISK",
        };

    private static string FormatIskAxis(double v) =>
        Math.Abs(v) >= 1_000_000_000_000 ? $"{v / 1_000_000_000_000:N1}T"
      : Math.Abs(v) >= 1_000_000_000     ? $"{v / 1_000_000_000:N1}B"
      : Math.Abs(v) >= 1_000_000         ? $"{v / 1_000_000:N1}M"
      : Math.Abs(v) >= 1_000             ? $"{v / 1_000:N1}K"
      :                                    v.ToString("N0");

    private static bool TryDate(string s, out DateTime date)
    {
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            date = d.Date;
            return true;
        }
        date = default;
        return false;
    }
}
