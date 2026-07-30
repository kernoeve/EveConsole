using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using EveConsole.Monitoring;
using ReactiveUI;

namespace EveConsole.ViewModels;

/// <summary>
/// Settings for the game log importer.
///
/// Setters write through immediately; the importer picks the change up on its next
/// scan, so nothing needs a restart.
/// </summary>
public class GameLogSettingsViewModel : ReactiveObject
{
    private readonly MonitoringSettings   _settings;
    private readonly GameLogImportService _importer;
    private bool _loading = true;

    public GameLogSettingsViewModel(MonitoringSettings settings, GameLogImportService importer)
    {
        _settings = settings;
        _importer = importer;

        _enabled           = settings.GameLogEnabled;
        _historyDays       = settings.HistoryImportDays;
        _storeUnmatched    = settings.StoreUnmatched;
        _scanSeconds       = settings.ScanSeconds;
        _newDirectory      = "";

        foreach (var d in settings.GameLogDirectories) Directories.Add(d);

        AddDirectoryCommand    = ReactiveCommand.CreateFromTask(AddDirectoryAsync);
        RemoveDirectoryCommand = ReactiveCommand.CreateFromTask(RemoveDirectoryAsync);
        DetectDirectoryCommand = ReactiveCommand.CreateFromTask(DetectDirectoryAsync);
        OpenDirectoryCommand   = ReactiveCommand.Create(OpenSelectedDirectory);
        ImportHistoryCommand   = ReactiveCommand.CreateFromTask(() => _importer.ImportHistoryAsync(HistoryDays));
        CancelImportCommand    = ReactiveCommand.Create(() => _importer.CancelImport());
        EstimateCommand        = ReactiveCommand.Create(UpdateEstimate);

        Observable.Interval(TimeSpan.FromSeconds(2))
                  .ObserveOn(RxApp.MainThreadScheduler)
                  .Subscribe(_ => Refresh());

        Refresh();
        UpdateEstimate();
        _loading = false;
    }

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { this.RaiseAndSetIfChanged(ref _enabled, value); Apply(s => s.GameLogEnabled = value); }
    }

    // ── History import ───────────────────────────────────────────────────────
    // Deliberately an explicit action rather than something that happens on enable:
    // the user picks how far back to go BEFORE anything is processed, and sees how
    // many files that means.

    private int _historyDays;
    public int HistoryDays
    {
        get => _historyDays;
        set
        {
            this.RaiseAndSetIfChanged(ref _historyDays, value);
            Apply(s => s.HistoryImportDays = value);
            UpdateEstimate();
        }
    }

    private string _estimateText = "";
    public string EstimateText
    {
        get => _estimateText;
        private set => this.RaiseAndSetIfChanged(ref _estimateText, value);
    }

    /// <summary>True until a history import has been run, so the panel can present
    /// itself as a first-run choice.</summary>
    private bool _historyPending = true;
    public bool HistoryPending
    {
        get => _historyPending;
        private set => this.RaiseAndSetIfChanged(ref _historyPending, value);
    }

    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        private set => this.RaiseAndSetIfChanged(ref _isImporting, value);
    }

    private int _progressCurrent;
    public int ProgressCurrent
    {
        get => _progressCurrent;
        private set => this.RaiseAndSetIfChanged(ref _progressCurrent, value);
    }

    private int _progressTotal = 1;
    public int ProgressTotal
    {
        get => _progressTotal;
        private set => this.RaiseAndSetIfChanged(ref _progressTotal, value);
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        private set => this.RaiseAndSetIfChanged(ref _progressText, value);
    }

    private void UpdateEstimate()
    {
        try
        {
            var files = _importer.EstimateHistoryFiles(HistoryDays);
            EstimateText = HistoryDays <= 0
                ? $"All {files:N0} log file(s) will be processed."
                : $"{files:N0} log file(s) modified in the last {HistoryDays:N0} day(s) will be processed.";
        }
        catch (Exception ex)
        {
            EstimateText = $"Could not count files — {ex.Message}";
        }
    }

    private bool _storeUnmatched;
    public bool StoreUnmatched
    {
        get => _storeUnmatched;
        set { this.RaiseAndSetIfChanged(ref _storeUnmatched, value); Apply(s => s.StoreUnmatched = value); }
    }

    private int _scanSeconds;
    public int ScanSeconds
    {
        get => _scanSeconds;
        set { this.RaiseAndSetIfChanged(ref _scanSeconds, value); Apply(s => s.ScanSeconds = value); }
    }

    /// <summary>Directories to import from. May be UNC paths, which is how EVE clients
    /// on other machines are covered without installing anything there.</summary>
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
    public ReactiveCommand<Unit, Unit> ImportHistoryCommand   { get; }
    public ReactiveCommand<Unit, Unit> CancelImportCommand    { get; }
    public ReactiveCommand<Unit, Unit> EstimateCommand        { get; }

    private string _status = "";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _resolvedPaths = "";
    /// <summary>What is actually being read. Shown because Documents redirection
    /// (OneDrive, corporate policy) is the most common reason nothing is found.</summary>
    public string ResolvedPaths
    {
        get => _resolvedPaths;
        private set => this.RaiseAndSetIfChanged(ref _resolvedPaths, value);
    }

    private void Refresh()
    {
        Status          = _importer.StatusText;
        IsImporting     = _importer.IsImporting;
        ProgressCurrent = _importer.ProgressCurrent;
        ProgressTotal   = Math.Max(1, _importer.ProgressTotal);
        ProgressText    = _importer.ProgressText;
        HistoryPending  = !_settings.HistoryImported;

        var resolved = _settings.ResolveDirectories();
        ResolvedPaths = resolved.Count == 0
            ? "No game log folder found — add one below."
            : string.Join("\n", resolved.Select(d =>
                (Directory.Exists(d) ? "✓ " : "✗ unreachable — ") + d));
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
        var auto = MonitoringSettings.DefaultGameLogDirectory();
        if (auto is null) { ResolvedPaths = "Could not find a local EVE game log folder."; return; }

        if (!Directories.Contains(auto, StringComparer.OrdinalIgnoreCase))
        {
            Directories.Add(auto);
            await SaveDirectoriesAsync();
        }
    }

    private void OpenSelectedDirectory()
    {
        var dir = SelectedDirectory ?? _settings.ResolveDirectories().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(dir)) return;

        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { /* nothing useful to do if the shell refuses */ }
    }

    private Task SaveDirectoriesAsync()
    {
        _settings.GameLogDirectories = Directories.ToList();
        Refresh();
        UpdateEstimate();
        return Task.CompletedTask;
    }

    private void Apply(Action<MonitoringSettings> mutate)
    {
        if (_loading) return;
        mutate(_settings);
    }
}
