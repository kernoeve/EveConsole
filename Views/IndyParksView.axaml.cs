using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class IndyParksView : UserControl
{
    public IndyParksView() => InitializeComponent();

    private async void OnExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not IndyParksViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title           = "Export Park",
            SuggestedFileName = $"{vm.SelectedPark?.Name ?? "park"}.json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await vm.ExportCurrentParkAsync(stream);
    }

    private async void OnImportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not IndyParksViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Import Park",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        await vm.ImportParkAsync(stream);
    }
}
