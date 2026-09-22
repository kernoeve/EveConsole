using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using EveConsole.Views;

namespace EveConsole.Services;

/// <summary>
/// The one way a link leaves the app for the browser, and the one place that asks first.
/// </summary>
/// <remarks>
/// <para>A link in a mail body, a corporation's description, a news item — anything written by
/// somebody else — says where it goes only in its own text, and that text can say anything.
/// Before the browser opens, the address is shown and the user says yes. "Don't ask me again"
/// is honoured for every external link in the app from then on, and lives in this machine's
/// own <see cref="UiState"/>: one person at two computers may well want different answers.</para>
///
/// <para>⚠️ Not for the app's own destinations. The SSO login, the Slack authorisation and the
/// About box open pages the user has just asked for by name, and a warning there is noise that
/// teaches them to click through it. This is for links that arrived as content.</para>
/// </remarks>
public static class ExternalLinks
{
    private static bool _asking;

    /// <summary>Confirms with the user, unless they have said not to, and opens the browser.</summary>
    public static void Open(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        url = url.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        Dispatcher.UIThread.Post(() => _ = OpenAsync(url));
    }

    private static async Task OpenAsync(string url)
    {
        if (UiState.GetBool(UiState.ConfirmExternalLinks, defaultValue: true))
        {
            // A second click while the question is still up would stack a second question.
            if (_asking) return;

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (owner is null) return;   // nowhere to ask; a link nobody can confirm stays closed

            _asking = true;
            bool go;
            try
            {
                var dialog = new ExternalLinkDialog(url);
                go = await dialog.ShowDialog<bool>(owner);
                if (go && dialog.DontAsk) UiState.SetBool(UiState.ConfirmExternalLinks, false);
            }
            finally { _asking = false; }

            if (!go) return;
        }

        Launch(url);
    }

    /// <summary>Hands the address to the OS. No confirmation — callers that have already asked,
    /// or that open the app's own pages, come here directly.</summary>
    public static void Launch(string url)
    {
        if (LaunchOverride is { } test) { test(url); return; }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser configured; nothing the app can usefully do about that */ }
    }

    /// <summary>Test seam: observe what would have been launched without starting a browser.</summary>
    internal static Action<string>? LaunchOverride { get; set; }
}
