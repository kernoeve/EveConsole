using System.IO.Compression;

namespace EveConsole.Services;

public class DatabaseBackupService(AppPreferencesService prefs)
{
    private Task? _loop;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Checks hourly whether a backup is due.
    ///
    /// <para>⚠️ A plain task loop rather than a DispatcherTimer. A DispatcherTimer only ticks
    /// while an Avalonia dispatcher is running, which made this the one background service that
    /// would have gone quiet in a headless worker — and gone quiet in the worst way, since a
    /// timer that never fires raises nothing to notice.</para>
    ///
    /// <para>The hour is waited before the first check, not after, which is what a
    /// DispatcherTimer did too: starting the app is not a reason to take a backup.</para>
    /// </summary>
    public void Start(CancellationToken outerCt = default)
    {
        if (_loop is not null) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var ct = _cts.Token;

        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(1), ct); }
                catch (OperationCanceledException) { return; }

                // ⚠️ Guarded. pg_dump missing or the server unreachable must cost one backup,
                // not end the loop for the rest of the session.
                if (!IsBackupDue()) continue;
                try { await BackupNowAsync(AppConfig.GetDbPath()); }
                catch { /* the next pass tries again; the manual button reports properly */ }
            }
        }, ct);
    }

    /// <summary>Stops the check, and leaves it startable again for a regained lease.</summary>
    public async Task StopAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }

        _cts.Dispose();
        _cts  = null;
        _loop = null;
    }

    public const string KeyEnabled    = "db.backup.enabled";
    public const string KeyInterval   = "db.backup.interval";   // hourly|daily|weekly|monthly
    public const string KeyKeepCount  = "db.backup.keep";
    public const string KeyLastTime   = "db.backup.last_time";  // ticks UTC
    public const string KeyLastSize   = "db.backup.last_size";  // bytes

    public bool   BackupEnabled  => prefs.Get(KeyEnabled)   != "false";
    public string Interval       => prefs.Get(KeyInterval) ?? "daily";
    public int    KeepCount      => (int)prefs.GetLong(KeyKeepCount, 7);
    public long   LastTimeTicks  => prefs.GetLong(KeyLastTime, 0);
    public long   LastSizeBytes  => prefs.GetLong(KeyLastSize, 0);

    public DateTime? LastBackupUtc =>
        LastTimeTicks > 0 ? new DateTime(LastTimeTicks, DateTimeKind.Utc) : null;

    public bool IsBackupDue()
    {
        if (!BackupEnabled) return false;
        var last = LastBackupUtc;
        if (last is null) return true;

        var elapsed = DateTime.UtcNow - last.Value;
        return Interval switch
        {
            "hourly"  => elapsed.TotalHours  >= 1,
            "weekly"  => elapsed.TotalDays   >= 7,
            "monthly" => elapsed.TotalDays   >= 30,
            _         => elapsed.TotalDays   >= 1,  // daily (default)
        };
    }

    /// <summary>
    /// Backs the database up, whichever engine it is.
    ///
    /// <para>The engine is decided here rather than by the caller so the hourly timer and the
    /// button on the settings screen cannot disagree about it.</para>
    /// </summary>
    public async Task<string?> BackupNowAsync(string dbPath, CancellationToken ct = default)
    {
        if (DbEngine.IsPostgres) return await BackupPostgresAsync(ct);

        if (!File.Exists(dbPath)) return null;

        var dir       = Path.GetDirectoryName(dbPath)!;
        var stem      = Path.GetFileNameWithoutExtension(dbPath);
        var stamp     = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var backupPath = Path.Combine(dir, $"{stem}_backup_{stamp}.db.gz");

        await using (var src  = new FileStream(dbPath,    FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var dst  = new FileStream(backupPath, FileMode.Create))
        await using (var gz   = new GZipStream(dst, CompressionLevel.Optimal))
            await src.CopyToAsync(gz, ct);

        var size = new FileInfo(backupPath).Length;
        await prefs.SetLongAsync(KeyLastTime,  DateTime.UtcNow.Ticks);
        await prefs.SetLongAsync(KeyLastSize,  size);

        // Prune old backups
        CleanupOldBackups(dir, stem);

        return backupPath;
    }

    /// <summary>
    /// Runs pg_dump against the configured server.
    ///
    /// <para>⚠️ A server's backup cannot be the file copy the SQLite path performs: the file is
    /// on the server, possibly on another machine, and copying it out from under a running
    /// PostgreSQL would not produce a usable database anyway. pg_dump asks the server for a
    /// consistent snapshot instead, which is the only correct way to do this from outside.</para>
    ///
    /// <para>Dumps go to the app data folder rather than beside a database file, there being no
    /// database file to sit beside.</para>
    /// </summary>
    private async Task<string?> BackupPostgresAsync(CancellationToken ct)
    {
        var cs = AppConfig.GetPostgresConnection();
        if (string.IsNullOrWhiteSpace(cs)) return null;

        var probe = await PgDumpService.ProbeAsync(cs, ct);
        if (!probe.Usable) throw new InvalidOperationException(probe.Message);

        var file = await PgDumpService.BackupAsync(probe.Path!, cs, AppConfig.AppDataDir, null, ct);

        var size = new FileInfo(file).Length;
        await prefs.SetLongAsync(KeyLastTime, DateTime.UtcNow.Ticks);
        await prefs.SetLongAsync(KeyLastSize, size);

        CleanupOldDumps();
        return file;
    }

    private void CleanupOldDumps()
    {
        try
        {
            foreach (var f in Directory.GetFiles(AppConfig.AppDataDir, "*_backup_*.dump")
                                       .OrderByDescending(f => f, StringComparer.Ordinal)
                                       .Skip(KeepCount))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private void CleanupOldBackups(string dir, string stem)
    {
        var pattern = $"{stem}_backup_*.db.gz";
        var files   = Directory.GetFiles(dir, pattern)
                               .OrderByDescending(f => f)   // timestamp in name → descending = newest first
                               .Skip(KeepCount)
                               .ToList();
        foreach (var f in files)
            try { File.Delete(f); } catch { }
    }

    public List<FileInfo> GetExistingBackups(string dbPath)
    {
        // A dump and a gzipped file are both "the backups" as far as the screen listing them is
        // concerned; only the shape of the name differs.
        var dir     = DbEngine.IsPostgres ? AppConfig.AppDataDir : Path.GetDirectoryName(dbPath) ?? "";
        var pattern = DbEngine.IsPostgres
            ? "*_backup_*.dump"
            : $"{Path.GetFileNameWithoutExtension(dbPath)}_backup_*.db.gz";

        if (!Directory.Exists(dir)) return [];

        return Directory.GetFiles(dir, pattern)
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Name)
                        .ToList();
    }
}
