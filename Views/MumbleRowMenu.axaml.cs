using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using EveConsole.Localization;
using EveConsole.ViewModels;

namespace EveConsole.Views;

/// <summary>
/// What the channel tree offers on a right-click, for whichever row is selected.
///
/// <para>⚠️ Each dialog here takes the row before it opens and acts on that row: the toolbar and the always-on-top window
/// are separate windows, so the selection can move while a dialog waits, and a kick must not land on someone else.</para>
/// </summary>
public partial class MumbleRowMenu : ContextMenu
{
    /// <summary>⚠️ Without this a subclass has no look at all: styles are found by exact type.</summary>
    protected override Type StyleKeyOverride => typeof(ContextMenu);

    public MumbleRowMenu() => InitializeComponent();

    /// <summary>The window this was opened from, which its dialogs belong to.</summary>
    public Window? Host { get; set; }

    /// <summary>Puts the tree's popup away, for what opens a window of its own.</summary>
    public Action? HideTree { get; set; }

    /// <summary>Brings it back after a dialog: it is where the result shows, or the server's refusal.</summary>
    public Action? ShowTree { get; set; }

    private MumbleViewModel? Vm => DataContext as MumbleViewModel;

    private void OnDetails(object? sender, RoutedEventArgs e) => ShowDetails();

    private void OnServerInfo(object? sender, RoutedEventArgs e)
    {
        Vm?.SelectServerOfRow();
        ShowDetails();
    }

    private void ShowDetails()
    {
        HideTree?.Invoke();
        if (Vm is { } vm && Host is { } host) MumbleDetailWindow.ShowFor(vm, host);
    }

    private async void OnKick(object? sender, RoutedEventArgs e) => await RemovePersonAsync(ban: false);
    private async void OnBan(object? sender, RoutedEventArgs e) => await RemovePersonAsync(ban: true);

    private async Task RemovePersonAsync(bool ban)
    {
        if (Vm is not { SelectedRow: { IsUser: true } row } vm) return;

        var message = string.Format(CultureInfo.CurrentCulture, ban ? MumbleText.ConfirmBan : MumbleText.ConfirmKick, row.Text);
        var reason = await AskAsync(ban ? MumbleText.Ban : MumbleText.Kick, message, MumbleText.LabelReason);
        if (reason is not null) await vm.RemovePersonAsync(row, reason, ban);
    }

    private async void OnMove(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedRow: { IsUser: true } row } vm) return;

        var to = await AskAsync(MumbleText.Move, row.Text, MumbleText.LabelMoveTo, MumbleText.MoveHint, vm.ChannelPathsOf(row));
        if (to is { Length: > 0 }) await vm.MovePersonAsync(row, to);
    }

    private async void OnNewChannel(object? sender, RoutedEventArgs e) => await NewChannelAsync(MumbleText.NewChannelTitle, temporary: false);
    private async void OnNewTempChannel(object? sender, RoutedEventArgs e) => await NewChannelAsync(MumbleText.NewTempChannelTitle, temporary: true);

    private async Task NewChannelAsync(string title, bool temporary)
    {
        if (Vm is not { SelectedRow: { IsChannel: true } row } vm) return;

        var name = await AskAsync(title, "", string.Format(CultureInfo.CurrentCulture, MumbleText.LabelNewChannelUnder, row.Text));
        if (name is { Length: > 0 }) await vm.CreateChannelAsync(row, name, temporary);
    }

    private async void OnRemoveChannel(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedRow: { IsChannel: true } row } vm) return;
        if (await ConfirmAsync(string.Format(CultureInfo.CurrentCulture, MumbleText.ConfirmRemoveChannel, row.Text))) await vm.RemoveChannelAsync(row);
    }

    /// <summary>⚠️ The channel and connection are taken now: the editor keeps acting on them whatever is selected later.</summary>
    private void OnPermissions(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedRow: { IsChannel: true } row } vm || vm.ClientOfRow(row) is not { } client || Host is not { } host) return;

        HideTree?.Invoke();
        MumbleAclWindow.ShowFor(client, row.Id, row.Text, host);
    }

    private async void OnRegister(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { SelectedRow: { IsUser: true } row } vm) return;
        if (await ConfirmAsync(string.Format(CultureInfo.CurrentCulture, MumbleText.ConfirmRegister, row.Text))) await vm.RegisterAsync(row);
    }

    private async Task<string?> AskAsync(string title, string message, string label, string hint = "", IEnumerable<string>? choices = null)
    {
        if (Host is not { } host) return null;

        HideTree?.Invoke();
        var typed = await new AskDialog(title, message, label, hint, choices).ShowDialog<string?>(host);
        ShowTree?.Invoke();
        return typed;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        if (Host is not { } host) return false;

        HideTree?.Invoke();
        var yes = await new ConfirmDialog(message).ShowDialog<bool>(host);
        ShowTree?.Invoke();
        return yes;
    }
}
