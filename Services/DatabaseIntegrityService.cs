using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace EveConsole.Services;

/// <summary>One backup offered as a recovery source.</summary>
public sealed record BackupOption(string Path, DateTime TakenUtc, long CompressedBytes)
{
    public string Display =>
        $"{TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  ({CompressedBytes / 1024.0 / 1024.0:N1} MB)";
}

/// <summary>
/// Checks the database opens before anything tries to use it, and puts the pieces back when it
/// does not.
///
/// <para>⚠️ A database that will not open is not a startup error to print small on a splash and
/// hang behind. It is the one fault where the user has both the most to lose and the clearest
/// available remedy — a backup sitting next to the file — and no way to reach it, because the
/// app that would offer it is the app that will not start.</para>
/// </summary>
public static class DatabaseIntegrityService
{
    /// <summary>
    /// Whether the file at <paramref name="dbPath"/> can be opened and read as a database.
    ///
    /// <para>A missing or empty file is <b>fine</b>: that is a first run, and EnsureCreated will
    /// build it. Only a file with content that SQLite refuses counts as damage.</para>
    ///
    /// <para>⚠️ Opening is not enough to prove it. SQLite defers reading the header until it has
    /// to, so a connection to a text file opens quite happily; the read is what fails. Hence the
    /// query rather than a bare Open.</para>
    /// </summary>
    public static bool IsUsable(string dbPath, out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(dbPath)) return true;
            if (new FileInfo(dbPath).Length == 0) return true;

            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master";
            cmd.ExecuteScalar();

            return true;
        }
        catch (SqliteException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            // Anything else — a permission problem, a vanished mount — is equally a reason not to
            // carry on into EnsureCreated, and equally worth showing rather than swallowing.
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Watches the database while the app runs, so damage is dated rather than discovered.
    ///
    /// <para>⚠️ Checking only at startup means the most useful fact about a corruption —
    /// when it happened — is never recorded. A database found broken at launch could have
    /// broken at any point in the previous session, which is precisely the position this app was
    /// in the first time it happened: no way to tell whether it coincided with anything.</para>
    ///
    /// <para>Deliberately the cheap check and not <c>PRAGMA integrity_check</c>. Reading
    /// sqlite_master touches the header and the schema root, costs nothing on a 750 MB file, and
    /// catches the failure actually seen — page one replaced. A full verification walks every
    /// page and has no business running on a timer; it belongs behind a button.</para>
    /// </summary>
    public static void StartMonitoring(
        Func<string> dbPath, Action<string> onDamaged, TimeSpan interval, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var reported = false;

            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(interval, ct); }
                catch (OperationCanceledException) { return; }

                if (IsUsable(dbPath(), out var error))
                {
                    reported = false;                 // recovered, or a transient read failure
                    continue;
                }

                // ⚠️ Once per spell of damage, not once per tick. A database that will not open
                // will not open on the next pass either, and an hourly repeat of the same line
                // buries the timestamp that makes the entry worth having.
                if (reported) continue;
                reported = true;

                onDamaged($"The database stopped being readable at {DateTimeOffset.Now:u}: {error}");
            }
        }, ct);
    }

    /// <summary>
    /// Moves a damaged database aside and returns where it went.
    ///
    /// <para>⚠️ Renamed, never deleted. It may be the only copy of something, it is the evidence
    /// for whatever went wrong, and a tool that destroys the file it could not read has taken the
    /// decision out of the user's hands. The companion -wal and -shm travel with it: left behind
    /// they would attach themselves to the restored database and undo it.</para>
    /// </summary>
    public static string Quarantine(string dbPath)
    {
        var dir   = Path.GetDirectoryName(dbPath) ?? "";
        var stem  = Path.GetFileNameWithoutExtension(dbPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var moved = Path.Combine(dir, $"{stem}.damaged-{stamp}.db");

        File.Move(dbPath, moved, overwrite: false);

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var side = dbPath + suffix;
            if (!File.Exists(side)) continue;
            try { File.Move(side, moved + suffix, overwrite: false); } catch { }
        }

        return moved;
    }

    /// <summary>Backups that could be restored over this database, newest first.</summary>
    public static List<BackupOption> FindBackups(string dbPath)
    {
        try
        {
            var dir  = Path.GetDirectoryName(dbPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(dbPath);

            return Directory.GetFiles(dir, $"{stem}_backup_*.db.gz")
                            .Select(f => new FileInfo(f))
                            .OrderByDescending(f => f.Name)
                            .Select(f => new BackupOption(f.FullName, f.LastWriteTimeUtc, f.Length))
                            .ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Expands a backup into place.
    ///
    /// <para>⚠️ Written to a temporary file and moved in, rather than decompressed straight over
    /// the destination. A restore interrupted half way through would otherwise leave a second
    /// unopenable database where the first one was, and the user would have spent their backup to
    /// get back exactly where they started.</para>
    /// </summary>
    public static void RestoreFrom(BackupOption backup, string dbPath)
    {
        var tmp = dbPath + ".restoring";

        try
        {
            using (var src = File.OpenRead(backup.Path))
            using (var gz  = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = File.Create(tmp))
                gz.CopyTo(dst);

            if (!IsUsable(tmp, out var error))
                throw new InvalidDataException($"The backup is itself unreadable: {error}");

            File.Move(tmp, dbPath, overwrite: true);

            // A restored file must not inherit the journal of the one it replaced.
            foreach (var suffix in new[] { "-wal", "-shm" })
                try { File.Delete(dbPath + suffix); } catch { }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }
}
