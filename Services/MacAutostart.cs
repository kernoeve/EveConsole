using System.Runtime.Versioning;
using System.Security;

namespace EveConsole.Services;

/// <summary>
/// The macOS equivalent of the Windows <c>Run</c> registry key: a launch agent in
/// <c>~/Library/LaunchAgents</c>, which launchd starts when this person logs in.
///
/// <para>Deliberately not a login item through <c>SMAppService</c>. That one wants a helper bundled
/// inside a signed app, and this app ships unsigned for now; a launch agent is a plain file the
/// app writes and deletes itself. macOS 13 and later lists it under System Settings → General →
/// Login Items as "allowed in the background", where it can be switched off like any other.</para>
/// </summary>
[SupportedOSPlatform("macos")]
public static class MacAutostart
{
    private const string Label = "com.eveconsole.tray";

    public static string AgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    public static bool StartsAtLogin() => OperatingSystem.IsMacOS() && File.Exists(AgentPath);

    public static void SetStartsAtLogin(bool on)
    {
        if (!OperatingSystem.IsMacOS()) return;

        if (!on)
        {
            if (File.Exists(AgentPath)) File.Delete(AgentPath);
            return;
        }

        if (AppLauncher.RelaunchPath is not { } exe) return;

        Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
        File.WriteAllText(AgentPath, Agent(exe));
    }

    // ⚠️ RunAtLoad and nothing more: no KeepAlive. The tray menu's Exit has to mean exit, and a
    // KeepAlive agent would be restarted by launchd the moment it quit.
    //
    // The path is passed as its own ProgramArguments entry, so a space in it needs no quoting;
    // only XML's own characters need escaping.
    private static string Agent(string exe) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
         <plist version="1.0">
         <dict>
           <key>Label</key>
           <string>{Label}</string>
           <key>ProgramArguments</key>
           <array>
             <string>{SecurityElement.Escape(exe)}</string>
             <string>{Program.TrayArgument}</string>
           </array>
           <key>RunAtLoad</key>
           <true/>
           <key>ProcessType</key>
           <string>Interactive</string>
         </dict>
         </plist>

         """;
}
