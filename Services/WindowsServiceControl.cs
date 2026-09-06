using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace EveConsole.Services;

/// <summary>
/// Installs, removes, starts and stops the background worker service, from the settings window.
///
/// <para>Two privilege levels are in play and the split is the whole design. Creating or deleting a
/// service needs elevation, so those relaunch this same executable with an internal argument and
/// let UAC ask once. Starting and stopping do not — provided installation granted the account that
/// right, which is what <see cref="GrantStartStop"/> is for. Without it every toggle of the switch
/// would raise a UAC prompt, and a switch that argues with you is one nobody uses.</para>
///
/// <para>⚠️ Every method is Windows-only and guarded. The package is referenced on all targets
/// because the code calling it is compiled on all targets; a runtime guard is what keeps a Linux
/// build from ever reaching it.</para>
/// </summary>
public static class WindowsServiceControl
{
    /// <summary>Internal arguments. Not documented anywhere a user would look: they exist so the
    /// app can ask itself to do something with a full token, not to be typed.</summary>
    public const string InstallArgument   = "--install-service";
    public const string UninstallArgument = "--uninstall-service";

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The service's state, or null when it is not installed at all.</summary>
    [SupportedOSPlatform("windows")]
    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(WindowsServiceHost.ServiceName);
            return sc.Status;
        }
        catch
        {
            // ServiceController throws rather than answering when there is no such service, so
            // "not installed" arrives here as an exception and means exactly that.
            return null;
        }
    }

    public static bool IsInstalled() => OperatingSystem.IsWindows() && Status() is not null;

    // ── Elevated half ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asks UAC for a full token and installs the service. Returns what went wrong, or null.
    /// </summary>
    public static string? Install() => Elevate(InstallArgument);

    /// <summary>Asks UAC for a full token and removes the service.</summary>
    public static string? Uninstall() => Elevate(UninstallArgument);

    private static string? Elevate(string argument)
    {
        if (!OperatingSystem.IsWindows()) return "Windows only.";

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return "Could not determine this application's path.";

            var start = new ProcessStartInfo(exe, argument)
            {
                UseShellExecute = true,   // required for the runas verb
                Verb            = "runas",
                CreateNoWindow  = true,
            };

            using var p = Process.Start(start);
            if (p is null) return "The elevated step did not start.";

            p.WaitForExit(TimeSpan.FromMinutes(2));

            // ⚠️ Its exit code, not its output. A separate elevated process has no console we can
            // read, so the two halves agree on a number: nonzero means it wrote the reason to the
            // application's own error log, which is where the settings page sends people.
            return p.ExitCode == 0 ? null : $"The elevated step failed (exit {p.ExitCode}). See the error log.";
        }
        catch (Exception ex)
        {
            // Cancelling the UAC prompt lands here, and is not a failure worth alarming about.
            return ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }
                ? "Cancelled."
                : ex.Message.Split('\n')[0];
        }
    }

    /// <summary>
    /// The work itself, run by the elevated instance of this executable.
    ///
    /// <para>⚠️ Elevation keeps the user's identity, so the CurrentUser-scoped connection string is
    /// still readable here — which is the only moment it can be copied into a machine-scoped one.
    /// A service installed with no database configured would start and immediately fail.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int RunInstall()
    {
        try
        {
            var connection = AppConfig.GetPostgresConnection();
            if (string.IsNullOrWhiteSpace(connection))
                return Fail("No PostgreSQL connection is configured. The service has nothing to connect to.");

            MachineConfig.Write(connection, AppConfig.GetGameLogDirs(), AppConfig.GetChatLogDirs());

            var exe = Environment.ProcessPath!;
            var create = Sc($"create {WindowsServiceHost.ServiceName} binPath= \"\\\"{exe}\\\" {Program.ServiceArgument}\" "
                          + $"start= auto obj= LocalSystem DisplayName= \"{WindowsServiceHost.DisplayName}\"");
            if (create != 0) return Fail($"sc create failed ({create}).");

            Sc($"description {WindowsServiceHost.ServiceName} \"Runs EVE Console's background processing: ESI polling, pricing, alarms and backups.\"");

            // Come back from a crash, but not from a deliberate exit. A version mismatch against the
            // database is a fatal, correct refusal to run; restarting it would loop.
            Sc($"failure {WindowsServiceHost.ServiceName} reset= 86400 actions= restart/15000/restart/60000//0");

            GrantStartStop();
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    [SupportedOSPlatform("windows")]
    public static int RunUninstall()
    {
        try
        {
            // Stopped first: sc delete on a running service marks it for deletion and leaves it
            // there until the process exits, which looks to the settings page like nothing happened.
            try
            {
                using var sc = new ServiceController(WindowsServiceHost.ServiceName);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
            }
            catch { /* not installed, or already stopping */ }

            Sc($"delete {WindowsServiceHost.ServiceName}");

            // The credential goes with it. Leaving a machine-scoped copy of a password behind after
            // the thing that needed it is gone would be the worst kind of tidy-up to skip.
            MachineConfig.Delete();
            return 0;
        }
        catch (Exception ex) { return Fail(ex.Message); }
    }

    /// <summary>
    /// Lets the logged-on user start and stop this service without elevation.
    ///
    /// <para>⚠️ Read-modify-write of the existing descriptor, never a wholesale replacement. A
    /// hand-written SDDL that omits the SYSTEM or Administrators entries produces a service nobody
    /// can control — including the installer that would have to undo it.</para>
    ///
    /// <para>Failure is non-fatal: the service is installed and works, and the settings page falls
    /// back to elevating for start and stop.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void GrantStartStop()
    {
        try
        {
            var current = ScOutput($"sdshow {WindowsServiceHost.ServiceName}").Trim();
            if (!current.StartsWith("D:", StringComparison.Ordinal)) return;

            // RP start, WP stop, CR user-defined control, for Interactive Users.
            const string ace = "(A;;RPWPCR;;;IU)";
            if (current.Contains(ace, StringComparison.Ordinal)) return;

            // Before the audit section if there is one, since S: must come last.
            var audit = current.IndexOf("S:", StringComparison.Ordinal);
            var updated = audit >= 0
                ? current[..audit] + ace + current[audit..]
                : current + ace;

            Sc($"sdset {WindowsServiceHost.ServiceName} \"{updated}\"");
        }
        catch { /* the service still works; start and stop will just ask for elevation */ }
    }

    // ── Unelevated half ───────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    public static string? StartService()
    {
        try
        {
            using var sc = new ServiceController(WindowsServiceHost.ServiceName);
            if (sc.Status == ServiceControllerStatus.Running) return null;

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return null;
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    [SupportedOSPlatform("windows")]
    public static string? StopService()
    {
        try
        {
            using var sc = new ServiceController(WindowsServiceHost.ServiceName);
            if (sc.Status == ServiceControllerStatus.Stopped) return null;

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            return null;
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    // ── sc.exe ────────────────────────────────────────────────────────────────

    private static int Sc(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit(TimeSpan.FromSeconds(60));
        return p.ExitCode;
    }

    private static string ScOutput(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(TimeSpan.FromSeconds(60));

        // sc sdshow prints a blank line then the descriptor; the caller wants the descriptor.
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .FirstOrDefault(l => l.StartsWith("D:", StringComparison.Ordinal)) ?? "";
    }

    /// <summary>Where the elevated half writes what went wrong. Named in the settings page.</summary>
    public static string InstallLogPath => Path.Combine(MachineConfig.Folder, "service-install.log");

    private static int Fail(string message)
    {
        // ⚠️ A file, not AppErrorLogger. That one writes through the database, and this runs in a
        // separate elevated process that deliberately exits before building a container — the
        // failure being reported is quite often "there is no database to write to". A plain file
        // beside the service's own config is the one place that always works.
        try
        {
            Directory.CreateDirectory(MachineConfig.Folder);
            File.AppendAllText(InstallLogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* nothing left to try */ }

        return 1;
    }
}
