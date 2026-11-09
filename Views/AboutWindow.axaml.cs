using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;
using System.Reflection;

namespace EveConsole.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = ver is not null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0";
    }

    /// <summary>Opens whichever link the clicked button carries in its Tag, in the system browser.</summary>
    /// <remarks>
    /// UseShellExecute is correct on both platforms for a URL: Windows hands it to the shell, and
    /// on Linux the runtime routes it to xdg-open, which is exactly right here — unlike
    /// AppLauncher's case, where handing an executable path to xdg-open is exactly wrong.
    /// </remarks>
    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url }) return;

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser configured; nothing an About box can usefully do about that */ }
    }

    /// <summary>
    /// Opens THIRD-PARTY-NOTICES.txt, which every build and release carries beside the executable, in the system's text
    /// viewer. Without the file (a build that lost it) the published copy on GitHub opens instead.
    /// </summary>
    private void OnNoticesClick(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        var target = File.Exists(path) ? path : "https://github.com/kernoeve/EveConsole/blob/main/THIRD-PARTY-NOTICES.txt";

        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* nothing registered to open it; the file is still there beside the app */ }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
