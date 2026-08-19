using System.Collections.ObjectModel;
using System.Diagnostics;
using ReactiveUI;
using EveConsole.Services;

namespace EveConsole.ViewModels;

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

        // Startup-time database work reports itself here rather than in the error log: it succeeded
        // or it did not, and either way the person who asked for it is the one who should see the
        // outcome. Relocation first, since a shrink after one is the less surprising of the two.
        if (DatabaseRelocationService.LastResult is { Ran: true } moved)
            StatusText = moved.Message;
        if (DatabaseShrinkService.LastResult is { Ran: true } shrink)
            StatusText = shrink.Message;

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

    /// <summary>
    /// Moves the database, which covers renaming it: a rename is a move to the same folder under a
    /// different name, so one operation and one button serve both. The file picker decides which
    /// it is by where the user points it.
    ///
    /// <para>⚠️ Records the request and restarts; the move happens at startup before anything opens
    /// the database. This used to call File.Copy (Move) and File.Move (Rename) here, against a
    /// database the app had open and was writing to, which silently lost everything still in the
    /// WAL. See DatabaseRelocationService.</para>
    /// </summary>
    public async Task RelocateDatabaseAsync()
    {
        if (ShowSaveFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowSaveFileDialog("Move or Rename Database…", Path.GetFileName(DbPath));
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var sameFolder = string.Equals(
            Path.GetDirectoryName(newPath), Path.GetDirectoryName(DbPath),
            StringComparison.OrdinalIgnoreCase);

        var confirmed = await ShowConfirmDialog(
            sameFolder ? "Rename Database" : "Move Database",
            string.Join("\n\n",
                $"{(sameFolder ? "Rename" : "Move")} the database to:\n{newPath}",
                "EVE Console will restart and do this before it finishes starting, because it has " +
                "to happen while nothing is using the database.",
                "Within the same drive this is immediate whatever the size. Moving to a different " +
                "drive copies the file first and removes the original once that has succeeded, " +
                "which may take a while on a large database.",
                "Nothing is left at the old location."));
        if (!confirmed) return;

        AppConfig.SetPendingRelocation(newPath);
        StatusText = sameFolder ? "Restarting to rename the database…"
                                : "Restarting to move the database…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }

    public async Task PointToExistingDatabaseAsync()
    {
        if (ShowOpenFileDialog is null || ShowConfirmDialog is null) return;

        var newPath = await ShowOpenFileDialog("Select Existing Database…");
        if (newPath is null) return;

        if (string.Equals(newPath, DbPath, StringComparison.OrdinalIgnoreCase)) return;

        var confirmed = await ShowConfirmDialog(
            "Switch Database",
            $"Point EVE Console to the existing database at:\n{newPath}\n\nThe application will restart.");
        if (!confirmed) return;

        AppConfig.SetDbPath(newPath);
        StatusText = "Done. Restarting…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }

    /// <summary>
    /// Records the request and restarts. The work itself happens on the way back up, before
    /// anything opens the database — see DatabaseShrinkService for why it cannot happen here.
    /// </summary>
    public async Task ShrinkDatabaseAsync()
    {
        if (ShowConfirmDialog is null || IsBusy) return;

        var sizeText = File.Exists(DbPath) ? FormatBytes(new FileInfo(DbPath).Length) : "unknown size";

        // ⚠️ One line per paragraph. The dialog wraps to its own width, so hard breaks inside a
        // paragraph wrap twice and come out ragged.
        var message = string.Join("\n\n",
            "Deleting data does not make the database file smaller. SQLite keeps the freed pages and reuses them later, so the file stays at its largest size. Shrinking rebuilds it and returns that space to your drive.",
            $"The database is currently {sizeText}.",
            "EVE Console will restart and shrink the database before it finishes starting, because the rebuild needs the database entirely to itself. This may take a while on a large database, and the app is unavailable until it finishes.",
            "A backup copy is taken first and removed once the shrink succeeds.");

        var confirmed = await ShowConfirmDialog("Shrink Database", message);
        if (!confirmed) return;

        AppConfig.SetShrinkPending(true);
        StatusText = "Restarting to shrink the database…";
        await Task.Delay(800);
        RequestRestart?.Invoke();
    }


    // ── Storage breakdown ─────────────────────────────────────────────────────
    //
    // On demand rather than on open: it scans, and most visits to this tab are about backups.

    private readonly DatabaseSizeService _sizeSvc = new();

    public ObservableCollection<TableSizeVm> TableSizes { get; } = [];

    private bool _isAnalysing;
    public bool IsAnalysing
    {
        get => _isAnalysing;
        private set { this.RaiseAndSetIfChanged(ref _isAnalysing, value);
                      this.RaisePropertyChanged(nameof(CanAnalyse)); }
    }
    public bool CanAnalyse => !IsAnalysing;

    private string _sizeStatusText = "Not measured yet.";
    public string SizeStatusText
    {
        get => _sizeStatusText;
        private set => this.RaiseAndSetIfChanged(ref _sizeStatusText, value);
    }

    private string _sizeSummaryText = "";
    public string SizeSummaryText
    {
        get => _sizeSummaryText;
        private set => this.RaiseAndSetIfChanged(ref _sizeSummaryText, value);
    }

    public bool HasTableSizes => TableSizes.Count > 0;

    public async Task AnalyseSizesAsync()
    {
        if (IsAnalysing) return;
        IsAnalysing = true;

        // Cleared up front so a reload visibly replaces the previous result rather than appearing
        // to sit unchanged while the scan runs.
        TableSizes.Clear();
        this.RaisePropertyChanged(nameof(HasTableSizes));
        SizeSummaryText = "";

        try
        {
            var progress = new Progress<string>(s => SizeStatusText = s);
            var report   = await _sizeSvc.AnalyseAsync(DbPath, progress);

            // Every table, including the empty ones: a table absent from the list reads as an
            // oversight, and the small ones are what make the big ones legible by comparison.
            // The service already returns them largest first.
            foreach (var t in report.Tables)
                TableSizes.Add(new TableSizeVm(t, report.UsedBytes));

            // ⚠️ Free-space-inside-the-file is deliberately NOT shown. FreeBytes is exact, but it
            // counts only wholly empty pages: deleting rows mostly leaves holes inside pages that
            // are still in use, so the figure reads as "reclaimable space" while being a floor far
            // below it. Only a VACUUM can answer the question people would ask of it.
            SizeSummaryText =
                $"File {FormatBytes(report.FileBytes)} across {report.Tables.Count:N0} tables. " +
                "Shares are of the space in use. Figures are measured and scaled to the file — " +
                "good for comparing tables, not exact byte counts.";
            SizeStatusText = $"Loaded {DateTime.Now:HH:mm:ss}.";

            this.RaisePropertyChanged(nameof(HasTableSizes));
        }
        catch (Exception ex)
        {
            SizeStatusText = $"Could not measure: {ex.Message}";
        }
        finally { IsAnalysing = false; }
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

/// <summary>One row of the storage breakdown.</summary>
public sealed class TableSizeVm(TableSizeRow row, long usedBytes)
{
    public string Name      { get; } = row.Name;
    public string RowsText  { get; } = row.Rows.ToString("N0");
    public string TableText { get; } = Format(row.TableBytes);
    public string IndexText { get; } = row.IndexCount == 0 ? "—" : Format(row.IndexBytes);
    public string TotalText { get; } = Format(row.TableTotalBytes);

    public double SharePercent { get; } =
        usedBytes == 0 ? 0 : row.TableTotalBytes * 100.0 / usedBytes;
    public string ShareText { get; } =
        usedBytes == 0 ? "" : $"{row.TableTotalBytes * 100.0 / usedBytes:N1}%";

    /// <summary>Says outright when a figure came from a sample, so nobody reads four significant
    /// figures into a number that was extrapolated from 20,000 rows.</summary>
    public string Method { get; } = row.Estimated ? "sampled" : "counted";

    public string Tooltip { get; } = row.IndexCount == 0
        ? $"{row.Rows:N0} row(s), no indexes."
        : $"{row.Rows:N0} row(s), {row.IndexCount} index(es) costing {Format(row.IndexBytes)}.";

    private static string Format(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }
}
