using EveConsole.Services;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SettingsWindow : Window
{
    private readonly CompositeDisposable _disposables = new();

    public SettingsWindow()
    {
        InitializeComponent();
    }

    // Select a tab by its header text (e.g. "Alerts").
    public void SelectTab(string header)
    {
        var tab = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header as string) == header);
        if (tab is not null) Tabs.SelectedItem = tab;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not SettingsViewModel vm) return;

        var scopeHandler = vm.CharacterVm.ScopeSelectionInteraction.RegisterHandler(async ctx =>
        {
            var dialog = new ScopeSelectionDialog(ctx.Input) { DataContext = vm.CharacterVm };
            var result = await dialog.ShowDialog<bool>(this);
            ctx.SetOutput(result);
        });

        var confirmHandler = vm.CharacterVm.ConfirmReplaceInteraction.RegisterHandler(async ctx =>
        {
            var dialog = new ConfirmDialog(ctx.Input) { Title = "Confirm Update" };
            var result = await dialog.ShowDialog<bool>(this);
            ctx.SetOutput(result);
        });

        _disposables.Add(scopeHandler);
        _disposables.Add(confirmHandler);

        _ = vm.AlertsVm.LoadAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _disposables.Dispose();
        base.OnClosed(e);
    }

    private DatabaseSettingsViewModel? _dbVm;

    private void OnRelocateDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.RelocateDatabaseAsync();

    private void OnPointToDbClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.PointToExistingDatabaseAsync();

    private void OnBackupNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.BackupNowAsync();

    /// <summary>On demand — the breakdown scans the database, so it is never run automatically.</summary>
    private void OnAnalyseDbSizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.AnalyseSizesAsync();

    private void OnShrinkDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.ShrinkDatabaseAsync();

    // One handler per retention section; each drives its own RetentionSectionVm.
    private DataRetentionSettingsViewModel? Retention => (DataContext as SettingsViewModel)?.RetentionVm;

    private void OnPurgeErrorLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.ErrorLog.PurgeNowAsync();
    private void OnPurgeKillmailsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.Killmails.PurgeNowAsync();
    private void OnPurgePriceHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.PriceHistory.PurgeNowAsync();
    private void OnPurgeGameLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.GameLog.PurgeNowAsync();
    private void OnPurgeChatClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.ChatMessages.PurgeNowAsync();

    public void WireDatabase(DatabaseSettingsViewModel dbVm, Window ownerWindow)
    {
        _dbVm = dbVm;
        // ⚠️ Both pickers start in the folder the database is in now. Without this the dialog
        // opens wherever the shell last left it — which is how a "rename" typed into the filename
        // box landed the database on a mapped network drive, taking a cross-volume copy the user
        // had every reason to expect to be instant.
        async Task<IStorageFolder?> CurrentDbFolder()
        {
            try
            {
                var dir = Path.GetDirectoryName(dbVm.DbPath);
                return string.IsNullOrWhiteSpace(dir) ? null
                     : await StorageProvider.TryGetFolderFromPathAsync(dir);
            }
            catch { return null; }   // a database on a path the shell cannot resolve is not fatal
        }

        dbVm.ShowSaveFileDialog = async (title, suggestedName) =>
        {
            var sp = StorageProvider;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title                  = title,
                SuggestedFileName      = suggestedName,
                SuggestedStartLocation = await CurrentDbFolder(),
                FileTypeChoices        =
                [
                    new FilePickerFileType("SQLite Database") { Patterns = ["*.db"] }
                ]
            });
            return file?.TryGetLocalPath();
        };

        dbVm.ShowOpenFileDialog = async title =>
        {
            var sp    = StorageProvider;
            var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title                  = title,
                AllowMultiple          = false,
                SuggestedStartLocation = await CurrentDbFolder(),
                FileTypeFilter =
                [
                    new FilePickerFileType("SQLite Database") { Patterns = ["*.db"] }
                ]
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };

        dbVm.ShowConfirmDialog = async (title, message) =>
        {
            var dlg = new ConfirmDialog(message) { Title = title };
            return await dlg.ShowDialog<bool>(this);
        };

        dbVm.RequestRestart = () =>
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (exe is not null)
            {
                // ⚠️ Hand the single-instance lock over BEFORE spawning the replacement. This
                // process is still alive for a moment after Process.Start, so without the release
                // the new instance sees the lock held, focuses this window and exits — and then
                // this one exits too, leaving nothing running. The argument makes the newcomer
                // wait for the handover rather than treat it as a rival.
                SingleInstance.Release();
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = true,
                    Arguments       = SingleInstance.RestartingArgument,
                });
            }
            Environment.Exit(0);
        };
    }
}
