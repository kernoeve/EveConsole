using System.Collections.ObjectModel;
using System.Reactive;
using EveConsole.Services;
using ReactiveUI;

namespace EveConsole.ViewModels;

public class TimerRowVm : ReactiveObject
{
    private readonly TimerSettingsService  _svc;
    private readonly EsiPollingService     _polling;
    private readonly TimerForceService?    _force;
    private int _intervalMinutes;

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
    public int    MinMinutes  { get; }

    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set => this.RaiseAndSetIfChanged(ref _intervalMinutes, Math.Max(MinMinutes, value));
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
        MinMinutes       = (int)Math.Ceiling(info.MinSeconds / 60.0);
        _intervalMinutes = (int)Math.Round(svc.GetInterval(info.Key, info.DefaultSeconds) / 60.0);
        if (_intervalMinutes < MinMinutes) _intervalMinutes = MinMinutes;

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
            ForceStatus = "started";
            return;
        }

        _polling.ResetCallTime(Key);
        ForceStatus = "due on the next cycle";
    }

    public async Task SaveAsync() =>
        await _svc.SetIntervalAsync(Key, IntervalMinutes * 60);
}

public class TimerSettingsViewModel : ReactiveObject
{
    public ObservableCollection<TimerRowVm> CharRows  { get; } = [];
    public ObservableCollection<TimerRowVm> CorpRows  { get; } = [];
    public ObservableCollection<TimerRowVm> OtherRows { get; } = [];

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    private string _saveStatus = "";
    public string SaveStatus
    {
        get => _saveStatus;
        private set => this.RaiseAndSetIfChanged(ref _saveStatus, value);
    }

    public TimerSettingsViewModel(EsiPollingService pollingService, TimerSettingsService timerSettings,
                                  TimerForceService? force = null)
    {
        foreach (var ep in pollingService.CharacterEndpointInfos)
            CharRows.Add(new TimerRowVm(ep, timerSettings, pollingService, force));

        foreach (var ep in pollingService.CorpEndpointInfos)
            CorpRows.Add(new TimerRowVm(ep, timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("market.refresh", "Market Price Refresh", 600, 3600),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("market.history", "Price History Check", 120, 600),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("contract.public", "Public Contracts (all regions)", 900, 3600),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("contract.items", "Contract Items Pull", 120, 600),
            timerSettings, pollingService, force));

        // One public call per NPC corporation. Catalogues only change on patch boundaries,
        // so a day between sweeps is already generous.
        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("lpstore.offers", "LP Store Offers (all NPC corps)", 3600, 86400),
            timerSettings, pollingService, force));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("contract.pricing", "Contract Pricing Rebuild", 300, 1800),
            timerSettings, pollingService, force));

        SaveCommand = ReactiveCommand.CreateFromTask(SaveAllAsync);
    }

    private async Task SaveAllAsync()
    {
        foreach (var row in CharRows)
            await row.SaveAsync();
        foreach (var row in CorpRows)
            await row.SaveAsync();
        foreach (var row in OtherRows)
            await row.SaveAsync();

        SaveStatus = "Saved.";
        await Task.Delay(2000);
        SaveStatus = "";
    }
}
