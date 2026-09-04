using System.Diagnostics;
using Npgsql;

namespace EveConsole.Services;

/// <summary>Where pg_dump was found, and whether it can be used against this server.</summary>
public sealed record PgDumpAvailability(
    string? Path,
    int     ClientMajor,
    int     ServerMajor,
    string  Message)
{
    public bool Usable => Path is not null && ClientMajor >= ServerMajor;
}

/// <summary>
/// Backs a PostgreSQL database up by running the server's own dump tool.
///
/// <para>The SQLite backup gzips the database file, which is a complete and honest backup because
/// the file IS the database. A server has no such file within reach — it may not even be this
/// machine — so the equivalent is pg_dump, whose custom format is compressed and restorable with
/// pg_restore, selectively if need be.</para>
///
/// <para>⚠️ pg_dump is not part of the application and is frequently absent, particularly on
/// Windows where nothing installs it unless PostgreSQL itself was installed locally. That is a
/// state the UI has to be able to describe rather than a failure to hide, so availability is
/// reported separately from the act of dumping.</para>
///
/// <para>⚠️ The client must be at least the server's major version. An older pg_dump refuses a
/// newer server outright, and the message it gives is about version numbers rather than about
/// what the user should do, so the check happens here where it can be explained.</para>
/// </summary>
public sealed class PgDumpService
{
    /// <summary>
    /// Finds pg_dump and compares it against the server.
    ///
    /// <para>PATH first, then the places an installer puts it. Looking in the well-known
    /// directories matters more than it might seem: the Windows installer does not add its bin
    /// directory to PATH by default, so a machine with PostgreSQL installed still answers "not
    /// found" to the obvious check.</para>
    /// </summary>
    public static async Task<PgDumpAvailability> ProbeAsync(
        string connectionString, CancellationToken ct = default)
    {
        var path = FindExecutable();
        if (path is null)
            return new PgDumpAvailability(null, 0, 0,
                "pg_dump was not found. It comes with the PostgreSQL client tools — "
                + "postgresql-client on Debian or Ubuntu, postgresql on Arch, or the Windows "
                + "installer's command line tools.");

        var clientMajor = await MajorVersionAsync(path, ct);
        int serverMajor;
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("SHOW server_version_num", conn);
            var num = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            serverMajor = num / 10000;
        }
        catch (Exception ex)
        {
            return new PgDumpAvailability(path, clientMajor, 0,
                $"Found pg_dump {clientMajor} at {path}, but could not reach the server to check "
                + $"its version: {ex.Message}");
        }

        if (clientMajor < serverMajor)
            return new PgDumpAvailability(path, clientMajor, serverMajor,
                $"pg_dump {clientMajor} at {path} is older than the server ({serverMajor}) and "
                + "will refuse to dump it. Install client tools of version "
                + $"{serverMajor} or newer.");

        return new PgDumpAvailability(path, clientMajor, serverMajor,
            $"pg_dump {clientMajor} at {path}; server {serverMajor}.");
    }

    /// <summary>
    /// Writes a dump and returns its path.
    ///
    /// <para>⚠️ The password goes in the child process's environment, never on its command line.
    /// A command line is readable by every process on the machine; an environment block is not.
    /// pg_dump has no password argument for exactly this reason.</para>
    /// </summary>
    public static async Task<string> BackupAsync(
        string exePath,
        string connectionString,
        string destinationDirectory,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        Directory.CreateDirectory(destinationDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var file  = Path.Combine(destinationDirectory, $"{b.Database}_backup_{stamp}.dump");

        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardError  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        // --format=custom is compressed and lets pg_restore pick out individual tables later;
        // --no-owner and --no-acl keep the dump restorable by whatever role restores it, which
        // is the usual case when moving between machines.
        psi.ArgumentList.Add($"--host={b.Host}");
        psi.ArgumentList.Add($"--port={b.Port}");
        psi.ArgumentList.Add($"--username={b.Username}");
        psi.ArgumentList.Add($"--dbname={b.Database}");
        psi.ArgumentList.Add("--format=custom");
        psi.ArgumentList.Add("--no-owner");
        psi.ArgumentList.Add("--no-acl");
        psi.ArgumentList.Add($"--file={file}");

        if (!string.IsNullOrEmpty(b.Password)) psi.Environment["PGPASSWORD"] = b.Password;

        progress?.Report("Running pg_dump…");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start {exePath}.");

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            // A failed dump leaves a partial file, which would otherwise sit in the folder looking
            // like a backup and be counted as one by the retention sweep.
            try { if (File.Exists(file)) File.Delete(file); } catch { }

            var why = string.IsNullOrWhiteSpace(stderr) ? $"exit code {proc.ExitCode}" : stderr.Trim();
            throw new InvalidOperationException($"pg_dump failed: {why}");
        }

        return file;
    }

    private static string? FindExecutable()
    {
        var name = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* an unusable PATH entry is not worth failing the search over */ }
        }

        var candidates = new List<string>();
        foreach (var root in WellKnownDirectories())
        {
            try
            {
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.EnumerateFiles(root, name, SearchOption.AllDirectories));
            }
            catch { }
        }

        // ⚠️ Ordered deliberately, not lexically. Sorting the paths as text picks PostgreSQL 9
        // over 18, because "9" is greater than "1" as a character; and it prefers the copy inside
        // pgAdmin's runtime folder over the one in bin, purely because "p" sorts after "b".
        // Neither is what anyone means by "the newest pg_dump". Version is compared as a number,
        // and bin wins ties as the canonical location.
        return candidates
            .OrderByDescending(VersionFromPath)
            .ThenByDescending(p => p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                                              StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }

    /// <summary>
    /// The highest whole number appearing as a path segment, which is where installers put the
    /// major version (…/PostgreSQL/18/bin, /usr/lib/postgresql/16/bin). Zero when there is none,
    /// so such a path sorts last rather than being discarded: it may still be a working pg_dump.
    /// </summary>
    private static int VersionFromPath(string path)
    {
        var best = 0;
        foreach (var segment in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (int.TryParse(segment, out var n) && n > best) best = n;
        return best;
    }

    private static IEnumerable<string> WellKnownDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return @"C:\Program Files\PostgreSQL";
            yield return @"C:\Program Files (x86)\PostgreSQL";
        }
        else
        {
            yield return "/usr/lib/postgresql";     // Debian and Ubuntu, one folder per version
            yield return "/usr/local/pgsql/bin";
            yield return "/opt/homebrew/opt";
        }
    }

    private static async Task<int> MajorVersionAsync(string exePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null) return 0;

            var text = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            // "pg_dump (PostgreSQL) 18.6" — the first run of digits is the major version.
            var digits = new string(text.SkipWhile(c => !char.IsDigit(c))
                                        .TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var major) ? major : 0;
        }
        catch { return 0; }
    }
}
