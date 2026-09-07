using System.Runtime.InteropServices;

namespace EveConsole.Services;

/// <summary>
/// How to start this application <em>again, later</em> — from a service unit, an autostart entry or
/// a registry Run value.
///
/// <para>⚠️ This is not always <see cref="Environment.ProcessPath"/>, and on one of the two ways
/// EVE Console ships for Linux it is never that. An AppImage is a single file that mounts itself on
/// a temporary directory and runs the binary inside; <c>ProcessPath</c> is therefore something like
/// <c>/tmp/.mount_EveCons2Kx9f/usr/bin/EveConsole</c>, a path that ceases to exist the moment the
/// process exits and is different on the next run. Writing it into a systemd unit produces a unit
/// that works exactly once — until the next login, when it fails with "No such file or directory"
/// about a path nobody can find because it was never a real one.</para>
///
/// <para>The AppImage runtime hands the payload the answer in <c>APPIMAGE</c>: the absolute path of
/// the AppImage file the user actually launched. That path survives, and Velopack's updater
/// rewrites the AppImage in place, so it stays right across updates too.</para>
///
/// <para>The tarball build has no such indirection — it is an ordinary self-contained publish, and
/// <c>ProcessPath</c> is the real binary. It can still go stale, but only in the honest way: if the
/// extracted folder is moved or deleted. That is the same failure Windows already surfaces as "the
/// service points at another copy", and it is handled the same way here.</para>
/// </summary>
public static class AppLauncher
{
    /// <summary>
    /// The file to record anywhere a launch outlives this process. Null only if the runtime cannot
    /// say where it was started from, which should not happen for a published application.
    /// </summary>
    public static string? RelaunchPath
    {
        get
        {
            // ⚠️ Checked for existence, not merely for being set. Any process started BY the
            // AppImage inherits APPIMAGE through its environment, so a stale or hand-set value
            // would otherwise be taken as gospel by something that is not an AppImage at all.
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage)) return appImage;

            return Environment.ProcessPath;
        }
    }

    /// <summary>
    /// Starts another copy of this application, detached from this one. Returns what went wrong,
    /// or null.
    ///
    /// <para>⚠️ UseShellExecute only on Windows. Off Windows it hands the path to xdg-open, which
    /// consults the desktop's file associations for what to do with the file — it may run it, it
    /// may open it in an editor, and for an AppImage it may offer to unpack it. Starting our own
    /// binary is not a job for the file manager.</para>
    ///
    /// <para>⚠️ And through <c>setsid</c> off Windows, so the new process gets its own session and
    /// process group. Everything started here is meant to OUTLIVE this process — the tray icon, the
    /// replacement after a "Save and Restart", a second client from the tray menu — and a plain
    /// child does not. It inherits this process's group, so a signal sent to the group reaches it
    /// too, a terminal closing SIGHUPs it, and an IDE that ends a run by killing the group takes it
    /// along. Ticking the tray box started an icon that duly appeared and then vanished the moment
    /// the client that started it closed, which is the one moment the icon exists for.</para>
    /// </summary>
    public static string? Start(params string[] arguments)
    {
        try
        {
            if (RelaunchPath is not { } exe) return "Could not determine this application's path.";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = OperatingSystem.IsWindows(),
            };

            // setsid forks when it is already a group leader and execs in place when it is not;
            // either way what comes out is detached. If it is missing, start directly rather than
            // not at all — a tray icon that dies with its parent still beats no tray icon.
            if (!OperatingSystem.IsWindows() && Setsid() is { } setsid)
            {
                psi.FileName = setsid;
                psi.ArgumentList.Add(exe);
            }
            else
            {
                psi.FileName = exe;
            }

            foreach (var a in arguments)
                if (!string.IsNullOrWhiteSpace(a)) psi.ArgumentList.Add(a);

            System.Diagnostics.Process.Start(psi);
            return null;
        }
        catch (Exception ex) { return ex.Message.Split('\n')[0]; }
    }

    /// <summary>
    /// Where <c>setsid</c> lives, or null if this system has not got it.
    ///
    /// <para>Probed by path rather than by running <c>which</c>: it is part of util-linux and sits
    /// in one of two places on every distribution that has it, and spawning a process to find out
    /// whether we can spawn a process is a poor trade.</para>
    /// </summary>
    private static string? Setsid()
    {
        foreach (var path in new[] { "/usr/bin/setsid", "/bin/setsid" })
            if (File.Exists(path)) return path;

        return null;
    }

    /// <summary>
    /// Ends this process now, without running the shutdown path.
    ///
    /// <para>⚠️ Not <c>Environment.Exit</c> off Windows, and that is not caution — it is a hang
    /// that was observed. Environment.Exit calls libc's <c>exit()</c>, which runs the atexit
    /// handlers registered by every native library loaded into the process: X11, Skia, HarfBuzz,
    /// libvlc. Called from the UI thread with the windowing loop still on the stack, one of those
    /// does not return — and the process then ignores SIGTERM as well, because the runtime is
    /// already inside shutdown and never dispatches the signal, so only SIGKILL clears it. That is
    /// what "Save and Restart" did on Linux: the replacement came up fine and the old client had to
    /// be killed by hand.</para>
    ///
    /// <para>Nothing is lost by skipping it. Every setting written on these paths is a synchronous
    /// file write that has already returned, the log writers open and close per line, and a restart
    /// has never closed SQLite's connections cleanly — <see cref="SqliteMaintenance.Checkpoint"/>
    /// exists precisely because the write-ahead log is expected to survive an abrupt exit.</para>
    ///
    /// <para>The same abruptness is what makes it the right end for a normal exit on Linux too.
    /// Avalonia keeps a D-Bus connection for the tray and the desktop portal, and tearing it down
    /// marshals a disconnect notice to the dispatcher with a SYNCHRONOUS Send — which a dispatcher
    /// already shutting down answers with a cancelled operation. That surfaces on a thread pool
    /// thread as an unhandled TaskCanceledException and aborts the process with SIGABRT, long after
    /// every piece of real work has finished. There is nothing to fix in that teardown from here;
    /// there is only the choice not to enter it.</para>
    /// </summary>
    public static void ExitNow(int code = 0)
    {
        if (OperatingSystem.IsWindows()) { Environment.Exit(code); return; }

        // ⚠️ _exit, not exit. exit() is what runs the atexit handlers; _exit is the raw syscall —
        // no handlers, no flushing, nothing left that can wedge or throw.
        //
        // ⚠️ Resolved through the main program handle rather than DllImport("libc"). The runtime's
        // name probing looks for "libc.so", which on glibc is a linker script rather than a shared
        // object, so binding by name can fail on exactly the systems this exists for. The main
        // program handle sees every symbol already loaded into the process, libc's included.
        try
        {
            if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "_exit", out var fn))
            {
                Marshal.GetDelegateForFunctionPointer<ExitFunction>(fn)(code);
                return;   // not reached
            }
        }
        catch { /* fall through to the blunter forms below */ }

        // No _exit to be had. A signal to ourselves is just as immediate; it costs the exit code,
        // which becomes 137, and nothing here reads ours.
        try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
        catch { Environment.Exit(code); }   // nothing better left to try
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ExitFunction(int code);

    /// <summary>Whether this copy is running from an AppImage, for wording that has to differ.</summary>
    public static bool IsAppImage
    {
        get
        {
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            return !string.IsNullOrWhiteSpace(appImage) && File.Exists(appImage);
        }
    }

    /// <summary>
    /// How this copy was installed, in the words the person reading a settings page would use.
    /// </summary>
    public static string InstallKind =>
        IsAppImage                  ? "AppImage"
        : OperatingSystem.IsLinux() ? "tarball"
                                    : "installed";

    /// <summary>
    /// Whether <paramref name="recorded"/> — a path read back out of a unit file or autostart entry
    /// — refers to this same copy of the application.
    ///
    /// <para>⚠️ Compared after resolving symlinks and relative segments. An AppImage in
    /// <c>~/Applications</c> reached through a symlink on the desktop is one copy, and a comparison
    /// on the literal strings would call it two and offer to repoint a service that is already
    /// correct.</para>
    /// </summary>
    public static bool SameFile(string? recorded, string? mine)
    {
        if (string.IsNullOrWhiteSpace(recorded) || string.IsNullOrWhiteSpace(mine)) return false;

        try
        {
            var a = Path.GetFullPath(recorded);
            var b = Path.GetFullPath(mine);

            if (File.ResolveLinkTarget(a, returnFinalTarget: true) is { } ra) a = ra.FullName;
            if (File.ResolveLinkTarget(b, returnFinalTarget: true) is { } rb) b = rb.FullName;

            return string.Equals(a, b, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(recorded, mine, StringComparison.Ordinal);
        }
    }
}
