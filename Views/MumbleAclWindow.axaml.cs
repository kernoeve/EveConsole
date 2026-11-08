using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.Services.Mumble;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>A channel's permissions and groups, for someone the server lets change them.</summary>
public partial class MumbleAclWindow : Window
{
    public MumbleAclWindow() => InitializeComponent();

    /// <summary>
    /// Opens one for this channel on this connection, owned by the window it was asked from: over a game, one owned by the
    /// main window would open behind the game.
    /// </summary>
    public static MumbleAclWindow ShowFor(MumbleClient client, uint channel, string channelName, Window host)
    {
        var vm = new MumbleAclEditorVm(client, channel, channelName);
        var window = new MumbleAclWindow { DataContext = vm };
        window.Closed += (_, _) => vm.Dispose();
        window.Show(host);
        _ = vm.AskAsync(MumbleText.AclLoading);
        return window;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // A pick fills the box beside it, then the list goes back to empty, ready to pick again.
    private void OnPickGroup(object? sender, SelectionChangedEventArgs e) => Picked(sender, (MumbleAclEntryVm line, string s) => line.Group = s);
    private void OnPickPerson(object? sender, SelectionChangedEventArgs e) => Picked(sender, (MumbleAclEntryVm line, string s) => line.UserName = s);
    private void OnPickMember(object? sender, SelectionChangedEventArgs e) => Picked(sender, (MumbleAclGroupVm group, string s) => group.NewMember = s);

    private static void Picked<T>(object? sender, Action<T, string> fill)
    {
        if (sender is not ComboBox { SelectedItem: string picked, DataContext: T into } box) return;
        fill(into, picked);
        box.SelectedItem = null;
    }
}
