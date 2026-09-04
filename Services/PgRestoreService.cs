using System.Diagnostics;
using Npgsql;

namespace EveConsole.Services;

/// <summary>What a restore did, for the settings screen to report once the app is back.</summary>
public sealed record RestoreResult(bool Ran, bool Success, string Message);

/// <summary>
/// Puts a pg_dump archive back, replacing what is in the database now.
///
/// <para>⚠️ Requested, then performed on the NEXT START, exactly as the shrink and the relocation
/// are. Restoring drops every object and recreates it, and the app holds a pool of connections to
/// the database it would be dropping — services would keep querying tables that were vanishing
/// underneath them. Running before <c>ConfigureServices</c> means nothing has opened the database
/// yet, which is the same reason those two live there.</para>
///
/// <para>⚠️ This is the one destructive operation in the app. Everything else adds, updates or
/// moves aside; this deletes what is there in favour of what is in a file. The UI therefore
/// names the database, names the file, and requires a typed confirmation rather than a click.</para>
/// </summary>
public static class PgRestoreService
{
    public static RestoreResult? LastResult { get; private set; }

    /// <summary>
    /// Runs a restore if one was requested, and clears the request either way.
    ///
    /// <para>⚠️ The request is cleared BEFORE the work, not after. A dump that makes pg_restore
    /// fail badly enough to take the process down would otherwise be retried on every launch,
    /// and the app would never start again — the same shape of trap as a poison message on a
    /// queue.</para>
    /// </summary>
    public static async Task<RestoreResult> RunIfPendingAsync(
        IProgress<(double Pct, string Status)>? progress = null, CancellationToken ct = default)
    {
        var file = AppConfig.GetRestorePending();
        if (string.IsNullOrWhiteSpace(file)) return new RestoreResult(false, false, "");

        AppConfig.SetRestorePending(null);

        var outcome = await RunAsync(file, progress, ct);
        LastResult = outcome;
        return outcome;
    }

    private static async Task<RestoreResult> RunAsync(
        string file, IProgress<(double, string)>? progress, CancellationToken ct)
    {
        if (!File.Exists(file))
            return new RestoreResult(true, false, $"Restore skipped: {file} is no longer there.");

        var cs = AppConfig.GetPostgresConnection();
        if (string.IsNullOrWhiteSpace(cs))
            return new RestoreResult(true, false, "Restore skipped: no PostgreSQL connection is configured.");

        progress?.Report((10, "Looking for pg_restore…"));

        var probe = await PgDumpService.ProbeAsync(cs, ct);
        if (probe.Path is null)
            return new RestoreResult(true, false, "Restore failed: " + probe.Message);

        // pg_restore sits beside pg_dump, so the search that found one finds the other.
        var exe = Path.Combine(
            Path.GetDirectoryName(probe.Path)!,
            OperatingSystem.IsWindows() ? "pg_restore.exe" : "pg_restore");

        if (!File.Exists(exe))
            return new RestoreResult(true, false,
                $"Restore failed: pg_restore was not found beside pg_dump at {probe.Path}.");

        var b = new NpgsqlConnectionStringBuilder(cs);
        progress?.Report((25, $"Restoring {Path.GetFileName(file)} into {b.Database}…"));

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        psi.ArgumentList.Add($"--host={b.Host}");
        psi.ArgumentList.Add($"--port={b.Port}");
        psi.ArgumentList.Add($"--username={b.Username}");
        psi.ArgumentList.Add($"--dbname={b.Database}");

        // --clean --if-exists drops each object before recreating it, which is what makes this a
        // replacement rather than a merge; --if-exists keeps it quiet about objects the dump
        // knows and this database does not.
        psi.ArgumentList.Add("--clean");
        psi.ArgumentList.Add("--if-exists");
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("--no-acl");

        // ⚠️ NOT --single-transaction, which cannot be combined with --clean when some of the
        // drops are expected to fail. Individual errors are tolerated and counted instead: a
        // restore into a database that does not already hold every object is the ordinary case.
        //
        // The archive is the last positional argument; --file means something else entirely to
        // pg_restore (write SQL to a file instead of restoring), which would silently do nothing.
        psi.ArgumentList.Add(file);

        if (!string.IsNullOrEmpty(b.Password)) psi.Environment["PGPASSWORD"] = b.Password;

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Could not start {exe}.");

            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            progress?.Report((90, "Restore finished."));

            // ⚠️ A non-zero exit is not necessarily failure. pg_restore reports errors for every
            // DROP of an object that was not there, which is normal on a database that does not
            // already hold the whole dump; it still restores everything else. The data is what
            // decides, so the table count is checked rather than the exit code alone.
            var tables = await CountTablesAsync(cs, ct);

            if (tables > 0)
            {
                var warnings = stderr.Split('\n').Count(l => l.Contains("error:", StringComparison.OrdinalIgnoreCase));
                return new RestoreResult(true, true,
                    $"Restored {Path.GetFileName(file)} — {tables:N0} tables"
                    + (warnings > 0 ? $", {warnings:N0} ignorable error(s) during DROP." : "."));
            }

            var why = string.IsNullOrWhiteSpace(stderr) ? $"exit code {proc.ExitCode}" : Tail(stderr);
            return new RestoreResult(true, false, $"Restore failed and the database is empty: {why}");
        }
        catch (Exception ex)
        {
            return new RestoreResult(true, false, $"Restore failed: {ex.Message}");
        }
    }

    private static async Task<long> CountTablesAsync(string cs, CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(cs);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'", conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        catch { return 0; }
    }

    /// <summary>The last few lines of stderr — the first are usually the least informative.</summary>
    private static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", lines.TakeLast(3).Select(l => l.Trim()));
    }
}
