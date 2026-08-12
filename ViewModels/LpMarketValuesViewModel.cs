using System.Collections.ObjectModel;
using Avalonia.Threading;
using EveConsole.Data;
using EveConsole.Models;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using SkiaSharp;

namespace EveConsole.ViewModels;

/// <summary>One corporation's current LP value, as shown on the Current Values tab.</summary>
public record LpCorpValueVm(
    int    CorporationId,
    string CorporationName,
    double IskPerLp,
    double MedianIskPerLp,
    int    ValuedOffers,
    int    TotalOffers,
    double BestIskPerLp,
    string BestOfferName,
    int    LpHeld,
    DateTimeOffset ComputedAt)
{
    public string IskPerLpText => Format(IskPerLp);
    public string MedianText   => Format(MedianIskPerLp);
    public string BestText     => Format(BestIskPerLp);

    /// <summary>The mean is dimmed when it disagrees sharply with the median — that gap is
    /// the signature of a store with a few extreme offers rather than a bad rate.</summary>
    public string MeanColor =>
        Math.Abs(IskPerLp - MedianIskPerLp) > Math.Max(50, Math.Abs(MedianIskPerLp))
            ? "#aa7744" : "#889";

    /// <summary>Values span several orders of magnitude between corporations, so the
    /// precision follows the number rather than being fixed.</summary>
    private static string Format(double v) =>
        Math.Abs(v) >= 100 ? v.ToString("N0")
        : Math.Abs(v) >= 1 ? v.ToString("N2")
                           : v.ToString("N4");

    public string CoverageText => TotalOffers == 0
        ? "—"
        : $"{ValuedOffers:N0} / {TotalOffers:N0}";

    /// <summary>An average resting on a small slice of the catalogue is worth less trust,
    /// so the coverage is dimmed when most offers could not be priced.</summary>
    public string CoverageColor =>
        TotalOffers > 0 && ValuedOffers * 2 < TotalOffers ? "#aa7744" : "#889";

    public string HeldText  => LpHeld > 0 ? $"{LpHeld:N0}" : "—";
    public string HeldColor => LpHeld > 0 ? "#4caf50" : "#555566";

    /// <summary>What the balance is worth at the median rate. The median, not the mean —
    /// valuing a holding off a figure a handful of freak offers moved would be the least
    /// useful place to use it.</summary>
    public string HoldingValueText => LpHeld > 0 && MedianIskPerLp > 0
        ? $"{LpHeld * MedianIskPerLp:N0} ISK"
        : "—";

    public string UpdatedText => ComputedAt == default
        ? "—"
        : ComputedAt.ToLocalTime().ToString("d MMM HH:mm");
}

public record LpHistoryPeriod(string Label, int Days);   // Days = -1 → all time

/// <summary>
/// LP Market Values. What a loyalty point is worth at each corporation now, and how that
/// has moved.
/// </summary>
public class LpMarketValuesViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ObservableCollection<LpCorpValueVm> Corps { get; } = [];

    public static LpHistoryPeriod[] Periods { get; } =
    [
        new("Past 30 Days",  30),
        new("Past 90 Days",  90),
        new("Past 365 Days", 365),
        new("All Time",      -1),
    ];

    public LpMarketValuesViewModel(IDbContextFactory<AppDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
        _selectedPeriod = Periods[2];        // Past 365 Days
        _ = LoadAsync();
    }

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
    }

    private string _status = "Loading…";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    public bool HasCorps => Corps.Count > 0;

    // ── Current values ────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var values = await db.LpCorpValues.AsNoTracking().ToListAsync();
            if (values.Count == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Corps.Clear();
                    this.RaisePropertyChanged(nameof(HasCorps));
                    Status = "No LP values yet — they are calculated when market prices refresh.";
                });
                return;
            }

            var corpIds = values.Select(v => v.CorporationId).ToList();
            var names = await db.SdeNpcCorporations.AsNoTracking()
                .Where(c => corpIds.Contains(c.CorporationId))
                .ToDictionaryAsync(c => c.CorporationId, c => c.Name);

            var typeIds = values.Select(v => v.BestTypeId).Distinct().ToList();
            var typeNames = await db.SdeTypes.AsNoTracking()
                .Where(t => typeIds.Contains(t.TypeId))
                .ToDictionaryAsync(t => t.TypeId, t => t.Name);

            // Highest balance any one character holds — LP cannot be pooled to spend.
            var held = (await db.EsiLoyaltyPoints.AsNoTracking()
                    .Where(l => corpIds.Contains(l.CorporationId)).ToListAsync())
                .GroupBy(l => l.CorporationId)
                .ToDictionary(g => g.Key, g => g.Max(l => l.Points));

            var rows = values
                .Select(v => new LpCorpValueVm(
                    v.CorporationId,
                    names.GetValueOrDefault(v.CorporationId, $"Corp {v.CorporationId}"),
                    v.IskPerLp, v.MedianIskPerLp, v.ValuedOffers, v.TotalOffers, v.BestIskPerLp,
                    typeNames.GetValueOrDefault(v.BestTypeId, ""),
                    held.GetValueOrDefault(v.CorporationId),
                    v.ComputedAt))
                // Corporations you hold LP with first — those are the rates that can be
                // acted on — then by value.
                .OrderByDescending(r => r.LpHeld > 0)
                .ThenByDescending(r => r.MedianIskPerLp)
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Corps.Clear();
                foreach (var r in rows) Corps.Add(r);
                this.RaisePropertyChanged(nameof(HasCorps));

                HistoryCorps.Clear();
                foreach (var r in rows.OrderBy(r => r.CorporationName)) HistoryCorps.Add(r);
                SelectedHistoryCorp ??= HistoryCorps.FirstOrDefault();

                Status = $"{rows.Count:N0} corporation(s) valued — updated {rows.Max(r => r.ComputedAt).ToLocalTime():d MMM HH:mm}";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = $"Error: {ex.Message}");
        }
    }

    // ── History ───────────────────────────────────────────────────────────────

    public ObservableCollection<LpCorpValueVm> HistoryCorps { get; } = [];

    private LpCorpValueVm? _selectedHistoryCorp;
    public LpCorpValueVm? SelectedHistoryCorp
    {
        get => _selectedHistoryCorp;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedHistoryCorp, value);
            _ = LoadHistoryAsync();
        }
    }

    private LpHistoryPeriod _selectedPeriod;
    public LpHistoryPeriod SelectedPeriod
    {
        get => _selectedPeriod;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedPeriod, value);
            _ = LoadHistoryAsync();
        }
    }

    private string _historyStatus = "";
    public string HistoryStatus { get => _historyStatus; private set => this.RaiseAndSetIfChanged(ref _historyStatus, value); }

    private ISeries[]? _series;
    private Axis[]?    _xAxes;
    private Axis[]?    _yAxes;
    public ISeries[] Series { get => _series ?? []; private set => this.RaiseAndSetIfChanged(ref _series, value); }
    public Axis[]    XAxes  { get => _xAxes  ?? []; private set => this.RaiseAndSetIfChanged(ref _xAxes,  value); }
    public Axis[]    YAxes  { get => _yAxes  ?? []; private set => this.RaiseAndSetIfChanged(ref _yAxes,  value); }

    /// <summary>Opens the History tab on a given corporation — used by the double-click on
    /// the Current Values grid.</summary>
    public void ShowHistoryFor(int corporationId)
    {
        var match = HistoryCorps.FirstOrDefault(c => c.CorporationId == corporationId);
        if (match is not null) SelectedHistoryCorp = match;
        SelectedTabIndex = 1;
    }

    public async Task LoadHistoryAsync()
    {
        var corp   = SelectedHistoryCorp;
        var period = SelectedPeriod;
        if (corp is null) { Series = []; XAxes = []; YAxes = []; return; }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            // Dates are stored as "yyyy-MM-dd" strings and compared as such — SQLite cannot
            // translate a DateTimeOffset comparison, and the ISO form sorts correctly anyway.
            var query = db.LpCorpValueSnapshots.AsNoTracking()
                .Where(s => s.CorporationId == corp.CorporationId);

            if (period.Days > 0)
            {
                var from = DateTime.UtcNow.AddDays(-period.Days).ToString("yyyy-MM-dd");
                query = query.Where(s => string.Compare(s.Date, from) >= 0);
            }

            var rows = await query.OrderBy(s => s.Date).ToListAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (rows.Count == 0)
                {
                    Series = []; XAxes = []; YAxes = [];
                    HistoryStatus = $"No history yet for {corp.CorporationName} in this period. "
                                  + "A point is recorded each time values are recalculated.";
                    return;
                }

                var points = rows
                    .Select(r => new DateTimePoint(
                        DateTime.TryParse(r.Date, out var d) ? d : DateTime.MinValue, r.MedianIskPerLp))
                    .ToList();

                Series =
                [
                    new LineSeries<DateTimePoint>
                    {
                        Name           = "ISK per LP",
                        Values         = points,
                        Stroke         = new SolidColorPaint(SKColors.Gold, 2),
                        Fill           = null,
                        GeometryFill   = null,
                        GeometryStroke = null,
                        YToolTipLabelFormatter = p => $"{p.Coordinate.PrimaryValue:N2} ISK/LP",
                    },
                ];

                XAxes =
                [
                    new DateTimeAxis(TimeSpan.FromDays(1), d => d.ToString("MMM d"))
                    {
                        LabelsPaint     = new SolidColorPaint(new SKColor(136, 136, 153)),
                        SeparatorsPaint = new SolidColorPaint(new SKColor(40, 40, 60)),
                    },
                ];

                YAxes =
                [
                    new Axis
                    {
                        Name            = "ISK per LP",
                        LabelsPaint     = new SolidColorPaint(new SKColor(200, 168, 75)),
                        SeparatorsPaint = new SolidColorPaint(new SKColor(40, 40, 60)),
                        Labeler         = v => Math.Abs(v) >= 100 ? v.ToString("N0") : v.ToString("N2"),
                    },
                ];

                HistoryStatus = $"{rows.Count:N0} day(s), median — {rows.Min(r => r.MedianIskPerLp):N2} to "
                              + $"{rows.Max(r => r.MedianIskPerLp):N2} ISK/LP";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => HistoryStatus = $"Error: {ex.Message}");
        }
    }
}
