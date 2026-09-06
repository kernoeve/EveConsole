using System.Runtime.Versioning;
using System.Text;

namespace EveConsole.Services;

/// <summary>
/// The Linux equivalent of the Windows <c>Run</c> registry key: a desktop entry in
/// <c>~/.config/autostart</c>, which GNOME, KDE, XFCE and everything else following the XDG
/// autostart specification start when the session comes up.
///
/// <para>Deliberately not a systemd user unit, even though one is already installed next door for
/// the worker. A tray icon needs the graphical session — a unit ordered only against
/// <c>default.target</c> can start before there is a notification area to put an icon in, and the
/// icon silently does not appear. The autostart directory is run by the desktop environment itself,
/// which by definition exists by then.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public static class LinuxAutostart
{
    private const string FileName = "eveconsole-tray.desktop";

    private static string Directory => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart");

    public static string EntryPath => Path.Combine(Directory, FileName);

    public static bool StartsAtLogin() => OperatingSystem.IsLinux() && File.Exists(EntryPath);

    public static void SetStartsAtLogin(bool on)
    {
        if (!OperatingSystem.IsLinux()) return;

        if (!on)
        {
            if (File.Exists(EntryPath)) File.Delete(EntryPath);
            return;
        }

        if (AppLauncher.RelaunchPath is not { } exe) return;

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(EntryPath, Entry(exe));

        // The desktop entry specification wants autostart files executable on some
        // implementations and ignores the bit on others; setting it costs nothing and removes one
        // way for this to quietly do nothing.
        try { File.SetUnixFileMode(EntryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch { /* a filesystem without modes; the entry still works where the bit is ignored */ }
    }

    private static string Entry(string exe) =>
        $"""
         [Desktop Entry]
         Type=Application
         Version=1.0
         Name=EVE Console (notification area)
         Comment=Shows what the EVE Console background worker is doing
         Exec={Quote(exe)} {Program.TrayArgument}
         Terminal=false
         X-GNOME-Autostart-enabled=true

         """;

    /// <summary>
    /// Desktop-entry <c>Exec=</c> quoting, which is two escaping passes stacked on each other.
    ///
    /// <para>⚠️ Both passes matter here, and the order is not interchangeable. The Exec value is
    /// first unescaped as a desktop-entry string — where a backslash escapes the next character —
    /// and the result is then parsed as a command line, where <c>"</c>, <c>`</c>, <c>$</c> and
    /// <c>\</c> inside a quoted argument each need a backslash of their own. So the command-line
    /// escapes go on first and every backslash is then doubled to survive the value pass.</para>
    ///
    /// <para>Quoted unconditionally for the same reason the systemd unit is: AppImages land in
    /// paths with spaces in them often enough that "usually fine" is not good enough.</para>
    /// </summary>
    private static string Quote(string path)
    {
        var escaped = new StringBuilder();

        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            escaped.Append(c);
        }

        return '"' + escaped.ToString().Replace("\\", "\\\\") + '"';
    }
}
