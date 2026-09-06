using System.Diagnostics;
using System.Runtime.Versioning;

namespace EveConsole.Services;

/// <summary>
/// Installs and controls the background worker as a systemd <em>user</em> unit.
///
/// <para>⚠️ A user unit, not a system one, and that choice decides almost everything about how this
/// behaves. It lives in <c>~/.config/systemd/user</c>, which the user owns — so installing needs no
/// root, no polkit and no prompt, unlike the Windows side. It also runs inside the login session's
/// D-Bus, which is what lets it reach the keyring: the saved database password just works, with no
/// environment variable holding a credential.</para>
///
/// <para>⚠️ The cost is that it starts at LOGIN, not at boot. A machine that boots unattended and
/// nobody signs into runs nothing — and enabling lingering to change that puts the keyring back out
/// of reach, because the login keyring is unlocked by PAM with the user's password. For that case
/// there is a system unit in packaging/, which takes its connection string from an environment file
/// precisely because it cannot ask the keyring for one.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public static class SystemdServiceControl
{
    public const string UnitName = "eveconsole-worker.service";

    public static bool IsSupported => OperatingSystem.IsLinux() && Which("systemctl") is not null;

    private static string UnitDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "systemd", "user");

    public static string UnitPath => Path.Combine(UnitDirectory, UnitName);

    public static bool IsInstalled() => OperatingSystem.IsLinux() && File.Exists(UnitPath);

    /// <summary>Whether systemd will start it at the next login.</summary>
    public static bool StartsAtLogin() =>
        IsInstalled() && Systemctl("is-enabled", UnitName).Output.StartsWith("enabled", StringComparison.Ordinal);

    public static bool IsRunning() =>
        IsInstalled() && Systemctl("is-active", UnitName).Output.StartsWith("active", StringComparison.Ordinal);

    /// <summary>
    /// The path the installed unit will actually run, read back out of its <c>ExecStart=</c>.
    ///
    /// <para>⚠️ Read from the file rather than remembered. The unit outlives this process, gets
    /// edited by hand, and survives the application being moved or replaced — the only honest
    /// answer to "what will start at your next login" is the one written down.</para>
    /// </summary>
    public static string? InstalledExePath()
    {
        try
        {
            if (!IsInstalled()) return null;

            foreach (var line in File.ReadAllLines(UnitPath))
            {
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith("ExecStart=", StringComparison.Ordinal)) continue;

                var value = trimmed["ExecStart=".Length..].Trim();

                // ⚠️ The path is quoted when it contains a space, so the first token is not simply
                // "up to the first space". Take a quoted run whole; otherwise take one word.
                if (value.StartsWith('"'))
                {
                    var end = value.IndexOf('"', 1);
                    if (end > 0) return value[1..end].Replace("\\\"", "\"").Replace("\\\\", "\\");
                }

                var space = value.IndexOf(' ');
                return space < 0 ? value : value[..space];
            }

            return null;
        }
        catch { return null; }
    }

    /// <summary>Whether the installed unit runs this copy of the application.</summary>
    public static bool PointsAtThisCopy() =>
        IsInstalled() && AppLauncher.SameFile(InstalledExePath(), AppLauncher.RelaunchPath);

    /// <summary>
    /// Writes the unit, reloads systemd, enables it and starts it. Returns what went wrong, or null.
    /// </summary>
    public static string? Install()
    {
        try
        {
            if (AppLauncher.RelaunchPath is not { } exe) return "Could not determine this application's path.";

            Directory.CreateDirectory(UnitDirectory);
            File.WriteAllText(UnitPath, UnitFile(exe));

            // ⚠️ daemon-reload first. systemd caches units, so enabling one it has not read yet
            // fails with "unit not found" about a file plainly sitting there.
            var reload = Systemctl("daemon-reload");
            if (reload.ExitCode != 0) return $"systemctl daemon-reload failed: {reload.Output}";

            var enable = Systemctl("enable", "--now", UnitName);
            return enable.ExitCode == 0 ? null : $"systemctl enable failed: {enable.Output}";
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    public static string? Uninstall()
    {
        try
        {
            Systemctl("disable", "--now", UnitName);

            if (File.Exists(UnitPath)) File.Delete(UnitPath);

            // After deleting, so systemd forgets a unit whose file has gone rather than keeping it
            // listed as not-found for the rest of the session.
            Systemctl("daemon-reload");
            return null;
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    /// <summary>
    /// Rewrites the unit to run this copy, and restarts it if it was running.
    ///
    /// <para>Needed by both Linux builds, for different reasons. A tarball that was extracted
    /// somewhere temporary and later moved leaves a unit pointing at nothing. An AppImage that was
    /// downloaded again under a new version-stamped filename leaves a unit pointing at the old
    /// file, which still exists and still runs — so it fails silently, in the worst way: the
    /// background worker comes up on the previous version and the version gate stops the desktop
    /// client with a mismatch nobody can account for.</para>
    /// </summary>
    public static string? Repoint()
    {
        try
        {
            if (AppLauncher.RelaunchPath is not { } exe) return "Could not determine this application's path.";
            if (!IsInstalled()) return "Not installed.";

            var wasRunning = IsRunning();
            var wasEnabled = StartsAtLogin();

            File.WriteAllText(UnitPath, UnitFile(exe));

            var reload = Systemctl("daemon-reload");
            if (reload.ExitCode != 0) return $"systemctl daemon-reload failed: {reload.Output}";

            if (wasEnabled) Systemctl("enable", UnitName);

            // ⚠️ restart, not start: the point is to move a running worker onto the new path, and
            // `start` on something already running does nothing at all.
            if (wasRunning)
            {
                var restart = Systemctl("restart", UnitName);
                if (restart.ExitCode != 0) return restart.Output;
            }

            return null;
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    public static string? Start()
    {
        var r = Systemctl("start", UnitName);
        return r.ExitCode == 0 ? null : r.Output;
    }

    public static string? Stop()
    {
        var r = Systemctl("stop", UnitName);
        return r.ExitCode == 0 ? null : r.Output;
    }

    /// <summary>Turns "start at login" on or off without stopping or starting it now.</summary>
    public static string? SetStartsAtLogin(bool enabled)
    {
        var r = Systemctl(enabled ? "enable" : "disable", UnitName);
        return r.ExitCode == 0 ? null : r.Output;
    }

    /// <summary>The last few journal lines, for a settings page to show when something is wrong.</summary>
    public static string RecentLog()
    {
        try
        {
            var r = Run("journalctl", ["--user", "-u", UnitName, "-n", "15", "--no-pager"]);
            return r.Output.Trim();
        }
        catch { return ""; }
    }

    /// <summary>
    /// systemd command-line quoting: the whole path in double quotes, with backslashes and quotes
    /// escaped inside.
    ///
    /// <para>⚠️ Quoted unconditionally, because an unquoted <c>ExecStart=</c> is split on spaces and
    /// AppImages are routinely downloaded to paths that have them — <c>~/Applications/EVE
    /// Console-1.4.0-x86_64.AppImage</c> would be read as the program <c>~/Applications/EVE</c>
    /// with an argument.</para>
    /// </summary>
    private static string Quote(string path) =>
        '"' + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';

    private static string UnitFile(string exe) =>
        $"""
         # Written by EVE Console. Edit through the application, or replace this file.
         #
         # A USER unit: it runs as you, inside your login session, which is what lets it reach the
         # keyring for the saved database password. It therefore starts when you log in, not when
         # the machine boots. For an unattended machine, see the system unit in the project's
         # packaging directory, which takes its connection string from an environment file instead.
         #
         # ExecStart below is this copy as it was when the unit was written: for the AppImage that
         # is the .AppImage file itself, and for the tarball the extracted binary. Move, rename or
         # replace that file and the unit stops matching it — the application's settings will say
         # so, and can rewrite this file.

         [Unit]
         Description=EVE Console background worker ({(AppLauncher.IsAppImage ? "AppImage" : "tarball")})
         After=network-online.target
         Wants=network-online.target

         [Service]
         Type=simple
         ExecStart={Quote(exe)} --headless

         # The worker handles SIGTERM itself and releases its lease on the way out, so another
         # client picks the work up on its next tick rather than waiting for the server to notice a
         # dropped socket.
         KillSignal=SIGTERM
         TimeoutStopSec=30

         # on-failure, not always: a version mismatch against the database is a deliberate fatal
         # exit, and restarting would loop on it.
         Restart=on-failure
         RestartSec=15

         [Install]
         WantedBy=default.target

         """;

    // ── Running things ────────────────────────────────────────────────────────

    private static (int ExitCode, string Output) Systemctl(params string[] args)
        => Run("systemctl", ["--user", .. args]);

    private static (int ExitCode, string Output) Run(string file, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return (1, $"could not run {file}");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            // ⚠️ Both streams. systemctl reports "Failed to ..." on stderr and the state word on
            // stdout, and a caller shown only one of them gets either the answer or the reason,
            // never both.
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (p.ExitCode, text.Trim());
        }
        catch (Exception ex) { return (1, ex.Message.Split('\n')[0]); }
    }

    private static string? Which(string tool)
    {
        try
        {
            var r = Run("/usr/bin/env", ["which", tool]);
            return r.ExitCode == 0 && r.Output.Length > 0 ? r.Output : null;
        }
        catch { return null; }
    }
}
