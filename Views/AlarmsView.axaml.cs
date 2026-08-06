using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EveConsole.Services;
using EveConsole.ViewModels;

namespace EveConsole.Views;

public partial class AlarmsView : UserControl
{
    public AlarmsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AlarmsViewModel vm)
                vm.PickSoundFileCallback = PickSoundFileAsync;
        };
    }

    /// <summary>
    /// Browses for an audio file. Lives here because the picker needs a TopLevel, which the
    /// view model has no business knowing about.
    /// </summary>
    private async Task<string?> PickSoundFileAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return null;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = "Choose an alarm sound",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio")
                {
                    Patterns = [.. AlarmSoundService.SupportedExtensions.Select(e => "*" + e)],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
