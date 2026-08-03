using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Settings and progress for the map-statistics pipeline. The progress readout matters more
/// than usual here: the first run fetches weeks of history across several datasets, and
/// without this the only way to tell whether it had finished was to query the database.
/// </summary>
public class MapStatsSettingsViewModel : ReactiveObject
{
    private readonly MapStatsSettings         _settings;
    private readonly MapStatsBackfillService  _backfill;
    private readonly MapStatsPollingService   _polling;
    private readonly MapStatsService          _stats;

    private bool _loading = true;

    public MapStatsSettingsViewModel(
        MapStatsSettings        settings,
        MapStatsBackfillService backfill,
        MapStatsPollingService  polling,
        MapStatsService         stats)
    {
        _settings = settings;
        _backfill = backfill;
        _polling  = polling;
        _stats    = stats;

        _enabled        = settings.Enabled;
        _backfillDays   = settings.BackfillDays.ToString();
        _keepHourlyDays = settings.KeepHourlyDays.ToString();
        _loading        = false;

        StartBackfillCommand = ReactiveCommand.CreateFromTask(StartBackfillAsync);
        CancelBackfillCommand = ReactiveCommand.Create(() => _backfill.Cancel());
        RefreshNowCommand    = ReactiveCommand.CreateFromTask(async () =>
        {
            await _polling.PollOnceAsync();
            await RefreshCoverageAsync();
        });

        StartBackfillCommand .ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");
        CancelBackfillCommand.ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");
        RefreshNowCommand    .ThrownExceptions.Subscribe(ex => Status = $"Error: {ex.Message}");

        this.WhenAnyValue(x => x.Enabled).Skip(1).Subscribe(v => { if (!_loading) settings.Enabled = v; });
        this.WhenAnyValue(x => x.BackfillDays).Skip(1).Subscribe(v =>
        {
            if (!_loading && int.TryParse(v, out var n) && n is > 0 and <= 3650) settings.BackfillDays = n;
        });
        this.WhenAnyValue(x => x.KeepHourlyDays).Skip(1).Subscribe(v =>
        {
            if (!_loading && int.TryParse(v, out var n) && n is > 0 and <= 365) settings.KeepHourlyDays = n;
        });

        // Progress is polled rather than pushed: the backfill is a plain loop with no change
        // notifications, and a two-second refresh is plenty for a job measured in minutes.
        Observable.Interval(TimeSpan.FromSeconds(2))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => Tick());

        _ = RefreshCoverageAsync();
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set => this.RaiseAndSetIfChanged(ref _enabled, value);
    }

    private string _backfillDays;
    public string BackfillDays
    {
        get => _backfillDays;
        set => this.RaiseAndSetIfChanged(ref _backfillDays, value);
    }

    private string _keepHourlyDays;
    public string KeepHourlyDays
    {
        get => _keepHourlyDays;
        set => this.RaiseAndSetIfChanged(ref _keepHourlyDays, value);
    }

    // ── Live state ───────────────────────────────────────────────────────────

    private string _status = "";
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    private string _pollStatus = "";
    public string PollStatus
    {
        get => _pollStatus;
        private set => this.RaiseAndSetIfChanged(ref _pollStatus, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set => this.RaiseAndSetIfChanged(ref _isRunning, value);
    }

    private double _progress;
    public double Progress
    {
        get => _progress;
        private set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    private string _coverage = "";
    public string Coverage
    {
        get => _coverage;
        private set => this.RaiseAndSetIfChanged(ref _coverage, value);
    }

    public ReactiveCommand<Unit, Unit> StartBackfillCommand  { get; }
    public ReactiveCommand<Unit, Unit> CancelBackfillCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshNowCommand     { get; }

    private int _sinceCoverage;

    private void Tick()
    {
        IsRunning  = _backfill.IsRunning;
        PollStatus = _polling.StatusText;

        if (_backfill.IsRunning)
        {
            var total = Math.Max(_backfill.ProgressTotal, 1);
            Progress     = 100.0 * _backfill.ProgressCurrent / total;
            ProgressText = $"{_backfill.ProgressCurrent:N0} / {total:N0} — {_backfill.StatusText}";
            Status       = "Backfilling from the EVE Ref archive…";
        }
        else
        {
            Progress     = _settings.InitialBackfillDone ? 100 : 0;
            ProgressText = _backfill.StatusText;
            Status = _settings.InitialBackfillDone
                ? "History complete — keeping the current hour up to date"
                : "Waiting to start";
        }

        // Coverage means counting stored buckets, so it refreshes on a slower beat than the
        // rest of this panel.
        if (++_sinceCoverage < 8) return;
        _sinceCoverage = 0;
        _ = RefreshCoverageAsync();
    }

    private async Task StartBackfillAsync()
    {
        if (!int.TryParse(BackfillDays, out var days) || days <= 0) days = 30;
        await _backfill.BackfillAsync(days);
        await RefreshCoverageAsync();
    }

    /// <summary>How much history is actually held, per dataset.</summary>
    private async Task RefreshCoverageAsync()
    {
        try
        {
            var lines = await _stats.GetCoverageAsync();
            Coverage = lines.Count == 0
                ? "Nothing stored yet."
                : string.Join("\n", lines.Select(c =>
                    $"{c.Dataset,-24} {c.Buckets,6:N0} buckets   {c.Days,4:N0} days   {c.Earliest} → {c.Latest}"));
        }
        catch (Exception ex) { Coverage = $"Error: {ex.Message}"; }
    }
}
