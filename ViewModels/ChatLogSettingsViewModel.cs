using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Monitoring;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>One discovered channel and whether the user has opted to store it.</summary>
public class ChatChannelViewModel : ReactiveObject
{
    private readonly Action _onChanged;

    public string Name { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { this.RaiseAndSetIfChanged(ref _isSelected, value); _onChanged(); }
    }

    public ChatChannelViewModel(string name, bool selected, Action onChanged)
    {
        Name        = name;
        _isSelected = selected;
        _onChanged  = onChanged;
    }
}

/// <summary>
/// Settings for chat log import.
///
/// Chat contains other people's words, so this is off by default and additionally
/// gated on a per-channel allowlist that starts empty — enabling alone stores nothing.
/// </summary>
public class ChatLogSettingsViewModel : ReactiveObject
{
    private readonly MonitoringSettings   _settings;
    private readonly ChatLogImportService _importer;
    private bool _loading = true;

    public ChatLogSettingsViewModel(MonitoringSettings settings, ChatLogImportService importer)
    {
        _settings = settings;
        _importer = importer;

        _enabled     = settings.ChatEnabled;
        _historyDays = settings.ChatHistoryDays;

        LoadChannels();

        DiscoverCommand      = ReactiveCommand.CreateFromTask(DiscoverAsync);
        ImportHistoryCommand = ReactiveCommand.CreateFromTask(() => _importer.ImportHistoryAsync(HistoryDays));
        CancelImportCommand  = ReactiveCommand.Create(() => _importer.CancelImport());
        SelectNoneCommand    = ReactiveCommand.Create(SelectNone);

        Observable.Interval(TimeSpan.FromSeconds(2))
                  .ObserveOn(RxApp.MainThreadScheduler)
                  .Subscribe(_ => Refresh());

        Refresh();
        _loading = false;
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { this.RaiseAndSetIfChanged(ref _enabled, value); if (!_loading) _settings.ChatEnabled = value; }
    }

    private int _historyDays;
    public int HistoryDays
    {
        get => _historyDays;
        set
        {
            this.RaiseAndSetIfChanged(ref _historyDays, value);
            if (!_loading) { _settings.ChatHistoryDays = value; UpdateEstimate(); }
        }
    }

    public ObservableCollection<ChatChannelViewModel> Channels { get; } = [];

    public ReactiveCommand<Unit, Unit> DiscoverCommand      { get; }
    public ReactiveCommand<Unit, Unit> ImportHistoryCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelImportCommand  { get; }
    public ReactiveCommand<Unit, Unit> SelectNoneCommand    { get; }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _estimateText = "";
    public string EstimateText { get => _estimateText; private set => this.RaiseAndSetIfChanged(ref _estimateText, value); }

    private string _selectionText = "";
    public string SelectionText { get => _selectionText; private set => this.RaiseAndSetIfChanged(ref _selectionText, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private int _progressCurrent;
    public int ProgressCurrent { get => _progressCurrent; private set => this.RaiseAndSetIfChanged(ref _progressCurrent, value); }

    private int _progressTotal = 1;
    public int ProgressTotal { get => _progressTotal; private set => this.RaiseAndSetIfChanged(ref _progressTotal, value); }

    private string _progressText = "";
    public string ProgressText { get => _progressText; private set => this.RaiseAndSetIfChanged(ref _progressText, value); }

    private void LoadChannels()
    {
        var selected = _settings.ChatChannels.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Show anything previously selected even if discovery hasn't run in this
        // session, so a saved allowlist is never invisible.
        var names = _settings.ChatDiscoveredChannels
                             .Concat(selected)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        Channels.Clear();
        foreach (var name in names)
            Channels.Add(new ChatChannelViewModel(name, selected.Contains(name), SaveSelection));

        UpdateSelectionText();
    }

    private void SaveSelection()
    {
        if (_loading) return;
        _settings.ChatChannels = Channels.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        UpdateSelectionText();
        UpdateEstimate();
    }

    private void SelectNone()
    {
        foreach (var c in Channels) c.IsSelected = false;
        SaveSelection();
    }

    private void UpdateSelectionText()
    {
        var n = Channels.Count(c => c.IsSelected);
        SelectionText = n == 0
            ? "No channels selected — nothing will be stored."
            : $"{n:N0} of {Channels.Count:N0} channel(s) selected.";
    }

    private void UpdateEstimate()
    {
        try
        {
            if (Channels.All(c => !c.IsSelected)) { EstimateText = ""; return; }

            var files = _importer.EstimateHistoryFiles(HistoryDays);
            EstimateText = HistoryDays <= 0
                ? $"All {files:N0} file(s) for the selected channels will be processed."
                : $"{files:N0} file(s) from the last {HistoryDays:N0} day(s) will be processed.";
        }
        catch (Exception ex)
        {
            EstimateText = $"Could not count files — {ex.Message}";
        }
    }

    private async Task DiscoverAsync()
    {
        await _importer.DiscoverChannelsAsync();
        LoadChannels();
        UpdateEstimate();
    }

    private void Refresh()
    {
        Status          = _importer.StatusText;
        IsBusy          = _importer.IsBusy;
        ProgressCurrent = _importer.ProgressCurrent;
        ProgressTotal   = Math.Max(1, _importer.ProgressTotal);
        ProgressText    = _importer.ProgressText;
    }
}
