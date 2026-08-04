using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Monitoring;
using EveConsole.Services;
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
        set
        {
            this.RaiseAndSetIfChanged(ref _isSelected, value);
            // A channel that is not stored has nothing to parse, so unticking it cannot leave
            // it marked as intel.
            if (!value && _isIntel) IsIntel = false;
            _onChanged();
        }
    }

    private bool _isIntel;
    /// <summary>Parse this channel's messages for sightings. Independent of storing it, except
    /// that it cannot be set without storing.</summary>
    public bool IsIntel
    {
        get => _isIntel;
        set
        {
            this.RaiseAndSetIfChanged(ref _isIntel, value);
            if (value && !_isSelected) IsSelected = true;
            _onChanged();
        }
    }

    public ChatChannelViewModel(string name, bool selected, bool intel, Action onChanged)
    {
        Name        = name;
        _isSelected = selected;
        _isIntel    = intel;
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
    private readonly IntelService?        _intel;
    private bool _loading = true;

    public ChatLogSettingsViewModel(
        MonitoringSettings settings, ChatLogImportService importer, IntelService? intel = null)
    {
        _settings = settings;
        _importer = importer;
        _intel    = intel;

        _enabled     = settings.ChatEnabled;
        _historyDays = settings.ChatHistoryDays;
        _newDirectory = "";

        foreach (var d in settings.ChatDirectories) Directories.Add(d);

        LoadChannels();

        DiscoverCommand        = ReactiveCommand.CreateFromTask(DiscoverAsync);
        ImportHistoryCommand   = ReactiveCommand.CreateFromTask(() => _importer.ImportHistoryAsync(HistoryDays));
        CancelImportCommand    = ReactiveCommand.Create(() => _importer.CancelImport());
        SelectNoneCommand      = ReactiveCommand.Create(SelectNone);
        ParseIntelHistoryCommand = ReactiveCommand.CreateFromTask(ParseIntelHistoryAsync);
        ParseIntelHistoryCommand.ThrownExceptions.Subscribe(ex => IntelStatus = $"Error: {ex.Message}");
        AddDirectoryCommand    = ReactiveCommand.CreateFromTask(AddDirectoryAsync);
        RemoveDirectoryCommand = ReactiveCommand.CreateFromTask(RemoveDirectoryAsync);
        DetectDirectoryCommand = ReactiveCommand.CreateFromTask(DetectDirectoryAsync);
        OpenDirectoryCommand   = ReactiveCommand.Create(OpenSelectedDirectory);

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
    public ReactiveCommand<Unit, Unit> ParseIntelHistoryCommand { get; }

    /// <summary>Directories to import from. May be UNC paths, same as game logs — that
    /// is how EVE clients on other machines are covered without installing anything
    /// there.</summary>
    public ObservableCollection<string> Directories { get; } = [];

    private string _newDirectory;
    public string NewDirectory
    {
        get => _newDirectory;
        set => this.RaiseAndSetIfChanged(ref _newDirectory, value);
    }

    private string? _selectedDirectory;
    public string? SelectedDirectory
    {
        get => _selectedDirectory;
        set => this.RaiseAndSetIfChanged(ref _selectedDirectory, value);
    }

    public ReactiveCommand<Unit, Unit> AddDirectoryCommand    { get; }
    public ReactiveCommand<Unit, Unit> RemoveDirectoryCommand { get; }
    public ReactiveCommand<Unit, Unit> DetectDirectoryCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDirectoryCommand   { get; }

    private string _resolvedPaths = "";
    /// <summary>What is actually being read. Shown because Documents redirection
    /// (OneDrive, corporate policy) is the most common reason nothing is found.</summary>
    public string ResolvedPaths
    {
        get => _resolvedPaths;
        private set => this.RaiseAndSetIfChanged(ref _resolvedPaths, value);
    }

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
        var intel    = _settings.ChatIntelChannels.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Show anything previously selected even if discovery hasn't run in this
        // session, so a saved allowlist is never invisible.
        var names = _settings.ChatDiscoveredChannels
                             .Concat(selected)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        Channels.Clear();
        foreach (var name in names)
            Channels.Add(new ChatChannelViewModel(
                name, selected.Contains(name), intel.Contains(name), SaveSelection));

        UpdateSelectionText();
    }

    private void SaveSelection()
    {
        if (_loading) return;
        _settings.ChatChannels      = Channels.Where(c => c.IsSelected).Select(c => c.Name).ToList();
        _settings.ChatIntelChannels = Channels.Where(c => c.IsIntel).Select(c => c.Name).ToList();
        UpdateSelectionText();
        UpdateEstimate();
    }

    /// <summary>
    /// Parses the intel channels' stored history. Without this the overlays stay empty until
    /// fresh intel is posted, when tens of thousands of usable messages may already be on disk.
    /// </summary>
    private async Task ParseIntelHistoryAsync()
    {
        if (_intel is null) return;

        IsBusy = true;
        try
        {
            var progress = new Progress<string>(s => IntelStatus = s);
            var n = await _intel.BackfillAsync(progress);
            IntelStatus = $"Parsed {n:N0} sighting(s) from stored history.";
        }
        catch (Exception ex)
        {
            IntelStatus = $"Intel parsing failed — {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string _intelStatus = "";
    public string IntelStatus
    {
        get => _intelStatus;
        private set => this.RaiseAndSetIfChanged(ref _intelStatus, value);
    }

    public string IntelHelp =>
        "Messages in the ticked channels are parsed into sightings: the system, how many " +
        "were reported, and any pilots named. \"clr\" retires whatever was standing in that " +
        "system. Sightings drive the Intel overlays on the Universe map.";

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

    private async Task AddDirectoryAsync()
    {
        var dir = NewDirectory?.Trim();
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (Directories.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;

        Directories.Add(dir);
        NewDirectory = "";
        await SaveDirectoriesAsync();
    }

    private async Task RemoveDirectoryAsync()
    {
        if (SelectedDirectory is null) return;
        Directories.Remove(SelectedDirectory);
        await SaveDirectoriesAsync();
    }

    private async Task DetectDirectoryAsync()
    {
        var auto = MonitoringSettings.DefaultChatLogDirectory();
        if (auto is null) { ResolvedPaths = "Could not find a local EVE chat log folder."; return; }

        if (!Directories.Contains(auto, StringComparer.OrdinalIgnoreCase))
        {
            Directories.Add(auto);
            await SaveDirectoriesAsync();
        }
    }

    private void OpenSelectedDirectory()
    {
        var dir = SelectedDirectory ?? _settings.ResolveChatDirectories().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dir)) return;

        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { /* nothing useful to do if the shell refuses */ }
    }

    private Task SaveDirectoriesAsync()
    {
        _settings.ChatDirectories = Directories.ToList();
        Refresh();
        UpdateEstimate();
        return Task.CompletedTask;
    }

    private void Refresh()
    {
        Status          = _importer.StatusText;
        IsBusy          = _importer.IsBusy;
        ProgressCurrent = _importer.ProgressCurrent;
        ProgressTotal   = Math.Max(1, _importer.ProgressTotal);
        ProgressText    = _importer.ProgressText;

        var resolved = _settings.ResolveChatDirectories();
        ResolvedPaths = resolved.Count == 0
            ? "No chat log folder found — add one below."
            : string.Join("\n", resolved.Select(d =>
                (Directory.Exists(d) ? "✓ " : "✗ unreachable — ") + d));
    }
}
