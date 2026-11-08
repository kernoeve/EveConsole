using System.Runtime.Versioning;

namespace EveConsole.Services;

/// <summary>
/// The desktop entry in <c>~/.config/autostart</c> that started the old notification-area process
/// with the session. Only found and taken away now: the desktop app shows the icon itself
/// (see <see cref="TrayIconController.RetireLogonTray"/>).
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

    public static void Remove()
    {
        if (OperatingSystem.IsLinux() && File.Exists(EntryPath)) File.Delete(EntryPath);
    }
}
