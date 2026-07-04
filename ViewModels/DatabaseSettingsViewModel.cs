using System.Collections.ObjectModel;
using System.Diagnostics;
using ReactiveUI;
using EveCortex.Services;

namespace EveCortex.ViewModels;

public class DatabaseSettingsViewModel : ReactiveObject
{
    private readonly AppPreferencesService  _prefs;
    private readonly DatabaseBackupService  _backupSvc;

    // ── Database type ─────────────────────────────────────────────────────────

    public ObservableCollection<string> DbTypes { get; } = ["SQLite"];

    private string _selectedDbType = "SQLite";
    public string SelectedDbType
    {
        get => _selectedDbType;
        set => this.RaiseAndSetIfChanged(ref _selectedDbType, value);
    }

    public bool IsSqlite => SelectedDbType == "SQLite";

    // ── SQLite info ───────────────────────────────────────────────────────────

    public string DbPath { get; private set; } = AppConfig.GetDbPath();

    private string _dbFileSizeText = "";
    public string DbFileSizeText
    {
        get => _dbFileSizeText;
        private set => this.RaiseAndSetIfChanged(ref _dbFileSizeText, value);
    }

    // ── Backup settings ───────────────────────────────────────────────────────

    private bool _backupEnabled;
    public bool BackupEnabled
    {
        get => _backupEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _backupEnabled, value);
            _ = _prefs.SetAsync(DatabaseBackupService.KeyEnabled, value ? "true" : "false");
        }
    }

    public ObservableCollection<string> BackupIntervals { get; } = ["Hourly", "Daily", "Weekly", "Monthly"];

    private string _selectedInterval = "Daily";
    public string SelectedInterval
    {
        get => _selectedInterval;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedInterval, value);
            _ = _prefs.SetAsync(DatabaseBackupService.KeyInterval, value.ToLowerInvariant());
        }
    }

    private int _backupsToKeep = 7;
    public int BackupsToKeep
    {
        get => _backupsToKeep;
        set
        {
            this.RaiseAndSetIfChanged(ref _backupsToKeep, value);
            _ = _prefs.SetLongAsync(DatabaseBackupService.KeyKeepCount, value);
        }
    }

    // ── Last backup info ──────────────────────────────────────────────────────

    private string _lastBackupText = "Never";
    public string LastBackupText
    {
        get => _lastBackupText;
        private set => this.RaiseAndSetIfChanged(ref _lastBackupText, value);
    }

    // ── Status / busy ─────────────────────────────────────────────────────────

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    // ── Dialog delegates (wired in code-behind) ───────────────────────────────

    public Func<string, string, Task<string?>>?    ShowSaveFileDialog  { get; set; }
    public Func<string, Task<string?>>?            ShowOpenFileDialog  { get; set; }
    public Func<string, string, Task<bool>>?       ShowConfirmDialog   { get; set; }
    public Action?                                 RequestRestart      { get; set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public DatabaseSettingsViewModel(AppPreferencesService prefs, DatabaseBackupService backupSvc)
    {
        _prefs     = prefs;
        _backupSvc = backupSvc;

        // Load persisted settings
        _backupEnabled    = backupSvc.BackupEnabled;
        _selectedInterval = CapitalizeFirst(backupSvc.Interval);
        _backupsToKeep    = backupSvc.KeepCount;

        RefreshDbInfo();
        RefreshLastBackupText();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public async Task BackupNowAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "Backing up…";
        try
        {
            var result = await _backupSvc.BackupNowAsync(DbPath);
            RefreshLastBackupText();
            StatusText = result is not null
                ? $"Backup saved: {Path.GetFileName(result)}"
                : "Backup failed — DB file not found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Backup error: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    public async Task MoveDatabaseAsync()
    {
        if (ShowSaveFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowSaveFileDialog("Move Database To…", Path.GetFileName(DbPath));
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var confirmed = await ShowConfirmDialog(
            "Move Database",
            $"Copy the database to:\n{newPath}\n\nThe application will restart to use the new location.");
        if (!confirmed) return;

        IsBusy = true;
        StatusText = "Moving database…";
        try
        {
            File.Copy(DbPath, newPath, overwrite: false);
            AppConfig.SetDbPath(newPath);
            StatusText = "Done. Restarting…";
            await Task.Delay(800);
            RequestRestart?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Move failed: {ex.Message}";
            IsBusy = false;
        }
    }

    public async Task RenameDatabaseAsync()
    {
        if (ShowSaveFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowSaveFileDialog("Rename Database…", Path.GetFileName(DbPath));
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var confirmed = await ShowConfirmDialog(
            "Rename Database",
            $"Rename/move the database file to:\n{newPath}\n\nThe application will restart.");
        if (!confirmed) return;

        IsBusy = true;
        StatusText = "Renaming database…";
        try
        {
            File.Move(DbPath, newPath, overwrite: false);
            AppConfig.SetDbPath(newPath);
            StatusText = "Done. Restarting…";
            await Task.Delay(800);
            RequestRestart?.Invoke();
        }
        catch (Exception ex)
        {
            StatusText = $"Rename failed: {ex.Message}";
            IsBusy = false;
        }
    }

    public async Task PointToExistingDatabaseAsync()
    {
        if (ShowOpenFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowOpenFileDialog("Select Existing Database…");
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var confirmed = await ShowConfirmDialog(
            "Switch Database",
            $"Point Eve Cortex to the existing database at:\n{newPath}\n\nThe application will restart.");
        if (!confirmed) return;

        AppConfig.SetDbPath(newPath);
        StatusText = "Done. Restarting…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public void RefreshDbInfo()
    {
        DbPath = AppConfig.GetDbPath();
        this.RaisePropertyChanged(nameof(DbPath));
        try
        {
            if (File.Exists(DbPath))
            {
                var bytes = new FileInfo(DbPath).Length;
                DbFileSizeText = FormatBytes(bytes);
            }
            else
            {
                DbFileSizeText = "File not found";
            }
        }
        catch
        {
            DbFileSizeText = "Unknown";
        }
    }

    private void RefreshLastBackupText()
    {
        var last = _backupSvc.LastBackupUtc;
        if (last is null)
        {
            LastBackupText = "Never";
            return;
        }
        var local    = last.Value.ToLocalTime();
        var sizeText = _backupSvc.LastSizeBytes > 0 ? $" ({FormatBytes(_backupSvc.LastSizeBytes)})" : "";
        LastBackupText = $"{local:yyyy-MM-dd HH:mm:ss}{sizeText}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F1} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    private static string CapitalizeFirst(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public void Dispose() { }
}
