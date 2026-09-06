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
