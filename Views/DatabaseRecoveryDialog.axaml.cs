using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Services;

namespace EveConsole.Views;

/// <summary>What the user chose to do about an unopenable database.</summary>
public enum DatabaseRecoveryChoice
{
    /// <summary>Close the app and leave everything exactly as it is.</summary>
    Quit,
    /// <summary>A backup was expanded into place; carry on with it.</summary>
    Restored,
    /// <summary>The damaged file was set aside; carry on with an empty database.</summary>
    StartedFresh,
}

/// <summary>
/// Offered when the database will not open.
///
/// <para>⚠️ A dialog rather than a line on the splash. This is the one startup fault where the
/// remedy is usually sitting in the same folder as the problem, and the app that would offer it
/// is the app that will not start. Small red text behind a stalled splash tells the user their
/// data is gone; this tells them where it went and what can be done.</para>
/// </summary>
public partial class DatabaseRecoveryDialog : Window
{
    private readonly string             _dbPath;
    private readonly List<BackupOption> _backups;

    public DatabaseRecoveryChoice Choice { get; private set; } = DatabaseRecoveryChoice.Quit;

    public DatabaseRecoveryDialog(string dbPath, string error)
    {
        InitializeComponent();

        _dbPath  = dbPath;
        _backups = DatabaseIntegrityService.FindBackups(dbPath);

        ExplainText.Text =
            $"EVE Console could not read its database at {dbPath}. "
            + "Nothing has been changed or deleted yet.";

        ErrorText.Text = error;

        QuarantineText.Text =
            "Whichever you choose, the damaged file is renamed rather than deleted — it keeps its "
            + "folder, with \".damaged-\" and the date added to the name — so it can be inspected "
            + "or recovered later.";

        if (_backups.Count > 0)
        {
            BackupList.ItemsSource   = _backups.Select(b => b.Display).ToList();
            BackupList.SelectedIndex = 0;                 // newest
        }
        else
        {
            BackupPanel.IsVisible   = false;
            NoBackupText.IsVisible  = true;
            RestoreButton.IsEnabled = false;
        }
    }

    private void OnQuit(object? sender, RoutedEventArgs e)
    {
        Choice = DatabaseRecoveryChoice.Quit;
        Close();
    }

    private void OnFresh(object? sender, RoutedEventArgs e)
    {
        try
        {
            DatabaseIntegrityService.Quarantine(_dbPath);
            Choice = DatabaseRecoveryChoice.StartedFresh;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Could not move the damaged file aside: {ex.Message}";
        }
    }

    private void OnRestore(object? sender, RoutedEventArgs e)
    {
        var index = BackupList.SelectedIndex;
        if (index < 0 || index >= _backups.Count) return;

        try
        {
            // ⚠️ Aside first, then restore. The other order would overwrite the damaged file with
            // the backup and lose the evidence — and if the restore then failed, both copies.
            DatabaseIntegrityService.Quarantine(_dbPath);
            DatabaseIntegrityService.RestoreFrom(_backups[index], _dbPath);

            Choice = DatabaseRecoveryChoice.Restored;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Restore failed: {ex.Message}";
        }
    }
}
