using EveConsole.Data;
using EveConsole.Models;
using EveConsole.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace EveConsole.Monitoring;

/// <summary>
/// Imports EVE chat logs into the ChatMessages table.
///
/// ⚠ PRIVACY — this is why the service is shaped the way it is.
/// Chat logs contain other people's words, including private conversations, which
/// appear as channels named "Private Chat (2)", "Private Chat (3)" and so on. Those are
/// generic slot names reused for different people over time, so a single one can span
/// DMs with many different players. So there are TWO independent gates, and both must
/// be satisfied before a single row is written:
///   1. the feature is enabled — off by default, and
///   2. the channel is on an explicit allowlist — empty by default.
/// Enabling alone stores nothing. The allowlist is matched against the channel name
/// taken from the FILENAME, so a non-allowed channel's file is never even opened.
///
/// Read-only, like the game log importer: nothing is written back to any EVE file.
/// Mechanics (encoding, offsets, partial lines) come from LogFileCursor, shared with
/// the game log importer so the two cannot drift.
/// </summary>
public sealed class ChatLogImportService : ReactiveObject
{
    /// <summary>Tail mode only looks at files touched this recently.</summary>
    private const int TailWindowMinutes = 180;

    private const int InsertBatchSize = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitoringSettings   _settings;
    private readonly AppErrorLogger       _errorLogger;

    private CancellationTokenSource? _cts;
    private Task?                    _runTask;
    private CancellationTokenSource? _importCts;

    public ChatLogImportService(
        IServiceScopeFactory scopeFactory,
        MonitoringSettings   settings,
        AppErrorLogger       errorLogger)
    {
        _scopeFactory = scopeFactory;
        _settings     = settings;
        _errorLogger  = errorLogger;
    }

    /// <summary>Run after each tail pass — intel parsing, wired at startup. Kept as a hook so
    /// this service knows nothing about intel.</summary>
    public Func<CancellationToken, Task>? AfterTail { get; set; }

    // ── Observable state ─────────────────────────────────────────────────────

    private string _statusText = "Chat logs: Disabled";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
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

    // ── Lifecycle ────────────────────────────────────────────────────────────

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
        StatusText = "Chat logs: Stopped";
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_settings.ChatEnabled && !IsBusy)
            {
                try
                {
                    await TailAsync(ct);

                    // Intel parsing runs on the same pass that stored the messages, so a
                    // sighting reaches the map as soon as it is logged. It fails independently:
                    // a parsing problem must not stop chat being imported.
                    if (AfterTail is { } hook)
                    {
                        try { await hook(ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            _errorLogger.Log(nameof(ChatLogImportService), "AfterTail", ex);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    StatusText = $"Chat logs: Error — {Truncate(ex.Message)}";
                    _errorLogger.Log(nameof(ChatLogImportService), nameof(RunAsync), ex);
                }
            }
            else if (!_settings.ChatEnabled)
            {
                StatusText = "Chat logs: Disabled";
            }

            await Task.Delay(TimeSpan.FromSeconds(_settings.ScanSeconds), ct);
        }
    }

    // ── Allowlist ────────────────────────────────────────────────────────────

    /// <summary>Both gates. Deliberately a single place, so no code path can skip one.</summary>
    private bool IsAllowed(string channelName)
    {
        if (!_settings.ChatEnabled) return false;

        var allowed = _settings.ChatChannels;
        if (allowed.Count == 0) return false;   // empty allowlist stores nothing

        return allowed.Contains(channelName, StringComparer.OrdinalIgnoreCase);
    }

    // ── Discovery ────────────────────────────────────────────────────────────

    /// <summary>
    /// Channel names present in the chat log folders, from filenames only — no file is
    /// opened. Explicit and cached rather than automatic: these folders hold tens of
    /// thousands of files and often live on OneDrive, where enumeration is slow.
    /// </summary>
    public async Task<IReadOnlyList<string>> DiscoverChannelsAsync(CancellationToken ct = default)
    {
        IsBusy       = true;
        ProgressText = "Scanning chat log folders…";

        try
        {
            var channels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var dir in _settings.ResolveChatDirectories())
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var path in Directory.EnumerateFiles(dir, "*.txt"))
                        {
                            ct.ThrowIfCancellationRequested();
                            var name = ChatLogRules.ChannelNameFromFile(path);
                            if (!string.IsNullOrWhiteSpace(name)) channels.Add(name);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _errorLogger.Log(nameof(ChatLogImportService), $"Discover {dir}", ex);
                    }
                }
            }, ct);

            var sorted = channels.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            _settings.ChatDiscoveredChannels = sorted;

            ProgressText = $"Found {sorted.Count:N0} channel(s).";
            StatusText   = ProgressText;
            return sorted;
        }
        catch (Exception ex)
        {
            ProgressText = $"Discovery failed — {Truncate(ex.Message)}";
            _errorLogger.Log(nameof(ChatLogImportService), nameof(DiscoverChannelsAsync), ex);
            return [];
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ── Tail ─────────────────────────────────────────────────────────────────

    private async Task TailAsync(CancellationToken ct)
    {
        var dirs = _settings.ResolveChatDirectories();
        if (dirs.Count == 0) { StatusText = "Chat logs: no folder found"; return; }

        if (_settings.ChatChannels.Count == 0)
        {
            StatusText = "Chat logs: no channels selected";
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff   = DateTime.UtcNow.AddMinutes(-TailWindowMinutes);
        var imported = 0;

        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();

            List<FileInfo> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = LogFileCursor.RecentFiles(dir, cutoff)
                            // Allowlist applied on the FILENAME, so a channel that is
                            // not selected is never opened.
                            .Where(f => IsAllowed(ChatLogRules.ChannelNameFromFile(f.FullName)))
                            .ToList();
            }
            catch (Exception ex)
            {
                _errorLogger.Log(nameof(ChatLogImportService), $"Enumerate {dir}", ex);
                continue;
            }

            foreach (var fi in files)
            {
                ct.ThrowIfCancellationRequested();
                imported += await SafeImportAsync(db, fi, ct);
            }
        }

        StatusText = imported > 0
            ? $"Chat logs: +{imported:N0} message(s)"
            : $"Chat logs: watching {_settings.ChatChannels.Count} channel(s)";
    }

    // ── History import ───────────────────────────────────────────────────────

    /// <summary>How many files a history import would open, after the allowlist.</summary>
    public int EstimateHistoryFiles(int days)
    {
        var cutoff = days <= 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);
        var count  = 0;

        foreach (var dir in _settings.ResolveChatDirectories())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                count += new DirectoryInfo(dir).EnumerateFiles("*.txt")
                             .Count(f => f.LastWriteTimeUtc >= cutoff
                                      && IsAllowed(ChatLogRules.ChannelNameFromFile(f.FullName)));
            }
            catch { /* unreachable path contributes nothing */ }
        }

        return count;
    }

    public void CancelImport() => _importCts?.Cancel();

    public async Task ImportHistoryAsync(int days)
    {
        if (IsBusy) return;

        if (_settings.ChatChannels.Count == 0)
        {
            StatusText = "Chat logs: select at least one channel first";
            return;
        }

        _importCts = new CancellationTokenSource();
        var ct     = _importCts.Token;

        IsBusy          = true;
        ProgressCurrent = 0;
        ProgressTotal   = 1;
        ProgressText    = "Scanning chat log folders…";

        try
        {
            var cutoff = days <= 0 ? DateTime.MinValue : DateTime.UtcNow.AddDays(-days);
            var files  = new List<FileInfo>();

            foreach (var dir in _settings.ResolveChatDirectories())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    files.AddRange(new DirectoryInfo(dir).EnumerateFiles("*.txt")
                        .Where(f => f.LastWriteTimeUtc >= cutoff
                                 && IsAllowed(ChatLogRules.ChannelNameFromFile(f.FullName))));
                }
                catch (Exception ex)
                {
                    _errorLogger.Log(nameof(ChatLogImportService), $"Enumerate {dir}", ex);
                }
            }

            // Oldest first, so a cancelled import leaves a contiguous span of history.
            files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));

            ProgressTotal = Math.Max(1, files.Count);
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

            StatusText = ct.IsCancellationRequested
                ? $"Chat logs: import cancelled after {ProgressCurrent:N0} file(s), {imported:N0} message(s)"
                : $"Chat logs: imported {imported:N0} message(s) from {files.Count:N0} file(s)";
            ProgressText = StatusText;
        }
        catch (Exception ex)
        {
            StatusText   = $"Chat logs: import failed — {Truncate(ex.Message)}";
            ProgressText = StatusText;
            _errorLogger.Log(nameof(ChatLogImportService), nameof(ImportHistoryAsync), ex);
        }
        finally
        {
            IsBusy = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    // ── File import ──────────────────────────────────────────────────────────

    private async Task<int> SafeImportAsync(AppDbContext db, FileInfo fi, CancellationToken ct)
    {
        try
        {
            var record = await db.ChatLogFiles.FindAsync([fi.FullName], ct);
            return await ImportFileAsync(db, fi, record, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return 0; }   // locked or vanished; next pass retries
        catch (Exception ex)
        {
            _errorLogger.Log(nameof(ChatLogImportService), $"Import {fi.Name}", ex);
            return 0;
        }
    }

    private async Task<int> ImportFileAsync(
        AppDbContext db, FileInfo fi, ChatLogFile? record, CancellationToken ct)
    {
        var channelName = ChatLogRules.ChannelNameFromFile(fi.FullName);

        // Belt and braces: the enumerator already filtered, but this method must never
        // be the one that leaks a non-allowed channel into the database.
        if (!IsAllowed(channelName)) return 0;

        var isNew = record is null;
        record ??= new ChatLogFile
        {
            Path                = fi.FullName,
            ChannelName         = channelName,
            FirstSeenAt         = DateTimeOffset.UtcNow,
            ListenerCharacterId = LogFileCursor.TryParseCharacterId(fi.FullName),
        };

        // Shorter than what we already consumed means truncation or a reused filename.
        //
        // The re-read gives every line a new ChatMessages row with a new id, so anything derived
        // from the old rows has to go with them. Without this the intel parsed from the deleted
        // messages survives as orphans and the re-read parses the same lines again — the same
        // sighting twice, distinguishable only by a chat id that no longer resolves. It happens
        // routinely when the logs sit on a synced or network share, where the reported length
        // goes backwards for reasons that have nothing to do with the file being truncated.
        if (fi.Length < record.LastOffset)
        {
            var path = record.Path;

            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM "IntelReportCharacters" WHERE "IntelReportId" IN (
                  SELECT r."Id" FROM "IntelReports" r
                  JOIN "ChatMessages" m ON m."Id" = r."ChatMessageId"
                  WHERE m."SourceFile" = {0})
                """, [path], ct);

            await db.Database.ExecuteSqlRawAsync("""
                DELETE FROM "IntelReports" WHERE "ChatMessageId" IN (
                  SELECT "Id" FROM "ChatMessages" WHERE "SourceFile" = {0})
                """, [path], ct);

            await db.ChatMessages.Where(m => m.SourceFile == path).ExecuteDeleteAsync(ct);
            record.LastOffset     = 0;
            record.LastLineNumber = 0;
        }

        if (!isNew && fi.Length == record.LastOffset) return 0;

        var encoding = LogFileCursor.DetectEncoding(fi.FullName);
        var chunk    = await LogFileCursor.ReadNewLinesAsync(fi, record.LastOffset, encoding, ct);
        if (chunk is null) return 0;

        var rows       = new List<ChatMessage>();
        var lineNumber = record.LastLineNumber;

        foreach (var rawLine in chunk.Lines)
        {
            // Chat logs put a U+FEFF at the start of every message line, not just the
            // file header — without stripping it per line, nothing matches.
            var raw = LogFileCursor.StripLeadingBom(rawLine);
            lineNumber++;

            if (record.ChannelId is null && ChatLogRules.TryParseChannelId(raw) is { } cid)
            { record.ChannelId = cid; continue; }

            if (record.ListenerName is null && ChatLogRules.TryParseListener(raw) is { } listener)
            { record.ListenerName = listener; continue; }

            var msg = ChatLogRules.TryParseMessage(raw);
            if (msg is null) continue;

            rows.Add(new ChatMessage
            {
                OccurredAt          = msg.Timestamp is { } t ? ChatLogRules.FormatTimestamp(t) : "",
                ChannelName         = record.ChannelName,
                ChannelId           = record.ChannelId,
                ListenerCharacterId = record.ListenerCharacterId,
                ListenerName        = record.ListenerName,
                SenderName          = msg.Sender,
                Message             = msg.Text,
                IsSystemMessage     = msg.IsSystem,
                SystemName          = msg.SystemName,
                SourceFile          = record.Path,
                LineNumber          = lineNumber,
            });
        }

        record.LastOffset     += chunk.ConsumedBytes;
        record.LastLineNumber  = lineNumber;
        record.LastFileLength  = fi.Length;
        record.LastParsedAt    = DateTimeOffset.UtcNow;

        if (isNew) db.ChatLogFiles.Add(record);

        rows = await DropAlreadyStoredAsync(db, record.ChannelName, rows, ct);

        for (var i = 0; i < rows.Count; i += InsertBatchSize)
        {
            db.ChatMessages.AddRange(rows.Skip(i).Take(InsertBatchSize));
            await db.SaveChangesAsync(ct);
        }

        if (rows.Count == 0) await db.SaveChangesAsync(ct);

        // Keep the change tracker from growing over a multi-thousand-file import.
        db.ChangeTracker.Clear();

        return rows.Count;
    }

    /// <summary>
    /// Removes messages already stored from a different file.
    ///
    /// Two of the user's characters sitting in the same channel each write their own log, and
    /// importing a second PC's folder brings the same channels again — so the same message
    /// arrives several times under different filenames. The unique index on
    /// (SourceFile, LineNumber) cannot see that, because the file genuinely differs.
    ///
    /// A message is the same message when the channel, timestamp, sender and text all match.
    /// Only the chunk's own time span is queried, through the (ChannelName, OccurredAt) index,
    /// so this stays cheap no matter how much history is already stored.
    /// </summary>
    private static async Task<List<ChatMessage>> DropAlreadyStoredAsync(
        AppDbContext db, string channel, List<ChatMessage> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return rows;

        var from = rows.Min(r => r.OccurredAt);
        var to   = rows.Max(r => r.OccurredAt);

        var existing = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ChannelName == channel
                     && string.Compare(m.OccurredAt, from) >= 0
                     && string.Compare(m.OccurredAt, to)   <= 0)
            .Select(m => new { m.OccurredAt, m.SenderName, m.Message })
            .ToListAsync(ct);

        var seen = new HashSet<string>(existing.Count + rows.Count);
        foreach (var e in existing) seen.Add(Key(e.OccurredAt, e.SenderName, e.Message));

        // Also guards against a single file repeating a line, which the in-batch check catches
        // before SaveChanges rather than after.
        var kept = new List<ChatMessage>(rows.Count);
        foreach (var r in rows)
            if (seen.Add(Key(r.OccurredAt, r.SenderName, r.Message))) kept.Add(r);

        return kept;

        // Separated by U+001F (unit separator), which cannot appear in a chat line, so a sender
        // ending in the text's opening characters cannot collide with a different split.
        static string Key(string at, string sender, string text) => $"{at}{sender}{text}";
    }

    private static string Truncate(string s, int max = 60) => s.Length <= max ? s : s[..max];
}
