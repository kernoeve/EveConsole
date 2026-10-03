using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveConsole.Services;
using Velopack;
using EveConsole.Localization;

namespace EveConsole.Views;

/// <summary>
/// A database ahead of this build: says so, looks for the release that can open it, and offers to
/// install it.
///
/// <para>⚠️ Why this exists. When one client upgrades a shared database, every client still on the
/// older build stops at start — correctly, since schema changes only move forward. But the updater
/// lives in the main window, which a stopped client never reaches, so all it could do was say
/// "update this client" and close, leaving a person to find the release and install it by hand.
/// And the obvious way to do that, running the installer again, replaces the whole install
/// folder, which is also where this app keeps its settings and, for SQLite, its database.</para>
/// </summary>
public partial class UpdateRequiredDialog : Window
{
    private readonly Version        _database;
    private readonly AppErrorLogger _log;
    private UpdateManager?          _mgr;
    private UpdateInfo?             _found;

    public UpdateRequiredDialog(string message, Version database, AppErrorLogger log)
    {
        InitializeComponent();
        MessageText.Text = message;
        _database        = database;
        _log             = log;
        Opened += async (_, _) => await CheckAsync();
    }

    // ⚠️ Everything inside the try, the manager's construction included: this runs from an event
    // handler on the way out of a failed start, and a throw here would crash the one path meant
    // to rescue the client. (UpdateManager throws when Velopack was never initialised.)
    private async Task CheckAsync()
    {
        try
        {
            var mgr = AppUpdater.CreateManager();
            if (!mgr.IsInstalled)
            {
                ShowRemedy(AppUpdater.AheadRemedy.NotInstalled, null);
                return;
            }

            _mgr   = mgr;
            _found = await mgr.CheckForUpdatesAsync();
            var latest = _found is null ? null : AppUpdater.VersionOf(_found);
            ShowRemedy(AppUpdater.RemedyFor(true, false, latest, _database), latest);
        }
        catch (Exception ex)
        {
            _log.Log("Startup", "update check for a database ahead of this build", ex);
            ShowRemedy(AppUpdater.AheadRemedy.CheckFailed, null, ex.Message);
        }
    }

    /// <summary>Puts the dialog in the state the update check ended in. Internal so the
    /// dialog's states can be rendered without a release feed.</summary>
    internal void ShowRemedy(AppUpdater.AheadRemedy remedy, Version? latest, string? error = null)
    {
        StatusText.Text        = AppUpdater.Describe(remedy, _database, latest, error);
        UpdateButton.IsVisible = remedy == AppUpdater.AheadRemedy.UpdateAvailable;
        if (UpdateButton.IsVisible) UpdateButton.Focus();
    }

    private async void OnUpdate(object? sender, RoutedEventArgs e)
    {
        if (_mgr is null || _found is null) return;
        var target = AppUpdater.VersionOf(_found);

        UpdateButton.IsEnabled       = false;
        CloseButton.IsEnabled        = false;
        DownloadProgress.Value       = 0;
        DownloadProgress.IsVisible   = true;
        StatusText.Text              = string.Format(ShellText.UpdateDownloadingVersion, target);
        try
        {
            // ⚠️ Progress arrives on a download thread, not this one.
            await _mgr.DownloadUpdatesAsync(_found, p => Dispatcher.UIThread.Post(() =>
            {
                DownloadProgress.Value = p;
                StatusText.Text        = string.Format(ShellText.UpdateDownloadingVersionPercent, target, p);
            }));
            StatusText.Text = string.Format(ShellText.UpdateInstallingVersion, target);
            _mgr.ApplyUpdatesAndRestart(_found, AppUpdater.RestartArgs());   // exits the process
        }
        catch (Exception ex)
        {
            _log.Log("Startup", $"update to {target} for a database ahead of this build", ex);
            StatusText.Text            = string.Format(ShellText.UpdateFailedGetRelease, ex.Message, target);
            DownloadProgress.IsVisible = false;
            UpdateButton.IsEnabled     = true;
            CloseButton.IsEnabled      = true;
        }
    }

    // The app's own page, which the user asked for by name: no confirmation (see ExternalLinks).
    private void OnReleases(object? sender, RoutedEventArgs e) => ExternalLinks.Launch(AppUpdater.ReleasesUrl);

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
