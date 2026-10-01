using EveConsole.Services;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EveConsole.ViewModels;
using EveConsole.Localization;

namespace EveConsole.Views;

public partial class SettingsWindow : Window
{
    private readonly CompositeDisposable _disposables = new();

    public SettingsWindow()
    {
        InitializeComponent();

        // Every tab saves as it is changed, and text once typing pauses. Leaving a box saves it
        // now: a pause is no promise the next thing done is not closing the window, or reading
        // the setting somewhere else. Handled ones too — a box inside a control (a number picker)
        // may have its focus events handled by it.
        AddHandler(LostFocusEvent, OnFieldLostFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        // A pick is saved at once — but some picks land in a text value (a model chosen from a
        // service's list is the model's name, which can also be typed), and a text value waits for
        // the typing pause. A pick in any drop-down saves what is waiting.
        AddHandler(SelectingItemsControl.SelectionChangedEvent, OnPicked, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox && DataContext is SettingsViewModel vm)
            _ = vm.FlushPendingSavesAsync();
    }

    private void OnPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (e.Source is not ComboBox || DataContext is not SettingsViewModel vm) return;
        // Posted: the event can come before the binding has handed the pick to the view model.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = vm.FlushPendingSavesAsync(),
                                                    Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The saves still waiting when the window closed, for the owner to await before it
    /// reads what was changed.</summary>
    public Task PendingSaves { get; private set; } = Task.CompletedTask;

    // Select a tab by its header text — pass the same resource the header is built from
    // (SettingsText.TabAlerts), never the English words, or it finds nothing in any other language.
    public void SelectTab(string header)
    {
        var tab = Tabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header as string) == header);
        if (tab is not null) Tabs.SelectedItem = tab;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is not SettingsViewModel vm) return;

        // ⚠️ Read every time the window opens, not once at construction. The service can be started,
        // stopped or removed from services.msc while this window is closed, and a stale switch is
        // one that lies about a thing the user is about to act on.
        vm.PollingVm.RefreshServiceState();

        var scopeHandler = vm.CharacterVm.ScopeSelectionInteraction.RegisterHandler(async ctx =>
        {
            var dialog = new ScopeSelectionDialog(ctx.Input) { DataContext = vm.CharacterVm };
            var result = await dialog.ShowDialog<bool>(this);
            ctx.SetOutput(result);
        });

        var confirmHandler = vm.CharacterVm.ConfirmReplaceInteraction.RegisterHandler(async ctx =>
        {
            var dialog = new ConfirmDialog(ctx.Input) { Title = SettingsText.ConfirmUpdateTitle };
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
        // Before base.OnClosed, which completes ShowDialog: the owner finds it set.
        if (DataContext is SettingsViewModel vm) PendingSaves = vm.CloseAsync();
        base.OnClosed(e);
    }

    private DatabaseSettingsViewModel? _dbVm;

    // ── Windows service ───────────────────────────────────────────────────────
    //
    // The view model owns the work and the reporting; these only say which verb was asked for.

    private PollingSettingsViewModel? PollingVm => (DataContext as SettingsViewModel)?.PollingVm;

    private void OnServiceInstallClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = PollingVm?.InstallServiceAsync();

    private void OnServiceRemoveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = PollingVm?.UninstallServiceAsync();

    private void OnServiceRepointClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = PollingVm?.RepointServiceAsync();

    private void OnServiceStartClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = PollingVm?.SetServiceRunningAsync(true);

    private void OnServiceStopClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = PollingVm?.SetServiceRunningAsync(false);

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

    private void OnTestPostgresClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.TestPostgresAsync();

    private void OnSaveDbChoiceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.SaveDatabaseChoiceAsync();

    private void OnCopyToPostgresClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.CopyToPostgresAsync();

    private void OnCancelCopyClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.CancelCopyAsync();

    private void OnCheckPgDumpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.CheckPgDumpAsync();

    private void OnRestoreDumpClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.RestoreFromDumpAsync();

    // One handler per retention section; each drives its own RetentionSectionVm.
    private DataRetentionSettingsViewModel? Retention => (DataContext as SettingsViewModel)?.RetentionVm;

    private void OnPurgeErrorLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.ErrorLog.PurgeNowAsync();
    private void OnPurgeOurKillmailsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.OurKillmails.PurgeNowAsync();
    private void OnPurgeOtherKillmailsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.OtherKillmails.PurgeNowAsync();
    private void OnPurgePriceHistoryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.PriceHistory.PurgeNowAsync();
    private void OnPurgeGameLogClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.GameLog.PurgeNowAsync();
    private void OnPurgeChatClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.ChatMessages.PurgeNowAsync();
    private void OnPurgeAgentTelemetryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = Retention?.AgentTelemetry.PurgeNowAsync();

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
                    new FilePickerFileType(SettingsText.FileTypeSqliteDatabase) { Patterns = ["*.db"] }
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
                    new FilePickerFileType(SettingsText.FileTypeSqliteDatabase) { Patterns = ["*.db"] }
                ]
            });
            return files.Count > 0 ? files[0].TryGetLocalPath() : null;
        };

        dbVm.ShowConfirmDialog = async (title, message) =>
        {
            var dlg = new ConfirmDialog(message) { Title = title };
            return await dlg.ShowDialog<bool>(this);
        };

        dbVm.ShowTypedConfirmDialog = async (title, message, phrase) =>
        {
            var dlg = new ConfirmDialog(message, phrase) { Title = title };
            return await dlg.ShowDialog<bool>(this);
        };

        dbVm.RequestRestart = () =>
        {
            // ⚠️ Through AppLauncher rather than MainModule.FileName, which under an AppImage names
            // the binary inside a temporary mount: starting that directly skips the AppImage's own
            // runtime, and the replacement comes up without the environment its bundled libraries
            // are found through. It hands the single-instance lock over and keeps --profile; see
            // AppLauncher.Restart.
            //
            // It used to exit whether or not the replacement started, which left nothing running;
            // now a failed start stays up and says so. The work is recorded either way and runs at
            // the next start.
            if (AppLauncher.Restart() is { } error)
                dbVm.StatusText = string.Format(SettingsText.DbRestartFailed, error);
        };
    }
}
