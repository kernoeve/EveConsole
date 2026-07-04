using System.Collections.ObjectModel;
using System.Reactive;
using EveCortex.Services;
using ReactiveUI;

namespace EveCortex.ViewModels;

public class TimerRowVm : ReactiveObject
{
    private readonly TimerSettingsService  _svc;
    private readonly EsiPollingService     _polling;
    private int _intervalMinutes;

    public string Key         { get; }
    public string DisplayName { get; }
    public int    MinMinutes  { get; }

    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set => this.RaiseAndSetIfChanged(ref _intervalMinutes, Math.Max(MinMinutes, value));
    }

    public ReactiveCommand<Unit, Unit> ForceNowCommand { get; }

    public TimerRowVm(EndpointInfo info, TimerSettingsService svc, EsiPollingService polling)
    {
        _svc             = svc;
        _polling         = polling;
        Key              = info.Key;
        DisplayName      = info.DisplayName;
        MinMinutes       = (int)Math.Ceiling(info.MinSeconds / 60.0);
        _intervalMinutes = (int)Math.Round(svc.GetInterval(info.Key, info.DefaultSeconds) / 60.0);
        if (_intervalMinutes < MinMinutes) _intervalMinutes = MinMinutes;
        ForceNowCommand  = ReactiveCommand.Create(() => _polling.ResetCallTime(Key));
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

    public TimerSettingsViewModel(EsiPollingService pollingService, TimerSettingsService timerSettings)
    {
        foreach (var ep in pollingService.CharacterEndpointInfos)
            CharRows.Add(new TimerRowVm(ep, timerSettings, pollingService));

        foreach (var ep in pollingService.CorpEndpointInfos)
            CorpRows.Add(new TimerRowVm(ep, timerSettings, pollingService));

        OtherRows.Add(new TimerRowVm(
            new EndpointInfo("market.refresh", "Market Price Refresh", 600, 3600),
            timerSettings, pollingService));

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
