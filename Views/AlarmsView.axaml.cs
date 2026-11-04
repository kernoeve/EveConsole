using Avalonia.Controls;
using Avalonia.Platform.Storage;
using EveConsole.Services;
using EveConsole.ViewModels;
using EveConsole.Localization;

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
    /// Silences alarms on this machine, or lets them speak again.
    ///
    /// <para>No confirmation. Muting loses nothing — every firing is still recorded as an alert —
    /// and a prompt in front of somebody reaching for this during a fight would be its own kind of
    /// failure.</para>
    /// </summary>
    private void OnMuteClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is AlarmsViewModel vm) vm.ToggleMute();
    }

    /// <summary>Passes the whole selection on: the ListBox's SelectedItems cannot be bound as a list.</summary>
    private void OnAlarmSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox list && DataContext is AlarmsViewModel vm)
            vm.SetSelection(list.SelectedItems?.OfType<AlarmRowVm>() ?? []);
    }

    // ── Export / import: the file pickers need a TopLevel, which only the view has ──

    private async void OnExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AlarmsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = AlarmsText.TitleExportAlarms,
            SuggestedFileName = "alarms.json",
            FileTypeChoices   = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;

        await using var stream = await file.OpenWriteAsync();
        await vm.ExportAsync(stream);
    }

    private async void OnImportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not AlarmsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = AlarmsText.TitleImportAlarms,
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        await vm.ImportAsync(stream);
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
            Title         = AlarmsText.PickSoundTitle,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(AlarmsText.PickSoundFileType)
                {
                    Patterns = [.. AlarmSoundService.SupportedExtensions.Select(e => "*" + e)],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
