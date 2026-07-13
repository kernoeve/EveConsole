using System.Diagnostics;
using System.Linq;
using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EveCortex.ViewModels;

namespace EveCortex.Views;

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

    private void OnMoveDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.MoveDatabaseAsync();

    private void OnRenameDatabaseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.RenameDatabaseAsync();

    private void OnPointToDbClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.PointToExistingDatabaseAsync();

    private void OnBackupNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _ = _dbVm?.BackupNowAsync();

    public void WireDatabase(DatabaseSettingsViewModel dbVm, Window ownerWindow)
    {
        _dbVm = dbVm;
        dbVm.ShowSaveFileDialog = async (title, suggestedName) =>
        {
            var sp = StorageProvider;
            var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices   =
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
                Title         = title,
                AllowMultiple = false,
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
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            Environment.Exit(0);
        };
    }
}
