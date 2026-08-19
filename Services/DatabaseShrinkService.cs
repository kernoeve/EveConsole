using Microsoft.Data.Sqlite;

namespace EveConsole.Services;

/// <summary>What a shrink did, for the status line the user sees afterwards.</summary>
public sealed record ShrinkResult(bool Ran, bool Succeeded, long BeforeBytes, long AfterBytes, string Message)
{
    public long SavedBytes => Math.Max(0, BeforeBytes - AfterBytes);
}

/// <summary>
/// Rebuilds the database file so deleted rows stop occupying disk.
///
/// <para>SQLite never hands pages back to the operating system. Deleting rows puts their pages on
/// an internal freelist to be reused, so the file stays the size it reached at its fullest. Only
/// a VACUUM — a full rebuild with pages packed tight — actually shrinks it, which is why this
/// exists alongside the retention settings rather than instead of them.</para>
///
/// <para><b>⚠️ Why an in-place VACUUM rather than VACUUM INTO and a file swap.</b> The swap version
/// shipped first and broke startup twice. <c>VACUUM INTO</c> writes a database in <c>delete</c>
/// journal mode regardless of the source, so every shrink left a file that EF's connection
/// interceptor then had to CONVERT back to WAL on first open — a conversion needing an exclusive
/// lock, which failed in the running app with "attempt to write a readonly database" and killed
/// startup while the splash sat there forever. An in-place VACUUM preserves the journal mode, so
/// the interceptor's <c>PRAGMA journal_mode = WAL</c> is a no-op. It also removes the temp file,
/// the delete-and-move swap and the stale-sidecar cleanup — three things that could go wrong,
/// replaced by one statement SQLite performs transactionally.</para>
///
/// <para><b>⚠️ Why it runs at startup rather than from the button.</b> A VACUUM needs the database
/// to itself, and every background service here creates its own DbContext on demand with no central
/// pause to hold them off. The button records a flag and restarts; this runs on the way back up,
/// before the DI container exists.</para>
/// </summary>
public static class DatabaseShrinkService
{
    private const string BackupSuffix = ".preshrink.bak";

    /// <summary>
    /// What the last shrink did, for the Database settings tab to report once the app is up.
    /// Static because the work happens before any service — including the view models — exists.
    /// </summary>
    public static ShrinkResult? LastResult { get; private set; }

    /// <summary>
    /// Runs a pending shrink, if one was requested. Call before anything opens the database.
    /// Never throws: a failed shrink must not stop the app from starting, because the user would
    /// have no way back in to turn it off.
    /// </summary>
    public static ShrinkResult RunIfPending(string dbPath, Action<double, string>? progress = null)
    {
        var outcome = Run(dbPath, progress);
        if (outcome.Ran) LastResult = outcome;
        return outcome;
    }

    private static ShrinkResult Run(string dbPath, Action<double, string>? progress)
    {
        if (!AppConfig.GetShrinkPending())
            return new ShrinkResult(false, false, 0, 0, "");

        // Cleared first, not last. If this crashes the machine mid-vacuum, the user gets a working
        // app on the next launch rather than an unskippable operation that fails every time.
        AppConfig.SetShrinkPending(false);

        var backup = dbPath + BackupSuffix;

        try
        {
            if (!File.Exists(dbPath))
                return new ShrinkResult(true, false, 0, 0, "Database file not found.");

            // ── 1. Fold the write-ahead log back into the file ──────────────────
            // The restart is an Environment.Exit, which kills the process without closing SQLite's
            // connections — so a -wal with committed frames is normally still sitting there. Back
            // up without folding it in first and those transactions are missing from the copy.
            progress?.Invoke(3, "Preparing database…");
            SqliteMaintenance.Checkpoint(dbPath);

            var before = new FileInfo(dbPath).Length;

            // ── 2. Safety copy ─────────────────────────────────────────────────
            // After the checkpoint and before any modification: the only moment the file is both
            // complete and untouched by anyone else.
            progress?.Invoke(5, "Backing up database…");
            Clean(backup);
            File.Copy(dbPath, backup);

            // ── 3. Rebuild, in place ───────────────────────────────────────────
            // SQLite does this transactionally: on any failure the original is left intact, which
            // is why there is no restore step below.
            progress?.Invoke(8, "Shrinking database — this may take a while…");
            using (var conn = new SqliteConnection(SqliteMaintenance.ConnectionString(dbPath)))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText    = "VACUUM";
                cmd.CommandTimeout = 0;               // no timeout; a big database is slow
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            // ── 4. Check before declaring success ──────────────────────────────
            progress?.Invoke(90, "Verifying…");
            if (!Verify(dbPath, out var problem))
                return new ShrinkResult(true, false, before, before,
                    $"Shrink finished but the database failed verification: {problem}. " +
                    $"A copy taken beforehand is at {backup}.");

            var after = new FileInfo(dbPath).Length;

            // ── 5. Only now is the backup redundant ────────────────────────────
            Clean(backup);
            progress?.Invoke(100, "Database shrunk.");

            // Belt and braces on top of Pooling=False: nothing this process opened survives into
            // the app's own connections.
            SqliteConnection.ClearAllPools();

            return new ShrinkResult(true, true, before, after,
                $"Database shrunk from {Format(before)} to {Format(after)} — {Format(before - after)} freed.");
        }
        catch (Exception ex)
        {
            SqliteConnection.ClearAllPools();
            var before = SafeLength(dbPath);
            return new ShrinkResult(true, false, before, before,
                $"Shrink failed — the database was left unchanged. A copy taken beforehand is at " +
                $"{backup}. ({ex.Message})");
        }
    }

    /// <summary>
    /// Asks SQLite whether the rebuilt database is sound. <c>quick_check</c> rather than
    /// <c>integrity_check</c>: it catches the plausible failure here without a second full pass
    /// over several gigabytes.
    /// </summary>
    private static bool Verify(string path, out string problem)
    {
        problem = "";
        try
        {
            using var conn = new SqliteConnection(SqliteMaintenance.ConnectionString(path));
            conn.Open();

            using (var check = conn.CreateCommand())
            {
                check.CommandText    = "PRAGMA quick_check(1)";
                check.CommandTimeout = 0;
                var result = check.ExecuteScalar()?.ToString();
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    problem = result ?? "no result";
                    return false;
                }
            }

            // A structurally sound but empty file would pass the check above.
            using var tables = conn.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table'";
            if (Convert.ToInt64(tables.ExecuteScalar() ?? 0L) == 0)
            {
                problem = "the rebuilt file contains no tables";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return false;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static void Clean(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static long SafeLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private static string Format(long bytes)
    {
        if (bytes >= 1_073_741_824) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576)     return $"{bytes / 1_048_576.0:F0} MB";
        if (bytes >= 1_024)         return $"{bytes / 1_024.0:F0} KB";
        return $"{bytes} B";
    }
}
