using System.Runtime.Versioning;

namespace EveConsole.Services;

/// <summary>
/// The launch agent in <c>~/Library/LaunchAgents</c> that started the old notification-area process
/// at login. Only found and taken away now: the desktop app shows the icon itself
/// (see <see cref="TrayIconController.RetireLogonTray"/>).
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacAutostart
{
    private const string Label = "com.eveconsole.tray";

    public static string AgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    public static bool StartsAtLogin() => OperatingSystem.IsMacOS() && File.Exists(AgentPath);

    public static void Remove()
    {
        if (OperatingSystem.IsMacOS() && File.Exists(AgentPath)) File.Delete(AgentPath);
    }
}
