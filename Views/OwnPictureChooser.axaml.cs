using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EveConsole.Localization;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>"Your picture": the file picker is the window's, so it is asked for here.</summary>
public partial class OwnPictureChooser : UserControl
{
    public OwnPictureChooser() => InitializeComponent();

    private async void OnChooseFile(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OwnPictureVm vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = JabberText.PictureChoose,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0) return;

        var size = (await files[0].GetBasicPropertiesAsync()).Size ?? 0;
        if (size > (ulong)OwnPicture.MostFileBytes) { await vm.UseFileAsync([]); return; }

        await using var stream = await files[0].OpenReadAsync();
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        await vm.UseFileAsync(copy.ToArray());
    }
}
