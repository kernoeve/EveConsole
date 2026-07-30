using System.Text;
using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Monitoring;

/// <summary>
/// Imports EVE game logs into the GameLogEvents table.
///
/// Reads only. Files are opened with sharing flags that let the client keep writing
/// and rotating freely; nothing is ever written back to any EVE-owned file, and
/// nothing is sent to the game client.
///
/// TWO DISTINCT MODES, deliberately separated:
///
///   • TAIL (automatic, while enabled) — only looks at files touched in the last
///     <see cref="TailWindowMinutes"/> minutes, i.e. live sessions. Parse position is
///     recorded per file, so activity logged while EVE Console was closed is picked up
///     on the next start rather than lost.
///
///   • HISTORY IMPORT (explicit, user-initiated) — walks the whole archive back N days
///     with progress reporting. Kept separate so the user chooses how much history to
///     process BEFORE anything runs, rather than discovering the setting afterwards.
///     A logs folder can hold thousands of files going back years.
///
/// Directory scanning is a poll rather than FileSystemWatcher: FSW drops events under
/// load, has a fixed internal buffer that overflows, and is unreliable on
/// OneDrive-synced and network folders — all three of which apply here.
/// </summary>
public sealed class GameLogImportService : ReactiveObject
{
    /// <summary>Tail mode only considers files touched this recently. Wide enough to
    /// span a pause in a live session, narrow enough that startup never walks the
    /// whole archive.</summary>
    private const int TailWindowMinutes = 180;

    private const int InsertBatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitoringSettings   _settings;
    private readonly AppErrorLogger       _errorLogger;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;
    private CancellationTokenSource? _importCts;

    public GameLogImportService(
        IServiceScopeFactory scopeFactory,
        MonitoringSettings   settings,
        AppErrorLogger       errorLogger)
    {
        _scopeFactory = scopeFactory;
        _settings     = settings;
        _errorLogger  = errorLogger;
    }

    // ── Observable state ─────────────────────────────────────────────────────

    private string _statusText = "Game logs: Not started";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private long _importedTotal;
    public long ImportedTotal
    {
        get => _importedTotal;
        private set => this.RaiseAndSetIfChanged(ref _importedTotal, value);
    }

    private bool _isImporting;
    /// <summary>True while a history import is running.</summary>
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

    private int _progressTotal;
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

    /// <summary>True once a history import has completed at least once, so the UI can
    /// stop offering it as a first-run action.</summary>
    public bool HistoryImported => _settings.HistoryImported;

    // ── Lifecycle (mirrors EsiPollingService) ────────────────────────────────

    public void Start()
    {
        if (_cts is not null) return;
        _cts     = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _importCts?.Cancel();
        if (_cts is null) return;

        await _cts.CancelAsync();
        if (_runTask is not null)
            try { await _runTask; } catch (OperationCanceledException) { }

        _cts     = null;
        _runTask = null;
        StatusText = "Game logs: Stopped";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // A history import owns the DB and the status line while it runs.
            if (_settings.GameLogEnabled && !IsImporting)
            {
                try
                {
                    await TailAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    StatusText = $"Game logs: Error — {Truncate(ex.Message)}";
                    _errorLogger.Log(nameof(GameLogImportService), nameof(RunAsync), ex);
                }
            }
            else if (!_settings.GameLogEnabled)
            {
                StatusText = "Game logs: Disabled";
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.ScanSeconds), ct);
        }
    }

    // ── Tail ─────────────────────────────────────────────────────────────────

    private async Task TailAsync(CancellationToken ct)
    {
        var dirs = _settings.ResolveDirectories();
        if (dirs.Count == 0)
        {
            StatusText = "Game logs: no folder found";
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff      = DateTime.UtcNow.AddMinutes(-TailWindowMinutes);
        var imported    = 0;
        var unreachable = 0;

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();

            List<FileInfo> files;
            try
            {
                if (!Directory.Exists(dir)) { unreachable++; continue; }
                files = new DirectoryInfo(dir).EnumerateFiles("*.txt")
                            .Where(f => f.LastWriteTimeUtc >= cutoff)
                            .ToList();
            }
            catch (Exception ex)
            {
                unreachable++;
                _errorLogger.Log(nameof(GameLogImportService), $"Enumerate {dir}", ex);
                continue;
            }

            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                imported += await SafeImportAsync(db, fi, ct);
            }
        }

        if (imported > 0) ImportedTotal += imported;

        StatusText = unreachable > 0
            ? $"Game logs: watching, {unreachable} unreachable folder(s)"
            : imported > 0
                ? $"Game logs: +{imported:N0} new"
                : "Game logs: watching";
    }

    // ── History import ───────────────────────────────────────────────────────

    /// <summary>How many files a history import over <paramref name="days"/> would
    /// process. Shown before the user commits, so the cost is visible up front.</summary>
    public int EstimateHistoryFiles(int days)
    {
        var cutoff = days <= 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);
        var count  = 0;

        foreach (var dir in _settings.ResolveDirectories())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                count += new DirectoryInfo(dir).EnumerateFiles("*.txt")
                             .Count(f => f.LastWriteTimeUtc >= cutoff);
            }
            catch { /* unreachable path contributes nothing */ }
        }

        return count;
    }

    public void CancelImport() => _importCts?.Cancel();

    /// <summary>
    /// Walk the archive back <paramref name="days"/> days, reporting progress per file.
    /// Explicit and user-initiated — see the mode note on the class.
    /// </summary>
    public async Task ImportHistoryAsync(int days)
    {
        if (IsImporting) return;

        _importCts = new CancellationTokenSource();
        var ct     = _importCts.Token;

        IsImporting     = true;
        ProgressCurrent = 0;
        ProgressTotal   = 0;
        ProgressText    = "Scanning folders…";

        try
        {
            var cutoff = days <= 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);

            var files = new List<FileInfo>();
            foreach (var dir in _settings.ResolveDirectories())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    files.AddRange(new DirectoryInfo(dir).EnumerateFiles("*.txt")
                                       .Where(f => f.LastWriteTimeUtc >= cutoff));
                }
                catch (Exception ex)
                {
                    _errorLogger.Log(nameof(GameLogImportService), $"Enumerate {dir}", ex);
                }
            }

            // Oldest first, so a cancelled import leaves a contiguous span of history
            // rather than a hole in the middle.
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            ProgressTotal = files.Count;
            var imported  = 0;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            for (var i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested) break;

                var fi = files[i];
                ProgressCurrent = i + 1;
                ProgressText    = $"File {i + 1:N0} of {files.Count:N0} — {fi.Name}";

                imported += await SafeImportAsync(db, fi, ct);
            }

            ImportedTotal += imported;

            if (ct.IsCancellationRequested)
            {
                StatusText   = $"Game logs: import cancelled after {ProgressCurrent:N0} file(s), {imported:N0} row(s)";
                ProgressText = StatusText;
            }
            else
            {
                _settings.HistoryImported = true;
                StatusText   = $"Game logs: imported {imported:N0} row(s) from {files.Count:N0} file(s)";
                ProgressText = StatusText;
            }
        }
        catch (Exception ex)
        {
            StatusText   = $"Game logs: import failed — {Truncate(ex.Message)}";
            ProgressText = StatusText;
            _errorLogger.Log(nameof(GameLogImportService), nameof(ImportHistoryAsync), ex);
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    // ── File import ──────────────────────────────────────────────────────────

    private async Task<int> SafeImportAsync(AppDbContext db, FileInfo fi, CancellationToken ct)
    {
        try
        {
            var record = await db.GameLogFiles.FindAsync([fi.FullName], ct);
            return await ImportFileAsync(db, fi, record, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException)
        {
            // Locked or vanished mid-read; the next pass picks it up.
            return 0;
        }
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(GameLogImportService), $"Import {fi.Name}", ex);
            return 0;
        }
    }

    /// <summary>Read whatever is new in one file and store it. Returns rows added.</summary>
    private async Task<int> ImportFileAsync(
        AppDbContext db, FileInfo fi, GameLogFile? record, CancellationToken ct)
    {
        var isNew = record is null;
        record ??= new GameLogFile
        {
            Path        = fi.FullName,
            FirstSeenAt = DateTimeOffset.UtcNow,
            CharacterId = TryParseCharacterIdFromName(fi.FullName),
        };

        // A shorter file than last time means it was truncated, or the name was reused
        // for a different file. Start over and drop the rows from the old one.
        if (fi.Length < record.LastOffset)
        {
            var path = record.Path;
            await db.GameLogEvents.Where(e => e.SourceFile == path).ExecuteDeleteAsync(ct);
            record.LastOffset     = 0;
            record.LastLineNumber = 0;
        }

        if (!isNew && fi.Length == record.LastOffset) return 0;

        var encoding = LogFileCursor.DetectEncoding(fi.FullName);
        var chunk    = await LogFileCursor.ReadNewLinesAsync(fi, record.LastOffset, encoding, ct);
        if (chunk is null) return 0;

        var rows       = new List<GameLogEvent>();
        var lineNumber = record.LastLineNumber;

        foreach (var rawLine in chunk.Lines)
        {
            var raw = LogFileCursor.StripLeadingBom(rawLine);
            lineNumber++;

            if (record.CharacterName is null && GameLogRules.TryParseListener(raw) is { } listener)
                record.CharacterName = listener;

            var parsed = GameLogRules.TryParseLine(raw);
            if (parsed is null) continue;

            var row = GameLogRules.Match(parsed, record.CharacterId, record.CharacterName)
                   ?? (_settings.StoreUnmatched
                        ? GameLogRules.Unmatched(parsed, record.CharacterId, record.CharacterName)
                        : null);

            if (row is null) continue;

            row.SourceFile = record.Path;
            row.LineNumber = lineNumber;
            rows.Add(row);
        }

        record.LastOffset     += chunk.ConsumedBytes;
        record.LastLineNumber  = lineNumber;
        record.LastFileLength  = fi.Length;
        record.LastParsedAt    = DateTimeOffset.UtcNow;

        if (isNew) db.GameLogFiles.Add(record);

        for (var i = 0; i < rows.Count; i += InsertBatchSize)
        {
            db.GameLogEvents.AddRange(rows.Skip(i).Take(InsertBatchSize));
            await db.SaveChangesAsync(ct);
        }

        if (rows.Count == 0) await db.SaveChangesAsync(ct);

        // Keep the tracker from growing unboundedly over a multi-thousand-file import.
        db.ChangeTracker.Clear();

        return rows.Count;
    }

    private static long? TryParseCharacterIdFromName(string path)
        => LogFileCursor.TryParseCharacterId(path);

    private static string Truncate(string s, int max = 60) => s.Length <= max ? s : s[..max];
}
