using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>The Jabber tool.</summary>
public partial class JabberView : UserControl
{
    public JabberView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is JabberViewModel vm) vm.Confirm = AskAsync;
        };

        StatusBox.LostFocus += (_, _) => (DataContext as JabberViewModel)?.ApplyStatusMessage();
        StatusBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) (DataContext as JabberViewModel)?.ApplyStatusMessage(); };
    }

    private void OnMessagePerson(object? sender, RoutedEventArgs e) => (DataContext as JabberViewModel)?.MessageSelectedPerson();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) => (DataContext as JabberViewModel)?.MessageSelectedPerson();

    /// <summary>A Pidgin smiley theme: its zip, or the theme file of one already unpacked.</summary>
    private async void OnLoadPack(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not JabberViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = JabberText.EmoteLoadTitle,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(JabberText.EmoteFileType) { Patterns = ["*.zip", "theme"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        await vm.LoadPackAsync(path);
    }

    private void OnPackUp(object? sender, RoutedEventArgs e) => Pack(sender, (vm, pack) => vm.MovePackAsync(pack, -1));
    private void OnPackDown(object? sender, RoutedEventArgs e) => Pack(sender, (vm, pack) => vm.MovePackAsync(pack, 1));
    private void OnPackRemove(object? sender, RoutedEventArgs e) => Pack(sender, (vm, pack) => vm.RemovePackAsync(pack));

    private void Pack(object? sender, Func<JabberViewModel, JabberEmotePackVm, Task> act)
    {
        if (sender is Control { DataContext: JabberEmotePackVm pack } && DataContext is JabberViewModel vm) _ = act(vm, pack);
    }

    private async Task<bool> AskAsync(string message)
    {
        // ⚠️ No window means nobody to ask, so nothing that cannot be undone goes ahead.
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        return await new ConfirmDialog(message).ShowDialog<bool>(owner);
    }
}
