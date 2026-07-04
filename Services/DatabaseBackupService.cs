using System.IO.Compression;
using Avalonia.Threading;

namespace EveCortex.Services;

public class DatabaseBackupService(AppPreferencesService prefs)
{
    private DispatcherTimer? _timer;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += async (_, _) =>
        {
            if (IsBackupDue())
                await BackupNowAsync(AppConfig.GetDbPath());
        };
        _timer.Start();
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

    public async Task<string?> BackupNowAsync(string dbPath, CancellationToken ct = default)
    {
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
        var dir     = Path.GetDirectoryName(dbPath) ?? "";
        var stem    = Path.GetFileNameWithoutExtension(dbPath);
        var pattern = $"{stem}_backup_*.db.gz";
        return Directory.GetFiles(dir, pattern)
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Name)
                        .ToList();
    }
}
