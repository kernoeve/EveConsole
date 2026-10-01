using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Services;
using ReactiveUI;
using EveConsole.Localization;

namespace EveConsole.ViewModels;

public class TimerRowVm : ReactiveObject
{
    private readonly TimerSettingsService  _svc;
    private readonly EsiPollingService     _polling;
    private readonly TimerForceService?    _force;
    private int _interval;

    private string _forceStatus = "";
    /// <summary>Shown beside the button. Resetting a timer looks exactly like doing nothing, which
    /// is how a button that genuinely did nothing went unnoticed.</summary>
    public string ForceStatus
    {
        get => _forceStatus;
        private set => this.RaiseAndSetIfChanged(ref _forceStatus, value);
    }

    public string Key         { get; }
    public string DisplayName { get; }

    /// <summary>
    /// Seconds for an endpoint polled on the scale of seconds, minutes otherwise. The location and
    /// ship polls run every ten seconds against a five-second cache; shown in minutes they read
    /// "1 min (min: 1 min)", and saving that quietly made them a minute.
    /// </summary>
    private readonly bool _inSeconds;

    /// <summary>The unit's symbol as the interface writes it: "s" or "min" in English.</summary>
    public string Unit    => _inSeconds ? SettingsText.TimerUnitSeconds : SettingsText.TimerUnitMinutes;
    public int    Min     { get; }
    public string MinText => string.Format(SettingsText.TimerMinimum, Min, Unit);

    private int UnitSeconds => _inSeconds ? 1 : 60;

    /// <summary>The interval in <see cref="Unit"/>s.</summary>
    public int Interval
    {
        get => _interval;
        set => this.RaiseAndSetIfChanged(ref _interval, Math.Max(Min, value));
    }

    public ReactiveCommand<Unit, Unit> ForceNowCommand { get; }

    public TimerRowVm(EndpointInfo info, TimerSettingsService svc, EsiPollingService polling,
                      TimerForceService? force = null)
    {
        _svc             = svc;
        _polling         = polling;
        _force           = force;
        Key              = info.Key;
        DisplayName      = info.DisplayName;
        _inSeconds       = info.MinSeconds < 60 || info.DefaultSeconds < 60;
        Min              = (int)Math.Ceiling(info.MinSeconds / (double)UnitSeconds);
        _interval        = (int)Math.Round(svc.GetInterval(info.Key, info.DefaultSeconds) / (double)UnitSeconds);
        if (_interval < Min) _interval = Min;

        ForceNowCommand  = ReactiveCommand.Create(ForceNow);
    }

    /// <summary>
    /// A service that runs on its own timer is told to run now; anything the polling loop drives
    /// has its schedule cleared so the next cycle picks it up.
    ///
    /// <para>⚠️ The reset alone was the whole implementation, and it does nothing for the rows
    /// under "Other" — those services never consult the polling loop's schedule.</para>
    /// </summary>
    private void ForceNow()
    {
        if (_force is not null && _force.TryForce(Key))
        {
            ForceStatus = SettingsText.TimerForceStarted;
            return;
        }

        _polling.ResetCallTime(Key);
        ForceStatus = SettingsText.TimerForceDue;
    }

    /// <summary>Writes the interval: read here, on the UI thread, and written off it.</summary>
    public Task SaveAsync()
    {
        var seconds = Interval * UnitSeconds;
        return Task.Run(() => _svc.SetIntervalAsync(Key, seconds));
    }
}

public class TimerSettingsViewModel : ReactiveObject
{
    public ObservableCollection<TimerRowVm> CharRows  { get; } = [];
    public ObservableCollection<TimerRowVm> CorpRows  { get; } = [];
    public ObservableCollection<TimerRowVm> OtherRows { get; } = [];

    private string _saveStatus = "";
    public string SaveStatus
    {
        get => _saveStatus;
        private set => this.RaiseAndSetIfChanged(ref _saveStatus, value);
    }

    /// <summary>The rows changed since the last save: only those are written.</summary>
    private readonly HashSet<TimerRowVm> _changed = [];
    private readonly AutoSave            _autoSave;

    public TimerSettingsViewModel(EsiPollingService pollingService, TimerSettingsService timerSettings,
                                  TimerForceService? force = null)
    {
        _autoSave = new AutoSave(SaveChangedAsync,
            ex => SaveStatus = string.Format(CommonText.ErrorWithMessage, ex.Message));

        foreach (var ep in pollingService.CharacterEndpointInfos)
            CharRows.Add(new TimerRowVm(ep, timerSettings, pollingService, force));

        foreach (var ep in pollingService.CorpEndpointInfos)
            CorpRows.Add(new TimerRowVm(ep, timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("market.refresh", SettingsText.TimerMarketRefresh, 600, 3600),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("market.history", SettingsText.TimerPriceHistoryCheck, 120, 600),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("contract.public", SettingsText.TimerPublicContracts, 900, 3600),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("contract.items", SettingsText.TimerContractItems, 120, 600),
            timerSettings, pollingService, force));

        // One public call per NPC corporation. Catalogues only change on patch boundaries,
        // so a day between sweeps is already generous.
        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("lpstore.offers", SettingsText.TimerLpStoreOffers, 3600, 86400),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("contract.pricing", SettingsText.TimerContractPricing, 300, 1800),
            timerSettings, pollingService, force));

        // Watched once every row holds its stored interval, so building the rows saves nothing.
        // An interval is typed, so it is saved once typing pauses; one that does not read as a
        // number never reaches Interval (the box shows the error) and so is never saved.
        foreach (var row in CharRows.Concat(CorpRows).Concat(OtherRows))
            row.WhenAnyValue(r => r.Interval).Skip(1).Subscribe(_ =>
            {
                _changed.Add(row);
                _autoSave.Typed();
            });
    }

    /// <summary>Saves a change still waiting — a box losing focus, or the Settings window closing.</summary>
    public Task FlushAsync() => _autoSave.FlushAsync();

    /// <summary>
    /// Writes the changed rows. Nothing restarts: the polling loop reads an interval from the
    /// service's cache as it schedules each call, and the cache takes the new one at once.
    /// </summary>
    private async Task SaveChangedAsync()
    {
        var rows = _changed.ToList();
        _changed.Clear();   // a change made while these are written is saved by the next save
        try
        {
            foreach (var row in rows)
                await row.SaveAsync();
        }
        catch
        {
            _changed.UnionWith(rows);   // tried again with the next change
            throw;
        }

        _autoSave.Flash(s => SaveStatus = s, SettingsText.Saved);
    }
}
