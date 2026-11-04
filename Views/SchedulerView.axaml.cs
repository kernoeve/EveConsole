using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class SchedulerView : UserControl
{
    public SchedulerView()
    {
        InitializeComponent();

        // The view model asks; the view is the only thing holding a window to hang a dialog off.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SchedulerViewModel vm)
            {
                vm.ConfirmDiscard  = AskAsync;
                vm.PickMarketGroup = async () =>
                    TopLevel.GetTopLevel(this) is Window owner
                        ? await new MarketGroupPickerWindow(new MarketGroupPickerViewModel(vm.Batch))
                              .ShowDialog<MarketGroupPickerResult?>(owner)
                        : null;
            }
        };
    }

    // ── Export / import: the file pickers need a TopLevel, which only the view has ──

    private async void OnExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SchedulerViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = AlarmsText.TitleExportTasks,
            SuggestedFileName = "scheduled-tasks.json",
            FileTypeChoices   = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await vm.ExportAsync(stream);
    }

    private async void OnImportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SchedulerViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = AlarmsText.TitleImportTasks,
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        await vm.ImportAsync(stream);
    }

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means no dialog, and refusing on that basis would trap the user in an
        // editor they cannot leave. Let it through instead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return true;

        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
