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

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
