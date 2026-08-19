using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Settings for the optional zKillboard kill supplement/backfill. Setters write through
/// immediately; the polling/firehose loops pick changes up on their next tick, so
/// nothing here needs a restart.
/// </summary>
public class ZkillboardSettingsViewModel : ReactiveObject
{
    private readonly ZkillboardSettings        _settings;
    private readonly ZkillboardPollingService  _poller;
    private readonly ZkillboardFirehoseService _firehose;
    private readonly ZkillboardBackfillService _backfill;
    private readonly ZkillboardPostService     _post;
    private bool _loading = true;

    public ZkillboardSettingsViewModel(
        ZkillboardSettings        settings,
        ZkillboardPollingService  poller,
        ZkillboardFirehoseService firehose,
        ZkillboardBackfillService backfill,
        ZkillboardPostService     post)
    {
        _settings = settings;
        _poller   = poller;
        _firehose = firehose;
        _backfill = backfill;
        _post     = post;

        _enabled             = settings.Enabled;
        _postEnabled         = settings.PostEnabled;
        _pollIntervalSeconds = settings.PollIntervalSeconds;
        _backfillDays        = settings.BackfillDays;

        BackfillCommand       = ReactiveCommand.CreateFromTask(() => _backfill.BackfillAsync(BackfillDays));
        CancelBackfillCommand = ReactiveCommand.Create(() => _backfill.CancelImport());

        Observable.Interval(TimeSpan.FromSeconds(2))
                  .ObserveOnUi("ZkbSettings.Poll")
                  .Subscribe(_ => Refresh());

        Refresh();
        _loading = false;
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { this.RaiseAndSetIfChanged(ref _enabled, value); Apply(() => _settings.Enabled = value); }
    }

    /// <summary>Submit kills zKillboard doesn't have back to zKillboard. Bounded by the
    /// coverage window shown alongside it — see ZkillboardPostService.</summary>
    private bool _postEnabled;
    public bool PostEnabled
    {
        get => _postEnabled;
        set { this.RaiseAndSetIfChanged(ref _postEnabled, value); Apply(() => _settings.PostEnabled = value); }
    }

    /// <summary>Two bool properties over one underlying enum, so each radio button can
    /// two-way bind directly — both read/write through the same ZkillboardSettings.Scope
    /// so they always agree.</summary>
    public bool ScopeIsMineAndCorp
    {
        get => _settings.Scope == ZkbScope.MineAndCorp;
        set { if (value) SetScope(ZkbScope.MineAndCorp); }
    }

    public bool ScopeIsAll
    {
        get => _settings.Scope == ZkbScope.All;
        set { if (value) SetScope(ZkbScope.All); }
    }

    private void SetScope(ZkbScope scope)
    {
        if (_loading) return;
        _settings.Scope = scope;
        this.RaisePropertyChanged(nameof(ScopeIsMineAndCorp));
        this.RaisePropertyChanged(nameof(ScopeIsAll));
        Refresh();
    }

    /// <summary>Only meaningful for Mine+Corp scope — the All-scope firehose paces
    /// itself and ignores this.</summary>
    private int _pollIntervalSeconds;
    public int PollIntervalSeconds
    {
        get => _pollIntervalSeconds;
        set { this.RaiseAndSetIfChanged(ref _pollIntervalSeconds, value); Apply(() => _settings.PollIntervalSeconds = value); }
    }

    private int _backfillDays;
    public int BackfillDays
    {
        get => _backfillDays;
        set { this.RaiseAndSetIfChanged(ref _backfillDays, value); Apply(() => _settings.BackfillDays = value); }
    }

    // ── Backfill progress/status — reflects whichever is currently running, the
    //    manual button below or the automatic hourly gap-fill check ──────────────

    private bool _isImporting;
    public bool IsImporting { get => _isImporting; private set => this.RaiseAndSetIfChanged(ref _isImporting, value); }

    private int _progressCurrent;
    public int ProgressCurrent { get => _progressCurrent; private set => this.RaiseAndSetIfChanged(ref _progressCurrent, value); }

    private int _progressTotal = 1;
    public int ProgressTotal { get => _progressTotal; private set => this.RaiseAndSetIfChanged(ref _progressTotal, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => this.RaiseAndSetIfChanged(ref _progressText, value); }

    private string _backfillStatus = "";
    public string BackfillStatus { get => _backfillStatus; private set => this.RaiseAndSetIfChanged(ref _backfillStatus, value); }

    private string _liveStatus = "";
    public string LiveStatus { get => _liveStatus; private set => this.RaiseAndSetIfChanged(ref _liveStatus, value); }

    private string _lastFullDayText = "";
    public string LastFullDayText { get => _lastFullDayText; private set => this.RaiseAndSetIfChanged(ref _lastFullDayText, value); }

    private string _postStatus = "";
    public string PostStatus { get => _postStatus; private set => this.RaiseAndSetIfChanged(ref _postStatus, value); }

    private string _coverageFromText = "";
    public string CoverageFromText { get => _coverageFromText; private set => this.RaiseAndSetIfChanged(ref _coverageFromText, value); }

    public ReactiveCommand<Unit, Unit> BackfillCommand       { get; }
    public ReactiveCommand<Unit, Unit> CancelBackfillCommand { get; }

    private void Refresh()
    {
        IsImporting     = _backfill.IsImporting;
        ProgressCurrent = _backfill.ProgressCurrent;
        ProgressTotal   = Math.Max(1, _backfill.ProgressTotal);
        ProgressText    = _backfill.ProgressText;
        BackfillStatus  = _backfill.StatusText;
        LiveStatus      = _settings.Scope == ZkbScope.All ? _firehose.StatusText : _poller.StatusText;
        LastFullDayText = _settings.LastFullDay is { } d ? d.ToString("yyyy-MM-dd") : "never";
        PostStatus       = _post.StatusText;
        CoverageFromText = _post.CoverageText;
    }

    private void Apply(Action mutate)
    {
        if (_loading) return;
        mutate();
    }
}
